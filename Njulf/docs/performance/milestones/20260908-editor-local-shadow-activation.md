# Editor point and spot shadow activation — 2026-09-08

Source revision: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, dirty workspace with pre-existing framework/API changes. Hardware: AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU and AMD integrated graphics. Workload: Sponza editor, manually added point light, then enable **Casts shadows**. Verification uses CPU settings, selection, and allocation planning under the actual High, Medium, and Low Sponza memory profiles, with point and spot cases.

The light checkbox previously updated `CastsShadows` while Sponza's renderer pass remained disabled. The regression fixture demonstrates one eligible light and zero selected maps before explicit activation. Setting that flag alone is insufficient. The editor now forwards successful light additions and edits to the renderer's shadow policy. Enabling shadows, or changing a shadow-casting light's type, activates its point or spot pass. Ordinary light edits preserve a manually disabled renderer pass. Count and memory limits remain authoritative, including zero.

Explicit activation also updates the saved settings in an active imported-light shadow override, so turning off imported shadows preserves the manual light's request. Disabled-pass diagnostics identify the exact **Shadows > Point/Spot > Point/Spot Shadows Enabled** control.

Candidate result: one admitted map with positive planned image storage in each of the six Sponza profile/type cases. The focused Development build and test run passed **69 tests**, with zero failures or skips, including 14 new cases covering activation, type changes, unrelated edits, zero capacity/memory, and imported-override restoration. `git diff --check` passed. Baseline behavior is observed in the regression fixture before invoking the new policy; a separate run of the new tests against old production code was not performed.

Decision: retain the editor activation fix. Baseline/candidate frame timings and tail latency were not measured; this is a correctness change with no performance claim. Enabling shadows adds rendering work within the configured budgets. Tests exercise policy and renderer admission directly; no live Sponza image or GPU draw was captured. The fix applies when shadows are explicitly requested through editor add/edit commands.

Storage review: `tools/prune-local-artifacts.ps1` dry run found 414 older payload candidates totaling 3.48 GiB, with 296.58 GiB free on D:. Those candidates belong to other investigations with unverified active-reference ownership and were retained. This fix generated only normal Development build outputs and compact test log/TRX evidence under `.codex-tmp/point-light-shadows/` on D:.

Reproduce from the repository root with `TEMP` and `TMP` pointing to an existing workspace directory on D::

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -p:ShaderBuildMode=UseExisting --filter 'FullyQualifiedName~EditorLightShadowTests|FullyQualifiedName~ModelLightImportTests|FullyQualifiedName~LocalShadowCapacityTests|FullyQualifiedName~ShadowEditorPanelTests' --verbosity quiet
```

For an already checked light in the rebuilt editor, toggle **Casts shadows** off and on, or enable **Shadows > Point > Point Shadows Enabled** directly.
