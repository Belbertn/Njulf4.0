# Renderer Settings Reference

This is a readable index of renderer-facing settings exposed by `VulkanRenderer.Settings`,
plus the nearby renderer-level toggles and sample runtime controls.

## Renderer-Level Toggles

These live directly on `VulkanRenderer`, not inside `RenderSettings`.

| Setting | Purpose |
| --- | --- |
| `EnableHiZOcclusion` | Enables Hi-Z occlusion culling. |
| `EnableAdaptiveHiZOcclusion` | Allows Hi-Z occlusion to be adaptively suppressed/probed based on measured benefit. |
| `EnableDepthPrePass` | Obsolete compatibility property. Production Forward+ always runs the depth pre-pass; attempting to disable it fails immediately. |
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

`RenderSettings` defaults to `DdgiHigh`, the Simple-DDGI production profile. `Ultra` remains selectable as the highest Simple-DDGI quality tier.

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
| `TargetLuminance` | Display-linear gray value assigned to the metered luminance. The default is the reflected-light-meter value `0.125`. |
| `MinExposure` | Minimum computed exposure. |
| `MaxExposure` | Maximum computed exposure. |
| `LowPercentile` | Darkest histogram percentile excluded from metering. The default `70` keeps deep shade from pulling the whole frame toward gray. |
| `HighPercentile` | Brightest histogram percentile included in metering. The default `95` rejects small highlight outliers. |
| `DarkToLightAdaptationSpeed` | Adaptation speed when moving from a dark view into a bright one. |
| `LightToDarkAdaptationSpeed` | Adaptation speed when moving from a bright view into a dark one; slower by default to mimic eye adaptation. |
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
| `DirectionalCascadeBlendFraction` | Fraction of the smaller neighbouring cascade span used to overlap and smoothly hand off directional shadow cascades. Default `0.12`; clamped to `[0.02, 0.30]`. |
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

