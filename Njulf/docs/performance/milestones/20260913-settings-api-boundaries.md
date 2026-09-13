# Settings, rendering state, API boundaries and native input

- Date: 2026-09-13. Revision: `09be7c7d`; workspace already contained ongoing API changes, preserved throughout.
- Hardware: Windows; AMD Ryzen 5 5600H; NVIDIA GeForce RTX 3060 Laptop GPU.
- Decision: retain points 18–22, including the source migration from `Njulf.Core.Game` to `Njulf.Framework.Game`.
- Implementation: common runtime settings remain on `GraphicsDevice.Settings`; startup uses `ConfigureRendering`.
  Shadow tiers reuse existing values, with overall preset → shadow preset → explicit override order.
  Settings declarations are grouped by domain; depth comparison is separate from depth read/write policy.
  Native input is explicit through `INativeInputIntegration`, including the editor bridge.
- Visibility: 305 declarations internalized across renderer execution, GPU-only layouts, managers, diagnostic
  builders and one cooker ABI helper. Supported service/diagnostic signatures retain their dependencies;
  snapshot readers/writers, cooker entry points and Vulkan extension contracts remain public.
  Renderer constructors are internal; the existing `AddRendering` factory already uses internal construction.
  [Compact declaration audit](20260913-settings-api-boundaries.json).

## Validation

- Development solution build passed, using the workspace FFmpeg executable for the existing audio example.
- 72 focused settings/input/shadow-persistence/GPU-layout cases passed, none skipped.
- One 16-second validation-enabled Vulkan/editor smoke passed: hidden 128×128 host, editor overlay,
  deterministic keyboard/text/mouse callbacks through the Silk-backed manager and advanced adapter,
  runtime Low → Ultra shadow transition, completed resource-preparation receipt, and clean shutdown.
  Vulkan validation errors: **0**. The fixture also connects the real host input to the editor adapter.
- Final settings/editor check: 23 cases passed, none skipped, after the editor preset selector and hash reuse change.
- An initial focused test caught missing shared assignments in the extracted High shadow tier. The extraction
  was corrected against the pre-task source; all tier cases then passed. Failed build attempts exposed
  visibility dependencies and a missing FFmpeg path; the final build resolved both.
- CPU/GPU field order, packing and numeric values were unchanged. Settings schema remains version 28.
  No performance measurements or tail-latency comparison: no performance change is claimed.

## Reproduction and limits

```powershell
$apiFfmpeg = (Resolve-Path '.codex-tmp/audio-tools/ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe').Path
dotnet build Njulf.sln -c Development --no-restore "-p:NjulfFFmpeg=$apiFfmpeg"
dotnet test Njulf.Tests -c Development --no-build --no-restore --filter 'FullyQualifiedName~GraphicsSettingsControllerTests|FullyQualifiedName~InputManagerTests|FullyQualifiedName~InputGameplayTests|FullyQualifiedName~InputRebindingTests|FullyQualifiedName~SaveLoad_PreservesCompleteDirectionalShadowContract|FullyQualifiedName~Load_Version10ShadowSwitchRetainsLegacyDirectionalModes|FullyQualifiedName~Load_Version12ShadowObjectMigratesNewControlsToLegacyParity|FullyQualifiedName~QualityPresets_UseAdaptiveTentDirectionalFilteringAboveLow|FullyQualifiedName~GPUStructLayoutTests|FullyQualifiedName~RenderingSettingsEditorPanelTests|FullyQualifiedName~SettingsAndInputSmokeTests' --results-directory .codex-tmp/api-results
```

Any installed FFmpeg executable can replace the temporary path. The smoke uses injected native callbacks,
not physical keyboard/mouse automation; it verifies integration and resource lifecycle, not image quality.
No full test suite, benchmark campaign or cross-platform matrix was run.

Artifact inspection via `tools/prune-local-artifacts.ps1` found 0.33 GiB of older candidates and 256.16 GiB
free on D:. This task created compact logs/TRX and normal build outputs, with no captures or isolated build
copies. Older outputs belong to other investigations and were retained. Detailed local logs are under
`.codex-tmp/api-*`; this record preserves their relevant conclusions.

Migration details: [settings and API migration](../../ApiMigration-SettingsAndBoundaries.md).
