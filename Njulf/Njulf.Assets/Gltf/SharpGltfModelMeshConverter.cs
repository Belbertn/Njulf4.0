using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Njulf.Core.Animation;
using SharpGLTF.Schema2;
using SharpGLTF.Runtime;
using SharpGLTF.Transforms;
using CoreAnimationChannel = Njulf.Core.Animation.AnimationChannel;
using CoreBoundingBox = Njulf.Core.Math.BoundingBox;
using CoreBoundingSphere = Njulf.Core.Math.BoundingSphere;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreQuaternion = Njulf.Core.Math.Quaternion;
using CoreSkeleton = Njulf.Core.Animation.Skeleton;
using CoreSkin = Njulf.Core.Animation.Skin;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;
using NumericsMatrix4x4 = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector4 = System.Numerics.Vector4;
using SchemaMesh = SharpGLTF.Schema2.Mesh;
using SchemaNode = SharpGLTF.Schema2.Node;
using SchemaSkin = SharpGLTF.Schema2.Skin;

namespace Njulf.Assets.Gltf;

internal static class SharpGltfModelMeshConverter
{
    public static ModelMesh Import(
        string path,
        ImporterOptions options,
        SharpGltfCapabilityReport? capability = null,
        AssetImportDiagnostics? diagnostics = null)
    {
        string fullPath = Path.GetFullPath(path);
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints = CreateImageSourceHints(fullPath);
        ModelRoot root = ModelRoot.Load(fullPath, new ReadSettings());

        diagnostics ??= new AssetImportDiagnostics();
        if (string.Equals(Path.GetExtension(fullPath), ".glb", StringComparison.OrdinalIgnoreCase))
            diagnostics.ImportedGlbCount++;
        else
            diagnostics.ImportedGltfCount++;

        AddImageDiagnostics(root, imageSourceHints, diagnostics);

        foreach (SharpGltfBufferViewSummary bufferView in capability?.BufferViews ?? Array.Empty<SharpGltfBufferViewSummary>())
        {
            if (bufferView.IsVertexBuffer && root.LogicalMeshes.Any(mesh => mesh.Primitives.Any(primitive => primitive.VertexAccessors.ContainsKey("COLOR_0"))))
                diagnostics.VertexColorMeshCount++;
        }
        diagnostics.ImportedSamplerCount = root.LogicalTextureSamplers.Count;

        var model = new ModelMesh
        {
            Name = Path.GetFileNameWithoutExtension(fullPath),
            ImportDiagnostics = diagnostics
        };

        model.Materials.AddRange(CreateMaterials(root, fullPath, diagnostics, imageSourceHints));
        if (model.Materials.Count == 0)
            model.Materials.Add(ModelMaterial.Default);

        SkinImportContext skinContext = ImportSkins(root, model, options.GlobalScale);
        ImportAnimations(root, model, options.GlobalScale, skinContext.AnimationJointByNode);

        Scene scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException($"glTF asset '{fullPath}' does not contain a scene.");

        var vertices = new List<CoreVector3>();
        var normals = new List<CoreVector3>();
        var tangents = new List<CoreVector3>();
        var bitangents = new List<CoreVector3>();
        var texCoords = new List<CoreVector2>();
        var texCoords1 = new List<CoreVector2>();
        var vertexColors = new List<CoreVector4>();
        var jointIndices = new List<VertexJointIndices>();
        var jointWeights = new List<VertexJointWeights>();
        var indices = new List<uint>();

        int sceneNodeIndex = 0;
        foreach (SchemaNode node in EnumerateSceneNodes(scene))
        {
            if (node.Mesh == null)
            {
                sceneNodeIndex++;
                continue;
            }

            AppendNodeMesh(
                model,
                node,
                node.LogicalIndex >= 0 ? node.LogicalIndex : sceneNodeIndex,
                node.Mesh,
                options,
                skinContext,
                vertices,
                normals,
                tangents,
                bitangents,
                texCoords,
                texCoords1,
                vertexColors,
                jointIndices,
                jointWeights,
                indices);
            sceneNodeIndex++;
        }

        model.Vertices = vertices.ToArray();
        model.Normals = normals.ToArray();
        model.Tangents = tangents.ToArray();
        model.Bitangents = bitangents.ToArray();
        model.TexCoords = texCoords.ToArray();
        model.TexCoords1 = texCoords1.ToArray();
        model.VertexColors = vertexColors.ToArray();
        if (jointIndices.Count == vertices.Count && jointWeights.Any(weight => weight.Sum > 0f))
        {
            model.JointIndices0 = jointIndices.ToArray();
            model.JointWeights0 = jointWeights.ToArray();
        }

        model.Indices = indices.ToArray();
        ComputeBoundingVolume(vertices, out CoreBoundingBox boundingBox, out CoreBoundingSphere boundingSphere);
        model.BoundingBox = boundingBox;
        model.BoundingSphere = boundingSphere;
        model.AnimationDiagnostics = CreateAnimationDiagnostics(model);

        return model;
    }

