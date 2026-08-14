using System.Security.Cryptography;
using System.Text;
using Njulf.Assets.Validation;
using Njulf.Core.Geometry;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Immutable, bounded identity snapshot of a cooked model package. The raw
/// bytes remain private so hashing, package parsing, and the resulting runtime
/// upload can be tied to one read of the package path.
/// </summary>
public sealed class CookedModelPackageSnapshot
{
    private readonly byte[] _content;

    internal CookedModelPackageSnapshot(string packagePath, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(content);

        PackagePath = Path.GetFullPath(packagePath);
        _content = content;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public string PackagePath { get; }
    public long ByteLength => _content.LongLength;
    public string Sha256 { get; }

    internal ReadOnlyMemory<byte> Content => _content;
}

public static class CookedPackage
{
    private const int MaximumCookedModelLights = 1024;
    private const CookedSectionFlags Required = CookedSectionFlags.Required;
    public const int MaximumModelPackageSnapshotBytes =
        512 * 1024 * 1024;
    private const int MaximumSignedCookedAssetBytes =
        MaximumModelPackageSnapshotBytes;

    public static void WriteModel(
        string path,
        CookedModelManifest manifest,
        uint toolVersion = 1,
        CookedOpacityMicromapModelChunk? opacityMicromapChunk = null)
    {
        ValidateModelLights(path, manifest.Lights);
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Model, manifest.SourceHash, manifest.ImportSettingsHash, manifest.DependencyListHash, toolVersion);
        writer.WriteSection(CookedSectionIds.Manifest, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(manifest));
        if (opacityMicromapChunk is not null)
        {
            writer.WriteSection(
                CookedSectionIds.OpacityMicromap,
                CookedSectionFlags.None,
                opacityMicromapChunk.EncodedBytes.Span);
        }
        writer.Complete();
    }

    public static void WriteModel(
        string path,
        CookedModelManifest manifest,
        OpacityMicromapCookedPayload opacityMicromapPayload,
        uint toolVersion = 1)
    {
        ArgumentNullException.ThrowIfNull(opacityMicromapPayload);
        if (!CookedOpacityMicromapModelChunk.TryCreate(
                opacityMicromapPayload,
                out CookedOpacityMicromapModelChunk? chunk,
                out string detail))
        {
            throw new ArgumentException(
                $"The optional opacity-micromap payload cannot be serialized: {detail}.",
                nameof(opacityMicromapPayload));
        }

        WriteModel(path, manifest, toolVersion, chunk);
    }

