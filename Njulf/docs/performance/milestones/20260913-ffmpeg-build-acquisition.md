# FFmpeg build dependency acquisition — 2026-09-13

- Source: `09be7c7d`, extensively dirty workspace with existing unrelated work.
- Baseline: API-example build failed because `ffmpeg` was absent from PATH.
- Decision: Windows audio targets resolve an explicit `NjulfFFmpeg` first, then PATH,
  then acquire the pinned Gyan 9.0.1 essentials ZIP. Archive SHA-256 is pinned in
  `tools/resolve-ffmpeg.ps1`; cached executable integrity is checked on reuse.
  Other platforms and `NjulfDownloadFFmpeg=false` retain PATH behavior.
- Validation: fresh download/hash/extraction succeeded; cached MSBuild resolution,
  explicit override, and download opt-out succeeded. Audio cook stamp identifies the
  acquired executable. Model example completed 3 full-quality frames, exit 0,
  validation errors=0 on Windows / NVIDIA GeForce RTX 3060 Laptop GPU.
  No FFmpeg payload found in the example build output.
- Corrected during validation: Windows PowerShell launched from MSBuild inherited
  an incompatible module search path; explicitly importing its utility module fixes
  `Get-FileHash` and download command discovery.
- Storage: approximately 109 MiB of executable, upstream notices and documentation
  under `artifacts/tools/ffmpeg-9.0.1`; download ZIP removed, unused FFplay/FFprobe
  not extracted. Pruner preserves this active build dependency for offline reuse.
  Pruner inspection found 0.33 GiB of older payloads and 256.05 GiB free; unrelated
  campaign payloads were left in place because their active status was not established.
- Limitations: first acquisition requires network; automatic download qualified on
  Windows x64 only. Standalone AssetTool still needs PATH or `--ffmpeg`. No performance
  comparison or tail-latency benchmark was needed for this build-dependency fix.

```powershell
dotnet run --project Njulf.ApiExamples -c Development -- --example model --frames 3 --validation
dotnet msbuild Njulf.ApiExamples/Njulf.ApiExamples.csproj -t:ResolveNjulfFFmpeg -p:Configuration=Development -getProperty:NjulfFFmpeg
```
