# Game template, content iteration, and publishing — 2026-09-10

- Source: `10440367`, dirty working tree; existing user changes preserved. Engine dependency lock files are unchanged.
- Machine: Windows, AMD Ryzen 5 5600H, NVIDIA RTX 3060 Laptop GPU (driver 32.0.16.1062); .NET SDK 10.0.203 and Vulkan SDK 1.4.335.0 for building.
- Workload: generated game under an ignored path containing spaces, isolated from parent build props. Original tetrahedron fixture plus an independent second model, an external buffer, one compute shader, and one declared runtime text file.
- Decision: accept the local-checkout template, opt-in MSBuild cooking integration, located diagnostics, and Windows x64 self-contained publish workflow. No engine package distribution, watcher, renderer redesign, or performance improvement is claimed.

## Validation

- First build cooked and copied both models and compiled game shader in the same invocation. Unchanged build skipped both models without changing package timestamps. Editing a buffer recooked only its owner; deleting a package repaired only that package.
- Malformed glTF and GLSL failed their builds with source file/line diagnostics. Two shader diagnostic tests, two scene/effect JSON diagnostic tests, and seven existing cooker discovery/transaction tests passed. Initial test compilation required rebuilding the already-dirty editor dependency; no editor source changes were made for this task.
- Release publish passed locked restore and excluded source models, GLSL/C# source sidecars, editor/importer assemblies, and build tooling. It included native/.NET runtime dependencies, cooked content, declared runtime files, compiled application shaders, and notices.
- Removing a model and runtime-content declaration removed their old files from reused build and publish folders. Unsafe overlapping cook/source roots failed before cooking; design-time and `NoBuild` target invocations skipped cooking.
- Final published executable completed **30 fully rendered frames**, exit code 0, from an unrelated working directory with its source assets moved aside, source fallback disabled, and Vulkan SDK removed from PATH/environment. Full-quality readiness is required before the frame counter advances. Vulkan validation layers and image-comparison gates were not enabled for this functional smoke.

## Timings, rejected approaches, and limits

[Compact results](20260910-game-workflow.json) retain the observations and checks. These are single-run functional timings, not a controlled benchmark; median/p95/p99 frame latency was not measured.

| Observation | Seconds |
| --- | ---: |
| Generated first Development build, existing engine cache | 43.2 |
| Unchanged Development build; zero recooks | 32.0 |
| Dependency edit / missing-output repair | 30.4 / 31.5 |
| Clean self-contained publish after engine validation | 34.0 |
| Final launch through 30 fully rendered frames, warm pipeline cache | 16.93 |

The final launch reported its first production scene at 14.078 seconds, including a 10.36-second first-render hitch. Startup remains dependent on existing driver/pipeline preparation; this work does not qualify or optimize those costs. The initial engine Release shader verification took most of a 16m49s build; subsequent builds reused its validation stamp.

Rejected or corrected during validation:

- Global command-line `-r win-x64` invalidated engine lock graphs. The template instead declares its Release RID locally and includes `win-x64` in its initial restore; the documented publish command omits `-r` and keeps locked restore enabled.
- Transitive engine content copied `common.glsl` and `ShaderLibrary.cs` into publish. Game targets now copy only explicitly declared game content; native/package runtime resolution remains enabled.
- Counting bootstrap presents gave an insufficient launch check. The corrected counter waits for full-quality scene presentation. This exposed an unnecessary early `LookAt` call using an uninitialized projection; the template uses the host's default camera. Its `Update` override also retains the base scene update.
- The default advanced-GI configuration did not reach fully rendered frames within 180 seconds on this machine. The starter uses the existing Low preset at native resolution. This is a deliberate starter-quality choice, not a like-for-like rendering speedup. No renderer settings defaults were changed globally.

Validation ran in focused stages after these fixes; completed content checks were reused while publishing and runtime checks were repeated for the changed paths. Windows x64 is the only validated shipping target. Source-backed material/scene JSON and standalone runtime files still use existing loaders; this change adds no new asset formats.

## Reproduction and retention

```powershell
./tools/test-game-template.ps1 -RunGame
dotnet test Njulf.ShaderBuild.Tests -c Release --filter 'ShaderDiagnosticsRetainIncludeLocationsAndUnparsedOutput|CompilerFailureDoesNotReplacePublishedOutputOrLeaveTemporaryFiles'
dotnet test Njulf.Tests -c Development --filter 'ContentDiagnosticTests|ModelAssetCookerFolderDiscoveryTests|ModelAssetCookerTransactionTests'
```

Set `TEMP`/`TMP` to a workspace artifact directory on D: when running tests locally. The smoke script does this automatically. See [Getting started](../../GettingStarted.md) for creation and publish commands.

Raw logs and the final publish are under `artifacts/game-template/7429ca7ca31d4e639d20c3f74036836a`; orchestration/test logs are under `artifacts/game-workflow`. Preserve the final payload for at most two days after completion. Compact results and this record are permanent.

`tools/prune-local-artifacts.ps1` preview found 4,248 old-timestamp payloads / 9.76 GiB with zero inspection failures and 279.38 GiB free. Some were newly copied runtime dependencies with inherited timestamps; other investigations' active-reference status was unknown, so a repository-wide `-Apply` was not safe. Automatic approval review then rejected deletion of this task's verified superseded build copies, including a narrower binary-file-only attempt, with “blocked by policy.” No cleanup deletion occurred. The copies remain inside ignored artifact roots and require cleanup when permitted; compact evidence is preserved here.