The reflection debug cycle includes `DdgiDirectionalRadianceLobe`, which shows
the directional SH radiance evaluated along the receiver reflection vector
before the split-sum BRDF, and `SourceOwnership`. Ownership encodes normalized
local-reflection, DDGI, and global-environment weights in red, green, and blue.
The channels sum to one wherever reflections are enabled.

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
| `Mode` | GI mode: `Disabled` or `Ddgi`. The active implementation is Simple DDGI. |
| `UseDdgi` | Enables Simple DDGI probe lighting. |
| `UseRayQueryBackend` | Enables ray-query Simple DDGI updates when the device supports them. |
| `DdgiQualityTier` | DDGI quality tier: `DdgiLow`, `DdgiMedium`, `DdgiHigh`, `DdgiUltra`. |
| `DdgiProbeClassificationEnabled` | Enables DDGI probe classification. |
| `DdgiProbeRelocationEnabled` | Enables DDGI probe relocation. |
| `DdgiCameraRelativeEnabled` | Enables camera-relative DDGI clipmaps. |
| `DdgiAdaptiveBudgetingEnabled` | Enables GPU-time-driven adaptive DDGI update budgets. |
| `DdgiAdaptiveBudgetHysteresisFraction` | Fractional timing headroom before adaptive DDGI reduces work. |
| `DdgiEmergencyDegradeGpuTimeMultiplier` | GPU-time multiplier that triggers emergency DDGI degradation. |
| `DdgiAsyncComputeEnabled` | Allows DDGI update work on async compute when renderer async compute is also enabled. |
| `DdgiMaxProbeUpdatesPerFrame` | Hard probe-update count cap. |
| `DdgiProbeUpdatePrimaryRayBudget` | Steady-frame primary ray budget for scheduled probe updates. |
| `DdgiMaxRaysPerProbe` | Upper bound for rays per updated probe. |
| `DdgiMaxShadedLights` | Compatibility-wide DDGI hit-light cap. Content-dependent Simple-DDGI uses the per-ring local-light sample settings below; directional lights remain exact. |
| `SimpleDdgiLocalLightSamplingMode` | `Auto` uses exact local-light evaluation through the threshold and the qualified GPU light tree above it. `Exact` forces all local lights. `LightTree` requests the production stochastic estimator. `LegacyTopKReference` is validation-authorized rollback only. |
| `SimpleDdgiNear/Mid/FarLocalLightSamplesPerHit` | Independent stochastic local-light samples per DDGI hit for each camera-relative ring; authored volumes use the near value. Values are clamped to the bounded production maximum of 64. A zero value causes the exact safety path. |
| `SimpleDdgiExactLocalLightThreshold` | Local-light count at or below which every local light is evaluated exactly. Directional lights do not consume this threshold or sample budget. |
| `SimpleDdgiLightTreeUniformMixtureProbability` | Exact uniform-over-tree-leaves proposal mixed with the point-dependent contribution-bound proposal so every represented nonzero light retains support. Default `0.02`; clamped to `[0.001, 0.25]`. |
| `SimpleDdgiLightTreeMaximumRefitAge` | Maximum consecutive refits before a deterministic inactive-bank rebuild is requested. Default `120`; clamped to `[1, 4096]`. |
| `SimpleDdgiDirectionalRadianceMode` | Probe incident-radiance sidecar: `Off`, validation-only `L1Reference`, or canonical RGB L2 SH. Each representation has a distinct ABI and cannot reinterpret live data from another mode. |
| `SimpleDdgiGlossyTransportMode` | `Off`, `ReceiverOnly`, bounded `OneBounce`, or validation-only `RecursiveExperimental`. One-bounce reads the previous compatible directional-radiance generation; recursive mode is never release-authorized by this plan. |
| `SimpleDdgiDirectionalRadianceMemoryBudgetBytes` | Independent hard admission budget for the canonical 64-byte-per-probe L2 sidecar and any mode-required parity allocation. Allocation failure leaves diffuse DDGI active and disables the DDGI rough-specular source. |
| `SimpleDdgiRoughSpecularMinimumRoughness`, `SimpleDdgiRoughSpecularFullWeightRoughness` | Lower edge and full-weight edge of the DDGI directional-radiance receiver band. DDGI ownership is exactly zero below the minimum and cross-fades to full weight over the nonzero band. Defaults are `0.55` and `0.70`. |
| `DdgiSkinnedGeometryMode` | Dynamic ray-scene representation: `Excluded`, `ConservativeProxy`, or frame-slot-owned `CurrentPose` geometry built from the GPU skinning output. |
| `DdgiTransparentGeometryMode` | `MaskOnly`, `MaskAndThin`, or stable `StochasticBlend`. Thick refraction, nested dielectric transport, caustics, and participating volumes remain unsupported and are explicitly diagnosed. |
| `DdgiFoliageGeometryMode` | `Excluded`, `AuthoredMeshOnly`, or `AuthoredAndProceduralProxy`. Procedural proxies are generated into AS-build-capable GPU buffers from stable patch/material/seed identity. |
| `DdgiDynamicBlasMemoryBudgetBytes`, `DdgiDynamicBlasScratchBudgetBytes` | Independent storage and scratch ceilings for current-pose and proxy BLAS work. Exhaustion selects the declared proxy/exclusion policy; it never reuses an arbitrary stale pose. |
| `DdgiDynamicBlasBuildsPerFrame`, `DdgiDynamicBlasPrimitivesPerFrame` | Hard per-frame dynamic-AS work caps. Topology-compatible entries refit; topology/format/count changes require a full build within these limits. |
| `DdgiFoliageProxyTriangleBudget` | Hard crossed-card/authored foliage proxy triangle budget. Stable probe-space priority admits near, then mid, then far work; lower-priority work is explicitly excluded when capacity is exhausted. |
| `DdgiFoliageProxyUpdateCadenceFrames` | Bounded wind/proxy regeneration cadence. Placement remains stable while deformation uses the raster wind clock/family; captures report wind age. |
| `DdgiTransparencyCandidateLimit`, `DdgiTransparencyLayerLimit` | Bounded ray-candidate and deterministic thin-transmittance layer limits. Overflow takes the documented conservative result and increments qualification counters. |
| `DdgiDecalCandidateLimit` | Maximum retained nearest geometry-decal overlays for a DDGI primary hit. Decals never become primary, shadow, or visibility blockers. |
| `DdgiMaterialTextureMaxCascade` | Highest camera-relative cascade that samples material textures in DDGI hit shading; `-1` disables cascade texture sampling while authored volumes still sample textures. |
| `DdgiSelfShadowBiasScale` | Artist-facing multiplier for authored DDGI normal/view self-shadow bias. Default `1.0`; higher values reduce acne/leaks at the cost of contact accuracy. |
| `DdgiThinWallPolicyEnabled`, `DdgiThinWallLeakClampStrength` | Enables and controls Simple-DDGI visibility-based leak attenuation. Simple-DDGI keeps one-sided source shading, records covered backfaces in receiver visibility, and uses close backface hits to relocate probes trapped behind architectural shells. |
| `DdgiHysteresisResponse` | Artist-facing response scale for DDGI probe lighting history. Default `1.0`; higher values converge lighting changes faster, lower values favor stability. |
| `SimpleDdgiSampledAtlasEnabled` | Enables the optional filtered image mirror of the canonical Simple-DDGI SSBO atlases. High and Ultra enable it by default; the runtime disables it safely if format, descriptor, layer, or remaining-memory admission fails. Low and Medium retain the canonical SSBO-only path. |
| `SimpleDdgiSampledAtlasCoverageMode` | `Disabled` uses canonical SSBOs only. `FullCanonical` mirrors every complete admitted physical volume. `ReceiverRelevant` mirrors complete authored receiver volumes and near/mid rings in priority order; excluded volumes and octahedral seams use the canonical typed SSBO path. A mirror never affects canonical volume admission. High and Ultra default to `ReceiverRelevant`; `FullCanonical` remains an explicit comparison/rollback override. |
| `SimpleDdgiStoragePackingMode` | Versioned Simple-DDGI storage contract: `Legacy` keeps the 36-byte FP32 source cache and 32-byte scratch rollback ABI; `Validate` keeps those bytes while tracing the fixed direction codebook and shadow-comparing stored/reconstructed directions; `Packed` uses mixed 28/24-byte FP16-radiance cache regions and 20-byte direction-free scratch. A change forces a cold allocation/source/atlas generation transition. Every quality preset defaults to `Packed`; `Legacy` and `Validate` remain explicit rollback/qualification overrides. |
| `SimpleDdgiReducedBlendEnabled` | Uses the SH irradiance projection on constrained tiers. Low and Medium enable it; High and Ultra use the full irradiance estimator. Visibility moments always use the exact directional ray estimator so reduced mode cannot create sparse zero-moment shadow bands. |
| `SimpleDdgiProbeResidencyMode` | Selects `Dense`, `Shadow`, or `SparseNearRing`. `Shadow` collects demand while retaining dense payload addressing. `SparseNearRing` pages only the camera-relative near ring and requires structured gather, Transport V2, toroidal scrolling, the GPU-resident scheduler, and an admitted dense coarser ring. High and Ultra select sparse; Low and Medium remain dense. |
| `SimpleDdgiSparsePhysicalPageBudget` | Immutable physical capacity of the 2×2×2 near-ring page pool. High defaults to `960`; Ultra defaults to `1440`. A demand spike never grows this allocation. |
| `SimpleDdgiSparseMinimumPhysicalPageBudget` | Lowest capacity that `Degrade` layout admission may select. High defaults to `768`; Ultra defaults to `1152`. Falling below it rejects sparse admission before Vulkan allocation. |
| `SimpleDdgiSparseRetentionFrames` | Retention hysteresis for recently relevant resident pages. High defaults to `120`; Ultra defaults to `150`; valid range is 1–3600 frames. |
| `SimpleDdgiSparseMaximumAdmissionsPerFrame` | Hard per-frame page admission limit. High defaults to `64`; Ultra defaults to `96`. Camera cuts waive old-page retention only under pressure and still obey this limit. |
| `SimpleDdgiSparseMaximumReceiverFeedbackRequests` | Bound on deduplicated supplemental receiver-page requests per epoch. High defaults to `2048`; Ultra defaults to `4096`. Opaque depth demand remains the primary producer. |
| `SimpleDdgiSparseInactiveRetryFrames` | Retry interval for pages suppressed after repeated all-inactive classification. Default `300`; geometry/topology invalidation reactivates affected pages immediately. |
| `SimpleDdgiTransportTailRelativeTolerance` | Relative error bound required by Transport V2 tail certification. Default `0.025`. |
| `SimpleDdgiTransportMaximumSolverGenerations` | Legacy-named minimum cached-source Jacobi generations required before convergence can retire a probe. Default `8`; a future maximum-work cap should use a separate setting. |
| `SimpleDdgiSecondVolumeOwnershipEarlyOutThreshold` | Ownership threshold for skipping a containing coarser-ring gather. Default `0.95`; lower values reduce gather work while increasing transition risk, and `1.0` keeps the conservative behavior. |
| `SimpleDdgiRingBaseSpacing`, `SimpleDdgiRingSpacingMultiplier` | Base near-ring spacing and per-ring spacing multiplier for camera-relative Simple-DDGI rings. |
| `SimpleDdgiNearRingGridSizeX/Y/Z`, `SimpleDdgiMidRingGridSizeX/Y/Z`, `SimpleDdgiFarRingGridSizeX/Y/Z` | Independent lattice dimensions for the near, mid, and far Simple-DDGI rings. The legacy `SimpleDdgiRingGridSizeX/Y/Z` setters broadcast a dimension to all three rings. |
| `SimpleDdgiAuthoredVolume.LatticePhase` | Spacing-relative XYZ phase for an authored lattice. It is wrapped to `[0, 1)` and shifts probe planes within unchanged volume bounds, helping avoid repeated wall/column alignments. |
| `SimpleDdgiNear/Mid/FarFullRaysPerProbe` | Full-refresh ray count for each Simple-DDGI ring; authored volumes use the near profile. |
| `SimpleDdgiNear/Mid/FarMaintenanceRaysPerProbe` | Stable maintenance ray count for each Simple-DDGI ring. |
| `SimpleDdgiNear/Mid/FarMinimumUpdateQuota`, `SimpleDdgiNear/Mid/FarMaximumUpdateQuota` | Per-ring update floors and preferred maxima applied before the weighted remainder allocator consumes the global Simple-DDGI update budget. |
| `FarFieldClipmapEnabled`, `FarFieldPagedEnabled` | Enables the bounded paged far-field representation used beyond detailed TLAS coverage. Quality-tier application enables both and supplies tier-specific page/cache budgets. The cache becomes authoritative only after at least one page is published and no requested page bake remains pending. |
| `GiAccelerationStructureMemoryBudgetBytes` | Hard detailed ray-query residency budget. High and Ultra require a complete scene-wide resolved resident set, trace it for the full probe ray, and publish no TLAS when it cannot fit. Low and Medium may trim only a coherent nearest-first static tail after the far-field representation is fully ready. |

