# Imported shadow renderer activation — 2026-09-07

- Source: `7eafc485`, dirty workspace containing the imported-light/editor work and unrelated renderer changes.
- Workload: user Sponza report `performance-20260907-174957-0071984-377e13711f9d4e5eb729f4a59ab93dfe.json`; NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.248.0, DdgiHigh, FullFrame feature isolation.
- Baseline evidence: 23 point lights are shadow candidates, but `PointShadowsEnabled=0`, selected count 0, rejected-by-budget count 23, rendered face count 0, shadow-map bytes 0. The bulk switch successfully changed light flags but left the renderer pass disabled.
- Rejected behavior: setting `CastsShadows` alone cannot activate point-shadow selection or resource allocation. A regression reproduces 23 candidates and zero selected with that configuration.
- Decision: apply a scene-owned imported-shadow policy before renderer shadow selection/resource allocation. Enable the required shadow passes and raise their budgets within the existing renderer limits. Restore prior gates and budgets when the switch is turned off, imports are deactivated, or the controller changes. Retain individual light settings and scene persistence. Show selected/candidate counts and the point-shadow limit beside the editor switch.
- Candidate evidence: the same 23-light fixture selects 4 and rejects 19 by budget; the allocation predicate enables the cubemap array, the GPU data builder produces 4 shadow records, and the index map associates 4 lights with them. Additional cases cover delayed activation for spot, rectangle, disk, tube and directional lights and restoration on scene exit.
- Validation: Development build and 65 focused imported-light, local-shadow and procedural-sky tests passed, with zero failures/skips. Whitespace validation passed. No asset recook or shader changes required.
- Timings, including tail latency: not measured; this is a correctness fix, with no performance claim. More shadowed lights increase rendering work.
- Limitations: renderer supports at most 4 simultaneous point shadow maps. Remaining point lights still illuminate without mapped shadows. Tests exercise selection, allocation decisions and GPU record construction; no post-fix Sponza image or live GPU draw was captured.
- Storage review: pruning dry run found 5.46 GiB of old payloads (5.42 GiB migration archive, 0.05 GiB Sponza campaign), with 294.97 GiB free on D:. Existing archives were retained because active-reference ownership was not established. No raw captures or isolated build copies were generated for this fix; user attachments remain unchanged.

Reproduce:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~ModelLightImportTests|FullyQualifiedName~LocalShadowTests|FullyQualifiedName~ProceduralSkyModelTests' --verbosity minimal
```
