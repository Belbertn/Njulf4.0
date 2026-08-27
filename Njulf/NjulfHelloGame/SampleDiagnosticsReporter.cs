using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Njulf.Core.Camera;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal enum SampleDiagnosticsFilter
{
    FullFrame,
    DdgiOnly
}

internal sealed class SampleDiagnosticsReporter
{
    private readonly MaterialManager _materialManager;
    private readonly IModelRenderUploadService? _uploadService;
    private SampleDiagnosticsFilter _filter = SampleDiagnosticsFilter.FullFrame;
    private bool _printedFrameDiagnostics;
    private int _diagnosticFrameCounter;
    private readonly PerformanceSampleWindow _movingFrameMs = new(180);
    private readonly PerformanceSampleWindow _stillFrameMs = new(180);
    private readonly PerformanceSampleWindow _movingCpuDrawMs = new(180);
    private readonly PerformanceSampleWindow _stillCpuDrawMs = new(180);
    private long _lastFrameTimestamp;
    private bool _hasLastCameraPose;
    private Njulf.Core.Math.Vector3 _lastCameraPosition;
    private float _lastCameraYaw;
    private float _lastCameraPitch;
    private int _pacingFrameCounter;
    private int _movingFrames;
    private int _stillFrames;
    private int _movingPayloadRebuilds;
    private int _stillPayloadRebuilds;
    private ulong _movingUploadedBytes;
    private ulong _stillUploadedBytes;
    private bool _hasLastDebugOverlayStatus;
    private DebugOverlayMode _lastDebugOverlayMode;
    private DebugOverlayAvailability _lastDebugOverlayAvailability;
    private string _lastDebugOverlayReason = string.Empty;
    private int _lastDebugDdgiGpuCountersValid;

    public SampleDiagnosticsReporter(
        MaterialManager materialManager,
        IModelRenderUploadService? uploadService)
    {
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _uploadService = uploadService;
    }

    public SampleDiagnosticsFilter Filter => _filter;

    public SampleDiagnosticsFilter ToggleDdgiFilter()
    {
        _filter = _filter == SampleDiagnosticsFilter.DdgiOnly
            ? SampleDiagnosticsFilter.FullFrame
            : SampleDiagnosticsFilter.DdgiOnly;

        Console.WriteLine($"Diagnostics filter: {_filter}");
        return _filter;
    }

    public void SetFilter(SampleDiagnosticsFilter filter)
    {
        _filter = filter;
        Console.WriteLine($"Diagnostics filter: {_filter}");
    }

