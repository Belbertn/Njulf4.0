# Moving-object GI invalidation — 2026-09-13

Kept a fix for spatial invalidation in the GPU DDGI scheduler. A moved box could leave stale illumination/visibility at its former position because the dirty-region test addressed a different world cell from the probe being scheduled.

Source started clean at `cc35bcc6fc69854dd96e316cd6e14ec69b139e8e`. The candidate changes the scheduler spatial helper, its include, and focused GPU regression coverage. No update budgets, hysteresis, or quality settings were changed. [Compact results](20260913-gi-moving-object-invalidation.json).

## Cause and runtime evidence

`ddgi_simple_schedule_classify.comp` obtains a scheduling ordinal, selects the probe with `SchedulerSequenceProbeIndex`, and tests its affected region with `SchedulerDirtyIntersects`. The former applied `(ordinal * stride) % probeCount`; the latter treated the ordinal itself as a world-lattice index.

Rider was attached to an agent-launched `physics-simulation` API example. A pause in `ExampleGame.OnFramePresented` provided the call stack and live volume policy: 28 × 14 × 28 probes, stride 5489, spacing 0.875 m, origin (-9.1875, -3.9375, -7.4375), and zero physical offsets. One captured movement region should cover 324 probes after the existing spacing expansion; evaluating the old mapping with these inputs misses 180 and selects 180 unrelated probes. This count is calculated from debugger inputs, not a diagnostic GPU counter.

For example, scheduling ordinal 7925 selects probe 2437 at (-8.3125, -1.3125, -2.1875), but the old bounds test used (-8.3125, -1.3125, 10.0625), 12.25 m away. Missed probes can retain the old box's cached source/visibility until a later refresh.

Both operations now share `SchedulerSequenceLogicalCoordinates` in `ddgi_simple_schedule_spatial.glsl`. Physical toroidal offsets remain confined to storage addressing.

The screenshot matches `Njulf.ApiExamples/PhysicsExample.cs`. The separately running HelloGame was on MaterialShowcase; its inspection was not used as evidence for the physics defect. That process was detached and left running. Agent breakpoints were removed, and the agent's diagnostic process was stopped.

## Validation and timing

Hardware: NVIDIA GeForce RTX 3060 Laptop GPU; Vulkan 1.4.341; driver reported by the renderer as 610.248.0. Development build, physics simulation, fixed camera, 960 × 640, DdgiHigh, GPU-resident scheduler, dense probes, Exact receiver cache.

The surface-free GPU regression compiles and dispatches the actual production spatial helpers against serialized volume/dirty-region inputs. Its independent oracle enumerates world cells, including both old and new footprints, without using the scheduling permutation.

| Case | Before missed / unrelated probes | After missed / unrelated probes |
| --- | ---: | ---: |
| Linear order | 0 / 0 | 0 / 0 |
| Stride 5489 | 120 / 120 | 0 / 0 |
| Stride 5489 and wrapped storage on all axes | 120 / 120 | 0 / 0 |

Before: one test passed and two failed. After: all three passed. The existing material GPU conformance fixture also passed, checking the shared Vulkan harness still runs its original 36 material cases; six selected NUnit cases passed in total. Thirteen affected production shader artifacts were freshly compiled and passed `spirv-val`. The rebuilt physics example used the same shader assembly hash as the source build and completed 600 frames with standard Vulkan validation and zero errors.

| Run | Validation | Frame interval median / P95 / P99 (ms) | Samples |
| --- | --- | --- | ---: |
| Baseline smoke | Off | 11.118 / 12.523 / 14.557 | 480 |
| Candidate diagnostic | Standard | 11.127 / 14.341 / 25.837 | 480 |

These timings are not comparable: validation, cache preparation, and the physics steps reached during each capture differ. No execution-speed improvement or end-to-end mutation latency is claimed. Debugger-run timings were discarded. The captured candidate image is a smoke check, not a deterministic comparison at the exact reported box pose. Inactive retry, dirty-work retention, and other scheduling policies were not changed or qualified by this fix.

## Reproduction

From the repository root, with the existing Development project references built:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:BuildProjectReferences=false --filter 'FullyQualifiedName~SimpleDdgiSpatialInvalidationGpuTests|FullyQualifiedName~GiMaterialGpuConformanceTests'
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Development --no-restore
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development --no-restore -p:BuildProjectReferences=false
& ./Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.exe --example physics-simulation --frames 600 --validation --capture ./artifacts/gi-box-invalidation-repro.png
```

Use a new capture path. The GPU test requires glslangValidator and a Vulkan compute device; a capability skip is unavailable evidence.

Raw logs, TRX results, and the small active captures are under `artifacts/gi-box-invalidation-20260913/` (approximately 1 MiB). The durable evidence is this record and its JSON companion. Storage was inspected using `tools/prune-local-artifacts.ps1`; obsolete generated payloads were checked against running process inputs before applying its normal two-day cleanup.
