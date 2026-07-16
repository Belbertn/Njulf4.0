# Phase 20 Shipping Hardening Plan

Target outcome: Njulf's renderer is reliable enough for game production. It should survive normal application lifecycle operations, fail clearly on unsupported hardware and bad content, provide crash-safe diagnostics, avoid resource leaks across long sessions and repeated scene reloads, and have CI gates that catch regressions before they reach the sample game.

Phase 20 is not a feature phase. It is the production readiness pass that turns the renderer from a visually capable technology sample into a dependable runtime component:
- lifecycle operations are tested and repeatable.
- device and feature rejection paths produce actionable messages.
- missing or invalid assets fall back predictably or fail with responsible paths.
- scene load/unload cycles do not leak CPU, GPU, descriptor, or bindless resources.
- long sessions expose descriptor exhaustion, stale deferred deletion, and memory growth.
- renderer initialization writes crash-safe logs before and after risky native calls.
- GPU validation can be enabled intentionally for local and CI smoke runs.
- build, test, shader, formatting, and smoke checks are automated.

## Current Baseline

The repo already has several pieces that Phase 20 can build on:

1. `Njulf.Core/Game.cs` owns window creation, service initialization, renderer initialization, render loop events, framebuffer resize, shutdown, and disposal ordering.
2. `Njulf.Rendering/VulkanRenderer.cs` owns renderer initialization, frame lifecycle, swapchain recreation, diagnostics construction, render target rebinding, and renderer disposal.
3. `Njulf.Rendering/Core/VulkanContext.cs` already validates required instance layers, instance extensions, device extensions, Vulkan 1.3 support, mesh shader support, descriptor indexing, dynamic rendering, synchronization2, buffer device address, and deferred host operations.
4. `Njulf.Rendering/Core/SwapchainManager.cs` handles swapchain creation and recreation, and currently waits idle during recreation.
5. `RendererDiagnostics`, `SceneRenderingData`, and `SampleDiagnosticsReporter` already expose substantial runtime state.
6. `NjulfHelloGame/Program.cs` already supports `--smoke-frames` and performs one smoke resize after the first rendered frame.
7. `Njulf.Tests/ShaderBuildTests.cs` verifies required embedded SPIR-V shader resources.
8. `Njulf.Tests/ContentRendererIntegrationTests.cs` already checks some content load and missing glTF external buffer behavior.
9. `SampleReflectionTestSpheres.cs` provides deterministic reflective materials and named render objects that are useful as a stable visual and diagnostics fixture.
10. Several resource managers implement `IDisposable`, but there is no unified lifecycle leak audit yet.

The missing layer is a production hardening harness that exercises those paths repeatedly, records what happened, and makes failures diagnosable after a crash or CI run.

## Industry Standard Definition

Phase 20 is industry-standard when:

1. Startup, resize, fullscreen/windowed switching, minimize, restore, repeated scene reload, and shutdown are covered by deterministic smoke workflows.
2. Renderer initialization failures are logged with operation name, selected device candidate, required features, required extensions, Vulkan result, and last successful initialization step.
3. Unsupported hardware fails before partial renderer construction leaks resources.
4. Missing assets, missing textures, unsupported material features, and malformed content report asset paths and material/object names.
5. Runtime fallback behavior is explicit and visible in diagnostics, not hidden behind silent defaults.
6. Long-running sample sessions can detect CPU memory growth, GPU allocation growth, descriptor pressure, stale deletion queues, and bindless index exhaustion.
7. Validation layers and GPU-assisted validation can be enabled through environment variables or command-line flags without code changes.
8. CI builds the solution, runs CPU tests, validates shader embedding or shader compilation, and optionally runs a short graphics smoke job on a capable runner.
9. All hardening tools can run in developer builds without affecting shipping performance when disabled.
10. Final acceptance includes both automated tests and a manual GPU validation matrix.

## Non-Goals

Do not optimize every stall discovered in this phase. Report and classify stalls first; fix only hard correctness problems required for reliable shipping behavior.

Do not replace the renderer architecture or rewrite the frame graph.

Do not introduce a full editor UI. Command-line flags, environment variables, structured logs, and sample diagnostics are enough.

Do not require GPU-dependent tests in the default unit test suite. GPU smoke tests should be optional and clearly gated.

Do not hide content failures by falling back for everything. The policy must distinguish recoverable missing optional assets from unrecoverable malformed or contract-breaking content.

Do not make validation layers mandatory for normal runs. They are expensive and should be opt-in outside development defaults.

## Proposed Files

New renderer files:

1. `Njulf.Rendering/Diagnostics/RendererStartupLog.cs`
2. `Njulf.Rendering/Diagnostics/RendererStartupStep.cs`
3. `Njulf.Rendering/Diagnostics/RendererStartupStepStatus.cs`
4. `Njulf.Rendering/Diagnostics/RendererFailureReport.cs`
5. `Njulf.Rendering/Diagnostics/RendererValidationMode.cs`
6. `Njulf.Rendering/Diagnostics/RendererValidationSettings.cs`
7. `Njulf.Rendering/Diagnostics/RendererLifecycleDiagnostics.cs`
8. `Njulf.Rendering/Diagnostics/RendererResourceLeakSnapshot.cs`
9. `Njulf.Rendering/Diagnostics/RendererResourceLeakAuditor.cs`
10. `Njulf.Rendering/Diagnostics/DescriptorPressureSnapshot.cs`
11. `Njulf.Rendering/Diagnostics/SceneReloadDiagnostics.cs`
12. `Njulf.Rendering/Diagnostics/LongRunStabilityTracker.cs`
13. `Njulf.Rendering/Diagnostics/RendererHealthReportWriter.cs`
14. `Njulf.Rendering/Diagnostics/ContentFallbackPolicy.cs`
15. `Njulf.Rendering/Diagnostics/ContentFallbackEvent.cs`
16. `Njulf.Rendering/Diagnostics/ContentFallbackDiagnostics.cs`
17. `Njulf.Rendering/Diagnostics/DeviceRequirementReport.cs`
18. `Njulf.Rendering/Diagnostics/DeviceRequirementOverride.cs`