    public void PrintModelSummary(Model model, SampleAssetManifest manifest)
    {
        if (model == null)
            throw new ArgumentNullException(nameof(model));
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));

        var materialHandles = new HashSet<MaterialHandle>();
        var dynamicTextureIndices = new HashSet<int>();

        foreach (RenderObject renderObject in model.RenderObjects)
        {
            if (renderObject.Material is not MaterialHandle materialHandle || !materialHandle.IsValid)
                continue;

            materialHandles.Add(materialHandle);
        }

        foreach (MaterialHandle materialHandle in materialHandles)
        {
            GPUMaterialData material = _materialManager.GetMaterialData(materialHandle);
            AddDynamicTextureIndex(dynamicTextureIndices, material.AlbedoTextureIndex);
            AddDynamicTextureIndex(dynamicTextureIndices, material.NormalTextureIndex);
            AddDynamicTextureIndex(dynamicTextureIndices, material.MetallicRoughnessTextureIndex);
            AddDynamicTextureIndex(dynamicTextureIndices, material.EmissiveTextureIndex);
        }

        ModelRenderUploadDiagnostics? uploadDiagnostics = _uploadService?.LastUploadDiagnostics;
        string diagnostics = uploadDiagnostics == null
            ? string.Empty
            : $", uploadedMaterials={uploadDiagnostics.LoadedMaterialCount}, " +
              $"uploadedTextures={uploadDiagnostics.LoadedTextureCount}, " +
              $"defaultWhite={uploadDiagnostics.DefaultWhiteSubstitutions}, " +
              $"defaultNormal={uploadDiagnostics.DefaultNormalSubstitutions}, " +
              $"defaultBlack={uploadDiagnostics.DefaultBlackSubstitutions}, " +
              $"blendMaterials={uploadDiagnostics.BlendMaterialCount}";

        Console.WriteLine(
            $"Loaded '{manifest.ModelPath}': objects={model.RenderObjects.Count}, " +
            $"materials={materialHandles.Count}, importedDynamicTextures={dynamicTextureIndices.Count}{diagnostics}.");
    }

    public void PrintProceduralSceneSummary(Scene scene, string sceneName)
    {
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));
        if (string.IsNullOrWhiteSpace(sceneName))
            throw new ArgumentException("Scene name cannot be empty.", nameof(sceneName));

        var materialHandles = new HashSet<MaterialHandle>();
        foreach (RenderObject renderObject in scene.RenderObjects)
        {
            if (renderObject.Material is MaterialHandle materialHandle && materialHandle.IsValid)
                materialHandles.Add(materialHandle);
        }

        Console.WriteLine(
            $"Loaded procedural scene '{sceneName}': objects={scene.RenderObjects.Count}, " +
            $"materials={materialHandles.Count}, probes={scene.ReflectionProbes.Count}, particles={scene.ParticleEffects.Count}.");
    }

    public void PrintFirstFrameDiagnostics(IRenderer renderer)
    {
        if (renderer is not VulkanRenderer vulkanRenderer)
            return;

        RendererDiagnostics diagnostics = vulkanRenderer.LastDiagnostics;
        PrintDebugOverlayTransition(diagnostics);
        if (_filter == SampleDiagnosticsFilter.DdgiOnly)
        {
            _diagnosticFrameCounter++;

            if (_diagnosticFrameCounter % 30 != 0)
                return;

            PrintDdgiTriageDiagnostics(diagnostics);
            PrintGiDiagnostics(diagnostics);
            PrintDdgiRingDiagnostics(diagnostics);
            PrintDdgiVolumeActivityDiagnostics(diagnostics);
            PrintDdgiUpdateDiagnostics(diagnostics);
            return;
        }

        if (diagnostics.VisibleObjectCount == 0 && diagnostics.VisibleMeshletCount == 0)
            return;

        _diagnosticFrameCounter++;
        if (_printedFrameDiagnostics && _diagnosticFrameCounter % 180 != 0)
            return;

        _printedFrameDiagnostics = true;
        Console.WriteLine(
            $"Frame diagnostics scene: visibleObjects={diagnostics.VisibleObjectCount}, visibleMeshlets={diagnostics.VisibleMeshletCount}, " +
            $"opaqueObjects={diagnostics.OpaqueObjectCount}, maskedObjects={diagnostics.MaskedObjectCount}, transparentObjects={diagnostics.TransparentObjectCount}, " +
            $"opaqueMeshlets={diagnostics.OpaqueMeshletCount}, transparentMeshlets={diagnostics.TransparentMeshletCount}, blendMaterials={diagnostics.BlendMaterialCount}, " +
            $"lights={diagnostics.LightCount}, tiles={diagnostics.TileCountX}x{diagnostics.TileCountY}, " +
            $"tileLightsAvgMax={diagnostics.AverageLightsPerNonEmptyTile:F1}/{diagnostics.MaxLightsInAnyTile}, " +
            $"tileSaturated={diagnostics.LightTileSaturationCount}, lightCullRejected={diagnostics.LightCullRejectedPointCount}/{diagnostics.LightCullRejectedSpotCount}, " +
            $"tileClearBytes={diagnostics.TiledLightHeaderBufferClearBytes}/{diagnostics.TiledLightIndexBufferClearBytes}, " +
            $"materials={diagnostics.MaterialCount}, textures={diagnostics.TextureCount}.");
        Console.WriteLine(
            $"Frame diagnostics transparency/decals: mode={diagnostics.TransparencyMode}, debug={diagnostics.TransparencyDebugView}, " +
            $"receiveShadows={diagnostics.TransparentReceiveShadows}, receiveDdgi={diagnostics.TransparentReceiveGlobalIllumination}, solidObjects={diagnostics.SolidObjectCount}, maskedObjects={diagnostics.MaskedObjectCount}, " +
            $"transparentObjects={diagnostics.TransparentObjectCount}, decalObjects={diagnostics.GeometryDecalObjectCount}, solidMeshlets={diagnostics.SolidMeshletCount}, " +
            $"maskedMeshlets={diagnostics.MaskedMeshletCount}, transparentMeshlets={diagnostics.TransparentMeshletCount}, decalMeshlets={diagnostics.GeometryDecalMeshletCount}, " +
            $"maskMaterials={diagnostics.MaskMaterialCount}, blendMaterials={diagnostics.BlendMaterialCount}, decalMaterials={diagnostics.GeometryDecalMaterialCount}, " +
            $"sortCandidates={diagnostics.TransparentSortCandidateCount}, sortUs={diagnostics.TransparentSortMicroseconds}, overflow={diagnostics.TransparentOverflowCount}, " +
            $"weightedOit={diagnostics.WeightedOitEnabled}, oitMiB={diagnostics.WeightedOitRenderTargetBytes / (1024.0 * 1024.0):F1}, " +
            $"decalDebug={diagnostics.DecalDebugView}, decalsEnabled={diagnostics.GeometryDecalsEnabled}, receiveDdgi={diagnostics.DecalReceiveGlobalIllumination}, receiveShadows={diagnostics.DecalReceiveShadows}, decalBias={diagnostics.GeometryDecalDepthBias:F5}, " +
            $"decalSlopeBias={diagnostics.GeometryDecalSlopeScaledDepthBias:F2}, " +
            $"decalEstimated invocation/backfaceKill/coverageKill/surviving/ddgiGather/shadowEval={diagnostics.DecalFragmentAttribution.EstimatedInvocationCount}/{diagnostics.DecalFragmentAttribution.EstimatedBackFaceKilledCount}/{diagnostics.DecalFragmentAttribution.EstimatedCoverageKilledCount}/{diagnostics.DecalFragmentAttribution.EstimatedSurvivingCount}/{diagnostics.DecalFragmentAttribution.EstimatedDdgiGatherCount}/{diagnostics.DecalFragmentAttribution.EstimatedShadowEvaluationCount}, " +
            $"decalEstimateStrideWeight={DecalFragmentAttributionCounters.SampleStride}/{DecalFragmentAttributionCounters.SampleWeight}.");
        Console.WriteLine(
            $"Frame diagnostics animation: enabled={diagnostics.AnimationEnabled}, skinning={diagnostics.AnimationSkinningMode}, debug={diagnostics.AnimationDebugView}, " +
            $"skinnedObjects={diagnostics.SkinnedObjectCount}, skeletons={diagnostics.SkeletonCount}, skins={diagnostics.SkinCount}, clips={diagnostics.AnimationClipCount}, " +
            $"activeAnimators={diagnostics.ActiveAnimatorCount}, playing={diagnostics.PlayingAnimatorCount}, paused={diagnostics.PausedAnimatorCount}, " +
            $"jointMatrices={diagnostics.JointMatrixCount}, dispatches={diagnostics.SkinningDispatchCount}, bounds={diagnostics.AnimatedBoundsMode}.");
        Console.WriteLine(
            $"Frame diagnostics particles: enabled={diagnostics.ParticlesEnabled}, mode={diagnostics.ParticleSimulationMode}, debug={diagnostics.ParticleDebugView}, " +
            $"effects={diagnostics.ParticleEffectCount}, emitters={diagnostics.ParticleEmitterCount}, live={diagnostics.LiveParticleCount}, " +
            $"simulated={diagnostics.SimulatedParticleCount}, culled={diagnostics.CulledParticleCount}, rendered={diagnostics.RenderedParticleCount}, " +
            $"batches={diagnostics.ParticleBatchCount}, alpha={diagnostics.AlphaParticleCount}, additive={diagnostics.AdditiveParticleCount}, " +
            $"soft={diagnostics.SoftParticleCount}, flipbook={diagnostics.FlipbookParticleCount}, trails={diagnostics.TrailCount}, beams={diagnostics.BeamCount}, " +
            $"uploadMiB={diagnostics.ParticleInstanceUploadBytes / (1024.0 * 1024.0):F2}, simUs={diagnostics.CpuParticleSimulationMicroseconds}, " +
            $"buildUs={diagnostics.CpuParticleBuildMicroseconds}, budgetExceeded={diagnostics.ParticleBudgetExceeded}, uploadBudgetExceeded={diagnostics.ParticleUploadBudgetExceeded}.");
        Console.WriteLine(
            $"Frame diagnostics debug: enabled={diagnostics.DebugToolingEnabled}, overlay={diagnostics.DebugOverlayMode}, " +
            $"cpuSnapshots={diagnostics.CpuDebugSnapshotsEnabled}, selected={diagnostics.DebugSelectedObjectIndex}:'{diagnostics.DebugSelectedObjectName}', " +
            $"lines={diagnostics.DebugDrawLineCount}, persistentLines={diagnostics.DebugDrawPersistentLineCount}, droppedLines={diagnostics.DebugDrawDroppedLineCount}, " +
            $"screenshotsPending={diagnostics.ScreenshotPendingCount}, renderDocAvailable={diagnostics.RenderDocAvailable}, renderDocRequested={diagnostics.RenderDocCaptureRequested}.");
        if (vulkanRenderer.TryInspectObject(diagnostics.DebugSelectedObjectIndex, out SelectedObjectInspection inspection))
        {
            MaterialInspectionResult material = inspection.MaterialInfo;
            Console.WriteLine(
                $"Frame diagnostics selected material: object='{inspection.ObjectName}', material={material.MaterialIndex}, mode={material.RenderMode}, " +
                $"metallic={material.Metallic:F2}, roughness={material.Roughness:F2}, ao={material.AmbientOcclusion:F2}, normal={material.NormalStrength:F2}, " +
                $"textures={material.AlbedoTextureIndex}/{material.NormalTextureIndex}/{material.MetallicRoughnessTextureIndex}/{material.EmissiveTextureIndex}.");
        }
        Console.WriteLine(
            $"Frame diagnostics CPU: totalDrawUs={diagnostics.CpuTotalDrawSceneMicroseconds}, sceneBuildUs={diagnostics.CpuSceneBuildMicroseconds}, " +
            $"signatureUs={diagnostics.CpuPayloadSignatureMicroseconds}, objectCullUs={diagnostics.CpuObjectCullMicroseconds}, " +
            $"meshletCullUs={diagnostics.CpuMeshletCullMicroseconds}, materialUploadUs={diagnostics.CpuMaterialUploadMicroseconds}, " +
            $"uploadUs={diagnostics.CpuUploadMicroseconds}, payloadRebuilt={diagnostics.ScenePayloadRebuilt}.");
        string gpuMemoryBudget = FormatGpuMemoryBudget(diagnostics);
        Console.WriteLine(
            $"Frame diagnostics budget: profile='{diagnostics.ActiveBudgetProfileName}', overall={diagnostics.BudgetOverallStatus}, " +
            $"cpu={diagnostics.CpuFrameBudgetStatus}, gpu={diagnostics.GpuFrameBudgetStatus}, {gpuMemoryBudget}, " +
            $"upload={diagnostics.UploadBudgetStatus}, " +
            $"uploadMiB={diagnostics.UploadedBytes / (1024.0 * 1024.0):F2}/{diagnostics.UploadBudgetBytesPerFrame / (1024.0 * 1024.0):F2}, " +
            $"stagingMiB={diagnostics.StagingBytesUsedThisFrame / (1024.0 * 1024.0):F2}, peakStagingMiB={diagnostics.StagingBytesPeakThisSession / (1024.0 * 1024.0):F2}, " +
            $"stagingOverflow={diagnostics.StagingOverflowCountThisFrame}/{diagnostics.StagingOverflowCount}, retainedOverflow={diagnostics.StagingRetainedOverflowBufferCount}:{diagnostics.StagingRetainedOverflowBytes / (1024.0 * 1024.0):F2}MiB, " +
            $"worstStall={diagnostics.RuntimeWorstStallReason}:{diagnostics.RuntimeWorstStallMicroseconds}us.");
        Console.WriteLine(
            $"Frame diagnostics memory: meshMiB={diagnostics.MeshBufferAllocatedBytes / (1024.0 * 1024.0):F1} used={diagnostics.MeshBufferUsedBytes / (1024.0 * 1024.0):F1}, " +
            $"meshGrowthRetry={diagnostics.MeshBufferGrowthRetrySuccessCount}/{diagnostics.MeshBufferGrowthRetryCount}, meshCompactionOomSkip={diagnostics.MeshBufferCompactionOutOfDeviceMemorySkipCount}, " +
            $"sceneMiB={diagnostics.SceneBufferAllocatedBytes / (1024.0 * 1024.0):F1}, materialMiB={diagnostics.MaterialBufferAllocatedBytes / (1024.0 * 1024.0):F1}, " +
            $"lightMiB={(diagnostics.LightBufferAllocatedBytes + diagnostics.TiledLightBufferAllocatedBytes) / (1024.0 * 1024.0):F1}, texturesMiB={diagnostics.TextureAssetBytes / (1024.0 * 1024.0):F1}, " +
            $"rtMiB={diagnostics.RenderTargetBytes / (1024.0 * 1024.0):F1}, rtScale={diagnostics.RequestedDynamicResolutionScale:F2}/{diagnostics.CommittedRenderTargetScale:F2}, " +
            $"rtResizes={diagnostics.RenderTargetResizeCount}, rtReason='{diagnostics.LastRenderTargetRecreateReason}', shadowMiB={diagnostics.ShadowMapBytes / (1024.0 * 1024.0):F1}, " +
            $"oitMiB={diagnostics.WeightedOitRenderTargetBytes / (1024.0 * 1024.0):F1}, " +
            $"envMiB={diagnostics.EnvironmentTextureBytes / (1024.0 * 1024.0):F1}, reflectionMiB={diagnostics.ReflectionProbeBytes / (1024.0 * 1024.0):F1}, " +
            $"swapchainMiB={diagnostics.SwapchainEstimatedBytes / (1024.0 * 1024.0):F1}, unknownMiB={diagnostics.UnknownGpuMemoryBytes / (1024.0 * 1024.0):F1}.");
        Console.WriteLine(
            $"Frame diagnostics static batches: batches={diagnostics.StaticInstanceBatchCount}, instances={diagnostics.StaticInstanceCount}, " +
            $"visible={diagnostics.VisibleStaticInstanceCount}, culled={diagnostics.CulledStaticInstanceCount}, " +
            $"meshletDraws={diagnostics.StaticBatchMeshletDrawCommandCount}, buildUs={diagnostics.CpuStaticBatchBuildMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics foliage: patches={diagnostics.FoliagePatchCount}, prototypes={diagnostics.FoliagePrototypeCount}, " +
            $"clusters={diagnostics.FoliageClusterCount}, visibleClusters={diagnostics.FoliageVisibleClusterCount}, " +
            $"meshletDraws={diagnostics.FoliageVisibleMeshletDrawCount}, overflow={diagnostics.FoliageOverflowCount}, " +
            $"drawOverflow={diagnostics.FoliageMeshletDrawOverflowCount}, indirect={(diagnostics.FoliageIndirectMeshletDispatchEnabled ? "on" : "off")}, " +
            $"farImpostors={diagnostics.FoliageFarImpostorVisibleCount}, impostorAtlasBytes={diagnostics.FoliageImpostorAtlasBytes}, " +
            $"cpuBuildUs={diagnostics.CpuFoliageBuildMicroseconds}, cpuUploadUs={diagnostics.CpuFoliageUploadMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics GPU: depthUs={diagnostics.GpuDepthPrePassMicroseconds}, hizUs={diagnostics.GpuHiZBuildMicroseconds}, " +
            $"lightCullUs={diagnostics.GpuLightCullMicroseconds}, forwardUs={diagnostics.GpuForwardOpaqueMicroseconds}, transparentUs={diagnostics.GpuTransparentMicroseconds}, " +
            $"frameUs={diagnostics.GpuFrameMicroseconds}, timing={diagnostics.GpuTimingSupported}/{diagnostics.GpuTimingEnabled}/{diagnostics.GpuTimingPending}/{diagnostics.GpuTimingValid}, " +
            $"timingReason='{diagnostics.GpuTimingUnavailableReason}', " +
            $"depthPrePass={diagnostics.DepthPrePassEnabled}, hiz={diagnostics.HiZEnabled}, occlusion={diagnostics.OcclusionEnabled}, hizSize={diagnostics.HiZWidth}x{diagnostics.HiZHeight}, hizMips={diagnostics.HiZMipCount}, " +
            $"hizConsumers={diagnostics.HiZConsumerCount}:{diagnostics.HiZConsumerSummary}, hizSkippedNoConsumer={diagnostics.HiZBuildSkippedBecauseNoConsumer}, " +
            $"hizCounterSource={diagnostics.HiZCounterSource}, forwardHiZ={diagnostics.ForwardHiZTestedCount}/{diagnostics.ForwardHiZCulledCount}/{diagnostics.ForwardHiZCullRate:F3}, previousHiZValid={diagnostics.PreviousHiZFrameValid}, " +
            $"hizFallback={diagnostics.HiZFallbackPath}, hizFallbackReason='{diagnostics.HiZFallbackReason}', hizValidateLegacy={diagnostics.HiZValidateAgainstLegacyPath}, " +
            $"previousHiZSkip={diagnostics.PreviousHiZSkippedInvalidHistory}/{diagnostics.PreviousHiZSkippedCameraMotion}, previousHiZ={diagnostics.PreviousHiZTested}/{diagnostics.PreviousHiZCulled}, " +
            $"hizPolicy={diagnostics.HiZPolicyStatus}, hizWarmup={diagnostics.HiZPolicyWarmupFramesRemaining}, hizReason='{diagnostics.HiZPolicyReason}', " +
            $"hizAdaptiveStatus={diagnostics.HiZPolicyAdaptiveStatus}, hizAdaptiveSuppressed={diagnostics.HiZPolicyAdaptiveSuppressed}, " +
            $"hizAdaptiveProbe={diagnostics.HiZPolicyAdaptiveProbe}, hizSuppressedFrames={diagnostics.HiZPolicyAdaptiveSuppressedFrameCount}, " +
            $"hizCullRate={diagnostics.HiZPolicyAdaptiveCullRate:F3}, hizEstimatedUs={diagnostics.HiZPolicyAdaptiveEstimatedSavedMicroseconds}/" +
            $"{diagnostics.HiZPolicyAdaptiveEstimatedCostMicroseconds}/{diagnostics.HiZPolicyAdaptiveEstimatedNetMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics CPU passes: depthRecordUs={diagnostics.CpuDepthPrePassRecordMicroseconds}, hizRecordUs={diagnostics.CpuHiZBuildRecordMicroseconds}, " +
            $"hizBreakdownUs=depthTransition:{diagnostics.CpuHiZDepthTransitionMicroseconds},pyramidTransition:{diagnostics.CpuHiZPyramidTransitionMicroseconds}," +
            $"descriptorBinds:{diagnostics.CpuHiZDescriptorBindMicroseconds},dispatches:{diagnostics.CpuHiZPushDispatchMicroseconds}," +
            $"mipDependenciesAndFinalLayout:{diagnostics.CpuHiZFinalBarrierMicroseconds}, " +
            $"shadowRecordUs={diagnostics.CpuDirectionalShadowRecordMicroseconds}, lightCullRecordUs={diagnostics.CpuLightCullRecordMicroseconds}, forwardRecordUs={diagnostics.CpuForwardOpaqueRecordMicroseconds}, " +
            $"transparentRecordUs={diagnostics.CpuTransparentRecordMicroseconds}, bloomExtractRecordUs={diagnostics.CpuBloomExtractRecordMicroseconds}, " +
            $"bloomDownsampleRecordUs={diagnostics.CpuBloomDownsampleRecordMicroseconds}, bloomUpsampleRecordUs={diagnostics.CpuBloomUpsampleRecordMicroseconds}, " +
            $"fogRecordUs={diagnostics.CpuFogRecordMicroseconds}, autoExposureRecordUs={diagnostics.CpuAutoExposureRecordMicroseconds}, " +
            $"compositeRecordUs={diagnostics.CpuCompositeRecordMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics graph: resources={diagnostics.Graph.ResourceCount}, passes={diagnostics.Graph.PassCount}, " +
            $"pipeline='{diagnostics.ProductionPipelineName}', declaredPasses={diagnostics.ProductionPipelineDeclaredPassCount}, " +
            $"activePasses={diagnostics.ProductionPipelineActivePassCount}, " +
            $"ownedTargets={diagnostics.Graph.OwnedRenderTargetCount}, estimatedMiB={diagnostics.Graph.ResourceMemoryEstimateBytes / (1024.0 * 1024.0):F1}, " +
            $"transient={diagnostics.Graph.TransientResourceCount}, persistent={diagnostics.Graph.PersistentResourceCount}, aliasable={diagnostics.Graph.AliasableResourceCount}, " +
            $"barriers={diagnostics.GraphPlannedBarrierCount}/{diagnostics.GraphExecutedBarrierCount}, queueTransfers={diagnostics.GraphQueueOwnershipTransitionCount}, " +
            $"asyncRequested={diagnostics.AsyncComputeRequested}, asyncEnabled={diagnostics.AsyncComputeEnabled}, asyncCandidates={diagnostics.AsyncComputeCandidatePassCount}, " +
            $"asyncQueueTransfers={diagnostics.AsyncComputeQueueOwnershipTransitionCount}, skippedPasses={diagnostics.SkippedRenderPassCount}.");
        Console.WriteLine(
            $"Frame diagnostics shadows: enabled={diagnostics.DirectionalShadowsEnabled}, map={diagnostics.DirectionalShadowMapSize}, " +
            $"cascades={diagnostics.DirectionalShadowCascadeCount}, lightIndex={diagnostics.ShadowedDirectionalLightIndex}, " +
            $"pcf={diagnostics.DirectionalShadowPcfRadius}/{diagnostics.SpotShadowPcfRadius}/{diagnostics.PointShadowPcfRadius}, " +
            $"forwardReceiverCapacity={diagnostics.ForwardShadowReceiverMeshletCapacity}, debug={diagnostics.ShadowDebugView}, " +
            $"normalBias={diagnostics.ShadowNormalBias:F4}, slopeBias={diagnostics.ShadowSlopeScaledDepthBias:F2}.");
        DirectionalShadowRuntimeDiagnostics directionalShadow = diagnostics.DirectionalShadowRuntime;
        DirectionalShadowReceiverCounters shadowReceivers = directionalShadow.ReceiverCounters;
        Console.WriteLine(
            $"Frame diagnostics directional-shadow transport: range={directionalShadow.EffectiveNearDistance:F2}-{directionalShadow.EffectiveFarDistance:F2}, " +
            $"splits={string.Join('/', directionalShadow.CascadeSplits)}, blend={directionalShadow.CascadeBlendFraction:F3}, " +
            $"staticCache={directionalShadow.StaticCacheActiveMask}/{directionalShadow.StaticCacheValidMask}/" +
            $"{directionalShadow.StaticCacheRefreshMask}/{directionalShadow.StaticCacheReuseMask}, " +
            $"lod0Fallback={directionalShadow.ConservativeLodFallbackCount}, receiverReadback={shadowReceivers.ReadbackValid}, " +
            $"selected={string.Join('/', shadowReceivers.PrimarySelectionCounts)}, " +
            $"projectionReject={string.Join('/', shadowReceivers.ProjectionRejectedCounts)}, " +
            $"uvDepthReject={string.Join('/', shadowReceivers.UvDepthRejectedCounts)}, " +
            $"fallback={string.Join('/', shadowReceivers.FallbackCounts)}, " +
            $"blended={string.Join('/', shadowReceivers.TransitionBlendCounts)}, unresolved={shadowReceivers.UnresolvedCount}.");
        Console.WriteLine(
            $"Frame diagnostics directional-shadow receiver values: resolved={string.Join('/', shadowReceivers.PrimaryResolvedCounts)}, " +
            $"clearFootprint={string.Join('/', shadowReceivers.ClearDepthFootprintCounts)}, " +
            $"primaryLit/Partial/Shadowed={string.Join('/', shadowReceivers.PrimaryFullyLitCounts)}:" +
            $"{string.Join('/', shadowReceivers.PrimaryPartiallyShadowedCounts)}:" +
            $"{string.Join('/', shadowReceivers.PrimaryFullyShadowedCounts)}, " +
            $"finalLit/Partial/Shadowed={string.Join('/', shadowReceivers.FinalFullyLitCounts)}:" +
            $"{string.Join('/', shadowReceivers.FinalPartiallyShadowedCounts)}:" +
            $"{string.Join('/', shadowReceivers.FinalFullyShadowedCounts)}, " +
            $"receiverDepthAvg={string.Join('/', shadowReceivers.AverageReceiverDepths)}, " +
            $"sampledDepthMinAvg={string.Join('/', shadowReceivers.AverageMinimumSampledDepths)}, " +
            $"sampledDepthMaxAvg={string.Join('/', shadowReceivers.AverageMaximumSampledDepths)}.");
        Console.WriteLine(
            $"Frame diagnostics local shadows: spotEnabled={diagnostics.SpotShadowsEnabled}, spotCandidates={diagnostics.SpotShadowCandidateCount}, " +
            $"spotSelected={diagnostics.SpotShadowSelectedCount}, spotRejected={diagnostics.SpotShadowRejectedByBudgetCount}, " +
            $"atlas={diagnostics.SpotShadowAtlasSize} tile={diagnostics.SpotShadowTileSize}, atlasUsed={diagnostics.SpotShadowAtlasUsedTiles}/{diagnostics.SpotShadowAtlasCapacity}, " +
            $"spotRecordUs={diagnostics.CpuSpotShadowRecordMicroseconds}, pointEnabled={diagnostics.PointShadowsEnabled}, " +
            $"pointCandidates={diagnostics.PointShadowCandidateCount}, pointSelected={diagnostics.PointShadowSelectedCount}, " +
            $"pointRejected={diagnostics.PointShadowRejectedByBudgetCount}, pointMap={diagnostics.PointShadowMapSize}, " +
            $"pointFaces={diagnostics.PointShadowRenderedFaceCount}, pointRecordUs={diagnostics.CpuPointShadowRecordMicroseconds}, " +
            $"localGpuJustified={diagnostics.SceneSubmissionLocalShadowGpuCompactionJustified}, spotTests={diagnostics.SceneSubmissionSpotShadowMeshletLightTests}, " +
            $"pointTests={diagnostics.SceneSubmissionPointShadowMeshletFaceTests}, localGpuStatus='{diagnostics.SceneSubmissionLocalShadowGpuCompactionStatus}', " +
            $"localOverflow='{diagnostics.SceneSubmissionLocalShadowOverflowSummary}'.");
        Console.WriteLine(
            $"Frame diagnostics HDR: enabled={diagnostics.HdrEnabled}, sceneColorFormat={diagnostics.SceneColorFormat}, " +
            $"toneMapper={diagnostics.ToneMapper}, exposure={diagnostics.Exposure:F2}, autoExposure={diagnostics.AutoExposureEnabled}, " +
            $"avgLum={diagnostics.AutoExposureAverageLuminance:F4}, targetExposure={diagnostics.AutoExposureTargetExposure:F2}, " +
            $"samples={diagnostics.AutoExposureSampleCount}.");
        Console.WriteLine(
            $"Frame diagnostics bloom: enabled={diagnostics.BloomEnabled}, format={diagnostics.BloomFormat}, " +
            $"base={diagnostics.BloomBaseWidth}x{diagnostics.BloomBaseHeight}, mips={diagnostics.BloomMipCount}, " +
            $"intensity={diagnostics.BloomIntensity:F2}, threshold={diagnostics.BloomThreshold:F2}, knee={diagnostics.BloomKnee:F2}, " +
            $"radius={diagnostics.BloomRadius:F2}, debug={diagnostics.BloomDebugView}, debugMip={diagnostics.BloomDebugMipLevel}.");
        Console.WriteLine(
            $"Frame diagnostics fog: enabled={diagnostics.FogEnabled}, mode={diagnostics.FogMode}, colorMode={diagnostics.FogColorMode}, " +
            $"density={diagnostics.FogDensity:F3}, start={diagnostics.FogStartDistance:F1}, end={diagnostics.FogEndDistance:F1}, " +
            $"height={diagnostics.FogHeight:F1}, falloff={diagnostics.FogHeightFalloff:F3}, heightDensity={diagnostics.FogHeightDensity:F3}, " +
            $"maxOpacity={diagnostics.FogMaxOpacity:F2}, inscatter={diagnostics.FogDirectionalInscatteringEnabled}, " +
            $"size={diagnostics.FogWidth}x{diagnostics.FogHeightPixels}, format={diagnostics.FogFormat}, debug={diagnostics.FogDebugView}, " +
            $"technique={diagnostics.FogRequestedTechnique}->{diagnostics.FogEffectiveTechnique}, status={diagnostics.VolumetricFogStatus}, " +
            $"froxel={diagnostics.VolumetricFogGridWidth}x{diagnostics.VolumetricFogGridHeight}x{diagnostics.VolumetricFogGridDepth}, " +
            $"clusters={diagnostics.VolumetricFogClusterCount}, bytes={diagnostics.VolumetricFogAllocatedBytes}, " +
            $"volumes={diagnostics.VolumetricFogLocalVolumeCount}, smokeParticles={diagnostics.VolumetricFogParticleAdmittedCount}/{diagnostics.VolumetricFogParticleCandidateCount}, " +
            $"history={diagnostics.VolumetricFogHistoryValid}, L2={diagnostics.VolumetricFogDirectionalL2Active}, " +
            $"energySplit={diagnostics.VolumetricFogEnergyOwnershipSeparated}, multiScatter={diagnostics.VolumetricFogMultipleScatteringIterations}.");
        Console.WriteLine(
            $"Frame diagnostics fog output: readback={diagnostics.VolumetricFogOutputReadbackValid}, produced={diagnostics.VolumetricFogOutputProduced}, " +
            $"samples={diagnostics.VolumetricFogDiagnosticSampleCount}, sampled medium/direct/indirect/L2={diagnostics.VolumetricFogMediumNonEmptyFroxelCount}/{diagnostics.VolumetricFogDirectNonZeroFroxelCount}/{diagnostics.VolumetricFogIndirectNonZeroFroxelCount}/{diagnostics.VolumetricFogDdgiSupportedFroxelCount}, " +
            $"extinction(max/mean)={diagnostics.VolumetricFogMaximumExtinction:F4}/{diagnostics.VolumetricFogMeanExtinction:F4}, " +
            $"direct(max/mean)={diagnostics.VolumetricFogMaximumDirectLuminance:F4}/{diagnostics.VolumetricFogMeanDirectLuminance:F4}, " +
            $"indirect(max/mean)={diagnostics.VolumetricFogMaximumIndirectLuminance:F4}/{diagnostics.VolumetricFogMeanIndirectLuminance:F4}, " +
            $"transmittance(min/mean)={diagnostics.VolumetricFogMinimumTransmittance:F4}/{diagnostics.VolumetricFogMeanTransmittance:F4}, " +
            $"historyAccepted/rejected={diagnostics.VolumetricFogHistoryAcceptedFroxelCount}/{diagnostics.VolumetricFogHistoryRejectedFroxelCount}, " +
            $"rejected invalid/bounds/extinction/radiance/velocity={diagnostics.VolumetricFogHistoryRejectedInvalidFroxelCount}/{diagnostics.VolumetricFogHistoryRejectedBoundsFroxelCount}/{diagnostics.VolumetricFogHistoryRejectedExtinctionFroxelCount}/{diagnostics.VolumetricFogHistoryRejectedRadianceFroxelCount}/{diagnostics.VolumetricFogHistoryRejectedVelocityFroxelCount}, " +
            $"overflow={diagnostics.VolumetricFogClusterOverflowCount}, nonFinite={diagnostics.VolumetricFogNonFiniteCount}.");
        Console.WriteLine(
            $"Frame diagnostics fog GPU stages: total={diagnostics.GpuFogMicroseconds}us, " +
            $"noise/source/medium/transmittance/ddgi/cache/multiple/temporal/integrate/resolve/composite=" +
            $"{diagnostics.GpuVolumetricFogNoiseMicroseconds}/{diagnostics.GpuVolumetricFogSourceCullMicroseconds}/{diagnostics.GpuVolumetricFogMediumMicroseconds}/{diagnostics.GpuVolumetricFogTransmittanceMicroseconds}/{diagnostics.GpuVolumetricFogDdgiBounceMicroseconds}/{diagnostics.GpuVolumetricFogLightingCacheMicroseconds}/{diagnostics.GpuVolumetricFogMultipleScatteringMicroseconds}/{diagnostics.GpuVolumetricFogTemporalMicroseconds}/{diagnostics.GpuVolumetricFogIntegrateMicroseconds}/{diagnostics.GpuVolumetricFogResolveMicroseconds}/{diagnostics.GpuVolumetricFogCompositeMicroseconds}us.");
        Console.WriteLine(
            $"Frame diagnostics AO: enabled={diagnostics.AmbientOcclusionEnabled}, mode={diagnostics.AmbientOcclusionMode}, " +
            $"size={diagnostics.AmbientOcclusionWidth}x{diagnostics.AmbientOcclusionHeight}, format={diagnostics.AmbientOcclusionFormat}, " +
            $"scale={diagnostics.AmbientOcclusionResolutionScale:F2}, radius={diagnostics.AmbientOcclusionRadius:F2}, " +
            $"intensity={diagnostics.AmbientOcclusionIntensity:F2}, bias={diagnostics.AmbientOcclusionBias:F3}, " +
            $"samples={diagnostics.AmbientOcclusionSampleCount}, blur={diagnostics.AmbientOcclusionBlurRadius}, forwardSampling={diagnostics.AmbientOcclusionForwardSamplingMode}, " +
            $"forwardDepthAwareSamples={diagnostics.AmbientOcclusionForwardDepthAwareSamples}, " +
            $"debug={diagnostics.AmbientOcclusionDebugView}, aoRecordUs={diagnostics.CpuAmbientOcclusionRecordMicroseconds}, " +
            $"blurRecordUs={diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds}.");
        PrintGiDiagnostics(diagnostics);
        PrintDdgiRingDiagnostics(diagnostics);
        PrintDdgiVolumeActivityDiagnostics(diagnostics);
        PrintDdgiInvestigationDiagnostics(diagnostics);
        PrintDdgiUpdateDiagnostics(diagnostics);
        Console.WriteLine(
            $"Frame diagnostics AA: mode={diagnostics.AntiAliasingMode}, size={diagnostics.AntiAliasingWidth}x{diagnostics.AntiAliasingHeight}, " +
            $"input={diagnostics.AntiAliasingInputFormat}, output={diagnostics.AntiAliasingOutputFormat}, debug={diagnostics.AntiAliasingDebugView}, " +
            $"smaaLookups={diagnostics.SmaaLookupTexturesReady}, fxaaRecordUs={diagnostics.CpuFxaaRecordMicroseconds}, " +
            $"smaaEdgeUs={diagnostics.CpuSmaaEdgeRecordMicroseconds}, smaaBlendUs={diagnostics.CpuSmaaBlendRecordMicroseconds}, " +
            $"smaaNeighborhoodUs={diagnostics.CpuSmaaNeighborhoodRecordMicroseconds}, jitter={diagnostics.JitterEnabled}:{diagnostics.JitterX:F6},{diagnostics.JitterY:F6}.");
        Console.WriteLine(
            $"Frame diagnostics environment: enabled={diagnostics.EnvironmentEnabled}, source={diagnostics.EnvironmentSourceKind}, " +
            $"fallback={diagnostics.EnvironmentUsesFallback}, path='{diagnostics.EnvironmentSourcePath}', sky={diagnostics.SkyIntensity:F2}, " +
            $"diffuse={diagnostics.DiffuseIblIntensity:F2}, specular={diagnostics.SpecularIblIntensity:F2}, " +
            $"env={diagnostics.EnvironmentCubemapSize}, irradiance={diagnostics.IrradianceCubemapSize}, " +
            $"prefilter={diagnostics.PrefilteredEnvironmentSize} mips={diagnostics.PrefilteredEnvironmentMipCount}, " +
            $"brdf={diagnostics.BrdfLutSize}, debug={diagnostics.EnvironmentDebugView}, " +
            $"textureMiB={diagnostics.EnvironmentTextureBytes / (1024.0 * 1024.0):F1}.");
        Console.WriteLine(
            $"Frame diagnostics reflections: enabled={diagnostics.ReflectionsEnabled}, mode={diagnostics.ReflectionMode}, " +
            $"probes={diagnostics.ReflectionProbeCount}/{diagnostics.ReflectionProbeCapacity}, resolution={diagnostics.ReflectionProbeResolution}, " +
            $"mips={diagnostics.ReflectionProbeMipCount}, maxPerPixel={diagnostics.MaxReflectionProbesPerPixel}, " +
            $"estimatedMiB={diagnostics.ReflectionProbeEstimatedBytes / (1024.0 * 1024.0):F1}, debug={diagnostics.ReflectionDebugView}, " +
            $"capturesQueued={diagnostics.ReflectionProbeCapturesQueued}, capturesCompleted={diagnostics.ReflectionProbeCapturesCompleted}, " +
            $"uploadUs={diagnostics.CpuReflectionProbeUploadMicroseconds}, captureRecordUs={diagnostics.CpuReflectionProbeCaptureRecordMicroseconds}, " +
            $"prefilterRecordUs={diagnostics.CpuReflectionProbePrefilterRecordMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics transparent reflections: enabled={diagnostics.TransparentSampleReflections}, snapshot={diagnostics.OpaqueSceneColorSnapshotAvailable}, " +
            $"receivers={diagnostics.TransparentReflectionReceiverObjectCount}/{diagnostics.TransparentReflectionReceiverMeshletCount}, " +
            $"rayBudget={diagnostics.TransparentSceneReflectionRayTaskBudget}, rayRequests={diagnostics.TransparentReflectionRayRequestCount}, " +
            $"estimatedSources=ssr:{diagnostics.TransparentReflectionEstimatedSsrHitCount},rayHit:{diagnostics.TransparentReflectionEstimatedRayHitCount}," +
            $"rayMiss:{diagnostics.TransparentReflectionEstimatedRayMissCount},budgetReject:{diagnostics.TransparentReflectionEstimatedBudgetRejectedCount}," +
            $"ddgi:{diagnostics.TransparentReflectionEstimatedDdgiFallbackCount},probe:{diagnostics.TransparentReflectionEstimatedProbeFallbackCount}," +
            $"environment:{diagnostics.TransparentReflectionEstimatedEnvironmentFallbackCount}.");
        Console.WriteLine(
            $"Frame diagnostics culling: cpuListRole=cameraInvariantSuperset, objectCandidatesCpu={diagnostics.ObjectCandidatesCpu}, objectFrustumCulledCpu={diagnostics.ObjectFrustumCulledCpu}, " +
            $"meshletCandidatesCpu={diagnostics.MeshletCandidatesCpu}, meshletFrustumCulledCpu={diagnostics.MeshletFrustumCulledCpu}, " +
            $"meshletLodSkippedCpu={diagnostics.MeshletLodSkippedCpu}, lod0Submitted={diagnostics.MeshletLod0SubmittedCpu}, " +
            $"lod1Submitted={diagnostics.MeshletLod1SubmittedCpu}, lod2Submitted={diagnostics.MeshletLod2SubmittedCpu}, " +
            $"gpuMeshletCounters={diagnostics.GpuMeshletCountersStatus}, " +
            $"depthTasks={diagnostics.DepthTaskInvocations}, depthFrustumCulledGpu={diagnostics.DepthFrustumCulledMeshletsGpu}, depthEmitted={diagnostics.DepthEmittedMeshletsGpu}, " +
            $"forwardTasks={diagnostics.ForwardTaskInvocations}, forwardFrustumCulledGpu={diagnostics.ForwardFrustumCulledMeshletsGpu}, " +
            $"occlusionTested={diagnostics.ForwardOcclusionTestedMeshletsGpu}, occlusionCulled={diagnostics.OcclusionCulledMeshlets}, forwardEmitted={diagnostics.ForwardEmittedMeshletsGpu}.");
        Console.WriteLine(
            $"Frame diagnostics scene submission: mode={diagnostics.SceneSubmissionActiveMode}, forwardPath={diagnostics.SceneSubmissionForwardPath}, taskShader={diagnostics.SceneSubmissionForwardTaskShader}, cpuCandidates={diagnostics.SceneSubmissionCpuCandidateCount}, " +
            $"gpuEmitted={diagnostics.SceneSubmissionGpuEmittedCount}, indirectTasks={diagnostics.SceneSubmissionIndirectTaskCount}, " +
            $"forwardBuckets={diagnostics.ForwardSimpleMeshletCount}/{diagnostics.ForwardFullMaterialMeshletCount}/{diagnostics.ForwardLocalProbeMeshletCount}, " +
            $"fallback='{diagnostics.SceneSubmissionFallbackReason}', compactionSkip='{diagnostics.SceneSubmissionCompactionSkipReason}', indirectSkip='{diagnostics.SceneSubmissionIndirectDispatchSkipReason}', " +
            $"gpuSettings={diagnostics.SceneSubmissionGpuCompactionEnabled}/{diagnostics.SceneSubmissionGpuLodSelectionEnabled}/{diagnostics.SceneSubmissionGpuShadowCompactionEnabled}, " +
            $"gpuCandidates={diagnostics.SceneSubmissionGpuOpaqueCandidateCount}, gpuRejected={diagnostics.SceneSubmissionGpuOpaqueFrustumRejectedCount}, gpuOverflow={diagnostics.SceneSubmissionGpuOpaqueOverflowCount}, " +
            $"gpuLod={diagnostics.SceneSubmissionGpuLod0EmittedCount}/{diagnostics.SceneSubmissionGpuLod1EmittedCount}/{diagnostics.SceneSubmissionGpuLod2EmittedCount}, gpuLodDecimated={diagnostics.SceneSubmissionGpuOpaqueLodDecimatedCount}, " +
            $"gpuDepth={diagnostics.SceneSubmissionGpuCompactedSolidDepthMeshletCount}/{diagnostics.SceneSubmissionGpuCompactedMaskedDepthMeshletCount}, depthOverflow={diagnostics.SceneSubmissionGpuDepthOverflowCount}, " +
            $"gpuDirShadow={diagnostics.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount}/{diagnostics.SceneSubmissionGpuDirectionalShadowCandidateCount}, dirShadowOverflow={diagnostics.SceneSubmissionGpuDirectionalShadowOverflowCount}, " +
            $"validation='{diagnostics.SceneSubmissionValidationStatus}', validationCounts={diagnostics.SceneSubmissionValidationCpuOpaqueCount}/{diagnostics.SceneSubmissionValidationGpuOpaqueCount}, " +
            $"validationMismatches={diagnostics.SceneSubmissionValidationMismatchCount}, " +
            $"compactedBytes={diagnostics.SceneSubmissionOpaqueCompactedMeshletDrawBufferSize}, depthCompactedBytes={diagnostics.SceneSubmissionSolidDepthCompactedMeshletDrawBufferSize}/{diagnostics.SceneSubmissionMaskedDepthCompactedMeshletDrawBufferSize}, shadowCompactedBytes={diagnostics.SceneSubmissionDirectionalShadowCompactedMeshletDrawBufferSize}, counterBytes={diagnostics.SceneSubmissionCounterBufferSize}, " +
            $"indirectBytes={diagnostics.SceneSubmissionOpaqueIndirectDispatchBufferSize}.");
        Console.WriteLine(
            $"Frame diagnostics meshlets/uploads: totalMeshlets={diagnostics.MeshletCountTotal}, submittedCpu={diagnostics.MeshletCountSubmittedCpu}, " +
            $"avgTris={diagnostics.AvgTrianglesPerSubmittedMeshlet:F1}, avgVerts={diagnostics.AvgVerticesPerSubmittedMeshlet:F1}, " +
            $"under16Tris={diagnostics.SmallMeshletsUnder16Triangles}, under32Tris={diagnostics.SmallMeshletsUnder32Triangles}, " +
            $"uploadedBytes={diagnostics.UploadedBytes}, objectBytes={diagnostics.ObjectUploadBytes}, instanceBytes={diagnostics.InstanceUploadBytes}, " +
            $"meshletDrawBytes={diagnostics.MeshletDrawUploadBytes}, transparentMeshletDrawBytes={diagnostics.TransparentMeshletDrawUploadBytes}, " +
            $"stableSceneInputUploadBytes={diagnostics.StableSceneInputUploadBytes}, cpuCandidateListUploadBytes={diagnostics.CpuCandidateListUploadBytes}, " +
            $"cameraRebuiltCpuLists={diagnostics.CameraDrivenCpuDrawListRebuilt}, " +
            $"materialBytes={diagnostics.MaterialUploadBytes}, materialExtensionBytes={diagnostics.MaterialExtensionUploadBytes}, materialExtensions={diagnostics.MaterialExtensionDataCount}, " +
            $"materialDebug={diagnostics.MaterialDebugView}, lightBytes={diagnostics.LightUploadBytes}, uploads={diagnostics.SceneUploadCount}, uploadSkipped={diagnostics.SceneUploadSkipped}.");
        Console.WriteLine(
            $"Frame diagnostics assets: loadedFileTextures={diagnostics.LoadedFileTextureCount}, mipFallbacks={diagnostics.MipmapFallbackCount}, " +
            $"downscaledTextures={diagnostics.DownscaledTextureCount}, maxTextureDim={diagnostics.MaxLoadedTextureDimension}, " +
            $"estimatedTextureMiB={diagnostics.EstimatedTextureBytes / (1024.0 * 1024.0):F1}, " +
            $"model='{diagnostics.LoadedModelName}', modelObjects={diagnostics.ModelRenderObjectCount}, registeredMeshes={diagnostics.RegisteredMeshCount}, " +
            $"modelMaterials={diagnostics.LoadedMaterialCount}, modelTextures={diagnostics.LoadedTextureCount}, defaultWhite={diagnostics.DefaultWhiteSubstitutions}, " +
            $"defaultNormal={diagnostics.DefaultNormalSubstitutions}, defaultBlack={diagnostics.DefaultBlackSubstitutions}.");
    }

    private static void PrintGiDiagnostics(RendererDiagnostics diagnostics)
    {
        ulong simpleOneSidedBackFaceRayCount = 0;
        foreach (DdgiVolumeDiagnosticsEntry volume in diagnostics.DdgiVolumes)
        {
            if (volume.EnergyCountersReadbackValid != 0)
                simpleOneSidedBackFaceRayCount += volume.EnergyCounters.OneSidedBackFaceRayCount;
        }

        Console.WriteLine(
            $"Frame diagnostics GI: enabled={diagnostics.GlobalIlluminationEnabled}, mode={diagnostics.GlobalIlluminationMode}, requestedDebug={diagnostics.GlobalIlluminationRequestedDebugView}, debug={diagnostics.GlobalIlluminationDebugView}, debugAvailable={diagnostics.GlobalIlluminationRequestedDebugViewAvailable}, debugAvailability='{diagnostics.GlobalIlluminationDebugViewAvailabilityReason}', " +
            $"rayQuerySupported={diagnostics.GlobalIlluminationRayQuerySupported}, rayQueryActive={diagnostics.GlobalIlluminationRayQueryActive}, " +
            $"ddgiVolumes={diagnostics.DdgiProbeVolumeCount}, ddgiProbes={diagnostics.DdgiActiveProbeCount}/{diagnostics.DdgiProbeCount}, " +
            $"ddgiUpdated={diagnostics.DdgiProbesUpdated}, ddgiRays={diagnostics.DdgiRaysPerProbe}, relocation={diagnostics.DdgiProbeRelocationCount}, " +
            $"simpleState active/probes/updated/recenter/preserve/clear/fresh={diagnostics.SimpleDdgiActive}/{diagnostics.SimpleDdgiProbeCount}/{diagnostics.SimpleDdgiProbesUpdated}/" +
            $"{diagnostics.SimpleDdgiRecentered}/{diagnostics.SimpleDdgiAtlasPreservedOnRecenter}/{diagnostics.SimpleDdgiAtlasCleared}/{diagnostics.SimpleDdgiAtlasFresh}, " +
            $"updateExec={diagnostics.DdgiUpdateExecuted}:'{diagnostics.DdgiUpdateSkipReason}', publishExec={diagnostics.DdgiPublishExecuted}:'{diagnostics.DdgiPublishSkipReason}', " +
            $"cacheGeneration={diagnostics.DdgiCacheGeneration}, cacheFrame={diagnostics.DdgiLastUpdatedFrameSerial}, cacheWarmup={diagnostics.DdgiCacheWarmupState}, cacheLatencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}, " +
            $"ddgiClipmapCoverage attempts/ok/fail/avgEdgeFade/avgBlend=" +
            $"{diagnostics.DdgiClipmapInfoPrimaryAttemptCount}/{diagnostics.DdgiClipmapInfoPrimaryOkCount}/{diagnostics.DdgiClipmapInfoPrimaryFailedCount}/" +
            $"{diagnostics.DdgiClipmapInfoPrimaryEdgeFadeAverage:F3}/{diagnostics.DdgiClipmapInfoPrimaryBlendWeightAverage:F3}, " +
            $"ddgiEstimate spatial/support/data/visibility/leak/effective/sampledIrrLum/ddgiDiffuseLum/hybridFinalLum/fallbackWeight/ownership/reloc/inactive=" +
            $"{diagnostics.DdgiAverageSpatialCoverageEstimate:F3}/{diagnostics.DdgiAverageSupportCoverageEstimate:F3}/{diagnostics.DdgiAverageDataConfidenceEstimate:F3}/" +
            $"{diagnostics.DdgiAverageVisibilityConfidenceEstimate:F3}/{diagnostics.DdgiAverageLeakAttenuationEstimate:F3}/{diagnostics.DdgiAverageEffectiveContributionEstimate:F3}/" +
            $"{diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F5}/{diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F5}/{diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F5}/{diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight:F3}/{diagnostics.DdgiAverageOwnershipConsumedEstimate:F3}/" +
            $"relocation legacyCount/simpleDisplacement={diagnostics.DdgiRelocatedProbeFractionEstimate:F3}/{diagnostics.DdgiAverageRelocationDisplacementFractionEstimate:F3}/{diagnostics.DdgiClassifiedInactiveProbeCountEstimate}, " +
            $"simpleClassification backface/close/hardInvalid/oneSidedRays={diagnostics.SimpleDdgiAverageBackfaceRatioEstimate:F3}/{diagnostics.SimpleDdgiAverageCloseRatioEstimate:F3}/{diagnostics.SimpleDdgiAverageHardInvalidProbeScoreEstimate:F3}/{simpleOneSidedBackFaceRayCount}, " +
            $"ddgiAlbedo receiver={diagnostics.DdgiReceiverDiffuseReflectanceLuminance:F4}/{diagnostics.DdgiReceiverDiffuseReflectanceSampleCount}, " +
            $"trace oneSided/opaque/thin/unsupported/reflectOff={diagnostics.DdgiTraceOneSidedBackFaceAlbedoLuminance:F4}/{diagnostics.DdgiTraceOneSidedBackFaceHitCount} " +
            $"{diagnostics.DdgiTraceOpaqueAlbedoLuminance:F4}/{diagnostics.DdgiTraceOpaqueHitCount} " +
            $"{diagnostics.DdgiTraceThinSurfaceAlbedoLuminance:F4}/{diagnostics.DdgiTraceThinSurfaceHitCount} " +
            $"{diagnostics.DdgiTraceUnsupportedTransmissionAlbedoLuminance:F4}/{diagnostics.DdgiTraceUnsupportedTransmissionHitCount} " +
            $"{diagnostics.DdgiTraceReflectDisabledAlbedoLuminance:F4}/{diagnostics.DdgiTraceReflectDisabledHitCount}, " +
            $"ddgiSampledProbeUse currentFrustum/sideRear/staleAge={diagnostics.DdgiSampledProbeCurrentFrustumCount}/{diagnostics.DdgiSampledProbeSideRearCount}/{diagnostics.DdgiSampledProbeStaleAgeCount}, " +
            $"ddgiSupportReject inactive/zeroAlpha/lowQuality={diagnostics.DdgiSupportRejectedInactiveCount}/{diagnostics.DdgiSupportRejectedZeroIrradianceAlphaCount}/{diagnostics.DdgiSupportRejectedLowQualityCount}, " +
            $"ddgiFastGather status={diagnostics.DdgiFastGatherStatus}, attempt/accepted/reject spatial/support/data/ownership={diagnostics.DdgiFastGatherAttemptCount}/{diagnostics.DdgiFastGatherAcceptedCount}/{diagnostics.DdgiFastGatherRejectedZeroSpatialCount}/{diagnostics.DdgiFastGatherRejectedZeroSupportCount}/{diagnostics.DdgiFastGatherRejectedZeroDataCount}/{diagnostics.DdgiFastGatherRejectedZeroOwnershipCount}, " +
            $"simpleGather one/two/recovery={diagnostics.SimpleDdgiGatherMultiplicity.OneGatherPixelCount}/{diagnostics.SimpleDdgiGatherMultiplicity.TwoGatherPixelCount}/{diagnostics.SimpleDdgiGatherMultiplicity.RecoveryGatherPixelCount}, " +
            $"simpleGatherRates one/second/recovery={diagnostics.SimpleDdgiGatherMultiplicity.OneGatherFraction:P2}/{diagnostics.SimpleDdgiGatherMultiplicity.SecondGatherFraction:P2}/{diagnostics.SimpleDdgiGatherMultiplicity.RecoveryGatherFraction:P2}, " +
            $"secondReasons ring/missing/recovery/edge/ownership/debug={diagnostics.SimpleDdgiGatherMultiplicity.RingTransitionBlendCount}/{diagnostics.SimpleDdgiGatherMultiplicity.MissingOrInvalidPrimarySupportCount}/{diagnostics.SimpleDdgiGatherMultiplicity.RecoveryCount}/{diagnostics.SimpleDdgiGatherMultiplicity.CoverageEdgeCount}/{diagnostics.SimpleDdgiGatherMultiplicity.PrimaryOwnershipBelowThresholdCount}/{diagnostics.SimpleDdgiGatherMultiplicity.DebugOrDiagnosticOnlyCount}, " +
            $"ddgiShaderFallback attempt/accepted/empty={diagnostics.DdgiShaderGatherFallbackAttemptCount}/{diagnostics.DdgiShaderGatherFallbackAcceptedCount}/{diagnostics.DdgiShaderGatherFallbackEmptyCount}, " +
            $"ddgiTrace diagSamples/hit/miss/rayLum/directLum/directNoShadowLum/emissiveLum/stableLum/skyLum/zeroDirect/directHit=" +
            $"{diagnostics.DdgiTraceEnergySampleCount}/{diagnostics.DdgiTraceEnergyHitCount}/{diagnostics.DdgiTraceEnergyMissCount}/" +
            $"{diagnostics.DdgiTraceEnergyRayLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyDirectLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyDirectNoShadowLuminanceAverage:F5}/" +
            $"{diagnostics.DdgiTraceEnergyEmissiveLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyStableLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergySkyLuminanceAverage:F5}/" +
            $"{diagnostics.DdgiTraceEnergyHitZeroDirectCount}/{diagnostics.DdgiTraceEnergyHitWithDirectCount}, " +
            $"ddgiShadow rays/occluded/near/avgHitDistance=" +
            $"{diagnostics.DdgiShadowVisibilityRayCount}/{diagnostics.DdgiShadowVisibilityOccludedCount}/{diagnostics.DdgiShadowVisibilityNearHitCount}/{diagnostics.DdgiShadowVisibilityCommittedHitDistanceAverage:F3}, " +
            $"ddgiThin hits detailed/compact/farExcluded={diagnostics.DdgiThinDetailedHitCount}/{diagnostics.DdgiThinCompactHitCount}/{diagnostics.DdgiThinFarFieldExcludedCount}, " +
            $"direct reflected/transmitted={diagnostics.DdgiThinReflectedDirectLuminance:F5}/{diagnostics.DdgiThinTransmittedDirectLuminance:F5}, " +
            $"recursive reflected/transmitted={diagnostics.DdgiThinReflectedRecursiveLuminance:F5}/{diagnostics.DdgiThinTransmittedRecursiveLuminance:F5}, " +
            $"thinShadow rays/layers/max/limit/low={diagnostics.DdgiThinColoredShadowTransmissionRayCount}/{diagnostics.DdgiThinTotalLayersTraversed}/{diagnostics.DdgiThinMaximumLayersTraversed}/{diagnostics.DdgiThinLayerLimitTerminationCount}/{diagnostics.DdgiThinLowTransmittanceTerminationCount}, " +
            $"thinZero opaque/thin/unsupported={diagnostics.DdgiThinZeroRadianceOpaqueHitCount}/{diagnostics.DdgiThinZeroRadianceThinHitCount}/{diagnostics.DdgiThinZeroRadianceUnsupportedHitCount}, " +
            $"thinInvalid unsupported/clamp/nonfinite={diagnostics.DdgiThinUnsupportedTransmissionHitCount}/{diagnostics.DdgiThinEnergyClampCount}/{diagnostics.DdgiThinInvalidTransmissionCount}, " +
            $"ddgiDelivery highOwnershipLowIndirect={diagnostics.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount}, " +
            $"ddgiLight selectedDir/local/visibility/skippedLocal={diagnostics.DdgiSelectedDirectionalHitCount}/{diagnostics.DdgiSelectedLocalHitCount}/{diagnostics.DdgiVisibilityRayCount}/{diagnostics.DdgiSkippedLocalLightCount}, " +
            $"ddgiBlend diagSamples/irrLum/conf/lowConf/nonzero/nonfinite/firefly=" +
            $"{diagnostics.DdgiBlendEnergySampleCount}/{diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage:F5}/{diagnostics.DdgiBlendEnergyConfidenceAverage:F3}/" +
            $"{diagnostics.DdgiBlendEnergyLowConfidenceCount}/{diagnostics.DdgiBlendEnergyNonzeroIrradianceCount}/{diagnostics.DdgiBlendEnergyNonFiniteIrradianceCount}/{diagnostics.DdgiBlendEnergyFireflySuppressedCount}, " +
            $"ddgiProbeConfidence alpha/qx/qy/qz={diagnostics.DdgiProbeIrradianceAlphaAverage:F3}/{diagnostics.DdgiProbeQualityXAverage:F3}/{diagnostics.DdgiProbeQualityYAverage:F3}/{diagnostics.DdgiProbeQualityZAverage:F3}, " +
            $"warmup={diagnostics.DdgiWarmupState}:{diagnostics.DdgiWarmedVisibleProbeFraction:F3}/{diagnostics.DdgiWarmedLocalProbeFraction:F3}/{diagnostics.DdgiWarmedCascade0ProbeFraction:F3}, " +
            $"volumeDesign={FormatDdgiVolumeDesignSummary(diagnostics)}, " +
            $"classification={diagnostics.DdgiProbeClassificationCount}, cpuDdgiUs={diagnostics.CpuDdgiRecordMicroseconds}, " +
            $"cpuSimpleDdgiUs={diagnostics.CpuSimpleDdgiRecordMicroseconds}, cpuFarFieldUs={diagnostics.CpuFarFieldRecordMicroseconds}, cpuGiUs={diagnostics.CpuGlobalIlluminationRecordMicroseconds}, cpuAsBuildUs={diagnostics.CpuAccelerationStructureBuildMicroseconds}, " +
            $"gpuDdgiUs={diagnostics.GpuDdgiUpdateMicroseconds}, " +
            $"schedulerCommitFailures='{diagnostics.SimpleDdgiSchedulerCommitFailureBreakdown}', " +
            $"bytes={diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes + diagnostics.AccelerationStructureBytes}.");
        SimpleDdgiUploadTiming upload = diagnostics.SimpleDdgiUploadTiming;
        Console.WriteLine(
            $"Frame diagnostics Simple DDGI upload: totalUs={upload.TotalMicroseconds}, " +
            $"layout/readback/capacity/invalidation/scheduler/importance/queue/lifecycle/atlas/bufferUpload/otherUs=" +
            $"{upload.LayoutMicroseconds}/{upload.ReadbackMicroseconds}/{upload.CapacityMicroseconds}/{upload.InvalidationMicroseconds}/" +
            $"{upload.SchedulerRefreshMicroseconds}/{upload.ImportanceMicroseconds}/{upload.QueueBuildMicroseconds}/{upload.LifecycleTelemetryMicroseconds}/" +
            $"{upload.AtlasMaintenanceMicroseconds}/{upload.BufferUploadMicroseconds}/{upload.OtherMicroseconds}, " +
            $"readbackProbes={upload.ReadbackProbeCount}, schedulerEntries={upload.SchedulerEntryRefreshCount}, " +
            $"schedulerWake={upload.SchedulerWakeEntryRefreshCount}/{upload.SchedulerWakeRefreshBudget}:saturated={upload.SchedulerWakeBudgetSaturated}, " +
            $"schedulerFullRebuilds={upload.SchedulerFullRebuildCount}, visibilityEntries={upload.VisibilityEntryRefreshCount}, " +
            $"stateDirtySlots/runs={upload.StateDirtySlotCount}/{upload.StateUploadRunCount}, " +
            $"receiver capacity/bytes/generation/published/invalidationBytes/ranges/fullClear=" +
            $"{diagnostics.SimpleDdgiReceiverProbeCapacity}/{diagnostics.SimpleDdgiReceiverProbeBytes}/" +
            $"{diagnostics.SimpleDdgiReceiverResourceGeneration}/{diagnostics.SimpleDdgiReceiverRecordsPublished}/" +
            $"{diagnostics.SimpleDdgiReceiverInvalidationBytes}/{diagnostics.SimpleDdgiReceiverInvalidationRangeCount}/" +
            $"{diagnostics.SimpleDdgiReceiverFullClear}.");
        SimpleDdgiStorageDiagnostics storage = diagnostics.SimpleDdgiStorage;
        if (storage.IsAvailable)
        {
            SimpleDdgiStorageValidationCounters validation =
                storage.ValidationCounters;
            Console.WriteLine(
                $"Frame diagnostics Simple DDGI storage: mode/abi/codebook={storage.PackingMode}/{(uint)storage.AbiVersion}/{storage.DirectionCodebookVersion}, " +
                $"canonical={storage.CanonicalIrradianceFormat}:{storage.CanonicalIrradianceBytes}/{storage.CanonicalVisibilityFormat}:{storage.CanonicalVisibilityBytes}, " +
                $"cache total/legacy/c28/c24/pad={storage.SourceCacheBytes}/{storage.SourceCacheLegacyBytes}/{storage.SourceCacheCompact28Bytes}/{storage.SourceCacheCompact24Bytes}/{storage.SourceCacheAlignmentBytes}, " +
                $"cacheRays legacy/c28/c24={storage.SourceCacheLegacyRayCount}/{storage.SourceCacheCompact28RayCount}/{storage.SourceCacheCompact24RayCount}, " +
                $"cacheLayout requested/effective/hotColdVolumes={storage.SourceCacheRequestedLayoutMode}/{storage.SourceCacheEffectiveLayoutMode}/{storage.SourceCacheHotColdVolumeCount}, " +
                $"cacheAdmission identity/hasSample/frame/reason={storage.SourceCacheAdmissionLayoutIdentity}/{storage.SourceCacheAdmissionHasCompletedSample}/{storage.SourceCacheAdmissionSampleFrameSerial}/'{storage.SourceCacheLayoutAdmissionReason}', " +
                $"distance16 volumes/probes={storage.Fp16DistanceEligibleVolumeCount}/{storage.Fp16DistanceEligibleProbeCount}, " +
                $"scratch stride/bytes={storage.RayScratchStrideBytes}/{storage.RayScratchBytes}, " +
                $"mirror mode requested/eligible/admitted/provisioned={storage.MirrorCoverageMode}/{storage.MirrorRequestedProbeCount}/{storage.MirrorEligibleProbeCount}/{storage.MirrorAdmittedProbeCount}/{storage.MirrorProvisionedProbeCount}, " +
                $"mirror logical/allocated={storage.MirrorTotalBytes}/{storage.MirrorAllocatedBytes}, " +
                $"mirrorSamples valid/frame/opportunity/hit/seam/unmirrored/invalidMap={validation.ReadbackValid}/{validation.FrameSerial}/{validation.MirrorInteriorOpportunityCount}/{validation.MirrorImageHitCount}/{validation.MirrorSeamFallbackCount}/{validation.MirrorUnmirroredFallbackCount}/{validation.MirrorInvalidMapFallbackCount}, " +
                $"pack attempts/nonfinite/saturated/maxRadianceError/maxDistanceError={validation.CachePackAttemptCount}/{validation.CachePackNonFiniteCount}/{validation.CachePackRadianceSaturationCount}/{validation.CachePackMaximumRadianceError:G6}/{validation.CachePackMaximumDistanceError:G6}, " +
                $"direction samples/epochMismatch/invalidEpoch/invalidHitKind/max/p99={validation.DirectionComparisonSampleCount}/{validation.DirectionEpochMismatchCount}/{validation.InvalidSourceEpochCount}/{validation.InvalidHitKindCount}/{validation.DirectionMaximumAngularErrorRadians:G6}/{validation.DirectionAngularErrorP99UpperBoundRadians:G6}, " +
                $"generation storage/mirror/allocation={storage.StorageLayoutFingerprint}/{storage.MirrorLayoutFingerprint}/{storage.MirrorAllocationGeneration}, " +
                $"fallback='{storage.MirrorFallbackReason}'.");
        }
        SimpleDdgiWarmStartTelemetry warmStart =
            diagnostics.SimpleDdgiWarmStart;
        if (warmStart.Enabled)
        {
            Console.WriteLine(
                $"Frame diagnostics Simple DDGI warm start: eligible/found/accepted/active=" +
                $"{warmStart.Eligible}/{warmStart.CacheFound}/{warmStart.CacheAccepted}/{warmStart.PriorActive}, " +
                $"pending load/readback/save={warmStart.LoadPending}/{warmStart.ReadbackPending}/{warmStart.SavePending}, " +
                $"cached volumes/probes/applied={warmStart.CachedVolumeCount}/{warmStart.CachedProbeCount}/{warmStart.AppliedProbeCount}, " +
                $"loads/rejects/applies/saves={warmStart.LoadCount}/{warmStart.RejectCount}/{warmStart.ApplyCount}/{warmStart.SaveCount}, " +
                $"bytes loaded/readback/saved={warmStart.LoadedFileBytes}/{warmStart.ReadbackBytes}/{warmStart.SavedFileBytes}, " +
                $"status='{warmStart.Status}', path='{warmStart.CachePath}'.");
        }
        SimpleDdgiTransportConvergenceTelemetry transportConvergence =
            diagnostics.SimpleDdgiTransportConvergence;
        Console.WriteLine(
            $"Frame diagnostics Simple DDGI tail: phase/reason/current=" +
            $"{transportConvergence.TailPhase}/{transportConvergence.TailReason}/{transportConvergence.TailCertificateCurrent}, " +
            $"controller solve/audit={transportConvergence.TailSolveEpoch}/{transportConvergence.TailAuditEpoch}, " +
            $"resident solve/visited/participants=" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackSolveEpoch}/" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackSolveVisitedCount}/" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackSolveParticipantCount}, " +
            $"accepted/source/cached/published=" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackAcceptedCount}/" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackSourceProbeCount}/" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackCachedSolverProbeCount}/" +
            $"{diagnostics.SimpleDdgiSchedulerFeedbackPublishedCount}, " +
            $"deadlineRecoveries={transportConvergence.TailConvergenceDeadlineRecoveryCount}.");
        SimpleDdgiRefinementBrickDiagnostics refinement =
            diagnostics.SimpleDdgiRefinement;
        SimpleDdgiRefinementEmissiveDemandDiagnostics emissiveDemand =
            diagnostics.SimpleDdgiRefinementEmissiveDemand;
        Console.WriteLine(
            $"Frame diagnostics Simple DDGI B3 refinement: requested/enabled={refinement.Requested}, " +
            $"bricks requested/admitted/ready/baseFallback={refinement.RequestedBrickCount}/{refinement.AdmittedBrickCount}/{refinement.ReceiverReadyBrickCount}/{refinement.BaseFallbackBrickCount}, " +
            $"receiverBlend={refinement.ReceiverBlendWeight:0.###}, " +
            $"probes/evictions/topologyChanged={refinement.AllocatedProbeCount}/{refinement.EvictionCount}/{refinement.TopologyChangedThisFrame}, " +
            $"emissive examined/eligible/admitted/rejectLarge/rejectDim={emissiveDemand.ExaminedSourceCount}/{emissiveDemand.EligibleSourceCount}/{emissiveDemand.AdmittedDemandCount}/{emissiveDemand.RejectedLargeSourceCount}/{emissiveDemand.RejectedDimSourceCount}, " +
            $"status='{refinement.AdmissionStatus}'.");
        SimpleDdgiNearVisibilityDiagnostics nearVisibility =
            diagnostics.SimpleDdgiNearVisibility;
        SimpleDdgiNearVisibilityGpuCounters nearVisibilityEvidence =
            nearVisibility.Evidence;
        Console.WriteLine(
            $"Frame diagnostics Simple DDGI B4 near visibility: requested/active={nearVisibility.Requested}/{nearVisibility.Active}, " +
            $"eligibleVolumes={nearVisibility.EligibleVolumeCount}, " +
            $"bytes public/private/allocated/required/budget={nearVisibility.PublicBytes}/{nearVisibility.PrivateBytes}/{nearVisibility.AllocatedBytes}/{nearVisibility.RequiredBytes}/{nearVisibility.BudgetBytes}, " +
            $"evidence valid/frame={nearVisibilityEvidence.ReadbackValid}/{nearVisibilityEvidence.FrameSerial}, " +
            $"clusters coherent/rejected={nearVisibilityEvidence.CoherentClusterTexelCount}/{nearVisibilityEvidence.RejectedClusterTexelCount}, " +
            $"taps lowConfidence/invalidDepth/noDiscrepancy/receiverInFront={nearVisibilityEvidence.InsufficientConfidenceTapCount}/{nearVisibilityEvidence.InvalidDepthTapCount}/{nearVisibilityEvidence.NoMomentDiscrepancyTapCount}/{nearVisibilityEvidence.ReceiverInFrontTapCount}, " +
            $"evaluations applied/total={nearVisibilityEvidence.AppliedEvaluationCount}/{nearVisibilityEvidence.EvaluationCount}, " +
            $"clamp avg/max={nearVisibilityEvidence.AverageClamp:0.####}/{nearVisibilityEvidence.MaximumClamp:0.####}, " +
            $"status='{nearVisibility.Status}'.");
        GiRoadmapExperimentDiagnostics experiments =
            diagnostics.GiRoadmapExperiments;
        Console.WriteLine(
            $"Frame diagnostics GI gated roadmap: " +
            $"B1={FormatExperimentMode(experiments.Modes.ReceiverFeedback)}, " +
            $"B5={FormatExperiment(experiments.DirectionalFog)}, " +
            $"C1={FormatExperimentMode(experiments.Modes.OpacityMicromap)}, " +
            $"C2={FormatExperiment(experiments.RayTracingInvocationReorder)}, " +
            $"C3={FormatExperimentMode(experiments.Modes.DirectionalGuiding)}, " +
            $"C4={FormatExperimentMode(experiments.Modes.Caustic)}, " +
            $"C5={FormatExperimentMode(experiments.Modes.NearFieldResidual)}, " +
            $"allocated={experiments.AllocatedBytes}.");
        SimpleDdgiReceiverFeedbackDiagnostics receiverFeedback =
            experiments.ReceiverFeedbackRuntime;
        Console.WriteLine(
            $"Frame diagnostics B1 exact receiver feedback: state={receiverFeedback.State}, " +
            $"authoritative={receiverFeedback.HasAuthoritativePublication}, " +
            $"generation/frame={receiverFeedback.Publication.FeedbackGeneration}/" +
            $"{receiverFeedback.Publication.FrameSerial}, " +
            $"records append/dropped/capacity={receiverFeedback.Publication.AppendCount}/" +
            $"{receiverFeedback.Publication.DroppedCount}/" +
            $"{receiverFeedback.Publication.RecordCapacity}, " +
            $"partials probe/fallback={receiverFeedback.Publication.ProbePartialCount}/" +
            $"{receiverFeedback.Publication.FallbackPartialCount}, " +
            $"summaries probe/fallback={receiverFeedback.Publication.SummaryCount}/" +
            $"{receiverFeedback.Publication.FallbackSummaryCount}, " +
            $"invalid/overflowMask={receiverFeedback.Publication.InvalidRecordCount}/" +
            $"0x{receiverFeedback.Publication.ProducerOverflowMask:X8}, " +
            $"utilization={receiverFeedback.Publication.AppendUtilization:P2}, " +
            $"us reset/capture/rawRadix/partialRadix/reduce=" +
            $"{receiverFeedback.Timings.ResetMicroseconds}/" +
            $"{receiverFeedback.Timings.CaptureMicroseconds}/" +
            $"{receiverFeedback.Timings.RawRadixMicroseconds}/" +
            $"{receiverFeedback.Timings.PartialBuildAndRadixMicroseconds}/" +
            $"{receiverFeedback.Timings.ReduceAndFinalizeMicroseconds}, " +
            $"bytes allocated/peak/retired={receiverFeedback.Memory.AllocatedBytes}/" +
            $"{receiverFeedback.Memory.PeakLiveBytes}/" +
            $"{receiverFeedback.Memory.RetiredButLiveBytes}, " +
            $"status='{receiverFeedback.Reason}'.");
        SimpleDdgiNearFieldResidualDiagnostics nearFieldResidual =
            diagnostics.SimpleDdgiNearFieldResidual;
        Console.WriteLine(
            $"Frame diagnostics C5 telemetry: state={nearFieldResidual.Readback.State}, " +
            $"authoritative={nearFieldResidual.IsAuthoritativeReadback}, " +
            $"frame/age={nearFieldResidual.Readback.CompletedFrameSerial}/" +
            $"{nearFieldResidual.Readback.AgeFrames}, " +
            $"bytes requested/admitted/allocated/peak/retired=" +
            $"{nearFieldResidual.Memory.RequestedBytes}/" +
            $"{nearFieldResidual.Memory.AdmittedBytes}/" +
            $"{nearFieldResidual.Memory.AllocatedBytes}/" +
            $"{nearFieldResidual.Memory.PeakAllocatedBytes}/" +
            $"{nearFieldResidual.Memory.RetiredBytes}, " +
            $"us source/trace/temporal/filter/composite=" +
            $"{nearFieldResidual.Timings.SourceMicroseconds}/" +
            $"{nearFieldResidual.Timings.RawTraceMicroseconds}/" +
            $"{nearFieldResidual.Timings.TemporalMicroseconds}/" +
            $"{nearFieldResidual.Timings.FilterMicroseconds}/" +
            $"{nearFieldResidual.Timings.CompositeMicroseconds}, " +
            $"trace hit/miss/edge/nonfinite={nearFieldResidual.Trace.RayHitCount}/" +
            $"{nearFieldResidual.Trace.RayMissCount}/" +
            $"{nearFieldResidual.Trace.EdgeRejectedCount}/" +
            $"{nearFieldResidual.Trace.NonFiniteRejectedCount}, " +
            $"history accepted/rejected={nearFieldResidual.History.AcceptedHistoryCount}/" +
            $"{nearFieldResidual.History.RejectedHistoryCount}, " +
            $"tiles compacted/candidate/overflow={nearFieldResidual.Tiles.CompactedTileCount}/" +
            $"{nearFieldResidual.Tiles.CandidateTileCount}/" +
            $"{nearFieldResidual.Tiles.OverflowTileCount}, " +
            $"status='{nearFieldResidual.Readback.Reason}'.");
    }

    private void PrintDebugOverlayTransition(RendererDiagnostics diagnostics)
    {
        DebugOverlayFrameStatus status = diagnostics.DebugOverlayStatus;
        bool changed = !_hasLastDebugOverlayStatus ||
            status.Mode != _lastDebugOverlayMode ||
            status.Availability != _lastDebugOverlayAvailability ||
            diagnostics.DebugDdgiGpuCountersValid !=
                _lastDebugDdgiGpuCountersValid ||
            !string.Equals(status.Reason, _lastDebugOverlayReason, StringComparison.Ordinal);
        if (!changed)
            return;

        _hasLastDebugOverlayStatus = true;
        _lastDebugOverlayMode = status.Mode;
        _lastDebugOverlayAvailability = status.Availability;
        _lastDebugOverlayReason = status.Reason;
        _lastDebugDdgiGpuCountersValid = diagnostics.DebugDdgiGpuCountersValid;

        string displayName = DebugOverlayCatalog.TryGet(status.Mode, out DebugOverlayDescriptor descriptor)
            ? descriptor.DisplayName
            : $"unknown ({(uint)status.Mode})";
        string availability = status.Availability.ToString().ToLowerInvariant();
        string reason = string.IsNullOrWhiteSpace(status.Reason)
            ? string.Empty
            : $" ({status.Reason})";
        Console.WriteLine(
            $"Debug overlay: {displayName} — {availability}{reason}; " +
            $"items={status.PrimaryItemCount}/{status.SecondaryItemCount}, " +
            $"dropped={status.DroppedItemCount}, " +
            $"cpuRecord={diagnostics.CpuDebugOverlayRecordMicroseconds}us, " +
            $"gpu(ddgi/tile)={diagnostics.GpuDebugDdgiProbeMicroseconds}/" +
            $"{diagnostics.GpuDebugLightTileMicroseconds}us");
        if (descriptor != null && !string.IsNullOrWhiteSpace(descriptor.Legend))
            Console.WriteLine($"Debug overlay legend: {descriptor.Legend}.");
        if (descriptor?.RendererKind == DebugOverlayRendererKind.DdgiProbe)
        {
            Console.WriteLine(diagnostics.DebugDdgiGpuCountersValid != 0
                ? $"Debug overlay GPU counters (fence-complete): " +
                  $"drawn/filtered/nonresident/stale/stateUnavailable/invalidTx=" +
                  $"{diagnostics.DebugDdgiProbeMarkersDrawn}/" +
                  $"{diagnostics.DebugDdgiProbeMarkersFiltered}/" +
                  $"{diagnostics.DebugDdgiNonresidentMarkers}/" +
                  $"{diagnostics.DebugDdgiStaleMappings}/" +
                  $"{diagnostics.DebugDdgiStateUnavailableMarkers}/" +
                  $"{diagnostics.DebugDdgiInvalidTransactions}."
                : "Debug overlay GPU counters: awaiting a matching fence-complete frame.");

            if (status.Mode == DebugOverlayMode.DdgiUpdateReasons &&
                diagnostics.DebugDdgiGpuCountersValid != 0)
            {
                Console.WriteLine(
                    $"Debug overlay update reasons: " +
                    FormatDebugDdgiUpdateReasons(
                        diagnostics.DebugDdgiUpdateReasonCounts));
            }
        }
    }

    private static string FormatDebugDdgiUpdateReasons(
        DebugDdgiUpdateReasonCounts counts)
    {
        var values = new List<string>(17);
        Add("fresh", counts.FreshCount);
        Add("scroll", counts.ScrollExposedCount);
        Add("regional", counts.RegionalDirtyCount);
        Add("global", counts.GlobalDirtyCount);
        Add("visible", counts.VisibleCount);
        Add("retry", counts.RetryCount);
        Add("relocation", counts.RelocationRetryCount);
        Add("sourceInvalid", counts.SourceCacheInvalidCount);
        Add("routine", counts.RoutineDueCount);
        Add("convergence", counts.ConvergencePendingCount);
        Add("inactive", counts.InactiveRetryCount);
        Add("topology", counts.TopologyCount);
        Add("pageCohort", counts.VisiblePageCohortCount);
        Add("relight", counts.RadiometricRelightCount);
        Add("segment", counts.SegmentSelectiveCount);
        Add("residual", counts.ResidualPropagationCount);
        Add("multi", counts.MultiReasonCount);
        return values.Count == 0 ? "none" : string.Join(", ", values);

        void Add(string name, uint value)
        {
            if (value != 0)
                values.Add($"{name}={value}");
        }
    }

    private static string FormatExperiment(GiExperimentAdmission admission) =>
        $"{admission.Stage}/{admission.Requested}/{admission.Active}/'{admission.Status}'";

    private static string FormatExperimentMode<TMode>(
        GiExperimentModeState<TMode> mode)
        where TMode : struct, Enum =>
        $"{mode.RequestedMode}->{mode.EffectiveMode}/" +
        $"{mode.FallbackReason}/'{mode.FallbackDetail}'";


    private static void PrintDdgiInvestigationDiagnostics(RendererDiagnostics diagnostics)
    {
        Console.WriteLine(
            $"Frame diagnostics DDGI investigation: " +
            $"simpleEvents recenter/clear/preserve/framesSinceClear/framesSinceRecenter={diagnostics.SimpleDdgiRecenterCount}/{diagnostics.SimpleDdgiAtlasClearCount}/{diagnostics.SimpleDdgiAtlasPreserveOnRecenterCount}/{diagnostics.SimpleDdgiFramesSinceLastClear}/{diagnostics.SimpleDdgiFramesSinceLastRecenter}, " +
            $"simpleForward fresh/zero/nonzero/avgIrrLum/avgVisibility/lowVisibility={diagnostics.SimpleDdgiFreshAtlasForwardSampleCount}/{diagnostics.SimpleDdgiZeroIrradianceSampleCount}/{diagnostics.SimpleDdgiNonzeroIrradianceSampleCount}/{diagnostics.SimpleDdgiAverageSampledIrradianceLuminance:F5}/{diagnostics.SimpleDdgiAverageVisibility:F3}/{diagnostics.SimpleDdgiLowVisibilitySampleCount}, " +
            $"simpleGather primary/second/rate={diagnostics.SimpleDdgiGatherSampleCount}/{diagnostics.SimpleDdgiSecondVolumeGatherCount}/{FormatSimpleDdgiSecondVolumeGatherRate(diagnostics)}, " +
            $"simpleReject primary=[{FormatSimpleDdgiRejections(diagnostics.SimpleDdgiGatherPrimaryRejectionCounts)}] fallback=[{FormatSimpleDdgiRejections(diagnostics.SimpleDdgiGatherFallbackRejectionCounts)}] recovery=[{FormatSimpleDdgiRejections(diagnostics.SimpleDdgiGatherRecoveryRejectionCounts)}] allFailed={diagnostics.SimpleDdgiGatherPrimaryAllFailedCount}/{diagnostics.SimpleDdgiGatherFallbackAllFailedCount}/{diagnostics.SimpleDdgiGatherRecoveryAllFailedCount}, " +
            $"simpleLifecycle target/oldestUnsupported/overTarget/repairUpdates/maxFresh/maxScroll/maxRelocate/maxUnpublished/findings={diagnostics.SimpleDdgiProbeLifecycleLatencyTargetFrames}/{diagnostics.SimpleDdgiOldestVisibleUnsupportedProbeAge}/{diagnostics.SimpleDdgiVisibleUnsupportedProbeCountAboveLatencyTarget}/{diagnostics.SimpleDdgiVisibleZeroSupportRepairUpdateCount}/{diagnostics.SimpleDdgiMaximumFreshProbeAge}/{diagnostics.SimpleDdgiMaximumScrollExposedProbeAge}/{diagnostics.SimpleDdgiMaximumRelocationPendingProbeAge}/{diagnostics.SimpleDdgiMaximumUnpublishedProbeAge}/{diagnostics.SimpleDdgiProbeLifecycleBoundExceededCount}, " +
            $"updateFrames full/partial/updatedFraction/start/end/skipped={diagnostics.DdgiFullRefreshFrameCount}/{diagnostics.DdgiPartialRefreshFrameCount}/{diagnostics.DdgiUpdatedProbeFraction:F3}/{diagnostics.DdgiProbeUpdateStartIndex}/{diagnostics.DdgiProbeUpdateEndIndex}/{diagnostics.DdgiSkippedProbeCount}, " +
            $"probeAge p50/p95/max={diagnostics.DdgiFramesSinceProbeUpdatedP50:F1}/{diagnostics.DdgiFramesSinceProbeUpdatedP95:F1}/{diagnostics.DdgiFramesSinceProbeUpdatedMax:F1}, " +
            $"invalidated={diagnostics.DdgiNewlyInvalidatedProbeCount}, reasons recenter/dirty/age/visibility/full={diagnostics.DdgiRefreshReasonRecenterProbeCount}/{diagnostics.DdgiRefreshReasonDirtyProbeCount}/{diagnostics.DdgiRefreshReasonAgeProbeCount}/{diagnostics.DdgiRefreshReasonVisibilityProbeCount}/{diagnostics.DdgiRefreshReasonFullRefreshProbeCount}, " +
            $"forward simple/legacy/zeroFinal/zeroDdgiIbl/zeroDdgiNoIbl/outOfGrid/clamped/nonfinite={diagnostics.DdgiForwardSimplePathSampleCount}/{diagnostics.DdgiForwardLegacyPathSampleCount}/{diagnostics.DdgiForwardZeroFinalIndirectCount}/{diagnostics.DdgiForwardZeroDdgiButNonzeroIblCount}/{diagnostics.DdgiForwardZeroDdgiAndZeroIblCount}/{diagnostics.DdgiForwardOutOfGridSampleCount}/{diagnostics.DdgiForwardClampedProbeSampleCount}/{diagnostics.DdgiForwardNanOrInfSampleCount}, " +
            $"atlas zeroIrrTexel/zeroVisMoment/writeProbe/writeTexel/zeroRayWeight/nonzeroIrr/prevAtlas/hystZero={diagnostics.DdgiIrradianceAtlasZeroTexelSampleCount}/{diagnostics.DdgiVisibilityAtlasZeroMomentSampleCount}/{diagnostics.DdgiAtlasWriteProbeCount}/{diagnostics.DdgiAtlasWriteTexelCount}/{diagnostics.DdgiBlendZeroRayWeightProbeCount}/{diagnostics.DdgiBlendNonzeroIrradianceProbeCount}/{diagnostics.DdgiBlendPreviousAtlasUsedCount}/{diagnostics.DdgiBlendHysteresisZeroFrameCount}, " +
            $"trace hit/miss/zeroRadiance/direct/emissive/farHit/farMiss/tlasUnavailable={diagnostics.DdgiSimpleTraceHitCount}/{diagnostics.DdgiSimpleTraceMissCount}/{diagnostics.DdgiSimpleTraceZeroRadianceHitCount}/{diagnostics.DdgiSimpleTraceDirectLightHitCount}/{diagnostics.DdgiSimpleTraceEmissiveHitCount}/{diagnostics.DdgiSimpleTraceFarFieldHitCount}/{diagnostics.DdgiSimpleTraceFarFieldMissCount}/{diagnostics.DdgiSimpleTraceTlasUnavailableFrameCount}, " +
            $"simpleFar skySamples/avg/farSun/occluded/roughSpec/nonzero={diagnostics.SimpleDdgiSkyVisibilitySampleCount}/{diagnostics.SimpleDdgiAverageSkyVisibility:F3}/{diagnostics.FarFieldSunShadowSampleCount}/{diagnostics.FarFieldSunShadowOccludedCount}/{diagnostics.SimpleDdgiRoughSpecularSampleCount}/{diagnostics.SimpleDdgiRoughSpecularNonzeroCount}, " +
            $"farSteps<=4/<=8/<=16/<=32/>32={diagnostics.DdgiSimpleTraceFarFieldStepBucket0Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket1Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket2Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket3Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket4Count}, " +
            $"black suspect/afterRecenter/afterClear/duringFresh={diagnostics.DdgiBlackFrameSuspect}/{diagnostics.DdgiBlackFrameAfterRecenter}/{diagnostics.DdgiBlackFrameAfterAtlasClear}/{diagnostics.DdgiBlackFrameDuringFreshAtlas}.");
    }

    private static void PrintDdgiUpdateDiagnostics(RendererDiagnostics diagnostics)
    {
        Console.WriteLine(
            $"Frame diagnostics DDGI update: traceDispatchGroups={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceDispatchGroupCount)}, " +
            $"traceProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceProbeCount)}, " +
            $"earlyOutDisabled={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutDisabledCount)}, earlyOutBeyondRequestCount={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutBeyondRequestCount)}, " +
            $"earlyOutResolveBounds={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutResolveBoundsCount)}, earlyOutResolveProbeRange={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutResolveProbeRangeCount)}, " +
            $"earlyOutResolveClipmapCell={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutResolveClipmapCellCount)}, earlyOutResolveClipmapRing={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceEarlyOutResolveClipmapRingCount)}, " +
            $"ringMismatchCorrected={FormatDdgiCounterReadback(diagnostics, diagnostics.DdgiTraceRingMismatchCorrectedCount)}, " +
            $"ringMismatchSample='{FormatDdgiRingMismatchSample(diagnostics)}', " +
            $"traceRayCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceRayCount)}, " +
            $"blendProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiBlendProbeCount)}, relocateClassifyProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiRelocateClassifyProbeCount)}, " +
            $"publishProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiPublishProbeCount)}.");
    }

    private static void PrintDdgiTriageDiagnostics(RendererDiagnostics diagnostics)
    {
        string state = ClassifyDdgiState(diagnostics);
        (string severity, string reason, string next) = DescribeDdgiTriageState(state);
        Console.WriteLine(
            $"DDGI TRIAGE: state={state} severity={severity} reason='{reason}' next='{next}'");
        Console.WriteLine(
            $"DDGI TRIAGE VALUES: volumes={diagnostics.DdgiProbeVolumeCount} probes={diagnostics.DdgiActiveProbeCount}/{diagnostics.DdgiProbeCount} " +
            $"updated={diagnostics.DdgiProbesUpdated} cache={diagnostics.DdgiCacheGeneration}:{diagnostics.DdgiCacheWarmupState} " +
            $"fast={diagnostics.DdgiFastGatherAttemptCount}/{diagnostics.DdgiFastGatherAcceptedCount} " +
            $"shaderFallback={diagnostics.DdgiShaderGatherFallbackAttemptCount}/{diagnostics.DdgiShaderGatherFallbackAcceptedCount}/{diagnostics.DdgiShaderGatherFallbackEmptyCount} " +
            $"samples={diagnostics.DdgiForwardEstimateSampleCount}/{diagnostics.DdgiProbeQualitySampleCount} " +
            $"trace={diagnostics.DdgiTraceEnergySampleCount}/{diagnostics.DdgiTraceEnergyHitCount}/{diagnostics.DdgiTraceEnergyMissCount}/{diagnostics.DdgiTraceEnergyRayLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyDirectLuminanceAverage:F5}/{diagnostics.DdgiTraceEnergyDirectNoShadowLuminanceAverage:F5} " +
            $"shadow={diagnostics.DdgiShadowVisibilityRayCount}/{diagnostics.DdgiShadowVisibilityOccludedCount}/{diagnostics.DdgiShadowVisibilityNearHitCount}/{diagnostics.DdgiShadowVisibilityCommittedHitDistanceAverage:F3} " +
            $"lowDelivered={diagnostics.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount} " +
            $"forwardEnergy sampledIrr/ddgiDiffuse/hybrid/fallbackWeight={diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F5}/{diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F5}/{diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F5}/{diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight:F3} " +
            $"blend={diagnostics.DdgiBlendEnergySampleCount}/{diagnostics.DdgiBlendEnergyIrradianceLuminanceAverage:F5}/{diagnostics.DdgiBlendEnergyConfidenceAverage:F3}/{diagnostics.DdgiBlendEnergyNonFiniteIrradianceCount}/{diagnostics.DdgiBlendEnergyFireflySuppressedCount} " +
            $"support/data/effective={diagnostics.DdgiAverageSupportCoverageEstimate:F3}/{diagnostics.DdgiAverageDataConfidenceEstimate:F3}/{diagnostics.DdgiAverageEffectiveContributionEstimate:F3} " +
            $"alpha/q={diagnostics.DdgiProbeIrradianceAlphaAverage:F3}/{diagnostics.DdgiProbeQualityXAverage:F3}/{diagnostics.DdgiProbeQualityYAverage:F3}/{diagnostics.DdgiProbeQualityZAverage:F3} " +
            $"inactive={diagnostics.DdgiClassifiedInactiveProbeCountEstimate}");
    }

    private static string ClassifyDdgiState(RendererDiagnostics d)
    {
        if (d.GlobalIlluminationEnabled == 0 || d.GlobalIlluminationMode == GlobalIlluminationMode.Disabled)
            return "Disabled";

        if (d.GlobalIlluminationMode == GlobalIlluminationMode.Ddgi &&
            d.GlobalIlluminationRayQueryActive == 0)
            return "RayQueryInactive";

        if (d.DdgiProbeVolumeCount <= 0 || d.DdgiActiveProbeCount <= 0)
            return "NoVolumesOrProbes";

        if (d.DdgiUpdateExecuted == 0 || d.DdgiProbesUpdated <= 0)
            return "NoProbeUpdates";

        bool boundedSimpleGather =
            d.SimpleDdgiGatherSampleCount > 0 &&
            d.SimpleDdgiGatherFallbackAllFailedCount == 0;

        bool fastGatherMeasured =
            d.DdgiFastGatherAttemptCount > 0 ||
            d.DdgiShaderGatherFallbackAttemptCount > 0;
        bool fullForwardEstimateMeasured = d.DdgiForwardEstimateSampleCount > 0;
        bool probeQualityMeasured = d.DdgiProbeQualitySampleCount > 0;

        bool noForwardContribution =
            d.DdgiAverageSupportCoverageEstimate <= 0.0001f &&
            d.DdgiAverageDataConfidenceEstimate <= 0.0001f &&
            d.DdgiAverageEffectiveContributionEstimate <= 0.0001f &&
            d.DdgiForwardEstimateRawDiffuseLuminance <= 0.0001f &&
            d.DdgiForwardEstimateFinalDiffuseLuminance <= 0.0001f;

        if (d.DdgiFastGatherAcceptedCount > 0 && !fullForwardEstimateMeasured)
            return "FastGatherAcceptedEstimateUnmeasured";

        if (boundedSimpleGather && noForwardContribution && !fastGatherMeasured)
            return "FastGatherUnmeasured";

        if (boundedSimpleGather && noForwardContribution && fullForwardEstimateMeasured)
            return "FastGatherBlackHole";

        if (d.DdgiShaderGatherFallbackAttemptCount > 0 &&
            d.DdgiShaderGatherFallbackAcceptedCount == 0 &&
            noForwardContribution)
            return "ShaderFallbackEmpty";

        bool noProbeQuality =
            d.DdgiProbeIrradianceAlphaAverage <= 0.0001f &&
            d.DdgiProbeQualityXAverage <= 0.0001f &&
            d.DdgiProbeQualityYAverage <= 0.0001f &&
            d.DdgiProbeQualityZAverage <= 0.0001f;

        if (noProbeQuality && probeQualityMeasured && d.DdgiCacheGeneration > 0)
            return "ProbeQualityZero";

        if (d.DdgiClassifiedInactiveProbeCountEstimate > 0 &&
            d.DdgiAverageSupportCoverageEstimate <= 0.0001f)
            return "ClassificationOrActiveStateSuppressed";

        if (d.DdgiAverageSpatialCoverageEstimate > 0.0f &&
            d.DdgiAverageSupportCoverageEstimate <= 0.0001f)
            return "SpatialCoverageWithoutSupport";

        if (d.DdgiAverageEffectiveContributionEstimate > 0.0f ||
            d.DdgiForwardEstimateFinalDiffuseLuminance > 0.0f)
            return "Contributing";

        return "UnknownZeroContribution";
    }

    private static (string Severity, string Reason, string Next) DescribeDdgiTriageState(string state)
    {
        return state switch
        {
            "Disabled" => ("Gray", "GI is disabled or not in an active GI mode", "enable DDGI render settings"),
            "RayQueryInactive" => ("Red", "DDGI mode is selected but ray queries are inactive", "device feature and GI ray-query setup"),
            "NoVolumesOrProbes" => ("Red", "DDGI has no active volumes or probes", "scene DDGI volume creation"),
            "NoProbeUpdates" => ("Red", "DDGI probes exist but no probe update executed", "DDGI scheduler and update skip reason"),
            "FastGatherUnmeasured" => ("Amber", "clipmap tiles are selected but fast gather counters were not collected", "enable gather debug or forward estimate counters"),
            "FastGatherAcceptedEstimateUnmeasured" => ("Amber", "fast gather accepts samples but full forward estimate counters were not collected", "enable forward estimate counters for contribution averages"),
            "FastGatherBlackHole" => ("Red", "clipmap tiles selected but forward support/data/effective all zero and fallback unused", "shader fast-gather acceptance fallback"),
            "ShaderFallbackEmpty" => ("Red", "shader fallback ran but found no usable DDGI sample", "probe atlas quality and volume sample addressing"),
            "ProbeQualityZero" => ("Red", "probe cache exists but irradiance alpha and quality averages are zero", "probe publish atlas quality data"),
            "ClassificationOrActiveStateSuppressed" => ("Amber", "inactive probe classification is present while support is zero", "probe classification and active-state upload"),
            "SpatialCoverageWithoutSupport" => ("Amber", "pixels have spatial DDGI coverage but no accepted support", "support acceptance thresholds"),
            "Contributing" => ("Green", "DDGI has measurable effective or final forward contribution", "compare visual output against expected lighting"),
            _ => ("Amber", "DDGI reached zero contribution without a more specific classifier", "inspect GI/DDGI diagnostics below")
        };
    }

    private static string FormatDdgiVolumeDesignSummary(RendererDiagnostics diagnostics)
    {
        if (diagnostics.DdgiVolumes.Count == 0)
            return "none";

        int localCount = 0;
        int warningCount = 0;
        float minSpacing = float.PositiveInfinity;
        float maxBudgetFraction = 0.0f;
        string dominantPreset = string.Empty;
        for (int i = 0; i < diagnostics.DdgiVolumes.Count; i++)
        {
            DdgiVolumeDiagnosticsEntry volume = diagnostics.DdgiVolumes[i];
            if (volume.Kind == SimpleDdgiVolumeKind.Authored)
                localCount++;
            if (!string.IsNullOrEmpty(volume.BudgetWarning))
                warningCount++;
            if (volume.MinProbeSpacing > 0.0f)
                minSpacing = MathF.Min(minSpacing, volume.MinProbeSpacing);
            if (volume.ActiveProbeBudgetFraction > maxBudgetFraction)
            {
                maxBudgetFraction = volume.ActiveProbeBudgetFraction;
                dominantPreset = volume.DesignPreset;
            }
        }

        if (!float.IsFinite(minSpacing))
            minSpacing = 0.0f;

        return $"locals={localCount},minSpacing={minSpacing:F2},maxBudget={maxBudgetFraction:P0}:{dominantPreset},warnings={warningCount}";
    }

    private static void PrintDdgiRingDiagnostics(RendererDiagnostics diagnostics)
    {
        Console.WriteLine($"Frame diagnostics DDGI rings: {FormatDdgiRingDiagnostics(diagnostics)}.");
    }

    private static string FormatDdgiRingDiagnostics(RendererDiagnostics diagnostics)
    {
        var result = new StringBuilder();
        for (int ringIndex = 0; ringIndex < 3; ringIndex++)
        {
            DdgiVolumeDiagnosticsEntry? ring = null;
            for (int i = 0; i < diagnostics.DdgiVolumes.Count; i++)
            {
                DdgiVolumeDiagnosticsEntry candidate = diagnostics.DdgiVolumes[i];
                if (candidate.CascadeIndex == ringIndex &&
                    string.Equals(candidate.DesignPreset, "simple-ring", StringComparison.Ordinal))
                {
                    ring = candidate;
                    break;
                }
            }

            if (ring == null)
                continue;

            if (result.Length > 0)
                result.Append("; ");

            int gridX = GridCountFromSize(ring.SizeX, ring.ProbeSpacingX);
            int gridY = GridCountFromSize(ring.SizeY, ring.ProbeSpacingY);
            int gridZ = GridCountFromSize(ring.SizeZ, ring.ProbeSpacingZ);
            float horizontalReach = Math.Max(ring.SizeX, ring.SizeZ) * 0.5f;
            float verticalReach = ring.SizeY * 0.5f;
            result.Append("ring")
                .Append(ringIndex)
                .Append(" grid=")
                .Append(gridX)
                .Append('x')
                .Append(gridY)
                .Append('x')
                .Append(gridZ)
                .Append(" spacing=")
                .Append(ring.ProbeSpacingX.ToString("F2", CultureInfo.InvariantCulture))
                .Append(" reach=±")
                .Append(horizontalReach.ToString("F1", CultureInfo.InvariantCulture))
                .Append("/±")
                .Append(verticalReach.ToString("F1", CultureInfo.InvariantCulture))
                .Append("m ageP95=")
                .Append(ring.EstimatedAgeP95Frames.ToString("F0", CultureInfo.InvariantCulture));
        }

        return result.Length == 0 ? "none" : result.ToString();
    }

    private static int GridCountFromSize(float size, float spacing)
    {
        if (spacing <= 0.0f || !float.IsFinite(spacing))
            return 0;
        return Math.Max(2, (int)MathF.Round(size / spacing) + 1);
    }

    private static void PrintDdgiVolumeActivityDiagnostics(RendererDiagnostics diagnostics)
    {
        Console.WriteLine($"Frame diagnostics DDGI volumes: {FormatDdgiVolumeActivityDiagnostics(diagnostics)}.");
    }

    private static string FormatDdgiVolumeActivityDiagnostics(RendererDiagnostics diagnostics)
    {
        if (diagnostics.DdgiVolumes.Count == 0)
            return "none";

        var result = new StringBuilder();
        for (int i = 0; i < diagnostics.DdgiVolumes.Count; i++)
        {
            DdgiVolumeDiagnosticsEntry volume = diagnostics.DdgiVolumes[i];
            if (result.Length > 0)
                result.Append("; ");

            string label = string.Equals(volume.DesignPreset, "simple-ring", StringComparison.Ordinal)
                ? $"ring{volume.CascadeIndex}"
                : volume.Kind == SimpleDdgiVolumeKind.Authored
                    ? "authored"
                    : volume.Kind.ToString();
            result.Append('v')
                .Append(volume.VolumeIndex)
                .Append(' ')
                .Append(label)
                .Append(" active/inactive=")
                .Append(volume.ActiveProbeCount)
                .Append('/')
                .Append(volume.InactiveProbeCount)
                .Append(" state=")
                .Append(volume.ProbeStateCountsValid != 0 ? "measured" : "pending")
                .Append(" gather primary/sampled=");
            if (volume.GatherCountersReadbackValid != 0)
            {
                result.Append(volume.PrimaryGatherCount)
                    .Append('/')
                    .Append(volume.SampledGatherCount);
            }
            else
            {
                result.Append("pending");
            }

            result.Append(" energy=");
            if (volume.EnergyCountersReadbackValid != 0)
            {
                SimpleDdgiVolumeEnergyCounters energy = volume.EnergyCounters;
                result.Append("n/")
                    .Append(energy.EvidenceSampleCount)
                    .Append(" p95/p99/max=")
                    .Append(energy.IrradianceLuminanceP95.ToString("F4", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(energy.IrradianceLuminanceP99.ToString("F4", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(energy.IrradianceLuminanceMaximum.ToString("F4", CultureInfo.InvariantCulture))
                    .Append(" witness(vprobe/vpage/pprobe/ppage/source/m1/m2/coherent/age)=");
                AppendOptionalIndex(result, energy.MaximumVirtualProbeIndex);
                result.Append('/');
                AppendOptionalIndex(result, energy.MaximumVirtualPageIndex);
                result.Append('/');
                AppendOptionalIndex(result, energy.MaximumPhysicalProbeIndex);
                result.Append('/');
                AppendOptionalIndex(result, energy.MaximumPhysicalPageIndex);
                result.Append('/')
                    .Append(energy.MaximumSourceLightingGeneration)
                    .Append('/')
                    .Append(energy.MaximumVisibilityMomentMean.ToString("F3", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(energy.MaximumVisibilityMomentSecond.ToString("F3", CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(energy.MaximumWitnessCoherent)
                    .Append('/')
                    .Append(energy.EvidenceAgeFrames);
            }
            else
            {
                result.Append("pending");
            }
        }

        return result.ToString();
    }

    private static void AppendOptionalIndex(StringBuilder result, uint value)
    {
        if (value == uint.MaxValue)
            result.Append("none");
        else
            result.Append(value);
    }

    public void PrintMovementFrameDiagnostics(IRenderer renderer, FirstPersonCamera camera)
    {
        if (renderer is not VulkanRenderer vulkanRenderer)
            return;
        if (camera == null)
            return;

        RendererDiagnostics diagnostics = vulkanRenderer.LastDiagnostics;
        long now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp == 0)
        {
            _lastFrameTimestamp = now;
            CaptureCameraPose(camera);
            return;
        }

        double frameMs = Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalMilliseconds;
        _lastFrameTimestamp = now;

        bool cameraMoved = CameraMoved(camera);
        CaptureCameraPose(camera);

        double cpuDrawMs = diagnostics.CpuTotalDrawSceneMicroseconds / 1000.0;
        if (cameraMoved)
        {
            _movingFrameMs.Add(frameMs);
            _movingCpuDrawMs.Add(cpuDrawMs);
            _movingFrames++;
            _movingPayloadRebuilds += diagnostics.ScenePayloadRebuilt != 0 ? 1 : 0;
            _movingUploadedBytes += diagnostics.UploadedBytes;
        }
        else
        {
            _stillFrameMs.Add(frameMs);
            _stillCpuDrawMs.Add(cpuDrawMs);
            _stillFrames++;
            _stillPayloadRebuilds += diagnostics.ScenePayloadRebuilt != 0 ? 1 : 0;
            _stillUploadedBytes += diagnostics.UploadedBytes;
        }

        _pacingFrameCounter++;
        if (_pacingFrameCounter % 120 != 0)
            return;

        PerformanceSampleStats movingFrame = _movingFrameMs.GetStats();
        PerformanceSampleStats stillFrame = _stillFrameMs.GetStats();
        PerformanceSampleStats movingCpu = _movingCpuDrawMs.GetStats();
        PerformanceSampleStats stillCpu = _stillCpuDrawMs.GetStats();
        double movingUploadMiB = _movingFrames == 0 ? 0.0 : _movingUploadedBytes / (1024.0 * 1024.0 * _movingFrames);
        double stillUploadMiB = _stillFrames == 0 ? 0.0 : _stillUploadedBytes / (1024.0 * 1024.0 * _stillFrames);

        Console.WriteLine(
            $"Movement pacing: movingFrames={_movingFrames}, frameMs avg/p95/max={movingFrame.Average:F2}/{movingFrame.P95:F2}/{movingFrame.Max:F2}, " +
            $"cpuDrawMs avg/p95/max={movingCpu.Average:F2}/{movingCpu.P95:F2}/{movingCpu.Max:F2}, " +
            $"rebuilds={_movingPayloadRebuilds}, avgUploadMiB={movingUploadMiB:F2}; " +
            $"stillFrames={_stillFrames}, frameMs avg/p95/max={stillFrame.Average:F2}/{stillFrame.P95:F2}/{stillFrame.Max:F2}, " +
            $"cpuDrawMs avg/p95/max={stillCpu.Average:F2}/{stillCpu.P95:F2}/{stillCpu.Max:F2}, " +
            $"rebuilds={_stillPayloadRebuilds}, avgUploadMiB={stillUploadMiB:F2}; " +
            $"last sceneBuildUs={diagnostics.CpuSceneBuildMicroseconds}, meshCullUs={diagnostics.CpuMeshletCullMicroseconds}, " +
            $"uploadUs={diagnostics.CpuUploadMicroseconds}, primaryRecordUs={diagnostics.CpuPrimaryCommandRecordMicroseconds}, " +
            $"validation={diagnostics.ValidationMode}, stall={diagnostics.RuntimeWorstStallReason}:{diagnostics.RuntimeWorstStallMicroseconds}us.");

        _movingFrames = 0;
        _stillFrames = 0;
        _movingPayloadRebuilds = 0;
        _stillPayloadRebuilds = 0;
        _movingUploadedBytes = 0;
        _stillUploadedBytes = 0;
    }

    private bool CameraMoved(FirstPersonCamera camera)
    {
        if (!_hasLastCameraPose)
            return false;

        const float PositionEpsilonSquared = 0.0000001f;
        const float RotationEpsilon = 0.000001f;
        return (camera.Position - _lastCameraPosition).LengthSquared() > PositionEpsilonSquared ||
               MathF.Abs(camera.Yaw - _lastCameraYaw) > RotationEpsilon ||
               MathF.Abs(camera.Pitch - _lastCameraPitch) > RotationEpsilon;
    }

    private void CaptureCameraPose(FirstPersonCamera camera)
    {
        _lastCameraPosition = camera.Position;
        _lastCameraYaw = camera.Yaw;
        _lastCameraPitch = camera.Pitch;
        _hasLastCameraPose = true;
    }

    private static void AddDynamicTextureIndex(HashSet<int> indices, int textureIndex)
    {
        if (textureIndex >= BindlessIndex.FirstDynamicTextureIndex)
            indices.Add(textureIndex);
    }

    private static string FormatGpuMemoryBudget(RendererDiagnostics diagnostics)
    {
        const double BytesPerMiB = 1024.0 * 1024.0;
        string trackedUsage = (diagnostics.TrackedGpuMemoryBytes / BytesPerMiB).ToString("F1", CultureInfo.InvariantCulture);
        string trackedBudget = (diagnostics.GpuMemoryBudgetBytes / BytesPerMiB).ToString("F1", CultureInfo.InvariantCulture);
        RenderBudgetStatus trackedStatus = RenderBudgetEvaluator.Classify(
            diagnostics.TrackedGpuMemoryBytes,
            diagnostics.GpuMemoryBudgetBytes);

        if (diagnostics.GpuMemoryBudgetQueryAvailable == 0 || diagnostics.ActualGpuMemoryBudgetBytes == 0)
            return $"trackedMemory={diagnostics.GpuMemoryBudgetStatus}:{trackedUsage}/{trackedBudget}MiB";

        string heapUsage = (diagnostics.ActualGpuMemoryUsageBytes / BytesPerMiB).ToString("F1", CultureInfo.InvariantCulture);
        string heapBudget = (diagnostics.ActualGpuMemoryBudgetBytes / BytesPerMiB).ToString("F1", CultureInfo.InvariantCulture);
        return $"heapMemory={diagnostics.GpuMemoryBudgetStatus}:{heapUsage}/{heapBudget}MiB, " +
               $"trackedMemory={trackedStatus}:{trackedUsage}/{trackedBudget}MiB";
    }

    private static string FormatPendingUInt(int readbackValid, uint value)
    {
        return readbackValid != 0 ? value.ToString(CultureInfo.InvariantCulture) : "pending";
    }

    private static string FormatDdgiUpdateCount(RendererDiagnostics diagnostics, uint value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatDdgiCounterReadback(RendererDiagnostics diagnostics, uint value)
    {
        return FormatPendingUInt(diagnostics.DdgiForwardEstimateCountersReadbackValid, value);
    }

    private static string FormatSimpleDdgiSecondVolumeGatherRate(RendererDiagnostics diagnostics)
    {
        if (diagnostics.DdgiInvestigationCountersReadbackValid == 0)
            return "pending";
        if (diagnostics.SimpleDdgiGatherSampleCount == 0)
            return "n/a";
        return (diagnostics.SimpleDdgiSecondVolumeGatherCount /
            (double)diagnostics.SimpleDdgiGatherSampleCount).ToString("P1", CultureInfo.InvariantCulture);
    }

    private static string FormatSimpleDdgiRejections(IReadOnlyList<uint> counts)
    {
        if (counts.Count == 0)
            return "unavailable";

        string[] names =
        [
            "fresh",
            "scroll",
            "relocate",
            "inactiveFlag",
            "inactiveClass",
            "zeroWeight",
            "irrAtlas",
            "visAtlas",
            "outside"
        ];
        StringBuilder result = new();
        int count = Math.Min(names.Length, counts.Count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                result.Append(',');
            result.Append(names[i]).Append('=').Append(counts[i]);
        }
        return result.ToString();
    }

    private static string FormatDdgiRingMismatchSample(RendererDiagnostics diagnostics)
    {
        if (diagnostics.DdgiForwardEstimateCountersReadbackValid == 0)
            return "pending";
        return diagnostics.DdgiTraceRingMismatchSample.Length == 0
            ? "none"
            : diagnostics.DdgiTraceRingMismatchSample;
    }

}
