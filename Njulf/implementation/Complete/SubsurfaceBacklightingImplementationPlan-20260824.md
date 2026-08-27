# Subsurface Backlighting Implementation Plan

Last updated: 2026-08-24

Design reference: reverted commit `65c3dfb97bfd3bee2c53b4c55d8dac743ceac8c5`. Reimplement only the bounded subsurface-backlighting work against the current tree; do not cherry-pick the commit because its forward-shader changes are coupled to automatic reflections, transmission, volumetric fog, shader-build changes, and unrelated renderer work.

## 1. Required outcome

Replace the current additive, view-dependent subsurface “wrap” term with a shadowed, light-driven, energy-split approximation.

For a material with subsurface strength `s`:

- ordinary front-side diffuse retains the share `1 - s`;
- a tint-bounded back-side diffuse lobe receives the share `s`;
- direct specular remains owned by the ordinary PBR lobe and is not reduced or duplicated;
- back-side direct energy exists only when a real directional, point, or spot light illuminates the opposite hemisphere and passes that light's normal shadow test;
- indirect backlighting uses the opposite-normal environment irradiance as a conservative receiver-side approximation;
- strength zero and materials without the subsurface feature retain the existing PBR result;
- all direct-diffuse captures, direct-specular captures, C5 near-field source output, final-indirect debug output, reflection captures, and final scene colour observe the same split result.

This is a bounded thin-surface visual approximation. It does not simulate a diffusion profile, optical thickness, mean free path, or transport through a closed volume.

## 2. Verified current state

### 2.1 Material plumbing is already complete

The current tree already contains:

- `MaterialFeatureFlags.Subsurface` and `SubsurfaceTexture`;
- authored `SubsurfaceColor`, `SubsurfaceStrength`, and texture binding data;
- import, validation, change classification, texture upload, GPU packing, and editor controls;
- `GPUMaterialExtensionData.Subsurface` and its bindless texture index;
- forward-shader sampling and the existing `SubsurfaceStrength` material debug view;
- a `MaterialQuality.SubsurfaceWax` fixture in `SampleMaterialShowcaseScene`.

No material ABI, bindless index, serialized setting, render-graph resource, or public API is needed for this feature.

### 2.2 The current lighting term is not backlighting

`forward.frag` currently adds this extension after normal direct and indirect composition:

```glsl
float wrap = clamp(dot(normal, viewDirection) * 0.5 + 0.5, 0.0, 1.0);
color += albedo * subsurfaceColor * subsurfaceStrength *
    wrap * indirectAo * 0.35;
```

That term:

- depends on the camera rather than an incident light direction;
- can brighten a material when no light illuminates its back side;
- is not evaluated through directional, spot, or point shadows;
- adds energy without removing any front-side diffuse share;
- is absent from `MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE`, the derived direct-specular capture, and the C5 direct-diffuse source attachment because it is added later to final colour;
- cannot represent tint variation caused by a light rotating from the front to the back of a surface.

`AccumulateLight` also returns before shadow evaluation whenever `dot(normal, lightDirection) <= 0`, so a proper back-side lobe cannot be added only at the final colour expression.

### 2.3 Useful lessons and hazards in the reverted implementation

Commit `65c3dfb` demonstrates the intended high-level shape: retain a separate direct back-diffuse accumulator, use the opposite shadow-bias normal for a back-facing light, replace rather than add diffuse energy, and evaluate opposite-normal environment/DDGI lighting.

Do not reproduce its implementation literally:

