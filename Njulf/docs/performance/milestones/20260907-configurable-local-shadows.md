# Configurable local shadows — 2026-09-07

Source: `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace, including pre-existing editor, imported-light, and secondary-view work. Changes are uncommitted.

## Decision and implementation

Implement independent configurable point/spot counts (defaults 32 each; bounded by the existing 1024-light engine capacity), a shared 256 MiB local depth-storage budget, caching enabled by default, and per-light 128–2048 pixel requests (zero inherits the 512 pixel global default). Preserve explicit priority when reducing resolutions, reduce resolution before omitting lights, and report requested/effective resolution and omission reasons in the editor. Imported-light toggles respect configured point/spot count limits.

Point lights own independent static/working six-face images. Spot lights use stable, variable-size aligned atlas regions. Persistent light identities own cache state; light projection and depth-bias changes, affected scene mutations, and coverage-affecting material edits invalidate static depth. Dynamic geometry and foliage compose onto static depth; previously dynamic maps are restored when dynamic casters disappear. Camera-independent local caster LOD avoids camera-induced depth changes. Allocation failures omit unavailable shadows and retry at a bounded interval.

Use the existing synchronous device-idle boundary for allocation changes. No new deferred-retirement queue, update scheduler, shadow streaming framework, or asset format is introduced. Existing assets do not need recooking.

## Validation

- Renderer, editor, and sample build successfully. The initial renderer build compiled the changed GLSL and all dependent variants successfully (10m36s, zero warnings/errors); subsequent C# iterations reused that compiled bundle.
- 124 focused tests pass: local selection/layout/cache, imported lights, shadow editor, persistence, render graph, and scene-data-builder coverage. Capacity cases include 7, 40, and 128 lights, both point and spot above historical limits. Tests cover memory-driven downgrade, zero budget, nonoverlapping persistent spot regions, shading-only versus depth changes, spatial caster movement/removal, skinned animation, dynamic cleanup state, and settings snapshots.
- Production GPU capture: NVIDIA RTX 3060 Laptop GPU, Vulkan 1.4.341, reported driver 610.248.0; Development build, High preset, 1600×900, procedural Cornell room with eight point and eight spot lights at 128/256/512 pixels, 900 active frames before capture. Both variants use the same resolution requests and memory allocation. No Vulkan validation errors in either completed capture.
- Cached capture: 16 cache hits, zero static refreshes, zero copies, zero point face draws. Forced-refresh capture: 16 static refreshes/copies and 48 point face draws. Both admit every light with zero allocation failures.
- RGB screenshot average absolute difference: 0.0266/0.0253/0.0198 on the 0–255 scale; maximum channel difference 14. Images inspected visually; no substantial shadow difference. This is an LDR comparison with temporal rendering still enabled, not an HDR or pixel-exact oracle.

## Timing evidence and limits

| Measurement | Forced refresh | Cached |
|---|---:|---:|
| Snapshot local shadow CPU recording | 1,458 µs | 7 µs |
| Snapshot point + spot GPU passes | 475 µs | 0 µs (rounded) |
| Last 120-frame CPU draw mean / p95 / max | 5.78 / 7.09 / 8.77 ms | 4.43 / 5.73 / 7.80 ms |
| Last 120-frame paced frame mean / p95 / max | 18.19 / 19.62 / 21.80 ms | 17.88 / 19.62 / 21.38 ms |

[Compact machine-readable comparison](20260907-local-shadows-comparison.json) includes sparse CPU recording samples, snapshot counters, image differences, and source capture paths. These are correctness/smoke observations, not a controlled shipping-performance qualification: validation, pacing, other render passes, and pipeline preparation affect whole-frame timing. The screenshot export itself produces a roughly 2.2-second diagnostic hitch and is outside the steady-state comparison. Static geometry was exercised on GPU; moving/skinned invalidation and memory-pressure planning were tested behaviorally, not exhaustively image-qualified across every material and GPU.

Rejected preliminary runs: a health-report export rejected missing producer commit metadata; an unsupported baseline fixture dispatch was corrected; an early screenshot captured bootstrap and was rejected. The fixture now waits for nonzero active shadow selection. Early termination while startup pipelines were still preparing reported pipeline teardown leaks; completed production captures exited without validation errors. A test build briefly failed because the capture process was still releasing its DLLs; rerunning after exit passed.

## Reproduction and retention

```powershell
dotnet build Njulf.Rendering/Njulf.Rendering.csproj -c Development --no-restore
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:ShaderBuildMode=UseExisting --filter "FullyQualifiedName~LocalShadow|FullyQualifiedName~ModelLightImport|FullyQualifiedName~ShadowEditorPanel|FullyQualifiedName~RenderSettingsPersistence|FullyQualifiedName~RenderGraph|FullyQualifiedName~SceneDataBuilder"
# Run sequentially; substitute LocalShadowCapacityUncached and a different output folder for the reference.
./NjulfHelloGame/bin/Development/net10.0/NjulfHelloGame.exe --scene GlobalIlluminationTest --performance-scenario LocalShadowCapacity --quality-preset High --smoke-frames 100000 --baseline-snapshot-dir artifacts/local-shadows/cached
```

Raw screenshots and reports are under ignored `artifacts/local-shadows/` on D:; retain only through the active investigation and at most two days afterward. Pruner dry run found 5.51 GiB of older payloads in unrelated migration/campaign folders; no deletion because their active-reference status was not established. D: has 294.71 GiB free. Preserve this compact record and comparison JSON.