New sample/harness files:

1. `NjulfHelloGame/SampleSmokeMode.cs`
2. `NjulfHelloGame/SampleSmokeOptions.cs`
3. `NjulfHelloGame/SampleSmokeOptionsParser.cs`
4. `NjulfHelloGame/SampleLifecycleSmokeRunner.cs`
5. `NjulfHelloGame/SampleSceneReloadRunner.cs`
6. `NjulfHelloGame/SampleMissingAssetScenario.cs`
7. `NjulfHelloGame/SampleLongRunMonitor.cs`
8. `NjulfHelloGame/SampleHealthReportWriter.cs`

New test files:

1. `Njulf.Tests/RendererStartupLogTests.cs`
2. `Njulf.Tests/RendererValidationSettingsTests.cs`
3. `Njulf.Tests/RendererFailureReportTests.cs`
4. `Njulf.Tests/DeviceRequirementReportTests.cs`
5. `Njulf.Tests/ContentFallbackPolicyTests.cs`
6. `Njulf.Tests/ContentFallbackDiagnosticsTests.cs`
7. `Njulf.Tests/RendererResourceLeakAuditorTests.cs`
8. `Njulf.Tests/DescriptorPressureSnapshotTests.cs`
9. `Njulf.Tests/SampleSmokeOptionsParserTests.cs`
10. `Njulf.Tests/SampleLifecycleSmokeRunnerTests.cs`
11. `Njulf.Tests/SampleSceneReloadRunnerTests.cs`

New CI files if the repo adopts GitHub Actions:

1. `.github/workflows/dotnet.yml`
2. `.github/workflows/shaders.yml`
3. `.github/workflows/graphics-smoke.yml`

Existing files to update:

1. `Njulf.Core/Game.cs`
2. `Njulf.Core/Interfaces/IRenderer.cs`
3. `Njulf.Rendering/ServiceCollectionExtensions.cs`
4. `Njulf.Rendering/Core/VulkanContext.cs`
5. `Njulf.Rendering/Core/SwapchainManager.cs`
6. `Njulf.Rendering/Core/SynchronizationManager.cs`
7. `Njulf.Rendering/Descriptors/BindlessHeap.cs`
8. `Njulf.Rendering/Descriptors/SamplerManager.cs`
9. `Njulf.Rendering/Memory/BufferManager.cs`
10. `Njulf.Rendering/Memory/FenceBasedDeleter.cs`
11. `Njulf.Rendering/Memory/StagingRing.cs`
12. `Njulf.Rendering/VulkanRenderer.cs`
13. `Njulf.Rendering/Data/RendererDiagnostics.cs`
14. `Njulf.Rendering/Data/SceneRenderingData.cs`
15. `Njulf.Rendering/Resources/TextureManager.cs`
16. `Njulf.Rendering/Resources/MeshManager.cs`
17. `Njulf.Rendering/Resources/MaterialManager.cs`
18. `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
19. `Njulf.Assets/ContentManager.cs`
20. `Njulf.Assets/ModelImporter.cs`
21. `NjulfHelloGame/Program.cs`
22. `NjulfHelloGame/SampleSceneLoader.cs`
23. `NjulfHelloGame/SampleDiagnosticsReporter.cs`
24. `NjulfHelloGame/SampleReflectionTestSpheres.cs`
25. `Njulf.Tests/RendererDiagnosticsTests.cs`
26. `Njulf.Tests/ContentRendererIntegrationTests.cs`
27. `Njulf.Tests/ShaderBuildTests.cs`

## Runtime Switches

Add a small, documented set of environment variables and matching command-line flags for smoke and validation runs.

Environment variables:

1. `NJULF_RENDERER_VALIDATION`
   - `off`
   - `standard`
   - `gpu`
   - `sync`
   - `all`
2. `NJULF_RENDERER_STARTUP_LOG`
   - path to write startup and failure logs.
3. `NJULF_RENDERER_HEALTH_REPORT`
   - path to write final health report JSON.
4. `NJULF_RENDERER_SMOKE_MODE`
   - `none`
   - `startup`
   - `resize`
   - `fullscreen`
   - `minimize`
   - `scene-reload`
   - `missing-assets`
   - `long-run`
   - `all`
5. `NJULF_RENDERER_SMOKE_FRAMES`
   - positive integer frame count.
6. `NJULF_RENDERER_SCENE_RELOAD_COUNT`
   - positive integer reload count.
7. `NJULF_RENDERER_FORCE_MISSING_ASSETS`
   - enables controlled missing-asset scenarios.
8. `NJULF_RENDERER_FAIL_ON_VALIDATION_MESSAGE`
   - when supported, treats validation errors as smoke failures.
9. `NJULF_RENDERER_DEVICE_REQUIREMENT_OVERRIDE`
   - development-only feature rejection override for CPU-side failure-path tests.

Command-line flags for `NjulfHelloGame`:

1. `--smoke-frames <count>`
2. `--smoke-mode <mode>`
3. `--scene-reloads <count>`
4. `--health-report <path>`
5. `--startup-log <path>`
6. `--validation <off|standard|gpu|sync|all>`
7. `--force-missing-assets`
8. `--fail-on-validation-message`

Rules:

1. Command-line flags override environment variables.
2. Invalid values fail before renderer construction and print valid options.
3. Paths must be normalized to absolute paths in diagnostics.
4. Smoke modes must be deterministic enough for CI.
5. Validation settings must be visible in `RendererDiagnostics`.

## Crash-Safe Startup Logging

Add `RendererStartupLog` as an append-only text or JSON-lines writer. It must be safe to use during partial initialization and must not depend on the renderer being fully constructed.

Startup steps:

1. `Game.CreateWindow`
2. `Game.ConfigureServices`
3. `VulkanContext.CreateInstance`
4. `VulkanContext.ValidateInstanceLayers`
5. `VulkanContext.ValidateInstanceExtensions`
6. `VulkanContext.PickPhysicalDevice`
7. `VulkanContext.ValidateDeviceExtensions`
8. `VulkanContext.ValidateDeviceFeatures`
9. `VulkanContext.CreateLogicalDevice`
10. `VulkanContext.CreateAllocator`
11. `VulkanContext.LoadExtensions`
12. `VulkanContext.CreateSingleTimeCommandPool`
13. `VulkanContext.SetupDebugMessenger`
14. `VulkanRenderer.CreateManagers`
15. `VulkanRenderer.CreatePipelines`
16. `VulkanRenderer.InitializeRenderGraph`
17. `VulkanRenderer.RegisterBindlessResources`
18. `Content.LoadInitialScene`
19. `FirstFrame.Begin`
20. `FirstFrame.End`

Log entry shape:

```csharp
public sealed record RendererStartupStep(
    string Name,
    RendererStartupStepStatus Status,
    DateTimeOffset TimestampUtc,
    long ElapsedMicroseconds,
    string? Detail,
    string? ExceptionType,
    string? ExceptionMessage,
    string? VulkanResult);
