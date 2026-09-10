# Optional fixed simulation and pause — 2026-09-10

- **Source:** `104403675fc74abf8c110c07a30a8a6411589689`, dirty workspace before and
  after this change. Existing content, input, scene, editor and renderer work was preserved.
- **Hardware:** Windows, AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU.
- **Decision:** retain variable simulation by default; add separate fixed callbacks,
  a five-step default catch-up limit, application interpolation, explicit/focus pause,
  and scaled/unscaled timing. Render-driven effects honor pause/scale without GPU substeps.
- **Workload:** Development configuration. Deterministic clock/CPU tests; 96×64 Vulkan
  particle fixture with 1 ms fixed steps, cap three, 64-particle budget, pause and half-speed
  resume, then variable simulation. API example: 960×640, 30 Hz simulation, 90 FPS CPU cap,
  VSync, default antialiasing, CPU particles and wind, 60 full-quality frames.

## Validation

- 20 focused clock/framework/CPU particle tests passed. Existing default stream timing,
  zero/one/multiple steps, truncation, interpolation, scale changes, pause/focus policy,
  resume after missing callbacks, and zero-delta burst behavior are covered.
- Two fixed-callback lifecycle tests passed: exit/disposal and exception stop catch-up
  and unload once.
- One live host/GPU test passed: input once per host tick, scene updated in exactly one
  simulation mode, no initial paused spawning, identical live particle state and cumulative
  spawn counts across paused presented frames, retained render instances, advancing age on
  resume, and successful return to variable simulation. Vulkan validation errors: zero.
- Timing API example built without warnings/errors and completed 60 full-quality frames
  with zero Vulkan validation errors.
- Changed particle shader freshly compiled and passed `spirv-val --target-env vulkan1.3`.
- `git diff --check` passed for the changed tracked files.

Evidence: [unit TRX](../../../artifacts/timing/tests/timing-unit.trx),
[GPU TRX](../../../artifacts/timing/tests/timing-gpu.trx),
[initial combined run](../../../artifacts/timing/tests/timing.trx),
[example log](../../../artifacts/timing/example-output.txt).
The initial combined run contains the two passing early-exit cases and a superseded
GPU fixture failure; the GPU-specific TRX is the final result for that case.

## Rejected behavior and limitations

- Initial clock implementation reset variable elapsed time when an unused fixed interval
  changed. The focused test detected this; the reset now applies only to active fixed timing.
- GPU fixture setup initially assumed a DI-owned particle manager and frame-local spawn
  counts. Corrected it to observe renderer-owned buffers and unchanged cumulative counts.
- Baseline/candidate throughput and p50/p95/p99 frame latency were not benchmarked; no
  performance improvement is claimed. The example logged 3.451 s bootstrap and 33.282 s
  first production scene presentation with incomplete application cache provenance. These
  startup observations are not a controlled baseline comparison.
- GPU readback validates particles directly; foliage/renderer integration has smoke coverage,
  not a separate image-equality oracle. Renderer effects retain their existing stability limits
  and render cadence. Interpolation remains application-owned.

## Reproduction and retention

Run from the repository root; keep caches inside this workspace:

```powershell
$timingRoot = Join-Path (Get-Location) 'artifacts/timing'
$env:NJULF_VULKAN_PIPELINE_CACHE_DIRECTORY = Join-Path $timingRoot 'pipeline-cache'
$env:NJULF_PIPELINE_BINARY_CACHE_DIRECTORY = Join-Path $timingRoot 'pipeline-binaries'
$env:NJULF_DDGI_WARM_CACHE_DIR = Join-Path $timingRoot 'ddgi-cache'
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter "FullyQualifiedName~GameTimingTests|FullyQualifiedName~GameTimingLifecycleTests|FullyQualifiedName~FrameworkContractTests|FullyQualifiedName~ParticleEmitterSimulationTests"
dotnet run --project Njulf.ApiExamples -c Development --no-restore -- --example timing --frames 60 --validation
spirv-val --target-env vulkan1.3 Njulf.Shaders/obj/Development/net10.0/Shaders/particle_simulate.comp.spv
```

`tools/prune-local-artifacts.ps1` inspection found 7.84 GiB of old candidates and
283.06 GiB free on D:. No broad deletion was applied because those directories belong
to other investigations with unverified active references. This task's artifacts total
approximately 44 MiB, mostly reusable pipeline caches; obsolete failed payloads were
overwritten, with useful findings retained here. Bulky timing payloads are eligible for
normal pruning two days after completion; preserve compact text/TRX evidence.
