# Optional physics

Games reference `Njulf.Physics/Njulf.Physics.csproj` explicitly. The framework and game template do not reference it. The module pins Jitter2 2.8.11 and uses Njulf vectors, quaternions, and matrices at its public boundary.

`PhysicsScene.Create()` defaults to Disabled and returns `null`: no tree, world, registration, subscription, or per-frame work. `QueryOnly` owns a standalone Jitter dynamic tree and exact shape queries; it never creates a `World`. `Simulation` uses its world's tree for the same queries. Choose the mode at creation and keep one physics scene per participating graphics scene, owned by the game.

```csharp
using Njulf.Physics;

// In Load; _physics is a game-owned PhysicsScene field.
_physics = new PhysicsScene(PhysicsMode.QueryOnly, Scene);
ColliderHandle handle = _physics.Register(
    ownerId: visual.Id,
    shapes: [ColliderShape.Box(new Vector3(1, 2, 1))],
    node: visual.Node,
    entity: visual,
    layer: 1, mask: uint.MaxValue);

// In Update, including while paused. Directions are normalized internally.
if (_physics.Raycast(Camera.Position, Camera.Forward, 20, out var hit,
        new QueryFilter(LayerMask: 1, IgnoreOwner: playerId)))
    InteractWith(hit.OwnerId, hit.Point);

// In Unload, before releasing the scene.
_physics.Dispose();
```

## Short registration helpers

```csharp
static class Layers
{
    public static readonly uint Player = CollisionLayers.Bit(1);
    public static readonly uint World = CollisionLayers.Bit(2);
}

var ground = physics.RegisterStatic(visual, ColliderShape.Box(size), layer: Layers.World);
var body = physics.RegisterDynamic(instance, ColliderShape.Capsule(.5f, 1),
    new BodySettings { Mass = 2 }, layer: Layers.Player);
var platform = physics.RegisterKinematic(platformVisual, ColliderShape.Box(platformSize));
```

`Register`, `RegisterStatic`, `RegisterDynamic`, and `RegisterKinematic` accept a `RenderObject`
or `ModelInstance`, with one shape or a shape list. Visuals supply `Id`, `Node`, and lifetime
ownership; instances use `PlacementRoot.Id`, `PlacementRoot`, and the whole instance for
lifetime ownership. The body helpers override `BodySettings.Kind`, retaining other settings.
Moving bodies still require Simulation mode. Supply the owning graphics scene to PhysicsScene
for automatic removal when a visual/instance leaves it.

Supplied shapes are in the bound node's space, or placement space for a model. These overloads
do not guess mesh geometry or apply `MeshToNode` automatically: use `WithLocalTransform` when
passing mesh-space geometry. Existing scale/rigid-body restrictions remain unchanged.

`CollisionLayers.Default` is bit 0, `All` is every bit, and `None` is zero. `Bit(index)` accepts
0 through 31; games can assign their own names to the returned uint masks. `QueryFilter.All`
explicitly includes every layer and `QueryFilter.None` includes none; null filters still mean All.

## Queries and filtering

`Raycast`, `SweepSphere`, and `SweepCapsule` return the closest exact hit, with collider identity, owner ID, world point, target's outward world normal, and distance in world units. Capsule length is the straight Y-axis segment between hemisphere centers; its total height is `length + 2 * radius`. Capsule sweeps also accept a pose for rotation. Ranges are finite, nonnegative, and inclusive. A null filter includes every layer; a filter with a zero layer mask includes none. Owner exclusion removes all registrations with that owner ID.

`OverlapSphere(center, radius, Span<OverlapHit>, filter)` prunes by tree bounds, tests the actual shapes, and emits each registration once, including compounds and meshes. `OverlapResult.Written` is the number stored, `Total` counts all matches, and `Overflowed` indicates truncation. Result order and equal-distance tie order are unspecified. Reuse caller-owned arrays or stack spans. Query scratch storage grows to the encountered workload, then is reused without steady-state managed allocation.