```

Rules:

1. Write `Started`, `Succeeded`, or `Failed` for each step.
2. Flush after each failed step and after risky native calls.
3. Include process id, OS version, .NET version, executable path, working directory, command-line args, and relevant environment switches in the header.
4. Include selected physical device name, vendor id, device id, driver version, API version, queue families, and supported required features once known.
5. Do not write native handles unless needed for local debugging. Health reports should avoid raw handles by default.
6. Do not swallow exceptions. Log and rethrow.

Acceptance:

1. A failed missing validation layer run writes a startup log before throwing.
2. A failed missing device feature run writes the missing feature list.
3. A successful smoke run writes all major steps through first frame.

## Device Feature Rejection Paths

`VulkanContext` already gathers required features and extensions. Phase 20 should make failure reporting testable and complete.

Add `DeviceRequirementReport`:

```csharp
public sealed record DeviceRequirementReport(
    string DeviceName,
    uint VendorId,
    uint DeviceId,
    string ApiVersion,
    string DriverVersion,
    IReadOnlyList<string> MissingInstanceExtensions,
    IReadOnlyList<string> MissingInstanceLayers,
    IReadOnlyList<string> MissingDeviceExtensions,
    IReadOnlyList<string> MissingFeatures,
    IReadOnlyList<string> MissingQueueFamilies,
    bool IsSupported);
```

Required rejection messages:

1. No Vulkan physical devices found.
2. Vulkan API version below 1.3.
3. Missing required instance extension.
4. Missing validation layer when validation is explicitly requested.
5. Missing device extension:
   - `VK_KHR_swapchain`
   - `VK_KHR_dynamic_rendering`
   - `VK_KHR_synchronization2`
   - `VK_EXT_mesh_shader`
   - `VK_KHR_buffer_device_address`
   - `VK_EXT_descriptor_indexing`
   - `VK_KHR_deferred_host_operations`
6. Missing feature:
   - mesh shader.
   - task shader.
   - buffer device address.
   - descriptor indexing features used by bindless resources.
   - synchronization2.
   - dynamic rendering.
7. Missing graphics queue.
8. Missing present support.
9. Missing compatible surface format or present mode.

Testing approach:

1. Extract pure requirement formatting into CPU-testable helpers.
2. Add development-only `DeviceRequirementOverride` to simulate missing extensions/features without needing unsupported hardware.
3. Keep actual Vulkan enumeration behind integration or manual smoke tests.

Acceptance:

1. Every missing requirement appears as a separate named item.
2. Exception text is short enough for console output but points to the full startup log.
3. Tests cover formatting and aggregation without requiring a GPU.

## Validation Modes

Add `RendererValidationSettings`:

```csharp
public enum RendererValidationMode
{
    Off,
    Standard,
    GpuAssisted,
    Synchronization,
    All
}

public sealed record RendererValidationSettings(
    RendererValidationMode Mode,
    bool FailOnErrorMessage,
    bool EnableBestPractices,
    bool EnableVerboseMessages,
    string? StartupLogPath,
    string? HealthReportPath);
```

Implementation notes:

1. Keep `EnableValidation` as a compatibility path but route it through `RendererValidationSettings`.
2. Use `VK_EXT_validation_features` if available for GPU-assisted and synchronization validation.
3. If requested validation features are not supported, fail clearly when explicitly requested.
4. If validation is automatic development default and unavailable, follow the existing policy or log a clear warning, depending on the selected mode.
5. Capture validation message severity counts in diagnostics:
   - verbose.
   - info.
   - warning.
   - error.
6. If `FailOnErrorMessage` is enabled, mark the smoke run failed after the frame or immediately when safe.

Acceptance:

1. `NJULF_RENDERER_VALIDATION=off` disables validation.
2. `NJULF_RENDERER_VALIDATION=standard` enables `VK_LAYER_KHRONOS_validation`.
3. Invalid validation mode values fail before renderer construction.
4. Validation message counts are visible in diagnostics and health reports.

## Lifecycle Smoke Harness

Extend the sample smoke workflow into explicit modes rather than a single hard-coded resize.

`SampleSmokeOptions`:

```csharp
public sealed record SampleSmokeOptions(
    SampleSmokeMode Mode,
    int FrameCount,
    int SceneReloadCount,
    string? StartupLogPath,
    string? HealthReportPath,
    RendererValidationMode ValidationMode,
    bool FailOnValidationMessage,
    bool ForceMissingAssets);
