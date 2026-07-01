# Renderer Settings Reference

This is a readable index of renderer-facing settings exposed by `VulkanRenderer.Settings`,
plus the nearby renderer-level toggles and sample runtime controls.

## Renderer-Level Toggles

These live directly on `VulkanRenderer`, not inside `RenderSettings`.

| Setting | Purpose |
| --- | --- |
| `EnableHiZOcclusion` | Enables Hi-Z occlusion culling. |
| `EnableAdaptiveHiZOcclusion` | Allows Hi-Z occlusion to be adaptively suppressed/probed based on measured benefit. |
| `EnableDepthPrePass` | Enables the depth pre-pass used by later depth-dependent passes. |
| `EnableTransparentPass` | Enables the transparent rendering pass. |
| `EnableMeshletDebugView` | Enables meshlet debug rendering. |

## Top-Level RenderSettings

| Setting | Purpose |
| --- | --- |
| `QualityPreset` | Active quality preset: `Low`, `Medium`, `High`, `Ultra`, `DdgiHigh`. |
| `ResolutionScale` | Base internal render resolution scale. |
| `EffectiveResolutionScale` | Resolved scale after dynamic resolution clamping. |
| `DynamicResolution` | Dynamic resolution settings bucket. |
| `ToneMapper` | Tone mapper: `None`, `Reinhard`, `AcesFitted`. |
| `Exposure` | Manual exposure multiplier. |
| `AutoExposure` | Auto-exposure settings bucket. |
| `ShowRawHdrSceneColor` | Shows the raw HDR scene color instead of the composited output. |
| `FeatureIsolation` | Feature isolation mode: `FullFrame`, `Geometry`, `Shadows`, `PostProcessing`, `Reflections`, `Animation`, `Particles`. |
| `HiZTestMode` | Hi-Z test mode: `Off`, `Bounds4Tap`, `Full6Point5Tap`. |
| `UseSecondaryCommandBuffers` | Enables secondary command buffers for eligible passes. |
| `UseCameraDependentCpuScenePayload` | Enables camera-dependent CPU scene payload generation. |
| `UseCpuMeshletFrustumCulling` | Enables CPU meshlet frustum culling. |

`RenderSettings` defaults to `DdgiHigh`, which is the DDGI-only production profile. `Ultra` remains selectable for the old ray-query hybrid path.

## Dynamic Resolution

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables dynamic resolution scaling. |
| `MinimumScale` | Lowest allowed render scale. |
| `MaximumScale` | Highest allowed render scale. |
| `TargetFrameMilliseconds` | Target frame time used for scaling decisions. |
| `AdjustmentRate` | Rate at which scale changes. |

## Auto Exposure

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables auto exposure. |
| `TargetLuminance` | Target average luminance. |
| `MinExposure` | Minimum computed exposure. |
| `MaxExposure` | Maximum computed exposure. |
| `AdaptationSpeed` | Exposure adaptation speed. |
| `MinLogLuminance` | Lower log-luminance range for sampling. |
| `MaxLogLuminance` | Upper log-luminance range for sampling. |
| `SamplingStride` | Sampling stride for luminance reads. |
| `LogLuminanceRange` | Derived luminance range. |

## Shadows

| Setting | Purpose |
| --- | --- |
| `DirectionalShadowsEnabled` | Enables directional shadows. |
| `SpotShadowsEnabled` | Enables spot light shadows. |
| `PointShadowsEnabled` | Enables point light shadows. |
| `DirectionalShadowMapSize` | Directional shadow map resolution. |
| `DirectionalCascadeCount` | Number of directional cascades. |
| `MaxShadowDistance` | Maximum directional shadow distance. |
| `NormalBias` | Directional normal bias. |
| `SlopeScaledDepthBias` | Directional slope-scaled depth bias. |
| `ConstantDepthBias` | Directional constant depth bias. |
| `PcfRadius` | Directional PCF radius. |
| `MaxShadowedSpotLights` | Maximum shadowed spot lights. |
| `SpotShadowAtlasSize` | Spot shadow atlas size. |
| `SpotShadowTileSize` | Spot shadow tile size. |
| `SpotShadowAtlasCapacity` | Derived spot shadow atlas capacity. |
| `SpotNormalBias` | Spot shadow normal bias. |
| `SpotConstantDepthBias` | Spot shadow constant depth bias. |
| `SpotSlopeScaledDepthBias` | Spot shadow slope-scaled depth bias. |
| `SpotPcfRadius` | Spot shadow PCF radius. |
| `MaxShadowedPointLights` | Maximum shadowed point lights. |
| `PointShadowMapSize` | Point shadow cubemap face resolution. |
| `PointNormalBias` | Point shadow normal bias. |
| `PointConstantDepthBias` | Point shadow constant depth bias. |
| `PointSlopeScaledDepthBias` | Point shadow slope-scaled depth bias. |
| `PointPcfRadius` | Point shadow PCF radius. |
| `DebugView` | Shadow debug view. |

