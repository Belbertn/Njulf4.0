# Audio content and playback controls — 2026-09-13

- Source: `09be7c7d2a70784bf91e876d9bd6a3eba8b2cc2b` plus dirty working tree. Existing gameplay/host-module changes were preserved; this change extends the current audio scopes.
- Hardware: Windows x64, AMD Ryzen 5 5600H, NVIDIA RTX 3060 Laptop GPU (also AMD integrated graphics).
- Workload: Development configuration; OpenAL Soft loopback at 48 kHz, tiny generated audio fixtures, and the published audio example for 120 full-quality frames with validation enabled.
- Decision: retain the optional content adapter, synchronous shared clip loading, build-only FFmpeg conversion to PCM16 WAV, fixed Music/SFX/UI groups, explicit resume/restart, and world-space bindings. Streaming remains a separate follow-up in `docs/Audio.md`.
- Baseline/candidate performance and tail latency: not measured; this was correctness work, with no performance claim or benchmark campaign.

## Validation

- **34 passed, 0 failed, 0 skipped** across AudioTests, AudioCookerTests, and ContentScopeTests (about 2 seconds of test execution).
- Converter fixtures: PCM WAV; stereo float WAV at 96 kHz; MP3; Ogg Vorbis; FLAC. Checked runtime readability, channels/sample rate, duration and approximate signal amplitude, unchanged conversion skip, explicit mono conversion, input/settings invalidation, and preservation of the last good output on failure.
- Loopback/content checks: normalized cache reuse, shared ownership and failed-unload retry, group gain/mute/fades, independent pause reasons, resume/restart sample positions, pooled deferred voices, and emitter/listener movement.
- Published example: **exit 0; 120 full-quality frames; validation errors=0**. Loaded `Audio/occlusion-loop.wav` through the cache, observed both blocked and clear occlusion, and completed host cleanup. Converted WAV was 48,044 bytes; FFmpeg was absent from publish output.
- Affected integration/tool builds and final example publish succeeded. Test-project compilation emitted existing warnings outside the changed audio tests.

## Findings corrected during validation

- OpenAL keeps a stopped, never-started queued restart in Initial state. Explicit Stop now reports Stopped and cancels pending playback.
- The FFmpeg build's automatic probing misidentified short, valid periodic PCM fixtures as MPEG-TS. The cooker selects the supported input container explicitly; output is still parsed and normalized before publication.
- MSBuild inline-task references lack `Path.GetRelativePath`; the validated output-root prefix supplies the relative Link instead. Intermediate paths are rooted at the consuming project.
- Manually disposing a registered audio scope during example Unload conflicted with host deactivation. The example now registers content cleanup before the scope, so host ordering releases voices, then clips, then the device. The final smoke run verified that ordering.

## Reproduce

Install FFmpeg on PATH, or supply its executable path as below:

```powershell
$env:NJULF_TEST_FFMPEG = 'D:/Tools/ffmpeg.exe'
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --filter 'FullyQualifiedName~AudioTests|FullyQualifiedName~AudioCookerTests|FullyQualifiedName~ContentScopeTests' --logger 'trx;LogFileName=audio-content.trx' --results-directory artifacts/audio-content-tests
dotnet publish Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development -o artifacts/audio-content-publish -p:NjulfFFmpeg=D:/Tools/ffmpeg.exe
./artifacts/audio-content-publish/Njulf.ApiExamples.exe --example audio --frames 120 --validation
```

Validation used BtbN `n9.0.1-29-gad500d59cb-20260912` from the Windows builds linked by FFmpeg's download page. Executable SHA-256: `E50359DB4A15DE57B5F744F330573B9C7364ED6B4F98A992B4FD5FB54C015B5C`. This is a build-machine tool, not a redistributed game dependency.

## Evidence, retention, and limits

- Compact evidence: `artifacts/audio-content-tests/audio-content.trx`, `test.log`, `publish.log`, and `published-run.log`.
- Storage inspection: `tools/prune-local-artifacts.ps1` found about 0.33 GiB of age-eligible files, with about 256 GiB free. Some copied files retain old timestamps despite belonging to recent work, so no broad Apply was run against other investigations.
- Automatic approval review rejected generated-artifact cleanup as `blocked by policy`, including a narrower deletion of verified downloaded archives and unused FFplay/FFprobe executables. No more specific reason was supplied. About 443 MiB of redundant downloads/utilities and the 413 MiB completed isolated publish copy remain pending permitted cleanup (retention deadline: 2026-09-15). The temporary FFmpeg executable remains usable for local reproduction under `.codex-tmp/audio-tools/ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe`.
- No streaming, asynchronous audio loading, subjective listening qualification, cross-platform matrix, or full-solution test run. Cooker tests explicitly skip if FFmpeg is unavailable; none skipped in this validation.
