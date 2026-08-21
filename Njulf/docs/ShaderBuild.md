# Shader build pipeline

`Njulf.Shaders` models every base shader and generated variant as an independent artifact. The build task hashes each artifact's source and transitive includes, exact compiler arguments, and the `glslangValidator` binary and version. Independent misses compile concurrently; unchanged outputs and cache objects are verified before reuse.

Production configurations retain their existing compiler options, output names, embedded resource names, and validation scripts. The deterministic `njulf-shaders.manifest.json` drives validation incrementality and records the SHA-256 of every one of the 254 embedded SPIR-V artifacts.

## Build properties

| Property | Default | Purpose |
|---|---|---|
| `NjulfShaderBuildMode` | `Compile` | Builds, reuses, or materializes artifacts. `UseExisting` performs no compiler or cache access and fails if any active output is missing or invalid. |
| `NjulfShaderMaxParallelism` | `0` | Auto-selects half the logical processors, clamped to 1 through 8. Set `1` for serial equivalence testing. |
| `NjulfShaderCacheMode` | `ReadWrite` | Enables the verified persistent cache. Set `Off` to bypass it for equivalence testing. |
| `NjulfShaderCacheDirectory` | `artifacts/shader-cache/v1` | Cache location, deliberately outside `obj` so `dotnet clean` does not remove it. |
| `NjulfGlslangValidator` | `glslangValidator` | Compiler executable name or explicit path. |

Examples:

```powershell
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderMaxParallelism=1 -p:NjulfShaderCacheMode=Off
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderBuildMode=UseExisting
```

`UseExisting` is intentionally an escape hatch: it validates the SPIR-V container but does not prove that the output matches current sources. It warns when an output predates its direct source.

## Performance campaigns

`tools/perf-campaign.ps1` explicitly points hermetic `--artifacts-path` builds at the real worktree's ignored `artifacts/shader-cache/v1` directory. This preserves shader cache reuse between campaign iterations while all compiled application artifacts remain isolated per run. The campaign rejects linked/reparse cache paths, paths outside `artifacts`, and paths that Git would track.
