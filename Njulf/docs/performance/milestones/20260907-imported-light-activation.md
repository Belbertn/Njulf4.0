# Imported light activation — 2026-09-07

- Source: `7eafc485`, dirty workspace including the directional-light toggle work and unrelated renderer changes.
- Reported workload: Sponza, 1600×900, DdgiHigh, NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0. User report `performance-20260907-170142-6704272-f7d741cdb9fc44baa2ac830475d123d4.json` records 25 live lights: 23 point and 2 directional.
- Cause: `NjulfHelloGame/NewSponza_Main_glTF_003.gltf` contains 23 point definitions and 1 directional definition, all with intensity zero. The bulk toggle registered lights without giving them radiance.
- Baseline: `RuntimeController_ExplicitActivationLightsZeroIntensityDefinitions` failed after activation with actual live intensity 0, expected positive. Earlier directional-limit tests did not exercise zero-intensity input.
- Decision: explicit controller activation substitutes local intensity 100 / directional intensity 1 only for zero-intensity definitions. Source metadata and positive authored intensities remain unchanged. The editor identifies definitions that use defaults. Ordinary direct model-light instantiation retains source semantics.
- Validation: Development build succeeded; 46 imported-light/procedural-sky tests passed, zero failed/skipped. New regression observes live store intensity, preservation of positive values and source metadata, and behavior after placement movement. Existing coverage exercises directional switching, rollback and save/reload.
- Timings, including tail latency: not measured; this is a correctness fix, with no performance claim.
- Limitations: no Sponza image comparison. Rider's configured game launch used a different scene and disallowed dynamic launch overrides; the test location was not recognized as runnable by Rider. The debugger run was stopped and agent breakpoints removed. A normal filtered test run provided the failing/passing evidence. Defaults are activation policy, not recovered authoring values.
- Temporary runtime log: `.tmp/imported-lights-investigation/runtime.log` (293 KB; copied from the Codex-created Rider log on C:, SHA-256 verified before removing that C: copy). Subject to the two-day raw-artifact retention policy. User attachments are unchanged.
- Storage review: pruning dry run found 5.42 GiB in the existing C-drive migration archive; D: has 294.97 GiB free. That unrelated archive was left intact because this investigation did not establish whether its references remain active.

Reproduce the focused validation:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~ModelLightImportTests|FullyQualifiedName~ProceduralSkyModelTests' --verbosity minimal
```
