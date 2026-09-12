# Audio integration validation — 2026-09-12

- Source: `2c47c9e4` plus uncommitted audio module, example, tests, and documentation.
  Independent physics edits were present during validation and were not changed by this task.
- Host: Windows x64, AMD Ryzen 5 5600H, NVIDIA RTX 3060 Laptop GPU; .NET 10 Development build.
- Workload: OpenAL Soft 1.23.1, 48 kHz stereo float loopback with 400/4000 Hz test tones;
  live example at 960x640, 120 full-quality frames, Vulkan validation enabled,
  one 24 kHz mono looping source, one query-only wall, 10 Hz occlusion queries.
- Results: all 7 focused audio test cases passed, none skipped. Loopback verified
  listener-relative panning, distance attenuation, selective high-frequency reduction,
  filter removal/restoration, volume-only fallback, initial occlusion, pause/resume,
  non-spatial playback, disposal, WAV formats/validation, and smoothing.
- Live and published Windows x64 apphost runs both passed 120 frames with blocked
  and clear paths, EFX enabled, and zero Vulkan validation errors. Published native
  library, WAV, and audio notices were present; the app ran from its publish directory.
- Baseline/candidate timing and p50/p95/p99: not measured; this is a functional
  feature integration, not a performance comparison. No performance improvement claimed.
- Decision: retain the optional game-owned module. No source pooling, dedicated
  audio thread, compressed decoding, streaming, or scene integration was added.
- Limitations: no subjective headphone listening assessment, other OS qualification,
  device hot-swap, AOT/trimmed deployment, or many-source performance measurement.
  Live runs used the renderer's existing shared local caches; these were not pruned
  because original ownership is uncertain. Future diagnostic runs should redirect
  renderer caches to the workspace using the environment settings below.
- Retention: loopback samples remained in memory. The temporary publish copy is
  approximately 413 MiB at `artifacts/audio-publish`. Automatic approval review
  rejected its removal as "blocked by policy"; it remains subject to the two-day
  retention window. The pruning utility was inspected without applying global
  cleanup, since an unrelated investigation was present.

Reproduction from the workspace (cache/output paths stay on D:):

```powershell
$env:NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY = "$PWD/artifacts/audio-validation/pipeline-cache"
$env:NJULF_PIPELINE_BINARY_CACHE_DIRECTORY = "$PWD/artifacts/audio-validation/pipeline-binaries"
$env:NJULF_ENVIRONMENT_CACHE_DIR = "$PWD/artifacts/audio-validation/environment-cache"
dotnet test Njulf.Tests -c Development --filter FullyQualifiedName~AudioTests
dotnet run --project Njulf.ApiExamples -c Development -- --example audio --frames 120 --validation
dotnet publish Njulf.ApiExamples -c Development --no-restore -o artifacts/audio-publish
./artifacts/audio-publish/Njulf.ApiExamples.exe --example audio --frames 120 --validation
```
