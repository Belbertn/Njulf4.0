# Material–GI Production Readiness Plan

Date: 2026-07-28

Status: Proposed

Scope: imported, cooked, runtime-edited, rasterized, SSGI, Simple DDGI, legacy DDGI compatibility, emissive-light sampling, and far-field material transport

## 1. Target outcome

Njulf must have one authoritative material-to-light-transport contract from asset import through final GI composition. A material must not mean one thing in forward shading, another at a DDGI ray hit, another in compact cascades, and another in the far field.

The shipping target is **energy-consistent diffuse GI for the supported glTF material model**, with explicit ownership of effects that diffuse probes cannot represent:

- DDGI transports stable low-frequency indirect diffuse.
- SSGI is a higher-detail estimate of the same diffuse quantity and replaces or refines DDGI where supported; it is not blindly added.
- Reflection probes, IBL, SSR, or a future ray-traced reflection path own indirect specular.
- Transmission, volume, caustics, and strongly directional glossy multi-bounce are never disguised as diffuse bounce.
- Emissive surfaces use a documented linear HDR radiance convention and participate consistently at textured hits, compact hits, far-field hits, and emissive importance sampling.

This scope is production-ready only when correctness, conformance, edit propagation, diagnostics, performance, compatibility, and rollout gates all pass.

## 2. Normative material behavior

The implementation must follow the glTF 2.0 core material contract and supported Khronos extensions:

- Core glTF material specification: <https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html#materials>
- `KHR_materials_unlit`: <https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_materials_unlit>
- `KHR_materials_emissive_strength`: <https://github.com/KhronosGroup/glTF/tree/main/extensions/2.0/Khronos/KHR_materials_emissive_strength>

Required interpretations:

1. Base-color and emissive RGB are decoded from sRGB before lighting or averaging. Alpha, metallic, roughness, occlusion, and normal data remain linear.
2. Base-color factor, texture, and vertex color are multiplied exactly once.
3. Metallic-roughness uses G for roughness and B for metallic.
4. Occlusion uses its independent texture binding and glTF strength equation:

   `materialOcclusion = 1 + strength * (sampleR - 1)`

   It affects indirect lighting, not direct lighting or emission.
5. Emission is:

   `emissiveRadiance = emissiveFactor * linear(emissiveTexture) * emissiveStrength`

   Zero is a valid result and must never mean “missing.”
6. `MASK` uses `alpha >= alphaCutoff` as covered. Values above `1` remain legal and make the material fully uncovered; upload must not clamp them to `1`.
7. `doubleSided` controls both raster and ray-facing behavior. Single-sided back faces do not become GI occluders or reflectors.
8. `KHR_materials_unlit` renders base color without lighting. It remains an alpha/sidedness-aware visibility surface, but it neither receives nor reflects GI and is not an emitter by default. An engine-owned, explicit `EmitsIntoGi` override may opt an unlit material into emission; glTF unlit itself must not imply emission.
9. Alpha-blended compositing materials do not become opaque diffuse-GI blockers. Physical transmission is handled only through the explicit transmission policy.

## 3. Non-negotiable transport invariants

1. All lighting math is linear HDR until exposure and tone mapping.
2. DDGI atlases store irradiance. Lambertian conversion is applied once; no path may add or omit an accidental `PI`.
3. A passive surface has finite diffuse reflectance in `[0, 1]`. Fully metallic material has zero diffuse reflectance.
4. The same canonical diffuse response is used at a DDGI hit, for multi-bounce caching, at the forward receiver, and by SSGI receiver composition.
5. Direct emission is never multiplied by albedo, metallic, AO, or `1 / PI`.
6. Material AO is applied once to indirect diffuse and never to direct light or emission. Screen-space AO remains a separate signal.
7. Geometric normal controls visibility, ray offsets, and sidedness. Shading normal may control the local BRDF only with a bounded shading-normal correction.
8. Fine textured transport, compact transport, and far-field transport may differ in frequency, not in mean energy.
9. Missing compact statistics are explicit. Invalid data forces a documented fallback or texture sampling; it never silently samples a center texel and labels the result valid.
10. A material edit atomically publishes authored values, derived transport values, metadata, revisions, and dirty regions.
11. Hybrid GI is a partition of ownership. Two estimators of the same path space are not added at full weight.
12. No exposure, sky-intensity, indirect-intensity, or tone-map adjustment may be used to hide a material transport defect.

## 4. Canonical contracts

### 4.1 Authored material definition

Introduce a renderer-owned immutable `MaterialDefinition` (or equivalently named type). `MaterialManager` stores this CPU contract instead of treating `GPUMaterialData` as the editable source of truth.

It contains:

- factors and independent texture bindings for base color, normal, metallic-roughness, occlusion, and emissive;
- each binding’s texture handle, sampler, UV set, offset, scale, and rotation;
- emissive factor and emissive strength as separate fields;
- metallic, roughness, occlusion strength, and normal scale;
- alpha mode, unclamped non-negative cutoff, and double-sided state;
- shading model: PBR, unlit, foliage, decal, or an explicitly supported engine model;
- material extension definition;
- explicit GI participation overrides, never inferred from a zero-valued field.

