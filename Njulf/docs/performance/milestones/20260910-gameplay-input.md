# Gameplay input — 2026-09-10

- Source: `10440367` plus the existing dirty workspace and this input implementation. Unrelated
  graphics/content/editor changes were retained. No dependency or renderer settings changes.
- Hardware: AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU (AMD integrated GPU also present),
  Windows, .NET 10, Silk 2.23 GLFW. No connected XInput slots were reported by `XInputGetState`.
- Decision: retain cached button actions; add float/Vector2 state and delta actions, typed standard
  gamepad controls, dead zones, exclusive contexts plus globals, cancellable capture and JSON bindings.
  Cursor/text use `Game.Input`; the host forwards focus and ignores focus callbacks during shutdown.

## Validation

- Final focused suite: **36 passed, zero failed/skipped**, including the eight existing input tests,
  new behavioral tests and framework architecture tests. Fake devices exercise actual public APIs,
  not source-text assertions. Covered normalized values, composites, deltas, contexts, capture,
  transactional persistence, standard mapping, hot-plug slots, cursor/focus/text and lifetime/thread rules.
- Final Debug API-example build: **zero warnings/errors**. Input XML documentation builds cleanly.
- Final rebuilt example, `--example input --frames 30 --validation`: **completed 30 full-quality
  frames, zero Vulkan validation errors**, and exited. This shorter run validates the final host focus
  and shutdown changes. First production scene presentation was 9.809 s with caches warmed by the
  earlier run; this is not a comparable performance baseline.
- Native-window smoke used messages addressed only to the sample's HWND: Jump, menu switching,
  Unicode `ø` delivery/acceptance (stdout uses OEM encoding), button and mouse-motion rebinding,
  F5 save/F6 reload, and focus transitions. Logs observed successful capture, saving/loading,
  and Jump through the newly captured binding after returning to gameplay.
- Physical controller exercise remains unavailable; gamepad ranges, mappings, dead zones and
  disconnect/reconnect behavior have simulated-device coverage.

## Run limitation and timing

The initial live run requested 1,200 full-quality frames, unnecessarily large for this feature.
It reported a stale native pipeline cache and first production scene presentation at **485.068 s**.
Input checks passed during startup, but the later rendering run stopped responding to its close
request and was terminated. This is not a successful finite-render-run result.

A single Rider snapshot showed worker `.NET TP Worker @2536` in native graphics-pipeline creation:
`GiPipelineCacheService.CreateGraphicsPipeline` line 1012, reached through
`MeshPipeline.TryPrepareHybridReflectionSiblingBank` and post-first-present pipeline preparation.
The main thread had no exposed managed frames; frame-value inspection could not select the worker.
This establishes outstanding native pipeline work, not the exact cause of the main-thread stall.
No renderer behavior was changed. The debugger was stopped and user breakpoint states were preserved.

No before/after input timing or median/p95/p99 latency comparison was performed; this milestone makes
no performance claim. The cold renderer startup observation is not an input benchmark.

## Reproduction and retention

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Debug --no-restore --filter "FullyQualifiedName~Njulf.Tests.Input|FullyQualifiedName~Njulf.Tests.FrameworkArchitectureTests"
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Debug --no-restore
dotnet run --project Njulf.ApiExamples -c Debug --no-build -- --example input --frames 30 --validation
dotnet run --project Njulf.ApiExamples -c Debug --no-build -- --example input --bindings artifacts/input-example/bindings.json
```

The guide `docs/GameplayInput.md` documents controls and persistence semantics. Native smoke logs,
the targeted-message script and generated bindings are under ignored `artifacts/input-smoke` on D:.
Generated smoke bindings were moved out of the executable directory so they do not change demo defaults.
No images, traces, dumps or isolated build copies were retained. The artifact-pruning inspection found
7.84 GiB / 1,926 old payload candidates with 283.1 GiB free. No broad cleanup was applied; other
investigations' artifacts and reference captures were left intact.
