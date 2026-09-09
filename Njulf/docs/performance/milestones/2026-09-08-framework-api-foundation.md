# Framework API foundation validation — 2026-09-08

- Source: `7ce858e642b7937789b33a1e3ab9978792b8d15b`, dirty with this API,
  examples, documentation and focused tests. Unrelated untracked
  `implementation/RendererFeatureBacklog-20260908.md` was preserved.
- Hardware: Windows, AMD Ryzen 5 5600H, NVIDIA GeForce RTX 3060 Laptop GPU,
  driver `32.0.16.1062`; .NET SDK 10.0.203, Development configuration.
- Workload: original four-vertex tetrahedron, loaded glTF and procedural paths,
  960x640, existing renderer quality defaults, directional light intensity 3,
  fixed exposure 0.01, standard Vulkan validation with fail-on-error.
- Build: rendering and API example builds passed with zero warnings/errors.
  Available XML documentation is emitted for assembly-consumer IntelliSense;
  existing incomplete/ambiguous/misplaced XML-comment warnings are suppressed.
- Focused tests: 22 passed, zero failed/skipped. Covers resource survival and final
  release, creation rollback, wrong owner/thread, initialization/shutdown guards,
  actual Vulkan device/buffer slices and existing mesh/model lifetime contracts.
- Final captures: both examples presented 123 full-quality frames, exited with
  code 0 and reported zero Vulkan validation errors. Visually checked orange
  procedural geometry and blue loaded geometry against the sky. Captures wait
  at least 120 full-quality frames and complete through existing readback handling.
- Iteration findings: the initial three-minute startup timeout expired during
  driver pipeline compilation; throwing from the window callback also exposed an
  existing Silk window-reset error during unwind. The example now closes normally
  on timeout and reports failure after the run, with a ten-minute allowance.
  Screenshot permission was initially omitted and is now explicitly enabled only
  for capture requests. Default exposure washed out the fixture; waiting 120 frames
  did not fix it, so the example uses fixed exposure 0.01. One intermediate run
  remained alive after printing successful completion and was stopped; both final
  runs exited normally. One intermediate rebuild encountered a running example's
  DLL lock; final validation used an isolated output on D:.
- Timing: no before/after performance benchmark or tail-latency comparison was
  performed. This changes resource creation/access and example setup, not frame
  orchestration. Startup/hitch logs from these runs are diagnostics, not performance
  claims; driver caches changed during validation.
- Decision: retain the API foundation; custom pass execution remains deferred.
  This is a small-scene ownership/access smoke, not a rendering-quality campaign,
  multi-GPU qualification or proof of the complete future framework API.
- Storage: inspected with `tools/prune-local-artifacts.ps1` (PowerShell 7);
  approximately 297 GiB free, 3.32 GiB older candidate payloads. No broad cleanup
  needed. This task's logs, temporary build and images are under ignored
  `artifacts/framework-api/`; retain final captures at most two days. Compact
  findings are preserved here.

## Reproduction

```powershell
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj -c Development
dotnet test Njulf.Tests/Njulf.Tests.csproj -c Development --filter "FullyQualifiedName~GraphicsApiIntegrationTests|FullyQualifiedName~ModelResourceLifetimeTests|FullyQualifiedName~MeshLifetimeTests"
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example procedural --frames 120 --validation --native-inspect --capture artifacts/framework-api/procedural-repro.png
dotnet run --project Njulf.ApiExamples -c Development --no-build -- --example model --frames 120 --validation --capture artifacts/framework-api/model-repro.png
```

Use new capture paths for reruns. Final recorded runs used the same build with
`-o artifacts/framework-api/final-bin`, then executed that directory's
`Njulf.ApiExamples.dll` using `dotnet` with the arguments above. Compact run logs
are `artifacts/framework-api/procedural-final.log` and `model-final.log`.
