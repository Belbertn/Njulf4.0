# Gameplay API correctness and conveniences — 2026-09-13

- Source: `09be7c7d2a70784bf91e876d9bd6a3eba8b2cc2b`, dirty working tree. Existing
  SphereShooter code, documentation, and lighting milestone changes were preserved.
- Environment: Windows, AMD Ryzen 5 5600H, .NET 10, Debug. CPU behavioral tests and
  bundled OpenAL Soft loopback; no renderer capture or GPU workload.
- Changes: closest-point box/sphere intersections and world-distance rays; valid bind
  globals and procedural pose publication; vector equality and matrix overloads;
  bounded one-shot audio; visual/model physics registration and masks; CPU primitive
  meshes; binding shortcuts and opt-in fixed-step command buffering.
- Defaults: 32 lazy one-shot voices, drop newest when full; one pending press per action;
  deltas accumulate until consumed. Capsule length is the straight Y-axis segment.
- Validation: **112 passed, 0 failed, 0 skipped** in the focused run (1.97 seconds test-run
  wall time). API examples build: **0 errors, 0 warnings** (4.13 seconds). `git diff --check`
  passed. Tests cover observable geometry, pose/skin/revision output, audio playback and
  reuse/disposal, physics transform/lifetime behavior, mesh winding/UV/tangent data and
  processing compatibility, plus input suppression/persistence/rebinding regressions.
- Baseline/candidate performance timings and tail latency: not measured; this is a
  correctness/convenience change, with no performance improvement claim. No bulky
  captures or isolated builds were created.
- Initial findings: the input project requires public XML documentation; added comments.
  The new scale overload makes target-typed `CreateScale(new(...))` ambiguous; updated
  the three existing callers to scalar/component syntax and documented migration.
- Wider-filter limitation: an initial `~AnimationTests` filter also selected
  `SampleBenchmarkSponzaSceneAnimationTests`. Its
  `ActivationVerificationCli_RecomputesFromReportAndSidecarBytes` and
  `PairComparer_AuthenticatesCommonSponzaSidecarAndPath` cases failed on report validation;
  the latter explicitly reports missing/unsupported loaded-shader identity. These report
  fixtures were not changed. The final filter identifies the intended animation fixture
  precisely. No full solution or interactive renderer/audio-device qualification was run.
- Decision: retain the implementation. API examples demonstrate buffered fixed-step input,
  one-shot audio, primitive box data, and static/dynamic/kinematic registration helpers.
- Storage: `tools/prune-local-artifacts.ps1` dry-run identified 184 eligible payloads
  totaling 0.22 GiB; D: had 257.13 GiB free. No deletion was applied because candidates
  include lighting data associated with other work in this dirty tree. Current test/build
  logs are compact text in `.tmp/gameplay-api-*.log`.

## Reproduce

From the repository workspace:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --no-restore --filter "FullyQualifiedName~Njulf.Tests.GeometryConvenienceTests.|FullyQualifiedName~Njulf.Tests.AnimationTests.|FullyQualifiedName~Njulf.Tests.PrimitiveMeshTests.|FullyQualifiedName~Njulf.Tests.AudioTests.|FullyQualifiedName~Njulf.Tests.PhysicsTests.|FullyQualifiedName~Njulf.Tests.PhysicsGeometryTests.|FullyQualifiedName~Njulf.Tests.Input|FullyQualifiedName~Njulf.Tests.Matrix4x4Tests." --logger "console;verbosity=normal" -v quiet
dotnet build Njulf.ApiExamples/Njulf.ApiExamples.csproj --no-restore -v quiet
git diff --check
```

See [math and primitives](../../MathAndGeometry.md), [procedural animation](../../ProceduralAnimation.md),
[audio](../../Audio.md), [physics](../../Physics.md), and [input](../../GameplayInput.md) for contracts and usage.