Finite positions, nonzero finite directions/rotations, positive dimensions/radii, and valid indices are required. Zero capsule segment length is allowed. Invalid inputs throw; misses return `false` and a default hit. Initial overlap returns distance zero and normal zero. A ray's point is its origin; an initially penetrating sweep's point is its query origin, because there is no unique impact point. Overlap tests include penetration; narrowphase tolerances are approximately `1e-4` world units. Triangle queries are two-sided and include edges; meshes remain surfaces, not solid-volume containment tests.

`SetEnabled` controls collision participation independently of render visibility. `SetFilter` changes collision layer/mask and invalidates existing contacts. Simulation accepts a pair only when **both** `(a.Layer & b.Mask)` and `(b.Layer & a.Mask)` are nonzero. Query layer masks select collider layers independently of simulation masks. Removed, foreign, and default handles are rejected. Disabled colliders retain their handle and can be re-enabled; disabling collision does not pause a dynamic body's integration.

## Geometry, transforms, and content preparation

Factories provide boxes (full size), spheres, Y-axis capsules, convex hulls from CPU points, and static triangle meshes from CPU positions and local triangle indices. Factories copy supplied geometry, and recipes can be reused across registrations. A registration accepts several offset shapes and creates at most one body. Use a single compound for a multi-material object's shared node or placement.

`shape.WithLocalTransform(meshToNode)` maps shape coordinates into the bound node's pivot space. Njulf uses row vectors: the complete render/collider transform is `MeshToNode * Node.WorldMatrix`. Bind `visual.Node` and use `visual.MeshToNode` for extracted render geometry. For a whole model placement, map each mesh into placement space first, then bind `instance.PlacementRoot` and pass `entity: instance`.

Bound nodes support positive **uniform world scale**, baked at registration. Local shape transforms support positive TRS scale (including nonuniform scale), rotation, and offset. Shear, reflections, singular transforms, changing a registered node's scale, and deforming/skinned collision meshes are unsupported; use authored rigid collision geometry or recreate the registration. Parent transforms are included. Unbound registrations use an explicit `PhysicsPose` and `SetPose` for static movement. Jitter's center of mass is computed separately from the visual pivot for moving compounds; publication preserves the node pivot and parent-relative transform. No public math types leak from Jitter.

Collision extraction is opt-in during CPU content preparation:

```csharp
// Raw path: Content.Load<ProcessedMeshAsset>(...) or ProcessedMeshAssetBuilder.Build(...).
CollisionGeometry data = CollisionGeometry.Extract(processed.SubMeshes[selectedIndex]);

// Cooked path: after CookedPackage.LoadMesh(...) decodes the package, before upload.
CollisionGeometry cookedData = CollisionGeometry.Extract(cookedMeshPayload, selectedIndex);

ColliderShape collision = ColliderShape.TriangleMesh(data.Positions, data.Indices)
    .WithLocalTransform(visual.MeshToNode);
```

`Njulf.Assets` returns neutral arrays and node identity; it has no Jitter reference. Only selected submeshes are copied. These extraction calls do not change content cache identity, cooked formats, or ordinary loading. Release the processed/cooked source data after preparation. Prefer simplified, separately authored collision meshes for detailed environments. Triangle meshes are rejected for dynamic and kinematic bodies.

## Fixed-step simulation

The XYZ axes are shared with Jitter; positions, linear velocity, forces, gravity, and hit vectors need no axis swap. Njulf applies quaternion matrices to row vectors, so the adapter conjugates rotations and transposes local shape matrices for Jitter. Public angular velocity is in radians/second with signs matching `new Quaternion(axis, positiveAngle)` in Njulf; its components are negated when crossing the Jitter boundary. Kinematic angular velocity is derived directly from the converted target rotations.

```csharp
// During setup:
IsFixedTimeStep = true;
TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
_physics = new PhysicsScene(PhysicsMode.Simulation, Scene);
var body = _physics.Register(ownerId, [ColliderShape.Box(Vector3.One)],
    node: visual.Node, entity: visual,
    body: new BodySettings { Kind = BodyKind.Dynamic, Mass = 2,
        Friction = .6f, Restitution = .1f, AffectedByGravity = true });

protected override void FixedUpdate(GameTime time)
{
    base.FixedUpdate(time); // Gameplay/scene work first.
    _physics.SetKinematicTarget(platform, targetPose);
    _physics.Step((float)time.ElapsedGameTime.TotalSeconds);
}
```

