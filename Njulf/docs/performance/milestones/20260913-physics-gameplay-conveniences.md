# Physics events, presentation, and character controller — 2026-09-13

- Source: `09be7c7d2a70784bf91e876d9bd6a3eba8b2cc2b`, dirty workspace with existing gameplay/audio/host-module changes retained; this milestone adds the physics conveniences on top.
- Hardware: AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU, Windows, .NET 10, Jitter2 2.8.11; Development configuration.
- Decision: implement copied impact contact details, non-blocking discrete triggers, completed-pose interpolation on separate visual nodes, and an upright kinematic capsule controller. Keep the framework independent of the physics package; pass its existing interpolation alpha through module frames.
- Workload: focused CPU physics/geometry/timing/level tests, plus the physics-simulation example at 60 Hz simulation and 90 FPS cap, 540 full-quality frames, standard Vulkan validation, and one screenshot. Finite runs automatically move the character across stairs and a checkpoint and perform a jump.

## Validation and findings

- **64/64 focused tests passed.** New cases observe contact midpoint/normal/closing speed in both pair orders, compound/angular impact selection, retained event values after removal, trigger pass-through/filter/deduplication/exit/clear behavior, dynamic and kinematic pose history, quaternion interpolation, authoritative query isolation, pause/teleport reset, presentation hierarchy restrictions, grounding, walkable and steep slopes, stair limits and headroom, jumping/ceiling/landing, recoverable and impossible overlap, rotating/translating platforms, inherited jump velocity, and support removal/disable/teleport.
- Physics and API-example Development builds succeeded with zero warnings/errors. The example's existing audio cook initially failed because FFmpeg was absent from PATH; using the already available workspace executable resolved it without dependency changes.
- Live example: **540 full-quality frames, 736 physics steps, 1 checkpoint entry, 0 Vulkan validation errors**; character finished grounded at approximately `(4.015, 0.920, 3.000)`. Stack toppled at 2.65 simulated seconds. Inspected the screenshot: character, stairs, checkpoint, platform, and boxes are visible.
- Initial candidates rejected by focused tests: trusting the Jitter filter XML normal direction reversed public normals; the adapter now follows the observed callback direction. Penetration recovery needed the opposite of Jitter's returned separation direction. Capsule-rounded stair contact normals initially rejected a flat tread; a short support-surface ray now distinguishes the tread from a steep slope. Gravity now retains the slope normal while descending so rejected steep slopes do not suspend the character.

## Timing and limitations

- Baseline/candidate performance comparison: **not run**; this is a gameplay correctness change, not a performance claim.
- Incidental smoke frame intervals after warmup: median **11.119 ms**, p95 **13.018 ms**, p99 **26.861 ms**, 420 samples. Startup/capture caused visible renderer hitches; these numbers are not an isolated physics measurement.
- Trigger detection is discrete per completed simulation step; crossing a thin volume between steps can be missed. QueryOnly exposes trigger geometry without transitions. Meshes remain surfaces rather than containment volumes.
- Controller is kinematic and Y-up, with bounded recovery and conservative clearance. Crouching, climbing, custom pushing, and networking remain out of scope. The automated live run and inspected still image do not substitute for subjective player-feel tuning.
- Presentation is explicitly bound to a separate visual hierarchy; physics nodes and queries remain authoritative. Paused kinematic targets wait for completion unless teleported.

## Reproduction

From the repository root:

```powershell
dotnet test Njulf.Tests -c Development --no-restore --filter 'FullyQualifiedName~PhysicsTests|FullyQualifiedName~PhysicsGeometryTests|FullyQualifiedName~PhysicsConvenienceTests|FullyQualifiedName~CharacterControllerTests|FullyQualifiedName~GameTimingTests|FullyQualifiedName~GameLevelTests'
dotnet build Njulf.ApiExamples -c Development --no-restore '-p:NjulfFFmpeg=D:/Code/C#/Njulf4.0-Simplified/Njulf/.codex-tmp/audio-tools/ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe'
dotnet Njulf.ApiExamples/bin/Development/net10.0/Njulf.ApiExamples.dll --example physics-simulation --frames 540 --validation --capture 'D:/Code/C#/Njulf4.0-Simplified/Njulf/.tmp/physics-conveniences/smoke.png'
```

For interactive use, omit `--frames` and `--capture`: arrows move the character, Enter jumps, R resets, P pauses, and T toggles half speed. An installed FFmpeg can replace the temporary executable for future builds.

## Artifact retention

The only new capture is `.tmp/physics-conveniences/smoke.png` (172,098 bytes), on D:, disposable by 2026-09-15. No isolated publish copy, trace, or benchmark archive was created. `tools/prune-local-artifacts.ps1` inspection reported 256.16 GiB free and 0.33 GiB of older candidates in other investigations' roots; those unrelated references were left intact. Preserve this compact record after the capture expires.
