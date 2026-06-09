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
        if (_printedFrameDiagnostics || renderer is not VulkanRenderer vulkanRenderer)
            return;

        RendererDiagnostics diagnostics = vulkanRenderer.LastDiagnostics;
        if (diagnostics.VisibleObjectCount == 0 && diagnostics.VisibleMeshletCount == 0)
            return;

        _printedFrameDiagnostics = true;
        Console.WriteLine(
            $"Frame diagnostics: visibleObjects={diagnostics.VisibleObjectCount}, " +
            $"visibleMeshlets={diagnostics.VisibleMeshletCount}, uploadedBytes={diagnostics.UploadedBytes}, " +
            $"opaqueObjects={diagnostics.OpaqueObjectCount}, maskedObjects={diagnostics.MaskedObjectCount}, " +
            $"transparentObjects={diagnostics.TransparentObjectCount}, opaqueMeshlets={diagnostics.OpaqueMeshletCount}, " +
            $"transparentMeshlets={diagnostics.TransparentMeshletCount}, blendMaterials={diagnostics.BlendMaterialCount}, " +
            $"submittedOpaqueMeshlets={diagnostics.SubmittedOpaqueMeshlets}, " +
            $"forwardCandidates={diagnostics.ForwardMeshletCandidates}, " +
            $"occlusionCulled={diagnostics.OcclusionCulledMeshlets}, " +
            $"forwardAfterOcclusion={diagnostics.ForwardMeshletVisibleAfterOcclusion}, " +
            $"lights={diagnostics.LightCount}, tiles={diagnostics.TileCountX}x{diagnostics.TileCountY}, " +
            $"materials={diagnostics.MaterialCount}, textures={diagnostics.TextureCount}, " +
            $"loadedFileTextures={diagnostics.LoadedFileTextureCount}, mipFallbacks={diagnostics.MipmapFallbackCount}, " +
            $"model='{diagnostics.LoadedModelName}', modelObjects={diagnostics.ModelRenderObjectCount}, " +
            $"registeredMeshes={diagnostics.RegisteredMeshCount}, modelMaterials={diagnostics.LoadedMaterialCount}, " +
            $"modelTextures={diagnostics.LoadedTextureCount}, defaultWhite={diagnostics.DefaultWhiteSubstitutions}, " +
            $"defaultNormal={diagnostics.DefaultNormalSubstitutions}, defaultBlack={diagnostics.DefaultBlackSubstitutions}.");
    }

    private static void AddDynamicTextureIndex(HashSet<int> indices, int textureIndex)
    {
        if (textureIndex >= BindlessIndex.FirstDynamicTextureIndex)
            indices.Add(textureIndex);
    }
}
