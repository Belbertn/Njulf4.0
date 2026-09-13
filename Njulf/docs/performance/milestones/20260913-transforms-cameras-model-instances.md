# Transform, camera, and model-instance conveniences — 2026-09-13

- Source: `09be7c7d`, dirty working tree with pre-existing gameplay, content, audio, and physics work preserved. This change adds explicit node transforms, camera switching/controllers, and Assets model instantiation; no renderer ownership refactor.
- Hardware: AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU, driver 610.62; Windows, Development configuration.
- Decision: accept the API additions and focused correctness evidence. No blending, camera stack, input abstraction, or performance optimization added.

## Validation

- Initial filtered suite: **125 passed, 0 failed/skipped**, covering transforms/controllers, existing camera behavior, content scopes, editor transforms, physics ownership, scene membership, and model lifetime. [TRX](../../../artifacts/api-conveniences/test-results/scene-camera-content.trx).
- Extended attachment rollback check: **3 passed**, covering a disposed scene, failure during primitive addition, and failure during transform-group addition. Tests observe scene collections and retained resource counts. [TRX](../../../artifacts/api-conveniences/test-results/model-attachment.trx).
- API example build succeeded with **0 warnings/errors** using the already available local FFmpeg executable for the unrelated audio-content build target.
- Final camera smoke: **120 full-quality frames**, gameplay follow → paused menu orbit → scripted cutscene → restored gameplay, **3 verified camera cuts**, active aspect ratio/listener propagation verified, **0 Vulkan validation errors**, process exit 0. [Log](../../../artifacts/api-conveniences-camera-final.log).
- Initial 150-frame smoke also rendered all stages with zero Vulkan errors and produced an inspected 800×600 capture. Its post-run validator incorrectly accessed the disposed host; fixed to inspect the last rendered camera and reran successfully. A test-only `SceneMutation.Source` typo was corrected to `Producer` before the final rollback run.

## Timing and limits

No performance baseline or candidate comparison was run; this is a correctness/API change.
Incidental initial-smoke frame intervals after frame 120: median 16.736 ms, p95 19.649 ms,
p99 20.702 ms (30 samples). These are not benchmark evidence. The final resize triggered
approximately 14.18 seconds of renderer preparation; resize/startup performance was not
investigated. Verification used one GPU and a small tetrahedron fixture; it is not a
general rendering-quality or timing qualification. Timed camera blending remains out of scope.

## Reproduction

From the workspace root:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~TransformConvenienceTests|FullyQualifiedName~CameraControllerTests|FullyQualifiedName~FirstPersonCameraTests|FullyQualifiedName~ContentScopeTests|FullyQualifiedName~EditorFoundationTests|FullyQualifiedName~PhysicsConvenienceTests|FullyQualifiedName~PhysicsTests|FullyQualifiedName~SceneTests|FullyQualifiedName~ModelResourceLifetimeTests'
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development --no-restore -p:NjulfFFmpeg='D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/audio-tools/ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example camera --frames 120 --validation
```

## Retention

All run logs, TRX files, and the single 125 KiB screenshot are under the workspace on D:.
No isolated build or raw trace was created. Keep compact logs/TRX evidence; the screenshot
may be pruned after 2026-09-15. Ran `tools/prune-local-artifacts.ps1` in inspection mode;
no storage shortage required removing unrelated investigation artifacts.
