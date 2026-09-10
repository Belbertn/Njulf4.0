# Production pipeline ownership — 2026-09-09

- Source: `10440367`, clean at implementation start; candidate is the uncommitted item 11 extraction.
- Machine: Windows, AMD Ryzen 5 5600H, RTX 3060 Laptop GPU, NVIDIA 610.62, .NET SDK 10.0.203.
- Boundary: internal production pipeline owner, explicit borrowed dependencies, pass ownership handoff on registration, retained frame ordering/waits and existing disposal stage dependencies in VulkanRenderer. See [ownership contract](../../ProductionPipelineOwnership.md).
- Failure handling: constructor unwind for native resources acquired before construction returns; pending-pass and graph cleanup retry without repeating successful releases. Readiness is published only after construction and graph initialization complete.

## Validation

- Development solution build: passed, zero warnings/errors. Final test-only build also passed, zero warnings/errors.
- 53 focused tests passed: production ownership, renderer lifetime, staged disposal, graph lifetime, production declarations, compilation scheduler and three GPU custom-rendering lifecycle cases. The eight ownership tests were rerun successfully after strengthening partial registration to use an actual missing-pass failure.
- GPU lifecycle includes real window/framebuffer resize and meshlet diagnostic variants enabled then disabled; camera aspect and recreation reason are checked, with zero validation errors.
- Initial fixture failures: graph initialization fixture lacked required resource declarations (fixed); resize fixture requested 96×80 but Windows clamped it to 120×80. Failure diagnostics showed phase 1, counters enabled, full quality and successful swapchain resize. The corrected fixture uses 256×192 → 320×240. Neither failure required renderer logic changes.
- Structural audit: production construction and graph initialization method tokens match the prior implementation after reversing dependency names, tracking wrappers and registration callback. Shader sources, ABI layouts and resource declarations were not changed.
- Active-scene custom example: 960×640, 360 full-quality frames, Vulkan validation and forced async Bloom; one async pass active, zero validation errors. Existing example exercises target replacement/rebinding at frame 180 and resolution rebuilding at frame 240. Capture inspected: scene triangle, custom target panel and overlay visible.
- Active-scene observed intervals (240 samples): median 16.686 ms, p95 19.743 ms, p99 52.657 ms; 556,956,288 allocated bytes. No matched baseline was collected; these validation-run numbers include resource changes and are not performance qualification.
- Blocking-active-scene custom example: 360 full-quality frames, zero validation errors; target rebinding and resolution rebuilding completed. Capture inspected with the same visible scene, panel and overlay. Observed intervals (240 samples): median 16.682 ms, p95 18.371 ms, p99 22.794 ms; 155,577,344 allocated bytes.
- Blocking startup took about 331 seconds and exceeded its reporting-only startup latency threshold. CPU activity continued during preparation; a brief attached managed-debugger pause exposed no managed main-thread frames, so it did not establish the native cause. The process resumed and completed normally within the example's ten-minute limit. No baseline blocking run was collected and debugger attachment perturbed timing; do not interpret this as a measured extraction regression or as acceptable startup performance.
- Decision: retain the ownership extraction on focused behavioral and GPU correctness evidence. Blocking startup latency remains unqualified; investigate it separately with a matched baseline if that mode's startup performance is required. Final whitespace diff check passed.

## Reproduction and retention

```powershell
dotnet build Njulf.sln -c Development --no-restore
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-build --filter 'FullyQualifiedName~ProductionPipelineOwnershipTests|FullyQualifiedName~RendererLifetimeCoordinatorTests|FullyQualifiedName~StagedDisposalPlanTests|FullyQualifiedName~RenderGraphLifetimeTests|FullyQualifiedName~ProductionRenderPipelineDeclarationTests|FullyQualifiedName~PipelineCompilationSchedulerTests|FullyQualifiedName~CustomRenderingLifecycleTests'
$env:NJULF_PIPELINE_STARTUP_MODE='active-scene'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example custom --frames 360 --validation --async-validation --capture artifacts/pipeline-extraction/custom-active-new.png
$env:NJULF_PIPELINE_STARTUP_MODE='blocking-active-scene'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example custom --frames 360 --validation --capture artifacts/pipeline-extraction/custom-blocking-new.png
Remove-Item Env:NJULF_PIPELINE_STARTUP_MODE
```

Compact logs/TRX are under ignored `artifacts/pipeline-extraction`. Pruner inspection reported
295.57 GiB free and 4.84 GiB eligible across investigations; active and unrelated campaign
data was retained. Bulky captures and scratch tooling remain only for the active investigation
and normal two-day retention. No new public API, project boundary or rendering backend was added.
Validation is limited to Windows/NVIDIA and the current single-view workloads, not every
specialist GI mode or native allocation failure. No broad performance campaign was run.
