# Reflection qualification

The deterministic reflection gate recooks Amazon Bistro with its explicit
material convention, verifies the cooked package, runs the 720-frame
`HybridRayQueryAb` hardware sequence at DDGI High, authenticates every captured
beauty frame, and evaluates the reflection path independently of Bistro's
broader DDGI scrolling and transport-tail checks.

From the repository root:

```powershell
./tools/reflection-qualification.ps1 `
  -OutputDirectory artifacts/reflection-qualification/my-run
```

Use a new or empty output directory. `-SkipBuild` and `-SkipCook` are intended
only for local iteration after the corresponding steps have already succeeded.
An existing capture can be reauthenticated without launching Vulkan:

```powershell
./tools/reflection-qualification.ps1 `
  -OutputDirectory artifacts/reflection-qualification/my-run `
  -AnalyzeExisting
```

The capture contract is `bistro-quality-run/v9`; the authoritative scoped
result is `reflection-qualification.json` with contract
`bistro-reflection-qualification/v2`. A passing run proves all of the
following:

- the model package is format 1.4 and matches the Amazon Bistro import contract;
- all four Bistro window materials remain non-metallic thin glass, including a
  sharp clear-glass profile;
- the stable reflection windows cover both sorted transparency and weighted
  OIT, and each mode records useful transparent SSR and admitted ray-query
  work;
- transparent SSR obeys its sample budget in every accepted frame:
  eligible equals admitted plus rejected, actual samples do not exceed the
  reservation, reservations do not exceed the configured budget, and hits do
  not exceed admissions;
- stable A/B off windows record no ray-query work, while the on window records
  exact requests equal to admitted plus rejected, hits, misses, and GPU time
  with no overflow;
- SSR, DDGI base radiance, resolve, temporal, spatial, and composite stages all
  perform measured GPU work;
- Bistro uses no manual reflection probe and records no probe fallback;
- the ten deterministic beauty frames exist and match the sizes and SHA-256
  identities stored in the source report.

`bistro-quality-run.json` may still report a failure for an unrelated DDGI
scrolling or tail-certification invariant. Those failures are retained under
`BistroRunFailures` in the scoped report; they do not erase valid reflection
evidence.

For material-level diagnosis, select the `RoughnessInputs` reflection debug
view (or benchmark variant `reflection-roughness-inputs`). Red is physical BRDF
roughness, green is conservative scheduling roughness, and blue is their
positive difference. A sharp material may have a green scheduling footprint,
but it must stay dark in red; physical roughness is what shades the reflection.
