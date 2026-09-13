# Onboarding and public documentation — 2026-09-13

- Source: `09be7c7d`, extensively dirty before this task (301 status entries); existing work preserved.
- Environment: Windows x64, .NET SDK 10.0.203; AMD Ryzen 5 5600H; NVIDIA RTX 3060 Laptop GPU, driver 32.0.16.1062.
- Workload: existing local-checkout template, all four physics/audio option combinations; SphereShooter; custom-host documentation snippet.
- Performance baseline/candidate/tail timings: not measured; this is an onboarding/correctness change, with no performance claim.

## Changes and decision

Keep the single `njulf-game` template. Independent `--physics` / `--audio` flags default to false and include only their optional dependencies.
Physics registers a fixed-step world and falling model. Audio registers device/scope modules and plays a prepared mono WAV on Space.
The WAV is a PCM16 channel-average downmix of the existing supplied `SphereShooter/Assets/hit-sound.pcm16.wav`; authoring sources remain intact.

Upgrade SphereShooter in place: current input/primitive helpers, level-owned physics and local audio, contact-position one-shots,
P pause/cursor handling, and observed R replacement tasks. Replacement preserves pause and disposes old scene/modules.
Keep the focused API examples and NjulfHelloGame because their distinct feature and renderer-qualification coverage remains relevant.
Replace the stale shooter code listing with a short guide linked to its actual source.

Add startup-only `Game.InitialWindowState`; document its combination with the existing border/size settings.
Document AddRendering custom-host setup and obligations with a compiled complete snippet.
Expand common math, animation, physics, audio, ownership and settings XML documentation; enable Physics/Audio.Assets XML output.
Update the existing XML check's stale Game namespace and add representative members; no broad documentation warning-policy change.

## Evidence

| Check | Result |
| --- | --- |
| Generated default / physics-only / audio-only / both | All build; evaluated runtime dependency closures include exactly the selected optional modules. |
| Both-enabled Release self-contained publish | Required managed/native dependencies, cooked model and mono WAV present; no authoring models needed. |
| Final published app, source loading disabled, different working directory | 180 full-quality frames, Space input, visible model, exit 0; initial pre-lighting run also completed 300 frames. |
| SphereShooter Development | Builds; native input exercised W, shooting, P, R and Escape. A shot toppled the central stack; W/fire while paused left the scene frozen; replacements restored a fresh scene both paused and running; process ended after Escape, stderr empty. |
| Startup window configurations | Actual native state/border matched Normal/Resizable, Normal/Hidden and Fullscreen/Resizable; all three processes exited 0. These checks exit after native startup rather than requiring scene rendering. |
| Custom-host C# snippet | Compiled with zero warnings/errors. |
| CriticalIntelliSense_IsEmittedBesideAssemblies | Passed, 38 ms; generated XML files and representative member summaries present. |
| LocalAudioWaitsForActivationResumesOnlyHostPausedVoicesAndPreservesMusic | Existing native loopback test passed, 87 ms; activation, independent pause, bounded one-shots, disposal and nonzero output verified. |
| Changed Markdown file links and whitespace | Passed. |

Scope limits: native audio calls and loopback output were checked; speaker/headphone audibility was not subjectively assessed.
The custom-host snippet was compile-checked. No full regression suite or performance qualification was run.

## Corrections and diagnostic limitation

- The physics starter initially used a nonexistent ModelInstance.Position property; its generated build caught this and the template now uses PlacementRoot.Position.
- Source review caught the floor's unsupported nonuniform physics-node scale; the sample uses correctly sized mesh/collider geometry instead.
- Published visual inspection found the existing starter sky overbright. Applying atmosphere intensity 0.02 and light intensity 1 produced the final balanced image.
- An initial Development full-render window probe took 39.323 s to present its scene, emitted `VUID-vkCmdDrawMeshTasksIndirectEXT-imageView-06183`
  for the First-Frame Universal Compacted Opaque Forward Pipeline, and exceeded its 45 s test timeout. This renderer diagnostic was not fixed
  or attributed to this change without a baseline comparison. The targeted native startup-state checks and final Release render smoke passed.
  The diagnostic text is retained under `artifacts/onboarding/window-initial-attempt-errors.log`.
- Release runs reported stale pipeline-cache provenance after changing build configurations; they completed successfully.

## Reproduction

Run from the checkout on D:, keeping generated output under ignored workspace artifacts:

```powershell
$env:TEMP = "$PWD/artifacts/onboarding/temp"
$env:TMP = $env:TEMP
$env:DOTNET_CLI_HOME = "$PWD/artifacts/onboarding/cli"
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
# Isolate generated projects from the repository's parent build properties.
New-Item -ItemType Directory -Force artifacts/onboarding | Out-Null
Set-Content artifacts/onboarding/Directory.Build.props '<Project />'
dotnet new install ./templates/Njulf.Game --force
dotnet new njulf-game -n StarterSmoke -o artifacts/onboarding/both --engine-root $PWD.Path --physics --audio
dotnet build artifacts/onboarding/both/StarterSmoke.csproj -c Development
dotnet publish artifacts/onboarding/both/StarterSmoke.csproj -c Release --self-contained true -o artifacts/onboarding/publish
$env:NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD = 'false'
./artifacts/onboarding/publish/StarterSmoke.exe --frames 180
dotnet run --project SphereShooter -c Development
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~CriticalIntelliSense_IsEmittedBesideAssemblies|FullyQualifiedName~LocalAudioWaitsForActivationResumesOnlyHostPausedVoicesAndPreservesMusic'
```

Repeat generation/build into separate folders with neither flag and with each flag individually.
The custom-host source is the complete snippet in `docs/FrameworkApi.md#custom-hosts-with-addrendering`.
For native window probes the generated test copy set the documented startup properties and printed
`Window.WindowState` / `Window.WindowBorder` in Load before exiting; no product test harness was added.

Retention: inspected storage using `tools/prune-local-artifacts.ps1`. Compact logs, TRX/JSON and generated repro sources stay on D:.
Superseded isolated binaries, cooked copies, runtime caches and captures from this task are removed after recording these findings;
other investigations and source assets are preserved.