- its horizon guard is unnecessarily contradictory and obscures the exact zero-cosine case;
- it gates back-side shadow work from raw strength even when metallic, transmission, or a black tint leaves no diffuse budget;
- it manually rebuilds a diffuse BRDF from raw albedo, bypassing the current canonical clearcoat, sheen, transmission, and diffuse-energy helpers;
- it multiplies metallic/transmission suppression into both the blend amount and the back-lobe budget, which needs a fresh energy derivation against the current transport contract;
- it repurposes the one directional DDGI query from reflection to subsurface, degrading reflection fallback on those pixels;
- cache-required forward variants cannot provide that directional backside result without another gather or a receiver-cache ABI expansion;
- its subsurface indirect composition occurs after the existing `FinalIndirect` debug return, so the diagnostic does not show the value used by final scene colour;
- its only focused protection is a broad source-string assertion, with no numerical endpoint, energy, or route-parity tests.

## 3. Scope

### In scope

- Shadowed back-side diffuse for directional, point, and spot lights.
- A convex energy split between ordinary direct diffuse and back diffuse.
- Tinting from the existing uniform/texture-combined subsurface colour.
- Opposite-normal environment diffuse for the indirect subsurface share.
- Correct integration with existing material, AO, shadow, debug/capture, C5, receiver-cache, and forward shader-variant contracts.
- CPU-reference, shader-source, build, runtime-quality, and performance validation.

### Out of scope

- Screen-space or separable subsurface diffusion.
- Burley/dipole profiles, mean free path, skin channels, per-channel scattering radius, or thickness maps.
- Transmitting a directional light through closed solid geometry or defeating legitimate self-shadowing.
- Subsurface transport at DDGI ray hits or changes to probe source material transport; the existing receiver-side classification remains authoritative.
- A second per-fragment DDGI gather, a receiver-cache payload expansion, or stealing the directional DDGI lobe from reflection.
- Changes to the separate foliage backlight model.
- Thick transmission, refraction, caustics, or any other optical feature from `65c3dfb`.
- A new material flag, debug enum, runtime setting, or pipeline variant.

True dynamic-GI backside transport remains the roadmap's separate “improve indirect subsurface and translucent-material transport” item. This implementation must remain conservative when only front-side DDGI data is available.

## 4. Non-negotiable lighting contracts

1. The authored subsurface strength is the diffuse split weight and is clamped to `[0, 1]`.
2. Metallic, transmission, clearcoat, and sheen energy are applied once through the existing canonical diffuse budgets; do not multiply those exclusions into the split weight again.
3. Subsurface colour is clamped component-wise to `[0, 1]` after texture sampling, so the tint cannot create energy.
4. The back directional base is the canonical directional diffuse base multiplied by the bounded tint.
5. The back hemispherical reflectance is the canonical diffuse reflectance multiplied by the same bounded tint.
6. Only diffuse is split. Direct and indirect specular, clearcoat specular, sheen, iridescence, emissive, and transmission retain their current ownership.
7. A light with positive signed `NdotL` uses the existing front shadow-bias normal. A light with negative signed `NdotL` may contribute only to enabled backlighting and uses the negated geometric shadow normal. Exactly zero contributes to neither lobe.
8. Each contributing light performs at most one shadow evaluation. Backlighting must not evaluate both front and back shadow paths.
9. The same post-split direct diffuse feeds scene colour, linear direct-diffuse capture, derived direct-specular capture, and the C5 direct-source attachment.
10. The post-split indirect result is computed before any diagnostic or output named `FinalIndirect`.
11. Material AO and screen-space AO retain their current ownership: direct light receives no AO; opposite-normal environment diffuse receives `indirectAo` once.
12. `SampleSimpleDdgiGather` remains a single syntactic call in `forward.frag`, and the receiver-cache entry remains one 16-byte `uvec4`.

## 5. Chosen implementation

### 5.1 Add shared bounded-split helpers

In `Njulf.Shaders/gi_material_transport.glsl`, add small helpers with CPU-mirrored operation order:

