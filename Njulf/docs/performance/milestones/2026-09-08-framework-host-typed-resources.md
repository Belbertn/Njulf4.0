# Automatic game host and typed resources — 2026-09-08

- Source: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, dirty with prior API foundation
  and this implementation of roadmap items 3–4. Existing foundation files and unrelated
  `implementation/RendererFeatureBacklog-20260908.md` preserved; no commit created.
- Hardware: Windows, Ryzen 5 5600H, RTX 3060 Laptop GPU, driver 32.0.16.1062;
  .NET SDK 10.0.203, Development configuration.
- Decision: retain automatic Framework composition, explicit lifecycle/GameTime,
  borrowed typed scene views with independently owned wrappers, typed RGBA8 textures,
  and graphics-completion-based public resource retirement. Existing material compiler,
  generation handles, Vulkan device, shader ABI and render graph retained.
- Build: full Development solution passed, zero warnings/errors. Core, Framework and
  Rendering emit XML documentation. Sample registrations and typed consumers migrated.
- Focused verification: 228 tests passed, zero skips, covering clocks, early lifecycle
  exit/failure, ownership, cloning/upload rollback, material edit transactions, content,
  invalidation, animation and sample consumers. After final generation validation changes,
  109 core tests passed again. A further real GPU retirement test passed with the two
  early lifecycle tests (3/3): remove the only object after recording its draw, observe
  mesh/material/texture still alive, then observe all three handles invalid after completion.
  Vulkan validation reported zero errors.
- Captures: procedural textured and loaded-model examples each presented 123 full-quality
  frames and exited 0; zero Vulkan validation errors. Original tetrahedron at 960x640,
  default rendering quality, directional light 3, fixed exposure 0.01. Visually inspected
  orange textured geometry and blue model against the sky; no image-quality change claimed.
- Iteration failures resolved: legacy raw-handle test assertions and lifetime fixtures;
  model-clone rollback losing access to a partial clone; material ownership accounting
  during replacement; and a native crash from Silk.NET resetting the window before input
  callbacks were unregistered. Game now owns the native reset boundary through its event
  loop. Silk.NET 2.23.0's [default Run implementation](https://github.com/dotnet/Silk.NET/blob/v2.23.0/src/Windowing/Silk.NET.Windowing.Common/WindowExtensions.cs)
  explains the reset ordering; the early-exit regression reproduced and then passed.
- Broader-suite limitation: two unchanged benchmark report test bodies failed because their
  reports omit supported loaded-shader identity: `ActivationVerificationCli_RecomputesFromReportAndSidecarBytes`
  and `PairComparer_AuthenticatesCommonSponzaSidecarAndPath`. These were recorded and excluded
  from the 228-test migration result; no claim that the entire test suite passes.
- Timing: baseline/candidate frame-time and tail-latency comparison not collected. Startup
  logs varied with driver caches and concurrent checks and are not comparative performance
  evidence. The existing unchanged-snapshot zero-allocation behavioral test passes. No
  broad performance campaign, custom-pass API, alternate backend, scene-light migration,
  or richer texture/vertex formats were introduced.
- Storage: pruning inspection reported 3.48 GiB older candidates and 296.58 GiB free.
  Candidates include unrelated investigations, so no broad deletion was performed. This
  task's captures and TRX data are under ignored workspace artifact roots on D:. Retain
  raw captures only through the active investigation and at most two days afterward;
  preserve this compact record and text logs.

## Reproduction

```powershell
dotnet build Njulf.sln -c Development --no-restore
dotnet test Njulf.Tests -c Development --no-build --filter "FullyQualifiedName~FrameworkContractTests|FullyQualifiedName~GameLifecycleIntegrationTests|FullyQualifiedName~GraphicsApiIntegrationTests|FullyQualifiedName~ModelResourceLifetimeTests|FullyQualifiedName~ModelRenderUpload|FullyQualifiedName~MaterialRenderObjectEditTransactionTests|FullyQualifiedName~SceneMaterialOverridePersistenceTests|FullyQualifiedName~SecondarySceneSnapshotTests"
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example procedural --frames 120 --validation --native-inspect --capture artifacts/framework-api-typed/procedural-repro.png
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example model --frames 120 --validation --capture artifacts/framework-api-typed/model-repro.png
```

Use fresh capture paths. Evidence: `artifacts/framework-contracts/framework-contracts.trx`,
`framework-core-final.trx`, `framework-gpu-retirement.trx`; images and compact example logs
under `artifacts/framework-api-typed/`. Public contracts and migration guidance are in
[FrameworkApi.md](../../FrameworkApi.md).
