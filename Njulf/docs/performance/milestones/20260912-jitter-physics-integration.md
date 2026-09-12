# Jitter Physics 2 integration — 2026-09-12

Decision: keep the optional integration. QueryOnly has no dynamics world, both modes pass the same query contracts, simulation follows the game's fixed clock, and cleanup rejects stale handles. No backend abstraction or automatic render-object collider generation was added. Setup and limits: [Physics guide](../../Physics.md).

Source: `b306b2dddf3eff8b1ed505fedf278d9698d6d6a3` plus the uncommitted physics module, scene lifecycle notifications, opt-in asset extraction, examples, tests, documentation, solution/project references, and dependency locks. The pre-existing asset package edits and lock changes were preserved. Jitter2 is exactly **2.8.11**, upstream package revision `a3127eeb6114c77fa30775feea2fd235938f6eb5`. The only project locks containing Jitter are Physics, Tests, and ApiExamples; Framework and the default game/template dependency graph remain unchanged by physics.

## Workload and environment

AMD Ryzen 5 5600H, 12 logical processors, Windows build 26200, .NET 10.0.7. Development configuration (optimized), `DOTNET_TieredCompilation=0`. Each mode/variant runs in a fresh process, without graphics or validation. Setup includes first-use JIT, node creation, and explicit registration; filesystem/OS caches are warm, so this is not disk-cold startup. Rendered smoke checks used the NVIDIA GeForce RTX 3060 Laptop GPU with Vulkan validation enabled.

256 unit boxes on a 16×16 grid. Each active-mode frame performs 32 rays, 32 sphere sweeps, and 32 sphere overlaps. Moving variant changes 32 node translations; those registrations are kinematic in Simulation. Static variant has only static bodies. Simulation steps at 1/60 second. Disabled still performs the same game-owned node updates but creates no physics scene or collider geometry. 2,000 warmup frames, then 4,000 samples. This measures queries, tree maintenance, and base stepping, not a contact-heavy dynamics workload.

## Final measurements

| Mode | Scene | Setup ms | Median µs | p95 µs | p99 µs | Private MiB during | Private MiB after disposal/GC |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Disabled | static | 3.61 | 0.0 | 0.1 | 0.1 | 9.33 | 8.78 |
| QueryOnly | static | 36.39 | 40.5 | 46.9 | 69.8 | 10.46 | 10.50 |
| Simulation | static | 55.59 | 60.3 | 95.4 | 123.9 | 13.08 | 11.40 |
| Disabled | moving | 4.02 | 0.6 | 0.7 | 0.7 | 9.29 | 8.73 |
| QueryOnly | moving | 37.80 | 61.2 | 69.2 | 95.9 | 10.36 | 10.40 |
| Simulation | moving | 61.70 | 107.5 | 119.0 | 198.2 | 13.01 | 11.38 |

Every measured case allocated **zero managed bytes** in the measured loop. QueryOnly allocated no Jitter unmanaged world storage and completed zero steps. Simulation allocated 1,728 KiB of Jitter unmanaged storage and completed 6,000 steps. Static registrations required zero transform synchronizations. Moving cases synchronized 191,999 changes (one initial assignment was identical), with identical active-mode hit checksums of 576,000. Disposal released approximately the native-world allocation in process private memory; managed heaps, JIT code, and runtime caches need not immediately return to the OS.

QueryOnly's observed median was 19.8 µs (32.8%) below Simulation for static geometry and 46.3 µs (43.1%) below it for moving geometry. These are local workload costs, not a release-grade performance claim. A zero Disabled/static median is below timer resolution, not a literal zero-cost stopwatch. Earlier development measurements had QueryOnly medians 40.0/60.7 µs and Simulation 56.0/142.5 µs (static/moving). Avoiding redundant identical body pose writes reduced moving-body maintenance, but runs were not randomized and tails varied substantially; no isolated optimization speedup is claimed.

Compact machine-readable reports include startup allocations, working set, pre/post-disposal private memory, synchronization counts, and query checksums:

- [Disabled static](20260912-jitter-Disabled-static.json), [Disabled moving](20260912-jitter-Disabled-moving.json)
- [QueryOnly static](20260912-jitter-QueryOnly-static.json), [QueryOnly moving](20260912-jitter-QueryOnly-moving.json)
- [Simulation static](20260912-jitter-Simulation-static.json), [Simulation moving](20260912-jitter-Simulation-moving.json)

## Correctness and rejected intermediate results