```

Smoke modes:

1. `Startup`
   - initialize renderer.
   - load sample scene.
   - render one frame.
   - shut down.
2. `Resize`
   - render at initial size.
   - resize to 1280x720.
   - resize to 1920x1080.
   - resize to 800x600.
   - render after each resize.
3. `Fullscreen`
   - switch windowed to fullscreen and back if Silk.NET backend supports it.
   - if backend cannot support it in CI, record skipped with reason.
4. `Minimize`
   - simulate or trigger zero-sized framebuffer.
   - verify renderer does not recreate invalid zero-sized swapchain.
   - restore to valid size.
5. `SceneReload`
   - clear and dispose scene content.
   - reload the sample scene.
   - repeat configured count.
   - render after each reload.
6. `MissingAssets`
   - run controlled missing texture, missing buffer, missing environment, and missing model scenarios.
7. `LongRun`
   - render for configured frame count.
   - sample memory and descriptor pressure periodically.
8. `All`
   - run startup, resize, minimize, scene reload, missing assets, and a short long-run pass.

Rules:

1. Smoke runner must produce a `RendererHealthReport`.
2. Every skipped operation must include a skip reason, not silently pass.
3. Smoke failures must exit with non-zero process code.
4. Smoke mode should not depend on interactive keyboard input.
5. The sample should keep `SampleReflectionTestSpheres` enabled during smoke runs so reflective material paths, probe sampling, and named material diagnostics remain exercised.

Acceptance:

1. `dotnet run --project NjulfHelloGame -- --smoke-mode resize --smoke-frames 6` performs multiple resize operations and exits.
2. `--smoke-mode scene-reload --scene-reloads 10` reloads without growing resource counts beyond defined tolerances.
3. `--smoke-mode minimize` validates zero-sized resize handling.
4. Health report records pass, fail, or skipped per operation.

## Scene Reload And Resource Lifetime

Repeated load/unload is a core shipping risk because it exercises disposal, deferred deletion, descriptor reuse, bindless slots, and manager cache behavior.

Add `SceneReloadDiagnostics`:

```csharp
public sealed record SceneReloadDiagnostics(
    int ReloadIndex,
    int RenderObjectCountBefore,
    int RenderObjectCountAfter,
    int MeshCountBefore,
    int MeshCountAfter,
    int MaterialCountBefore,
    int MaterialCountAfter,
    int TextureCountBefore,
    int TextureCountAfter,
    int DescriptorWritesBefore,
    int DescriptorWritesAfter,
    ulong GpuBytesBefore,
    ulong GpuBytesAfter,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    int PendingDeletionCountAfter);
```

Required behavior:

1. Scene-owned render objects and models dispose exactly once.
2. Repeated `Scene.ClearAndDispose()` does not double-dispose.
3. Reloading the same asset should reuse cached immutable resources where intended.
4. Resources intended to be freed must leave manager counts after fence retirement.
5. Deferred deletion queues must be drained by shutdown.
6. Bindless slots should either be reused or reported as monotonically consumed if the design chooses not to recycle yet.

Resource leak auditor:

1. Snapshot counts before reload.
2. Snapshot counts after reload.
3. Wait enough frames for fence-based deletion to retire.
4. Snapshot again after retirement.
5. Compare against tolerances.

Suggested tolerances:

1. Managed memory after 10 reloads: no sustained growth above 10 percent after forced GC in smoke mode.
2. GPU tracked bytes: stable within one frame's deferred deletion tolerance.
3. Descriptor writes: may grow as resources are rebound, but active descriptor pressure must stay under configured capacity.
4. Pending deletion count: returns to zero after shutdown.

Acceptance:

1. Scene reload smoke reports before/after counts.
2. Known intentionally cached resources are labeled as cached, not leaks.
3. Resource leak failures name the category and manager.

## Missing Asset And Fallback Policy

Shipping hardening requires a clear policy for content failures. Some failures can fall back; others should fail loudly.

Add `ContentFallbackPolicy`:

```csharp
public enum ContentFallbackSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ContentFallbackEvent(
    string AssetPath,
    string? MaterialName,
    string? ObjectName,
    string FallbackKind,
    ContentFallbackSeverity Severity,
    string Message);
```

Recoverable fallback cases:

1. Missing optional albedo texture:
   - use default white texture.
   - warning includes material name and texture path.
2. Missing optional normal texture:
   - use default normal texture.
   - warning includes material name and texture path.
3. Missing optional metallic-roughness-AO texture:
   - use default black or default scalar material values according to existing convention.
   - warning includes material name and texture path.
4. Missing optional emissive texture:
   - use default black texture.
   - warning includes material name and texture path.
5. Missing environment map when procedural fallback is configured:
   - use procedural environment or fallback sky.
   - warning includes environment path.
6. Unsupported optional glTF extension:
   - import asset without extension behavior if safe.
   - warning lists extension name.

Unrecoverable cases:

1. Missing model file requested directly by game code.
2. Missing external glTF buffer required for geometry.
3. Malformed vertex/index data.
4. Invalid material data that would break shader contracts.
5. Unsupported required glTF extension.
6. Texture decode failure when no fallback is allowed by policy.
7. Shader resource missing from build output.

Diagnostics:

1. Add fallback event count.
2. Add fallback count by kind.
3. Add last fallback asset path.
4. Add last fallback material name.
5. Add highest fallback severity this session.

Acceptance:

1. Existing default texture substitution counts continue to work.
2. Missing texture fallback reports path and material name.
3. Missing external glTF buffer remains a clear failure with absolute path.
4. Missing asset smoke mode covers each policy branch.

## Descriptor And Bindless Exhaustion

Bindless and descriptor pressure must fail clearly before random rendering corruption.

Add `DescriptorPressureSnapshot`:

```csharp
public sealed record DescriptorPressureSnapshot(
    int StorageDescriptorCapacity,
    int StorageDescriptorUsed,
    int TextureDescriptorCapacity,
    int TextureDescriptorUsed,
    int SamplerCount,
    int BindlessTextureHighWatermark,
    int BindlessStorageHighWatermark,
    int DescriptorWriteCount,
    int DescriptorAllocationFailureCount);