    private static IEnumerable<ModelMaterial> CreateMaterials(
        ModelRoot root,
        string assetPath,
        AssetImportDiagnostics diagnostics,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints)
    {
        foreach (Material material in root.LogicalMaterials)
        {
            var imported = new ModelMaterial
            {
                Name = string.IsNullOrWhiteSpace(material.Name) ? $"Material_{material.LogicalIndex}" : material.Name,
                AlphaMode = material.Alpha switch
                {
                    AlphaMode.MASK => ModelAlphaMode.Mask,
                    AlphaMode.BLEND => ModelAlphaMode.Blend,
                    _ => ModelAlphaMode.Opaque
                },
                AlphaCutoff = material.AlphaCutoff,
                DoubleSided = material.DoubleSided,
                Unlit = material.Unlit,
                Ior = material.IndexOfRefraction,
                Dispersion = material.Dispersion
            };

            MaterialChannel? baseColor = material.FindChannel("BaseColor");
            if (baseColor.HasValue)
            {
                imported.Albedo = ToCoreVector(baseColor.Value.Color);
                imported.BaseColorTexture = CreateTextureSlot(baseColor.Value, assetPath, TextureColorSpace.Srgb, diagnostics, imageSourceHints);
                imported.AlbedoTexturePath = imported.BaseColorTexture?.Source?.FilePath;
            }

            MaterialChannel? emissive = material.FindChannel("Emissive");
            if (emissive.HasValue)
            {
                imported.Emissive = ToCoreVector(emissive.Value.Color);
                imported.EmissiveStrength = GetFactorOrDefault(emissive.Value, "EmissiveStrength", 1f);
                imported.EmissiveTexture = CreateTextureSlot(emissive.Value, assetPath, TextureColorSpace.Srgb, diagnostics, imageSourceHints);
                imported.EmissiveTexturePath = imported.EmissiveTexture?.Source?.FilePath;
                if (Math.Abs(imported.EmissiveStrength - 1f) > float.Epsilon)
                    imported.FeatureFlags |= ModelMaterialFeatureBits.EmissiveStrength;
            }

            MaterialChannel? metallicRoughness = material.FindChannel("MetallicRoughness");
            if (metallicRoughness.HasValue)
            {
                imported.Metallic = GetFactorOrDefault(metallicRoughness.Value, "MetallicFactor", imported.Metallic);
                imported.Roughness = GetFactorOrDefault(metallicRoughness.Value, "RoughnessFactor", imported.Roughness);
                imported.MetallicRoughnessTexture = CreateTextureSlot(metallicRoughness.Value, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
                imported.MetallicRoughnessTexturePath = imported.MetallicRoughnessTexture?.Source?.FilePath;
            }

            MaterialChannel? normal = material.FindChannel("Normal");
            if (normal.HasValue)
            {
                imported.NormalScale = GetFactorOrDefault(normal.Value, "Scale", GetParameterXOrDefault(normal.Value, imported.NormalScale));
                imported.NormalTexture = CreateTextureSlot(normal.Value, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
                imported.NormalTexturePath = imported.NormalTexture?.Source?.FilePath;
            }

            MaterialChannel? occlusion = material.FindChannel("Occlusion");
            if (occlusion.HasValue)
            {
                imported.AmbientOcclusion = GetFactorOrDefault(occlusion.Value, "Strength", GetParameterXOrDefault(occlusion.Value, imported.AmbientOcclusion));
                imported.OcclusionTexture = CreateTextureSlot(occlusion.Value, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
                imported.OcclusionTexturePath = imported.OcclusionTexture?.Source?.FilePath;
            }

            if (imported.Dispersion > 0f)
                imported.FeatureFlags |= ModelMaterialFeatureBits.Dispersion;

            ApplyMaterialExtensions(imported, material, assetPath, diagnostics, imageSourceHints);

            yield return imported;
        }
    }

    private static void ApplyMaterialExtensions(
        ModelMaterial imported,
        Material material,
        string assetPath,
        AssetImportDiagnostics diagnostics,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints)
    {
        MaterialChannel? clearcoat = material.FindChannel("ClearCoat");
        MaterialChannel? clearcoatRoughness = material.FindChannel("ClearCoatRoughness");
        MaterialChannel? clearcoatNormal = material.FindChannel("ClearCoatNormal");
        if (clearcoat.HasValue || clearcoatRoughness.HasValue || clearcoatNormal.HasValue)
        {
            if (clearcoat.HasValue)
                imported.ClearcoatFactor = GetFactorOrDefault(clearcoat.Value, "ClearCoatFactor", imported.ClearcoatFactor);
            if (clearcoatRoughness.HasValue)
                imported.ClearcoatRoughness = GetFactorOrDefault(clearcoatRoughness.Value, "RoughnessFactor", imported.ClearcoatRoughness);
            if (clearcoatNormal.HasValue)
                imported.ClearcoatNormalScale = GetFactorOrDefault(clearcoatNormal.Value, "NormalScale", imported.ClearcoatNormalScale);

            imported.ClearcoatTexture = CreateTextureSlot(clearcoat, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.ClearcoatRoughnessTexture = CreateTextureSlot(clearcoatRoughness, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.ClearcoatNormalTexture = CreateTextureSlot(clearcoatNormal, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.ClearcoatTexturePath = imported.ClearcoatTexture?.Source?.FilePath;
            imported.ClearcoatRoughnessTexturePath = imported.ClearcoatRoughnessTexture?.Source?.FilePath;
            imported.ClearcoatNormalTexturePath = imported.ClearcoatNormalTexture?.Source?.FilePath;

            imported.FeatureFlags |= ModelMaterialFeatureBits.Clearcoat;
            if (imported.ClearcoatTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.ClearcoatTexture;
            if (imported.ClearcoatRoughnessTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.ClearcoatRoughnessTexture;
            if (imported.ClearcoatNormalTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.ClearcoatNormalTexture;
        }

        MaterialChannel? sheenColor = material.FindChannel("SheenColor");
        MaterialChannel? sheenRoughness = material.FindChannel("SheenRoughness");
        if (sheenColor.HasValue || sheenRoughness.HasValue)
        {
            if (sheenColor.HasValue)
                imported.SheenColor = ToCoreVector(sheenColor.Value.Color);
            if (sheenRoughness.HasValue)
                imported.SheenRoughness = GetFactorOrDefault(sheenRoughness.Value, "RoughnessFactor", imported.SheenRoughness);

            imported.SheenColorTexture = CreateTextureSlot(sheenColor, assetPath, TextureColorSpace.Srgb, diagnostics, imageSourceHints);
            imported.SheenRoughnessTexture = CreateTextureSlot(sheenRoughness, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.SheenColorTexturePath = imported.SheenColorTexture?.Source?.FilePath;
            imported.SheenRoughnessTexturePath = imported.SheenRoughnessTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.Sheen;
            if (imported.SheenColorTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.SheenColorTexture;
            if (imported.SheenRoughnessTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.SheenRoughnessTexture;
        }

        MaterialChannel? anisotropy = material.FindChannel("Anisotropy");
        if (anisotropy.HasValue)
        {
            imported.AnisotropyStrength = GetFactorOrDefault(anisotropy.Value, "AnisotropyStrength", imported.AnisotropyStrength);
            imported.AnisotropyRotation = GetFactorOrDefault(anisotropy.Value, "AnisotropyRotation", imported.AnisotropyRotation);
            imported.AnisotropyTexture = CreateTextureSlot(anisotropy, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.AnisotropyTexturePath = imported.AnisotropyTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.Anisotropy;
            if (imported.AnisotropyTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.AnisotropyTexture;
        }

        MaterialChannel? transmission = material.FindChannel("Transmission");
        if (transmission.HasValue)
        {
            imported.TransmissionFactor = GetFactorOrDefault(transmission.Value, "TransmissionFactor", imported.TransmissionFactor);
            imported.TransmissionTexture = CreateTextureSlot(transmission, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.TransmissionTexturePath = imported.TransmissionTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.Transmission;
            if (imported.TransmissionTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.TransmissionTexture;
        }

        MaterialChannel? volumeThickness = material.FindChannel("VolumeThickness");
        MaterialChannel? volumeAttenuation = material.FindChannel("VolumeAttenuation");
        if (volumeThickness.HasValue || volumeAttenuation.HasValue)
        {
            if (volumeThickness.HasValue)
                imported.ThicknessFactor = GetFactorOrDefault(volumeThickness.Value, "ThicknessFactor", imported.ThicknessFactor);
            if (volumeAttenuation.HasValue)
            {
                imported.AttenuationColor = ToCoreVector(volumeAttenuation.Value.Color);
                imported.AttenuationDistance = GetFactorOrDefault(volumeAttenuation.Value, "AttenuationDistance", imported.AttenuationDistance);
            }

            imported.ThicknessTexture = CreateTextureSlot(volumeThickness, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.ThicknessTexturePath = imported.ThicknessTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.VolumeApproximation;
        }

        MaterialChannel? specularFactor = material.FindChannel("SpecularFactor");
        MaterialChannel? specularColor = material.FindChannel("SpecularColor");
        if (specularFactor.HasValue || specularColor.HasValue)
        {
            if (specularFactor.HasValue)
                imported.SpecularFactor = GetFactorOrDefault(specularFactor.Value, "SpecularFactor", imported.SpecularFactor);
            if (specularColor.HasValue)
                imported.SpecularColor = ToCoreVector(specularColor.Value.Color);

            imported.SpecularTexture = CreateTextureSlot(specularFactor, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.SpecularColorTexture = CreateTextureSlot(specularColor, assetPath, TextureColorSpace.Srgb, diagnostics, imageSourceHints);
            imported.SpecularTexturePath = imported.SpecularTexture?.Source?.FilePath;
            imported.SpecularColorTexturePath = imported.SpecularColorTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.Specular;
            if (imported.SpecularTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.SpecularTexture;
            if (imported.SpecularColorTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.SpecularColorTexture;
        }

        MaterialChannel? iridescence = material.FindChannel("Iridescence");
        MaterialChannel? iridescenceThickness = material.FindChannel("IridescenceThickness");
        if (iridescence.HasValue || iridescenceThickness.HasValue)
        {
            if (iridescence.HasValue)
            {
                imported.IridescenceFactor = GetFactorOrDefault(iridescence.Value, "IridescenceFactor", imported.IridescenceFactor);
                imported.IridescenceIor = GetFactorOrDefault(iridescence.Value, "IndexOfRefraction", imported.IridescenceIor);
            }

            if (iridescenceThickness.HasValue)
            {
                imported.IridescenceThicknessMinimum = GetFactorOrDefault(iridescenceThickness.Value, "Minimum", imported.IridescenceThicknessMinimum);
                imported.IridescenceThicknessMaximum = GetFactorOrDefault(iridescenceThickness.Value, "Maximum", imported.IridescenceThicknessMaximum);
            }

            imported.IridescenceTexture = CreateTextureSlot(iridescence, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.IridescenceThicknessTexture = CreateTextureSlot(iridescenceThickness, assetPath, TextureColorSpace.Linear, diagnostics, imageSourceHints);
            imported.IridescenceTexturePath = imported.IridescenceTexture?.Source?.FilePath;
            imported.IridescenceThicknessTexturePath = imported.IridescenceThicknessTexture?.Source?.FilePath;
            imported.FeatureFlags |= ModelMaterialFeatureBits.Iridescence;
            if (imported.IridescenceTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.IridescenceTexture;
            if (imported.IridescenceThicknessTexture != null)
                imported.FeatureFlags |= ModelMaterialFeatureBits.IridescenceThicknessTexture;
        }
    }

    private static ModelTextureSlot? CreateTextureSlot(
        MaterialChannel? channel,
        string assetPath,
        TextureColorSpace colorSpace,
        AssetImportDiagnostics diagnostics,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints)
    {
        return channel.HasValue
            ? CreateTextureSlot(channel.Value, assetPath, colorSpace, diagnostics, imageSourceHints)
            : null;
    }

    private static ModelTextureSlot? CreateTextureSlot(
        MaterialChannel channel,
        string assetPath,
        TextureColorSpace colorSpace,
        AssetImportDiagnostics diagnostics,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints)
    {
        Texture? texture = TryGetTexture(channel);
        if (texture == null)
            return null;

        Image? image = TryGetPrimaryImage(texture) ?? TryGetFallbackImage(texture);
        ModelTextureSource? source = image == null ? null : CreateTextureSource(image, assetPath, imageSourceHints);
        if (source == null)
            return null;

        TextureTransform? transform = channel.TextureTransform;
        if (transform != null)
            diagnostics.TextureTransformCount++;

        int texCoordSet = transform?.TextureCoordinateOverride ?? channel.TextureCoordinate;
        return new ModelTextureSlot
        {
            Source = source,
            Sampler = ToSamplerDescription(texture.Sampler),
            ColorSpace = colorSpace,
            TexCoordSet = Math.Clamp(texCoordSet, 0, 1),
            Offset = transform == null ? CoreVector2.Zero : ToCoreVector(transform.Offset),
            Scale = transform == null ? new CoreVector2(1f, 1f) : ToCoreVector(transform.Scale),
            RotationRadians = transform?.Rotation ?? 0f
        };
    }

    private static Texture? TryGetTexture(MaterialChannel channel)
    {
        try
        {
            return channel.Texture;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Image? TryGetPrimaryImage(Texture texture)
    {
        try
        {
            return texture.PrimaryImage;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Image? TryGetFallbackImage(Texture texture)
    {
        try
        {
            return texture.FallbackImage;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static ModelTextureSource? CreateTextureSource(
        Image image,
        string assetPath,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints)
    {
        SharpGLTF.Memory.MemoryImage content = image.Content;
        if (!content.IsValid)
            return null;

        imageSourceHints.TryGetValue(image.LogicalIndex, out GltfImageSourceHint hint);
        string debugName = string.IsNullOrWhiteSpace(image.Name) ? $"image_{image.LogicalIndex}" : image.Name;
        string? sourcePath = content.SourcePath;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string absolutePath = Path.IsPathRooted(sourcePath)
                ? Path.GetFullPath(sourcePath)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assetPath) ?? AppContext.BaseDirectory, sourcePath));

            if (File.Exists(absolutePath))
            {
                return new ModelTextureSource
                {
                    DebugName = debugName,
                    SourceKind = TextureSourceKind.ExternalFile,
                    FilePath = absolutePath,
                    MimeType = content.MimeType,
                    ContainerKind = ToTextureContainerKind(content),
                    EncodedByteLength = hint.EncodedByteLength > 0
                        ? hint.EncodedByteLength
                        : checked((int)Math.Min(new FileInfo(absolutePath).Length, int.MaxValue)),
                    CacheIdentity = string.IsNullOrWhiteSpace(hint.CacheIdentity) ? absolutePath : hint.CacheIdentity
                };
            }
        }

        byte[] bytes = content.Content.ToArray();
        if (bytes.Length == 0)
            return null;

        return new ModelTextureSource
        {
            DebugName = debugName,
            SourceKind = hint.SourceKind == TextureSourceKind.Unknown
                ? string.Equals(Path.GetExtension(assetPath), ".glb", StringComparison.OrdinalIgnoreCase)
                    ? TextureSourceKind.GlbBinary
                    : TextureSourceKind.EmbeddedMemory
                : hint.SourceKind,
            Bytes = bytes,
            MimeType = content.MimeType,
            ContainerKind = ToTextureContainerKind(content),
            EncodedByteLength = hint.EncodedByteLength > 0 ? hint.EncodedByteLength : bytes.Length,
            CacheIdentity = string.IsNullOrWhiteSpace(hint.CacheIdentity)
                ? $"{Path.GetFullPath(assetPath)}#image:{image.LogicalIndex}"
                : hint.CacheIdentity
        };
    }

    private static void AddImageDiagnostics(
        ModelRoot root,
        IReadOnlyDictionary<int, GltfImageSourceHint> imageSourceHints,
        AssetImportDiagnostics diagnostics)
    {
        foreach (Image image in root.LogicalImages)
        {
            imageSourceHints.TryGetValue(image.LogicalIndex, out GltfImageSourceHint hint);
            TextureSourceKind kind = hint.SourceKind;
            if (kind == TextureSourceKind.Unknown)
            {
                kind = string.IsNullOrWhiteSpace(image.Content.SourcePath)
                    ? TextureSourceKind.EmbeddedMemory
                    : TextureSourceKind.ExternalFile;
            }

            switch (kind)
            {
                case TextureSourceKind.ExternalFile:
                    diagnostics.ExternalImageCount++;
                    break;
                case TextureSourceKind.DataUri:
                    diagnostics.EmbeddedImageCount++;
                    diagnostics.DataUriImageCount++;
                    break;
                case TextureSourceKind.BufferView:
                    diagnostics.EmbeddedImageCount++;
                    diagnostics.BufferViewImageCount++;
                    break;
                case TextureSourceKind.GlbBinary:
                    diagnostics.EmbeddedImageCount++;
                    diagnostics.BufferViewImageCount++;
                    break;
                case TextureSourceKind.EmbeddedMemory:
                    diagnostics.EmbeddedImageCount++;
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<int, GltfImageSourceHint> CreateImageSourceHints(string assetPath)
    {
        string extension = Path.GetExtension(assetPath);
        if (!string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<int, GltfImageSourceHint>();
        }

        try
        {
            byte[] jsonBytes;
            bool isGlb = string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase);
            if (isGlb)
            {
                jsonBytes = ReadGlbJson(assetPath);
            }
            else
            {
                jsonBytes = File.ReadAllBytes(assetPath);
            }

            using JsonDocument document = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            return ReadImageSourceHints(document.RootElement, assetPath, isGlb);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            System.Diagnostics.Debug.WriteLine($"SharpGLTF image source hint scan failed for '{assetPath}': {ex.Message}");
            return new Dictionary<int, GltfImageSourceHint>();
        }
    }

    private static IReadOnlyDictionary<int, GltfImageSourceHint> ReadImageSourceHints(JsonElement root, string assetPath, bool isGlb)
    {
        var hints = new Dictionary<int, GltfImageSourceHint>();
        if (!root.TryGetProperty("images", out JsonElement images) || images.ValueKind != JsonValueKind.Array)
            return hints;

        int[] bufferViewByteLengths = ReadBufferViewByteLengths(root);
        string assetDirectory = Path.GetDirectoryName(assetPath) ?? AppContext.BaseDirectory;
        int imageIndex = 0;
        foreach (JsonElement image in images.EnumerateArray())
        {
            if (image.TryGetProperty("bufferView", out JsonElement bufferViewElement) &&
                bufferViewElement.TryGetInt32(out int bufferViewIndex))
            {
                int byteLength = bufferViewIndex >= 0 && bufferViewIndex < bufferViewByteLengths.Length
                    ? bufferViewByteLengths[bufferViewIndex]
                    : 0;
                TextureSourceKind kind = isGlb ? TextureSourceKind.GlbBinary : TextureSourceKind.BufferView;
                hints[imageIndex] = new GltfImageSourceHint(
                    kind,
                    byteLength,
                    $"{Path.GetFullPath(assetPath)}#image:{imageIndex}:bufferView:{bufferViewIndex}");
            }
            else if (image.TryGetProperty("uri", out JsonElement uriElement) &&
                     uriElement.ValueKind == JsonValueKind.String)
            {
                string uri = uriElement.GetString() ?? string.Empty;
                if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    hints[imageIndex] = new GltfImageSourceHint(
                        TextureSourceKind.DataUri,
                        GetDataUriByteLength(uri),
                        $"{Path.GetFullPath(assetPath)}#image:{imageIndex}:data");
                }
                else
                {
                    string absolutePath = Path.IsPathRooted(uri)
                        ? Path.GetFullPath(Uri.UnescapeDataString(uri))
                        : Path.GetFullPath(Path.Combine(assetDirectory, Uri.UnescapeDataString(uri.Replace('\\', Path.DirectorySeparatorChar))));
                    int byteLength = File.Exists(absolutePath)
                        ? checked((int)Math.Min(new FileInfo(absolutePath).Length, int.MaxValue))
                        : 0;
                    hints[imageIndex] = new GltfImageSourceHint(
                        TextureSourceKind.ExternalFile,
                        byteLength,
                        absolutePath);
                }
            }

            imageIndex++;
        }

        return hints;
    }

    private static int[] ReadBufferViewByteLengths(JsonElement root)
    {
        if (!root.TryGetProperty("bufferViews", out JsonElement bufferViews) ||
            bufferViews.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        return bufferViews
            .EnumerateArray()
            .Select(bufferView => bufferView.TryGetProperty("byteLength", out JsonElement byteLengthElement) &&
                                  byteLengthElement.TryGetInt32(out int byteLength)
                ? byteLength
                : 0)
            .ToArray();
    }

    private static int GetDataUriByteLength(string uri)
    {
        int commaIndex = uri.IndexOf(',');
        if (commaIndex < 0 || commaIndex == uri.Length - 1)
            return 0;

        string metadata = uri[..commaIndex];
        string payload = uri[(commaIndex + 1)..];
        if (metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return Convert.FromBase64String(payload).Length;
            }
            catch (FormatException)
            {
                return 0;
            }
        }

        return Uri.UnescapeDataString(payload).Length;
    }

    private static byte[] ReadGlbJson(string assetPath)
    {
        byte[] data = File.ReadAllBytes(assetPath);
        if (data.Length < 20)
            throw new InvalidDataException($"glB asset '{assetPath}' is too small to contain a valid header.");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
        if (magic != 0x46546C67u)
            throw new InvalidDataException($"glB asset '{assetPath}' has an invalid magic value.");
        if (version != 2u)
            throw new NotSupportedException($"glB asset '{assetPath}' uses version {version}; only glB 2.0 is supported.");
        if (declaredLength > data.Length)
            throw new InvalidDataException($"glB asset '{assetPath}' declares {declaredLength} bytes but the file contains {data.Length} bytes.");

        int offset = 12;
        while (offset + 8 <= declaredLength)
        {
            int chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4)));
            uint chunkType = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4, 4));
            offset += 8;
            if (chunkLength < 0 || offset + chunkLength > declaredLength)
                throw new InvalidDataException($"glB asset '{assetPath}' contains a chunk that extends past the declared file length.");

            if (chunkType == 0x4E4F534Au)
                return TrimJsonPadding(data.AsSpan(offset, chunkLength).ToArray());

            offset += chunkLength;
        }

        throw new InvalidDataException($"glB asset '{assetPath}' does not contain a JSON chunk.");
    }

    private static byte[] TrimJsonPadding(byte[] jsonChunk)
    {
        int length = jsonChunk.Length;
        while (length > 0 && (jsonChunk[length - 1] == 0x20 || jsonChunk[length - 1] == 0x00))
            length--;

        if (length == jsonChunk.Length)
            return jsonChunk;

        byte[] trimmed = new byte[length];
        Array.Copy(jsonChunk, trimmed, length);
        return trimmed;
    }

    private static TextureSamplerDescription ToSamplerDescription(TextureSampler? sampler)
    {
        if (sampler == null)
            return TextureSamplerDescription.Default;

        return new TextureSamplerDescription(
            ToWrapMode(sampler.WrapS),
            ToWrapMode(sampler.WrapT),
            ToMinFilter(sampler.MinFilter),
            ToMagFilter(sampler.MagFilter),
            ToMipFilter(sampler.MinFilter),
            16f);
    }

    private static TextureWrapMode ToWrapMode(SharpGLTF.Schema2.TextureWrapMode wrapMode)
    {
        return wrapMode switch
        {
            SharpGLTF.Schema2.TextureWrapMode.CLAMP_TO_EDGE => TextureWrapMode.ClampToEdge,
            SharpGLTF.Schema2.TextureWrapMode.MIRRORED_REPEAT => TextureWrapMode.MirroredRepeat,
            _ => Njulf.Assets.TextureWrapMode.Repeat
        };
    }

    private static TextureFilterMode ToMinFilter(TextureMipMapFilter filter)
    {
        return filter switch
        {
            TextureMipMapFilter.NEAREST or TextureMipMapFilter.NEAREST_MIPMAP_NEAREST or TextureMipMapFilter.NEAREST_MIPMAP_LINEAR => TextureFilterMode.Nearest,
            _ => TextureFilterMode.Linear
        };
    }

    private static TextureFilterMode ToMagFilter(TextureInterpolationFilter filter)
    {
        return filter == TextureInterpolationFilter.NEAREST ? TextureFilterMode.Nearest : TextureFilterMode.Linear;
    }

    private static TextureMipFilterMode ToMipFilter(TextureMipMapFilter filter)
    {
        return filter switch
        {
            TextureMipMapFilter.NEAREST_MIPMAP_NEAREST or TextureMipMapFilter.LINEAR_MIPMAP_NEAREST => TextureMipFilterMode.Nearest,
            _ => TextureMipFilterMode.Linear
        };
    }

    private static TextureContainerKind ToTextureContainerKind(SharpGLTF.Memory.MemoryImage image)
    {
        return image.IsKtx2 ? TextureContainerKind.Ktx2 : TextureContainerKind.StandardImage;
    }

    private static float GetFactorOrDefault(MaterialChannel channel, string name, float fallback)
    {
        try
        {
            return channel.GetFactor(name);
        }
        catch (KeyNotFoundException)
        {
            return fallback;
        }
        catch (ArgumentException)
        {
            return fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static float GetParameterXOrDefault(MaterialChannel channel, float fallback)
    {
        foreach (IMaterialParameter parameter in channel.Parameters)
        {
            if (parameter.IsDefault)
                continue;

            if (parameter.Value is float value)
                return value;

            if (parameter.Value is NumericsVector4 vector && Math.Abs(vector.X) > float.Epsilon)
                return vector.X;
        }

        return fallback;
    }

    private sealed class SkinImportContext
    {
        public Dictionary<SchemaNode, int> AnimationJointByNode { get; } = new();
        public Dictionary<SchemaSkin, SchemaNode> RootNodeBySkin { get; } = new();
    }

    private static SkinImportContext ImportSkins(ModelRoot root, ModelMesh model, float globalScale)
    {
        var globalNodeMatrices = new Dictionary<SchemaNode, NumericsMatrix4x4>();
        var context = new SkinImportContext();
        foreach (SchemaSkin schemaSkin in root.LogicalSkins)
        {
            IReadOnlyList<SchemaNode> skeletonNodes = BuildSkeletonNodeList(schemaSkin);
            var skeletonIndexByNode = skeletonNodes
                .Select((joint, index) => new { joint, index })
                .ToDictionary(item => item.joint, item => item.index);

            var joints = new List<SkeletonJoint>(skeletonNodes.Count);
            for (int i = 0; i < skeletonNodes.Count; i++)
            {
                SchemaNode jointNode = skeletonNodes[i];
                int parentIndex = FindNearestAncestorJoint(jointNode.VisualParent, skeletonIndexByNode);

                CoreMatrix4x4 globalBind = ToCoreMatrixWithScaledTranslation(GetGlobalNodeMatrix(jointNode, globalNodeMatrices), globalScale);
                CoreMatrix4x4 localBind = parentIndex >= 0
                    ? globalBind * ToCoreMatrixWithScaledTranslation(GetGlobalNodeMatrix(skeletonNodes[parentIndex], globalNodeMatrices), globalScale).Invert()
                    : globalBind;

                joints.Add(new SkeletonJoint
                {
                    Name = string.IsNullOrWhiteSpace(jointNode.Name) ? $"Joint_{i}" : jointNode.Name,
                    ParentIndex = parentIndex,
                    LocalBindPose = ToAnimationTransform(localBind),
                    LocalBindTransform = localBind,
                    InverseBindMatrix = CoreMatrix4x4.Identity
                });
            }

            int rootJointIndex = schemaSkin.Skeleton != null && skeletonIndexByNode.TryGetValue(schemaSkin.Skeleton, out int skeletonRoot)
                ? skeletonRoot
                : joints.FindIndex(joint => joint.ParentIndex < 0);
            int transformRootIndex = joints.FindIndex(joint => joint.ParentIndex < 0);
            if (transformRootIndex >= 0)
                context.RootNodeBySkin[schemaSkin] = skeletonNodes[transformRootIndex];

            int[] skinJointIndices = new int[schemaSkin.Joints.Count];
            CoreMatrix4x4[] inverseBindMatrices = new CoreMatrix4x4[schemaSkin.Joints.Count];
            for (int i = 0; i < schemaSkin.Joints.Count; i++)
            {
                SchemaNode skinJoint = schemaSkin.Joints[i];
                skinJointIndices[i] = skeletonIndexByNode[skinJoint];
                inverseBindMatrices[i] = i < schemaSkin.InverseBindMatrices.Count
                    ? ToCoreMatrix(schemaSkin.InverseBindMatrices[i])
                    : CoreMatrix4x4.Identity;
            }

            var skeleton = new CoreSkeleton
            {
                Name = string.IsNullOrWhiteSpace(schemaSkin.Name) ? $"SharpGltfSkeleton_{schemaSkin.LogicalIndex}" : schemaSkin.Name,
                Joints = joints,
                RootJointIndex = rootJointIndex
            };

            model.Skeletons.Add(skeleton);
            model.Skins.Add(new CoreSkin
            {
                Name = string.IsNullOrWhiteSpace(schemaSkin.Name) ? $"SharpGltfSkin_{schemaSkin.LogicalIndex}" : schemaSkin.Name,
                Skeleton = skeleton,
                JointIndices = skinJointIndices,
                InverseBindMatrices = inverseBindMatrices
            });

            foreach (KeyValuePair<SchemaNode, int> joint in skeletonIndexByNode)
                context.AnimationJointByNode.TryAdd(joint.Key, joint.Value);
        }

        return context;
    }

    private static IReadOnlyList<SchemaNode> BuildSkeletonNodeList(SchemaSkin schemaSkin)
    {
        var nodes = new List<SchemaNode>();
        var added = new HashSet<SchemaNode>();

        void AddWithAncestors(SchemaNode node)
        {
            if (added.Contains(node))
                return;

            if (node.VisualParent != null)
                AddWithAncestors(node.VisualParent);

            added.Add(node);
            nodes.Add(node);
        }

        foreach (SchemaNode joint in schemaSkin.Joints)
            AddWithAncestors(joint);

        return nodes;
    }

    private static int FindNearestAncestorJoint(SchemaNode? node, IReadOnlyDictionary<SchemaNode, int> jointIndexByNode)
    {
        while (node != null)
        {
            if (jointIndexByNode.TryGetValue(node, out int jointIndex))
                return jointIndex;

            node = node.VisualParent;
        }

        return -1;
    }

    private static NumericsMatrix4x4 GetGlobalNodeMatrix(
        SchemaNode node,
        IDictionary<SchemaNode, NumericsMatrix4x4> cache)
    {
        if (cache.TryGetValue(node, out NumericsMatrix4x4 cached))
            return cached;

        NumericsMatrix4x4 global = node.VisualParent == null
            ? node.LocalMatrix
            : node.LocalMatrix * GetGlobalNodeMatrix(node.VisualParent, cache);
        cache[node] = global;
        return global;
    }

    private static void ImportAnimations(
        ModelRoot root,
        ModelMesh model,
        float globalScale,
        IReadOnlyDictionary<SchemaNode, int> jointByNode)
    {
        if (model.Skeletons.Count == 0)
            return;

        if (jointByNode.Count == 0)
            return;

        foreach (SharpGLTF.Schema2.Animation animation in root.LogicalAnimations)
        {
            var channels = new List<CoreAnimationChannel>();
            foreach (SharpGLTF.Schema2.AnimationChannel channel in animation.Channels)
            {
                SchemaNode? targetNode = channel.TargetNode;
                if (targetNode == null || !jointByNode.TryGetValue(targetNode, out int targetJoint))
                    continue;

                switch (channel.TargetNodePath)
                {
                    case PropertyPath.translation:
                        AddVector3Channel(
                            channels,
                            channel.GetTranslationSampler(),
                            targetNode.LogicalIndex,
                            targetJoint,
                            AnimationChannelPath.Translation,
                            globalScale);
                        break;

                    case PropertyPath.rotation:
                        AddQuaternionChannel(
                            channels,
                            channel.GetRotationSampler(),
                            targetNode.LogicalIndex,
                            targetJoint);
                        break;

                    case PropertyPath.scale:
                        AddVector3Channel(
                            channels,
                            channel.GetScaleSampler(),
                            targetNode.LogicalIndex,
                            targetJoint,
                            AnimationChannelPath.Scale,
                            1f);
                        break;
                }
            }

            CompleteJointTrsChannels(channels, model);
            model.AnimationClips.Add(new AnimationClip
            {
                Name = string.IsNullOrWhiteSpace(animation.Name) ? $"Animation_{animation.LogicalIndex}" : animation.Name,
                DurationSeconds = channels
                    .SelectMany(imported => imported.Sampler.InputTimes)
                    .DefaultIfEmpty(animation.Duration)
                    .Max(),
                Channels = channels
            });
        }
    }

    private static void CompleteJointTrsChannels(List<CoreAnimationChannel> channels, ModelMesh model)
    {
        if (model.Skeletons.Count == 0)
            return;

        CoreSkeleton skeleton = model.Skeletons[0];
        foreach (IGrouping<int, CoreAnimationChannel> group in channels
                     .Where(channel => channel.TargetJointIndex >= 0 && channel.TargetJointIndex < skeleton.Joints.Count)
                     .GroupBy(channel => channel.TargetJointIndex)
                     .ToArray())
        {
            AnimationTransform bindPose = skeleton.Joints[group.Key].LocalBindPose;
            float[] times = group
                .SelectMany(channel => channel.Sampler.InputTimes)
                .Distinct()
                .OrderBy(time => time)
                .ToArray();
            if (times.Length == 0)
                times = [0f];

            int targetNodeIndex = group.First().TargetNodeIndex;
            if (!group.Any(channel => channel.Path == AnimationChannelPath.Translation))
            {
                channels.Add(CreateChannel(
                    targetNodeIndex,
                    group.Key,
                    AnimationChannelPath.Translation,
                    times,
                    times.Select(_ => new CoreVector4(bindPose.Translation.X, bindPose.Translation.Y, bindPose.Translation.Z, 0f)).ToArray(),
                    AnimationInterpolation.Linear));
            }

            if (!group.Any(channel => channel.Path == AnimationChannelPath.Rotation))
            {
                channels.Add(CreateChannel(
                    targetNodeIndex,
                    group.Key,
                    AnimationChannelPath.Rotation,
                    times,
                    times.Select(_ => new CoreVector4(bindPose.Rotation.X, bindPose.Rotation.Y, bindPose.Rotation.Z, bindPose.Rotation.W)).ToArray(),
                    AnimationInterpolation.Linear));
            }

            if (!group.Any(channel => channel.Path == AnimationChannelPath.Scale))
            {
                channels.Add(CreateChannel(
                    targetNodeIndex,
                    group.Key,
                    AnimationChannelPath.Scale,
                    times,
                    times.Select(_ => new CoreVector4(bindPose.Scale.X, bindPose.Scale.Y, bindPose.Scale.Z, 0f)).ToArray(),
                    AnimationInterpolation.Linear));
            }
        }
    }

    private static void AddVector3Channel(
        List<CoreAnimationChannel> channels,
        IAnimationSampler<NumericsVector3> sampler,
        int targetNodeIndex,
        int targetJoint,
        AnimationChannelPath path,
        float valueScale)
    {
        if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            IReadOnlyList<(float Time, (NumericsVector3 TangentIn, NumericsVector3 Value, NumericsVector3 TangentOut) Key)> keys = sampler.GetCubicKeys().ToArray();
            channels.Add(CreateChannel(
                targetNodeIndex,
                targetJoint,
                path,
                keys.Select(key => key.Time).ToArray(),
                keys.SelectMany(key => new[]
                {
                    ToVector4(key.Key.TangentIn * valueScale, 0f),
                    ToVector4(key.Key.Value * valueScale, 0f),
                    ToVector4(key.Key.TangentOut * valueScale, 0f)
                }).ToArray(),
                ToCoreInterpolation(sampler.InterpolationMode)));
            return;
        }

        IReadOnlyList<(float Time, NumericsVector3 Value)> linearKeys = sampler.GetLinearKeys().ToArray();
        channels.Add(CreateChannel(
            targetNodeIndex,
            targetJoint,
            path,
            linearKeys.Select(key => key.Time).ToArray(),
            linearKeys.Select(key => ToVector4(key.Value * valueScale, 0f)).ToArray(),
            ToCoreInterpolation(sampler.InterpolationMode)));
    }

    private static void AddQuaternionChannel(
        List<CoreAnimationChannel> channels,
        IAnimationSampler<NumericsQuaternion> sampler,
        int targetNodeIndex,
        int targetJoint)
    {
        if (sampler.InterpolationMode == AnimationInterpolationMode.CUBICSPLINE)
        {
            IReadOnlyList<(float Time, (NumericsQuaternion TangentIn, NumericsQuaternion Value, NumericsQuaternion TangentOut) Key)> keys = sampler.GetCubicKeys().ToArray();
            channels.Add(CreateChannel(
                targetNodeIndex,
                targetJoint,
                AnimationChannelPath.Rotation,
                keys.Select(key => key.Time).ToArray(),
                keys.SelectMany(key => new[]
                {
                    ToVector4(ToCoreRotationConvention(key.Key.TangentIn)),
                    ToVector4(ToCoreRotationConvention(key.Key.Value)),
                    ToVector4(ToCoreRotationConvention(key.Key.TangentOut))
                }).ToArray(),
                ToCoreInterpolation(sampler.InterpolationMode)));
            return;
        }

        IReadOnlyList<(float Time, NumericsQuaternion Value)> linearKeys = sampler.GetLinearKeys().ToArray();
        channels.Add(CreateChannel(
            targetNodeIndex,
            targetJoint,
            AnimationChannelPath.Rotation,
            linearKeys.Select(key => key.Time).ToArray(),
            linearKeys.Select(key => ToVector4(ToCoreRotationConvention(key.Value))).ToArray(),
            ToCoreInterpolation(sampler.InterpolationMode)));
    }

    private static NumericsQuaternion ToCoreRotationConvention(NumericsQuaternion value)
    {
        value = NumericsQuaternion.Normalize(value);
        return new NumericsQuaternion(-value.X, -value.Y, -value.Z, value.W);
    }

    private static CoreAnimationChannel CreateChannel(
        int targetNodeIndex,
        int targetJoint,
        AnimationChannelPath path,
        float[] times,
        CoreVector4[] values,
        AnimationInterpolation interpolation)
    {
        return new CoreAnimationChannel
        {
            TargetNodeIndex = targetNodeIndex,
            TargetJointIndex = targetJoint,
            Path = path,
            Sampler = new AnimationSampler
            {
                InputTimes = times,
                OutputValues = values,
                Interpolation = interpolation
            }
        };
    }

    private static AnimationInterpolation ToCoreInterpolation(AnimationInterpolationMode mode)
    {
        return mode switch
        {
            AnimationInterpolationMode.STEP => AnimationInterpolation.Step,
            AnimationInterpolationMode.CUBICSPLINE => AnimationInterpolation.CubicSpline,
            _ => AnimationInterpolation.Linear
        };
    }

    private static ModelAnimationImportDiagnostics CreateAnimationDiagnostics(ModelMesh model)
    {
        return new ModelAnimationImportDiagnostics(
            skeletonCount: model.Skeletons.Count,
            jointCount: model.Skeletons.Sum(skeleton => skeleton.Joints.Count),
            skinCount: model.Skins.Count,
            skinnedSubMeshCount: model.SubMeshes.Count(subMesh => subMesh.SkinIndex >= 0),
            animationClipCount: model.AnimationClips.Count,
            animationChannelCount: model.AnimationClips.Sum(clip => clip.Channels.Count),
            unsupportedInterpolationCount: 0,
            maxInfluencesPerVertex: model.JointWeights0.Length > 0 ? 4 : 0);
    }

    private static void AppendNodeMesh(
        ModelMesh model,
        SchemaNode node,
        int nodeIndex,
        SchemaMesh mesh,
        ImporterOptions options,
        SkinImportContext skinContext,
        List<CoreVector3> vertices,
        List<CoreVector3> normals,
        List<CoreVector3> tangents,
        List<CoreVector3> bitangents,
        List<CoreVector2> texCoords,
        List<CoreVector2> texCoords1,
        List<CoreVector4> vertexColors,
        List<VertexJointIndices> jointIndices,
        List<VertexJointWeights> jointWeights,
        List<uint> indices)
    {
        var decodedMesh = MeshDecoder.Decode(mesh, new RuntimeOptions());
        IMeshPrimitiveDecoder[] decodedPrimitives = decodedMesh.Primitives.ToArray();
        NumericsMatrix4x4 nodeWorld = node.WorldMatrix;
        bool isSkinned = node.Skin != null;
        CoreMatrix4x4 skinningBindTransform = CreateSkinningBindTransform(node, nodeWorld, skinContext, options.GlobalScale);
        NumericsMatrix4x4 vertexTransform = isSkinned ? NumericsMatrix4x4.Identity : nodeWorld;
        NumericsMatrix4x4 normalTransform = NumericsMatrix4x4.Invert(vertexTransform, out NumericsMatrix4x4 inverse)
            ? NumericsMatrix4x4.Transpose(inverse)
            : vertexTransform;

        for (int primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
        {
            MeshPrimitive sourcePrimitive = mesh.Primitives[primitiveIndex];
            if (sourcePrimitive.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                throw new NotSupportedException($"SharpGLTF importer currently supports TRIANGLES primitives only. Mesh {mesh.LogicalIndex}, primitive {primitiveIndex} uses {sourcePrimitive.DrawPrimitiveType}.");

            IMeshPrimitiveDecoder primitive = decodedPrimitives[primitiveIndex];
            if (primitive.VertexCount == 0)
                continue;

            int baseVertex = vertices.Count;
            var subVertices = new List<CoreVector3>(primitive.VertexCount);
            var subNormals = new List<CoreVector3>(primitive.VertexCount);
            var subTangents = new List<CoreVector3>(primitive.VertexCount);
            var subBitangents = new List<CoreVector3>(primitive.VertexCount);
            var subTexCoords = new List<CoreVector2>(primitive.VertexCount);
            var subTexCoords1 = new List<CoreVector2>(primitive.VertexCount);
            var subVertexColors = new List<CoreVector4>(primitive.VertexCount);
            var subJointIndices = isSkinned ? new VertexJointIndices[primitive.VertexCount] : Array.Empty<VertexJointIndices>();
            var subJointWeights = isSkinned ? new VertexJointWeights[primitive.VertexCount] : Array.Empty<VertexJointWeights>();
            var subIndices = new List<uint>();

            for (int vertexIndex = 0; vertexIndex < primitive.VertexCount; vertexIndex++)
            {
                CoreVector3 position = TransformPosition(ToCoreVector(primitive.GetPosition(vertexIndex)), vertexTransform, options.GlobalScale);
                CoreVector3 normal = NormalizeOrDefault(TransformDirection(SafeGetNormal(primitive, vertexIndex), normalTransform));
                CoreVector4 tangentSource = SafeGetTangent(primitive, vertexIndex);
                CoreVector3 tangent = NormalizeOrDefault(TransformDirection(new CoreVector3(tangentSource.X, tangentSource.Y, tangentSource.Z), vertexTransform));
                CoreVector3 bitangent = NormalizeOrDefault(CoreVector3.Cross(normal, tangent) * tangentSource.W);
                CoreVector2 uv0 = primitive.TexCoordsCount > 0 ? ToCoreVector(primitive.GetTextureCoord(vertexIndex, 0)) : CoreVector2.Zero;
                CoreVector2 uv1 = primitive.TexCoordsCount > 1 ? ToCoreVector(primitive.GetTextureCoord(vertexIndex, 1)) : CoreVector2.Zero;
                CoreVector4 color = primitive.ColorsCount > 0 ? ToCoreVector(primitive.GetColor(vertexIndex, 0)) : new CoreVector4(1f, 1f, 1f, 1f);

                vertices.Add(position);
                normals.Add(normal);
                tangents.Add(tangent);
                bitangents.Add(bitangent);
                texCoords.Add(uv0);
                texCoords1.Add(uv1);
                vertexColors.Add(color);
                jointIndices.Add(default);
                jointWeights.Add(default);

                subVertices.Add(position);
                subNormals.Add(normal);
                subTangents.Add(tangent);
                subBitangents.Add(bitangent);
                subTexCoords.Add(uv0);
                subTexCoords1.Add(uv1);
                subVertexColors.Add(color);

                if (isSkinned && primitive.JointsWeightsCount > 0)
                {
                    ToJointStreams(primitive.GetSkinWeights(vertexIndex), out VertexJointIndices joints, out VertexJointWeights weights);
                    jointIndices[^1] = joints;
                    jointWeights[^1] = weights;
                    subJointIndices[vertexIndex] = joints;
                    subJointWeights[vertexIndex] = weights;
                }
            }

            foreach ((int A, int B, int C) triangle in primitive.TriangleIndices)
            {
                if (options.FlipWindingOrder)
                {
                    indices.Add((uint)(baseVertex + triangle.C));
                    indices.Add((uint)(baseVertex + triangle.B));
                    indices.Add((uint)(baseVertex + triangle.A));
                    subIndices.Add((uint)triangle.C);
                    subIndices.Add((uint)triangle.B);
                    subIndices.Add((uint)triangle.A);
                }
                else
                {
                    indices.Add((uint)(baseVertex + triangle.A));
                    indices.Add((uint)(baseVertex + triangle.B));
                    indices.Add((uint)(baseVertex + triangle.C));
                    subIndices.Add((uint)triangle.A);
                    subIndices.Add((uint)triangle.B);
                    subIndices.Add((uint)triangle.C);
                }
            }

            if (subIndices.Count == 0)
                continue;

            var subMesh = new ModelSubMesh
            {
                Name = CreateSubMeshName(model.Name, node, mesh, primitiveIndex),
                MaterialIndex = sourcePrimitive.Material != null && sourcePrimitive.Material.LogicalIndex < model.Materials.Count
                    ? sourcePrimitive.Material.LogicalIndex
                    : 0,
                NodeIndex = nodeIndex,
                SkinIndex = node.Skin != null ? node.Skin.LogicalIndex : -1,
                SkinningBindTransform = skinningBindTransform,
                Vertices = subVertices.ToArray(),
                Normals = subNormals.ToArray(),
                Tangents = subTangents.ToArray(),
                Bitangents = subBitangents.ToArray(),
                TexCoords = subTexCoords.ToArray(),
                TexCoords1 = subTexCoords1.ToArray(),
                VertexColors = subVertexColors.ToArray(),
                JointIndices0 = subJointIndices,
                JointWeights0 = subJointWeights,
                Indices = subIndices.ToArray()
            };

            ComputeBoundingVolume(subVertices, out CoreBoundingBox subBox, out CoreBoundingSphere subSphere);
            subMesh.BoundingBox = subBox;
            subMesh.BoundingSphere = subSphere;
            model.SubMeshes.Add(subMesh);
        }
    }

    private static CoreMatrix4x4 CreateSkinningBindTransform(
        SchemaNode node,
        NumericsMatrix4x4 nodeWorld,
        SkinImportContext skinContext,
        float globalScale)
    {
        if (node.Skin == null)
            return CoreMatrix4x4.Identity;

        CoreMatrix4x4 meshWorld = ToCoreMatrixWithScaledTranslation(nodeWorld, globalScale);
        if (!skinContext.RootNodeBySkin.TryGetValue(node.Skin, out SchemaNode? rootNode))
            return meshWorld;

        CoreMatrix4x4 skeletonRootWorld = ToCoreMatrixWithScaledTranslation(rootNode.WorldMatrix, globalScale);
        return meshWorld * skeletonRootWorld.Invert();
    }

    private static IEnumerable<SchemaNode> EnumerateSceneNodes(Scene scene)
    {
        var visited = new HashSet<int>();
        foreach (SchemaNode node in scene.VisualChildren)
        {
            foreach (SchemaNode child in EnumerateNode(node, visited))
                yield return child;
        }
    }

    private static IEnumerable<SchemaNode> EnumerateNode(SchemaNode node, HashSet<int> visited)
    {
        if (!visited.Add(node.LogicalIndex))
            yield break;

        yield return node;
        foreach (SchemaNode child in node.VisualChildren)
        {
            foreach (SchemaNode descendant in EnumerateNode(child, visited))
                yield return descendant;
        }
    }

    private static string CreateSubMeshName(string modelName, SchemaNode node, SchemaMesh mesh, int primitiveIndex)
    {
        if (!string.IsNullOrWhiteSpace(node.Name))
            return node.Name;
        if (!string.IsNullOrWhiteSpace(mesh.Name))
            return mesh.Primitives.Count > 1 ? $"{mesh.Name}_{primitiveIndex}" : mesh.Name;

        return $"{modelName}_mesh_{mesh.LogicalIndex}_{primitiveIndex}";
    }

    private static CoreVector3 SafeGetNormal(IMeshPrimitiveDecoder primitive, int vertexIndex)
    {
        try
        {
            return ToCoreVector(primitive.GetNormal(vertexIndex));
        }
        catch (InvalidOperationException)
        {
            return CoreVector3.Zero;
        }
    }

    private static CoreVector4 SafeGetTangent(IMeshPrimitiveDecoder primitive, int vertexIndex)
    {
        try
        {
            return ToCoreVector(primitive.GetTangent(vertexIndex));
        }
        catch (InvalidOperationException)
        {
            return new CoreVector4(0f, 0f, 0f, 1f);
        }
    }

    private static void ToJointStreams(SparseWeight8 sparse, out VertexJointIndices joints, out VertexJointWeights weights)
    {
        SparseWeight8 trimmed = sparse.GetTrimmed(4).GetNormalized();
        (int Index, float Weight)[] influences = trimmed.GetIndexedWeights()
            .OrderByDescending(item => item.Weight)
            .Take(4)
            .ToArray();

        Array.Resize(ref influences, 4);
        joints = new VertexJointIndices(
            checked((ushort)influences[0].Index),
            checked((ushort)influences[1].Index),
            checked((ushort)influences[2].Index),
            checked((ushort)influences[3].Index));
        weights = new VertexJointWeights(
            influences[0].Weight,
            influences[1].Weight,
            influences[2].Weight,
            influences[3].Weight).Normalized();
    }

    private static AnimationTransform ToAnimationTransform(AffineTransform transform, float globalScale)
    {
        AffineTransform decomposed = transform.GetDecomposed();
        return new AnimationTransform(
            ToCoreVector(decomposed.Translation) * globalScale,
            ToCoreQuaternion(decomposed.Rotation),
            ToCoreVector(decomposed.Scale));
    }

    private static AnimationTransform ToAnimationTransform(CoreMatrix4x4 transform)
    {
        var source = new NumericsMatrix4x4(
            transform.M11, transform.M12, transform.M13, transform.M14,
            transform.M21, transform.M22, transform.M23, transform.M24,
            transform.M31, transform.M32, transform.M33, transform.M34,
            transform.M41, transform.M42, transform.M43, transform.M44);

        if (!NumericsMatrix4x4.Decompose(
                source,
                out NumericsVector3 scale,
                out NumericsQuaternion rotation,
                out NumericsVector3 translation))
        {
            return new AnimationTransform(transform.Translation, CoreQuaternion.Identity, transform.Scale);
        }

        return new AnimationTransform(
            ToCoreVector(translation),
            ToCoreQuaternion(rotation),
            ToCoreVector(scale));
    }

    private static CoreVector3 TransformPosition(CoreVector3 position, NumericsMatrix4x4 transform, float globalScale)
    {
        return new CoreVector3(
            (position.X * transform.M11 + position.Y * transform.M21 + position.Z * transform.M31 + transform.M41) * globalScale,
            (position.X * transform.M12 + position.Y * transform.M22 + position.Z * transform.M32 + transform.M42) * globalScale,
            (position.X * transform.M13 + position.Y * transform.M23 + position.Z * transform.M33 + transform.M43) * globalScale);
    }

    private static CoreVector3 TransformDirection(CoreVector3 direction, NumericsMatrix4x4 transform)
    {
        return new CoreVector3(
            direction.X * transform.M11 + direction.Y * transform.M21 + direction.Z * transform.M31,
            direction.X * transform.M12 + direction.Y * transform.M22 + direction.Z * transform.M32,
            direction.X * transform.M13 + direction.Y * transform.M23 + direction.Z * transform.M33);
    }

    private static CoreVector3 NormalizeOrDefault(CoreVector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared <= float.Epsilon
            ? CoreVector3.Zero
            : value / (float)Math.Sqrt(lengthSquared);
    }

    private static CoreVector2 ToCoreVector(NumericsVector2 value) => new(value.X, value.Y);
    private static CoreVector3 ToCoreVector(NumericsVector3 value) => new(value.X, value.Y, value.Z);
    private static CoreVector4 ToCoreVector(NumericsVector4 value) => new(value.X, value.Y, value.Z, value.W);
    private static CoreQuaternion ToCoreQuaternion(NumericsQuaternion value) => new(-value.X, -value.Y, -value.Z, value.W);
    private static CoreVector4 ToVector4(NumericsVector3 value, float w) => new(value.X, value.Y, value.Z, w);
    private static CoreVector4 ToVector4(NumericsQuaternion value) => new(value.X, value.Y, value.Z, value.W);

    private static CoreMatrix4x4 ToCoreMatrix(NumericsMatrix4x4 matrix)
    {
        return new CoreMatrix4x4(
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44);
    }

    private static CoreMatrix4x4 ToCoreMatrixWithScaledTranslation(NumericsMatrix4x4 matrix, float globalScale)
    {
        CoreMatrix4x4 result = ToCoreMatrix(matrix);
        result.M41 *= globalScale;
        result.M42 *= globalScale;
        result.M43 *= globalScale;
        return result;
    }

    private static void ComputeBoundingVolume(List<CoreVector3> vertices, out CoreBoundingBox bbox, out CoreBoundingSphere bsphere)
    {
        if (vertices.Count == 0)
        {
            bbox = new CoreBoundingBox();
            bsphere = new CoreBoundingSphere();
            return;
        }

        var min = new CoreVector3(float.MaxValue);
        var max = new CoreVector3(float.MinValue);

        foreach (CoreVector3 vertex in vertices)
        {
            min.X = Math.Min(min.X, vertex.X);
            min.Y = Math.Min(min.Y, vertex.Y);
            min.Z = Math.Min(min.Z, vertex.Z);
            max.X = Math.Max(max.X, vertex.X);
            max.Y = Math.Max(max.Y, vertex.Y);
            max.Z = Math.Max(max.Z, vertex.Z);
        }

        bbox = new CoreBoundingBox(min, max);
        bsphere = CoreBoundingSphere.FromBox(bbox);
    }

    private readonly record struct GltfImageSourceHint(
        TextureSourceKind SourceKind,
        int EncodedByteLength,
        string CacheIdentity);
}
