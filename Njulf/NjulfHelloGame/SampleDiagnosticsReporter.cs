using System;
using System.Collections.Generic;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
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
            $"lights={diagnostics.LightCount}, tiles={diagnostics.TileCountX}x{diagnostics.TileCountY}, materials={diagnostics.MaterialCount}, textures={diagnostics.TextureCount}.");
        Console.WriteLine(
            $"Frame diagnostics CPU: totalDrawUs={diagnostics.CpuTotalDrawSceneMicroseconds}, sceneBuildUs={diagnostics.CpuSceneBuildMicroseconds}, " +
            $"signatureUs={diagnostics.CpuPayloadSignatureMicroseconds}, objectCullUs={diagnostics.CpuObjectCullMicroseconds}, " +
            $"meshletCullUs={diagnostics.CpuMeshletCullMicroseconds}, materialUploadUs={diagnostics.CpuMaterialUploadMicroseconds}, " +
            $"uploadUs={diagnostics.CpuUploadMicroseconds}, payloadRebuilt={diagnostics.ScenePayloadRebuilt}.");
        Console.WriteLine(
            $"Frame diagnostics GPU: depthUs={diagnostics.GpuDepthPrePassMicroseconds}, hizUs={diagnostics.GpuHiZBuildMicroseconds}, " +
            $"lightCullUs={diagnostics.GpuLightCullMicroseconds}, forwardUs={diagnostics.GpuForwardOpaqueMicroseconds}, transparentUs={diagnostics.GpuTransparentMicroseconds}, " +
            $"depthPrePass={diagnostics.DepthPrePassEnabled}, hiz={diagnostics.HiZEnabled}, occlusion={diagnostics.OcclusionEnabled}, hizSize={diagnostics.HiZWidth}x{diagnostics.HiZHeight}, hizMips={diagnostics.HiZMipCount}.");
        Console.WriteLine(
            $"Frame diagnostics CPU passes: depthRecordUs={diagnostics.CpuDepthPrePassRecordMicroseconds}, hizRecordUs={diagnostics.CpuHiZBuildRecordMicroseconds}, " +
            $"lightCullRecordUs={diagnostics.CpuLightCullRecordMicroseconds}, forwardRecordUs={diagnostics.CpuForwardOpaqueRecordMicroseconds}, " +
            $"transparentRecordUs={diagnostics.CpuTransparentRecordMicroseconds}.");
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
            $"materialBytes={diagnostics.MaterialUploadBytes}, lightBytes={diagnostics.LightUploadBytes}, uploads={diagnostics.SceneUploadCount}, uploadSkipped={diagnostics.SceneUploadSkipped}.");
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