```glsl
vec3 EvaluateGiSubsurfaceDiffuseBudget(
    vec3 ordinaryDiffuseBudget,
    vec3 subsurfaceTint)
{
    return clamp(ordinaryDiffuseBudget, vec3(0.0), vec3(1.0)) *
        clamp(subsurfaceTint, vec3(0.0), vec3(1.0));
}

vec3 ApplyGiSubsurfaceDiffuseSplit(
    vec3 frontDiffuse,
    vec3 backDiffuse,
    float strength)
{
    return mix(
        max(frontDiffuse, vec3(0.0)),
        max(backDiffuse, vec3(0.0)),
        clamp(strength, 0.0, 1.0));
}
```

Use one budget helper for both directional and hemispherical diffuse inputs. Do not put light, shadow, AO, or environment policy into this shared file.

Mirror the two helpers in `GiMaterialReferenceEvaluator` so numerical tests have an authoritative CPU oracle. Keep them pure and do not change `GiSurfaceSample`, compact material data, or probe transport.

### 5.2 Resolve subsurface inputs once per fragment

In `forward.frag`, after the canonical directional and hemispherical diffuse values are available:

- clamp the sampled `subsurfaceColor` to `[0, 1]`;
- set `subsurfaceWeight = clamp(subsurfaceStrength, 0, 1)`;
- derive `subsurfaceDirectionalDiffuseBase` from `directionalDiffuseBase` through the shared budget helper;
- derive `subsurfaceDiffuseReflectance` from `canonicalDiffuseReflectance` through the same helper;
- define backlighting as active only when the weight and at least one back-budget component exceed a small fixed epsilon.

The feature flag and existing extension-data path remain the source of `subsurfaceStrength`; do not infer activation from a non-white texture or the `SubsurfaceApproximation` shading-model enum.

The simple opaque variants already compile with `hasMaterialExtension = false`. Keep the computation structured so the shader compiler can constant-fold all subsurface state and branches out of those variants.

### 5.3 Refactor per-light horizon and shadow selection

Extend `AccumulateLight` with the resolved subsurface weight/budget and a second diffuse accumulator, named for example `directBackDiffuseSource`.

For every supported light:

1. Resolve and normalize `lightDirection` and apply range/spot attenuation exactly as today.
2. Compute one `signedNdotL = dot(normal, lightDirection)`.
3. If `signedNdotL > 0`, keep the existing front-light path and shadow with `shadowNormal`.
4. If `signedNdotL < 0`, return immediately unless backlighting is active; otherwise shadow with `-shadowNormal`.
5. If `signedNdotL == 0`, return without a shadow lookup.
6. Evaluate ordinary PBR lighting unchanged. It naturally returns zero for the back hemisphere and remains the sole owner of specular.
7. For an active back hemisphere, evaluate:
   - `backNdotL = max(-signedNdotL, 0)`;
   - the shared `EvaluateGiDiffuseBrdf(subsurfaceDirectionalDiffuseBase, dielectricF0, backNdotL, nDotV)`;
   - light radiance, attenuation, `backNdotL`, and the already-selected shadow factor exactly once.
8. Accumulate that result into `directBackDiffuseSource`.

Use the shading normal to define the visible front/back lobe and the geometric `shadowNormal` only for shadow bias, matching the current separation of BRDF and shadow responsibilities. Do not increase normal bias to force light through thick/self-occluding geometry.

### 5.4 Replace direct diffuse before every observer

After the directional and tiled-local light loops:

```glsl
vec3 originalDirectDiffuse = directDiffuseSource;
directDiffuseSource = ApplyGiSubsurfaceDiffuseSplit(
    originalDirectDiffuse,
    directBackDiffuseSource,
    subsurfaceWeight);
directLighting += directDiffuseSource - originalDirectDiffuse;
```

Perform this before:

- `MATERIAL_CAPTURE_LINEAR_DIRECT_DIFFUSE`;
- calculation of `MATERIAL_CAPTURE_LINEAR_DIRECT_SPECULAR`;
- directional-shadow overlays that intentionally alter final presentation;
- the C5 `outDirectDiffuseAndEmissive` write;
- final scene-colour composition.

