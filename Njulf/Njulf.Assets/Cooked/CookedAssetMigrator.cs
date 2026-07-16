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
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"Cooked migration source '{sourceRoot}' was not found.");
        Directory.CreateDirectory(outputRoot);
        int migrated = 0;
        int copied = 0;
        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            string target = Path.Combine(outputRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (BinaryExtensions.Contains(Path.GetExtension(source)))
            {
                MigrateFile(source, target);
                migrated++;
            }
            else if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase) && !source.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, target, overwrite: true);
                copied++;
            }
        }

        // Sidecar bytes can change during migration, so refresh manifest whole-file hashes.
        foreach (string modelPath in Directory.EnumerateFiles(outputRoot, "*.njmodel", SearchOption.AllDirectories))
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
            foreach (string asset in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories).Where(path => BinaryExtensions.Contains(Path.GetExtension(path)) || path.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase)))
                CookedPackageSigner.SignFile(asset, signingPrivateKey);
        }
        return new CookedMigrationReport(migrated, copied, sourceRoot, outputRoot);
    }

    public static void MigrateFile(string sourcePath, string targetPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        targetPath = Path.GetFullPath(targetPath);
        string writePath = string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) ? targetPath + ".migrating" : targetPath;
        using (var reader = new CookedAssetReader(sourcePath))
        using (var writer = new CookedAssetWriter(writePath, reader.Header.AssetKind, reader.Header.SourceHash, reader.Header.ImportSettingsHash, reader.Header.DependencyListHash, reader.Header.BuildToolVersion, reader.Header.Flags))
        {
            foreach (CookedSectionEntry section in reader.Sections.OrderBy(section => section.Offset))
                MigrateSection(reader, writer, section);
            writer.Complete();
        }
        if (!string.Equals(writePath, targetPath, StringComparison.OrdinalIgnoreCase))
            File.Move(writePath, targetPath, overwrite: true);
    }

    private static void MigrateSection(CookedAssetReader reader, CookedAssetWriter writer, CookedSectionEntry section)
    {
        CookedSectionFlags flags = section.Flags & CookedSectionFlags.Required;
        uint id = section.SectionId;
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

    private static CookedAssetReference RefreshReference(string modelDirectory, CookedAssetReference reference)
    {
        string path = Path.GetFullPath(Path.Combine(modelDirectory, reference.RelativePath));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Migrated model sidecar '{reference.RelativePath}' was not found.", path);
        return reference with { ContentHash = CookedHash.File(path) };
    }

    private static void WriteModelInPlace(string path, CookedModelManifest manifest)
    {
        string temporary = path + ".migrating";
        CookedPackage.WriteModel(temporary, manifest);
        File.Move(temporary, path, overwrite: true);
    }
}
