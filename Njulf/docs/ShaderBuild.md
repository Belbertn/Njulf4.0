# Shader build pipeline

`Njulf.Shaders` models every base shader and generated variant as an independent artifact. The build task hashes each artifact's source and transitive includes, exact compiler arguments, and every compiler/optimizer binary and version used by its recipe. Independent misses compile concurrently; unchanged outputs and cache objects are verified before reuse.

Production configurations retain their output names, embedded resource names, and validation scripts. The deterministic `njulf-shaders.manifest.json` drives validation incrementality and records the SHA-256 of all 436 embedded SPIR-V artifacts.

The four C5 trace-resolution source variants use a bounded function-preserving production recipe. `glslangValidator` first emits a raw `-Od` module. The build task validates its instruction stream and marks non-entry functions containing at least 200 SPIR-V instructions `DontInline`, then runs `spirv-opt --preserve-bindings --preserve-interface --preserve-spec-constants -Os`. The threshold, exact pass list, and optimizer fingerprint are part of the artifact cache key. Debug does not use this recipe. A requested selective recipe fails clearly if the optimizer is unavailable; it never silently publishes the raw module.

## Build properties

| Property | Default | Purpose |
|---|---|---|
| `NjulfShaderBuildMode` | `Compile` | Builds, reuses, or materializes artifacts. `UseExisting` performs no compiler or cache access and fails if any active output is missing or invalid. |
| `NjulfShaderMaxParallelism` | `0` | Auto-selects the logical processor count, clamped to 1 through 8. Set `1` for serial equivalence testing. |
| `NjulfShaderCacheMode` | `ReadWrite` | Enables the verified persistent cache. Set `Off` to bypass it for equivalence testing. |
| `NjulfShaderCacheDirectory` | `artifacts/shader-cache/v1` | Cache location, deliberately outside `obj` so `dotnet clean` does not remove it. |
| `NjulfGlslangValidator` | `glslangValidator` | Compiler executable name or explicit path. |
| `NjulfSpirvOptimizer` | `spirv-opt` | Optimizer executable name or explicit path. Resolved only when an active artifact requests the selective function-preserving recipe. |

Examples:

```powershell
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderMaxParallelism=1 -p:NjulfShaderCacheMode=Off
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderBuildMode=UseExisting
```

`UseExisting` is intentionally an escape hatch: it validates the SPIR-V container but does not prove that the output matches current sources. It warns when an output predates its direct source.

## Performance campaigns

`tools/perf-campaign.ps1` explicitly points hermetic `--artifacts-path` builds at the real worktree's ignored `artifacts/shader-cache/v1` directory. This preserves shader cache reuse between campaign iterations while all compiled application artifacts remain isolated per run. The campaign rejects linked/reparse cache paths, paths outside `artifacts`, and paths that Git would track.