`DdgiHigh` is the default production profile. It enables Simple DDGI with ray-query updates, camera-relative rings, AO, reflections, and optional async compute. Authored Simple-DDGI volumes add density to specific rooms or locations; they are not a second GI backend.

Authored Simple-DDGI volumes are optional standalone local overrides, not required scene coverage. A normal scene starts with an empty authored-volume list and uses the quality tier's camera-relative rings. Add a volume explicitly with only its world-space bounds and desired spacing when a specific room or location needs extra probe density; phase, purpose, and priority have safe defaults for the simple case.

Content-dependent modes have separate requested and effective state. Quality presets configure requested modes and budgets; `DdgiContentRolloutPolicy` independently admits reviewed features for the current device/profile. `ConfiguredContentDependentFeatures` therefore describes intent, while `ActiveContentDependentFeatures` and the effective mode properties describe what shaders and resource managers may use. Validation-only reference modes additionally require explicit conformance authorization. The `ContentDependentDdgi` runtime/performance snapshot records both sides plus the fallback reason, so a copied settings file cannot silently promote an unqualified feature.

### Advanced GI features

The editor presents B1, C1, C3, C4, and C5 as ordinary on/off switches. Turning
a switch on selects the feature's explicit production implementation; it does
not load a profile and does not require a qualification identifier. Internally,
the switches map to versioned modes so saved settings remain migratable and
automation can still select reference or `AutoQualified` modes. Every frame
records requested, supported, effective, and fallback state so a real hardware,
memory, ABI, allocation, or resource failure remains visible instead of being
misreported as active.

