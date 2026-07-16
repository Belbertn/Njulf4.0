using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Geometry;

namespace Njulf.Assets.Cooked;

public static class CookedPackage
{
    private const CookedSectionFlags Required = CookedSectionFlags.Required;

    public static void WriteModel(string path, CookedModelManifest manifest, uint toolVersion = 1)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Model, manifest.SourceHash, manifest.ImportSettingsHash, manifest.DependencyListHash, toolVersion);
        writer.WriteSection(CookedSectionIds.Manifest, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(manifest));
        writer.Complete();
    }

    public static void WriteMesh(string path, CookedMeshPayload mesh, ulong sourceHash, ulong settingsHash, ulong dependencyHash, uint toolVersion = 1, bool useMeshOptimizer = true)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Mesh, sourceHash, settingsHash, dependencyHash, toolVersion);
        writer.WriteSection(CookedSectionIds.SubMeshes, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(mesh.SubMeshes));
        WriteVertexSection(writer, CookedSectionIds.VertexPositions, Required, mesh.VertexPositions, useMeshOptimizer);
        WriteVertexSection(writer, CookedSectionIds.VertexNormals, Required, mesh.VertexNormalTangents, useMeshOptimizer);
        WriteVertexSection(writer, CookedSectionIds.VertexUvColors, Required, mesh.VertexUvColors, useMeshOptimizer);
        if (mesh.VertexSkinning.Length > 0)
            WriteVertexSection(writer, CookedSectionIds.VertexSkinning, CookedSectionFlags.None, mesh.VertexSkinning, useMeshOptimizer);
        // Submeshes are concatenated while retaining local indices, so this is an arbitrary
        // index sequence rather than one globally indexed triangle list.
        WriteIndexSequenceSection(writer, CookedSectionIds.Indices, Required, mesh.Indices, mesh.VertexPositions.Length, useMeshOptimizer);
        WriteVertexSection(writer, CookedSectionIds.Meshlets0, Required, mesh.MeshletsLod0, useMeshOptimizer);
        if (mesh.MeshletsLod1.Length > 0)
            WriteVertexSection(writer, CookedSectionIds.Meshlets1, CookedSectionFlags.None, mesh.MeshletsLod1, useMeshOptimizer);
        if (mesh.MeshletsLod2.Length > 0)
            WriteVertexSection(writer, CookedSectionIds.Meshlets2, CookedSectionFlags.None, mesh.MeshletsLod2, useMeshOptimizer);
        WriteIndexSequenceSection(writer, CookedSectionIds.MeshletVertices, Required, mesh.MeshletVertices, mesh.VertexPositions.Length, useMeshOptimizer);
        WriteIndexSequenceSection(writer, CookedSectionIds.MeshletTriangles, Required, mesh.MeshletTriangles, RendererMeshletLodBuilder.MaxVerticesPerMeshlet, useMeshOptimizer);
        writer.Complete();
    }

    private static void WriteVertexSection<T>(CookedAssetWriter writer, uint id, CookedSectionFlags flags, ReadOnlySpan<T> data, bool useMeshOptimizer)
        where T : unmanaged
    {
        if (useMeshOptimizer)
            writer.WriteMeshoptVertexSection(id, flags, data);
        else
            writer.WriteSection(id, flags | CookedSectionFlags.Zstd, data);
    }

    private static void WriteIndexSequenceSection(CookedAssetWriter writer, uint id, CookedSectionFlags flags, ReadOnlySpan<uint> data, int vertexCount, bool useMeshOptimizer)
    {
        if (useMeshOptimizer)
            writer.WriteMeshoptIndexSequenceSection(id, flags, data, vertexCount);
        else
            writer.WriteSection(id, flags | CookedSectionFlags.Zstd, data);
    }

    public static void WriteMaterials(string path, CookedMaterialTable materials, ulong sourceHash, ulong settingsHash, ulong dependencyHash, uint toolVersion = 1)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Material, sourceHash, settingsHash, dependencyHash, toolVersion);
        writer.WriteSection(CookedSectionIds.Materials, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(materials));
        writer.Complete();
    }

    public static void WriteAnimation(string path, CookedAnimationPayload animation, ulong sourceHash, ulong settingsHash, ulong dependencyHash, uint toolVersion = 1)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Animation, sourceHash, settingsHash, dependencyHash, toolVersion);
        writer.WriteSection(CookedSectionIds.Animation, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(animation));
        writer.Complete();
    }

    public static void WriteTextureMeta(string path, CookedTextureMeta texture, uint toolVersion = 1)
    {
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Texture, texture.SourceHash, 0, 0, toolVersion);
        writer.WriteSection(CookedSectionIds.Metadata, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(texture));
        writer.Complete();
    }

    public static CookedModelAsset LoadModel(string modelPath, CookedAssetReaderFlags flags = CookedAssetReaderFlags.None, ulong? expectedSourceHash = null)
    {
        modelPath = Path.GetFullPath(modelPath);
        VerifySignatureIfRequired(modelPath, flags);
        CookedModelManifest manifest;
        long bytesRead;
        using (var reader = new CookedAssetReader(modelPath, CookedAssetKind.Model, flags, expectedSourceHash))
        {
            manifest = CookedJson.Deserialize<CookedModelManifest>(reader.GetRequiredSection(CookedSectionIds.Manifest).Span, modelPath, "manifest");
            bytesRead = reader.BytesRead;
        }

        string directory = Path.GetDirectoryName(modelPath)!;
        string packageRoot = Path.GetFullPath(Path.Combine(directory, ".."));
        bool verifyWholeFileHashes = flags.HasFlag(CookedAssetReaderFlags.StrictSourceHash);
        string meshPath = ResolveReference(directory, packageRoot, manifest.Mesh.RelativePath, manifest.Mesh.ContentHash, verifyWholeFileHashes);
        string materialPath = ResolveReference(directory, packageRoot, manifest.Material.RelativePath, manifest.Material.ContentHash, verifyWholeFileHashes);
        VerifySignatureIfRequired(meshPath, flags);
        VerifySignatureIfRequired(materialPath, flags);
        CookedMeshPayload mesh = LoadMesh(meshPath, flags, out long meshBytes);
        CookedMaterialTable materials = LoadMaterials(materialPath, flags, out long materialBytes);
        RebaseMaterialTexturePaths(materials, Path.GetDirectoryName(materialPath)!);
        CookedAnimationPayload animation = new(Array.Empty<Core.Animation.Skeleton>(), Array.Empty<Core.Animation.Skin>(), Array.Empty<Core.Animation.AnimationClip>());
        long animationBytes = 0;
        if (manifest.Animation is not null)
        {
            string animationPath = ResolveReference(directory, packageRoot, manifest.Animation.RelativePath, manifest.Animation.ContentHash, verifyWholeFileHashes);
            VerifySignatureIfRequired(animationPath, flags);
            animation = LoadAnimation(animationPath, flags, out animationBytes);
        }
        return new CookedModelAsset(manifest, mesh, materials, animation, modelPath, bytesRead + meshBytes + materialBytes + animationBytes);
    }

    private static void VerifySignatureIfRequired(string path, CookedAssetReaderFlags flags)
    {
        if (flags.HasFlag(CookedAssetReaderFlags.RequireSignature))
            CookedPackageSigner.VerifyRequired(path);
    }

    public static CookedMeshPayload LoadMesh(string path, CookedAssetReaderFlags flags, out long bytesRead)
    {
        using var reader = new CookedAssetReader(path, CookedAssetKind.Mesh, flags);
        var subMeshes = CookedJson.Deserialize<CookedSubMeshRecord[]>(reader.GetRequiredSection(CookedSectionIds.SubMeshes).Span, path, "submesh");
        var positions = reader.ReadSection<CookedVertexPositionStream>(CookedSectionIds.VertexPositions);
        var normals = reader.ReadSection<CookedVertexNormalTangentStream>(CookedSectionIds.VertexNormals);
        var uvColors = reader.ReadSection<CookedVertexUvColorStream>(CookedSectionIds.VertexUvColors);
        _ = reader.TryReadSection(CookedSectionIds.VertexSkinning, out CookedVertexSkinningData[] skinning);
        var indices = reader.ReadSection<uint>(CookedSectionIds.Indices);
        var lod0 = reader.ReadSection<Meshlet>(CookedSectionIds.Meshlets0);
        _ = reader.TryReadSection(CookedSectionIds.Meshlets1, out Meshlet[] lod1);
        _ = reader.TryReadSection(CookedSectionIds.Meshlets2, out Meshlet[] lod2);
        var meshletVertices = reader.ReadSection<uint>(CookedSectionIds.MeshletVertices);
        var meshletTriangles = reader.ReadSection<uint>(CookedSectionIds.MeshletTriangles);
        bytesRead = reader.BytesRead;
        ValidateMeshRanges(path, subMeshes, positions.Length, indices.Length, skinning.Length, lod0.Length, lod1.Length, lod2.Length, meshletVertices.Length, meshletTriangles.Length);
        return new CookedMeshPayload(subMeshes, positions, normals, uvColors, skinning, indices, lod0, lod1, lod2, meshletVertices, meshletTriangles);
    }

    public static CookedMaterialTable LoadMaterials(string path, CookedAssetReaderFlags flags, out long bytesRead)
    {
        using var reader = new CookedAssetReader(path, CookedAssetKind.Material, flags);
        CookedMaterialTable result = CookedJson.Deserialize<CookedMaterialTable>(reader.GetRequiredSection(CookedSectionIds.Materials).Span, path, "materials");
        bytesRead = reader.BytesRead;
        return result;
    }

    public static CookedAnimationPayload LoadAnimation(string path, CookedAssetReaderFlags flags, out long bytesRead)
    {
        using var reader = new CookedAssetReader(path, CookedAssetKind.Animation, flags);
        CookedAnimationPayload result = CookedJson.Deserialize<CookedAnimationPayload>(reader.GetRequiredSection(CookedSectionIds.Animation).Span, path, "animation");
        bytesRead = reader.BytesRead;
        return result;
    }

    public static CookedTextureMeta LoadTextureMeta(string path, CookedAssetReaderFlags flags = CookedAssetReaderFlags.None)
    {
        using var reader = new CookedAssetReader(path, CookedAssetKind.Texture, flags);
        return CookedJson.Deserialize<CookedTextureMeta>(reader.GetRequiredSection(CookedSectionIds.Metadata).Span, path, "texture metadata");
    }

    public static Guid StableAssetId(string canonicalSourcePath)
    {
        string canonical = File.Exists(canonicalSourcePath)
            ? Path.GetFullPath(canonicalSourcePath).Replace('\\', '/').ToUpperInvariant()
            : canonicalSourcePath.Replace('\\', '/');
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static string ResolveReference(string baseDirectory, string packageRoot, string relativePath, ulong expectedHash, bool verifyHash)
    {
        if (Path.IsPathRooted(relativePath))
            throw new CookedAssetFormatException(baseDirectory, $"package reference '{relativePath}' must be relative");
        string path = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        string normalizedRelative = Path.GetRelativePath(packageRoot, path);
        if (normalizedRelative == ".." || normalizedRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CookedAssetFormatException(baseDirectory, $"package reference '{relativePath}' escapes the cooked package root");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Cooked package dependency '{relativePath}' was not found for '{baseDirectory}'.", path);
        if (verifyHash)
        {
            ulong actualHash = CookedHash.File(path);
            if (actualHash != expectedHash)
                throw new CookedAssetHashException(path, $"package reference expected 0x{expectedHash:x16}, got 0x{actualHash:x16}");
        }
        return path;
    }

    private static void ValidateMeshRanges(string path, IReadOnlyList<CookedSubMeshRecord> meshes, int vertices, int indices, int skinning, int meshletsLod0, int meshletsLod1, int meshletsLod2, int meshletVertices, int meshletTriangles)
    {
        foreach (CookedSubMeshRecord mesh in meshes)
        {
            ValidateRange(path, mesh.Name, "vertices", mesh.VertexOffset, mesh.VertexCount, vertices);
            ValidateRange(path, mesh.Name, "indices", mesh.IndexOffset, mesh.IndexCount, indices);
            ValidateRange(path, mesh.Name, "skinning", mesh.SkinningOffset, mesh.SkinningCount, skinning);
            ValidateRange(path, mesh.Name, "LOD0 meshlets", mesh.MeshletOffset, mesh.MeshletCount, meshletsLod0);
            ValidateRange(path, mesh.Name, "LOD1 meshlets", mesh.MeshletLod1Offset, mesh.MeshletLod1Count, meshletsLod1);
            ValidateRange(path, mesh.Name, "LOD2 meshlets", mesh.MeshletLod2Offset, mesh.MeshletLod2Count, meshletsLod2);
            ValidateRange(path, mesh.Name, "meshlet vertices", mesh.MeshletVertexOffset, mesh.MeshletVertexCount, meshletVertices);
            ValidateRange(path, mesh.Name, "meshlet triangles", mesh.MeshletTriangleOffset, mesh.MeshletTriangleCount, meshletTriangles);
        }
    }

    private static void ValidateRange(string path, string name, string rangeName, int offset, int count, int total)
    {
        if (offset < 0 || count < 0 || offset > total || count > total - offset)
            throw new CookedAssetFormatException(path, $"submesh '{name}' has an out-of-range {rangeName} slice ({offset}, {count}, total {total})");
    }

    private static void RebaseMaterialTexturePaths(CookedMaterialTable table, string materialDirectory)
    {
        string packageRoot = Path.GetFullPath(Path.Combine(materialDirectory, ".."));
        foreach (ModelMaterial material in table.Materials)
        {
            foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(ModelTextureSlot) && p.CanRead && p.CanWrite))
            {
                if (property.GetValue(material) is not ModelTextureSlot slot || slot.Source is null || string.IsNullOrWhiteSpace(slot.Source.FilePath))
                    continue;
                string absolute = Path.GetFullPath(Path.Combine(materialDirectory, slot.Source.FilePath));
                string relativeToRoot = Path.GetRelativePath(packageRoot, absolute);
                if (relativeToRoot == ".." || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new CookedAssetFormatException(materialDirectory, $"material texture reference '{slot.Source.FilePath}' escapes the cooked package root");
                property.SetValue(material, CloneSlot(slot, absolute));
            }
        }
    }

    internal static ModelTextureSlot CloneSlot(ModelTextureSlot slot, string filePath) => new()
    {
        Sampler = slot.Sampler,
        ColorSpace = slot.ColorSpace,
        TexCoordSet = slot.TexCoordSet,
        Offset = slot.Offset,
        Scale = slot.Scale,
        RotationRadians = slot.RotationRadians,
        Source = slot.Source is null ? null : new ModelTextureSource
        {
            DebugName = slot.Source.DebugName,
            SourceKind = TextureSourceKind.ExternalFile,
            FilePath = filePath,
            CacheIdentity = slot.Source.CacheIdentity.StartsWith("cooked:", StringComparison.Ordinal)
                ? slot.Source.CacheIdentity
                : "cooked:" + slot.Source.CacheIdentity,
            ContainerKind = TextureContainerKind.Ktx2,
            EncodedByteLength = slot.Source.EncodedByteLength,
            MimeType = "image/ktx2"
        }
    };
}
