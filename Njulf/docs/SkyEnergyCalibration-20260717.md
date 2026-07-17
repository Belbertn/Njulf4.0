# DDGI sky-energy calibration

The procedural environment is coupled to the first shadow-casting directional
light. Its normalized `-Light.Direction` positions the visible sun disc and its
linear `Light.Color * Light.Intensity` defines the direct-sun reference energy.

The sky dome is calibrated to a clear-day diffuse fraction rather than an
artist-controlled indirect multiplier:

`E_sky = f / (1 - f) * E_sun_horizontal`, where `f = 0.15` and
`E_sun_horizontal = luminance(L_sun) * max(dot(toSun, up), 0)`.

The environment’s diffuse irradiance cubemap excludes the sun disc. Direct sun
is already evaluated by the directional-light path, so including it would
double-count solar energy for DDGI probe misses and forward IBL. The disc
remains in the skybox/prefiltered map for visual and specular appearance.

The lower hemisphere uses a neutral ground-albedo model (`0.20`) lit by the
global horizontal irradiance. It is continuous with the horizon and prevents
downward escape rays from sampling an artificial black void.

Sponza and Plaza use unity environment channel intensities. Probe ownership
prevents diffuse IBL double counting; indirect transport remains at gain one.
Interactive Sponza enables bounded auto exposure (`0.5..8.0`), while the
validation overlay pins exposure to one and disables adaptation.

Alpha-masked DDGI transport evaluates the glTF base-color alpha/cutoff at
ray-query candidates. Opaque instances retain `ForceOpaqueBitKhr`; only mask
instances pay the texture lookup. `DdgiAlphaMaskedTransportEnabled` is a
persisted A/B switch and defaults to enabled.

## Capture counters

The locked RTX 3060 baseline used to motivate this calibration was:

| Counter | Before | Calibrated target |
| --- | ---: | ---: |
| `DdgiTraceEnergySkyLuminanceAverage` | 0.048 | 0.15–0.25 |
| `SimpleDdgiAverageSampledIrradianceLuminance` | 0.187 | 0.5–0.8 |
| `DdgiForwardEstimateFinalDiffuseLuminance` | 0.012 | 0.04–0.07 |
| Shade/sunlit brightness ratio | 1.7% | 5–10% |
| `GpuSimpleDdgiTraceMicroseconds` | 3.7–3.9 ms | within +10–15% |

The calibrated values must be recorded from a fresh locked capture before
claiming verification. They are intentionally not fabricated in source or
documentation; the table defines the reproducible before/after comparison.