The delta update preserves direct specular exactly while replacing only the diffuse part already included in `directLighting`. The direct-specular capture must continue to equal `directLighting - directDiffuseSource`.

C5's colour source must receive the post-split direct diffuse because it promises pre-DDGI direct-diffuse-plus-emissive scene-linear radiance. Do not change the C5 receiver payload ABI or semantics version: its geometric/shading normals and canonical material budget remain unchanged, while the colour attachment records the actual visible direct source.

### 5.5 Replace the indirect share conservatively

After the active forward path has resolved `finalDiffuseIndirect`, compute:

- opposite-normal environment irradiance with `EvaluateEnvironmentDiffuseIrradiance(environment, -normal)`;
- back environment radiance with `EvaluateGiDiffuseFromIrradiance(..., subsurfaceDiffuseReflectance)`;
- `indirectAo` exactly once on that environment-owned term;
- a convex split between the existing front `finalDiffuseIndirect` and the back environment result.

This common composition must run for GI-disabled, receiver-cache-required, exact-gather, transparent, and reflection-capture variants. Place or refactor it so `GLOBAL_ILLUMINATION_DEBUG_FINAL_INDIRECT` and any diagnostic field explicitly describing final indirect see the post-split value. DDGI-backend diagnostics that intentionally report raw front-side probe irradiance may remain raw, but their names/comments must not claim to be final scene indirect.

Do not port `ddgiDirectionalQueryForSubsurface` from `65c3dfb`. The current one-gather directional sidecar remains available to rough reflection, and cache-required variants retain semantic parity without a cache expansion. When the environment is disabled and only front-side DDGI exists, the back indirect share conservatively resolves to zero rather than inventing unoccluded energy.

### 5.6 Remove the old approximation

Delete the late additive wrap block completely. There must be no second subsurface addition in the extension tail and no compatibility toggle that can enable both models.

Keep the existing strength debug view. The existing linear direct-diffuse, direct-specular, final-indirect, material-normal, and shadow-receiver views provide the required component isolation without consuming a new stable debug enum value.

## 6. Automated tests

Create `Njulf.Tests/SubsurfaceBacklightingContractTests.cs`.

### 6.1 CPU numerical contract

Using the new `GiMaterialReferenceEvaluator` helpers and existing canonical diffuse functions, test:

1. Strength `0` returns the front diffuse exactly.
2. Strength `1` returns the tinted back diffuse exactly.
3. Strength `0.5` is the exact component-wise midpoint.
4. Strength values outside `[0, 1]` clamp to their nearest endpoint.
5. White tint preserves the available back budget; black tint removes it; intermediate RGB tint scales each channel independently.
6. Non-finite authored inputs are still rejected by existing material validation; normalized inputs produce finite, non-negative results.
7. A tint in `[0, 1]` never increases a canonical directional or hemispherical diffuse component.
8. Full metallic or full canonical transmission produces a zero back diffuse budget through the existing base calculation, without modifying the split weight.
9. Clearcoat and sheen attenuation appear once in both front and tinted-back budgets.
10. With equal-magnitude front/back cosines, the convex split cannot exceed the untinted ordinary diffuse response.
11. Multiplying by shadow factor zero produces zero backlight and factor one preserves the reference result.

Use representative `NdotL` and `NdotV` values near `0`, `0.5`, and `1` to cover Fresnel boundaries. Match GLSL single-precision operation order and use explicit tolerances.

### 6.2 Shader source/ordering contract

Read `forward.frag` and `gi_material_transport.glsl` and require:

- the two bounded helper functions and their use for both canonical diffuse budgets;
- one signed light cosine and explicit front/back/zero classification;
- `-shadowNormal` only on the contributing back-side shadow route;
- `EvaluateGiDiffuseBrdf` for the back lobe instead of duplicated Fresnel/albedo math;
- a separate back-diffuse accumulator;
- the direct split before direct-diffuse/direct-specular captures and before the C5 source write;
- the indirect split before the `FinalIndirect` output;
- opposite-normal environment irradiance;
- absence of the old view-dependent wrap expression;
- exactly one `SampleSimpleDdgiGather(` call;
- absence of `ddgiDirectionalQueryForSubsurface`;
- no changes to `ForwardDdgiReceiverCacheSample` or its 16-byte `uvec4` entry.

