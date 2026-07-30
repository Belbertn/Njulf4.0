using Njulf.Core.Geometry;

namespace Njulf.Assets.Cooked;

public sealed record CookedMigrationReport(int MigratedFiles, int CopiedFiles, string SourceRoot, string OutputRoot);

public static class CookedAssetMigrator
{
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".njmodel", ".njmesh", ".njmat", ".njtex", ".njanim"
    };

    public static CookedMigrationReport MigrateTree(string sourceRoot, string outputRoot, string? signingPrivateKey = null)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        EnsureMigrationRootsDoNotOverlap(sourceRoot, outputRoot);
        string outputParent = Path.GetDirectoryName(outputRoot)
            ?? throw new ArgumentException(
                "Cooked migration output cannot be a filesystem root.",
                nameof(outputRoot));
        string outputName = Path.GetFileName(outputRoot);
        if (string.IsNullOrWhiteSpace(outputName))
        {
            throw new ArgumentException(
                "Cooked migration output must have a directory name.",
                nameof(outputRoot));
        }

        Directory.CreateDirectory(outputParent);
        string lockPath = Path.Combine(
            outputParent,
            $".{outputName}.migration.lock");
        string stagingRoot = Path.Combine(
            outputParent,
            $".{outputName}.migration-staging");
        string backupRoot = Path.Combine(
            outputParent,
            $".{outputName}.migration-backup");
        using var migrationLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        RecoverInterruptedPublication(
            outputRoot,
            stagingRoot,
            backupRoot);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"Cooked migration source '{sourceRoot}' was not found.");
        }

        string[] sourceFiles = Directory
            .EnumerateFiles(
                sourceRoot,
                "*",
                SearchOption.AllDirectories)
            .OrderBy(
                path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Directory.CreateDirectory(stagingRoot);
        bool published = false;
        try
        {
            (int migrated, int copied) = MigrateTreeContents(
                sourceRoot,
                stagingRoot,
                sourceFiles,
                signingPrivateKey);
            PublishStagedTree(
                outputRoot,
                stagingRoot,
                backupRoot);
            published = true;
            return new CookedMigrationReport(
                migrated,
                copied,
                sourceRoot,
                outputRoot);
        }
        finally
        {
            if (!published && Directory.Exists(stagingRoot))
                TryDeleteDirectory(stagingRoot);
        }
    }

    private static (int Migrated, int Copied) MigrateTreeContents(
        string sourceRoot,
        string stagingRoot,
        IReadOnlyList<string> sourceFiles,
        string? signingPrivateKey)
    {
        int migrated = 0;
        int copied = 0;
        foreach (string source in sourceFiles)
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            string target = Path.Combine(stagingRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (BinaryExtensions.Contains(Path.GetExtension(source)))
            {
                MigrateFile(source, target);
                migrated++;
            }
            else if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && !source.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            {
                AssetArtifactFileIo.CopyAtomic(
                    source,
                    target,
                    CookedAssetReader.MaximumAssetBytes,
                    "Cooked migration sidecar");
                copied++;
            }
        }

        // Sidecar bytes can change during migration, so refresh manifest whole-file hashes.
        foreach (string modelPath in Directory.EnumerateFiles(stagingRoot, "*.njmodel", SearchOption.AllDirectories))
        {
            CookedModelManifest manifest;
            using (var reader = new CookedAssetReader(modelPath, CookedAssetKind.Model))
                manifest = CookedJson.Deserialize<CookedModelManifest>(reader.GetRequiredSection(CookedSectionIds.Manifest).Span, modelPath, "manifest");
            string modelDirectory = Path.GetDirectoryName(modelPath)!;
            CookedAssetReference mesh = RefreshReference(modelDirectory, manifest.Mesh);
            CookedAssetReference material = RefreshReference(modelDirectory, manifest.Material);
            CookedAssetReference? animation = manifest.Animation is null ? null : RefreshReference(modelDirectory, manifest.Animation);
            WriteModelInPlace(modelPath, manifest with { Mesh = mesh, Material = material, Animation = animation });
        }

        if (!string.IsNullOrWhiteSpace(signingPrivateKey))
        {
            foreach (string asset in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories).Where(path => BinaryExtensions.Contains(Path.GetExtension(path)) || path.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase)))
                CookedPackageSigner.SignFile(asset, signingPrivateKey);
        }
        return (migrated, copied);
    }

    private static void PublishStagedTree(
        string outputRoot,
        string stagingRoot,
        string backupRoot)
    {
        bool previousMoved = false;
        try
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Move(outputRoot, backupRoot);
                previousMoved = true;
            }

            Directory.Move(stagingRoot, outputRoot);
        }
        catch (Exception publicationFailure)
        {
            List<Exception>? rollbackFailures = null;
            if (!Directory.Exists(outputRoot) &&
                previousMoved &&
                Directory.Exists(backupRoot))
            {
                try
                {
                    Directory.Move(backupRoot, outputRoot);
                }
                catch (Exception rollbackFailure)
                {
                    (rollbackFailures ??= []).Add(rollbackFailure);
                }
            }

            if (rollbackFailures != null)
            {
                throw new AggregateException(
                    "Cooked tree publication failed and restoring the " +
                    "previous output tree was incomplete.",
                    [publicationFailure, .. rollbackFailures]);
            }
            throw;
        }

        TryDeleteDirectory(backupRoot);
    }

    private static void RecoverInterruptedPublication(
        string outputRoot,
        string stagingRoot,
        string backupRoot)
    {
        if (Directory.Exists(backupRoot))
        {
            if (!Directory.Exists(outputRoot))
                Directory.Move(backupRoot, outputRoot);
            else
                TryDeleteDirectory(backupRoot);
        }
        TryDeleteDirectory(stagingRoot);
        if (Directory.Exists(backupRoot) ||
            Directory.Exists(stagingRoot))
        {
            throw new IOException(
                "Cooked migration could not recover or remove a prior " +
                "staging/backup tree.");
        }
    }

    private static void EnsureMigrationRootsDoNotOverlap(
        string sourceRoot,
        string outputRoot)
    {
        if (string.Equals(
                sourceRoot,
                outputRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string sourcePrefix = sourceRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string outputPrefix = outputRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (sourcePrefix.StartsWith(
                outputPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            outputPrefix.StartsWith(
                sourcePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Cooked migration source and output trees must be disjoint " +
                "or exactly equal.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A committed output does not become invalid because cleanup of a
            // staging/backup directory was delayed. The next migration call
            // performs deterministic recovery while holding the same lock.
        }
    }

    public static void MigrateFile(string sourcePath, string targetPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        bool inPlace = string.Equals(
            sourcePath,
            targetPath,
            StringComparison.OrdinalIgnoreCase);
        string writePath = inPlace
            ? AssetArtifactFileIo.CreateSiblingTemporaryPath(
                targetPath,
                "migrating.tmp")
            : targetPath;
        try
        {
            using (var reader = new CookedAssetReader(sourcePath))
            using (var writer = new CookedAssetWriter(writePath, reader.Header.AssetKind, reader.Header.SourceHash, reader.Header.ImportSettingsHash, reader.Header.DependencyListHash, reader.Header.BuildToolVersion, reader.Header.Flags))
            {
                foreach (CookedSectionEntry section in reader.Sections.OrderBy(section => section.Offset))
                    MigrateSection(reader, writer, section);
                writer.Complete();
            }
            if (inPlace)
                File.Move(writePath, targetPath, overwrite: true);
        }
        finally
        {
            if (inPlace && File.Exists(writePath))
                File.Delete(writePath);
        }
    }

    private static void MigrateSection(CookedAssetReader reader, CookedAssetWriter writer, CookedSectionEntry section)
    {
        CookedSectionFlags flags = section.Flags & CookedSectionFlags.Required;
        uint id = section.SectionId;
        if (TryMigrateTransportMetadataSection(reader, writer, id, flags))
            return;
        if (id is var vertexId && (vertexId == CookedSectionIds.VertexPositions || vertexId == CookedSectionIds.VertexNormals || vertexId == CookedSectionIds.VertexUvColors || vertexId == CookedSectionIds.VertexSkinning || vertexId == CookedSectionIds.Meshlets0 || vertexId == CookedSectionIds.Meshlets1 || vertexId == CookedSectionIds.Meshlets2))
        {
            if (id == CookedSectionIds.VertexPositions) writer.WriteMeshoptVertexSection(id, flags, reader.ReadSection<CookedVertexPositionStream>(id));
            else if (id == CookedSectionIds.VertexNormals) writer.WriteMeshoptVertexSection(id, flags, reader.ReadSection<CookedVertexNormalTangentStream>(id));
            else if (id == CookedSectionIds.VertexUvColors) writer.WriteMeshoptVertexSection(id, flags, reader.ReadSection<CookedVertexUvColorStream>(id));
            else if (id == CookedSectionIds.VertexSkinning) writer.WriteMeshoptVertexSection(id, flags, reader.ReadSection<CookedVertexSkinningData>(id));
            else writer.WriteMeshoptVertexSection(id, flags, reader.ReadSection<Meshlet>(id));
            return;
        }
        if (id is var indexId && (indexId == CookedSectionIds.Indices || indexId == CookedSectionIds.MeshletVertices || indexId == CookedSectionIds.MeshletTriangles))
        {
            uint[] values = reader.ReadSection<uint>(id);
            int valueRange = values.Length == 0 ? 1 : checked((int)values.Max() + 1);
            writer.WriteMeshoptIndexSequenceSection(id, flags, values, valueRange);
            return;
        }
        if (!reader.TryGetSection(id, out ReadOnlyMemory<byte> bytes))
            throw new CookedAssetFormatException(reader.Path, $"section '{CookedSectionIds.ToText(id)}' disappeared during migration");
        writer.WriteSection(id, flags | CookedSectionFlags.Zstd, bytes.Span);
    }

    private static bool TryMigrateTransportMetadataSection(
        CookedAssetReader reader,
        CookedAssetWriter writer,
        uint id,
        CookedSectionFlags flags)
    {
        if (!reader.TryGetSection(id, out ReadOnlyMemory<byte> bytes))
            return false;
        try
        {
            if (reader.Header.AssetKind == CookedAssetKind.Texture && id == CookedSectionIds.Metadata)
            {
                CookedTextureMeta texture = CookedJson.Deserialize<CookedTextureMeta>(
                    bytes.Span,
                    reader.Path,
                    "texture metadata");
                texture = CookedPackage.NormalizeTextureTransportMetadata(texture);
                TextureTransportStatistics statistics = texture.TransportStatistics;
                if (statistics.SchemaVersion !=
                        TextureTransportStatistics.CurrentSchemaVersion ||
                    statistics.AlgorithmVersion !=
                        TextureTransportStatistics.CurrentAlgorithmVersion)
                {
                    texture = texture with
                    {
                        TransportStatistics = TextureTransportStatistics.Invalid(
                            TextureTransportStatisticsStatus.LegacyMissing,
                            "Migrated cooked texture used unsupported transport-statistics " +
                            $"schema {statistics.SchemaVersion}, algorithm " +
                            $"{statistics.AlgorithmVersion}; authoritative source statistics " +
                            "must be regenerated by recooking.",
                            texture.SourceHash,
                            texture.Semantic,
                            texture.ColorSpace,
                            statistics.Decoder)
                    };
                }
                writer.WriteSection(id, flags | CookedSectionFlags.Zstd, CookedJson.Serialize(texture));
                return true;
            }
            if (reader.Header.AssetKind == CookedAssetKind.Material && id == CookedSectionIds.Materials)
            {
                CookedMaterialTable materials = CookedJson.Deserialize<CookedMaterialTable>(
                    bytes.Span,
                    reader.Path,
                    "materials");
                materials = CookedPackage.NormalizeMaterialTransportMetadata(materials);
                if (!HasCurrentPrimitiveTransportMetadata(materials))
                {
                    materials = materials with
                    {
                        HasCompleteTransportMetadata = false,
                        PrimitiveTransportAlgorithmVersion = 0,
                        PrimitiveTransportProfiles =
                            Array.Empty<GiPrimitiveTransportProfile>()
                    };
                }
                writer.WriteSection(id, flags | CookedSectionFlags.Zstd, CookedJson.Serialize(materials));
                return true;
            }
        }
        catch (CookedAssetFormatException)
        {
            // Preserve opaque sections from very old/custom packages. The
            // current migrator historically supported such payloads; readers
            // will still reject them if a caller attempts typed loading.
        }
        return false;
    }

    private static bool HasCurrentPrimitiveTransportMetadata(
        CookedMaterialTable materials)
    {
        IReadOnlyList<GiPrimitiveTransportProfile> profiles =
            materials.PrimitiveTransportProfiles;
        if (profiles.Count == 0 ||
            materials.Materials is null ||
            materials.PrimitiveTransportAlgorithmVersion !=
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion)
        {
            return false;
        }

        var keys = new HashSet<(int SubMesh, int Material)>();
        long emissiveRecordCount = 0;
        foreach (GiPrimitiveTransportProfile? profile in profiles)
        {
            if (profile is null ||
                profile.SchemaVersion !=
                    GiPrimitiveTransportProfile.CurrentSchemaVersion ||
                profile.AlgorithmVersion !=
                    GiPrimitiveTransportProfile.CurrentAlgorithmVersion ||
                (uint)profile.MaterialSlot >= (uint)materials.Materials.Count ||
                !keys.Add((profile.SubMeshIndex, profile.MaterialSlot)) ||
                profile.Validate().Count != 0 ||
                materials.HasCompleteTransportMetadata && !profile.IsComplete)
            {
                return false;
            }

            try
            {
                emissiveRecordCount = checked(
                    emissiveRecordCount + profile.EmissiveTriangles.Length);
            }
            catch (Exception exception)
                when (exception is OverflowException or NullReferenceException)
            {
                return false;
            }
            if (emissiveRecordCount >
                GiPrimitiveTransportProfile
                    .MaximumEmissiveTriangleRecordsPerPackage)
            {
                return false;
            }
        }

        return true;
    }

    private static CookedAssetReference RefreshReference(string modelDirectory, CookedAssetReference reference)
    {
        string path = Path.GetFullPath(Path.Combine(modelDirectory, reference.RelativePath));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Migrated model sidecar '{reference.RelativePath}' was not found.", path);
        return reference with { ContentHash = CookedHash.File(path) };
    }

    private static void WriteModelInPlace(string path, CookedModelManifest manifest)
    {
        string temporary = AssetArtifactFileIo.CreateSiblingTemporaryPath(
            path,
            "migrating.tmp");
        try
        {
            CookedPackage.WriteModel(temporary, manifest);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