Low-level `UpdateMaterial(handle, GPUMaterialData)` becomes internal test/migration infrastructure. Runtime and editor code use `UpdateMaterialDefinition`, which always recompiles derived data.

### 4.2 Texture transport statistics

Replace the single optional color average with versioned `TextureTransportStatistics`:

- validity and algorithm version;
- source content hash, semantic, and color space;
- linear mean RGBA;
- linear second moment or variance where useful;
- mean metallic B, roughness G, and occlusion R;
- linear emissive mean RGB and luminance;
- alpha histogram or equivalent cutoff-queryable coverage data;
- tangent-normal variance/cone data for optional coarse normal handling;
- finite/range validation results.

Statistics are computed from decoded source texels before destructive resize or compression. KTX2 pass-through must be transcoded or decoded for statistics during cooking. If a runtime-only KTX2 cannot be analyzed, its statistics remain explicitly invalid and compact consumers use the configured correctness fallback.

### 4.3 Primitive transport profile

Texture-wide averages cannot account for UV transforms, wrapping, vertex colors, correlated base-color/metallic textures, or a primitive using only part of an image. Cooking therefore produces an optional, authoritative `GiPrimitiveTransportProfile` keyed by primitive/material pairing.

Use deterministic, surface-area-weighted sampling over the primitive to evaluate:

- mean diffuse reflectance;
- mean emission and emissive importance;
- mean material occlusion;
- alpha coverage for the authored cutoff;
- mean metallic and roughness for diagnostics;
- normal variance if coarse normal transport is enabled;
- validity/quality flags and source hashes.

The DDGI ray-query and far-field instance records reference the primitive profile independently of the shared material index. Runtime uncooked assets may use a lower-quality texture-statistics profile, but the quality level must be visible in diagnostics.

### 4.4 GPU transport surface

Define one shader structure and evaluator in a shared include:

```text
GiSurfaceSample
    geometricNormal
    shadingNormal
    diffuseReflectance
    emissiveRadiance
    materialOcclusion
    opacity
    metallic
    roughness
    flags
```

Required helpers:

- `EvaluateGiTexturedSurface(...)`
- `EvaluateGiCompactSurface(...)`
- `EvaluateGiDiffuseBrdf(...)`
- `EvaluateGiDiffuseFromIrradiance(...)`
- `EvaluateGiOpacity(...)`
- `EvaluateGiSidedness(...)`

Forward, scene-surface output, SSGI, DDGI hit shading, multi-bounce transport, and far-field baking must consume these helpers or a generated equivalent contract. No consumer may reconstruct diffuse reflectance independently as `albedo * (1 - metallic)`.

### 4.5 Diffuse BRDF scope

For direct light at a ray hit, evaluate the supported glTF diffuse BRDF using the actual incoming direction and the outgoing direction toward the probe.

For directionally compressed probe irradiance, use a documented hemispherical diffuse response derived from the same BRDF:

`Lo_diffuse = irradiance * rhoDiffuse(material, NdotV) / PI`

The response must include:

- zero diffuse for fully metallic surfaces;
- dielectric Fresnel energy loss;
- `KHR_materials_specular`/IOR influence on dielectric F0;
- base-layer energy reduction from supported clearcoat and sheen models;
- transmission removal from the opaque diffuse share;
- bounded behavior for custom subsurface approximation.

Anisotropic, iridescent, clearcoat-specular, metallic-specular, and dispersive energy remains owned by the indirect-specular/transmission systems. The diffuse probe path must not invent Lambertian energy for those lobes.

## 5. Issue-closure matrix

| Existing defect or gap | Required closure | Delivery phase |
|---|---|---:|
| DDGI hit transports full base color without metallic | Canonical diffuse reflectance; metal diffuse is zero | 4 |
| Source and receiver use different diffuse/Fresnel policy | Shared BRDF helpers and CPU/GPU oracle | 4 |
| Rough/specular/transmission extensions disappear in GI | Explicit diffuse energy policy and separate ownership | 4, 6 |
| Compact emission has no texture average | Linear emissive statistics/profile | 2, 5 |
| Zero emission is treated as a missing sentinel | Explicit validity flags | 1, 5 |
| CPU emissive proxy takes component-wise max with raw factor | Remove fallback max; use compiled radiance | 5 |
| Bounding-sphere emissive proxy is not area/orientation correct | Bounded emissive-triangle importance sampler | 5 |
| Emissive units and clamps are ambiguous | Documented HDR radiance scale and format-safe bounds | 5 |
| Far field takes `max(average, factor)` | Consume valid primitive/material diffuse profile | 7 |
| Far-field color uses bitwise `atomicOr` across writers | Deterministic winner or weighted material resolve | 7 |
| Far field omits emission/coverage/material semantics | Versioned far-field material payload | 7 |
| Live edits leave DDGI averages/policy stale | Authored definition plus transactional compiler | 3 |
| Dirty signatures omit transport inputs | Aspect revisions and property-complete change masks | 3 |
| Texture hot reload does not guarantee material recompilation | Texture-to-material dependency graph | 3 |
| Editor conflates emissive color and strength | Separate controls and scene serialization fields | 3 |
| Separate occlusion texture is dropped | Independent texture binding and transform | 1, 6 |
| Occlusion strength is multiplied incorrectly | glTF strength equation | 6 |
| Simple DDGI ignores material AO | Apply material AO to indirect ownership exactly once | 6 |
| DDGI ignores shading normals | Geometric/shading normal split with correction | 6 |
| Ray hits force every surface two-sided | AS/ray sidedness parity | 6 |
| Cooked unlit classification is not consumed | Runtime shading-model metadata and unlit pipeline behavior | 6 |
| Alpha textures may be skipped by coarse transport | Visibility samples alpha independently of color LOD policy | 6 |
| Skinned masked material is forced opaque | Orthogonal skinning and alpha policy | 6 |
| Masked objects are omitted from dirty/emissive tracking | Track every GI-participating render mode | 3, 6 |
| Alpha cutoff is clamped and equality differs by pass | Shared spec-conformant coverage helper | 6 |
| KTX2/pass-through compact averages may be unknown | Cook/transcode statistics; explicit invalid fallback | 2 |
| SSGI trace source includes aggregate direct radiance, including specular | Export diffuse-source radiance and emission separately | 4 |
| SSGI and DDGI add overlapping first-bounce energy | Support-aware replacement/refinement composition | 8 |
| SSGI receiver reconstructs material from albedo/metal only | Store canonical receiver diffuse response and AO | 4, 8 |
| Passive material inputs can exceed physical bounds | Import/editor/API validation and finite diagnostics | 1 |
| Existing tests largely prove source text, not transport behavior | Executable CPU/GPU oracles and image tests | 0–9 |
| Punctual/emissive lighting is not photometrically calibrated | Separate, synchronized raster/GI radiometry PR | 5 |