Shadow debug views:

- `None`
- `CascadeOverlay`
- `ShadowMapPreview`
- `ReceiverFactor`
- `SpotAtlasPreview`
- `PointCubemapFacePreview`
- `LocalShadowSelection`

## Bloom

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables bloom. |
| `Intensity` | Bloom contribution strength. |
| `Threshold` | Bright-pass threshold. |
| `Knee` | Soft threshold knee. |
| `Radius` | Bloom spread radius. |
| `MipCount` | Number of bloom mips. |
| `DebugView` | Bloom debug view. |
| `DebugMipLevel` | Bloom debug mip index. |

Bloom debug views:

- `None`
- `ExtractMask`
- `DownsampleMip`
- `UpsampleResult`
- `BloomOnly`

## Environment

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables environment lighting. |
| `SourceKind` | Environment source: `ProceduralSky`, `HdrEquirectangular`, `Cubemap`. |
| `SourcePath` | Optional source texture path. |
| `TexturePrecision` | Environment texture precision: `Float16`, `Float32`. |
| `SkyIntensity` | Skybox intensity. |
| `DiffuseIntensity` | Diffuse IBL intensity. |
| `SpecularIntensity` | Specular IBL intensity. |
| `RotationRadians` | Environment rotation. |
| `EnvironmentSize` | Environment cubemap size. |
| `IrradianceSize` | Irradiance cubemap size. |
| `PrefilteredSize` | Prefiltered cubemap size. |
| `BrdfLutSize` | BRDF LUT size. |
| `DebugView` | Environment debug view. |
| `DebugMipLevel` | Environment debug mip level. |

Environment debug views:

- `None`
- `SkyboxOnly`
- `IrradianceCubemap`
- `PrefilteredEnvironmentMip`
- `BrdfLut`
- `DiffuseIblOnly`
- `SpecularIblOnly`
- `AmbientOcclusion`

## Reflections

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables reflections. |
| `Mode` | Reflection mode. |
| `MaxProbes` | Maximum reflection probes. |
| `MaxProbesPerPixel` | Maximum probes blended per pixel. |
| `ProbeResolution` | Reflection probe cubemap resolution. |
| `Intensity` | Reflection intensity. |
| `GlobalFallbackIntensity` | Global fallback reflection intensity. |
| `BoxProjectionEnabled` | Enables box projection. |
| `ProbeBlendingEnabled` | Enables probe blending. |
| `CaptureOnLoad` | Captures probes when loaded. |
| `MaxProbeCapturesPerFrame` | Probe capture budget per frame. |
| `DebugView` | Reflection debug view. |
| `DebugProbeIndex` | Debug probe index. |
| `DebugCubemapFace` | Debug cubemap face. |
| `DebugMipLevel` | Debug mip level. |

Reflection modes:

- `Disabled`
- `GlobalEnvironmentOnly`
- `StaticProbes`
- `StaticProbesAndSsr`
- `StaticProbesAndPlanar`

Reflection debug views:

- `None`
- `ProbeInfluence`
- `ProbeIndex`
- `ProbeBlendWeights`
- `ProbeCubemapFace`
- `ProbePrefilterMip`
- `BoxProjectionDirection`
- `SsrMask`
- `PlanarReflection`
- `LocalReflectionOnly`
- `GlobalFallbackOnly`

## Ambient Occlusion

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables ambient occlusion. |
| `Mode` | AO mode: `Disabled`, `Ssao`, `Gtao`. |
| `ResolutionScale` | AO render scale. |
| `Radius` | AO sample radius. |
| `Intensity` | AO strength. |
| `Bias` | AO depth bias. |
| `Power` | AO contrast power. |
| `SampleCount` | AO sample count. |
| `BlurRadius` | AO blur radius. |
| `DepthSigma` | Depth-aware blur sigma. |
| `NormalSigma` | Normal-aware blur sigma. |
| `UseSceneNormals` | Uses scene normals for AO. |
| `DebugView` | AO debug view. |

AO debug views:

- `None`
- `RawAo`
- `BlurredAo`
- `FinalAo`
- `ReconstructedNormal`
- `LinearDepth`

