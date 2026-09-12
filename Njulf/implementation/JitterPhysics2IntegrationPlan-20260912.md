**Jitter Physics 2 integration plan**

Date: 2026-09-12. Status: implemented and validated. See [setup and API contracts](../docs/Physics.md) and the [implementation/validation milestone](../docs/performance/milestones/20260912-jitter-physics-integration.md). The original scope and deferred work below are retained for reference.

Add accurate gameplay queries and optional rigid-body simulation through one small `Njulf.Physics` module. Games explicitly register collision geometry and choose a mode when creating their physics scene.

| Mode | Runtime work |
| --- | --- |
| Disabled (default) | No physics scene, colliders, subscriptions, or simulation work. |
| QueryOnly | Collider storage, spatial-tree maintenance, and requested ray/shape queries. |
| Simulation | The same query API plus Jitter rigid bodies and fixed-step simulation. |

QueryOnly still has memory and query costs. Its requirement is to avoid constructing a Jitter `World` or running the dynamics solver. Select the mode at creation; changing modes can recreate the physics scene in the first version.

1. **Add the optional module and prove the query path.**

   Create `Njulf.Physics`, targeting the existing `net10.0`, referencing `Njulf.Core`, `Njulf.Graphics` for scene contracts, and a pinned `Jitter2` package. Start with **2.8.11**, the current published release checked for this plan; update affected dependency locks deliberately. Games reference the module explicitly, keeping Jitter out of the default framework dependency graph. [Package](https://www.nuget.org/packages/Jitter2/2.8.11)

   First, verify translated and rotated collider raycasts using a standalone `DynamicTree` and a small collider proxy implementing Jitter's query interfaces. Reuse Jitter's shape routines for exact intersection. Prove that moving/removing a proxy updates query results without a simulation step. This establishes the QueryOnly implementation before adding bodies. [Tree API](https://jitterphysics.com/api/Jitter2.Collision.DynamicTree.html), [Shape-query API](https://jitterphysics.com/api/Jitter2.Collision.NarrowPhase.html)

2. **Expose a small collider and query API.**

   Use one `PhysicsScene` facade with collider registration/removal, explicit pose updates, and optional scene-node binding. Collider records carry an owner ID, enabled state, collision layer/mask, and local shape offset. Rendering visibility is independent of collision participation. Return collider/owner identity, world-space point, normal, and distance in `RaycastHit`.

   Implement closest-hit `Raycast`, `OverlapSphere`, and sphere/capsule sweeps. Support maximum distance, layer filtering, and ignoring the caller's owner ID. Use Njulf math at the public boundary, normalize ray directions internally, and define invalid-input, initial-overlap, and result-buffer-overflow behavior. Overlaps must test actual shapes after tree pruning. Use caller-owned result buffers and reusable scratch storage for repeated queries. [Query behavior](https://jitterphysics.com/docs/documentation/dynamictree.html)

3. **Support practical collision geometry.**

   Start with boxes, spheres, capsules, convex hulls, and static triangle meshes. Support several offset shapes on one body for compound objects. Moving bodies use primitives or convex geometry; triangle meshes are static in this integration. Jitter provides these shape building blocks. [Shapes](https://jitterphysics.com/docs/documentation/shapes.html)

   Mesh creation accepts CPU positions/indices. Add a small opt-in extraction path around the existing `ProcessedSubMeshAsset` data during content preparation, covering the supported raw/cooked loading paths. Only requested collision geometry is retained; `Njulf.Assets` returns neutral geometry data and does not reference Jitter. Prefer separate, simplified collision meshes for detailed levels.

   Verify Njulf/Jitter vector and quaternion conversion, triangle winding, `MeshToNode`, parent transforms, and collider offsets. Bake supported scale at creation; reject unsupported shear or scale changes explicitly. Keep mesh pivot and body center-of-mass offsets separate. A multi-material model should share a body through its bound node/placement.

4. **Add rigid-body simulation using existing timing.**

   Simulation mode creates a Jitter `World` and uses its `DynamicTree` for queries, avoiding a duplicate query tree. Colliders default to static geometry unless attached to a moving body. Expose static, kinematic, and dynamic bodies, mass, friction, restitution, gravity, velocities, forces, impulses, and teleporting. Keep advanced solver settings at Jitter defaults initially. [Bodies](https://jitterphysics.com/docs/documentation/bodies.html)

   Reuse `Game.IsFixedTimeStep`, `TargetElapsedTime` (initially 60 Hz), pause/time-scale handling, and catch-up limits. The game explicitly calls `PhysicsScene.Step` once from `FixedUpdate`; no second accumulator or automatic step through `Scene.Update`. The documented order is gameplay/scene updates, kinematic targets and dirty collider synchronization, Jitter step, dynamic pose publication, then collision notifications. Kinematic target movement must provide velocity so moving platforms affect dynamic bodies correctly; teleport remains a separate operation.

   Public mutation/query calls run on the game thread outside a step. Begin with single-threaded stepping. Deliver buffered begin/end contact notifications after stepping so handlers can safely change gameplay state. Collision-layer masks govern simulation pairs as well as query filtering.

5. **Bind transforms and lifecycle explicitly.**

   Keep one physics scene per participating Njulf scene, owned by the game. Subscribe only for registered bindings, using existing `SceneNode`/scene mutation events to track changes. QueryOnly applies dirty transforms before queries, including while simulation is paused. Static and kinematic poses are game-owned; dynamic poses are physics-owned and change through body controls or explicit teleporting. Physics writes to bound nodes must not feed back into Jitter.

   Publish the latest completed fixed pose for the first version. Preserve scene-node offsets and mark simulated visuals as moving geometry. Remove colliders when bound entities are removed; clear registrations on `Scene.Clear`; unsubscribe and release Jitter state on disposal. Switch the active physics scene alongside `Game.ExchangeScene`, retaining or disposing the previous pair together. Repeated cleanup must be safe, and removed handles must be rejected.

6. **Demonstrate and validate the complete path.**

   Add two small `Njulf.ApiExamples` scenarios: a player-facing interaction ray with QueryOnly, and falling/stacked bodies plus a moving kinematic platform with Simulation. Document setup, frame order, filtering, mesh creation, and cleanup. Include ray/hit and collider visualization using existing debug drawing where available, enabled only for debugging.

   Add CPU tests in `Njulf.Tests` for nearest/range/filter/self-exclusion behavior; rotated, moved, disabled, and removed colliders; rays through mesh gaps; overlap/sweep initial penetration; equivalent queries across both modes; falling/resting bodies and impulses; kinematic contact response; pause/time scaling; scene replacement; and disposal. Validate results numerically with tolerances. Run the focused physics tests and existing timing/lifecycle tests, then smoke-test both examples.

   Measure a repeatable workload in Disabled, QueryOnly, and Simulation modes, with a static scene and a moving-collider variant. Record startup cost, median/p95/p99 CPU time, managed allocations, and process memory including Jitter's unmanaged storage. Check that QueryOnly never constructs/steps a `World`, unchanged colliders avoid redundant synchronization, and repeated queries avoid steady-state allocation. Confirm the default template has no Jitter dependency or added frame work.

   Save the compact comparison and reproduction commands under `docs/performance/milestones/`. Keep generated payloads on D: within this workspace and follow `AGENTS.md` retention/pruning rules.

Completion means both examples work through the same query API, QueryOnly operates without a dynamics world, simulation follows the existing timing controls, and scene cleanup leaves no stale hits or retained physics state.

Defer a general backend abstraction, ECS changes, automatic collider generation for every render object, collider editor/scene-file authoring, trigger event tracking, a full character controller, visual interpolation using the existing `InterpolationAlpha`, joints/ragdolls/vehicles/soft bodies, and networking determinism. Add these when a game needs them; the first delivery is the six steps above.
