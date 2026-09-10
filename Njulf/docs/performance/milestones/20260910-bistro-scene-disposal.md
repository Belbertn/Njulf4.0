# Bistro loading-scene teardown regression — 2026-09-10

Source: `10440367`, dirty working tree with existing scene-ownership, renderer and editor work preserved. This fix changes the scene light add/remove guards and adds behavioral regression coverage.

## Failure and decision

The reported SponzaPlaza → Bistro transition chose the lightweight loading scene because its estimated 5388.9 MiB requirement exceeded the 4180.8 MiB admission ceiling. It failed at 2% while releasing Sponza. `Scene.Dispose()` sets its disposal guards before disposing the scene-owned `ModelLightRuntimeController`; that controller then calls `SceneLightStore.TryRemove`, which previously rejected every light removal. `Scene.Clear()` had the same failure. Imported directional-light cleanup also restores suspended authored lights through `Add`.

Allow light additions/removals only within the active scene teardown scope, before the final light collection clear. Scene lights hold no disposable resource registrations. Resource-owning additions and update/reentrant clear remain blocked; light ID uniqueness, nonempty IDs and cross-scene ownership remain checked. Light mutations are rejected after disposal and between failed disposal attempts. Keep the existing imported-light cleanup and authored-sun restoration behavior.

## Evidence

Hardware/environment: AMD Ryzen 5 5600H, Windows, .NET SDK 10.0.203, Development configuration. CPU-only scene fixtures with two imported point lights and an optional imported directional light replacing an authored sun; no GPU workload required for this contract.

- Before the fix: all four real `SceneLightStore` regression cases failed with the reported `ObjectDisposedException` chain (Clear/Dispose × imported sun disabled/enabled).
- After the fix: 94 focused scene, model-light, transition coordinator and transition recovery tests passed; zero failed or skipped. Includes the four reproductions and two additional guard/failure-retry cases.
- Assertions observe empty scene collections, controller shutdown, released light membership/event subscriptions, reusable cleared scenes, permanently closed disposed scenes, and validation during/after teardown.
- Development tests project and its references, including the sample, built successfully with no warning/error diagnostics. `git diff --check` passed.
- No frame-time, tail-latency or image comparison was performed. No performance claim; the full interactive Sponza → Bistro load was not rerun. Existing coordinator/recovery tests do not prove a complete rendered transition.

Compact logs and TRX: `artifacts/bistro-scene-disposal-20260910/{before,after}.{log,trx}`. The final focused test run reported about one second of test execution; this is not renderer timing evidence.

## Reproduction

```powershell
$runRoot = Join-Path (Get-Location) 'artifacts/bistro-scene-disposal-20260910'
New-Item -ItemType Directory -Path (Join-Path $runRoot 'temp') -Force | Out-Null
$env:TEMP = Join-Path $runRoot 'temp'
$env:TMP = $env:TEMP
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Njulf.Tests.SceneTests|FullyQualifiedName~ModelLightImportTests|FullyQualifiedName~SampleSceneTransitionRecoveryTests|FullyQualifiedName~SampleSceneTransitionCoordinatorTests' --results-directory $runRoot --logger 'trx;LogFileName=after.trx'
```

For the four original reproductions alone, filter on `FullyQualifiedName~RuntimeController_SceneTeardownReleasesSceneBackedLights`.

## Storage

The pruning tool's read-only inspection found 7.84 GiB eligible by age with 288.46 GiB free on D:. It includes 2.89 GiB belonging to the still-active scene/view investigation, so no blanket deletion was applied. This investigation produced about 172 KiB in six files under its D: artifact directory, with no captures, traces or isolated build copies. Existing build output was reused.
