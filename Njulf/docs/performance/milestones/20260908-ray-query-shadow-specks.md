# Ray-query shadow bright specks — 2026-09-08

Source revision `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace with existing asset, local-shadow, renderer, GI and sample edits. This correction changes only the receiver-facing early exits in `directional_ray_shadow.comp` and `forward.frag`. Existing changes in the latter are preserved. [Compact comparison evidence](20260908-ray-query-shadow-specks.json).

The user supplied a 1600×900 Sponza image and `performance-20260908-061823-2733698-56db8696bcd04f8593528646f27b60e3.json`. The capture reports RayQueryHard requested and effective, no fallback, RTX 3060 Laptop GPU, driver `610.248.0`, Development. The shared hard/soft compute path returned full visibility without tracing when a normal reconstructed from neighboring depth samples pointed away from the sun (`N·L < -0.25`). At folds and silhouettes this estimate can disagree with the forward material's shading normal. Full visibility then produces isolated direct-light leaks. The layered fragment path contained the same unsupported visibility assumption.

Removed both orientation-based fully-lit exits. Normals still determine the ray-origin bias; actual shadow queries determine visibility. Invalid/background receivers, ray bounds, alpha handling and soft-shadow filtering retain their existing behavior.

Reproduced at the supplied camera: position `(5.1274147, 10.843294, 0.28348958)`, yaw `-1.2049768`, pitch `0.69391876`, FOV `0.98174775`, near/far `0.05/250`. The diagnostic host compiles the current sample with an isolated Draw hook; source assets are linked within D:. It locks exposure to `1.2279266`, freezes the Sponza sun, disables GI, environment surface lighting, reflections, bloom and fog to isolate direct illumination, and captures hard and temporally/spatially filtered soft shadows. It uses the production Vulkan renderer and shader override resolver. Soft capture confirms active history and no mode fallback.

The baseline reproduces the curtain specks at the same screen locations as the user image. The first candidate changes only the compute shader. In independently selected curtain rectangles `[800,465,828,583)` and `[938,685,976,825)`, pixels with any RGB channel ≥16 fall from **182 to 0 in hard mode** and **248 to 0 in soft mode**; both corrected rectangles are entirely black in the direct-light output. Hard-mode sunlit stone `[100,566,177,663)` is byte-identical. Soft sunlit stone differs by only 149 total channel levels across 7,469 pixels. Both candidate modes report zero Vulkan warnings/errors. This is direct rendered regression evidence; source-string checks alone cannot detect this defect.

Initial compute-only timing observations, microseconds, 99 frames per mode after warmup:

| Metric | Baseline median / P95 / P99 | Candidate median / P95 / P99 |
| --- | --- | --- |
| Hard shadow trace | 839 / 1073 / 1093 | 941 / 1165 / 1297 |
| Soft shadow trace | 878 / 902 / 916 | 987 / 1004 / 1014 |
| Hard GPU frame | 9915 / 10459 / 10559 | 9915 / 10588 / 10878 |
| Soft GPU frame | 11365 / 11593 / 11702 | 11306 / 11499 / 11628 |

Decision: retain the visibility correction despite approximately 0.1 ms more tracing in this view. These short diagnostic windows establish the cost scale, not a performance qualification. They do not measure the user's complete GI workload or transparent-only regression coverage. The gallery region retains some other nonzero direct pixels; this investigation does not classify every remaining edge pixel as an error.

Development sample/editor build passed with zero warnings/errors. `spirv-val --target-env vulkan1.3` passed for the production shadow compute module and ordinary, generic and weighted-OIT transparent ray variants. The built compute module is byte-identical to the tested candidate (`4d93dd60959892c5d06940b0aeafcf78a8bc67c8c8d55b02ecbed53c2d4ae348`). All 14 focused directional-shadow contract tests passed, no skips; these supplement the rendered regression gate.

Final verification repeated the renderer capture with both source corrections and the same shader assembly as the rebuilt editor. The curtain gate and hard sunlit control pass again; soft sunlit control differs by 76 total channel levels. Both modes have no fallback and zero Vulkan warnings/errors; soft history is valid. Final trace median/P95/P99 is `952/991/1181 µs` hard and `999/1023/1049 µs` soft. Final GPU frame median/P95/P99 is `9981/10189/10755 µs` hard and `11474/11692/11875 µs` soft. The fresh generic transparent pipeline required several minutes of native driver compilation before this run (274.002 s to production-graph scene present); no corresponding cold-baseline startup measurement was taken. This startup observation is separate from the settled frame measurements. After all outputs were written and the image gate passed, the owned diagnostic host was stopped because shutdown had not returned; shutdown behavior was not qualified by this investigation.

Reproduction files and active images reside under `artifacts/ray-shadow-specks-20260908/`. `host/ShadowCapture.cs` contains the exact camera/settings/frame sequence, `baseline.comp` preserves the unfixed shader, and `compare.ps1` asserts the reproduced baseline, zero direct light in both curtain regions in both modes, and byte-identical hard-mode sunlit control, then records timing percentiles. The original user attachments remain untouched on C:. The initial launcher attempts used an invalid quality option, then accidentally selected the sample's short startup smoke mode; neither produced accepted comparison data. Successful captures explicitly use `--smoke-mode none`. A startup-only pipeline-creation assertion was temporarily bypassed in the isolated diagnostic host; that branch was not exercised in the accepted non-smoke runs. The bypass was removed before final verification. Production startup code is unchanged.

```powershell
dotnet build NjulfHelloGame/NjulfHelloGame.csproj -c Development --no-restore -v:q
dotnet build artifacts/ray-shadow-specks-20260908/host/ShadowCapture.csproj -c Development --no-restore -p:NjulfShaderBuildMode=UseExisting -v:q
./artifacts/ray-shadow-specks-20260908/compile.ps1
./artifacts/ray-shadow-specks-20260908/run.ps1 -Variant baseline
# Wait for baseline/timings.json and process exit before the next GPU run.
./artifacts/ray-shadow-specks-20260908/run.ps1 -Variant candidate
./artifacts/ray-shadow-specks-20260908/compare.ps1
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:NjulfShaderBuildMode=UseExisting --filter 'FullyQualifiedName~DirectionalRayShadowShaderContractTests|FullyQualifiedName~DirectionalShadowContractsTests' -v:q
```

Storage review: `tools/prune-local-artifacts.ps1` dry run reported approximately 298 GiB free on D:. Its candidates included active diagnostic dependencies with inherited old timestamps, so no blanket apply was performed during rendering. The two rejected startup PNGs were pruned after recording their causes. Final capture output supersedes the initial candidate images rather than keeping another archive. Raw captures and isolated binaries are temporary and subject to the two-day retention limit; compact comparison evidence and this record are retained. No source assets or original user attachments were deleted.