## Global Illumination

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables diffuse global illumination. |
| `Mode` | GI mode: `Disabled`, `Ssgi`, `Ddgi`, `Hybrid`, `RayQueryHybrid`. |
| `UseSsgi` | Allows the SSGI backend when the selected mode supports it. |
| `UseDdgi` | Allows DDGI probe lighting when the selected mode supports it. |
| `UseRayQueryBackend` | Allows ray-query DDGI updates when DDGI is effective and the device supports it. |
| `DdgiQualityTier` | DDGI quality tier: `DdgiLow`, `DdgiMedium`, `DdgiHigh`, `DdgiUltra`. |
| `DdgiProbeClassificationEnabled` | Enables DDGI probe classification. |
| `DdgiProbeRelocationEnabled` | Enables DDGI probe relocation. |
| `DdgiCameraRelativeEnabled` | Enables camera-relative DDGI clipmaps. |
| `DdgiAdaptiveBudgetingEnabled` | Enables GPU-time-driven adaptive DDGI update budgets. |
| `DdgiSchedulerMode` | DDGI probe scheduler mode: `CpuReference`, `Gpu`, or `CpuGpuCompare`. Defaults to CPU reference while the GPU scheduler is gated. |
| `DdgiGpuSchedulerReadbackValidationEnabled` | Enables optional frame-late GPU scheduler validation readback. |
| `DdgiCompareModeUseGpuQueueForRendering` | In `CpuGpuCompare`, renders with the GPU-produced queue while the CPU scheduler runs as the validation reference. Disable to render the CPU queue and skip the GPU schedule pass. |
| `DdgiGpuSchedulerMaxDirtyRegions` | Maximum dirty regions uploaded for GPU scheduler input. |
| `DdgiGpuSchedulerCandidateBucketCount` | Priority bucket count reserved for GPU scheduler candidate compaction. |
| `DdgiGpuSchedulerLocalScanFraction` | Fraction of the bounded steady-frame GPU scheduler scan list reserved for authored local volumes before age-refresh remainder. |
| `DdgiGpuSchedulerCascade0ScanFraction` | Fraction of the bounded steady-frame GPU scheduler scan list reserved for near camera-relative cascade probes before age-refresh remainder. |
| `DdgiGpuSchedulerSafetyScanFraction` | Fraction of the bounded steady-frame GPU scheduler scan list reserved for camera-relative safety-shell probes before age-refresh remainder. |
| `DdgiGpuSchedulerDirtyScanFraction` | Fraction of the bounded steady-frame GPU scheduler scan list reserved for dirty-region probes before age-refresh remainder. |
| `DdgiGpuSchedulerFallbackOnValidationFailure` | Allows GPU scheduler validation failures to fall back to the CPU reference scheduler. |
| `DdgiGpuSchedulerForceCpuFallback` | Forces CPU reference scheduling while still reporting GPU scheduler fallback diagnostics. |
| `DdgiGpuSchedulerAutoRetryAfterFallback` | Allows a latched GPU scheduler fallback to retry after the configured stable-frame count. Defaults off for conservative production behavior. |
| `DdgiGpuSchedulerValidationFailureThreshold` | Consecutive validation mismatch count required before latching CPU fallback. |
| `DdgiGpuSchedulerFallbackRetryStableFrames` | Stable fallback frames required before an opt-in GPU scheduler retry. |
| `DdgiGpuScheduleTimeBudgetMilliseconds` | Target GPU scheduler pass budget used for schedule-specific P95 diagnostics. |
| `DdgiGpuTotalUpdateTimeBudgetMilliseconds` | Target total GPU DDGI update budget used by adaptive update selection in GPU scheduler modes. |
| `DdgiAdaptiveBudgetHysteresisFraction` | Fractional timing headroom before adaptive DDGI reduces work. |
| `DdgiEmergencyDegradeGpuTimeMultiplier` | GPU-time multiplier that triggers emergency DDGI degradation. |
| `DdgiAsyncComputeEnabled` | Allows DDGI update work on async compute when renderer async compute is also enabled. |
| `DdgiMaxProbeUpdatesPerFrame` | Hard probe-update count cap. |
| `DdgiProbeUpdatePrimaryRayBudget` | Steady-frame primary ray budget for scheduled probe updates. |
| `DdgiColdStartMaxProbeUpdatesPerFrame` | Cold-start probe-update count cap used before GPU timing is available. |
| `DdgiColdStartPrimaryRayBudget` | Cold-start primary ray budget used before GPU timing is available. |
| `DdgiMinimumProbeRefreshFrames` | Maximum target frame age for active probes before the adaptive scheduler preserves refresh work. |
| `DdgiMaxRaysPerProbe` | Upper bound for rays per updated probe. |
| `DdgiCascade0RaysPerProbe` ... `DdgiCascade3RaysPerProbe` | Per-cascade ray budgets, clamped by `DdgiMaxRaysPerProbe`. |
| `DdgiCascade0MaxRayDistance` ... `DdgiCascade3MaxRayDistance` | Explicit per-cascade ray traversal distances for camera-relative DDGI. |
| `DdgiMaxShadedLights` | Maximum lights shaded at a DDGI ray hit before the shader hard cap. |
| `DdgiMaterialTextureMaxCascade` | Highest camera-relative cascade that samples material textures in DDGI hit shading; `-1` disables cascade texture sampling while authored volumes still sample textures. |

