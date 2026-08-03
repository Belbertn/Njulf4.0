# DDGI Bounce Energy and Curtain Thin-Transmission Fix Plan

Date: 2026-07-31

Status: Proposed

Scope: Simple DDGI source injection, recursive diffuse transport, thin-cloth transmission,
ray-query visibility, far-field parity, diagnostics, Sponza curtain authoring, and production
qualification

## 1. Target outcome

Fix the low-bounce-lighting report without hiding it behind an indirect-intensity multiplier, and
make the Sponza curtains participate in physically plausible diffuse light transport.

The work has two independently gated tracks:

1. Prove whether the existing opaque diffuse bounce solver loses energy relative to a controlled
   reference. Change the general solver only if that reference demonstrates a numerical deficit.
2. Add an explicit, energy-conserving thin-surface transport policy for cloth. Raster opacity and GI
   transmission must remain separate concepts.

The target is reached when:

- controlled diffuse scenes match a numeric oracle;
- opaque materials retain their current behavior when thin transmission is disabled;
- authored curtain fabric reflects light on its incident side and diffusely transmits attenuated,
  tinted light to the opposite side;
- receivers behind the curtain receive correctly attenuated direct and indirect illumination;
- the new transport remains stable across detailed and far-field geometry;
- diagnostics attribute reflected and transmitted energy separately;
- memory and GPU cost remain within the budgets defined in this plan.

Full refraction, rough glass, thick-volume absorption, caustics, and arbitrary order-independent
transparent GI are explicitly out of scope.

## 2. Evidence and current diagnosis

### 2.1 Capture identity

The supplied performance snapshots identify commit `0e059ae`, while the current workspace is at
`792972d` (`Decals and transparency ddgi. Better`). The first implementation task is therefore a
fresh capture from the current commit. No baseline from the supplied snapshots may be promoted
without recording this mismatch.

### 2.2 Gather and convergence are healthy in the supplied capture

The latest supplied capture reports:

- `15,346 / 15,346` probes fully converged;
- no convergence wave pending;
- zero source-cache misses;
- spatial coverage approximately `0.99995`;
- support/data confidence approximately `0.99967`;
- visibility confidence approximately `0.99517`;
- effective DDGI contribution approximately `0.99981`;
- leak attenuation equal to `1.0`.

The fixed support, visibility, directional-support, and gather debug views agree with those
counters. The implementation must preserve this path unless a controlled test proves a defect.
Increasing transport generations is not a first-line fix because the field has already converged.

### 2.3 The converged field contains little recursive energy

The latest capture reports approximately:

- source luminance: `0.35982`;
- recursive bounce luminance: `0.02852`;
- total luminance: `0.38835`;
- recursive addition relative to source: approximately `7.9%`;
- surface trace hits: `374`;
- zero-radiance surface hits: `325`;
- direct-lit surface hits: `49`;
- shadow visibility rays: `318`;
- shadow-occluded rays: `269`;
- near shadow hits: `36`;
- local lights: `0`;
- emissive hits: `0`.

This points to source starvation or physically dark/shadowed hit materials, not gather suppression.
The zero-radiance-hit count is diagnostic evidence, not by itself proof of a bug: a shadowed hit is
allowed to have zero source radiance. Controlled reference scenes must distinguish expected zeros
from incorrect source loss.

### 2.4 Curtain materials cannot currently transmit bounce

The Sponza curtain glTF contains four double-sided, opaque materials. It has no authored glTF
transmission or volume extension and its texture alpha is effectively opaque. The fabric is
therefore included as double-sided triangle geometry and behaves as an opaque diffuse reflector.

The cooked linear base-color means have luminance around `0.08-0.10`. After metallic and dielectric
Fresnel energy removal, approximate diffuse-reflectance luminance is only around `0.06-0.075`.
Weak reflected color bleed is physically plausible for those authored values.