The order is gameplay/scene updates, kinematic targets, dirty transform synchronization, one single-threaded Jitter step, dynamic node pose publication, then contact notifications. `Step` has no accumulator and does not interpret wall time, pause, or time scale. `Game` supplies fixed callbacks using its existing `IsPaused`, `TimeScale`, `PauseWhenInactive`, and catch-up limits. Do not also step from `Update` or `Scene.Update`.

Static and kinematic poses are game-owned. Registered node changes are synchronized before queries, even while paused. Kinematic targets are immediately queryable, and the next step derives linear/angular velocity from the last completed pose so platforms transfer motion through contacts. Without another target, the platform stops. `Teleport` is separate, clears velocity by default, and resets the kinematic reference pose.

Dynamic poses are physics-owned. Use `SetVelocity` (linear and angular), `AddForce`, `ApplyImpulse`, or `Teleport`. Editing a bound dynamic node directly is rejected at synchronization. `GetPose`, `GetVelocity`, and `GetAngularVelocity` expose completed dynamic state. `Gravity` controls the scene's simulation gravity. Authoritative nodes receive the latest pose; separate visual nodes can opt into interpolation below. Bound moving visuals present at registration are marked `IsStatic = false`.

All physics calls run on the creating game thread outside a step. `Contact` emits buffered begin/end events at registration-pair granularity after poses are published. Handlers can query, remove, register, or teleport bodies; recursively stepping is rejected. Events describe completed transitions, so another handler may already have removed a handle. Removal/disable-induced end events are delivered after the next step; `Clear` discards pending events. Dispose is idempotent.

## Contact data and triggers

Begin `PhysicsContact` events provide nullable `Details`: a world-space contact midpoint,
unit normal **from event A toward B**, and nonnegative `ClosingSpeed` in world units/second.
Closing speed is measured before solver response at the contact points, including angular
motion. A compound pair reports the contact with greatest closing speed that step. Use this
value to choose damage or impact sound intensity; it is not an impulse or an energy measurement.
End events have no details. Begin/end notifications do not provide continuous scraping sounds.

```csharp
physics.Contact += contact =>
{
    if (contact.Details is { ClosingSpeed: > 2 } impact)
        PlayImpact(impact.Position, impact.ClosingSpeed);
};

var checkpoint = physics.Register(checkpointId, [ColliderShape.Box(new(3, 2, 3))],
    new PhysicsPose(checkpointCenter), new BodySettings { IsTrigger = true });
physics.Trigger += transition =>
{
    if (transition.Entered && (transition.A == checkpoint || transition.B == checkpoint))
        ActivateCheckpoint();
};
```

`BodySettings.IsTrigger` also works with the visual/body registration helpers. Triggers
participate in queries but never generate solver response or `Contact` events. To query only
solid geometry, use `QueryFilter.All with { IncludeTriggers = false }`. Ordinary layer masks
and owner exclusions still apply. Simulation tests exact shape overlaps after every completed
step, including static/kinematic pairs, and emits one `Trigger` enter/exit per registration
pair, even for compounds. Both colliders' layer/mask rules must accept the pair.
QueryOnly can query triggers but does not track or emit transitions. Detection is discrete:
crossing a thin volume completely between steps can miss it. Mesh triggers are surfaces, not
solid containment volumes; use boxes, capsules, spheres, or convex hulls for zones.

Contact and trigger payloads contain copied values, owner IDs, and handles; they contain no
Jitter pointers or borrowed entity references and may be retained indefinitely. A handle is an
identity, not an ownership reference: use `physics.IsValid(handle)` before accessing a body.
It returns false for default, foreign, removed handles and disposed scenes. Event callbacks
may invalidate handles in an already buffered event. Removal, disabling, and filter changes
queue exits for the next step; pausing delays delivery. Teleport-induced overlap changes are
detected at the next step. Clear/dispose discard pending and remaining buffered notifications.

## Presentation interpolation