| Setting | Values and behavior |
| --- | --- |
| `SimpleDdgiReceiverFeedbackMode` | `Off`, `LegacyPackedReference`, `ExactCompacted`, or `AutoQualified`. `ExactCompacted` is the versioned B1 receiver-feedback ABI. It remains a one-frame-late scheduler priority signal and never controls probe liveness, residency, or visibility. |
| `DdgiOpacityMicromapMode` | `Off`, `ExtFourStateExperiment`, or `AutoQualified`. The production experiment is four-state only; unsupported, dynamic, or ambiguous alpha content uses the unchanged candidate-confirmation path. |
| `SimpleDdgiDirectionalGuidingMode` | `Off`, `CpuOracle`, `PerProbeHistogramExperiment`, or `AutoQualified`. Guiding retains a nonzero uniform proposal and an independent uniform-maintenance subset. |
| `GiCausticMode` | `Off`, `PhotonReference`, `WorldCacheExperiment`, or `AutoQualified`. It is a separate hero-specular/refractive path; caustic flux never becomes DDGI source/transport data. |
| `SimpleDdgiNearFieldResidualMode` | `Off`, `Reference`, `HiZHalfResolutionExperiment`, or `AutoQualified`. It requires measured post-B3 evidence and an exact direct-diffuse-plus-emissive source contract; it never samples final scene color. |