Changing the curtain to ordinary `BLEND` is not a fix. `ResolveGeometryPolicy` in
`Njulf.Rendering/Resources/AccelerationStructureManager.cs` explicitly excludes blended materials
from DDGI ray-query geometry. A blended curtain would cease to act as a DDGI hit, blocker, or bounce
surface.

### 2.5 Existing transmission support is deliberately incomplete

`GiTransmissionPolicy` already contains `ThinSurface` and `Volume`, but imported materials with a
positive transmission factor are currently mapped to `Unsupported` by
`ModelRenderUploadService`. The material compiler can remove transmission energy from the opaque
diffuse lobe, but Simple DDGI does not transport that energy to the opposite side.

The persistent V2 source cache stores source radiance, distance, ray direction, one normal, reflected
diffuse reflectance, material AO, and generation flags. It has no transmitted reflectance or
thin-surface policy bit.

## 3. Normative transport semantics

### 3.1 Separate raster compositing from GI transport

`MaterialRenderMode` controls raster coverage and compositing. `GiTransmissionPolicy` controls
physical light transport. A material may therefore be:

- raster `OPAQUE` plus GI `ThinSurface`;
- raster `MASK` plus GI `ThinSurface`;
- raster `BLEND` plus no physical GI transmission;
- raster `BLEND` plus explicit GI `ThinSurface`, if the narrowly scoped acceleration-structure
  exception in this plan is implemented.

Ordinary blend alpha must never be interpreted as physical transmittance.

### 3.2 Thin diffuse sheet model

For a receiver direction on one side of a zero-thickness sheet:

```text
Lout =
    reflectedIrradiance * diffuseReflectance / PI
  + oppositeSideIrradiance * diffuseTransmittance / PI
  + reflectedOrTransmittedDirectLighting
  + emission
```

The compiler must enforce, per color channel:

```text
diffuseReflectance + diffuseTransmittance
    <= available non-metallic, non-specular energy
```

The canonical transport response must account for metallic and dielectric Fresnel removal before
allocating diffuse reflection and transmission. A thin surface must not create diffuse energy that
the equivalent opaque material did not have available.

### 3.3 Side and normal ownership

The surface contract must preserve:

- a canonical geometric sheet normal that identifies its two authored sides;
- a face-oriented geometric normal for offsets and the current outgoing side;
- a shading normal for the local reflected or transmitted lobe;
- the committed-hit front-face state.

Reflection gathers incident energy from the outgoing side. Transmission gathers incident energy
from the opposite side. Offsets must move onto the hemisphere being sampled, not always along the
authored normal.

### 3.4 Path ownership

A sheet hit is a shading event. For the same path, the implementation must choose the diffuse BTDF
at the hit and must not also continue the source ray through the sheet. Doing both would count the
same transmission path twice.

Transmittance-aware shadow traversal is separate: it allows a direct-light visibility ray to reach a
receiver through one or more thin sheets. It does not replace the sheet's own reflected/transmitted
surface response.

## 4. Phase 0 — Produce a current, locked baseline

### Tasks

1. Run the deterministic Sponza capture harness from current HEAD.
2. Record in the manifest:
   - Git commit;
   - shader fingerprint;
   - material/cook fingerprint;
   - camera and light profile;
   - DDGI quality settings;
   - source-cache generation;
   - warm-up and capture frame counts.
3. Capture both fixed endpoints and the existing required debug outputs:
   - beauty;
   - direct-only;
   - final indirect;
   - source-cache radiance;
   - sampled irradiance;
   - final diffuse;
   - spatial coverage;
   - support and data confidence;
   - directional support;
   - visibility;
   - ownership and fallback;
   - probe state and update reasons.
4. Add three curtain-specific world-space receiver ROIs:
   - lit-side curtain/floor patch;
   - shadowed-side receiver immediately behind the curtain;
   - adjacent wall/floor patch expected to show colored bounce.
5. Preserve the existing outdoor reference patch so exposure or sun changes cannot masquerade as
   transport changes.