`DdgiHigh` is the default DDGI-only production profile: DDGI mode, SSGI disabled, ray-query backend requested, probe classification/relocation enabled, camera-relative clipmaps enabled, AO/reflections enabled, and DDGI async compute enabled. The production DDGI tier budgets are explicit: `DdgiLow` uses 2 cascades at 16x6x16 with a 64 MB DDGI memory target, `DdgiMedium` uses 3 cascades at 20x8x20 with 128 MB, `DdgiHigh` uses 3 cascades at 24x14x24 with 192 MB, and `DdgiUltra` uses 4 cascades at 32x12x32 with 384 MB. Dense probe density is reserved for admitted authored local volumes such as interiors and alleys; the camera-relative clipmap remains tier-bounded instead of allocating a dense all-world volume. Milestone 9 validation reports include a DDGI production gate for required benchmark scenes and fail if SSGI resources/passes remain active in `DdgiHigh`.

DDGI debug views include `DdgiCoverage`, `DdgiCascadeSelection`, `DdgiCascadeBlendWeight`, `DdgiUpdateReasons`, and `DdgiRayBudget` for validating volume selection, probe validity, update reasons, and ray-budget behavior.

DDGI debug screenshots are designed to be self-identifying without console history: DDGI debug views render a category-colored screen border, a top-left checker/binary view-id badge, and a bottom-left RGB legend strip. The sample also prints a `DDGI debug legend` line when DDGI debug views are selected. Screenshots requested while GI debug or the DDGI diagnostics filter is active include a filename suffix such as `-gi-DdgiSupportCoverage-ddgi-filter.png`.

Performance snapshots expose Phase 1 GPU scheduler resource diagnostics separately from aggregate DDGI memory: scheduler buffer bytes, dirty-region/candidate/group/prefix capacities, dirty-region upload count, dirty-region overflow count, and scheduler input upload bytes. Phase 2 adds `DdgiSchedulePass` before `DdgiTracePass` and reports `GpuDdgiScheduleMicroseconds` as part of aggregate `GpuDdgiUpdateMicroseconds`. Phase 3 GPU scheduler mode writes the DDGI update queue and scheduler counters on GPU; CPU reference mode still builds and uploads the CPU queue. Phase 4 copies scheduler counters through a frame-late diagnostics readback ring and reports GPU-produced request/candidate/overflow/budget counters without same-frame CPU synchronization. Phase 5 compare mode can run the CPU reference scheduler before GPU scheduling, copy the GPU queue frame-late, and report validation status, mismatch count, first mismatch, and CPU/GPU request counts. Phase 6 feeds adaptive budgets from frame-late DDGI GPU update timing in GPU scheduler modes, falls back to estimated timing while timestamps are pending, caps GPU candidate generation before prefix/compact/finalize, applies priority quotas with downward carry, protects visible/local/new-cell budget floors, and reports scheduler pass P95/over-budget diagnostics. Phase 7 enforces production tier memory budgets and reports DDGI probe volume buffer, probe state buffer, irradiance/visibility atlas, ray scratch, update queue, relocation/classification buffer, scheduler buffers, gather tile buffer, and local slot reserved pool bytes in performance snapshots and sample diagnostics output. Phase 8 hardens relocation and classification: trace output stores nearest-hit, close-hit, backface, miss, and relocation-direction evidence; relocation is confidence-gated free-space correction rather than surface snapping; and inactive probe coverage falls back to environment lighting instead of black. Phase 9 makes one-frame DDGI cache publication explicit: the probe-volume header, performance snapshots, and sample diagnostics expose cache generation, last updated frame serial, and cache warmup state, and forward shading treats cold cache as zero DDGI confidence so environment fallback is used instead of black. Phase 10 adds unit coverage for scheduler input clamping and scripted sample support for `cpu-reference`, `gpu`, and `cpu-gpu-compare` DDGI scheduler snapshot runs.

DDGI-only debug shortcut cycle order: `FinalIndirect`, `DdgiIrradiance`, `DdgiVisibility`, `DdgiVisibilityMoments`, `DdgiProbeIndex`, `DdgiProbeState`, `DdgiProbeRelocation`, `DdgiLeakClamp`, `DdgiCoverage`, `DdgiCascadeSelection`, `DdgiCascadeBlendWeight`, `DdgiUpdateReasons`, `DdgiRayBudget`, `None`.

## Anti-Aliasing