New settings turn on every completed advanced-GI production path in every
preset: B1 `ExactCompacted`, C1 `ExtFourStateExperiment`, C3
`PerProbeHistogramExperiment`, C4 `WorldCacheExperiment`, and C5
`HiZHalfResolutionExperiment`. Explicit modes need no promotion evidence and
enter their production graph/resource paths whenever their real prerequisites
are available. Existing saved settings retain their persisted mode. Unsupported
hardware, an exceeded memory or Vulkan limit, an ABI mismatch, allocation
failure, or incomplete runtime resources still falls back transactionally to
canonical DDGI without changing the switch. `AutoQualified` remains a separate
automation-only policy and is the only path gated by qualification artifacts.

Advanced-GI graph inventory is immutable for a renderer instance. The editor
therefore applies a switch by automatically performing a clean renderer/device
restart with the new selection; no separate Apply, profile, or evidence-loading
step is required. Applications that configure the renderer directly must set
startup modes through `RenderingOptions.InitialSettings.GlobalIllumination`
before resolving `VulkanRenderer`. The sample also exposes the startup contract
through these command-line options for automation:

| Command-line option | Environment variable |
| --- | --- |
| `--advanced-gi-prerequisite-manifest=<path>` | `NJULF_ADVANCED_GI_PREREQUISITE_MANIFEST` |
| `--advanced-gi-qualification-manifest=<path>` | `NJULF_ADVANCED_GI_QUALIFICATION_MANIFEST` |
| `--advanced-gi-runtime-evidence-bundle=<path>` | `NJULF_ADVANCED_GI_RUNTIME_EVIDENCE_BUNDLE` |
| `--advanced-gi-startup-profile=<path>` | `NJULF_ADVANCED_GI_STARTUP_PROFILE` |
| `--simple-ddgi-receiver-feedback-mode=<mode>` | `NJULF_RENDERER_SIMPLE_DDGI_RECEIVER_FEEDBACK_MODE` |
| `--ddgi-opacity-micromap-mode=<mode>` | `NJULF_RENDERER_DDGI_OPACITY_MICROMAP_MODE` |
| `--simple-ddgi-directional-guiding-mode=<mode>` | `NJULF_RENDERER_SIMPLE_DDGI_DIRECTIONAL_GUIDING_MODE` |
| `--gi-caustic-mode=<mode>` | `NJULF_RENDERER_GI_CAUSTIC_MODE` |
| `--simple-ddgi-near-field-residual-mode=<mode>` | `NJULF_RENDERER_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_MODE` |
| `--simple-ddgi-receiver-feedback-qualification-id=<id>` | `NJULF_SIMPLE_DDGI_RECEIVER_FEEDBACK_QUALIFICATION_ID` |
| `--ddgi-opacity-micromap-qualification-id=<id>` | `NJULF_DDGI_OPACITY_MICROMAP_QUALIFICATION_ID` |
| `--simple-ddgi-directional-guiding-qualification-id=<id>` | `NJULF_SIMPLE_DDGI_DIRECTIONAL_GUIDING_QUALIFICATION_ID` |
| `--gi-caustic-qualification-id=<id>` | `NJULF_GI_CAUSTIC_QUALIFICATION_ID` |
| `--simple-ddgi-near-field-residual-qualification-id=<id>` | `NJULF_SIMPLE_DDGI_NEAR_FIELD_RESIDUAL_QUALIFICATION_ID` |