6. Produce an opaque-curtain control with the same camera, lighting, warm-up, and random seed.
7. Run the capture twice and establish repeatability tolerances before accepting either run as the
   baseline.

### Files

- `NjulfHelloGame/SampleSponzaGiCaptureHarness.cs`
- `NjulfHelloGame/SampleSponzaGiCaptureHarnessTests.cs`
- `NjulfHelloGame/SampleDiagnosticsReporter.cs`
- `Plans/Baselines/RealtimeGiClosure-20260715/`

### Exit gate

Two current-HEAD runs have matching fingerprints and agree within the declared luminance,
chromaticity, counter, and timing tolerances.

## 5. Phase 1 — Establish an opaque diffuse-bounce oracle

General bounce must be tested independently of the curtain asset.

### Required scenes

1. **Constant-radiance Lambertian plane**
   - constant incident radiance;
   - known irradiance integral;
   - validates that irradiance storage and `reflectance / PI` are applied exactly once.
2. **White diffuse enclosure**
   - material reflectance variants `0.2`, `0.5`, and `0.8`;
   - validates first and recursive bounce magnitude.
3. **Two-color enclosure**
   - independent red and green reflectors;
   - validates RGB propagation and color bleeding.
4. **Twenty-cell propagation chain**
   - GPU equivalent of the existing analytic Jacobi-chain convergence test;
   - validates distant propagation after the minimum generation count.
5. **Dark Sponza-like material enclosure**
   - reflectance around `0.06-0.08`;
   - demonstrates the physically expected low-bounce range for the curtain asset.
6. **Shadow/source test**
   - identical geometry with light visibility enabled and forced unoccluded;
   - attributes loss to source visibility rather than recursive propagation.

### Reference

Implement a CPU diffuse reference or analytic matrix solution using the same canonical material
reflectance. Record per generation:

- source radiance;
- recursive radiance;
- total radiance;
- convergence residual;
- expected fixed point;
- relative RGB and luminance error.

### Decision gate

- If the converged GPU result is within `5-10%` of the reference, retain the current general
  bounce equation, convergence policy, gather weights, and global indirect intensity.
- If it is below the reference, fix the first failing stage in this order:
  1. direct/source-cache light injection;
  2. shadow-query false occlusion;
  3. geometric and shading-normal orientation;
  4. source-ray distribution and estimator variance;
  5. hit-position offset;
  6. cache packing/decoding;
  7. recursive gather ownership;
  8. convergence only if the earlier stages match.

Increasing `IndirectIntensity`, removing `/ PI`, or raising transport generations without oracle
evidence is prohibited.

### Files

- `Njulf.Shaders/gi_material_transport.glsl`
- `Njulf.Shaders/ddgi_simple_trace.comp`
- `Njulf.Shaders/ddgi_simple_transport.comp`
- `Njulf.Shaders/ddgi_simple_shared.glsl`
- `Njulf.Tests/SimpleDdgiBounceConvergenceTests.cs`
- `Njulf.Tests/SimpleDdgiShaderMirrorTests.cs`
- `NjulfHelloGame/SampleGlobalIlluminationValidation.cs`

## 6. Phase 2 — Add an authoritative thin-surface material contract

### Authored fields

Add or fully activate:

```text
GiTransmissionPolicy
ThinTransmissionFactor
ThinTransmissionTint
ThinTransmissionTexture
```

The exact naming may use the existing general transmission fields if their semantics are made
unambiguous and the policy remains authoritative.

### Import policy

1. Existing `KHR_materials_transmission` must not automatically become `ThinSurface`; it may
   represent glass.
2. Require explicit renderer-owned opt-in using one of:
   - a Njulf glTF material extra;
   - a scene material override;
   - an editor-authored material setting.
3. A positive transmission factor without a supported policy remains `Unsupported`.
4. A zero factor normalizes to `None` unless the authoring layer must retain the dormant policy for
   editing.