```

Required checks:

1. `BindlessHeap` reports capacity and high-water marks.
2. Descriptor allocation failures throw `VulkanException` with pool name, requested count, capacity, and current use.
3. Texture manager reports bindless texture slot use.
4. Renderer diagnostics include descriptor pressure.
5. Long-run smoke watches high-water marks for unbounded growth.

Acceptance:

1. Descriptor pressure is visible in `RendererDiagnostics`.
2. Artificial exhaustion tests can exercise formatting without allocating a huge Vulkan pool.
3. Real exhaustion paths name the descriptor pool and resource kind.

## Long-Running Session Stability

Add `LongRunStabilityTracker` and sample monitor output.

Metrics:

1. Frame count.
2. Elapsed wall-clock time.
3. Managed memory bytes.
4. Gen0/Gen1/Gen2 collection counts.
5. Native/GPU tracked bytes if Phase 19 tracking is available.
6. Active buffer count.
7. Active image count.
8. Active sampler count.
9. Active render target count.
10. Active descriptor usage.
11. Pending deferred deletion count.
12. Staging ring high-water mark.
13. Validation warning and error counts.
14. Last renderer exception or validation failure.

Sampling:

1. Capture every 300 frames by default.
2. Capture before and after every resize or scene reload.
3. Capture final shutdown report.
4. Keep rolling baseline from first stable sample after warm-up.

Failure policy:

1. Sustained managed memory growth above threshold fails smoke.
2. GPU tracked bytes growth above threshold fails smoke unless tied to intentional asset load.
3. Descriptor high-water growth during static scene long run fails smoke.
4. Pending deletion count that never drains after idle frames fails smoke.
5. Validation errors fail smoke when `FailOnValidationMessage` is enabled.

Acceptance:

1. A 1,000-frame smoke run writes periodic stability samples.
2. Static scene descriptor usage does not grow after warm-up.
3. Shutdown report indicates whether deferred deletion queues drained.

## Health Report

Add a final JSON health report for smoke and manual validation runs.

Top-level shape:

```csharp
public sealed record RendererHealthReport(
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    bool Passed,
    string Mode,
    string? FailureReason,
    RendererValidationSettings Validation,
    IReadOnlyList<RendererStartupStep> StartupSteps,
    IReadOnlyList<SmokeOperationResult> Operations,
    RendererDiagnostics FinalDiagnostics,
    RendererResourceLeakSnapshot? FinalResourceSnapshot,
    DescriptorPressureSnapshot? FinalDescriptorPressure,
    IReadOnlyList<ContentFallbackEvent> FallbackEvents);
