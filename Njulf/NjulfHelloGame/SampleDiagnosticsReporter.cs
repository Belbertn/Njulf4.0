using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

internal sealed class SampleDiagnosticsReporter
{
    private readonly MaterialManager _materialManager;
    private readonly IModelRenderUploadService? _uploadService;
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

    public SampleDiagnosticsReporter(
        MaterialManager materialManager,
        IModelRenderUploadService? uploadService)
    {
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _uploadService = uploadService;
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
            $"receiveShadows={diagnostics.TransparentReceiveShadows}, solidObjects={diagnostics.SolidObjectCount}, maskedObjects={diagnostics.MaskedObjectCount}, " +
            $"transparentObjects={diagnostics.TransparentObjectCount}, decalObjects={diagnostics.GeometryDecalObjectCount}, solidMeshlets={diagnostics.SolidMeshletCount}, " +
            $"maskedMeshlets={diagnostics.MaskedMeshletCount}, transparentMeshlets={diagnostics.TransparentMeshletCount}, decalMeshlets={diagnostics.GeometryDecalMeshletCount}, " +
            $"maskMaterials={diagnostics.MaskMaterialCount}, blendMaterials={diagnostics.BlendMaterialCount}, decalMaterials={diagnostics.GeometryDecalMaterialCount}, " +
            $"sortCandidates={diagnostics.TransparentSortCandidateCount}, sortUs={diagnostics.TransparentSortMicroseconds}, overflow={diagnostics.TransparentOverflowCount}, " +
            $"weightedOit={diagnostics.WeightedOitEnabled}, oitMiB={diagnostics.WeightedOitRenderTargetBytes / (1024.0 * 1024.0):F1}, " +
            $"decalDebug={diagnostics.DecalDebugView}, decalsEnabled={diagnostics.GeometryDecalsEnabled}, decalBias={diagnostics.GeometryDecalDepthBias:F5}, " +
            $"decalSlopeBias={diagnostics.GeometryDecalSlopeScaledDepthBias:F2}.");
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
        Console.WriteLine(
            $"Frame diagnostics budget: profile='{diagnostics.ActiveBudgetProfileName}', overall={diagnostics.BudgetOverallStatus}, " +
            $"cpu={diagnostics.CpuFrameBudgetStatus}, gpu={diagnostics.GpuFrameBudgetStatus}, memory={diagnostics.GpuMemoryBudgetStatus}, " +
            $"upload={diagnostics.UploadBudgetStatus}, trackedGpuMiB={diagnostics.TrackedGpuMemoryBytes / (1024.0 * 1024.0):F1}/{diagnostics.GpuMemoryBudgetBytes / (1024.0 * 1024.0):F1}, " +
            $"uploadMiB={diagnostics.UploadedBytes / (1024.0 * 1024.0):F2}/{diagnostics.UploadBudgetBytesPerFrame / (1024.0 * 1024.0):F2}, " +
            $"stagingMiB={diagnostics.StagingBytesUsedThisFrame / (1024.0 * 1024.0):F2}, peakStagingMiB={diagnostics.StagingBytesPeakThisSession / (1024.0 * 1024.0):F2}, " +
            $"stagingOverflow={diagnostics.StagingOverflowCountThisFrame}/{diagnostics.StagingOverflowCount}, retainedOverflow={diagnostics.StagingRetainedOverflowBufferCount}:{diagnostics.StagingRetainedOverflowBytes / (1024.0 * 1024.0):F2}MiB, " +
            $"worstStall={diagnostics.RuntimeWorstStallReason}:{diagnostics.RuntimeWorstStallMicroseconds}us.");
        Console.WriteLine(
            $"Frame diagnostics memory: meshMiB={diagnostics.MeshBufferAllocatedBytes / (1024.0 * 1024.0):F1} used={diagnostics.MeshBufferUsedBytes / (1024.0 * 1024.0):F1}, " +
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
            $"hizBreakdownUs={diagnostics.CpuHiZDepthTransitionMicroseconds}/{diagnostics.CpuHiZPyramidTransitionMicroseconds}/" +
            $"{diagnostics.CpuHiZDescriptorBindMicroseconds}/{diagnostics.CpuHiZPushDispatchMicroseconds}/{diagnostics.CpuHiZFinalBarrierMicroseconds}, " +
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
            $"forwardReceivers={diagnostics.ForwardShadowReceiverMeshletCount}, debug={diagnostics.ShadowDebugView}, " +
            $"normalBias={diagnostics.ShadowNormalBias:F4}, slopeBias={diagnostics.ShadowSlopeScaledDepthBias:F2}.");
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
            $"size={diagnostics.FogWidth}x{diagnostics.FogHeightPixels}, format={diagnostics.FogFormat}, debug={diagnostics.FogDebugView}.");
        Console.WriteLine(
            $"Frame diagnostics AO: enabled={diagnostics.AmbientOcclusionEnabled}, mode={diagnostics.AmbientOcclusionMode}, " +
            $"size={diagnostics.AmbientOcclusionWidth}x{diagnostics.AmbientOcclusionHeight}, format={diagnostics.AmbientOcclusionFormat}, " +
            $"scale={diagnostics.AmbientOcclusionResolutionScale:F2}, radius={diagnostics.AmbientOcclusionRadius:F2}, " +
            $"intensity={diagnostics.AmbientOcclusionIntensity:F2}, bias={diagnostics.AmbientOcclusionBias:F3}, " +
            $"samples={diagnostics.AmbientOcclusionSampleCount}, blur={diagnostics.AmbientOcclusionBlurRadius}, forwardSampling={diagnostics.AmbientOcclusionForwardSamplingMode}, " +
            $"forwardDepthAwareSamples={diagnostics.AmbientOcclusionForwardDepthAwareSamples}, " +
            $"debug={diagnostics.AmbientOcclusionDebugView}, aoRecordUs={diagnostics.CpuAmbientOcclusionRecordMicroseconds}, " +
            $"blurRecordUs={diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics GI: enabled={diagnostics.GlobalIlluminationEnabled}, mode={diagnostics.GlobalIlluminationMode}, debug={diagnostics.GlobalIlluminationDebugView}, " +
            $"rayQuerySupported={diagnostics.GlobalIlluminationRayQuerySupported}, rayQueryActive={diagnostics.GlobalIlluminationRayQueryActive}, " +
            $"ssgi={diagnostics.SsgiWidth}x{diagnostics.SsgiHeight}, scale={diagnostics.SsgiResolutionScale:F2}, rays={diagnostics.SsgiRayCount}, " +
            $"history={diagnostics.SsgiHistoryValid}, rejected={diagnostics.SsgiRejectedHistoryPixelCount}, " +
            $"ddgiVolumes={diagnostics.DdgiProbeVolumeCount}, ddgiProbes={diagnostics.DdgiActiveProbeCount}/{diagnostics.DdgiProbeCount}, " +
            $"ddgiUpdated={diagnostics.DdgiProbesUpdated}, ddgiRays={diagnostics.DdgiRaysPerProbe}, relocation={diagnostics.DdgiProbeRelocationCount}, " +
            $"updateExec={diagnostics.DdgiUpdateExecuted}:'{diagnostics.DdgiUpdateSkipReason}', publishExec={diagnostics.DdgiPublishExecuted}:'{diagnostics.DdgiPublishSkipReason}', " +
            $"cacheGeneration={diagnostics.DdgiCacheGeneration}, cacheFrame={diagnostics.DdgiLastUpdatedFrameSerial}, cacheWarmup={diagnostics.DdgiCacheWarmupState}, cacheLatencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}, " +
            $"gatherFallback={diagnostics.DdgiGatherFallbackTileCount}, forwardFallback={diagnostics.DdgiForwardGatherFallbackUsed}/{diagnostics.DdgiForwardGatherFallbackDisabled}, emptyTiles={diagnostics.DdgiForwardGatherTileEmpty}, " +
            $"gatherFractions local/clipmap/fallback={diagnostics.DdgiGatherSelectedLocalTileFraction:F3}/{diagnostics.DdgiGatherSelectedClipmapTileFraction:F3}/{diagnostics.DdgiGatherFallbackTileFraction:F3}, " +
            $"ddgiEstimate spatial/support/data/visibility/leak/effective/rawLum/finalLum/ownership/reloc/inactive=" +
            $"{diagnostics.DdgiAverageSpatialCoverageEstimate:F3}/{diagnostics.DdgiAverageSupportCoverageEstimate:F3}/{diagnostics.DdgiAverageDataConfidenceEstimate:F3}/" +
            $"{diagnostics.DdgiAverageVisibilityConfidenceEstimate:F3}/{diagnostics.DdgiAverageLeakAttenuationEstimate:F3}/{diagnostics.DdgiAverageEffectiveContributionEstimate:F3}/" +
            $"{diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F3}/{diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F3}/{diagnostics.DdgiAverageOwnershipConsumedEstimate:F3}/" +
            $"{diagnostics.DdgiAverageRelocationFractionEstimate:F3}/{diagnostics.DdgiClassifiedInactiveProbeCountEstimate}, " +
            $"ddgiSupportReject inactive/zeroAlpha/lowQuality={diagnostics.DdgiSupportRejectedInactiveCount}/{diagnostics.DdgiSupportRejectedZeroIrradianceAlphaCount}/{diagnostics.DdgiSupportRejectedLowQualityCount}, " +
            $"ddgiProbeConfidence alpha/qx/qy/qz={diagnostics.DdgiProbeIrradianceAlphaAverage:F3}/{diagnostics.DdgiProbeQualityXAverage:F3}/{diagnostics.DdgiProbeQualityYAverage:F3}/{diagnostics.DdgiProbeQualityZAverage:F3}, " +
            $"warmup={diagnostics.DdgiWarmupState}:{diagnostics.DdgiWarmedVisibleProbeFraction:F3}/{diagnostics.DdgiWarmedLocalProbeFraction:F3}/{diagnostics.DdgiWarmedCascade0ProbeFraction:F3}, " +
            $"volumeDesign={FormatDdgiVolumeDesignSummary(diagnostics)}, " +
            $"classification={diagnostics.DdgiProbeClassificationCount}, cpuSsgiUs={diagnostics.CpuSsgiRecordMicroseconds}, cpuDdgiUs={diagnostics.CpuDdgiRecordMicroseconds}, " +
            $"gpuSsgiUs={diagnostics.GpuSsgiTraceMicroseconds + diagnostics.GpuSsgiTemporalMicroseconds + diagnostics.GpuSsgiDenoiseMicroseconds}, " +
            $"gpuDdgiUs={diagnostics.GpuDdgiUpdateMicroseconds}, bytes={diagnostics.GlobalIlluminationRenderTargetBytes + diagnostics.DdgiTextureBytes + diagnostics.DdgiBufferBytes + diagnostics.AccelerationStructureBytes}.");
        Console.WriteLine(
            $"Frame diagnostics DDGI scheduler: mode={diagnostics.DdgiSchedulerMode}, considered={diagnostics.DdgiGpuSchedulerConsideredProbeCount}, " +
            $"requestBudget={diagnostics.DdgiScheduledRequestBudget}, primaryRayBudget={diagnostics.DdgiScheduledPrimaryRayBudget}, " +
            $"ddgiDispatchCapacity={diagnostics.DdgiGpuSchedulerPredictedRequestUpperBound}, ddgiActualRequests={FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, diagnostics.DdgiGpuSchedulerActualRequestCount)}, " +
            $"ddgiActualPrimaryRays={FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, diagnostics.DdgiGpuSchedulerActualPrimaryRayCount)}, " +
            $"scanFull={diagnostics.DdgiGpuSchedulerFullScan}, candidateOutput={diagnostics.DdgiGpuSchedulerCandidateOutputCapacity}, " +
            $"candidates={FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, diagnostics.DdgiGpuSchedulerCandidateCount)}, requests={FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, diagnostics.DdgiGpuSchedulerRequestCount)}, primaryRays={FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, diagnostics.DdgiGpuSchedulerPrimaryRayCount)}, " +
            $"priority={diagnostics.DdgiGpuSchedulerPriority0RequestCount}/{diagnostics.DdgiGpuSchedulerPriority1RequestCount}/{diagnostics.DdgiGpuSchedulerPriority2RequestCount}/{diagnostics.DdgiGpuSchedulerPriority3RequestCount}, " +
            $"rejected request/primary/duplicate/invalid={diagnostics.DdgiGpuSchedulerRequestBudgetRejectedCount}/{diagnostics.DdgiGpuSchedulerPrimaryRayBudgetRejectedCount}/{diagnostics.DdgiGpuSchedulerDuplicateRequestCount}/{diagnostics.DdgiGpuSchedulerInvalidProbeCount}, " +
            $"overflow candidate/perBucket/total={diagnostics.DdgiGpuSchedulerCandidateBufferOverflowCount}/{diagnostics.DdgiGpuSchedulerPerBucketOverflowCount}/{diagnostics.DdgiGpuSchedulerOverflowCount}, saturated request/ray={diagnostics.DdgiGpuSchedulerRequestBudgetSaturated}/{diagnostics.DdgiGpuSchedulerPrimaryRayBudgetSaturated}, " +
            $"reasons dirty/visible/safety/age/variance/confidence/stable={diagnostics.DdgiGpuSchedulerDirtyRegionCount}/{diagnostics.DdgiGpuSchedulerVisibleFrustumCandidateCount}/" +
            $"{diagnostics.DdgiGpuSchedulerSafetyShellCandidateCount}/{diagnostics.DdgiGpuSchedulerAgeRefreshCandidateCount}/{diagnostics.DdgiGpuSchedulerHighVarianceCandidateCount}/" +
            $"{diagnostics.DdgiGpuSchedulerLowConfidenceCandidateCount}/{diagnostics.DdgiGpuSchedulerStableSkippedCount}, readback={FormatReadbackStatus(diagnostics)}, " +
            $"validation={diagnostics.DdgiGpuSchedulerValidationStatus}:{diagnostics.DdgiGpuSchedulerValidationMismatchCount}, fallback={diagnostics.DdgiGpuSchedulerFallbackActive}:'{diagnostics.DdgiGpuSchedulerFallbackReason}', " +
            $"schedulerReinit={diagnostics.DdgiGpuSchedulerResourceReinitializationCount}/{diagnostics.DdgiGpuSchedulerTotalResourceReinitializationCount}, " +
            $"scheduleUs={diagnostics.GpuDdgiScheduleMicroseconds}, scheduleP95Us={diagnostics.GpuDdgiScheduleP95Microseconds}, scheduleOverBudget={diagnostics.GpuDdgiScheduleOverBudget}, " +
            $"scheduleStages reset/score/prefix/compact/finalize/readback/barrier={diagnostics.GpuDdgiScheduleResetMicroseconds}/{diagnostics.GpuDdgiScheduleScoreMicroseconds}/" +
            $"{diagnostics.GpuDdgiSchedulePrefixMicroseconds}/{diagnostics.GpuDdgiScheduleCompactMicroseconds}/{diagnostics.GpuDdgiScheduleFinalizeMicroseconds}/" +
            $"{diagnostics.GpuDdgiScheduleReadbackMicroseconds}/{diagnostics.GpuDdgiScheduleBarrierMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics DDGI update: traceDispatchGroups={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceDispatchGroupCount)}, " +
            $"traceProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceProbeCount)}, traceRayCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiTraceRayCount)}, " +
            $"blendProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiBlendProbeCount)}, relocateClassifyProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiRelocateClassifyProbeCount)}, " +
            $"publishProbeCount={FormatDdgiUpdateCount(diagnostics, diagnostics.DdgiPublishProbeCount)}.");
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
            $"Frame diagnostics culling: objectCandidatesCpu={diagnostics.ObjectCandidatesCpu}, objectFrustumCulledCpu={diagnostics.ObjectFrustumCulledCpu}, " +
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
            $"gpuLod={diagnostics.SceneSubmissionGpuLod0EmittedCount}/{diagnostics.SceneSubmissionGpuLod1EmittedCount}/{diagnostics.SceneSubmissionGpuLod2EmittedCount}, " +
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
            if (volume.Kind == DdgiProbeVolumeKind.Authored)
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
            $"uploadUs={diagnostics.CpuUploadMicroseconds}, stall={diagnostics.RuntimeWorstStallReason}:{diagnostics.RuntimeWorstStallMicroseconds}us.");

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

    private static string FormatPendingUInt(int readbackValid, uint value)
    {
        return readbackValid != 0 ? value.ToString(CultureInfo.InvariantCulture) : "pending";
    }

    private static string FormatDdgiUpdateCount(RendererDiagnostics diagnostics, uint value)
    {
        return diagnostics.DdgiSchedulerMode == DdgiSchedulerMode.CpuReference
            ? value.ToString(CultureInfo.InvariantCulture)
            : FormatPendingUInt(diagnostics.DdgiGpuSchedulerReadbackValid, value);
    }

    private static string FormatReadbackStatus(RendererDiagnostics diagnostics)
    {
        return diagnostics.DdgiGpuSchedulerReadbackValid != 0
            ? $"valid:{diagnostics.DdgiGpuSchedulerReadbackLatencyFrames}f"
            : "pending";
    }
}