## 6. Delivery phases

Every phase has an independent feature flag, evidence bundle, rollback, and gate. Do not combine a cooked-format migration, a lighting-composition change, a scheduling change, or an optimization in the same pull request.

### Phase 0 — Reproducible baseline and behavioral oracle

1. Preserve the current targeted test baseline and record the full solution state, including known unrelated failures.
2. Add deterministic material/GI conformance scenes:
   - white and colored dielectric Cornell boxes;
   - metallic sweep from 0 to 1;
   - roughness and dielectric-F0 sweep;
   - sparse checker emissive panel at strengths 0, 0.5, 1, and 10;
   - separate-UV occlusion;
   - single/double-sided cards;
   - static and skinned alpha-mask cards;
   - unlit material;
   - near/compact/far-field transition corridor;
   - live-edit material wall;
   - SSGI/DDGI overlap scene.
3. Add a pure CPU `GiMaterialReferenceEvaluator` for core glTF diffuse, emission, AO, alpha, and sidedness behavior.
4. Add a small GPU compute conformance harness that evaluates the shader material helper over structured cases and reads results back. Compare against the CPU oracle numerically.
5. Capture linear/HDR buffers separately:
   - direct diffuse;
   - direct specular;
   - raw DDGI irradiance;
   - final DDGI diffuse;
   - SSGI estimate;
   - final composed indirect;
   - material diffuse reflectance;
   - emission;
   - AO and ownership weights.
6. Check in capture metadata: scene hash, camera, exposure, lights, material values, renderer settings, build commit, shader hash, device, driver, warmup, and random seed.

Phase 0 gate:

- Every reported defect has a failing behavioral test or deterministic capture.
- The CPU/GPU oracle runs without a window on CI-capable Vulkan hardware.
- Existing correct sRGB, texture-factor, irradiance, and `PI` behavior is locked by tests.
- No visual fix begins until the baseline is reproducible.

### Phase 1 — Canonical authored and GPU contracts

1. Add `MaterialDefinition`, independent `MaterialTextureBinding`, `GiMaterialTransportProfile`, and `MaterialChangeMask`.
2. Add `MaterialShadingModel` to `MaterialRenderMetadata`; consume cooked PBR/unlit/foliage/decal classification.
3. Give occlusion its own texture binding. Do not infer sampling equivalence from matching source paths; bindings are shared only if image, sampler, UV set, and transform are all equivalent.
4. Replace overloaded float/sentinel policy with named integer flags:
   - base statistics valid;
   - diffuse profile valid;
   - emission profile valid;
   - alpha profile valid;
   - unlit;
   - double-sided;
   - transmission policy;
   - profile quality.
5. Validate all factors for finite values. Enforce glTF ranges at import and editor boundaries while preserving legal HDR emission and alpha cutoff above one.
6. Decide the measured GPU layout:
   - reuse/rename the existing DDGI vectors where possible;
   - use a same-index transport buffer if clearer than expanding common raster material stride;
   - keep integer flags as integer bits, not rounded floats.
7. Add C#/GLSL size, alignment, offset, and default-sentinel tests.
8. Retain a read-only V1 conversion adapter behind `GiMaterialTransportV2`; no V2 code may author a raw `GPUMaterialData`.

Phase 1 gate:

- One authored definition deterministically produces all raster and GI payloads.
- Every optional statistic uses explicit validity.
- Default material and all extension combinations validate.
- Shader and C# layouts match exactly.
- No rendered behavior changes in this phase.

### Phase 2 — Complete statistics and cooked data