```

Rules:

1. Write report on success and failure.
2. Avoid raw native handles.
3. Include absolute asset paths only where they help diagnose content failures.
4. Include selected device and driver metadata.
5. Include command line and environment switches.
6. Include smoke skip reasons.
7. Keep JSON stable enough for CI artifacts.

Acceptance:

1. Health report writes when smoke succeeds.
2. Health report writes when startup fails after log initialization.
3. CI can upload the report as an artifact.

## Implementation Slices

### Slice 20.1: Startup Log And Validation Settings

Goal: make renderer startup and failure state observable.

Tasks:

1. Add `RendererValidationMode`.
2. Add `RendererValidationSettings`.
3. Parse validation mode from environment variables.
4. Add command-line override parsing in `NjulfHelloGame`.
5. Add `RendererStartupLog`.
6. Add `RendererStartupStep` and status enum.
7. Add startup log hooks around `Game.Initialize`, `VulkanContext` construction, renderer initialization, sample scene load, and first frame.
8. Include device metadata once a device is selected.
9. Update `VulkanContext` validation setup to use the new settings.
10. Add tests for parsing and log step ordering.

Acceptance:

1. Invalid validation mode is rejected before renderer construction.
2. Startup log records success through first frame.
3. Startup failures write the failed step.
4. Existing `dotnet test Njulf.sln` passes.

### Slice 20.2: Device Requirement Reports

Goal: make unsupported hardware failure paths actionable and testable.

Tasks:

1. Add `DeviceRequirementReport`.
2. Split device requirement checks from exception formatting.
3. Add explicit lists for missing layers, instance extensions, device extensions, features, and queue support.
4. Add development-only requirement override for tests and smoke mode.
5. Add failure report serialization.
6. Update `VulkanException` messages to point to full startup log when available.
7. Add CPU tests for missing extension, missing feature, missing queue, and unsupported API version formatting.

Acceptance:

1. Missing requirement messages are named and grouped.
2. Requirement tests do not need a GPU.
3. Runtime unsupported device failure includes selected candidates where available.

### Slice 20.3: Lifecycle Smoke Modes

Goal: cover lifecycle operations from the sample app without manual input.

Tasks:

1. Add `SampleSmokeMode`.
2. Add `SampleSmokeOptions`.
3. Add `SampleSmokeOptionsParser`.
4. Replace one-off smoke resize logic with `SampleLifecycleSmokeRunner`.
5. Implement startup smoke.
6. Implement resize smoke.
7. Implement minimize/zero-sized framebuffer smoke.
8. Implement fullscreen/windowed smoke with capability skip reason.
9. Emit operation results to console and health report.
10. Add parser and runner unit tests for deterministic operation scheduling.

Acceptance:

1. Existing `--smoke-frames` still works.
2. `--smoke-mode resize` performs deterministic resize sequence.
3. Zero-sized resize is ignored safely and restore renders correctly.
4. Fullscreen smoke skips cleanly if backend support is unavailable.

### Slice 20.4: Scene Reload And Leak Auditing

Goal: prove repeated load/unload does not leak obvious resources.

Tasks:

1. Add `RendererResourceLeakSnapshot`.
2. Add `RendererResourceLeakAuditor`.
3. Add manager count/high-water APIs where missing.
4. Add `SceneReloadDiagnostics`.
5. Add `SampleSceneReloadRunner`.
6. Reload the sample scene with `SampleReflectionTestSpheres` included each cycle.
7. Wait enough frames for fence-based deletion to retire.
8. Force GC only in smoke mode after scene disposal.
9. Compare counts against configured tolerances.
10. Add tests for leak snapshot comparison and tolerance behavior.

Acceptance:

1. `--smoke-mode scene-reload --scene-reloads 10` completes or reports exact leak category.
2. Pending deletions drain after idle frames or are reported.
3. Cached resources are identified as cache retention, not leaks.

### Slice 20.5: Missing Asset Policy And Diagnostics

Goal: make content failures deterministic, visible, and useful to artists/programmers.

Tasks:

1. Add `ContentFallbackPolicy`.
2. Add `ContentFallbackEvent`.
3. Add `ContentFallbackDiagnostics`.
4. Wire texture fallback events in `ModelRenderUploadService` and `TextureManager`.
5. Wire environment fallback events in `EnvironmentManager` if environment loading is present.
6. Update `ContentManager` and `ModelImporter` errors to include absolute path and asset kind.
7. Add controlled missing-asset sample scenarios.
8. Add tests for missing optional texture fallback.
9. Add tests for missing required glTF buffer failure.
10. Add diagnostics output in `SampleDiagnosticsReporter`.

Acceptance:

1. Missing optional texture reports fallback event with material name.
2. Missing required geometry buffer fails with absolute path.
3. Health report includes fallback events.
4. Existing model import tests still pass.

### Slice 20.6: Descriptor Pressure And Exhaustion Reporting

Goal: catch descriptor and bindless exhaustion before it causes undefined rendering behavior.

Tasks:

1. Add `DescriptorPressureSnapshot`.
2. Add capacity and high-water reporting to `BindlessHeap`.
3. Add bindless texture high-water reporting to `TextureManager`.
4. Add sampler count reporting to `SamplerManager`.
5. Improve descriptor allocation failure messages.
6. Add diagnostics fields to `RendererDiagnostics.Empty`.
7. Add tests for snapshot aggregation and failure message formatting.
8. Add long-run checks for descriptor growth in static scenes.

Acceptance:

1. Descriptor capacity and use are visible in diagnostics.
2. Artificial capacity failures produce actionable messages.
3. Long-run static scene does not show unbounded descriptor growth.

### Slice 20.7: Long-Run Stability Tracking

Goal: expose memory growth, validation errors, and deferred deletion problems over time.

Tasks:

1. Add `LongRunStabilityTracker`.
2. Add `SampleLongRunMonitor`.
3. Capture managed memory and GC collection counts.
4. Capture renderer resource snapshots.
5. Capture descriptor pressure snapshots.
6. Capture validation message counts.
7. Capture deferred deletion count.
8. Write periodic samples to console in smoke mode.
9. Add final stability summary to health report.
10. Add tests for baseline comparison and threshold classification.

Acceptance:

1. `--smoke-mode long-run --smoke-frames 1000` writes periodic samples.
2. Static scene resource counts stabilize after warm-up.
3. Final report flags sustained growth beyond tolerance.

### Slice 20.8: Health Report Writer

Goal: produce a durable artifact for local runs and CI.

Tasks:

1. Add `RendererHealthReportWriter`.
2. Add sample-facing `SampleHealthReportWriter`.
3. Write report on success.
4. Write report on smoke failure.
5. Write partial report on startup failure when possible.
6. Include startup log path and selected device metadata.
7. Include smoke operations, diagnostics, fallback events, leak snapshots, descriptor pressure, and validation counts.
8. Add tests for serialization shape and no-native-handle policy.

Acceptance:

1. Health report writes valid JSON.
2. Health report contains enough data to diagnose failed smoke runs.
3. Report writer failure does not hide original renderer failure.

### Slice 20.9: CI Gates

Goal: make renderer hardening repeatable outside a local developer machine.

Tasks:

1. Add build workflow:
   - `dotnet restore Njulf.sln`
   - `dotnet build Njulf.sln --configuration Release --no-restore`
2. Add test workflow:
   - `dotnet test Njulf.sln --configuration Release --no-build`
3. Add shader workflow:
   - run existing shader build path if present.
   - run `ShaderBuildTests`.
   - optionally verify no stale SPIR-V resources.
4. Add formatting/static analysis workflow if adopted:
   - `dotnet format --verify-no-changes`
   - nullable/analyzer warnings as errors only after the repo is ready.
5. Add optional graphics smoke workflow:
   - gated by runner label or manual dispatch.
   - runs `NjulfHelloGame --smoke-mode startup --smoke-frames 3 --validation standard`.
   - uploads startup log and health report.
6. Document local equivalents in the plan and future docs.

Acceptance:

1. CPU-only CI works on a standard hosted runner.
2. Graphics smoke is optional and clearly skipped when no Vulkan-capable runner exists.
3. CI artifacts include health reports for smoke jobs.

### Slice 20.10: Final Validation And Release Checklist

Goal: finish Phase 20 with a documented production readiness pass.

Tasks:

1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run startup smoke with validation off.
4. Run startup smoke with standard validation.
5. Run resize smoke with standard validation.
6. Run minimize smoke.
7. Run scene reload smoke for at least 25 reloads.
8. Run missing asset smoke.
9. Run long-run smoke for at least 10,000 frames locally on a Vulkan-capable machine.
10. Capture RenderDoc frame after resize and after scene reload.
11. Confirm shutdown is validation-clean.
12. Confirm health reports are written for all smoke modes.
13. Update this plan with final notes or move completed details into project docs.

Acceptance:

1. All automated tests pass.
2. Standard validation smoke produces no validation errors.
3. Long-run smoke shows stable resource counts after warm-up.
4. Unsupported hardware and missing content failures are actionable.
5. CI gates are active or documented as intentionally deferred.

## Automated Tests

Add or update tests:

1. `RendererValidationSettings_ParsesOffStandardGpuSyncAndAll`
2. `RendererValidationSettings_InvalidValueThrowsBeforeRendererConstruction`
3. `RendererStartupLog_WritesStartedSucceededAndFailedSteps`
4. `RendererStartupLog_FlushesFailedStep`
5. `RendererFailureReport_ContainsLastSuccessfulStep`
6. `DeviceRequirementReport_GroupsMissingExtensionsAndFeatures`
7. `DeviceRequirementReport_FormatsUnsupportedApiVersion`
8. `DeviceRequirementReport_FormatsMissingPresentQueue`
9. `SampleSmokeOptionsParser_CommandLineOverridesEnvironment`
10. `SampleSmokeOptionsParser_RejectsInvalidMode`
11. `SampleLifecycleSmokeRunner_SchedulesResizeSequence`
12. `SampleLifecycleSmokeRunner_ZeroSizeResizeIsSkippedOrIgnored`
13. `RendererResourceLeakAuditor_WithinTolerancePasses`
14. `RendererResourceLeakAuditor_OverToleranceFailsWithCategory`
15. `RendererResourceLeakSnapshot_TotalEqualsCategorySum`
16. `ContentFallbackPolicy_MissingOptionalAlbedoUsesDefaultWhite`
17. `ContentFallbackPolicy_MissingRequiredGeometryFails`
18. `ContentFallbackDiagnostics_RecordsAssetPathAndMaterialName`
19. `DescriptorPressureSnapshot_ComputesUsageRatios`
20. `DescriptorPressureSnapshot_FormatsExhaustionFailure`
21. `LongRunStabilityTracker_EstablishesWarmupBaseline`
22. `LongRunStabilityTracker_FlagsSustainedManagedMemoryGrowth`
23. `LongRunStabilityTracker_FlagsDescriptorGrowth`
24. `RendererHealthReportWriter_WritesValidJson`
25. `RendererHealthReportWriter_DoesNotSerializeNativeHandles`
26. `ShaderBuildTests_RequiredShadersAreEmbeddedAsSpirv`
27. `ContentRendererIntegrationTests_MissingExternalBufferReportsAbsolutePath`
28. `SampleReflectionTestSpheres_CreatesNamedObjectsForSmokeFixture`

Avoid GPU-dependent assertions in the default unit tests. Use pure formatting, parsing, aggregation, and policy tests for CI portability.

## Manual GPU Validation Matrix

Run these on a Vulkan-capable development machine.

Baseline:

1. `dotnet build Njulf.sln`
2. `dotnet test Njulf.sln`
3. `dotnet run --project NjulfHelloGame -- --smoke-mode startup --smoke-frames 3 --validation off`
4. `dotnet run --project NjulfHelloGame -- --smoke-mode startup --smoke-frames 3 --validation standard`

Lifecycle:

1. `dotnet run --project NjulfHelloGame -- --smoke-mode resize --smoke-frames 12 --validation standard`
2. `dotnet run --project NjulfHelloGame -- --smoke-mode minimize --smoke-frames 12 --validation standard`
3. `dotnet run --project NjulfHelloGame -- --smoke-mode fullscreen --smoke-frames 12 --validation standard`
4. Confirm every mode writes a health report.

Scene reload:

1. `dotnet run --project NjulfHelloGame -- --smoke-mode scene-reload --scene-reloads 25 --smoke-frames 100 --validation standard`
2. Confirm material, mesh, texture, descriptor, and pending deletion counts stabilize.
3. Confirm `SampleReflectionTestSpheres` objects are present after each reload.

Missing assets:

1. `dotnet run --project NjulfHelloGame -- --smoke-mode missing-assets --force-missing-assets --validation standard`
2. Confirm optional texture fallback events include asset path and material name.
3. Confirm required missing geometry fails with an actionable message.

Long run:

1. `dotnet run --project NjulfHelloGame -- --smoke-mode long-run --smoke-frames 10000 --validation standard`
2. Confirm descriptor usage stabilizes after warm-up.
3. Confirm managed memory does not grow beyond tolerance after forced smoke checkpoints.
4. Confirm no validation errors.

RenderDoc:

1. Capture first frame after startup.
2. Capture frame after resize.
3. Capture frame after scene reload.
4. Capture frame with reflection test spheres visible.
5. Verify pass labels, render targets, and descriptor sets are valid.

Shutdown:

1. Close window normally.
2. Exit from smoke mode.
3. Exit immediately after first frame.
4. Confirm no validation errors during teardown.
5. Confirm health report marks shutdown complete.

## CI Plan

Recommended initial CI gates:

1. Build:
   - `dotnet restore Njulf.sln`
   - `dotnet build Njulf.sln --configuration Release --no-restore`
2. Tests:
   - `dotnet test Njulf.sln --configuration Release --no-build --logger trx`
3. Shader resources:
   - run `ShaderBuildTests`.
   - optionally run the shader compile project if it has a deterministic command.
4. Formatting/static analysis:
   - add `dotnet format --verify-no-changes` only after existing formatting is clean.
   - enable warnings-as-errors only for projects that are already clean.
5. Optional graphics smoke:
   - manual dispatch or self-hosted Vulkan runner.
   - `dotnet run --project NjulfHelloGame -c Release -- --smoke-mode startup --smoke-frames 3 --validation standard --health-report artifacts/renderer-health.json --startup-log artifacts/renderer-startup.jsonl`

CI artifacts:

1. test results.
2. startup logs.
3. health reports.
4. sample console output.
5. RenderDoc captures only for manual jobs, not every CI run.

Failure policy:

1. CPU tests are required.
2. Shader resource tests are required.
3. Formatting is required only once adopted.
4. Graphics smoke is optional until a Vulkan-capable runner exists.
5. Validation errors fail graphics smoke when `--fail-on-validation-message` is enabled.

## Logging And Diagnostics Policy

Renderer diagnostics should separate:

1. startup failures.
2. content fallback warnings.
3. runtime validation messages.
4. performance budget warnings from Phase 19.
5. lifecycle smoke operation failures.
6. resource leak findings.
7. descriptor pressure findings.

Rules:

1. Console output should be concise.
2. Startup log and health report should contain full detail.
3. Exceptions should include the immediate cause and log path.
4. Asset-related messages must include path and material/object name when known.
5. Device-related messages must include missing requirement names.
6. Smoke failures must include operation name and frame/reload index.

## Performance Overhead Budget

Hardening code must be cheap when disabled.

Targets:

1. Startup logging: only active during startup and explicit smoke runs.
2. Validation message counting: negligible in validation mode, disabled in normal shipping mode.
3. Descriptor pressure snapshot: less than 0.05 ms when sampled every 300 frames.
4. Resource leak snapshot: smoke/debug only.
5. Long-run stability sampling: less than 0.1 ms at sample interval.
6. Fallback diagnostics: append event only when fallback happens.
7. Shipping default: no per-frame allocations from hardening systems.

Implementation rules:

1. Avoid per-frame string formatting except reporter paths.
2. Keep health report construction outside hot render pass code.
3. Use reusable buffers or snapshots for smoke monitors.
4. Do not call `GC.Collect()` outside explicit smoke validation.
5. Do not add GPU waits for diagnostics outside explicit smoke/lifecycle checkpoints.

## Failure Handling

1. Startup log path invalid:
   - fall back to console/debug output.
   - include warning in health report if possible.
2. Health report path invalid:
   - write warning to console.
   - do not hide original smoke result.
3. Validation layer requested but unavailable:
   - fail clearly for explicit validation modes.
4. GPU-assisted validation requested but unsupported:
   - fail clearly or downgrade only if mode allows downgrade.
5. Fullscreen smoke unsupported:
   - mark operation skipped with reason.
6. Minimize simulation unavailable:
   - call renderer resize with zero dimensions directly and mark OS minimize skipped.
7. Scene reload leak detected:
   - fail smoke with manager/category and before/after counts.
8. Missing optional asset:
   - apply fallback and record warning.
9. Missing required asset:
   - throw with path and content kind.
10. Descriptor exhaustion:
   - throw with descriptor pool, capacity, requested count, and active usage.
11. Long-run growth:
   - fail smoke only after sustained samples exceed threshold.

## Documentation Requirements

Add final documentation in this plan or a future `Docs` file:

1. How to run smoke modes locally.
2. How to enable validation modes.
3. How to read startup logs.
4. How to read health reports.
5. What fallback events mean.
6. What resource leak categories mean.
7. What descriptor pressure fields mean.
8. Which CI gates are required and which are optional.
9. Known limitations:
   - hosted CI may not support Vulkan.
   - GPU memory tracking depends on Phase 19 allocation tracking for exact categories.
   - fullscreen/minimize behavior can vary by platform and window backend.
   - validation layers can produce driver-specific noise.

## Implementation Order

1. Add validation settings and startup logging.
2. Add device requirement reports and failure formatting.
3. Replace the existing one-off smoke resize with general smoke options and lifecycle runner.
4. Add health report writing.
5. Add scene reload smoke and resource leak snapshots.
6. Add missing asset policy and fallback diagnostics.
7. Add descriptor pressure snapshots.
8. Add long-run stability tracking.
9. Add CI build/test/shader workflows.
10. Run final manual GPU validation matrix and document results.

This order gives useful crash logs and smoke artifacts early, then broadens coverage to content and long-session stability.

## Final Acceptance Criteria

Phase 20 is complete when:

1. Startup, resize, minimize/restore, fullscreen/windowed where supported, scene reload, long-run, missing asset, and shutdown workflows are covered by smoke modes.
2. Renderer startup writes crash-safe logs for successful and failed initialization.
3. Unsupported hardware failures list missing requirements clearly.
4. Missing optional assets produce fallback events with asset path and material/object context.
5. Missing required assets fail with actionable exceptions.
6. Repeated scene reloads do not leak unmanaged renderer resources beyond documented cache retention.
7. Long-running static scenes show stable descriptor usage and resource counts after warm-up.
8. Validation mode is controlled by environment variable or command-line flag.
9. Validation message counts are visible in diagnostics and health reports.
10. Descriptor and bindless pressure are visible and exhaustion failures are actionable.
11. Health reports are written for smoke success, smoke failure, and startup failure where possible.
12. `SampleReflectionTestSpheres` remains part of the sample smoke scene and acts as a stable reflective material fixture.
13. CI builds the solution, runs CPU tests, and validates shader resources.
14. Optional graphics smoke is available for Vulkan-capable runners.
15. `dotnet build Njulf.sln` and `dotnet test Njulf.sln` pass.
16. Manual standard-validation smoke runs are validation-clean for startup, resize, scene reload, long-run, and shutdown.
