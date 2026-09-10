# Scene ownership implementation checkpoint — 2026-09-10

Source: `10440367`, dirty working tree. The pre-existing production-pipeline ownership work is preserved. Initial source/status/diff evidence lives under `artifacts/scene-views-20260910/`. This checkpoint covers authored light/environment and model-placement migration, not completion of the scene/view plan.

## Implemented behavior

- Editor and ordinary sample light mutations use scene-owned lights with stable GUIDs. Editing, selection, naming and saving work before GPU mirroring. Imported-light controller attachment origins use the scene store; unresolved IES source references survive edits.
- Editor authored environment controls commit to `Scene.Environment`. Runtime quality controls remain renderer settings. Sample environment presets are scene-owned; bootstrap/profile and capture overlays publish their authored settings to the relevant scene.
- Complete model placements belong to the scene as `ModelInstance` groups. Selecting one asset subobject creates only that standalone clone. Group deletion and preparation cancellation remove the owning placement. Foliage prototypes retain their cloned resource owner.
- Animated light mirroring reuses scratch collections and converts source values directly. Typed `Light` equality avoids boxing without changing its fields/layout. The focused allocation test measures zero bytes over 128 warmed-up source-position updates and mirror operations.

The renderer still mirrors one active scene into its existing global resources. Scene/view GPU isolation, descriptors, history transactions, public view APIs, full orthographic rendering, scheduling/composition/capture, the views example and editor preview remain unimplemented. This checkpoint does not make secondary public views available.

## Validation

- Development tests project compiled; 111 focused scene, scene-document, imported-light, editor, preparation/cancellation, transition and mirroring tests passed, zero skipped. Result: `artifacts/scene-views-20260910/stage1-verified/stage1.trx`.
- Development API examples build passed with zero warnings/errors: `stage1-examples-build.log`.
- Release sample build passed with zero warnings/errors. Two production timing captures each collected 480 valid GPU samples after 120 warmup frames. Both HDR comparisons passed. Shader inventories were stable and matched the baseline; capture metadata differed only in executable hash.
- `git diff --check` passed.

Earlier rejected checks are retained in compact logs/TRX: an editor test incorrectly asserted that all scene updateables disappear after deleting a placement, overlooking the retained imported-light controller. It now checks removal of the deleted child. The first mirroring allocation check measured 73,728 bytes across 128 updates; typed `Light` equality removed that allocation and the retry passed.

## Render/timing evidence

Same hardware/workload as the [accepted baseline](20260910-scene-view-baseline.md): Ryzen 5 5600H, RTX 3060 Laptop, Vulkan 1.4.341, runtime driver 610.248.0, .NET SDK 10.0.203; Bistro Normal, DdgiHigh, 1920×1080, cooked assets, VSync/validation off, GPU timestamps on, blocking-active-scene startup and full-quality readiness. No shader changes in this checkpoint.

| Run | CPU p50/p95/p99 ms | GPU p50/p95/p99 ms | HDR relative RMSE |
| --- | --- | --- | ---: |
| Baseline | 8.051 / 9.939 / 14.486 | 21.918 / 22.177 / 22.344 | 0.001961 |
| Ownership candidate | 8.3055 / 10.961 / 13.651 | 21.8975 / 22.252 / 22.544 | 0.001036 |
| Candidate repeat | 7.8005 / 11.601 / 17.357 | 21.6875 / 23.734 / 25.830 | 0.002416 |

Decision: retain the functional implementation as work in progress; **performance acceptance remains open**. The repeat did not reproduce the median increase, but CPU p95 increases repeated and the second run also exceeded GPU tail thresholds. No causal explanation has been established. Neither capture rebuilt scene payloads during measurement. Small increases across unrelated CPU stages do not establish whether the tail behavior is environmental or caused by this change. Do not label this a performance pass or a speedup. Full-renderer allocated bytes/frame comparison is still outstanding; the zero-allocation result above covers only animated-light mirroring.

Compact evidence: [comparison JSON](20260910-scene-ownership-comparison.json). Raw captures remain under `artifacts/scene-views-20260910/stage1-timing*/` while investigation is active.

```powershell
. ./artifacts/scene-views-20260910/environment.ps1
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Njulf.Tests.SceneTests|FullyQualifiedName~SceneDocumentTests|FullyQualifiedName~ModelLightImportTests|FullyQualifiedName~SampleScenePreparationWorkTests|FullyQualifiedName~SampleLightingSceneTests|FullyQualifiedName~EditorLightShadowTests|FullyQualifiedName~SampleSceneTransitionRecoveryTests|FullyQualifiedName~GraphicsApiIntegrationTests.Editor|FullyQualifiedName~GraphicsApiIntegrationTests.SceneSwitch|FullyQualifiedName~GraphicsApiIntegrationTests.AnimatedSceneLightMirroring' --results-directory artifacts/scene-views-20260910/stage1-verified
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
pwsh -NoProfile -File tools/perf-loop.ps1 -BaselineOnly -RepeatCount 1 -Configuration Release -Scene Bistro -Scenario Normal -WarmupFrames 120 -MeasureFrames 480 -BenchmarkTimeoutSeconds 2400 -RunDirectory artifacts/scene-views-20260910/stage1-timing-repeat -HdrReferencePath artifacts/scene-views-20260910/reference-blocking.pfm
```

## Storage and remaining gates

The pruning tool's read-only inspection reported 7.84 GiB eligible by age, including 2.89 GiB of copied caches in this active investigation, with about 288.5 GiB free on D:. No blanket `-Apply` was run because those old-timestamp cache files remain active inputs. All new benchmark/test/cache payloads remain on D:. Keep the reference and relevant evidence for the open comparison; prune obsolete raw payloads within two days of completion under the repository policy.

Next required work: resolve the timing tails, isolate scene/view GPU ownership while retaining the single-view path, then implement the remaining projection/API/rendering/integration stages and their evidence gates. The 600-frame views validation/capture and temporal isolation oracles have not run because the feature is not implemented.
