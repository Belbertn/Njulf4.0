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

Dynamic poses are physics-owned. Use `SetVelocity` (linear and angular), `AddForce`, `ApplyImpulse`, or `Teleport`. Editing a bound dynamic node directly is rejected at synchronization. `GetPose`, `GetVelocity`, and `GetAngularVelocity` expose completed state. `Gravity` controls the scene's simulation gravity. The first version publishes the latest completed pose without interpolation. Bound moving visuals present at registration are marked `IsStatic = false`.

All physics calls run on the creating game thread outside a step. `Contact` emits buffered begin/end events at registration-pair granularity after poses are published. Handlers can query, remove, register, or teleport bodies; recursively stepping is rejected. Events describe completed transitions, so another handler may already have removed a handle. Removal/disable-induced end events are delivered after the next step; `Clear` discards pending events. Dispose is idempotent.

## Ownership and scene replacement

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

```powershell
dotnet test Njulf.Tests --filter 'FullyQualifiedName~PhysicsTests|FullyQualifiedName~PhysicsGeometryTests|FullyQualifiedName~GameTimingTests|FullyQualifiedName~GameTimingLifecycleTests|FullyQualifiedName~GameLifecycleIntegrationTests|FullyQualifiedName~SceneTests'
$env:DOTNET_TieredCompilation='0'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --physics-workload QueryOnly moving docs/performance/milestones/physics-query-moving.json
```

Run the workload in separate processes for Disabled, QueryOnly, and Simulation, each with `static` and `moving`. It measures 256 boxes, 32 rays + 32 sphere sweeps + 32 overlaps per frame, 2,000 warmup frames, and 4,000 measured frames. The moving case updates 32 nodes; Simulation treats those as kinematic. This isolates registration/query/tree and base stepping cost, not contact-heavy solver throughput. JSON records cold-process setup (including first-use JIT), median/p95/p99, allocations, process private/working-set memory, and Jitter's allocated unmanaged bytes. See the dated milestone for measured results and limitations.
