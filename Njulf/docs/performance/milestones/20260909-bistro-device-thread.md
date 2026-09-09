# Bistro scene preparation: device-thread fix — 2026-09-09

- Source: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, with substantial pre-existing framework, scene, renderer, and test changes. This fix preserves that work.
- Baseline: Rider reproduced generation 2, BistroExterior, after cooked upload completed at 92%. `Model.CreateInstance` validated graphics references on managed worker thread 24; the graphics device required thread 2. No successful baseline first-present timing exists.
- Change: assemble the isolated Bistro scene through the existing cooperative content pump (512 objects per step, existing 12 ms pump budget). Validate objects incrementally; resolve cached models asynchronously before attachment. Publish and drain cancelled/failed scenes on the device thread. Reuse existing scene finalization and retain full exception stacks in failure logs.
- Hardware/workload: AMD Ryzen 5 5600H; NVIDIA GeForce RTX 3060 Laptop GPU, Vulkan-reported driver 610.248.0. Development build, Standard Vulkan validation, default sample settings, warm application pipeline cache. Real shipping cooked models; zero source fallbacks.
- Validation: 47 focused preparation, transition-coordinator, dispatcher, and resource-lifetime tests passed. Tests exercise successful publication, cancellation before and during assembly, shutdown, build failure, supersession, and owner-thread retain/release balance.

| Candidate/workflow | Cold first present | Cold full residency | Resident first present | Maximum host step |
|---|---:|---:|---:|---:|
| Initial candidate, Sponza → Bistro | 10,811.141 ms | 16,842.300 ms | N/A | 2,362.832 ms |
| Final, Sponza → Bistro | 7,113.925 ms | 11,072.834 ms | N/A | 1,332.378 ms |
| Final, GI → Bistro → GI → Bistro | 12,750.562 ms | 20,008.877 ms | 527.139 ms | 3,538.331 ms |

- Rejected detail: the initial candidate fixed the exception but still resolved cached cooked models during device-thread assembly, producing 211.093 ms and 193.949 ms post-residency pump hitches. The final code resolves those borrowed models before dispatch. The final Sponza run logged no upload-pump hitches above the existing 33 ms warning threshold.
- Decision: retain the fix. Both final workflows successfully presented Bistro, including full residency and a warm revisit, with zero Vulkan validation errors/warnings and no device-thread exceptions. The GI return took 185.667 ms.
- Limits: both smoke reports have overall `failed` status because existing cold-load/maximum-host-step latency gates fail; the largest host steps were scene commit. The warm revisit met its 1,000 ms target. These are single functional runs, not controlled performance comparisons; p95/p99 distributions and image-quality comparisons were not measured. Debugger timings are not used as performance baselines.
- Evidence: [compact comparison](20260909-bistro-device-thread.json). Final raw reports/logs remain under workspace `artifacts/bistro-device-thread-*-20260909.*` for the active two-day retention window; superseded diagnostic and initial-candidate output was removed after consolidation.
- Storage: pruner inspection found 3.57 GiB of older payloads across other investigations and 296.57 GiB free on D:. Those older references were not established as obsolete in this task, so they were retained. No isolated build copies or captures were created. Rider's temporary C: log was copied to D:, hash-verified, and removed from C: during diagnosis.

Reproduce from the repository root:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~SampleScenePreparationWorkTests|FullyQualifiedName~SampleSceneTransitionCoordinatorTests|FullyQualifiedName~RenderThreadContentUploadDispatcherTests|FullyQualifiedName~ModelResourceLifetimeTests'
Push-Location NjulfHelloGame/bin/Development/net10.0
./NjulfHelloGame.exe --scene sponza --smoke-mode scene-transition --validation standard
./NjulfHelloGame.exe --scene GlobalIlluminationTest --smoke-mode scene-transition --validation standard
Pop-Location
```