1. Generalize `TextureColorAverages` into the versioned statistics pipeline while retaining double-precision accumulation.
2. Compute statistics before resize/compression for PNG/JPEG/WebP/HDR sources.
3. Add KTX2/Basis/BC decode or transcode support to the cooker so pass-through assets receive the same statistics. Pin the decoder/tool version.
4. Generate alpha-coverage-preserving mipmaps for mask textures and retain a cutoff-queryable alpha histogram.
5. Generate deterministic primitive transport profiles using surface-area-weighted samples, UV transforms, sampler wrapping, and vertex colors.
6. Persist texture statistics in `CookedTextureMeta` and primitive profiles in the cooked material/model payload.
7. Bump texture and material cooked-format minor versions in isolated migration PRs. Update `CookedAssetMigrator`, cache keys, dependency hashes, and cooker algorithm version.
8. Backward compatibility:
   - development builds recook old packages;
   - shipping builds either use an explicitly tagged V1 fallback or reject packages according to product policy;
   - no V1 path claims compact statistics are valid;
   - remove the terminal-mip center-sample compatibility fallback after the recook window.
9. Runtime uncooked loading computes equivalent statistics and caches them by content hash. Unknown precompressed data remains invalid rather than guessed.

Phase 2 gate:

- Raw and KTX2 forms of the same source produce statistics within the defined compression tolerance.
- sRGB averages match linear reference values.
- Sparse emissive and correlated base/metallic fixtures match brute-force reference integration.
- Cook output is deterministic across repeated runs.
- Old-format behavior is explicit, tested, and observable.

### Phase 3 — Transactional runtime editing and invalidation

1. Store `MaterialDefinition`, texture dependencies, compiled payloads, metadata, and per-aspect revisions in each `MaterialManager` slot.
2. Implement a pure `MaterialTransportCompiler`. Registration, scene overrides, editor changes, texture hot reload, and asset reload all call this compiler.
3. Publish an edit atomically:
   - authored definition;
   - common GPU payload;
   - extension payload;
   - transport profile;
   - render metadata;
   - material, emission, coverage, sidedness, and far-field revisions;
   - scoped dirty bounds.
4. Return a `MaterialChangeMask` that distinguishes:
   - raster-only appearance;
   - diffuse transport;
   - emission;
   - alpha coverage;
   - sidedness/AS policy;
   - shading model;
   - far-field page content.
5. Replace ad hoc hashes in `VulkanRenderer` with revisions from `MaterialManager`. Include texture content revisions, UV bindings, extension energy fields, alpha cutoff, and metadata.
6. Track opaque and masked objects. Emissive tracking includes masked materials with nonzero covered emissive area.
7. Invalidate only intersecting DDGI regions and far-field pages; changes that alter opacity or sidedness also update/rebuild the required AS records.
8. Make shared/deduplicated material editing explicit:
   - scene overrides clone or use copy-on-write;
   - an explicit “edit shared asset” operation may update all users;
   - accidental global edits are prohibited.
9. Update the editor and `SceneMaterialOverrideDocument`:
   - separate emissive color and emissive strength;
   - add occlusion strength, shading model/GI override where supported, and explicit optional fields;
   - preserve backward-compatible scene loading;
   - validate ranges and show derived diffuse/emission previews.
10. Add edit-latency and dirty-region diagnostics.

Phase 3 gate:

- Mutating every authored field changes exactly the expected revision categories in property-based tests.
- An albedo, metallic, emission, texture, UV, alpha, or sidedness edit reaches the GPU and emits the correct dirty event without restart.
- Visible dirty probes begin updating within one frame P95 and converge within the existing tier budget.
- Far-field pages cannot remain published with an obsolete material revision.
- No editable path writes derived transport vectors directly.

### Phase 4 — Energy-consistent diffuse transport

1. Add the shared material transport shader include and the CPU mirror.
2. At textured DDGI hits, sample base color and metallic-roughness with their independent bindings and evaluate canonical diffuse reflectance.
3. At compact hits, consume the primitive transport profile. If invalid, use the explicit correctness fallback selected by tier.
4. Define a ray-footprint/LOD policy from probe spacing, cascade, hit distance, and UV scale. Sample correlated material channels at a consistent footprint; do not use an unexplained fixed preferred mip.
5. Replace `TraceDirectDiffuseAtHit` with the supported diffuse BRDF evaluation using light direction, view-to-probe direction, F0/IOR/specular, metallic, and base-layer extension energy.
6. Split the SSGI trace source into diffuse-source radiance and emission. Do not feed aggregate direct specular into the diffuse-GI estimator; specular path transport remains owned by the reflection system.
7. Use the same irradiance response for:
   - DDGI multi-bounce;
   - forward DDGI receiver;
   - legacy DDGI compatibility;
   - environment diffuse;
   - SSGI receiver material data.
8. Store canonical diffuse reflectance in the multi-bounce cache, not raw albedo. Replace the arbitrary transport clamp with validated passive-reflectance bounds plus a documented numerical safety limit.
9. Change `scene_surface.frag` to output receiver diffuse response and material AO rather than raw albedo plus metallic. Preserve enough data for debug views.
10. Keep specular GI out of the diffuse probe result. Verify that existing IBL/reflection paths still own it.
11. Add shading-normal-aware BRDF evaluation for near textured hits only after Phase 6 normal policy is available; geometric-normal reference remains selectable during rollout.