5. Only curtain fabric primitives receive the policy. Curtain rods, holders, and metal fittings
   remain opaque.

### Derived transport

The material compiler must produce:

- canonical reflected diffuse reflectance;
- canonical transmitted diffuse reflectance;
- thin-surface policy flag;
- detailed texture-sampling requirement;
- compact/far-field statistics;
- energy-clamp diagnostics.

Near cascades may sample detailed transmission textures. Compact and far-field paths must preserve
the same mean energy, using validated texture statistics rather than an arbitrary texel.

### Change propagation

A transmission policy, factor, tint, texture, UV transform, or relevant base material change must
invalidate:

- diffuse transport profiles;
- material GPU data;
- source transport cache;
- relevant acceleration-structure policy/signature;
- far-field material payload and dirty voxels;
- texture dependencies and cooked artifact hash.

### Files

- `Njulf.Rendering/Data/MaterialTransportContracts.cs`
- `Njulf.Rendering/Data/ModelMaterial.cs`
- `Njulf.Rendering/Resources/ModelRenderUploadService.cs`
- `Njulf.Rendering/Resources/MaterialTransportCompiler.cs`
- `Njulf.Rendering/Resources/MaterialDefinitionV1Adapter.cs`
- glTF converter and cooked-asset material serialization
- material editor and scene override definitions

### Exit gate

A thin curtain material survives import, cooking, serialization, scene override, editor mutation,
and runtime upload without being classified as `Unsupported`.

## 7. Phase 3 — Extend the shader surface contract

Extend `GiSurfaceSample` with:

- canonical sheet normal;
- reflected diffuse reflectance;
- transmitted diffuse reflectance;
- thin-surface flag;
- any side/orientation bit required to reproduce committed-hit behavior.

Detailed and compact hit resolution must calculate the same mean response.

### Direct lighting

For each light:

- light and outgoing direction on the same side use the reflected diffuse lobe;
- light on the opposite side uses the transmitted diffuse lobe;
- shadow-ray origin is offset toward the light's side;
- single-sided material policy remains authoritative;
- alpha coverage is resolved before the transport response;
- emission remains independent of reflection and transmission.

### Required unit cases

- front and back hit symmetry for double-sided cloth;
- `T = 0` exact opaque behavior;
- `R = 0, T > 0` opposite-side response only;
- black transmission tint;
- white tint at the energy limit;
- fully metallic material;
- dielectric F0 variation;
- alpha-masked sheet edges;
- invalid/nonfinite input normalization.

### Files

- `Njulf.Shaders/gi_material_transport.glsl`
- common DDGI material hit-resolution shader includes
- `Njulf.Rendering/Data/GiMaterialReferenceEvaluator.cs`
- material path-conformance tests

### Exit gate

CPU and shader-mirror evaluations agree for both sides of the sheet, and all passive cases satisfy
the component-wise energy budget.

## 8. Phase 4 — Extend the V2 source cache and recursive solver

### Cache ABI

The current `GPUSimpleDdgiTransportRayCache` is 32 bytes and the shader cache stride is eight words.
Add:

- packed transmitted diffuse reflectance/tint;
- thin-surface flag;
- stable canonical-normal orientation.

A nine-word/36-byte layout is the straightforward first implementation. At approximately
`15,368 probes * 128 rays`, this increases cache memory by about `7.5 MiB`. If a smaller packing is
chosen, it must not overload a field with unrelated semantics without explicit ABI tests.

Update:

- `GPUSimpleDdgiTransportRayCache`;
- shader stride and read/write helpers;
- allocation and residency calculations;
- structure size/layout assertions;
- cache generation or ABI version;
- debug buffer decoders;
- performance snapshot cache-byte reporting.

### Solver

For a thin hit, orient the sheet normal toward the outgoing/probe side and gather:

