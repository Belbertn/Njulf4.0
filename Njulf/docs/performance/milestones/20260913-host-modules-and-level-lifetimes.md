# Optional host modules and managed level lifetimes — 2026-09-13

Source: `09be7c7d2a70784bf91e876d9bd6a3eba8b2cc2b` plus this implementation and the existing
uncommitted gameplay/audio/physics/math/example changes. Existing edits were preserved.
Hardware: AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU, Windows; .NET 10 Development.

## Decision and contracts

Retain opt-in `IGameModule` registration, fixed physics followed by contacts, spatial audio,
and unscaled device maintenance. Framework gains no Physics/Audio dependency. Simulation
requires fixed mode. Audio scopes distinguish host suspension from explicit source pause/stop;
root music survives pause and level replacement.

`GameLevel` coordinates scene/content/local-module ownership. Candidates remain inactive until
commit between callbacks. Cancellation/failure preserves the current level; unload and shutdown
cancel/drain pending loading. Local audio precedes physics, scene and content during disposal.
Scope-loaded scenes release through their content owner. API XML and guides document timing,
pause, ownership and lifecycle order; physics/audio/content examples use the new integration.

Review correction: capture the pending level task before shutdown cancellation, since cancellation
may finish it synchronously. Native shutdown-during-loading coverage passes with that correction.

## Validation

- First focused run: 44/44 passing, no skips (level ownership/cancellation, OpenAL loopback,
  architecture/XML documentation, existing timing and content scopes).
- Physics/host regression run: 10/10 passing, no skips, including native replacement, existing
  timing and early-exit/loading/shutdown cases, and actual post-step physics contact delivery.
- Final native run after the shutdown correction: 2/2 passing, no skips (replacement and shutdown
  during pending loading). This replaces the earlier replacement case: 55 distinct final cases.
- API examples build succeeded. Content example: 180 full-quality frames, Vulkan validation
  errors zero; old local assets released, shared material/root texture preserved, final root release verified.
- `git diff --check` passed. Existing unrelated nullable/unawaited-call test-project warnings remain.

Timing baseline: not measured; this is correctness work, not a performance comparison.
The content smoke incidentally reported frame intervals median 16.661 ms, p95 19.776 ms,
p99 22.421 ms (60 samples). These unpaired values are not evidence of a performance improvement.
No image-quality change, benchmark campaign, or full-suite run was needed.

## Reproduction and retention

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~GameLevelTests|FullyQualifiedName~AudioTests|FullyQualifiedName~FrameworkArchitectureTests|FullyQualifiedName~GameTimingTests|FullyQualifiedName~ContentScopeTests'
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --no-restore --filter 'FullyQualifiedName~GameModuleLifecycleTests|FullyQualifiedName~GameTimingLifecycleTests|FullyQualifiedName~GameLifecycleIntegrationTests|FullyQualifiedName~MasksFilterSimulationAndContactsAllowRemovalAfterPublication'
dotnet run --project Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development --no-restore -- --example content --frames 180 --validation
```

Compact run evidence is under `artifacts/host-level-validation/` on D: (logs/TRX only, roughly
60 KiB). No captures or isolated builds were created. The prune tool was inspected/run without
`-Apply`: about 0.22 GiB of older eligible payloads and 257 GiB free; no unrelated payloads were
removed. Use PowerShell 7 for that tool (`Path.GetRelativePath` is unavailable in Windows PowerShell 5).

Limitations/defaults: one pending transition; cooperative cancellation and all loader work must
be awaited; loading temporarily overlaps old/new memory. WAV loading remains synchronous.
Cleanup errors after commit fault the task but leave the new level active. Borrowed external
audio clips must outlive their sources. Manual APIs remain available outside managed ownership.