Phase 4 gate:

- Metallic `1` produces zero diffuse DDGI/SSGI contribution within numerical epsilon.
- CPU and GPU diffuse evaluations agree within `1e-4` for the conformance matrix.
- Passive surfaces never increase integrated energy.
- Uniform-material DDGI converges within 5% mean diffuse luminance of the approved high-sample reference.
- Near and compact mean diffuse energy differ by at most 5% on the texture fixtures.
- Irradiance/`PI` regression tests remain unchanged and pass.

### Phase 5 — Emissive transport and radiometry

1. Compile emission from factor, linear texture/profile, and strength exactly once.
2. Replace zero/nonzero sentinel logic with explicit validity. Strength `0` yields exactly zero everywhere.
3. Use compiled emission at textured DDGI hits, compact hits, SSGI trace-source output, and emissive tracking.
4. Remove component-wise `max(compiled, rawFactor)` behavior.
5. Define the scene radiance convention:
   - document how glTF’s emissive luminance interpretation maps into Njulf scene-linear radiance and exposure;
   - retain HDR values through FP16/FP32 targets;
   - use finite format-safety bounds with overflow diagnostics, not an artistic hidden clamp.
6. Replace bounding-sphere emissive lighting with a bounded emissive-triangle light table:
   - triangle world area;
   - mean covered emissive radiance;
   - orientation;
   - alpha coverage;
   - stable material/geometry revisions;
   - alias-table or equivalent area-luminance importance sampling;
   - visibility ray and correct geometric/PDF weighting.
7. If another estimator can sample the same emissive light path, use multiple-importance sampling or disable one estimator for that path class. Direct-hit emission, next-event emission, and cached multi-bounce must have documented, non-overlapping ownership.
8. Keep the existing proxy behind a rollback flag only until the triangle sampler passes. Proxy and triangle paths must never contribute simultaneously.
9. Budget emissive samples per tier and expose skipped-energy estimates. Dynamic/skinned emitters require an explicit supported update path or a diagnosed exclusion.
10. In a separate PR, make punctual light units/attenuation identical between raster and GI and adopt the chosen physical or scene-unit convention. Do not mix this with emissive storage changes.

Phase 5 gate:

- Strength `0`, `0.5`, `1`, and `10` scale mean emitted contribution linearly within 1%.
- Sparse emissive texture contribution matches the brute-force surface reference within 5%.
- Scaling emitter area changes total received power appropriately; changing only bounding-box padding has no effect.
- Emissive proxy and real surface-hit paths have no accidental duplicate mode.
- No NaN, infinity, silent saturation, or more than the declared tier budget.

### Phase 6 — AO, normals, sidedness, alpha, unlit, and extension policy

#### 6.1 Occlusion

1. Sample the independent occlusion binding using its own UV set and transform, even if it references the same image as metallic-roughness.
2. Implement the glTF strength equation.
3. Keep `materialOcclusion` separate from SSAO.
4. Apply material AO once to environment, DDGI, and SSGI indirect diffuse at the receiver.
5. Apply it to incoming indirect irradiance before a secondary diffuse re-bounce; do not apply it to direct light or emission.

#### 6.2 Normal handling

1. Carry both geometric and shading normals.
2. Geometric normal controls ray offset, back-face policy, visibility hemisphere, and leak prevention.
3. Near textured BRDF evaluation may use tangent-space normal maps with the correct tangent basis, UV binding, scale, and ray LOD.
4. Apply a tested shading-normal correction and clamp invalid hemispheres.
5. Compact/far transport uses geometric normal plus optional cooked normal variance; never sample a random normal-map center.

#### 6.3 Sidedness

1. Complete double-sided raster variants and `gl_FrontFacing` tangent-basis handling.
2. Enable back-face culling for single-sided GI ray queries.
3. Set per-instance facing-cull disable only for double-sided materials.
4. Handle negative-determinant transforms and winding parity.
5. Flip a shading normal only for a valid double-sided back-face hit; do not turn every hit into double-sided geometry.

#### 6.4 Alpha coverage

1. Move all raster, shadow, DDGI, SSGI-surface, and far-field coverage decisions to one mirrored helper.
2. Preserve the spec’s `alpha >= cutoff` rule and unclamped cutoff.
3. Decouple alpha candidate testing from the cascade’s color-texture sampling policy. Coarse cascades still resolve visibility correctly.
4. Use deterministic ray LOD/alpha-coverage mips or a validated compact coverage mask.
5. Make skinning and opacity orthogonal AS axes; a skinned masked material is not forced opaque.
6. Include masked objects in transform/material dirty tracking and emissive-source tracking.
7. Integrate foliage cards/clusters with the same alpha contract or keep them explicitly diagnosed and excluded until their transport proxy is ready.

#### 6.5 Unlit and advanced material policy

1. Consume `CookedMaterialPipeline.Unlit` through runtime metadata.
2. Render unlit as base-color term only, with alpha, vertex color, texture transforms, and double-sided behavior preserved.
3. Set default unlit diffuse reflectance and emission to zero for GI; provide a named engine opt-in for GI emission.
4. Treat alpha blend as non-opaque compositing, not physical transmission.
5. For `KHR_materials_transmission`/volume:
   - remove transmitted energy from opaque diffuse reflectance;
   - continue/attenuate transport only in the explicit transmission implementation;
   - otherwise diagnose the material as unsupported for transmission GI rather than shading it as opaque diffuse.