```text
E_reflect = solver gather from the outgoing side
E_transmit = solver gather from the opposite side

L_bounce =
    EvaluateGiDiffuseFromIrradiance(E_reflect, reflectedReflectance)
  + EvaluateGiDiffuseFromIrradiance(E_transmit, transmittedReflectance)
```

Use independently sided offsets to prevent both gathers from sampling the same side. Preserve the
existing field-ownership rule so environment energy is represented once.

The recursive convergence residual must include combined reflected and transmitted RGB. A material
transmission edit must start a new source cohort and propagation wave.

### Files

- `Njulf.Rendering/Data/GPUStructs.cs`
- `Njulf.Shaders/ddgi_simple_shared.glsl`
- `Njulf.Shaders/ddgi_simple_trace.comp`
- `Njulf.Shaders/ddgi_simple_transport.comp`
- `Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs`
- cache layout, allocation, and convergence tests

### Exit gate

A thin sheet inserted into the numeric propagation scene matches the CPU fixed point within the
Phase 1 tolerance, with no cache miss, stale generation, nonfinite value, or premature retirement.

## 9. Phase 5 — Define acceleration-structure and shadow semantics

### DDGI geometry policy

Preferred shipping policy:

- raster `OPAQUE` plus GI `ThinSurface`: included;
- raster `MASK` plus GI `ThinSurface`: included with alpha candidate testing;
- generic raster `BLEND`: excluded;
- `BLEND` plus explicit GI `ThinSurface`: included only if blended curtain rendering is required;
- geometry decals: excluded;
- unsupported volume/glass: retain its explicit fallback or exclusion policy.

`ResolveGeometryPolicy` must receive enough material transport information to make this distinction.
Thin surfaces must not use `ForceOpaqueBitKhr` if candidate material evaluation is required.
Double-sided instance flags must continue to disable facing cull where appropriate.

### Colored transmittance shadows

Direct-light visibility through cloth requires bounded ray-query traversal that accumulates:

```text
visibilityRgb *= thinSurfaceTransmittance
```

Policy:

- alpha-mask rejection continues traversal;
- thin-surface acceptance multiplies colored visibility and continues;
- opaque acceptance terminates with zero visibility;
- stop after a configurable maximum, initially eight thin layers;
- terminate early below `0.01` remaining maximum-channel transmittance;
- record layer-limit and low-transmittance termination diagnostics;
- use a bounded candidate count and fail closed on overflow.

Visibility moments may continue treating the sheet as a geometric distance surface in the first
release to preserve leak prevention. Weighted-transparent moments are a separate future feature.
Document this approximation and validate that it does not create a near/far leak seam.

### Files

- `Njulf.Rendering/Resources/AccelerationStructureManager.cs`
- DDGI candidate-opacity/material helpers
- direct-light shadow ray-query shaders
- acceleration-structure policy tests

### Exit gate

An isolated receiver behind one or more thin sheets receives the expected multiplicatively
attenuated, tinted direct light, while the opaque control remains occluded.

## 10. Phase 6 — Preserve far-field parity

Audit far-field voxel material transport.

Preferred implementation:

- store a thin-surface flag and compact transmitted reflectance/tint in the far-field payload;
- use the same reflected/transmitted energy split as detailed TLAS hits;
- preserve mean energy across the detailed-to-compact transition.

If this cannot ship in the first increment:

- explicitly restrict thin transmission to detailed TLAS rings;
- ensure thin geometry cannot silently become an opaque far-field blocker at the transition;
- expose the limitation in diagnostics;
- add a camera/cascade movement test before enabling the feature in production Sponza.

### Exit gate

Moving the camera or curtain across detailed/far-field ownership boundaries does not switch
transmission on or off, create a luminance step above tolerance, or produce a colored light leak.

## 11. Phase 7 — Add transport diagnostics

### Counters

Add:

