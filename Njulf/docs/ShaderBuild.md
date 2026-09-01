# Shader build pipeline

`Njulf.Shaders` models every active base shader and generated variant as an independent artifact. The build task hashes each artifact's source and transitive includes, exact compiler arguments, and the `glslangValidator` binary and version. Independent misses compile concurrently; unchanged outputs and cache objects are verified before reuse. Source templates such as `ddgi_simple_trace.comp` remain inputs to their declared variants without being emitted as unspecialized runtime artifacts.

Production configurations retain their existing output names, embedded
resource names, and validation scripts. The deterministic
`njulf-shaders.manifest.json` drives validation incrementality and records the
SHA-256 of every embedded SPIR-V artifact. The 2026-09-01 Release inventory is
483 logical artifacts, 467 unique binaries, and 16 content duplicates, with
shader-bundle fingerprint
`sha256:6b8884b2d08200a4402e2742632a5d782f7acb22f441a230ae821f970845b0bf`.

## Build properties

| Property | Default | Purpose |
|---|---|---|
| `NjulfShaderBuildMode` | `Compile` | Builds, reuses, or materializes artifacts. `UseExisting` performs no compiler or cache access and fails if any active output is missing or invalid. |
| `NjulfShaderMaxParallelism` | `0` | Auto-selects the logical processor count, clamped to 1 through 8. Set `1` for serial equivalence testing. |
| `NjulfShaderCompilerTimeoutSeconds` | `900` | Maximum wall time for one compiler process. A timeout terminates its complete process tree and fails the build. Set `0` to disable. |
| `NjulfShaderCacheMode` | `ReadWrite` | Enables the verified persistent cache. Set `Off` to bypass it for equivalence testing. |
| `NjulfShaderCacheDirectory` | `artifacts/shader-cache/v1` | Cache location, deliberately outside `obj` so `dotnet clean` does not remove it. |
| `NjulfGlslangValidator` | `glslangValidator` | Compiler executable name or explicit path. |

Examples:

```powershell
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderMaxParallelism=1 -p:NjulfShaderCacheMode=Off
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release -p:NjulfShaderBuildMode=UseExisting
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c ProfileSymbols -p:NjulfShaderCompilerTimeoutSeconds=1800
```

`UseExisting` is intentionally an escape hatch: it validates the SPIR-V container but does not prove that the output matches current sources. It warns when an output predates its direct source.

With the default `ReadWrite` cache, separate Rider, CLI, and MSBuild-node
invocations coordinate each recipe through an exclusive lock under
`artifacts/shader-cache/v1/locks`. A waiter rechecks the verified cache after
the owner publishes, so only one process compiles an identical miss. Compiler
and lock waits emit a progress message every 60 seconds. Build cancellation or
the first compiler failure cancels pending work and terminates active compiler
process trees before temporary outputs are discarded.

Symbol builds use classic SPIR-V `OpLine`/`OpSource` debug information. This
retains profiler source attribution without the much larger non-semantic debug
graph that can overflow glslang's identifier space in large forward variants.
The Shipping/Profile parity gate strips debug information and compacts result
IDs before hashing; ID compaction changes numbering only and leaves executable
instructions and interfaces unchanged.

## Targeted native-compiler workarounds

Production shaders ordinarily use `-Os`. Forward ray-query programs and exact
receiver-attribution programs append `-Od` after that shared recipe. The
trailing option preserves function boundaries in shader families where the
otherwise aggressively inlined SPIR-V approaches the identifier limit or
creates a pathological single-function NVIDIA native program. Ordinary shaders
remain optimized, and the targeted families retain their public ABI and
production validation counters.

This is an operational driver workaround, not a standalone performance claim.
The current campaign did not produce the plan-required isolated native-pipeline
creation A/B for the option, so its startup benefit remains inconclusive.

The native receiver split emits 24 accepted-only, 24 exact-fallback, and 24
combined rollback artifacts. Their current aggregate sizes are 20.93 MiB,
27.03 MiB, and 34.35 MiB, with 1,272,450, 1,643,926, and 2,035,538 SPIR-V
instructions respectively. These static reductions are supporting evidence
only. Native 1080p runtime qualification found that duplicating the receiver
draws was slower and outside the quality envelope, so production presets use
the exact receiver path unless an experiment explicitly requests the cache.

## Performance campaigns

`tools/perf-campaign.ps1` explicitly points hermetic `--artifacts-path` builds at the real worktree's ignored `artifacts/shader-cache/v1` directory. This preserves shader cache reuse between campaign iterations while all compiled application artifacts remain isolated per run. The campaign rejects linked/reparse cache paths, paths outside `artifacts`, and paths that Git would track.