`GetInterpolatedPose(handle, alpha)` returns a value between the previous and current
**completed** simulation poses. Alpha must be finite and in `[0, 1]`. Position uses linear
interpolation and rotation uses shortest-path slerp. Pending kinematic targets remain immediately
queryable but do not enter interpolation history until a step completes. Catch-up steps retain
only the last two completed poses; interpolation adds the usual one-fixed-step presentation lag.

```csharp
// The physics node and visual node must be separate.
var body = physics.Register(visual.Id, [ColliderShape.Box(Vector3.One)],
    body: new BodySettings { Kind = BodyKind.Dynamic },
    node: new SceneNode { Position = spawn }, entity: visual);
physics.BindPresentation(body, visual.Node);
RegisterModule(new PhysicsHostModule(physics));
```

`BindPresentation` preserves the visual node's world scale and marks scene visuals in its
subtree as moving. Use a model's `PlacementRoot` for its visual hierarchy. A presentation node
cannot contain any registered physics node or overlap another presentation binding's hierarchy;
unsafe reparenting is rejected before presentation writes. Binding null unbinds the visual;
removing the collider releases its binding. Static colliders do not need interpolation.

`PhysicsHostModule` uses `GameModuleFrame.InterpolationAlpha`, supplied by the existing game
clock, after all fixed steps and before rendering. With manual ownership, call
`physics.UpdatePresentation(InterpolationAlpha, IsSimulationPaused)` once after the fixed-step
batch (for example, at the start of `Draw`). It never changes authoritative nodes, body poses,
or query results. Registration and teleport reset both history poses; teleport snaps the bound
visual immediately. Effective pause displays the latest completed pose and collapses history,
so resume cannot interpolate backward. Paused kinematic target changes still affect queries,
but presentation waits for a completed step; use teleport for an immediate snap.

## Character controller

`CharacterController` is an upright Y-axis kinematic capsule in a Simulation scene. Its position
is the capsule center. It owns one collider; dispose the controller when removing the character.
Scene clear/disposal invalidates it, and later controller disposal is safe. It does not own its
visual or the physics scene.

```csharp
// Setup; visual geometry should match the configured capsule dimensions.
character = new CharacterController(physics, playerId, spawn,
    new CharacterControllerSettings { Radius = .35f, Length = 1.1f,
        MaxSlopeAngle = 45, StepHeight = .3f, JumpSpeed = 5 });
physics.BindPresentation(character.Collider, visual.Node);

// FixedUpdate, before the registered PhysicsHostModule steps:
physics.SetKinematicTarget(platform, platformTarget);
character.Move(desiredWorldVelocity, jumpAction.ConsumePressed(),
    (float)time.ElapsedGameTime.TotalSeconds);
```

`Move` consumes world X/Z velocity (Y is ignored) and a jump request once per fixed update.
The settings additionally expose skin width, ground snap distance, downward gravity magnitude,
and collision layer/mask. Defaults are a .35 radius, 1.1 straight segment, .02 skin, .1 ground
snap, 45-degree slope limit, .3 step height, 9.81 gravity, and 5 jump speed. Layer/mask filtering
is mutual; triggers never block movement. The public `Pose`, `Velocity`, `IsGrounded`,
`GroundNormal`, and `SupportingCollider` report controller state; its pending pose is queryable
before the physics step like other kinematic targets.

Movement uses bounded capsule sweep-and-slide and penetration recovery. Walkable slopes permit
ascent; steeper surfaces reject horizontal ascent and allow gravity to slide downward. Steps
require upward and forward clearance and a walkable landing. A short ray at a capsule contact
distinguishes a flat tread edge from a steep surface. Jumps require grounding, ceiling contacts
cancel upward motion, and downward probes snap to nearby ground. If six recovery corrections
cannot resolve an overlap, `RecoveryFailed` is true, movement stops at its starting pose, and
velocity is cleared. Use `character.Teleport(position)` to reset movement and presentation.

Grounded characters follow the supporting contact point's translation and rotation through
collision-constrained movement while remaining upright. Walking off or jumping inherits the
support point's velocity. Removed, disabled, or teleported supports detach without launching
the character. Set platform targets **before** controller movement, then step physics; do not
also move the capsule from Update. Pause/time scale use the existing fixed callback behavior.
This controller is not force-driven; crouching, climbing, custom pushing, and networking are
outside this API. Dynamic objects may still react to its kinematic collider.

