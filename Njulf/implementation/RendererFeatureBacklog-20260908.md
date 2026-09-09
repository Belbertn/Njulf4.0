# Renderer feature backlog

Date: 2026-09-08

This backlog records the substantial rendering gaps identified in the current source review, beyond the separate anti-aliasing and motion-stability quality pass. It distinguishes missing capabilities, deliberately limited implementations, and gaps in integration evidence. These are priorities for future work; this review did not run new GPU captures or establish new runtime defects.

## Recommended direction

For the current environment-focused renderer, start with **physical atmosphere and aerial perspective**, then add clouds and cloud shadows. These would extend the outdoor rendering capabilities while building on the existing sky, fog, lighting, and GI systems.

Subsurface scattering becomes the first priority for a character-focused project. A complete water system becomes a priority when a representative scene makes substantial use of water. Camera and color controls can progress independently. A baked lighting backend depends on the intended hardware and content targets.

| Feature | Current status | Main missing capability |
| --- | --- | --- |
| Physical atmosphere and clouds | Analytic sky and volumetric fog exist | Consistent aerial perspective, volumetric clouds, and cloud shadows coupled to lighting and GI |
| Fuller subsurface scattering | Bounded thin-surface backlighting exists | Diffusion profiles, scattering distance, thickness-aware transmission, and local indirect-light participation |
| Complete water system | Optical and transport building blocks exist | Waves, foam, shoreline transitions, underwater scattering, and camera crossings |
| Camera and color pipeline | Exposure, bloom, tone mapping, and AA exist | Depth of field, motion blur, white balance, and color-grading LUTs |
| Baked lighting backend | Lighting centers on DDGI and runtime infrastructure | Baked lightmaps or irradiance volumes for suitable static content and hardware |

## 1. Physical atmosphere and clouds

The environment currently uses a Hosek-Wilkie clear-sky model with twilight/night extensions. Volumetric fog is also implemented. The outstanding feature is a coherent atmosphere that accounts for scattering between the camera and distant geometry, alongside the visible sky and scene illumination.

- [ ] Add physically consistent aerial perspective for distant geometry and changes in camera altitude.
- [ ] Integrate atmospheric scattering with the existing sun, sky, environment lighting, and fog composition.
- [ ] Add volumetric clouds.
- [ ] Couple cloud shadows and changing cloud cover to direct lighting and GI.

Relevant source: [ProceduralSkyModel.cs](../Njulf.Rendering/Resources/ProceduralSkyModel.cs), [EnvironmentManager.cs](../Njulf.Rendering/Resources/EnvironmentManager.cs), and the [remaining GI roadmap](Complete/GlobalIlluminationRemainingFeaturesRoadmap-20260822.md).

Design reference: [Hillaire, A Scalable and Production Ready Sky and Atmosphere Rendering Technique](https://onlinelibrary.wiley.com/doi/10.1111/cgf.14050).

## 2. Fuller subsurface scattering

The existing implementation divides diffuse energy between front-side and tinted back-side lighting. Indirect backlighting samples opposite-normal environment irradiance. Its documented scope explicitly excludes diffusion profiles, optical thickness, mean free path, and transport through closed volumes.

This is the clearest example of a feature whose current implementation covers a useful subset of the broader material behavior.

- [ ] Add diffusion profiles and scattering-distance controls for materials such as skin, wax, and marble.
- [ ] Add thickness-aware transmission and suitable authoring inputs.
- [ ] Improve local indirect-light participation in subsurface and translucent materials.
- [ ] Preserve the existing diffuse/specular energy ownership when extending the model.

Relevant source: [subsurface backlighting implementation](Complete/SubsurfaceBacklightingImplementationPlan-20260824.md) and [forward.frag](../Njulf.Shaders/forward.frag).

Design reference: [Epic's subsurface profile shading model](https://dev.epicgames.com/documentation/en-us/unreal-engine/subsurface-profile-shading-model-in-unreal-engine).

## 3. Complete water system

Water boundaries, animated normals, refraction, absorption, caustics, and deforming-geometry participation already exist. The material showcase exercises water optics. The source review did not find a complete system for the broader surface and underwater behaviors below.

- [ ] Add authored wave generation and surface displacement.
- [ ] Add foam and shoreline transitions.
- [ ] Add underwater scattering and a coherent above-water/below-water camera transition.
- [ ] Integrate these behaviors with the existing reflection, refraction, caustic, and dynamic-geometry paths.

Relevant source: [thick_transmission_transport.glsl](../Njulf.Shaders/thick_transmission_transport.glsl), [MaterialTransportContracts.cs](../Njulf.Rendering/Data/MaterialTransportContracts.cs), and [SampleMaterialShowcaseScene.cs](../NjulfHelloGame/SampleMaterialShowcaseScene.cs).

## 4. Camera and color pipeline

The production pipeline includes auto exposure, bloom, tone mapping, and anti-aliasing. The review did not find integrated depth-of-field, motion-blur, white-balance, or color-grading LUT implementations.

- [ ] Add white balance and color-grading LUT support.
- [ ] Add depth of field with camera controls.
- [ ] Add motion blur using the existing motion-vector infrastructure.
- [ ] Define composition and color-space behavior with exposure, bloom, tone mapping, and AA.

Relevant source: [ProductionRenderPipelineDeclaration.cs](../Njulf.Rendering/Pipeline/ProductionRenderPipelineDeclaration.cs), [RenderSettings.cs](../Njulf.Rendering/Data/RenderSettings.cs), and [tonemap_composite.frag](../Njulf.Shaders/tonemap_composite.frag).

## 5. Baked lighting backend

A baked lightmap or irradiance-volume backend remains an explicit roadmap item. It would provide another lighting option for static environments and hardware without practical ray-query performance. Its priority depends on whether those are shipping targets.

- [ ] Define the intended hardware and static-content use cases.
- [ ] Add a baking, asset-storage, and runtime-sampling path for the selected representation.
- [ ] Define how baked lighting combines with dynamic objects and existing direct lighting without duplicating indirect energy.

Relevant source: [remaining GI roadmap](Complete/GlobalIlluminationRemainingFeaturesRoadmap-20260822.md).

## Integration evidence for existing advanced features

Analytical area lights, volumetric fog, hybrid ray-query reflections, thick refraction, and caustic support are already implemented. Their existence should be distinguished from evidence that their combinations behave correctly.

The current GI all-on qualifier disables hybrid reflections, selects the bounded thick-transmission approximation, and disables dispersion. That route therefore does not establish the combined behavior of those feature families. This is a limitation of that qualification route, not proof that the implementations fail or that no other tests exist.

- [ ] Establish a representative combined scene exercising DDGI, hybrid reflections, ray-query thick transmission, and caustics.
- [ ] Verify effective runtime paths and evaluate their interaction under camera, lighting, and geometry changes.
- [ ] Record rendered reference comparisons and GPU costs for the exercised combinations.

Relevant evidence: [GI all-on runtime qualification](../docs/rendering/gi-all-on-qualification.md) and the [roadmap's outstanding reference, traversal, and device checks](Complete/GlobalIlluminationRemainingFeaturesRoadmap-20260822.md).