| Setting | Purpose |
| --- | --- |
| `Mode` | AA mode. |
| `EffectiveMode` | Resolved AA mode. |
| `DebugView` | AA debug view. |
| `FxaaContrastThreshold` | FXAA absolute contrast threshold. |
| `FxaaRelativeThreshold` | FXAA relative contrast threshold. |
| `FxaaSubpixelBlending` | FXAA subpixel blending amount. |
| `SmaaPredicationEnabled` | Enables SMAA predication. |
| `JitterEnabled` | Enables camera jitter for temporal AA. |
| `JitterSampleCount` | Jitter pattern sample count. |
| `TaaFeedbackMin` | Minimum TAA feedback. |
| `TaaFeedbackMax` | Maximum TAA feedback. |
| `TaaVelocityRejectionScale` | TAA velocity rejection scale. |
| `EffectiveSmaaSpatialSampleCount` | Resolved SMAA spatial sample count. |
| `EffectiveSmaaUsesSpatialMultisampling` | Whether resolved SMAA uses spatial multisampling. |
| `EffectiveSmaaThreshold` | Resolved SMAA threshold. |
| `EffectiveSmaaMaxSearchSteps` | Resolved SMAA max search steps. |
| `EffectiveSmaaMaxSearchStepsDiagonal` | Resolved SMAA diagonal search steps. |
| `EffectiveSmaaCornerRounding` | Resolved SMAA corner rounding. |
| `EffectiveSmaaDiagonalEnabled` | Whether resolved SMAA diagonal search is enabled. |
| `EffectiveSmaaCornerEnabled` | Whether resolved SMAA corner detection is enabled. |
| `EffectiveSmaaQuality` | Resolved SMAA quality level. |

AA modes:

- `None`
- `Fxaa`
- `SmaaLow`
- `SmaaMedium`
- `SmaaHigh`
- `Taa`

AA debug views:

- `None`
- `InputColor`
- `FxaaLuma`
- `SmaaEdges`
- `SmaaBlendWeights`
- `MotionVectors`
- `JitterPattern`
- `TaaHistory`

## Fog

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables fog. |
| `Mode` | Fog mode. |
| `ColorMode` | Fog color mode. |
| `Color` | Constant fog color. |
| `ColorBlend` | Blend factor for sky/constant fog color modes. |
| `Density` | Distance fog density. |
| `StartDistance` | Distance where fog starts. |
| `EndDistance` | Distance where fog reaches its far range. |
| `Height` | Height fog reference height. |
| `HeightFalloff` | Height fog falloff. |
| `HeightDensity` | Height fog density. |
| `MaxOpacity` | Maximum fog opacity. |
| `DirectionalInscatteringEnabled` | Enables directional inscattering. |
| `DirectionalInscatteringColor` | Inscattering color. |
| `DirectionalInscatteringDirection` | Override inscattering direction. |
| `DirectionalInscatteringIntensity` | Inscattering intensity. |
| `DirectionalInscatteringExponent` | Inscattering angular exponent. |
| `DebugView` | Fog debug view. |

Fog modes:

- `Disabled`
- `Distance`
- `Height`
- `DistanceAndHeight`

Fog color modes:

- `ConstantColor`
- `SkyColor`
- `SkyAndConstantBlend`

Fog debug views:

- `None`
- `FogFactor`
- `Transmittance`
- `DistanceFog`
- `HeightFog`
- `Inscattering`
- `LinearDepth`
- `WorldHeight`
- `FoggedScene`

## Transparency

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables transparency settings. |
| `Mode` | Transparency mode: `SortedAlphaBlend`, `WeightedBlendedOit`. |
| `DebugView` | Transparency debug view. |
| `ReceiveShadows` | Transparent surfaces receive shadows. |
| `SampleReflections` | Transparent surfaces sample reflections. |
| `SortPerMeshlet` | Sorts transparency at meshlet granularity. |
| `MaxTransparentMeshlets` | Transparent meshlet budget. |
| `AlphaDiscardThreshold` | Alpha discard threshold. |

Transparency debug views:

- `None`
- `AlphaMode`
- `AlphaValue`
- `AlphaCutoff`
- `TransparentSortOrder`
- `Overdraw`
- `WeightedOitAccumulation`
- `WeightedOitRevealage`

## Decals

| Setting | Purpose |
| --- | --- |
| `GeometryDecalsEnabled` | Enables geometry decals. |
| `ProjectedDecalsEnabled` | Enables projected decals. |
| `DebugView` | Decal debug view. |
| `GeometryDepthBias` | Geometry decal depth bias. |
| `GeometrySlopeScaledDepthBias` | Geometry decal slope-scaled depth bias. |
| `MaxProjectedDecals` | Maximum projected decals. |
| `MaxProjectedDecalsPerTile` | Maximum projected decals per tile. |
| `MaxProjectedDecalsPerPixel` | Maximum projected decals per pixel. |

Decal debug views:

- `None`
- `GeometryDecalMask`
- `DecalLayer`
- `DecalDepthBias`
- `ProjectedDecalVolume`
- `ProjectedDecalAtlas`