## Ownership and scene replacement

For automatic integration, register `new PhysicsHostModule(physics)` once with
`Game.RegisterModule`, or `GameLevel.RegisterModule` for a local world. Registration owns
disposal; remove manual `Step` and `Dispose` calls. Simulation requires fixed mode and steps
after gameplay `FixedUpdate`, delivering contacts before spatial audio. Query-only physics
works in either mode and synchronizes during pause. See
[managed levels](FrameworkApi.md#managed-levels) for coordinated replacement/cancellation.

The following manual ownership pattern remains available:

Pass `entity` to tie registration lifetime to that scene member. Removing/detaching the member removes its collider; for a compound model use the `ModelInstance`, not one material subobject. Node-only bindings live until explicit removal, scene clear, or physics disposal. `Scene.Clear`/`Dispose` clear registrations even when the graphics scene has no render objects. Physics disposal unsubscribes and releases native world state.

Switch the pair explicitly on the game thread. The framework remains independent of the physics module:

```csharp
void SwitchScene(Scene next, PhysicsScene? nextPhysics)
{
    if (nextPhysics != null && !ReferenceEquals(nextPhysics.Scene, next))
        throw new ArgumentException("Physics must belong to the next scene.");
    Scene previous = ExchangeScene(next);
    PhysicsScene? previousPhysics = _physics;
    _physics = nextPhysics;
    previousPhysics?.Dispose();
    previous.Dispose();
}
```

If the previous scene will be reused, retain both objects together and dispose both later. Never run an old physics scene from the new scene's fixed callback.

## Examples and verification

```powershell
dotnet run --project Njulf.ApiExamples -c Development -- --example physics-query
dotnet run --project Njulf.ApiExamples -c Development -- --example physics-simulation
```

QueryOnly demonstrates an interaction ray. Simulation demonstrates stacked falling boxes and a moving platform through the same query API. The platform sweeps from x=2 to x=-2, strikes the stack's base, and topples it; the floor leaves room for the fallen boxes. Box faces have separate vertices and flat normals. Space inspects the hit and pushes a dynamic body; P pauses, T toggles half speed, and B enables optional ray/hit and collider **bounds** drawing. Debug drawing starts disabled. `--frames 180 --validation` provides a finite smoke run that checks hits, stepping, floor penetration, and renderer validation. Simulation runs lasting at least eight simulated seconds also check that the stack toppled; use `--frames 720 --validation` for this longer smoke.

Simulation also interpolates its moving visuals and includes a purple capsule character,
two stairs, and a green checkpoint pad with a non-blocking volume. Arrow keys move the
character, Enter jumps, and R resets it. Finite runs automatically walk the course and jump;
runs with at least five simulated seconds verify reaching the checkpoint. Contact logs show
position, normal, and closing speed. A `--frames 540 --validation` run exercises the course.

```powershell
dotnet test Njulf.Tests -c Development --filter 'FullyQualifiedName~PhysicsTests|FullyQualifiedName~PhysicsGeometryTests|FullyQualifiedName~PhysicsConvenienceTests|FullyQualifiedName~CharacterControllerTests|FullyQualifiedName~GameTimingTests|FullyQualifiedName~GameLevelTests'
$env:DOTNET_TieredCompilation='0'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --physics-workload QueryOnly moving docs/performance/milestones/physics-query-moving.json
```

Run the workload in separate processes for Disabled, QueryOnly, and Simulation, each with `static` and `moving`. It measures 256 boxes, 32 rays + 32 sphere sweeps + 32 overlaps per frame, 2,000 warmup frames, and 4,000 measured frames. The moving case updates 32 nodes; Simulation treats those as kinematic. This isolates registration/query/tree and base stepping cost, not contact-heavy solver throughput. JSON records cold-process setup (including first-use JIT), median/p95/p99, allocations, process private/working-set memory, and Jitter's allocated unmanaged bytes. See the dated milestone for measured results and limitations.
