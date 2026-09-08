# Sponza imported shadows: zero capacity — 2026-09-07

Revision `7eafc485087af01fabfa4a931fb4d03eae93dae7`, dirty workspace including earlier local-shadow and unrelated user edits. User workload: Sponza, imported point lights enabled, 1024 pixel requests, RTX 3060 Laptop GPU, 1600×900. [Compact evidence](20260907-sponza-shadow-zero-capacity.json).

The supplied capture reports 23 eligible point lights, the point pass enabled, zero selected, all 23 rejected before memory allocation, and no allocation failures. Scene setup still set point/spot count limits to zero. The imported-shadow policy correctly preserved configured counts, so enabling its gate could not admit lights. The earlier synthetic GPU fixture explicitly restored a positive count and therefore missed this integration failure.

Removed count resets from Sponza profiles, shared sample lighting, and the area-light scene. Low quality now disables local passes with their gates while retaining the standard capacity for later explicit activation. Lighting-mode changes preserve configured counts, including intentional zero. Removed the synthetic fixture's forced counts. Status messages distinguish a zero count, an exhausted positive count, invalid spot cone, inactive intensity/range, memory budget, and allocation failure. Captures now record both configured count limits and the memory budget.

Validation: a new test drives actual Sponza High/Medium/Low setup, imported-light activation, shadow policy, selection, and memory planning. All three failed before the fix (7 eligible, 0 admitted), then passed (7 admitted). Explicit zero still rejects selection. Tests also cover lighting-mode transitions and diagnostic reasons. The first draft test was not public; corrected before recording the pre-fix behavior above. Final filtered run: 97 passed, no failures/skips, including editor and sample configuration coverage; sample/editor/renderer compiled during the test build. `git diff --check` passed.

This is a settings/admission correction. No shader or GPU lifetime changes; no new GPU capture or performance comparison was required. Baseline/candidate frame timings and tail latency are not measured for this fix. The source user capture and screenshot remain untouched on C:; only compact evidence was written on D:. No new bulky artifacts were generated. Prior same-session storage review reported 294.71 GiB free on D:.

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:ShaderBuildMode=UseExisting --filter "FullyQualifiedName~ModelLightImport|FullyQualifiedName~LocalShadow|FullyQualifiedName~SampleGlobalIlluminationValidationSettings|FullyQualifiedName~SampleVfxShowcase|FullyQualifiedName~ShadowEditorPanel|FullyQualifiedName~SampleAnalyticalArea" --verbosity quiet
```

Restart the rebuilt editor to apply corrected scene setup. No asset recook is needed. An intentionally saved zero limit remains zero and is now identified explicitly in the editor.