    public static void WriteMesh(string path, CookedMeshPayload mesh, ulong sourceHash, ulong settingsHash, ulong dependencyHash, uint toolVersion = 1, bool useMeshOptimizer = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ValidateMeshRanges(path, mesh.SubMeshes, mesh.VertexPositions.Length,
            mesh.Indices.Length, mesh.VertexSkinning.Length,
            mesh.MeshletsLod0.Length, mesh.MeshletsLod1.Length,
            mesh.MeshletsLod2.Length, mesh.MeshletVertices.Length,
            mesh.MeshletTriangles.Length);
        ValidateCausticTopologyEvidence(
            path, mesh.SubMeshes, mesh.VertexPositions, mesh.Indices);
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
        ArgumentNullException.ThrowIfNull(materials);
        materials = NormalizeMaterialTransportMetadata(materials);
        ValidateMaterialTransportMetadata(path, materials);
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
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Ktx2ContentHash == 0)
        {
            throw new ArgumentException(
                "Current cooked texture metadata requires a non-zero KTX2 whole-file hash.",
                nameof(texture));
        }
        texture = NormalizeTextureTransportMetadata(texture);
        ValidateTextureTransportMetadata(path, texture);
        using var writer = new CookedAssetWriter(path, CookedAssetKind.Texture, texture.SourceHash, 0, 0, toolVersion);
        writer.WriteSection(CookedSectionIds.Metadata, Required | CookedSectionFlags.Zstd, CookedJson.Serialize(texture));
        writer.Complete();
    }

    public static CookedModelAsset LoadModel(string modelPath, CookedAssetReaderFlags flags = CookedAssetReaderFlags.None, ulong? expectedSourceHash = null)
    {
        modelPath = Path.GetFullPath(modelPath);
        using CookedAssetReader reader = OpenAuthenticatedReader(
            modelPath,
            CookedAssetKind.Model,
            flags,
            expectedSourceHash);
        return LoadModel(reader, modelPath, flags);
    }

    /// <summary>
    /// Captures one bounded, immutable read of a cooked model package. The
    /// returned identity remains valid even if the package path is replaced
    /// after this method returns.
    /// </summary>
    public static CookedModelPackageSnapshot CaptureModelSnapshot(
        string modelPath,
        long maximumBytes = MaximumModelPackageSnapshotBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (maximumBytes <= 0 || maximumBytes > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytes),
                maximumBytes,
                $"The cooked model snapshot limit must be in (0, {int.MaxValue}].");
        }

        string fullPath = Path.GetFullPath(modelPath);
        byte[] content = ReadBoundedSnapshot(
            fullPath,
            checked((int)maximumBytes),
            "cooked model package");
        return new CookedModelPackageSnapshot(fullPath, content);
    }

    /// <summary>
    /// Decodes a model from the exact package bytes held by
    /// <paramref name="snapshot"/>. Referenced mesh/material/animation
    /// packages are decoded once into the returned model asset; callers can
    /// therefore upload that asset without reopening the model package path.
    /// </summary>
    public static CookedModelAsset LoadModel(
        CookedModelPackageSnapshot snapshot,
        CookedAssetReaderFlags flags = CookedAssetReaderFlags.None,
        ulong? expectedSourceHash = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (flags.HasFlag(CookedAssetReaderFlags.RequireSignature))
        {
            CookedPackageSigner.VerifyRequired(
                snapshot.PackagePath,
                snapshot.Content.Span);
        }

        using var reader = new CookedAssetReader(
            snapshot.Content,
            snapshot.PackagePath,
            CookedAssetKind.Model,
            flags,
            expectedSourceHash);
        return LoadModel(reader, snapshot.PackagePath, flags);
    }

    private static CookedModelAsset LoadModel(
        CookedAssetReader reader,
        string modelPath,
        CookedAssetReaderFlags flags)
    {
        CookedModelManifest manifest = CookedJson.Deserialize<CookedModelManifest>(
            reader.GetRequiredSection(CookedSectionIds.Manifest).Span,
            modelPath,
            "manifest");
        manifest = manifest with
        {
            Lights = manifest.Lights ?? Array.Empty<Core.Scene.ModelLightDefinition>()
        };
        ValidateModelLights(modelPath, manifest.Lights);
        (OpacityMicromapCookedPayload? opacityMicromapPayload,
            CookedOpacityMicromapPayloadLoadStatus opacityMicromapLoadStatus) =
            LoadOptionalOpacityMicromapPayload(reader);
        long bytesRead = reader.BytesRead;

        string directory = Path.GetDirectoryName(modelPath)!;
        string packageRoot = Path.GetFullPath(Path.Combine(directory, ".."));
        bool verifyWholeFileHashes = flags.HasFlag(CookedAssetReaderFlags.StrictSourceHash);
        string meshPath = ResolveReference(
            directory,
            packageRoot,
            manifest.Mesh.RelativePath);
        string materialPath = ResolveReference(
            directory,
            packageRoot,
            manifest.Material.RelativePath);
        string? animationPath = manifest.Animation is null
            ? null
            : ResolveReference(
                directory,
                packageRoot,
                manifest.Animation.RelativePath);

        // The manifest has already authenticated the independent sidecar
        // references. Read/decode them concurrently so cold model loads can
        // overlap disk latency and CPU decompression without involving the
        // renderer thread. The set is bounded to these three fixed sidecars.
        Task<(CookedMeshPayload Payload, long Bytes)> meshTask = Task.Run(() =>
        {
            CookedMeshPayload payload = LoadMesh(
                meshPath,
                flags,
                verifyWholeFileHashes ? manifest.Mesh.ContentHash : null,
                out long bytes);
            return (payload, bytes);
        });
        Task<(CookedMaterialTable Payload, long Bytes)> materialTask =
            Task.Run(() =>
            {
                CookedMaterialTable payload = LoadMaterials(
                    materialPath,
                    flags,
                    verifyWholeFileHashes ? manifest.Material.ContentHash : null,
                    out long bytes);
                return (payload, bytes);
            });
        Task<(CookedAnimationPayload Payload, long Bytes)>? animationTask =
            animationPath is null
                ? null
                : Task.Run(() =>
                {
                    CookedAnimationPayload payload = LoadAnimation(
                        animationPath,
                        flags,
                        verifyWholeFileHashes
                            ? manifest.Animation!.ContentHash
                            : null,
                        out long bytes);
                    return (payload, bytes);
                });
        Task[] sidecarTasks = animationTask is null
            ? [meshTask, materialTask]
            : [meshTask, materialTask, animationTask];
        Task.WhenAll(sidecarTasks).GetAwaiter().GetResult();

        (CookedMeshPayload mesh, long meshBytes) = meshTask.GetAwaiter().GetResult();
        (CookedMaterialTable materials, long materialBytes) =
            materialTask.GetAwaiter().GetResult();
        RebaseMaterialTexturePaths(materials, Path.GetDirectoryName(materialPath)!);
        if (opacityMicromapPayload is not null &&
            !CookedOpacityMicromapModelChunk.TryValidateModelAttachment(
                opacityMicromapPayload,
                mesh,
                materials,
                out OpacityMicromapPayloadValidationFailure attachmentFailure,
                out string attachmentDetail))
        {
            opacityMicromapPayload = null;
            opacityMicromapLoadStatus =
                CookedOpacityMicromapPayloadLoadStatus.Rejected(
                    attachmentFailure,
                    attachmentDetail);
        }
        (CookedAnimationPayload animation, long animationBytes) = animationTask is null
            ? (new CookedAnimationPayload(
                Array.Empty<Core.Animation.Skeleton>(),
                Array.Empty<Core.Animation.Skin>(),
                Array.Empty<Core.Animation.AnimationClip>()), 0)
            : animationTask.GetAwaiter().GetResult();
        return new CookedModelAsset(
            manifest,
            mesh,
            materials,
            animation,
            modelPath,
            bytesRead + meshBytes + materialBytes + animationBytes)
        {
            OpacityMicromapPayload = opacityMicromapPayload,
            OpacityMicromapLoadStatus = opacityMicromapLoadStatus
        };
    }

    private static void ValidateModelLights(
        string path,
        IReadOnlyList<Core.Scene.ModelLightDefinition>? lights)
    {
        if (lights is null)
            throw new InvalidDataException(
                $"Cooked model '{path}' contains a null light collection.");
        if (lights.Count > MaximumCookedModelLights)
        {
            throw new InvalidDataException(
                $"Cooked model '{path}' contains {lights.Count} lights, exceeding the runtime limit of {MaximumCookedModelLights}.");
        }

        var diagnostics = new AssetImportDiagnostics();
        for (int index = 0; index < lights.Count; index++)
        {
            Core.Scene.ModelLightDefinition? light = lights[index];
            if (light is null)
            {
                throw new InvalidDataException(
                    $"Cooked model '{path}' contains a null light at index {index}.");
            }
            ModelLightImportUtilities.ValidateAndRecord(
                light,
                diagnostics,
                path);
        }
    }

    private static (
        OpacityMicromapCookedPayload? Payload,
        CookedOpacityMicromapPayloadLoadStatus Status)
        LoadOptionalOpacityMicromapPayload(CookedAssetReader reader)
    {
        try
        {
            if (!reader.TryGetSection(
                    CookedSectionIds.OpacityMicromap,
                    out ReadOnlyMemory<byte> bytes))
            {
                return (null, CookedOpacityMicromapPayloadLoadStatus.Missing);
            }

            OpacityMicromapPayloadReadResult parsed =
                OpacityMicromapCookedPayloadCodec.TryRead(bytes.Span);
            if (!parsed.Success || parsed.Payload is null)
            {
                return (
                    null,
                    CookedOpacityMicromapPayloadLoadStatus.Rejected(
                        parsed.Failure,
                        "opacity-micromap-section-schema-validation-failed"));
            }

            return (parsed.Payload, CookedOpacityMicromapPayloadLoadStatus.Valid);
        }
        catch (CookedAssetFormatException)
        {
            return (
                null,
                CookedOpacityMicromapPayloadLoadStatus.Rejected(
                    OpacityMicromapPayloadValidationFailure.SpanOutOfRange,
                    "opacity-micromap-section-container-validation-failed"));
        }
        catch (CookedAssetHashException)
        {
            return (
                null,
                CookedOpacityMicromapPayloadLoadStatus.Rejected(
                    OpacityMicromapPayloadValidationFailure.SpanChecksumMismatch,
                    "opacity-micromap-section-container-checksum-failed"));
        }
    }

    public static CookedMeshPayload LoadMesh(string path, CookedAssetReaderFlags flags, out long bytesRead)
        => LoadMesh(
            path,
            flags,
            expectedContentHash: null,
            bytesRead: out bytesRead);

    private static CookedMeshPayload LoadMesh(
        string path,
        CookedAssetReaderFlags flags,
        ulong? expectedContentHash,
        out long bytesRead)
    {
        using var reader = OpenAuthenticatedReader(
            path,
            CookedAssetKind.Mesh,
            flags,
            expectedContentHash: expectedContentHash);
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
        ValidateCausticTopologyEvidence(path, subMeshes, positions, indices);
        return new CookedMeshPayload(subMeshes, positions, normals, uvColors, skinning, indices, lod0, lod1, lod2, meshletVertices, meshletTriangles);
    }

    public static CookedMaterialTable LoadMaterials(string path, CookedAssetReaderFlags flags, out long bytesRead)
        => LoadMaterials(
            path,
            flags,
            expectedContentHash: null,
            bytesRead: out bytesRead);

    private static CookedMaterialTable LoadMaterials(
        string path,
        CookedAssetReaderFlags flags,
        ulong? expectedContentHash,
        out long bytesRead)
    {
        using var reader = OpenAuthenticatedReader(
            path,
            CookedAssetKind.Material,
            flags,
            expectedContentHash: expectedContentHash);
        CookedMaterialTable result = CookedJson.Deserialize<CookedMaterialTable>(reader.GetRequiredSection(CookedSectionIds.Materials).Span, path, "materials");
        bytesRead = reader.BytesRead;
        result = NormalizeMaterialTransportMetadata(result);
        ValidateMaterialTransportMetadata(path, result);
        return result;
    }

    public static CookedAnimationPayload LoadAnimation(string path, CookedAssetReaderFlags flags, out long bytesRead)
        => LoadAnimation(
            path,
            flags,
            expectedContentHash: null,
            bytesRead: out bytesRead);

    private static CookedAnimationPayload LoadAnimation(
        string path,
        CookedAssetReaderFlags flags,
        ulong? expectedContentHash,
        out long bytesRead)
    {
        using var reader = OpenAuthenticatedReader(
            path,
            CookedAssetKind.Animation,
            flags,
            expectedContentHash: expectedContentHash);
        CookedAnimationPayload result = CookedJson.Deserialize<CookedAnimationPayload>(reader.GetRequiredSection(CookedSectionIds.Animation).Span, path, "animation");
        bytesRead = reader.BytesRead;
        return result;
    }

    private static CookedAssetReader OpenAuthenticatedReader(
        string path,
        CookedAssetKind expectedKind,
        CookedAssetReaderFlags flags,
        ulong? expectedSourceHash = null,
        ulong? expectedContentHash = null)
    {
        path = Path.GetFullPath(path);
        bool immutableSnapshotRequired =
            flags.HasFlag(CookedAssetReaderFlags.RequireSignature) ||
            expectedContentHash.HasValue;
        if (!immutableSnapshotRequired)
        {
            return new CookedAssetReader(
                path,
                expectedKind,
                flags,
                expectedSourceHash);
        }

        byte[] content = ReadBoundedSnapshot(
            path,
            MaximumSignedCookedAssetBytes,
            $"cooked {expectedKind} asset");
        if (flags.HasFlag(CookedAssetReaderFlags.RequireSignature))
            CookedPackageSigner.VerifyRequired(path, content);
        if (expectedContentHash.HasValue)
        {
            ulong actualContentHash = CookedHash.Bytes(content);
            if (actualContentHash != expectedContentHash.Value)
            {
                throw new CookedAssetHashException(
                    path,
                    $"package reference expected whole-file hash " +
                    $"0x{expectedContentHash.Value:x16}, got " +
                    $"0x{actualContentHash:x16}");
            }
        }

        return new CookedAssetReader(
            content,
            path,
            expectedKind,
            flags,
            expectedSourceHash);
    }

    public static CookedTextureMeta LoadTextureMeta(
        string path,
        CookedAssetReaderFlags flags = CookedAssetReaderFlags.None) =>
        LoadTextureMeta(path, flags, out _);

    public static CookedTextureMeta LoadTextureMeta(
        string path,
        CookedAssetReaderFlags flags,
        out ulong contentHash)
    {
        path = Path.GetFullPath(path);
        const int maxTextureMetadataBytes = 16 * 1024 * 1024;
        byte[] content = ReadBoundedSnapshot(
            path,
            maxTextureMetadataBytes,
            "cooked texture metadata");
        if (flags.HasFlag(CookedAssetReaderFlags.RequireSignature))
            CookedPackageSigner.VerifyRequired(path, content);

        using var reader = new CookedAssetReader(
            content,
            path,
            CookedAssetKind.Texture,
            flags);
        CookedTextureMeta texture = CookedJson.Deserialize<CookedTextureMeta>(
            reader.GetRequiredSection(CookedSectionIds.Metadata).Span,
            path,
            "texture metadata");
        texture = NormalizeTextureTransportMetadata(texture);
        ValidateTextureTransportMetadata(path, texture);
        contentHash = CookedHash.Bytes(content);
        return texture;
    }

    private static byte[] ReadBoundedSnapshot(
        string path,
        int maximumBytes,
        string description)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
            {
                throw new CookedAssetFormatException(
                    path,
                    $"{description} is {stream.Length} bytes; the runtime limit is " +
                    $"{maximumBytes} bytes");
            }

            var content = GC.AllocateUninitializedArray<byte>(
                checked((int)stream.Length));
            stream.ReadExactly(content);
            if (stream.ReadByte() != -1)
            {
                throw new CookedAssetFormatException(
                    path,
                    $"{description} changed while its immutable snapshot was read");
            }

            return content;
        }
        catch (CookedAssetFormatException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CookedAssetFormatException(
                path,
                $"{description} could not be read ({exception.Message})");
        }
    }

    internal static CookedMaterialTable NormalizeMaterialTransportMetadata(CookedMaterialTable table)
    {
        IReadOnlyList<GiPrimitiveTransportProfile> profiles =
            table.PrimitiveTransportProfiles ?? Array.Empty<GiPrimitiveTransportProfile>();
        IReadOnlyList<CookedMaterialPipeline> pipelines =
            table.Pipelines ?? Array.Empty<CookedMaterialPipeline>();
        IReadOnlyList<CookedMaterialFallback> fallbacks =
            table.Fallbacks ?? Array.Empty<CookedMaterialFallback>();
        return table with
        {
            Pipelines = pipelines,
            Fallbacks = fallbacks,
            PrimitiveTransportProfiles = profiles
        };
    }

    internal static CookedTextureMeta NormalizeTextureTransportMetadata(CookedTextureMeta texture)
    {
        TextureTransportStatistics? statistics = texture.TransportStatistics;
        if (texture.Ktx2ContentHash == 0)
        {
            statistics = TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.LegacyMissing,
                "Legacy cooked texture metadata does not authenticate its KTX2 payload.",
                texture.SourceHash,
                texture.Semantic,
                texture.ColorSpace);
        }
        else if (statistics is null ||
            statistics.Status == TextureTransportStatisticsStatus.LegacyMissing &&
            statistics.SourceContentHash == 0)
        {
            statistics = TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.LegacyMissing,
                "Legacy cooked texture metadata contains no transport statistics.",
                texture.SourceHash,
                texture.Semantic,
                texture.ColorSpace);
        }
        return texture with { TransportStatistics = statistics };
    }

    private static void ValidateMaterialTransportMetadata(string path, CookedMaterialTable materials)
    {
        if (materials.Materials is null)
            throw new InvalidDataException($"Cooked material '{path}' contains a null material table.");
        if (materials.PrimitiveTransportProfiles.Count == 0)
        {
            if (materials.HasCompleteTransportMetadata)
                throw new InvalidDataException($"Cooked material '{path}' claims complete transport metadata but has no primitive profiles.");
            if (materials.PrimitiveTransportAlgorithmVersion != 0)
            {
                throw new InvalidDataException(
                    $"Cooked material '{path}' has no primitive profiles but " +
                    $"declares primitive algorithm " +
                    $"{materials.PrimitiveTransportAlgorithmVersion}; empty " +
                    "transport metadata must use algorithm version 0.");
            }
            return;
        }
        if (materials.PrimitiveTransportAlgorithmVersion != GiPrimitiveTransportProfile.CurrentAlgorithmVersion)
        {
            throw new InvalidDataException(
                $"Cooked material '{path}' declares primitive algorithm {materials.PrimitiveTransportAlgorithmVersion}, " +
                $"expected {GiPrimitiveTransportProfile.CurrentAlgorithmVersion}.");
        }
        var keys = new HashSet<(int SubMesh, int Material)>();
        long emissiveRecordCount = 0;
        foreach (GiPrimitiveTransportProfile profile in materials.PrimitiveTransportProfiles)
        {
            if (profile is null)
                throw new InvalidDataException($"Cooked material '{path}' contains a null primitive transport profile.");
            if (profile.SchemaVersion != GiPrimitiveTransportProfile.CurrentSchemaVersion ||
                profile.AlgorithmVersion != GiPrimitiveTransportProfile.CurrentAlgorithmVersion)
            {
                throw new InvalidDataException($"Cooked material '{path}' contains an unsupported primitive transport profile version.");
            }
            if ((uint)profile.MaterialSlot >= (uint)materials.Materials.Count)
                throw new InvalidDataException($"Cooked material '{path}' contains an out-of-range primitive material slot.");
            if (!keys.Add((profile.SubMeshIndex, profile.MaterialSlot)))
                throw new InvalidDataException($"Cooked material '{path}' contains duplicate primitive/material transport keys.");
            IReadOnlyList<string> errors = profile.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidDataException(
                    $"Cooked material '{path}' contains invalid primitive transport metadata: {string.Join(" ", errors)}");
            }
            if (materials.HasCompleteTransportMetadata && !profile.IsComplete)
                throw new InvalidDataException($"Cooked material '{path}' claims complete transport metadata but contains an incomplete profile.");
            emissiveRecordCount = checked(emissiveRecordCount + profile.EmissiveTriangles.Length);
            if (emissiveRecordCount > GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPackage)
            {
                throw new InvalidDataException(
                    $"Cooked material '{path}' contains {emissiveRecordCount} emissive triangle records, " +
                    $"exceeding the hard package cap " +
                    $"{GiPrimitiveTransportProfile.MaximumEmissiveTriangleRecordsPerPackage}.");
            }
        }
    }

    private static void ValidateTextureTransportMetadata(string path, CookedTextureMeta texture)
    {
        if (string.IsNullOrWhiteSpace(texture.SourceIdentity))
            throw new InvalidDataException($"Cooked texture '{path}' has no source identity.");
        if (texture.SourceHash == 0)
            throw new InvalidDataException($"Cooked texture '{path}' has no source-content hash.");
        if (string.IsNullOrWhiteSpace(texture.Ktx2RelativePath) ||
            Path.IsPathRooted(texture.Ktx2RelativePath))
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' has an invalid KTX2 relative path.");
        }
        if (texture.Ktx2ContentHash == 0 &&
            texture.TransportStatistics?.Status !=
            TextureTransportStatisticsStatus.LegacyMissing)
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' has no authenticated KTX2 whole-file hash.");
        }
        if (texture.OriginalWidth <= 0 ||
            texture.OriginalHeight <= 0 ||
            texture.CookedWidth <= 0 ||
            texture.CookedHeight <= 0 ||
            texture.MipCount <= 0 ||
            texture.EncodedBytes <= 0)
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' contains invalid dimensions, mip count, or encoded size.");
        }

        TextureTransportStatistics statistics = texture.TransportStatistics ??
            throw new InvalidDataException($"Cooked texture '{path}' has null transport statistics.");
        if (statistics.SourceContentHash != texture.SourceHash)
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' statistics hash 0x{statistics.SourceContentHash:x16} " +
                $"does not match metadata hash 0x{texture.SourceHash:x16}.");
        }
        if (statistics.Semantic != texture.Semantic)
            throw new InvalidDataException($"Cooked texture '{path}' statistics semantic does not match metadata.");
        if (statistics.ColorSpace != texture.ColorSpace)
            throw new InvalidDataException($"Cooked texture '{path}' statistics color space does not match metadata.");
        if (!Enum.IsDefined(texture.Semantic) ||
            !Enum.IsDefined(texture.ColorSpace) ||
            !Enum.IsDefined(texture.Sampler.WrapU) ||
            !Enum.IsDefined(texture.Sampler.WrapV) ||
            !Enum.IsDefined(texture.Sampler.MinFilter) ||
            !Enum.IsDefined(texture.Sampler.MagFilter) ||
            !Enum.IsDefined(texture.Sampler.MipFilter) ||
            !float.IsFinite(texture.Sampler.MaxAnisotropy) ||
            texture.Sampler.MaxAnisotropy <= 0f)
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' contains invalid semantic, color-space, or sampler metadata.");
        }
        if (texture.AlphaCoveragePreserved &&
            (!texture.AlphaCoverageCutoff.HasValue ||
             !float.IsFinite(texture.AlphaCoverageCutoff.Value) ||
             texture.AlphaCoverageCutoff.Value < 0f))
        {
            throw new InvalidDataException($"Cooked texture '{path}' has invalid alpha-coverage preservation metadata.");
        }
        if (!texture.AlphaCoveragePreserved && texture.AlphaCoverageCutoff.HasValue)
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' declares an alpha cutoff without preserved coverage.");
        }
        if (statistics.Status == TextureTransportStatisticsStatus.Valid &&
            (statistics.Width != texture.OriginalWidth ||
             statistics.Height != texture.OriginalHeight))
        {
            throw new InvalidDataException(
                $"Cooked texture '{path}' statistics dimensions do not match the original source dimensions.");
        }
        if (statistics.Status == TextureTransportStatisticsStatus.Valid)
            statistics.EnsureValid(path);
        else if (statistics.Validate().Count > 0)
            throw new InvalidDataException($"Cooked texture '{path}' contains malformed invalid-statistics metadata.");
    }

    public static Guid StableAssetId(string canonicalSourcePath)
    {
        string canonical = File.Exists(canonicalSourcePath)
            ? Path.GetFullPath(canonicalSourcePath).Replace('\\', '/').ToUpperInvariant()
            : canonicalSourcePath.Replace('\\', '/');
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static string ResolveReference(
        string baseDirectory,
        string packageRoot,
        string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
            throw new CookedAssetFormatException(baseDirectory, $"package reference '{relativePath}' must be relative");
        string path = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
        string normalizedRelative = Path.GetRelativePath(packageRoot, path);
        if (normalizedRelative == ".." || normalizedRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new CookedAssetFormatException(baseDirectory, $"package reference '{relativePath}' escapes the cooked package root");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Cooked package dependency '{relativePath}' was not found for '{baseDirectory}'.", path);
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

    private static void ValidateCausticTopologyEvidence(
        string path,
        IReadOnlyList<CookedSubMeshRecord> meshes,
        IReadOnlyList<CookedVertexPositionStream> positions,
        IReadOnlyList<uint> indices)
    {
        foreach (CookedSubMeshRecord mesh in meshes)
        {
            ModelGiCausticHeroTopologyEvidence evidence =
                mesh.CausticTopologyEvidence;
            if (evidence == default)
                continue;
            if (!evidence.IsStructurallyValid)
            {
                throw new CookedAssetFormatException(
                    path,
                    $"submesh '{mesh.Name}' contains malformed C4 topology evidence");
            }

            var localPositions = new NumericsVector3[mesh.VertexCount];
            for (int index = 0; index < localPositions.Length; index++)
            {
                Njulf.Core.Math.Vector4 source =
                    positions[mesh.VertexOffset + index].Position;
                localPositions[index] = new NumericsVector3(
                    source.X, source.Y, source.Z);
            }
            var localIndices = new uint[mesh.IndexCount];
            for (int index = 0; index < localIndices.Length; index++)
                localIndices[index] = indices[mesh.IndexOffset + index];
            if (!ModelGiCausticHeroTopologyAnalyzer.Matches(
                    localPositions,
                    localIndices,
                    isSkinned: mesh.SkinIndex >= 0 || mesh.SkinningCount > 0,
                    evidence,
                    out string reason))
            {
                throw new CookedAssetFormatException(
                    path,
                    $"submesh '{mesh.Name}' C4 topology evidence failed exact revalidation ({reason})");
            }
        }
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

    internal static ModelTextureSlot CloneSlot(
        ModelTextureSlot slot,
        string filePath,
        string? authenticatedSourceIdentity = null) => new()
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
                CacheIdentity = authenticatedSourceIdentity is not null
                ? "cooked:" + RequireAuthenticatedTextureIdentity(
                    authenticatedSourceIdentity)
                : slot.Source.CacheIdentity.StartsWith(
                    "cooked:",
                    StringComparison.Ordinal)
                    ? slot.Source.CacheIdentity
                    : "cooked:" + RequireAuthenticatedTextureIdentity(
                        slot.Source.CacheIdentity),
                ContainerKind = TextureContainerKind.Ktx2,
                EncodedByteLength = slot.Source.EncodedByteLength,
                MimeType = "image/ktx2"
            }
        };

    private static string RequireAuthenticatedTextureIdentity(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
        {
            throw new InvalidDataException(
                "A cooked texture slot requires a non-empty authenticated source identity.");
        }

        return identity;
    }
}
