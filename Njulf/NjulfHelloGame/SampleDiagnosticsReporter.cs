using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

internal sealed class SampleDiagnosticsReporter
{
    private readonly MaterialManager _materialManager;
    private readonly IModelRenderUploadService? _uploadService;
    private bool _printedFrameDiagnostics;
    private int _diagnosticFrameCounter;

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

    public void PrintFirstFrameDiagnostics(IRendererRuntimeControls renderer)
    {
        if (renderer == null)
            return;

        RendererDiagnostics diagnostics = renderer.LastDiagnostics;
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
            $"lights={diagnostics.LightCount}, tiles={diagnostics.TileCountX}x{diagnostics.TileCountY}, materials={diagnostics.MaterialCount}, textures={diagnostics.TextureCount}.");
        Console.WriteLine(
            $"Frame diagnostics transparency/decals: mode={diagnostics.TransparencyMode}, debug={diagnostics.TransparencyDebugView}, " +
            $"receiveShadows={diagnostics.TransparentReceiveShadows}, solidObjects={diagnostics.SolidObjectCount}, maskedObjects={diagnostics.MaskedObjectCount}, " +
            $"transparentObjects={diagnostics.TransparentObjectCount}, decalObjects={diagnostics.GeometryDecalObjectCount}, solidMeshlets={diagnostics.SolidMeshletCount}, " +
            $"maskedMeshlets={diagnostics.MaskedMeshletCount}, transparentMeshlets={diagnostics.TransparentMeshletCount}, decalMeshlets={diagnostics.GeometryDecalMeshletCount}, " +
            $"maskMaterials={diagnostics.MaskMaterialCount}, blendMaterials={diagnostics.BlendMaterialCount}, decalMaterials={diagnostics.GeometryDecalMaterialCount}, " +
            $"sortCandidates={diagnostics.TransparentSortCandidateCount}, sortUs={diagnostics.TransparentSortMicroseconds}, overflow={diagnostics.TransparentOverflowCount}, " +
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
        if (renderer.TryInspectObject(diagnostics.DebugSelectedObjectIndex, out SelectedObjectInspection inspection))
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
            $"worstStall={diagnostics.RuntimeWorstStallReason}:{diagnostics.RuntimeWorstStallMicroseconds}us.");
        Console.WriteLine(
            $"Frame diagnostics memory: meshMiB={diagnostics.MeshBufferAllocatedBytes / (1024.0 * 1024.0):F1} used={diagnostics.MeshBufferUsedBytes / (1024.0 * 1024.0):F1}, " +
            $"sceneMiB={diagnostics.SceneBufferAllocatedBytes / (1024.0 * 1024.0):F1}, materialMiB={diagnostics.MaterialBufferAllocatedBytes / (1024.0 * 1024.0):F1}, " +
            $"lightMiB={(diagnostics.LightBufferAllocatedBytes + diagnostics.TiledLightBufferAllocatedBytes) / (1024.0 * 1024.0):F1}, texturesMiB={diagnostics.TextureAssetBytes / (1024.0 * 1024.0):F1}, " +
            $"rtMiB={diagnostics.RenderTargetBytes / (1024.0 * 1024.0):F1}, shadowMiB={diagnostics.ShadowMapBytes / (1024.0 * 1024.0):F1}, " +
            $"envMiB={diagnostics.EnvironmentTextureBytes / (1024.0 * 1024.0):F1}, reflectionMiB={diagnostics.ReflectionProbeBytes / (1024.0 * 1024.0):F1}, " +
            $"swapchainMiB={diagnostics.SwapchainEstimatedBytes / (1024.0 * 1024.0):F1}, unknownMiB={diagnostics.UnknownGpuMemoryBytes / (1024.0 * 1024.0):F1}.");
        Console.WriteLine(
            $"Frame diagnostics static batches: batches={diagnostics.StaticInstanceBatchCount}, instances={diagnostics.StaticInstanceCount}, " +
            $"visible={diagnostics.VisibleStaticInstanceCount}, culled={diagnostics.CulledStaticInstanceCount}, " +
            $"meshletDraws={diagnostics.StaticBatchMeshletDrawCommandCount}, buildUs={diagnostics.CpuStaticBatchBuildMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics GPU: depthUs={diagnostics.GpuDepthPrePassMicroseconds}, hizUs={diagnostics.GpuHiZBuildMicroseconds}, " +
            $"lightCullUs={diagnostics.GpuLightCullMicroseconds}, forwardUs={diagnostics.GpuForwardOpaqueMicroseconds}, transparentUs={diagnostics.GpuTransparentMicroseconds}, " +
            $"depthPrePass={diagnostics.DepthPrePassEnabled}, hiz={diagnostics.HiZEnabled}, occlusion={diagnostics.OcclusionEnabled}, hizSize={diagnostics.HiZWidth}x{diagnostics.HiZHeight}, hizMips={diagnostics.HiZMipCount}.");
        Console.WriteLine(
            $"Frame diagnostics CPU passes: depthRecordUs={diagnostics.CpuDepthPrePassRecordMicroseconds}, hizRecordUs={diagnostics.CpuHiZBuildRecordMicroseconds}, " +
            $"shadowRecordUs={diagnostics.CpuDirectionalShadowRecordMicroseconds}, lightCullRecordUs={diagnostics.CpuLightCullRecordMicroseconds}, forwardRecordUs={diagnostics.CpuForwardOpaqueRecordMicroseconds}, " +
            $"transparentRecordUs={diagnostics.CpuTransparentRecordMicroseconds}, bloomExtractRecordUs={diagnostics.CpuBloomExtractRecordMicroseconds}, " +
            $"bloomDownsampleRecordUs={diagnostics.CpuBloomDownsampleRecordMicroseconds}, bloomUpsampleRecordUs={diagnostics.CpuBloomUpsampleRecordMicroseconds}, " +
            $"fogRecordUs={diagnostics.CpuFogRecordMicroseconds}, autoExposureRecordUs={diagnostics.CpuAutoExposureRecordMicroseconds}, " +
            $"compositeRecordUs={diagnostics.CpuCompositeRecordMicroseconds}.");
        Console.WriteLine(
            $"Frame diagnostics shadows: enabled={diagnostics.DirectionalShadowsEnabled}, map={diagnostics.DirectionalShadowMapSize}, " +
            $"cascades={diagnostics.DirectionalShadowCascadeCount}, lightIndex={diagnostics.ShadowedDirectionalLightIndex}, " +
            $"debug={diagnostics.ShadowDebugView}, normalBias={diagnostics.ShadowNormalBias:F4}, slopeBias={diagnostics.ShadowSlopeScaledDepthBias:F2}.");
        Console.WriteLine(
            $"Frame diagnostics local shadows: spotEnabled={diagnostics.SpotShadowsEnabled}, spotCandidates={diagnostics.SpotShadowCandidateCount}, " +
            $"spotSelected={diagnostics.SpotShadowSelectedCount}, spotRejected={diagnostics.SpotShadowRejectedByBudgetCount}, " +
            $"atlas={diagnostics.SpotShadowAtlasSize} tile={diagnostics.SpotShadowTileSize}, atlasUsed={diagnostics.SpotShadowAtlasUsedTiles}/{diagnostics.SpotShadowAtlasCapacity}, " +
            $"spotRecordUs={diagnostics.CpuSpotShadowRecordMicroseconds}, pointEnabled={diagnostics.PointShadowsEnabled}, " +
            $"pointCandidates={diagnostics.PointShadowCandidateCount}, pointSelected={diagnostics.PointShadowSelectedCount}, " +
            $"pointRejected={diagnostics.PointShadowRejectedByBudgetCount}, pointMap={diagnostics.PointShadowMapSize}, " +
            $"pointFaces={diagnostics.PointShadowRenderedFaceCount}, pointRecordUs={diagnostics.CpuPointShadowRecordMicroseconds}.");
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
            $"samples={diagnostics.AmbientOcclusionSampleCount}, blur={diagnostics.AmbientOcclusionBlurRadius}, " +
            $"debug={diagnostics.AmbientOcclusionDebugView}, aoRecordUs={diagnostics.CpuAmbientOcclusionRecordMicroseconds}, " +
            $"blurRecordUs={diagnostics.CpuAmbientOcclusionBlurRecordMicroseconds}.");
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
            $"depthTasks={diagnostics.DepthTaskInvocations}, depthFrustumCulledGpu={diagnostics.DepthFrustumCulledMeshletsGpu}, depthEmitted={diagnostics.DepthEmittedMeshletsGpu}, " +
            $"forwardTasks={diagnostics.ForwardTaskInvocations}, forwardFrustumCulledGpu={diagnostics.ForwardFrustumCulledMeshletsGpu}, " +
            $"occlusionTested={diagnostics.ForwardOcclusionTestedMeshletsGpu}, occlusionCulled={diagnostics.OcclusionCulledMeshlets}, forwardEmitted={diagnostics.ForwardEmittedMeshletsGpu}.");
        Console.WriteLine(
            $"Frame diagnostics meshlets/uploads: totalMeshlets={diagnostics.MeshletCountTotal}, submittedCpu={diagnostics.MeshletCountSubmittedCpu}, " +
            $"avgTris={diagnostics.AvgTrianglesPerSubmittedMeshlet:F1}, avgVerts={diagnostics.AvgVerticesPerSubmittedMeshlet:F1}, " +
            $"under16Tris={diagnostics.SmallMeshletsUnder16Triangles}, under32Tris={diagnostics.SmallMeshletsUnder32Triangles}, " +
            $"uploadedBytes={diagnostics.UploadedBytes}, objectBytes={diagnostics.ObjectUploadBytes}, instanceBytes={diagnostics.InstanceUploadBytes}, " +
            $"meshletDrawBytes={diagnostics.MeshletDrawUploadBytes}, transparentMeshletDrawBytes={diagnostics.TransparentMeshletDrawUploadBytes}, " +
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

    private static void AddDynamicTextureIndex(HashSet<int> indices, int textureIndex)
    {
        if (textureIndex >= BindlessIndex.FirstDynamicTextureIndex)
            indices.Add(textureIndex);
    }
}