Use small helper methods to extract a function/block and compare source positions. Avoid relying on one broad `Does.Contain` assertion that can pass when the operations occur in the wrong order.

### 6.3 Existing contract coverage

Run and, only if necessary, update:

- `MaterialTransportV2Tests` for receiver-side classification;
- `MaterialChangeClassificationTests` for raster-only edits;
- `ForwardNearFieldDirectSourceContractTests` for C5 source ownership;
- `SimpleDdgiShaderMirrorTests` for one-gather and cache-entry contracts;
- `ShaderBuildTests` for embedded shader validity.

Do not change their semantic constants merely to accommodate the feature. A conflict means the shader ordering or ownership is wrong.

## 7. Implementation phases

### Phase 1: Freeze the energy model

- Add the CPU and GLSL bounded-budget/split helpers.
- Add endpoint, tint, canonical-layer, and conservation tests.
- Document that strength is a lobe split, not a brightness multiplier.

Exit gate: numerical tests pass and demonstrate that tint/strength cannot create diffuse energy.

### Phase 2: Implement direct backlighting

- Refactor `AccumulateLight` with explicit signed-horizon routing.
- Flip only the geometric shadow-bias normal on the back path.
- Accumulate and mix back diffuse before captures.
- Delete the late wrap term.

Exit gate: source-order tests pass for directional, point, and spot paths; direct-diffuse and derived-specular captures retain their exact ownership.

### Phase 3: Integrate conservative indirect backlighting

- Add opposite-normal environment diffuse.
- Apply the same canonical tint and strength split.
- Place final composition ahead of final-indirect diagnostics.
- Preserve the reflection directional query, one DDGI gather, and receiver-cache ABI.

Exit gate: cache-required, exact-gather, GI-disabled, transparent, and reflection-capture artifacts compile with the same stated subsurface policy.

### Phase 4: Qualify shader variants and advanced-GI outputs

- Rebuild all forward artifacts in Debug, Release, and ShippingPerformance.
- Verify simple variants eliminate the feature code.
- Validate C5-only, C4-only, combined C4/C5, material-provenance, receiver-cache, GI-disabled, transparent, weighted-OIT, and reflection-receiver variants.

Exit gate: no shader interface, location, semantics-version, or SPIR-V validation failure; C5 source captures the post-split direct result.

### Phase 5: Runtime quality and performance qualification

Run the fixture and campaign described below. Retain the implementation only after both feature-on correctness and feature-off non-regression gates pass.

## 8. Runtime validation

Use Vulkan validation and a deterministic thin-shell/wedge fixture with the existing wax material plus white, red-tinted, black-tinted, metallic, and transmission controls.

Exercise:

1. Rotate a directional light continuously from the front hemisphere through the horizon to the back. Front diffuse must fade; tinted back diffuse must appear only after crossing the horizon; no camera rotation may create light by itself.
2. Repeat with one point light and one spot light, including range and cone boundaries.
3. Place an external blocker between the light and the back surface. Both front and back diffuse must obey the matching shadow result, with no light leak at the flipped-bias transition.
4. Compare strengths `0`, `0.5`, and `1` under identical exposure. The result must interpolate rather than stack.
5. Test white, coloured, textured, and black subsurface tints. A black tint absorbs the split share instead of reverting to front diffuse.
6. Test normal-mapped, double-sided, and mirrored geometry. Back-face orientation and shadow bias must remain stable.
7. Test metallic and transmission controls. Their already-removed diffuse energy must not reappear through subsurface.
8. Disable every explicit light and use the linear direct-diffuse capture. The subsurface object must contribute no direct light.
9. Compare the linear direct-diffuse capture, derived direct-specular capture, C5 source/debug output, and final scene colour. The diffuse split must agree and direct specular must remain unchanged.
10. Compare GI disabled, environment-only, normal receiver-cache, and exact-gather diagnostic routes. Final indirect must use the same bounded environment-back policy and switching routes must not steal rough reflections.
11. Exercise reflection capture and transparent/weighted-OIT paths with a subsurface material to catch variant-only control-flow or output errors.
12. Inspect the existing `SubsurfaceStrength`, geometric-normal, shadow-factor, and `FinalIndirect` views during the sequence.

