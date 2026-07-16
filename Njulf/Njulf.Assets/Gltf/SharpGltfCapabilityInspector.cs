using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SharpGLTF.Runtime;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;

namespace Njulf.Assets.Gltf;

public static class SharpGltfCapabilityInspector
{
    public static SharpGltfCapabilityReport Inspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Asset path must not be empty.", nameof(path));

        string fullPath = Path.GetFullPath(path);
        var packageVersions = new SharpGltfPackageVersions(
            GetInformationalVersion(typeof(ModelRoot).Assembly),
            GetInformationalVersion(typeof(SceneTemplate).Assembly),
            ResolveToolkitVersion());

        if (!File.Exists(fullPath))
        {
            return SharpGltfCapabilityReport.Failed(
                fullPath,
                packageVersions,
                nameof(FileNotFoundException),
                $"Asset file was not found: {fullPath}");
        }

        try
        {
            var settings = new ReadSettings();
            ModelRoot model = ModelRoot.Load(fullPath, settings);
            SharpGltfRuntimeSummary runtime = InspectRuntime(model);

            return SharpGltfCapabilityReport.Loaded(
                fullPath,
                packageVersions,
                new SharpGltfDocumentSummary(
                    model.Asset.Version?.ToString() ?? string.Empty,
                    model.Asset.MinVersion?.ToString() ?? string.Empty,
                    model.Asset.Generator ?? string.Empty,
                    model.ExtensionsUsed.Order(StringComparer.Ordinal).ToArray(),
                    model.ExtensionsRequired.Order(StringComparer.Ordinal).ToArray(),
                    model.IncompatibleExtensions.Order(StringComparer.Ordinal).ToArray(),
                    model.LogicalBuffers.Count,
                    model.LogicalBufferViews.Count,
                    model.LogicalAccessors.Count,
                    model.LogicalImages.Count,
                    model.LogicalTextures.Count,
                    model.LogicalTextureSamplers.Count,
                    model.LogicalMaterials.Count,
                    model.LogicalMeshes.Count,
                    model.LogicalNodes.Count,
                    model.LogicalScenes.Count,
                    model.LogicalSkins.Count,
                    model.LogicalAnimations.Count,
                    model.LogicalPunctualLights.Count,
                    model.DefaultScene?.LogicalIndex ?? -1),
                model.LogicalAccessors.Select(CreateAccessorSummary).ToArray(),
                model.LogicalBufferViews.Select(CreateBufferViewSummary).ToArray(),
                model.LogicalImages.Select(CreateImageSummary).ToArray(),
                model.LogicalTextures.Select(CreateTextureSummary).ToArray(),
                model.LogicalTextureSamplers.Select(CreateSamplerSummary).ToArray(),
                model.LogicalMaterials.Select(CreateMaterialSummary).ToArray(),
                model.LogicalMeshes.Select(CreateMeshSummary).ToArray(),
                runtime);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return SharpGltfCapabilityReport.Failed(
                fullPath,
                packageVersions,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message);
        }
    }

    private static SharpGltfRuntimeSummary InspectRuntime(ModelRoot model)
    {
        var runtimeIssues = new List<string>();
        int sceneTemplateCount = 0;
        int sceneInstanceCount = 0;
        int decodedMeshCount = 0;
        int decodedPrimitiveCount = 0;
        int decodedTriangleCount = 0;
        int decodedVertexCount = 0;
        int decodedUvSetCount = 0;
        int decodedColorSetCount = 0;
        int decodedJointWeightSetCount = 0;
        int decodedMorphTargetCount = 0;

        var runtimeOptions = new RuntimeOptions();

        foreach (Scene scene in model.LogicalScenes)
        {
            try
            {
                SceneTemplate template = SceneTemplate.Create(scene, runtimeOptions);
                sceneTemplateCount++;
                template.CreateInstance();
                sceneInstanceCount++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtimeIssues.Add($"Scene {scene.LogicalIndex}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (Mesh mesh in model.LogicalMeshes)
        {
            try
            {
                var decodedMesh = MeshDecoder.Decode(mesh, runtimeOptions);
                decodedMeshCount++;

                foreach (IMeshPrimitiveDecoder primitive in decodedMesh.Primitives)
                {
                    decodedPrimitiveCount++;
                    decodedVertexCount += primitive.VertexCount;
                    decodedTriangleCount += primitive.TriangleIndices.Count();
                    decodedUvSetCount += primitive.TexCoordsCount;
                    decodedColorSetCount += primitive.ColorsCount;
                    decodedJointWeightSetCount += primitive.JointsWeightsCount;
                    decodedMorphTargetCount += primitive.MorphTargetsCount;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtimeIssues.Add($"Mesh {mesh.LogicalIndex}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return new SharpGltfRuntimeSummary(
            sceneTemplateCount,
            sceneInstanceCount,
            decodedMeshCount,
            decodedPrimitiveCount,
            decodedTriangleCount,
            decodedVertexCount,
            decodedUvSetCount,
            decodedColorSetCount,
            decodedJointWeightSetCount,
            decodedMorphTargetCount,
            runtimeIssues);
    }

    private static SharpGltfAccessorSummary CreateAccessorSummary(Accessor accessor)
    {
        return new SharpGltfAccessorSummary(
            accessor.LogicalIndex,
            accessor.Name ?? string.Empty,
            accessor.Count,
            accessor.Dimensions.ToString(),
            accessor.Encoding.ToString(),
            accessor.Normalized,
            accessor.IsSparse,
            accessor.ByteOffset,
            accessor.ByteLength,
            accessor.SourceBufferView?.LogicalIndex ?? -1);
    }

    private static SharpGltfBufferViewSummary CreateBufferViewSummary(BufferView bufferView)
    {
        return new SharpGltfBufferViewSummary(
            bufferView.LogicalIndex,
            bufferView.Name ?? string.Empty,
            bufferView.Content.Count,
            bufferView.ByteStride,
            bufferView.IsVertexBuffer,
            bufferView.IsIndexBuffer,
            bufferView.IsDataBuffer);
    }

    private static SharpGltfImageSummary CreateImageSummary(Image image)
    {
        var content = image.Content;
        string kind = content.IsPng ? "png" :
            content.IsJpg ? "jpg" :
            content.IsKtx2 ? "ktx2" :
            content.IsDds ? "dds" :
            content.IsWebp ? "webp" :
            content.IsExtendedFormat ? "extended" :
            content.IsEmpty ? "empty" :
            "unknown";

        return new SharpGltfImageSummary(
            image.LogicalIndex,
            image.Name ?? string.Empty,
            kind,
            content.MimeType ?? string.Empty,
            content.SourcePath ?? string.Empty,
            content.Content.Length,
            content.IsValid);
    }

    private static SharpGltfTextureSummary CreateTextureSummary(Texture texture)
    {
        return new SharpGltfTextureSummary(
            texture.LogicalIndex,
            texture.Name ?? string.Empty,
            texture.PrimaryImage?.LogicalIndex ?? -1,
            texture.FallbackImage?.LogicalIndex ?? -1,
            texture.Sampler?.LogicalIndex ?? -1,
            GetExtensionTypeNames(texture.Extensions));
    }

    private static SharpGltfSamplerSummary CreateSamplerSummary(TextureSampler sampler)
    {
        return new SharpGltfSamplerSummary(
            sampler.LogicalIndex,
            sampler.Name ?? string.Empty,
            sampler.WrapS.ToString(),
            sampler.WrapT.ToString(),
            sampler.MinFilter.ToString(),
            sampler.MagFilter.ToString());
    }

    private static SharpGltfMaterialSummary CreateMaterialSummary(Material material)
    {
        SharpGltfMaterialChannelSummary[] channels = material.Channels
            .Select(channel => new SharpGltfMaterialChannelSummary(
                channel.Key,
                channel.Texture?.LogicalIndex ?? -1,
                channel.TextureCoordinate,
                channel.TextureTransform != null,
                channel.TextureTransform?.TextureCoordinateOverride))
            .OrderBy(channel => channel.Key, StringComparer.Ordinal)
            .ToArray();

        return new SharpGltfMaterialSummary(
            material.LogicalIndex,
            material.Name ?? string.Empty,
            material.Alpha.ToString(),
            material.AlphaCutoff,
            material.DoubleSided,
            material.Unlit,
            material.IndexOfRefraction,
            material.Dispersion,
            channels,
            GetExtensionTypeNames(material.Extensions));
    }

    private static SharpGltfMeshSummary CreateMeshSummary(Mesh mesh)
    {
        return new SharpGltfMeshSummary(
            mesh.LogicalIndex,
            mesh.Name ?? string.Empty,
            mesh.Primitives.Count,
            mesh.AllPrimitivesHaveJoints,
            mesh.Primitives.Select(CreatePrimitiveSummary).ToArray(),
            GetExtensionTypeNames(mesh.Extensions));
    }

    private static SharpGltfPrimitiveSummary CreatePrimitiveSummary(MeshPrimitive primitive)
    {
        return new SharpGltfPrimitiveSummary(
            primitive.LogicalIndex,
            primitive.DrawPrimitiveType.ToString(),
            primitive.Material?.LogicalIndex ?? -1,
            primitive.IndexAccessor?.LogicalIndex ?? -1,
            primitive.VertexAccessors.Keys.Order(StringComparer.Ordinal).ToArray(),
            primitive.MorphTargetsCount,
            GetExtensionTypeNames(primitive.Extensions));
    }

    private static string[] GetExtensionTypeNames(IEnumerable<object> extensions)
    {
        return extensions
            .Select(extension => extension.GetType().Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string GetInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            string.Empty;
    }

    private static string ResolveToolkitVersion()
    {
        return GetInformationalVersion(typeof(SceneBuilder).Assembly);
    }
}

public enum SharpGltfCapabilityStatus
{
    Loaded,
    Failed
}

public sealed record SharpGltfCapabilityReport(
    SharpGltfCapabilityStatus Status,
    string AssetPath,
    SharpGltfPackageVersions PackageVersions,
    SharpGltfDocumentSummary? Document,
    IReadOnlyList<SharpGltfAccessorSummary> Accessors,
    IReadOnlyList<SharpGltfBufferViewSummary> BufferViews,
    IReadOnlyList<SharpGltfImageSummary> Images,
    IReadOnlyList<SharpGltfTextureSummary> Textures,
    IReadOnlyList<SharpGltfSamplerSummary> Samplers,
    IReadOnlyList<SharpGltfMaterialSummary> Materials,
    IReadOnlyList<SharpGltfMeshSummary> Meshes,
    SharpGltfRuntimeSummary? Runtime,
    string? FailureType,
    string? FailureMessage)
{
    public bool LoadedSuccessfully => Status == SharpGltfCapabilityStatus.Loaded;

    public static SharpGltfCapabilityReport Loaded(
        string assetPath,
        SharpGltfPackageVersions packageVersions,
        SharpGltfDocumentSummary document,
        IReadOnlyList<SharpGltfAccessorSummary> accessors,
        IReadOnlyList<SharpGltfBufferViewSummary> bufferViews,
        IReadOnlyList<SharpGltfImageSummary> images,
        IReadOnlyList<SharpGltfTextureSummary> textures,
        IReadOnlyList<SharpGltfSamplerSummary> samplers,
        IReadOnlyList<SharpGltfMaterialSummary> materials,
        IReadOnlyList<SharpGltfMeshSummary> meshes,
        SharpGltfRuntimeSummary runtime)
    {
        return new SharpGltfCapabilityReport(
            SharpGltfCapabilityStatus.Loaded,
            assetPath,
            packageVersions,
            document,
            accessors,
            bufferViews,
            images,
            textures,
            samplers,
            materials,
            meshes,
            runtime,
            null,
            null);
    }

    public static SharpGltfCapabilityReport Failed(
        string assetPath,
        SharpGltfPackageVersions packageVersions,
        string failureType,
        string failureMessage)
    {
        return new SharpGltfCapabilityReport(
            SharpGltfCapabilityStatus.Failed,
            assetPath,
            packageVersions,
            null,
            Array.Empty<SharpGltfAccessorSummary>(),
            Array.Empty<SharpGltfBufferViewSummary>(),
            Array.Empty<SharpGltfImageSummary>(),
            Array.Empty<SharpGltfTextureSummary>(),
            Array.Empty<SharpGltfSamplerSummary>(),
            Array.Empty<SharpGltfMaterialSummary>(),
            Array.Empty<SharpGltfMeshSummary>(),
            null,
            failureType,
            failureMessage);
    }
}

public sealed record SharpGltfPackageVersions(
    string Core,
    string Runtime,
    string Toolkit);

public sealed record SharpGltfDocumentSummary(
    string Version,
    string MinVersion,
    string Generator,
    IReadOnlyList<string> ExtensionsUsed,
    IReadOnlyList<string> ExtensionsRequired,
    IReadOnlyList<string> IncompatibleExtensions,
    int BufferCount,
    int BufferViewCount,
    int AccessorCount,
    int ImageCount,
    int TextureCount,
    int SamplerCount,
    int MaterialCount,
    int MeshCount,
    int NodeCount,
    int SceneCount,
    int SkinCount,
    int AnimationCount,
    int PunctualLightCount,
    int DefaultSceneIndex);

public sealed record SharpGltfRuntimeSummary(
    int SceneTemplateCount,
    int SceneInstanceCount,
    int DecodedMeshCount,
    int DecodedPrimitiveCount,
    int DecodedTriangleCount,
    int DecodedVertexCount,
    int DecodedUvSetCount,
    int DecodedColorSetCount,
    int DecodedJointWeightSetCount,
    int DecodedMorphTargetCount,
    IReadOnlyList<string> Issues);

public sealed record SharpGltfAccessorSummary(
    int LogicalIndex,
    string Name,
    int Count,
    string Dimensions,
    string Encoding,
    bool Normalized,
    bool IsSparse,
    int ByteOffset,
    int ByteLength,
    int SourceBufferViewIndex);

public sealed record SharpGltfBufferViewSummary(
    int LogicalIndex,
    string Name,
    int ByteLength,
    int ByteStride,
    bool IsVertexBuffer,
    bool IsIndexBuffer,
    bool IsDataBuffer);

public sealed record SharpGltfImageSummary(
    int LogicalIndex,
    string Name,
    string Kind,
    string MimeType,
    string SourcePath,
    int ByteLength,
    bool IsValid);

public sealed record SharpGltfTextureSummary(
    int LogicalIndex,
    string Name,
    int PrimaryImageIndex,
    int FallbackImageIndex,
    int SamplerIndex,
    IReadOnlyList<string> ExtensionTypeNames);

public sealed record SharpGltfSamplerSummary(
    int LogicalIndex,
    string Name,
    string WrapS,
    string WrapT,
    string MinFilter,
    string MagFilter);

public sealed record SharpGltfMaterialSummary(
    int LogicalIndex,
    string Name,
    string AlphaMode,
    float AlphaCutoff,
    bool DoubleSided,
    bool Unlit,
    float IndexOfRefraction,
    float Dispersion,
    IReadOnlyList<SharpGltfMaterialChannelSummary> Channels,
    IReadOnlyList<string> ExtensionTypeNames);

public sealed record SharpGltfMaterialChannelSummary(
    string Key,
    int TextureIndex,
    int TextureCoordinate,
    bool HasTextureTransform,
    int? TextureCoordinateOverride);

public sealed record SharpGltfMeshSummary(
    int LogicalIndex,
    string Name,
    int PrimitiveCount,
    bool AllPrimitivesHaveJoints,
    IReadOnlyList<SharpGltfPrimitiveSummary> Primitives,
    IReadOnlyList<string> ExtensionTypeNames);

public sealed record SharpGltfPrimitiveSummary(
    int LogicalIndex,
    string DrawPrimitiveType,
    int MaterialIndex,
    int IndexAccessorIndex,
    IReadOnlyList<string> VertexAttributeKeys,
    int MorphTargetsCount,
    IReadOnlyList<string> ExtensionTypeNames);
