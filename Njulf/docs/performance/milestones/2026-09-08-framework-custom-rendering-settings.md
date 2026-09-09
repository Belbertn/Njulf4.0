# Framework custom rendering and settings — 2026-09-08

- Source: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, dirty throughout. Preserved earlier framework work and unrelated light/shadow edits. Implemented the approved single-view scope of items 7–8.
- Hardware: Ryzen 5 5600H, RTX 3060 Laptop GPU, NVIDIA driver 32.0.16.1062; Windows, .NET SDK 10.0.203.
- Changes: typed RGBA16F targets and storage/transfer buffers; scoped Vulkan callbacks at three graph stages; retained/rebound resources, physical image aliases and partitioned buffer ranges, deferred native cleanup; common settings preview/apply/cancellation and capability snapshots; editor controls, examples and XML/API documentation.
- Ownership: TextureManager owns images/descriptors, BufferManager owns buffers, graph borrows, registrations retain dependencies. Existing GPU completion and device-idle fault paths retire native owners. Existing settings schema 28, native ABI and built-in graph IDs remain unchanged.

## Validation

- Development solution build passed. The test-field naming warning found during the last build was fixed with a Rider semantic rename; final build evidence is `artifacts/framework-contracts/items78-build-complete.log`.
- **134 focused tests passed**, none skipped: `items78-complete.trx` and `items78-complete-tests.log` in `artifacts/framework-contracts/`. Covers settings purity, preset overrides/admission, restart/rejection, cancellation before/after processing, shutdown settlement, persistence, editor catalog, graph resource/lifetime/async contracts, typed resource rejection, overlapping ranges, pass ordering/removal, load-time settings awaits and recording failure.
- Custom example: **360 full-quality frames, zero Vulkan validation errors**, with AA enabled and disabled. Compute writes an image/buffer, graphics samples into scene color, a scene material shares the target, and a final backbuffer overlay renders. Rebinding replaces the target after frame 180; a receipted scene-resolution rebuild occurs after frame 240. Latest evidence: `items78-custom-final.log/.png` and `items78-custom-no-aa-final.log/.png`.
- Async Bloom was **actually active** (one enabled pass) in `items78-custom-final.log`; its handoff runs alongside the custom stages without validation errors. This is a focused correctness run, not profitability certification.
- Inspected AA-on/off images: expected corner gradient, retained material and green final overlay. No-custom-pass before/after captures are visually consistent; no pixel-equality claim.
- `git diff --check` passed.

## Short no-custom-pass comparison

Workload: Development procedural API example, 960×640, default DdgiHigh/AA, exposure 0.01, auto exposure off, validation off, 120 warmup frames followed by 240 frame intervals; same screenshot/exit settings. No competing builds or GPU tests ran during the timed executions.

| Observation | Before | After |
| --- | ---: | ---: |
| Median frame interval | 16.575 ms | 16.687 ms |
| p95 frame interval | 19.589 ms | 18.610 ms |
| p99 frame interval | 23.455 ms | 22.507 ms |
| Process allocation, frame 120 through shutdown | 545,187,224 B | 185,884,272 B |

Decision: **keep the implementation on contract evidence; performance comparison is inconclusive**. Median increased 0.112 ms (0.68%); p95 decreased 0.979 ms (5.00%); p99 decreased 0.948 ms (4.04%). These are paced frame intervals, not isolated renderer timings. Cache membership differs and the candidate log records a 1,630.978 ms hitch dominated by scene/pipeline preparation. p99 does not bound that rare event; the full series/maximum were not retained. Allocation totals include capture, background work and shutdown, so their reduction is not attributed to the change. No broad performance campaign or hardware qualification was run. See the [compact comparison](2026-09-08-framework-rendering-settings-comparison.json).

## Rejected attempts and limitations

- First custom GPU attempt failed graph validation because image aliases had different layout-provider delegates. Fixed by sharing one physical texture state and cached tracker/provider delegates; subsequent material/custom and async runs passed.
- Force mode alone did not authorize a validation path. Selecting DDGI then exposed existing missing sampled-atlas bindings, and no DDGI async work ran. Preserved that fail-closed policy; selected Bloom for an actual handoff check. Logs: `items78-custom-async.log`, `items78-custom-async2.log`, `items78-custom-async-bloom.log`.
- Presets that alter immutable advanced-GI inventory/profile return RestartRequired. Specialist settings retain the advanced API. Multiple views, custom async scheduling and a portable shader/command abstraction were explicitly excluded.
- Optional hardware paths such as opacity micromaps were reported through existing capability/admission data, not newly qualified.

## Reproduction and retention

```powershell
dotnet build Njulf.sln -c Development --no-restore
dotnet test Njulf.Tests -c Development --no-restore --filter 'FullyQualifiedName~GraphicsSettingsControllerTests|FullyQualifiedName~VulkanPassContractTests|FullyQualifiedName~CustomRenderingLifecycleTests|FullyQualifiedName~GraphicsApiIntegrationTests|FullyQualifiedName~FrameworkContractTests|FullyQualifiedName~RenderGraphResourceDeclarationTests|FullyQualifiedName~RenderGraphLifetimeTests|FullyQualifiedName~AsyncComputePhase3Tests|FullyQualifiedName~RenderingSettingsEditorPanelTests|FullyQualifiedName~RenderSettingsFileIoTests'
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example custom --frames 360 --validation --async-validation --capture artifacts/framework-contracts/custom-repeat-new.png
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example custom --frames 360 --validation --no-aa --capture artifacts/framework-contracts/custom-no-aa-repeat-new.png
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example procedural --frames 360 --capture artifacts/framework-contracts/procedural-repeat-new.png
```

Set TEMP/TMP to the workspace's `artifacts/framework-contracts/temp` for test runs. All explicit logs, captures and results are on D:. Retain compact evidence; raw PNGs remain only for this investigation and the following two days. `tools/prune-local-artifacts.ps1` inspection found 416 eligible payloads / 3.52 GiB, 296.58 GiB free, zero inspection failures. Did not apply deletion: eligible older campaign/reference data is unrelated and its active status is uncertain. Inspection: `items78-storage-review.log`.