Require:

- no Vulkan validation warning/error;
- finite non-negative HDR output;
- no unshadowed glow, horizon discontinuity beyond the expected lobe crossing, view-locked wrap, light leak, or temporal flicker;
- no change to a strength-zero control beyond normal floating-point noise;
- stable specular highlights while the diffuse lobe is split;
- no cache/exact route change attributable to a stolen directional DDGI query.

Closed thick objects may remain self-shadowed. That is a correct limitation of this thin-surface approximation; do not “fix” it by weakening shadow tests.

## 9. Build and test commands

Run from the `Njulf` directory:

```powershell
dotnet test Njulf.Tests/Njulf.Tests.csproj --filter "FullyQualifiedName~SubsurfaceBacklightingContractTests|FullyQualifiedName~ForwardNearFieldDirectSourceContractTests|FullyQualifiedName~SimpleDdgiShaderMirrorTests"
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Debug
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c Release
dotnet build Njulf.Shaders/Njulf.Shaders.csproj -c ShippingPerformance
dotnet build Njulf.sln
dotnet test Njulf.sln
```

Do not use `NjulfShaderBuildMode=UseExisting` for qualification. No new shader artifact or embedded-resource count is expected because the implementation changes existing shared shader sources.

## 10. Performance qualification

The reverted aggregate commit explicitly reported a performance regression, especially in Sponza. Treat feature-off cost as a hard gate even though this extraction is much smaller.

### 10.1 Feature-off campaign

Run the existing three-cycle ABBA Bistro/Sponza campaign in Release and ShippingPerformance with unchanged assets, settings, camera trajectories, warmup, and measurement windows:

- `bistro-motion`;
- `sponza-horizontal-motion`.

Record whole-frame CPU/GPU P50/P95/P99 and `ForwardPlusPass`/`ForwardGiGatherPass` P50/P95/P99. Require no more than a 1% GPU- or CPU-frame P95 regression and no statistically supported non-target pass regression.

Compare compiled simple-forward artifacts before/after. The no-extension variants should eliminate subsurface inputs, the second accumulator, and backlight control flow. If they do not, restructure the preprocessor/constant path before accepting runtime numbers.

### 10.2 Feature-on fixture

Capture a fixed-resolution material-showcase route with a bounded number of subsurface pixels and each supported light type. Record:

- `ForwardPlusPass` GPU P50/P95/P99;
- whole-frame GPU P50/P95/P99;
- complete timing sample counts;
- shader register/spill statistics where the toolchain exposes them.

The implementation may cost work on enabled pixels, but must perform no second DDGI gather and at most one shadow lookup per contributing light. Use a provisional acceptance budget of the larger of 5% or 0.10 ms `ForwardPlusPass` P95 versus the old wrap fixture; exceeding it requires profiling and a separate material-routing optimization rather than weakening lighting correctness.

Use the repository's existing HDR comparison gates on strength-zero and non-subsurface controls: relative RMSE <= `0.005`, FLIP P95 <= `0.02`, ROI mean-luminance shift <= `0.02`, and ROI P95-luminance shift <= `0.03`. Feature-enabled images intentionally change and should be judged by the physical invariants and approved reference captures, not by similarity to the old wrap term.

## 11. File-level implementation map