## Animation

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables animation support. |
| `SkinningMode` | Skinning mode: `Disabled`, `CpuDebug`, `GpuCompute`. |
| `DebugView` | Animation debug view. |
| `MaxJointsPerSkeleton` | Joint budget per skeleton. |
| `MaxAnimatedInstances` | Animated instance budget. |
| `UpdateWhenOffscreen` | Updates animation when offscreen. |
| `UseConservativeBounds` | Uses conservative animated bounds. |
| `BoundsPadding` | Animated bounds padding. |

Animation debug views:

- `None`
- `SkinnedObjects`
- `JointWeights`
- `JointIndex`
- `SkinningError`
- `Skeleton`
- `AnimatedBounds`
- `ClipTime`

## Particles

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables particle rendering/simulation. |
| `SimulationMode` | Simulation mode: `Cpu`, `Gpu`. |
| `DebugView` | Particle debug view. |
| `MaxParticles` | Particle budget. |
| `MaxEmitters` | Emitter budget. |
| `MaxBatches` | Batch budget. |
| `MaxTrails` | Trail budget. |
| `MaxTrailSegments` | Trail segment budget. |
| `SoftParticlesEnabled` | Enables soft particles. |
| `SoftParticleDistance` | Soft particle fade distance. |
| `DepthTestEnabled` | Enables depth testing for particles. |
| `ReceiveFog` | Particles receive fog. |
| `UsePremultipliedAlphaByDefault` | Defaults particles to premultiplied alpha. |
| `GlobalSpawnRateScale` | Global spawn-rate multiplier. |
| `GlobalVelocityScale` | Global velocity multiplier. |
| `GlobalEmissiveScale` | Global emissive multiplier. |
| `DistanceCullMultiplier` | Distance culling multiplier. |
| `MaxUploadBytesPerFrame` | Particle upload budget. |

Particle debug views:

- `None`
- `Bounds`
- `Overdraw`
- `SoftParticleFade`
- `FlipbookFrame`
- `SortOrder`
- `Lifetime`
- `Velocity`
- `EmitterId`
- `BatchId`
- `BudgetHeatmap`

## Materials

| Setting | Purpose |
| --- | --- |
| `DebugView` | Material debug view. |

Material debug views:

- `None`
- `FeatureFlags`
- `BaseColor`
- `Metallic`
- `Roughness`
- `NormalStrength`
- `WorldNormal`
- `EmissiveIntensity`
- `ClearcoatFactor`
- `ClearcoatRoughness`
- `SheenColor`
- `SheenRoughness`
- `AnisotropyStrength`
- `AnisotropyDirection`
- `Transmission`
- `Ior`
- `VolumeThickness`
- `AttenuationColor`
- `SubsurfaceStrength`
- `SpecularFactor`
- `SpecularColor`
- `IridescenceFactor`
- `IridescenceThickness`
- `Dispersion`

## Foliage

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables foliage. |
| `GpuDrivenEnabled` | Enables GPU-driven foliage path. |
| `HiZCullingEnabled` | Enables Hi-Z foliage culling. |
| `CastShadows` | Foliage casts shadows. |
| `IndirectMeshletDispatchEnabled` | Enables indirect foliage meshlet dispatch. |
| `FarImpostorsEnabled` | Enables far impostors. |
| `MotionVectorsEnabled` | Enables foliage motion vectors. |
| `LocalShadowsEnabled` | Enables local foliage shadows. |
| `GrassShadowDistance` | Grass shadow distance. |
| `GrassShadowDensityScale` | Grass shadow density scale. |
| `MaxDrawDistance` | Maximum foliage draw distance. |
| `DensityScale` | Foliage density scale. |
| `MaxVisibleClusters` | Visible cluster budget. |
| `MaxVisibleMeshletDraws` | Visible meshlet draw budget. |
| `MaxLocalShadowedSpotLights` | Local shadowed spot light budget. |
| `MaxLocalShadowedPointLights` | Local shadowed point light budget. |
| `MaxLocalShadowClusters` | Local shadow cluster budget. |
| `MaxLocalShadowMeshletDraws` | Local shadow meshlet draw budget. |
| `DebugView` | Foliage debug view. |

Foliage debug views:

- `None`
- `Clusters`
- `LodBands`
- `DensityFade`
- `WindStrength`
- `HiZRejectedClusters`
- `ShadowCasting`
- `AlphaCutoff`

## Scene Submission

| Setting | Purpose |
| --- | --- |
| `GpuCompactionEnabled` | Enables GPU compaction of scene draw lists. |
| `IndirectMeshletDispatchEnabled` | Enables indirect meshlet dispatch. |
| `GpuLodSelectionEnabled` | Enables GPU LOD selection. |
| `GpuShadowCompactionEnabled` | Enables GPU shadow compaction. |
| `ValidationCompareCpuGpuLists` | Compares CPU/GPU lists for validation. |

## Hi-Z Visibility Policy