When automation selects `AutoQualified`, startup is intentionally ordered: the
application first supplies requested modes, then the frozen prerequisite
manifest, the per-device qualification manifest, and finally the
scene/layout-bound C4/C5 runtime-evidence bundle.
`RenderingOptions.ConfigureAdvancedGiEvidence` runs last for applications that
already own strongly typed evidence. Each document is bounded, strict-schema,
and revalidated against the production planner. A missing, malformed, stale, or
mismatched document leaves canonical DDGI running and allocates no rejected
experiment resources. Unless the application supplies an explicit optional
device-feature override, a C1 request in
`RenderingOptions.InitialSettings.GlobalIllumination` also requests the
`VK_EXT_opacity_micromap` logical-device chain. Setting
`RenderingOptions.EnableExtOpacityMicromap` explicitly remains an application
override and can serve as a startup kill switch.

The local-light tree is an exact-capacity, double-banked GPU resource. Zero local lights allocate and dispatch nothing. Small sets use exact evaluation; larger qualified sets use a point-dependent bound proposal mixed with an exact uniform-support proposal. A finalized tree carries the complete 64-bit light, topology, and content revisions plus a checksum. Trace validates that state in O(1), and a fence-owned readback confirms publication before CPU diagnostics mark it valid. Invalid, stale, or partially published state falls back without exposing an incomplete hierarchy.

Directional projection is an ordered three-dispatch prepare/project/publish
stage after diffuse blend. It accumulates in FP32 but reuses 128 bytes from the
queue-local ray scratch after its diffuse consumers, then stores only the
budgeted FP16 sidecar; the setting does not allocate a persistent FP32
projection buffer. A checked record with zero usable samples is valid for
transaction completion but supplies no rough-reflection ownership.

Foliage proxy LOD is world/probe-space stable. Enabled authored probe volumes determine near, mid, far, and excluded tiers from their bounds, spacing, blend distance, and ray influence. With no authored volumes, patches conservatively remain near; camera position never participates. Mid and far procedural tiers reduce card density while increasing represented-instance weight so integrated density remains stable. Captures report requested/represented density, error, tier card counts, excluded patches, wind age, cadence generation, and the policy ABI version.

The `DdgiHigh` Simple-DDGI profile uses three asymmetric camera-relative rings: near `28x14x28` at `1.25 m`, mid `18x10x18` at `3.75 m`, and far `12x8x12` at `11.25 m`. This is 15,368 virtual probes total, reaching approximately `±16.9 m / ±8.1 m`, `±31.9 m / ±16.9 m`, and `±61.9 m / ±39.4 m` horizontally/vertically before authored volumes. The near ring has 1,372 fixed 2×2×2 virtual pages. Its 960-page sparse pool reserves 7,680 near payload probes alongside 4,392 dense mid/far probes; authored volumes remain dense. The exact resident-scheduler fixture is 160,821,296 live bytes versus 201,263,392 bytes for the same-binary Dense plan, a 40,442,096-byte saving, with a 139,024-byte residency arena. Its global update budget remains 2,048 probes per frame, with near/mid/far preferred quotas of 1,024/324/128 and 128/64/32 full-refresh rays per probe.

