# Restore realistic bounce light (physical sky energy + alpha-masked transport + adaptation)

## Context

After removing the fake 15% shadow floor, Sponza is far darker than reality. Goal: DDGI simulates real-world light as accurately as possible, performantly — no arbitrary gains.

### What the captures prove (RTX 3060, Exposure 1.0, AcesFitted)

Transport is **energy-consistent at every stage** — no bug is eating bounce:

- Per-ray radiance 0.171 = direct 0.075 + sky 0.048 + bounce 0.047 + emissive 0.0001 (reconciles exactly).
- Blend stores true DDGI irradiance `E = π·Σ(L·cos)/Σcos` (ddgi_simple_blend.comp:204-206), linear, no encoding gamma.
- Gather is the standard visibility-normalized estimator (ddgi_simple_shared.glsl:1182-1184). Ownership 1.0, support 0.995, fallback 0.
- Receiver: final = E·albedo/π; 0.0119 from sampled E 0.187 → effective albedo ~0.20 (plausible).

**The problem is authored energy.** The sky — the only ambient source — is starved:

1. `SampleSponzaGlobalIlluminationProfile.cs:66-68`: `SkyIntensity=0.45`, `DiffuseIntensity=0.10`, `SpecularIntensity=0.25` — legacy dimming hacks predating DDGI ownership composition.
2. Procedural dome (`EnvironmentMapProcessor.ProceduralSky:273`): horizon lum ~0.91, zenith ~0.34, ground ~0.016, cos-weighted average ~0.5.
3. Net sky diffuse fraction ≈ **5%** vs real clear-sky **10–20%** (sun intensity 14 → horizontal irradiance ~10.7; sky delivers ~0.7). Probes see 47% sky (`SimpleDdgiAverageSkyVisibility`), so all shade is starved 3–4×.
4. Alpha-masked geometry (curtains) forced opaque in the trace acceleration structure (audit risk #8) → over-occlusion of bounce and sun visibility.
5. Baked sky sun disc points (-0.3, 0.72, 0.62); actual to-sun is (-0.18, 0.82, -0.54) — skybox sun in the wrong azimuth.
6. Presentation: physically, midday interiors sit 3–5 stops under sunlit stone; eyes adapt, fixed Exposure 1.0 doesn't. AutoExposure pass exists (`RenderSettings.cs:232`, toggle at SampleInputController.cs:587) but the profile pins it off.

User-approved scope: physical sky model + parameter calibration, alpha-masked transport now, auto-exposure for interactive only.

## Implementation

### 0. Reconcile branch state first
Working tree has the earlier shadow-floor fix uncommitted (`SampleLighting.cs`, `ddgi_hit_shading.glsl`, `ShaderBuildTests.cs`) while the user pushed equivalent changes. `git fetch origin claude/flat-brightness-issue-drwmq9`, compare, and either fast-forward onto the remote state or commit the local files if the remote lacks them. All new work continues on this branch.

### 1. Physical procedural sky (`Njulf/Njulf.Rendering/Resources/EnvironmentMapProcessor.cs`)

Rework `ProceduralSky` into a sun-parameterized clear-sky model; thread sun data from the renderer:

- New inputs: `toSunDirection` (normalized `-light.Direction`) and `sunIntensity` (× light color) of the primary shadow-casting directional light (same selection LightManager uses at LightManager.cs:388-433). Fallback to current constants when no directional light exists.
- **Dome calibration**: scale the horizon/zenith gradient (preserving hue) so cos-weighted dome irradiance hits a target clear-sky diffuse fraction ~15%: `E_sky_target = f/(1-f) × E_sun_horizontal` where `E_sun_horizontal = sunIntensityLum × max(toSun.y, 0)`. With sun 14 this lands dome average luminance ≈ 0.6–0.7 (close to current colors at intensity 1.0 — the calibration formalizes it and tracks any authored sun).
- **Sun disc**: place at `toSunDirection` instead of the hardcoded vector. Exclude the disc from `GenerateProceduralSkyIrradianceCubemap` (sun energy is already the analytic directional light — no double count in diffuse IBL). Keep it in the skybox/specular cubemap for visuals; its energy through probe misses is ~1e-4 sr → negligible, acceptable.
- **Ground hemisphere**: replace near-black (0.03) with a ground-albedo model: `L_ground ≈ groundAlbedo × E_global_horizontal/π`, groundAlbedo ~0.2, horizon-blended. Represents sunlit surroundings for downward escape rays.
- **Regeneration**: environment settings are already hashed for rebuild (VulkanRenderer.cs:9570 area); extend that hash with quantized sun direction/intensity (e.g., 0.01 steps) so the sky rebakes when the sun changes materially and never per-frame. Sample scenes have a static sun, so bake cost stays load-time.

### 2. Profile calibration (parameter changes)

- `SampleSponzaGlobalIlluminationProfile.ConfigureReferenceOutput` (lines 66-68): SkyIntensity 0.45→1.0, DiffuseIntensity 0.10→1.0, SpecularIntensity 0.25→1.0. Ownership composition already prevents double counting (diffuse IBL fills only the non-DDGI-owned share; probes' sky misses use SkyIntensity by design — ddgi_hit_shading.glsl:269-273).
- Mirror in `SamplePlazaGlobalIllumination.cs:107-109` for consistency.
- Update tests pinning the old values (`SimpleDdgiVolumeManagerTests.cs:315` SkyIntensity 0.45; any others found by grep for `0.45f`/`0.10f`/`0.25f` in Njulf.Tests referencing environment settings).

### 3. Alpha-masked transport (probe rays + light visibility)

- **CPU**: where DDGI TLAS/BLAS instances are built, stop applying the force-opaque flag to instances whose material is alpha-masked (masked materials are already identified — `MaskedObjectCount` diagnostic exists); leave opaque geometry opaque so the fast path is untouched.
- **GPU**: in the ray-query loops of `ddgi_simple_trace.comp` and `TraceLightVisibility` (ddgi_hit_shading.glsl), handle non-opaque candidate intersections: resolve material + UV for the candidate via the same hit-attribute→material path used for committed hits (`ResolveCompactDdgiAlbedo` / material texture plumbing), sample base-color alpha at a fixed LOD, confirm the intersection only when alpha ≥ the material cutoff. Binary cutoff (no partial transmittance) keeps both loops single-pass.
- **Performance guardrails**: alpha-test only where material textures are available (existing `ShouldSampleDdgiMaterialTextures` cascade gate); add `GlobalIllumination.TransportAlphaTestEnabled` (default true) so cost can be measured/disabled; expected cost is bounded because only masked instances enter the candidate path. Measure against current trace 3.7–3.9 ms.
- Fallback decision (flag during implementation if candidate UV fetch proves unavailable in the visibility path): prefer masked-instances-skip (slight leak) over forced opaque (blocks all light) for the visibility trace only.

### 4. Auto-exposure for the interactive view

- Keep `ApplyValidationOverlay` exactly as-is (AutoExposure off, Exposure 1.0) — deterministic captures unchanged; capture harness already asserts/handles the disabled state (SampleSponzaGiCaptureHarness.cs:310).
- Enable AutoExposure in the interactive Sponza configuration path (in `Configure`/`ConfigureReferenceOutput` if only interactive code calls it, otherwise at the interactive call-site in Program.cs/SampleInputController): sensible clamps (min/max exposure ≈ [0.5, 8], default adaptation speed, mid-gray key). The `[` toggle remains for A/B.

### 5. Shader/test/doc bookkeeping

- Update `ShaderBuildTests.cs` string contracts for changed shader text (trace/visibility loops).
- Extend `EnvironmentMapProcessorTests.cs`: irradiance excludes the sun disc; dome irradiance tracks sun intensity at the target diffuse fraction; ground hemisphere non-black; disc follows the provided sun direction.
- New short doc `Njulf/docs/SkyEnergyCalibration-20260717.md`: the calibration math, measured before/after counters, masked-transport policy, and why no arbitrary gains were added.

## Verification

1. Unit tests (serial, `-m:1` per repo docs): shader-mirror, shader-build, environment processor, volume-manager, profile tests green (6 pre-existing unrelated failures may remain).
2. Shader compilation of all changed GLSL.
3. User-side captures (RTX 3060, same endpoints) — expected movement:
   - `DdgiTraceEnergySkyLuminanceAverage` 0.048 → ~0.15–0.25; bounce term rises with it via feedback.
   - `SimpleDdgiAverageSampledIrradianceLuminance` 0.187 → ~0.5–0.8; `DdgiForwardEstimateFinalDiffuseLuminance` 0.012 → ~0.04–0.07.
   - Shade-to-sunlit brightness ratio from ~1.7% → ~5–10% (real-world range); arcades readable, curtains no longer black occluders.
   - `GpuSimpleDdgiTraceMicroseconds` within ~10–15% of the current 3.7–3.9 ms; no new budget regressions.
4. Interactive sanity: auto-exposure adapts moving from courtyard to arcade; `[` toggle reverts to fixed.

## Delivery

Commit incrementally (sky model, calibration, masked transport, exposure — separable commits) on `claude/flat-brightness-issue-drwmq9`; push with `git push -u origin claude/flat-brightness-issue-drwmq9`. No PR unless requested.