| Setting | Purpose |
| --- | --- |
| `WarmupFrameCount` | Frames to build Hi-Z before using it after invalidation. |
| `CameraCutDistance` | Distance threshold for detecting camera cuts. |
| `CameraCutForwardDotThreshold` | Direction threshold for detecting camera cuts. |
| `MinMeasuredOcclusionTests` | Minimum measured tests before adaptive decisions. |
| `MinUsefulOcclusionCullRate` | Minimum useful cull rate for adaptive Hi-Z. |
| `AdaptiveProbeIntervalFrames` | Interval for adaptive probe frames. |

## Async Compute

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables async compute. |
| `HiZBuildEnabled` | Allows Hi-Z build on async compute. |
| `AmbientOcclusionBlurEnabled` | Allows AO blur on async compute. |
| `FogEnabled` | Allows fog on async compute. |
| `BloomEnabled` | Allows bloom on async compute. |
| `GpuParticlesEnabled` | Allows GPU particles on async compute. |

## Diagnostics

| Setting | Purpose |
| --- | --- |
| `GpuMeshletCountersEnabled` | Enables GPU meshlet counters. |

## Debug Overlays

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables debug tooling. |
| `Mode` | Active debug overlay mode. |
| `ShowLabels` | Shows debug labels. |
| `ShowDepthTestedVolumes` | Shows depth-tested debug volumes. |
| `ShowXRayVolumes` | Shows x-ray debug volumes. |
| `SelectedObjectIndex` | Selected object index. |
| `SelectedLightIndex` | Selected light index. |
| `SelectedReflectionProbeIndex` | Selected reflection probe index. |
| `AllowGpuTiming` | Allows GPU timing collection. |
| `AllowScreenshots` | Allows screenshot requests. |
| `AllowRenderDocCapture` | Allows RenderDoc capture requests. |
| `CpuSnapshotsEnabled` | Enables CPU debug snapshots. |
| `MaxDebugLineSegments` | Debug line segment budget. |

Debug overlay modes:

- `None`
- `LightTiles`
- `DirectionalShadowCascades`
- `ReflectionProbeVolumes`
- `DecalVolumes`
- `ObjectBounds`
- `MeshletBounds`
- `SelectedObject`
- `MaterialInspection`
- `PassTimings`
- `GpuMemory`

## Performance Budgets

| Setting | Purpose |
| --- | --- |
| `Enabled` | Enables render budget tracking. |
| `ActiveProfile` | Active budget profile. |
| `Profile` | Resolved budget profile data. |

Budget profiles:

- `Development`
- `LowSpec1080p30`
- `MidSpec1080p60`
- `HighSpec1440p60`
- `Ultra4k60`
- `StressUnlimited`

Budget profile fields:

- `OutputWidth`
- `OutputHeight`
- `ResolutionScale`
- `TargetFrameMilliseconds`
- `CpuFrameBudgetMilliseconds`
- `GpuFrameBudgetMilliseconds`
- `GpuMemoryBudgetBytes`
- `UploadBudgetBytesPerFrame`
- `ObjectBudget`
- `MeshletBudget`
- `FoliageClusterBudget`
- `FoliageMeshletDrawBudget`
- `FoliageGrassBladeBudget`
- `FoliageMemoryBudgetBytes`
- `MaterialBudget`
- `TextureBudget`
- `LightBudget`
- `ShadowedLightBudget`
- `ReflectionProbeBudget`
- `TransparentObjectBudget`

## SampleInputController Runtime Controls

These are the renderer-related controls wired in `SampleInputController`.