- **24/24 physics tests pass**, numerically checking exact rays/sweeps/overlaps in both modes, inclusive ranges, normals, layer/self filtering, moved/disabled/removed geometry, mesh gaps/edges/winding, transformed convex/mesh/capsule geometry, copied raw/cooked extraction, parent scale/rotation/offsets, independent dynamic child publication, COM/pivot preservation, forces/gravity/impulses, resting bodies, kinematic contact transfer, notifications allowing removal, paused/time-scaled fixed stepping, Game.ExchangeScene pairing, entity/model cleanup, disposal, and allocation-free repeated queries.
- **62 existing selected tests passed** in the timing/scene/lifecycle run, including the native Vulkan timing and game lifecycle fixtures. The original broad filter also selected some existing scene fixtures beyond `SceneTests`; no full repository suite was run. Existing unrelated test-project nullable warnings remain.
- Development ApiExamples build: **zero warnings/errors**. Physics module also builds independently.
- QueryOnly rendered smoke: **180 full-quality frames**, 699 updates with a ray hit, zero simulation steps, **zero Vulkan validation errors**. Screenshot inspected: intended blocks and floor visible, interaction ray targets the center block.
- Simulation rendered smoke: **240 full-quality frames**, 561 steps, zero Vulkan validation errors; inspected screenshot shows the resting stack and box on the platform. After the force-timing fix, a second smoke passed **180 full-quality frames**, 526 steps, 705 hit updates, zero Vulkan validation errors. Both runs checked for floor penetration before disposing physics.
- Rejected initial API behavior: Jitter's strict ray endpoint excluded zero/max-distance hits; its triangle fast path culled backfaces and excluded edges; sweep normals pointed toward the target; explicit shape removal omitted end-contact callbacks. The adapter now defines and tests consistent public behavior using Jitter's exact shape routines.
- Rejected force behavior: 2.8.11 prepares force/gravity integration deltas at the end of a step. The adapter refreshes the per-substep linear delta before stepping, so force, gravity, and timestep changes apply on the requested step without being applied again on the next one. This small pinned-backend compatibility adjustment has direct numerical coverage.
- Rejected first QueryOnly smoke: example `Camera.LookAt` ran before the projection matrix was initialized, causing a singular inverse. The example now sets the first-person camera orientation directly; framework camera behavior was not changed.

Limits: single-threaded fixed stepping, latest completed poses without interpolation, static triangle meshes, positive uniform bound-node world scale, and no shear/reflection/deforming collision geometry. Scene-pair switching and explicit collision registration remain game responsibilities. There is no cross-platform or heavy-contact solver performance qualification.

## Reproduction

From the repository root, with an available Vulkan device for the native lifecycle tests/smokes:

```powershell
dotnet restore Njulf.Tests/Njulf.Tests.csproj --force-evaluate -p:RestoreLockedMode=false
dotnet restore Njulf.ApiExamples/Njulf.ApiExamples.csproj --force-evaluate -p:RestoreLockedMode=false
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter 'FullyQualifiedName~Njulf.Tests.PhysicsTests|FullyQualifiedName~Njulf.Tests.PhysicsGeometryTests'
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-build --filter 'FullyQualifiedName~GameTimingTests|FullyQualifiedName~SceneTests|FullyQualifiedName~GameTimingLifecycleTests|FullyQualifiedName~GameLifecycleIntegrationTests'
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development --no-restore
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example physics-query --frames 180 --validation
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example physics-simulation --frames 180 --validation
$env:DOTNET_TieredCompilation='0'
foreach ($variant in @('static','moving')) {
    foreach ($mode in @('Disabled','QueryOnly','Simulation')) {
        dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --physics-workload $mode $variant "docs/performance/milestones/20260912-jitter-$mode-$variant.json"
    }
}
```

## Artifact retention

### Follow-up: box shading and stack interaction

The initial example reused eight cube corners, allowing generated normals to smooth across hard edges. Replaced these with 24 face-local vertices, explicit outward flat normals, and matching tangents/UVs. The platform now waits one simulated second, then sweeps from x=2 to x=-2 at angular frequency 1.5 radians/second. The ground was enlarged to retain fallen boxes. A longer example smoke now rejects a stack that never tilts more than about 37 degrees after eight simulated seconds.

Rejected intermediate: extending travel alone at 0.8 radians/second slid the rendered stack without toppling it during the run. CPU scene checks compared 0.8, 1.5, 2, and 3 radians/second; 1.5 supplied a reliable impact without the much wider scattering of 3. Final rendered validation: stack toppled at **2.63 simulated seconds**, **720 full-quality frames**, **1,033 physics steps**, zero Vulkan validation errors, and no floor-penetration failure. The final screenshot was inspected for flat faces and a collapsed stack. Development build: zero warnings/errors. Reproduction: `dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example physics-simulation --frames 720 --validation`. This is functional/visual validation, not a performance comparison. Follow-up logs, temporary CPU fixture, and screenshots are under the same D: investigation directory and retention policy.

Current raw smoke images/logs and the pinned upstream source inspection live under `.codex-tmp/jitter-integration/` on D:, for this investigation and at most two days after completion. The compact record above preserves failed-candidate findings. `tools/prune-local-artifacts.ps1` was inspected and run with `-Apply` after checking candidate roots against running process inputs. It removed **5,172 obsolete generated payload files (13.61 GiB)** without failures, leaving about **258 GiB** free. Recent payloads, tracked files, reparse points, compact text/JSON evidence, shader cache, and the protected campaign reference directory were preserved. [Cleanup evidence](cleanup-20260912-204838.json).