The page geometry is an internal ABI constant. Sparse allocation is capacity-based, not occupancy-based: diagnostics separately report virtual pages, resident pages, physical capacity, permanently invalid edge-page padding, sampled-atlas 256-layer rounding, dense-equivalent bytes, allocated bytes, and avoided bytes. A full pool leaves excess fine demand nonresident and uses a dense coarser ring; it never reallocates opportunistically.

Runtime overrides are available for controlled capture and qualification:

- `--simple-ddgi-residency-mode=dense|shadow|sparse-near-ring`
- `--simple-ddgi-sparse-page-budget=<pages>`
- `--simple-ddgi-sparse-min-page-budget=<pages>`
- `--simple-ddgi-sparse-retention-frames=<frames>`
- `--simple-ddgi-sparse-max-admissions=<pages>`
- `--simple-ddgi-sparse-max-feedback=<requests>`
- `--simple-ddgi-sparse-inactive-retry-frames=<frames>`

The corresponding environment variables are `NJULF_RENDERER_SIMPLE_DDGI_RESIDENCY_MODE`, `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_PAGE_BUDGET`, `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MIN_PAGE_BUDGET`, `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_RETENTION_FRAMES`, `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_ADMISSIONS`, `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_MAX_FEEDBACK`, and `NJULF_RENDERER_SIMPLE_DDGI_SPARSE_INACTIVE_RETRY_FRAMES`.

Simple-DDGI debug views cover irradiance, coverage, update reasons, ray budget, probe state, and ring transitions.

GI debug screenshots are self-identifying: Simple-DDGI views render a category-colored border, a view-id badge, and a legend strip. The sample prints a matching debug-legend line and includes the selected view in the screenshot filename.

Performance snapshots report `SimpleDdgiPageDemandPass`, `SimpleDdgiPageResidencyPass`, and `SimpleDdgiPageFeedbackPass` separately from the Simple-DDGI scheduler, trace, transport, blend, publish, upload, atlas, and far-field timings. The production gate applies the 0.20 ms P95 demand-plus-reconciliation gate and verifies the remaining Simple-DDGI stages, memory budgets, ray-query readiness, and visible-probe latency.

The production gate also includes the Phase 9 weak-bounce checks: healthy raw atlas/sample/blend energy must survive into final DDGI diffuse, high environment fallback weight must not mask weak DDGI contribution, emissive validation scenes must show emissive bounce energy, and thin-wall/leak scenes must keep leak attenuation active.

Simple-DDGI debug shortcut cycle order includes `DdgiProbeResidency`, `DdgiResidencyFallback`, `DdgiPageAge`, and `DdgiPhysicalPage` after `DdgiSourceCacheRadiance`, followed by the existing sampled, confidence, visibility, gather, far-field, and material-provenance views. The residency views preserve the logical virtual grid: nonresident probes remain visible as neutral/hollow markers, while the physical-page view shows page identity/generation only for validation.

`DdgiDataConfidence` displays accepted probe-data availability. `DdgiDirectionalSupport` displays the separate geometric fraction with useful normal-facing support. `DdgiConfidenceChain` encodes availability, directional authority, and transport visibility in RGB. `DdgiIrradiance` uses a normalized logarithmic presentation so low nonzero energy remains distinguishable from exact zero; `DdgiSampledIrradiance` retains the raw linear diagnostic. `DdgiSourceCacheRadiance` uses the same logarithmic presentation for direct, emissive, and sky source-cache energy before recursive bounce.

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
| `ReceiveGlobalIllumination` | Transparent surfaces sample Simple DDGI when GI is active. |
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
| `ReceiveGlobalIllumination` | Geometry decals sample DDGI independently of ordinary transparent materials. |
| `DebugView` | Decal debug view. |
| `GeometryDepthBias` | Geometry decal depth bias. |
| `GeometrySlopeScaledDepthBias` | Geometry decal slope-scaled depth bias. |
| `MaxProjectedDecals` | Maximum projected decals. |
| `MaxProjectedDecalsPerTile` | Maximum projected decals per tile. |
| `MaxProjectedDecalsPerPixel` | Maximum projected decals per pixel. |