6. Add an explicit diffuse-energy classification for clearcoat, sheen, specular, IOR, subsurface, anisotropy, iridescence, and dispersion.

Phase 6 gate:

- AO strength 0 is neutral and strength 1 matches the texture; direct/emissive output is unchanged.
- Raster and GI agree on every alpha-boundary fixture, including equality and cutoff above 1.
- Masked occlusion coverage differs by no more than 2% between raster reference and GI rays at tested distances.
- Single-sided back faces do not occlude or bounce; double-sided back faces shade stably.
- Skinned masked cards no longer become opaque solely because they are skinned.
- Unlit official sample assets render without lighting and do not silently illuminate the scene.
- All supported extension combinations have a documented GI classification and behavioral test.

### Phase 7 — Far-field material transport V2

1. Version the far-field voxel payload independently from the common material buffer.
2. Replace `max(average, factor)` with the valid primitive/material diffuse profile.
3. Replace color `atomicOr` with an order-independent material resolve:
   - preferred: deterministic two-pass dominant-surface selection using a stable distance/coverage/tie-break key;
   - acceptable alternative: bounded fixed-point weighted accumulation with overflow proof and normalization.
4. Store or reference:
   - occupancy/alpha coverage;
   - canonical diffuse reflectance;
   - emissive radiance or emissive profile reference;
   - geometric normal/cone;
   - material/profile revision.
5. Apply masked coverage and sidedness during voxelization.
6. Invalidate/rebake pages by material change mask and profile revision.
7. Define deterministic behavior when multiple surfaces share one voxel and expose conflict counters.
8. Keep V1/V2 far-field selection behind `GiFarFieldMaterialV2` until visual and budget gates pass.

Phase 7 gate:

- Voxel output is independent of triangle dispatch order.
- A dark textured wall never becomes white because its base factor is white.
- Metallic, emissive, masked, and double-sided far-field fixtures retain their declared semantics.
- Near/compact/far mean irradiance differs by at most 8%, with no visible transition step above the existing 10% transition target.
- Page edits publish no stale material revision.
- Memory and bake time remain inside explicit tier caps.

### Phase 8 — Energy-conserving SSGI/DDGI composition

1. Define both DDGI and SSGI as estimates of the same diffuse-indirect quantity.
2. Add a receiver/composition target containing:
   - canonical receiver diffuse response;
   - material AO;
   - DDGI radiometric ownership/support;
   - environment fallback share;
   - shading-model participation flags.
3. Replace additive SSGI blending with a partition-of-unity composition:

   `Lindirect = wSsgi * Lssgi + (1 - wSsgi) * LddgiOrFallback`

   `wSsgi` is bounded by SSGI support, depth/normal confidence, distance, temporal confidence, and valid DDGI ownership.
4. If SSGI is retained only as contact detail, extract a zero-mean/high-frequency residual and prove that it does not restore full low-frequency energy on top of DDGI.
5. Compose in a dedicated GI pass or use a mathematically equivalent signed delta path. Do not rely on irreversible positive-only additive blending.
6. Preserve environment fallback only for unowned share.
7. Ensure debug modes display raw estimators, weights, and final composition separately.
8. Reset/reject temporal SSGI history when material diffuse response, AO, alpha coverage, or shading model changes.

Phase 8 gate:

- With identical constant DDGI and SSGI inputs, hybrid output equals either input, not their sum.
- Hybrid output is a bounded convex estimate for all conformance cases.
- Enabling SSGI in a converged DDGI scene does not increase low-frequency mean irradiance by more than 2%.
- Contact detail improves the approved edge/crevice metric without a scene-wide energy increase.
- Unsupported pixels use one explicit fallback and never black out or double-light.

### Phase 9 — Diagnostics, performance, conformance, and rollout

1. Add debug views:
   - sampled base color, metallic, roughness, and material AO;
   - canonical diffuse reflectance;
   - compiled emission;
   - geometric versus shading normal;
   - opacity, sidedness, and shading model;
   - transport-profile validity and quality;
   - textured/compact/far source;
   - material/texture/profile revision;
   - DDGI/SSGI/fallback ownership and final composition.
2. Add bounded counters:
   - invalid/missing statistics;
   - V1 compatibility fallback use;
   - material recompiles and compile latency;
   - dirty-region latency;
   - alpha candidate tests and rejection rate;
   - emissive triangles/samples/skipped importance;
   - far-field material conflicts and stale-page rejection;
   - non-finite/clamped material or radiance values.
3. Keep normal telemetry within the existing `< 0.1 ms GPU / 0.05 ms CPU P95` budget. Investigation counters remain opt-in.
4. Profile Low/Medium/High/Ultra on the established reference and lower-memory ray-query devices. Preserve the existing total-GI GPU, CPU, memory, and convergence budgets.
5. Run:
   - all CPU tests;
   - shader compilation and ABI tests;
   - GPU compute oracle;
   - official Khronos material sample assets;
   - linear/HDR image regression suite;
   - Vulkan validation;
   - resize, reload, quality-switch, texture-hot-reload, device-loss/recovery where supported;
   - 30-minute dynamic-material soak and long camera path;
   - graphics/async equivalence.