- `Njulf.Shaders/gi_material_transport.glsl`
  - bounded subsurface diffuse-budget helper;
  - convex diffuse-split helper.
- `Njulf.Rendering/Data/GiMaterialReferenceEvaluator.cs`
  - operation-order-matched CPU mirrors for numerical tests.
- `Njulf.Shaders/forward.frag`
  - per-fragment resolved budgets;
  - explicit front/back light routing and flipped shadow bias;
  - direct accumulator replacement;
  - opposite-normal environment indirect split;
  - removal of the additive wrap term;
  - correct debug/capture/C5 ordering.
- `Njulf.Tests/SubsurfaceBacklightingContractTests.cs`
  - numerical energy tests and focused source/ordering contracts.
- `NjulfHelloGame/SampleMaterialShowcaseScene.cs`
  - reuse the existing wax material; change only if a deterministic validation pose/control material cannot be supplied by the capture harness.

No changes are planned for `GPUStructs.cs`, `MaterialTransportContracts.cs`, `MaterialTransportCompiler.cs`, `ForwardDdgiReceiverCacheSample`, `ForwardPlusPass`, `MeshPipeline`, bindless indices, render-graph declarations, or settings serialization.

## 12. Risks and mitigations

- **Energy duplication:** adding a back term after final colour recreates the current defect. Replace the diffuse share through a convex mix and test endpoint/conservation properties.
- **Canonical-layer drift:** reconstructing the BRDF from raw albedo can bypass transmission, clearcoat, sheen, or dielectric Fresnel. Derive back budgets from the existing canonical values and use `EvaluateGiDiffuseBrdf`.
- **Double metallic/transmission suppression:** keep strength independent; the canonical budgets already contain these factors once.
- **Shadow acne or peter-panning:** flip the geometric bias normal only for a genuinely back-facing contributing light. Do not alter global bias settings.
- **Thick-object expectations:** shadow maps cannot distinguish desired internal diffusion from real occlusion. Document the thin-surface limitation and validate thin fixtures.
- **Normal-map horizon mismatch:** use the shading normal for BRDF hemisphere selection and geometric normal for bias. Include aggressive normal maps and mirrored/double-sided cases in validation.
- **Direct-source disagreement:** performing the split after captures or C5 publication produces mutually inconsistent radiance. Lock ordering with source-position assertions and runtime captures.
- **Misleading final-indirect diagnostics:** apply the indirect split before outputs described as final; explicitly label raw DDGI diagnostics as backend values.
- **Receiver-cache regression:** a second gather or payload expansion would penalize every opaque pixel. Keep the one-gather/16-byte assertions and use environment-only backside indirect in this phase.
- **Reflection regression:** do not repurpose the directional DDGI lobe. Rough reflection retains its current query and environment fallback.
- **Register pressure:** a second RGB accumulator can lengthen live ranges in full-material fragments. Verify simple-variant dead-code elimination and benchmark full variants.
- **HDR tint amplification:** clamp texture-combined subsurface tint before budget multiplication.

## 13. Definition of done

- The view-dependent additive wrap expression is removed.
- Directional, point, and spot lights produce shadowed back-side diffuse only on the opposite hemisphere.
- Front and back diffuse are mixed by bounded authored strength; specular is unchanged.
- Back budgets reuse the canonical diffuse/Fresnel model and bounded existing tint.
- Direct-diffuse capture, derived direct-specular capture, C5 source output, reflection capture, and final scene colour agree.
- Opposite-normal environment indirect is mixed before `FinalIndirect` diagnostics.
- No extra DDGI gather, receiver-cache ABI change, or reflection-direction takeover is introduced.
- Strength-zero and non-subsurface materials remain within existing quality and 1% performance non-regression gates.
- Focused tests, all forward shader variants, full build, and full test suite pass.
- Runtime light-rotation, blocker, material-control, GI-route, transparency, and advanced-GI validation passes without relevant Vulkan errors or visual instability.