Geometry decals are authored explicitly in glTF material `extras`; names are
not interpreted:

```json
"extras": {
  "NJULF_geometry_decal": true,
  "NJULF_decal_layer": 0,
  "NJULF_decal_depth_bias": 0.0005
}
```

`NJULF_decal_layer` accepts 0–255 and `NJULF_decal_depth_bias` accepts
0–0.01. Invalid metadata fails import with a material-specific error.

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
| `GpuLod1DistanceRatio` | Distance-to-bounding-radius threshold for LOD0 → LOD1. Defaults to `4.0`; clamped to `[1, 64]`. |
| `GpuLod2DistanceRatio` | Distance-to-bounding-radius threshold for LOD1 → LOD2. Defaults to `10.0`; clamped to `[GpuLod1DistanceRatio, 128]`. |
| `GpuShadowCompactionEnabled` | Enables GPU shadow compaction. |
| `GpuShadowLodBias` | Additional requested LOD levels for GPU-compacted directional-shadow draws. The current per-meshlet stream preserves LOD0 whenever a lower-LOD mapping cannot be proven coverage-safe; those conservative fallbacks are reported in directional-shadow diagnostics. Defaults to `1`; clamped to `[0, 2]`. |
| `ValidationCompareCpuGpuLists` | Compares CPU/GPU lists for validation. |

Meshes without authored LOD1/LOD2 meshlet ranges safely remain on their available base range; changing these thresholds never fabricates simplified geometry. Set the ratios to `12.0` and `32.0` to reproduce the prior threshold behavior.

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
| `Ctrl+Keypad1` | Toggle the editor overlay in debug builds. |
| `Ctrl+K` | Cycle material debug view. |
| `Ctrl+A` | Cycle animation debug view. |
| `Ctrl+3` | Cycle lighting mode. |
| `Ctrl+[` | Toggle auto exposure. |
| `Ctrl+5` | Toggle global illumination. |
| `Ctrl+6` | Cycle the available Simple-DDGI debug views. |
| `Ctrl+D` | Enable Simple DDGI and cycle its debug view. |
| `Ctrl+F` | Toggle Simple-DDGI diagnostics console filter. |
| `Ctrl+G` | Cycle focused Simple-DDGI debug views: final indirect, irradiance, coverage, update reasons. |
| `Ctrl+P` | Restore the current scene/scenario's normal render view and clear visualization overrides. |
| `Ctrl+T` | Cycle Simple-DDGI quality tier and enable Simple DDGI. |
| `Ctrl+L` | Toggle Simple-DDGI compact L1 probe metadata. |
| `Ctrl+V` | Cycle Simple-DDGI investigation views: gather clipmap, shader-read blend weight, fallback, support, data, confidence, irradiance, raw diffuse, probe position, update reasons. |
| `Ctrl+R` | Print Simple-DDGI diagnostics: runtime state, probe/update budgets, scheduler policy, memory, acceleration structures, and CPU/GPU timings. |
| `Ctrl+Y` | Cycle GI mode: disabled or Simple DDGI. |
| `Ctrl+Backspace` | Clear GI debug view. |
| `Ctrl+Keypad0` | Store diagnostic output JSON and a window screenshot with matching base filenames in `DiagnosticSnapshots`, and enable CPU snapshots for object/material inspection. |
| `Ctrl+Keypad9` | Cycle debug overlay mode, including DDGI probe volume/activity/update overlays. |
| `Ctrl+Left` / `Ctrl+Right` | Select previous/next debug object. |