| Control | Default key | Effect |
| --- | --- | --- |
| Toggle shadows | `F1` | Toggle directional shadows. |
| Cycle shadow debug | `F2` | Cycle shadow debug view. |
| Cycle shadow cascade count | `F3` | Cycle directional cascade count. |
| Cycle tone mapper | `F4` | Cycle tone mapper. |
| Toggle bloom | `F5` | Toggle bloom. |
| Cycle bloom debug | `F6` | Cycle bloom debug view. |
| Cycle bloom debug mip | `F7` | Cycle bloom debug mip. |
| Toggle Hi-Z | `F8` | Toggle Hi-Z occlusion. |
| Toggle transparent pass | `F9` | Toggle transparent pass/settings. |
| Toggle meshlet debug | `F10` | Toggle meshlet debug view. |
| Toggle raw HDR | `F11` | Toggle raw HDR scene color. |
| Toggle spot shadows | `F12` | Toggle spot shadows. |
| Toggle point shadows | `4` | Toggle point shadows. |
| Toggle AO | `5` | Toggle ambient occlusion. |
| Cycle AO debug | `6` | Cycle AO debug view. |
| Cycle AA mode | `7` | Cycle anti-aliasing mode. |
| Cycle AA debug | `8` | Cycle anti-aliasing debug view. |
| Cycle reflection debug | `9` | Cycle reflection debug view. |
| Toggle reflections | `0` | Toggle reflections. |
| Cycle reflection mode | `Y` | Cycle reflection mode. |
| Toggle reflection box projection | `R` | Toggle reflection box projection. |
| Toggle fog | `Z` | Toggle fog. |
| Cycle fog debug | `X` | Cycle fog debug view. |
| Fog density down/up | `C` / `V` | Adjust fog density. |
| Fog height density down/up | `B` / `N` | Adjust fog height density. |
| Fog start distance down/up | `G` / `H` | Adjust fog start distance. |
| Toggle fog inscattering | `T` | Toggle fog directional inscattering. |
| Bloom intensity down/up | `PageDown` / `PageUp` | Adjust bloom intensity. |
| Bloom threshold down/up | `End` / `Home` | Adjust bloom threshold. |
| Bloom radius down/up | `Delete` / `Insert` | Adjust bloom radius. |
| Exposure down/up | `[` / `]` | Adjust manual exposure. |
| AO radius down/up | `J` / `U` | Adjust AO radius. |
| AO intensity down/up | `M` / `I` | Adjust AO intensity. |
| Shadow normal bias down/up | `,` / `.` | Adjust directional shadow normal bias. |
| Spot shadow budget down/up | `-` / `=` | Adjust spot shadow budget. |
| Point shadow budget down/up | `;` / `'` | Adjust point shadow budget. |
| Spot shadow bias down/up | `K` / `L` | Adjust spot shadow bias. |
| Point shadow bias down/up | `O` / `P` | Adjust point shadow bias. |
| Toggle particles | `F` | Toggle particles. |
| Cycle particle debug | `Tab` | Cycle particle debug view. |
| Pause particles | `Space` | Pause/resume sample particle effects. |
| Restart particles fixed seed | `Backspace` | Restart sample particles with fixed seed. |
| Toggle soft particles | `\` | Toggle soft particles. |
| Toggle debug tooling | `CapsLock` | Toggle debug tooling. |
| Request screenshot | `PrintScreen` | Request screenshot if enabled. |
| Request RenderDoc capture | `ScrollLock` | Request RenderDoc capture if enabled. |
| Print selected object | `/` | Print selected object inspection. |

Control-modified chords are also used by the sample:

| Chord | Effect |
| --- | --- |
| `Ctrl+F1` | Cycle performance budget profile. |
| `Ctrl+F2` | Export performance snapshot. |
| `Ctrl+F3` | Cycle performance scenario. |
| `Ctrl+F4` | Toggle GPU timing. |
| `Ctrl+F5` | Cycle quality preset. |
| `Ctrl+F6` | Cycle feature isolation. |
| `Ctrl+F7` | Toggle secondary command buffers. |
| `Ctrl+F8` | Toggle foliage indirect meshlet dispatch. |
| `Ctrl+F9` | Toggle foliage far impostors. |
| `Ctrl+F10` | Cycle foliage debug view. |
| `Ctrl+F11` | Toggle scene GPU compaction. |
| `Ctrl+F12` | Toggle scene indirect meshlet dispatch. |
| `Ctrl+M` | Cycle material debug view. |
| `Ctrl+A` | Cycle animation debug view. |
| `Ctrl+3` | Cycle lighting mode. |
| `Ctrl+[` | Toggle auto exposure. |
| `Ctrl+5` | Toggle global illumination. |
| `Ctrl+6` | Cycle all GI debug views, including SSGI, DDGI, and ray-query views. |
| `Ctrl+D` | Cycle DDGI-only debug view and force DDGI-only mode. |
| `Ctrl+F` | Toggle DDGI-only diagnostics console filter. |
| `Ctrl+G` | Cycle focused DDGI debug views: final indirect, irradiance, coverage, update reasons. |
| `Ctrl+P` | Apply the DDGI High production profile. |
| `Ctrl+T` | Cycle DDGI quality tier and force DDGI-only mode. |
| `Ctrl+V` | Cycle DDGI investigation views: gather clipmap, fallback, support, data, confidence, irradiance, raw diffuse, probe position, update reasons. |
| `Ctrl+R` | Print DDGI diagnostics: effective mode, runtime snapshot, forward estimate counters, persistent DDGI warnings, SSGI allocation status, probe/update budgets, adaptive state, GPU scheduler counts/reasons/fallback/validation, memory, AS counts, and CPU/GPU timings. |
| `Ctrl+Y` | Cycle GI mode for comparison: disabled, SSGI, DDGI, hybrid, ray-query hybrid. |
| `Ctrl+Backspace` | Clear GI debug view. |
| `Ctrl+Keypad9` | Cycle debug overlay mode, including DDGI probe volume/activity/update overlays. |
| `Ctrl+Left` / `Ctrl+Right` | Select previous/next debug object. |