6. Roll out flags independently:
   - `GiMaterialTransportV2`;
   - `GiEmissiveMeshSampling`;
   - `GiFarFieldMaterialV2`;
   - `GiHybridCompositionV2`.
7. Dual-run comparison may evaluate V1 and V2 for diagnostics, but only one path contributes to the frame.
8. Make V2 default only after two devices and every release gate pass. Retain the kill switch for one release window, then remove V1 code, terminal-mip compatibility, raw material editing, and obsolete diagnostics.

Phase 9 gate:

- All release targets below pass in Release builds.
- No unexplained compatibility fallback remains in production content.
- Vulkan validation is clean.
- Performance and memory remain bounded by tier.
- Rollback is tested without asset corruption or restart where practical.
- V1 removal has an owner and date.

## 7. Release-blocking acceptance targets

### 7.1 Numerical correctness

| Gate | Required result |
|---|---:|
| CPU/GPU core material evaluator | Absolute error `<= 1e-4` |
| Fully metallic diffuse response | `<= 1e-4` per channel |
| Passive diffuse reflectance | Finite and within `[0, 1]` |
| Emissive strength scaling | `<= 1%` relative error |
| Strength-zero emission | Exactly zero before floating-point denormal tolerance |
| AO strength equation | Absolute error `<= 1e-4` |
| Alpha equality/cutoff behavior | Exact boolean parity across passes |
| Raw versus cooked/KTX statistics | Within declared compression tolerance |
| Fine versus compact mean diffuse | `<= 5%` relative difference |
| Compact versus far mean diffuse | `<= 8%` relative difference |
| Hybrid identical-estimator case | No increase over input beyond `1e-4` |
| Non-finite shader diagnostics | Zero |

### 7.2 Visual correctness

| Gate | Required result |
|---|---:|
| Uniform material reference ROIs | Mean diffuse luminance within 5% |
| Approved HDR reference | Relative RMSE `<= 12%`, FLIP P95 `<= 0.08` |
| Material/cascade transition | No single step above 10% |
| Static temporal material ROI variation | `< 3%` P95 after warmup |
| Runtime material edit | No stale near/compact/far disagreement after convergence |
| Alpha-mask visibility | Raster/GI coverage difference `<= 2%` |
| Hybrid low-frequency mean change | `<= 2%` when adding SSGI to valid DDGI |

### 7.3 Responsiveness and budgets

- Visible material changes schedule their first affected probe update within one frame P95.
- Near-field convergence remains within eight frames P95 under the High tier reference workload.
- Material recompilation, dependency tracking, and upload fit inside the existing GI CPU scheduling/upload P95 budget.
- Added material evaluation does not cause total incremental GI to exceed the existing tier GPU budget.
- Primitive profiles, emissive tables, and far-field material payloads have explicit per-tier memory caps.
- Alpha and emissive work have hard sample/candidate limits and report dropped or approximated work.
- Long-run memory shows no positive trend after warmup.

## 8. Recommended pull-request sequence

Each PR must build, test, capture, and roll back independently.

1. Add conformance scenes, CPU reference evaluator, and failing behavioral tests.
2. Add GPU compute material oracle and capture metadata; no lighting change.
3. Introduce authored material/texture-binding/change-mask contracts behind V2.
4. Add texture statistics runtime and cooker logic; no cooked schema change yet.
5. Migrate cooked texture metadata and compatibility handling.
6. Add primitive transport-profile cooking and its separate material/model schema migration.
7. Make `MaterialManager` authoritative for compilation, revisions, dependencies, and copy-on-write editing.
8. Migrate editor and scene overrides to authored definitions and separate emission strength.
9. Add shared shader transport surface and ABI tests without enabling composition changes.
10. Switch DDGI hit, multi-bounce, forward receiver, and SSGI receiver to canonical diffuse response.
11. Fix independent occlusion/AO behavior.
12. Fix normal, sidedness, and alpha visibility across raster/AS/GI.
13. Enable runtime unlit and advanced-material diffuse/transmission policy.
14. Correct compiled emission and all compact/textured consumers.
15. Replace emissive proxy with the bounded mesh-light sampler.
16. Land punctual/emissive radiometry calibration as a separate synchronized raster/GI change.
17. Migrate far-field material payload and deterministic voxel resolve.
18. Replace additive hybrid composition with ownership partition.
19. Add final diagnostics, tier enforcement, performance optimization, and full release matrix.
20. Make V2 default, run staged rollout, then remove V1 after the rollback window.

## 9. Primary code ownership