- detailed thin-surface hit count;
- compact thin-surface hit count;
- far-field thin-surface hit count;
- reflected direct luminance;
- transmitted direct luminance;
- reflected recursive luminance;
- transmitted recursive luminance;
- colored shadow-transmission ray count;
- total and maximum thin layers traversed;
- thin-layer-limit termination count;
- low-transmittance early termination count;
- zero-radiance hits split by opaque/thin/unsupported material class;
- unsupported-transmission hit count;
- transmission energy-clamp count;
- invalid/nonfinite transmission count.

### Debug views

Add or extend views for:

- GI surface policy;
- reflected source and recursive energy;
- transmitted source and recursive energy;
- combined transport;
- transmission tint/factor;
- shadow transmittance;
- near/far thin-transport ownership.

### Snapshot and ROI metrics

Include the counters and mean luminances in performance JSON. For the curtain ROIs, report:

- beauty and direct-only mean luminance;
- indirect delta;
- reflected/transmitted source and bounce luminance;
- RGB chromaticity;
- opaque-control ratio;
- invalid pixel ratio.

The diagnostics must distinguish:

1. no energy reached the curtain;
2. the curtain was hit but had zero transmission;
3. transmitted energy was generated but lost during propagation;
4. propagation reached the receiver but final composition suppressed it.

## 12. Phase 8 — Author and calibrate the Sponza curtain

After the reference scenes pass:

1. Apply `ThinSurface` only to fabric primitives.
2. Keep curtain hardware opaque.
3. Derive transmission tint from the fabric texture/statistics or author a dedicated texture.
4. Start with a conservative transmission factor.
5. Calibrate using the locked lit-side, shadowed-side, and adjacent-bounce ROIs.
6. Keep reflected plus transmitted energy inside the compiler budget.
7. Compare against the opaque control and outdoor reference in every iteration.

Do not:

- change the material to generic blend as a GI workaround;
- increase global indirect intensity;
- remove the Lambertian `/ PI`;
- brighten base color solely to hide a transport defect;
- tune exposure or the directional light between control and candidate captures.

The final transmission factor is an authored asset value, not a renderer-wide compensation.

## 13. Automated test matrix

### Material and import

- thin policy import/override/cook round trip;
- unsupported transmission remains unsupported without opt-in;
- zero-factor normalization;
- texture binding, UV transform, and statistics;
- editor change classification and cache invalidation;
- curtain fabric/hardware primitive selection.

### Transport math

- `T = 0` matches opaque transport;
- `R = 0, T > 0` transports from the opposite side only;
- component-wise energy conservation;
- front/back symmetry;
- black and colored tint;
- metallic and dielectric Fresnel interaction;
- alpha-mask coverage;
- direct, recursive, and emission path separation.

### Cache and convergence

- CPU/GPU size and offset agreement;
- packed reflectance/transmittance round-trip tolerance;
- stale-generation rejection;
- material-edit invalidation;
- analytic thin-sheet chain;
- all-probe convergence after distant transmitted energy arrives;
- zero cache misses after steady-state warm-up.

### Acceleration structure and shadows

- opaque thin surface included;
- masked thin surface included with alpha testing;
- generic blend excluded;
- explicitly supported blended thin surface included;
- decals excluded;
- double-sided flags preserved;
- one-sheet and multi-sheet colored attenuation;
- opaque termination;
- layer limit and low-transmittance early-out.

### Runtime and captures

- controlled oracle scenes;
- opaque Sponza control;
- thin-curtain Sponza;
- detailed/far-field transition;
- stationary convergence;
- camera traversal;
- no NaN, Inf, negative radiance, overflow, or invalid pixel.

## 14. Production acceptance gates

### Correctness

- controlled diffuse and thin-sheet scenes are within `5-10%` of the numeric reference;
- `T = 0` opaque control is unchanged within numeric/capture tolerance;
- reflected plus transmitted passive diffuse energy exceeds available energy by no more than `1%`;
- transmitted ROI chromaticity matches the authored tint within `10-15%`;
- the receiver behind the curtain is at least `3x` the opaque-control indirect luminance, unless the
  numeric reference for the final authored factor predicts a smaller ratio;