| Area | Primary files/components |
|---|---|
| Import and authored definitions | `Njulf.Assets/ModelImporter.cs`, `Njulf.Assets/Gltf/*` |
| Texture statistics/cooking | `Njulf.Assets/TextureColorAverages.cs`, `Njulf.Assets/Cooked/TextureCooker.cs`, `Njulf.Rendering/Resources/TextureManager.cs` |
| Cooked schemas/migration | `Njulf.Assets/Cooked/CookedPayloads.cs`, `CookedFormat.cs`, `CookedAssetMigrator.cs`, `ModelAssetCooker.cs` |
| Material compiler/lifetime/editing | `Njulf.Rendering/Resources/MaterialManager.cs`, `ModelRenderUploadService.cs`, `MaterialManagerSceneMaterialOverrideStore.cs` |
| Metadata and GPU ABI | `Njulf.Rendering/Data/MaterialRenderMetadata.cs`, `GPUStructs.cs`, `Njulf.Shaders/common.glsl` |
| DDGI hit/direct/emission | `Njulf.Shaders/ddgi_hit_shading.glsl` |
| Simple/legacy bounce | `ddgi_simple_trace.comp`, `ddgi_simple_transport.comp`, `ddgi_update_shared.glsl` |
| Forward receiver/materials | `Njulf.Shaders/forward.frag` |
| SSGI surface/composition | `scene_surface.frag`, `ssgi_trace.comp`, `ssgi_composite.frag`, SSGI pipeline classes |
| AS opacity/sidedness | `Njulf.Rendering/Resources/AccelerationStructureManager.cs` |
| Far field | `farfield_voxelize.comp`, `farfield_clipmap.glsl`, far-field manager/pass classes |
| Runtime dirty tracking | `Njulf.Rendering/VulkanRenderer.cs`, material/texture managers |
| Editor/scene persistence | `Njulf.Editor/EditorImGuiPanels.cs`, `Njulf.Assets/Scenes/*` |
| Behavioral validation | focused new tests plus `ModelRenderUploadServiceTests`, `CookedAssetTests`, `SimpleDdgiShaderMirrorTests`, `FarFieldClipmapOracleTests`, `ShaderBuildTests` |

## 10. Risk register

| Risk | Mitigation |
|---|---|
| Correct metallic/Fresnel handling makes existing scenes darker | Lock exposure/lights, compare isolated buffers, fix authored lighting rather than add hidden GI multipliers |
| Additional hit texture samples exceed trace budget | Cook primitive profiles, sample detailed materials only in justified cascades, profile before optimizing |
| Compact profile differs from textured UV usage | Surface-area-weighted primitive profiles and fine/compact energy gates |
| Shading normals introduce leaks or energy gain | Separate geometric/shading normals, correction term, reference fallback, grazing tests |
| Alpha candidate testing becomes expensive | Coverage-preserving mips/compact masks, counters, hard budgets, optional validated opacity-micromap path |
| Emissive triangle sampling is noisy | Area-luminance importance sampling, stable tables, bounded temporal reuse, reference/MIS tests |
| Material edits fan out into excessive probe/page work | Aspect-specific revisions, spatial dirty bounds, debounced far-field rebuilds |
| Cooked schema change strands content | Separate migrations, recook tooling, explicit compatibility telemetry, rollback package |
| GPU ABI drifts between C# and GLSL | Exact layout/offset tests and compute round-trip test |
| Hybrid rewrite causes composition regressions | Raw-estimator captures, convex-combination oracle, independent feature flag |
| Dual V1/V2 paths become permanent | Named owner, default date, fallback-use counter, removal gate after one release window |
| KTX statistics vary with tools | Pin decoder/encoder versions and include algorithm version in cache/content hashes |

## 11. Explicit non-solutions

The following do not close this plan:

- multiplying all GI by an artistic scalar;
- reducing metal albedo while still evaluating it as Lambertian;
- treating roughness as a diffuse-bounce multiplier without a BRDF derivation;
- using a terminal mip or center texel as an unmarked whole-texture average;
- interpreting zero emission as unavailable data;
- taking component-wise maxima between authored factors and compiled averages;
- applying material AO to direct light or emission;
- using SSAO as a substitute for material occlusion;
- forcing all ray normals toward the ray to avoid sidedness work;
- forcing masked/skinned/foliage geometry opaque;
- adding SSGI and DDGI at full strength;
- tuning exposure, sky, or tone mapping to make an energy regression look acceptable;
- relying only on source-string tests or a visually pleasing Sponza screenshot.

## 12. Final definition of done

Material/GI interaction is production-ready only when:

1. One authored definition and one compiler own all derived raster/GI data.
2. Core glTF and supported extension material behavior passes CPU/GPU conformance tests.
3. Metals, dielectrics, AO, emission, alpha, sidedness, unlit, and supported extension energy behave according to the declared transport policy.
4. Textured, compact, and far-field paths preserve mean diffuse and emissive energy inside release tolerances.
5. Runtime edits and texture reloads atomically update GPU data, AS policy, dirty probes, emissive sources, and far-field pages.
6. Emissive sampling is surface-area/orientation aware, bounded, and radiometrically documented.
7. SSGI/DDGI/environment composition partitions ownership and cannot double-count identical input.
8. Unknown or legacy statistics are explicit and never silently accepted as accurate.
9. Debug views and counters identify material inputs, compiled transport, source path, validity, ownership, and revision.
10. All CPU, shader, GPU-oracle, conformance-asset, HDR image, validation, performance, recovery, and soak gates pass.
11. Every quality tier remains within declared GPU, CPU, memory, ray, candidate, and convergence budgets.
12. V2 is the only shipping path after the rollback window; raw material editing, V1 sentinels, terminal-mip fallback, bounding-sphere emission, far-field `max`, far-field color `atomicOr`, and additive hybrid overlap are removed.