- the adjacent bounce ROI is above the opaque control but below the energy upper bound;
- outdoor reference luminance remains within baseline tolerance;
- no new wall, floor, thin-wall, or cascade-boundary leaks.

### DDGI health

- all required probes converge;
- no convergence wave remains pending at capture;
- zero source-cache misses in the qualified frame;
- no stale-generation, nonfinite, energy-clamp-overflow, or cache-layout diagnostic;
- support, data confidence, directional support, visibility, effective contribution, and fallback
  remain within their existing gates;
- unsupported transmission count is zero for the qualified curtain materials.

### Performance and memory

- scenes without thin materials remain within `2%` of baseline DDGI GPU time;
- provisional curtain-scene DDGI trace/transport overhead is at most `15%`;
- cache growth remains near the predicted `7.5 MiB` for the measured configuration;
- memory residency remains inside the configured DDGI budget;
- colored shadow traversal does not exceed the candidate/layer cap in the qualified scene;
- production timing uses non-debug beauty frames only.

Existing absolute luminance thresholds in `SampleDdgiProductionGate` remain regression signals, but
the controlled reference-relative gates are authoritative for this fix. The old Sponza thresholds
must not be retuned until the oracle and current-HEAD baseline exist.

## 15. Rollout order

1. Current-HEAD capture, curtain ROIs, and opaque control.
2. Controlled diffuse oracle and source-loss attribution.
3. General diffuse correction only if the oracle proves a deficit.
4. Authored/imported/cooked thin-surface contract.
5. CPU and shader surface response.
6. V2 source-cache ABI and two-sided recursive transport.
7. Acceleration-structure policy and colored transmittance shadows.
8. Far-field parity.
9. Diagnostics and debug views.
10. Curtain asset authoring and calibration.
11. Full test suite, capture qualification, and performance/memory qualification.
12. Approved baseline update and feature-default decision.

Keep the new thin-transport path behind a renderer setting during implementation. Run opaque versus
thin A/B captures from identical seeds and settings. Remove the feature flag only after all
correctness, leak, convergence, and budget gates pass.

## 16. Primary risks and controls

| Risk | Control |
| --- | --- |
| Double-counted transmitted paths | Shade transmission at the sheet; do not also continue the same source ray. |
| Wrong-side gathers | Preserve canonical and face-oriented normals; test front and back separately. |
| Colored shadow cost explosion | Bound thin layers and candidates; early-out on low transmittance. |
| Generic blend accidentally becomes physical glass | Require explicit `ThinSurface` policy. |
| Cache ABI mismatch | Version the layout and assert CPU/shader stride and offsets. |
| Material edits reuse stale source energy | Invalidate source generation and propagation wave. |
| Far-field curtain becomes opaque | Store thin policy in far field or explicitly restrict ownership without a seam. |
| Renderer gain hides an asset or source problem | Oracle-first gate; prohibit global intensity and `/ PI` workarounds. |
| Dark curtain reflectance is mistaken for solver failure | Retain the dark-material reference scene and opaque Sponza control. |
| Diagnostics change production cost | Compile/execute detailed attribution only in diagnostic captures where possible. |

## 17. Completion definition

The issue is complete only when the implementation demonstrates all of the following in one
qualified evidence set:

1. The opaque diffuse solver agrees with its numeric reference or has been corrected to do so.
2. Curtain fabric is represented by an explicit thin-surface transport policy.
3. Direct and recursive reflected/transmitted terms are separately measurable.
4. Light reaches the curtain's opposite side with authored attenuation and tint.
5. Opaque controls, visibility, support, convergence, and leak prevention do not regress.
6. Detailed and far-field ownership remain visually and numerically consistent.
7. Automated tests, deterministic captures, production gates, GPU timing, and memory budgets pass.

