using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Njulf.Assets.Cooked;

public sealed class ModelAssetCooker : IDisposable
{
    private const int MaterialTransportMetadataRevision = 1;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".obj", ".fbx", ".dae", ".3ds", ".blend", ".ply", ".stl"
    };

    private readonly ModelImporter _importer;
    private readonly ProcessedMeshAssetBuilder _meshBuilder;
    private readonly ITextureCooker _textureCooker;
    private bool _disposed;

    public ModelAssetCooker()
        : this(new ModelImporter(), new ProcessedMeshAssetBuilder(), new TextureCooker())
    {
    }

    public ModelAssetCooker(ModelImporter importer, ProcessedMeshAssetBuilder meshBuilder, ITextureCooker textureCooker)
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _meshBuilder = meshBuilder ?? throw new ArgumentNullException(nameof(meshBuilder));
        _textureCooker = textureCooker ?? throw new ArgumentNullException(nameof(textureCooker));
    }

    public AssetCookResult CookModel(string sourcePath, string outputRoot, ModelCookOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new ModelCookOptions();
        sourcePath = Path.GetFullPath(sourcePath);
        outputRoot = options.UsePlatformSubdirectory
            ? CookedPlatform.ResolveOutputRoot(outputRoot, options.Platform)
            : Path.GetFullPath(outputRoot);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Model source was not found.", sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath)))
            throw new NotSupportedException($"Asset cooker does not support model extension '{Path.GetExtension(sourcePath)}'.");

        Directory.CreateDirectory(outputRoot);
        string modelDirectory = Path.Combine(outputRoot, "models");
        string materialDirectory = Path.Combine(outputRoot, "materials");
        string textureDirectory = Path.Combine(outputRoot, "textures");
        string reportDirectory = Path.Combine(outputRoot, "reports");
        Directory.CreateDirectory(modelDirectory);
        Directory.CreateDirectory(materialDirectory);
        Directory.CreateDirectory(textureDirectory);
        Directory.CreateDirectory(reportDirectory);

        TextureCookOptions platformTextureOptions = options.TextureOptions with
        {
            TargetFormatPolicy = CookedPlatform.ResolveTexturePolicy(options.Platform, options.TextureOptions.TargetFormatPolicy)
        };

        string stem = SanitizeName(Path.GetFileNameWithoutExtension(sourcePath));
        string modelPath = Path.Combine(modelDirectory, stem + ".njmodel");
        string meshPath = Path.Combine(modelDirectory, stem + ".meshes.njmesh");
        string materialPath = Path.Combine(materialDirectory, stem + ".materials.njmat");
        string animationPath = Path.Combine(modelDirectory, stem + ".anim.njanim");
        string reportPath = Path.Combine(reportDirectory, stem + ".cook-report.json");
        string databasePath = Path.Combine(outputRoot, "assetdb.njassetdb");
        if (File.Exists(modelPath))
        {
            using var existingReader = new CookedAssetReader(modelPath, CookedAssetKind.Model);
            CookedModelManifest existingManifest = CookedJson.Deserialize<CookedModelManifest>(
                existingReader.GetRequiredSection(CookedSectionIds.Manifest).Span,
                modelPath,
                "manifest");
            if (!Path.GetFullPath(existingManifest.SourcePath).Equals(sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Cook output collision: source '{sourcePath}' and '{existingManifest.SourcePath}' both map to '{modelPath}'. " +
                    "Cook them to separate output roots or give the source files distinct base names.");
            }
        }

        ulong sourceHash = CookedHash.File(sourcePath);
        ulong settingsHash = CookedHash.Bytes(CookedJson.Serialize(new
        {
            options.ImporterOptions,
            TextureOptions = platformTextureOptions,
            options.ToolVersion,
            options.Platform,
            MaterialTransportMetadataRevision
        }));
        string databaseKey = NormalizeRelative(outputRoot, sourcePath);
        CookedAssetDatabase database = CookedAssetDatabase.Load(databasePath);
        var dependencies = new SortedDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, ulong hash) in DiscoverDependencies(sourcePath))
            dependencies[path] = hash;
        if (database.Assets.TryGetValue(databaseKey, out CookedAssetDatabaseEntry? previousEntry))
        {
            foreach (string dependencyPath in previousEntry.Dependencies.Keys)
                dependencies[dependencyPath] = File.Exists(dependencyPath) ? CookedHash.File(dependencyPath) : 0;
        }
        ulong dependencyHash = CookedHash.Ordered(dependencies.Select(pair => (pair.Key, pair.Value)));
        if (!options.Force && database.Assets.TryGetValue(databaseKey, out CookedAssetDatabaseEntry? existing) &&
            existing.SourceHash == sourceHash && existing.ImportSettingsHash == settingsHash &&
            existing.DependencyHash == dependencyHash && existing.ToolVersion == options.ToolVersion &&
            existing.Status == "Succeeded" && OutputsAreCurrent(outputRoot, existing.Outputs))
        {
            AssetCookReport skippedReport = File.Exists(reportPath)
                ? JsonSerializer.Deserialize<AssetCookReport>(File.ReadAllBytes(reportPath), CookedJson.Options)
                    ?? CreateSkippedReport(sourcePath, existing.Outputs)
                : CreateSkippedReport(sourcePath, existing.Outputs);
            return new AssetCookResult(skippedReport, true);
        }

        var warnings = new List<string>();
        var textureReports = new List<CookedTextureReport>();
        var timer = Stopwatch.StartNew();
        ModelImportResult import = _importer.ImportDetailed(sourcePath, options.ImporterOptions);
        ModelMesh model = import.EnsureImported();
        foreach ((string path, ulong hash) in DiscoverModelDependencies(model))
            dependencies[path] = hash;
        dependencyHash = CookedHash.Ordered(dependencies.Select(pair => (pair.Key, pair.Value)));
        long importMs = timer.ElapsedMilliseconds;
        foreach (AssetImportMessage message in import.Diagnostics.Messages.Where(message => message.Severity != AssetImportSeverity.Info))
            warnings.Add($"{message.Code}: {message.Message}");

        timer.Restart();
        ProcessedMeshAsset processed = _meshBuilder.Build(model, sourcePath);
        CookedMeshPayload mesh = CookedMeshBuilder.Build(processed);
        long meshMs = timer.ElapsedMilliseconds;

        timer.Restart();
        CookedMaterialTable materials = CookMaterials(model.Materials, materialDirectory, textureDirectory, platformTextureOptions, options.ToolVersion, textureReports);
        long textureMs = timer.ElapsedMilliseconds;

        timer.Restart();
        CookedPackage.WriteMesh(
            meshPath,
            mesh,
            sourceHash,
            settingsHash,
            dependencyHash,
            options.ToolVersion,
            CookedPlatform.SupportsMeshOptimizer(options.Platform));
        CookedPackage.WriteMaterials(materialPath, materials, sourceHash, settingsHash, dependencyHash, options.ToolVersion);
        CookedAssetReference? animationReference = null;
        if (model.Skeletons.Count > 0 || model.Skins.Count > 0 || model.AnimationClips.Count > 0)
        {
            CookedPackage.WriteAnimation(
                animationPath,
                new CookedAnimationPayload(model.Skeletons.ToArray(), model.Skins.ToArray(), model.AnimationClips.ToArray()),
                sourceHash,
                settingsHash,
                dependencyHash,
                options.ToolVersion);
            animationReference = new CookedAssetReference(Path.GetFileName(animationPath), CookedHash.File(animationPath));
        }
        var manifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath),
            model.Name,
            sourcePath.Replace('\\', '/'),
            sourceHash,
            settingsHash,
            dependencyHash,
            new CookedAssetReference(Path.GetFileName(meshPath), CookedHash.File(meshPath)),
            new CookedAssetReference(NormalizeRelative(modelDirectory, materialPath), CookedHash.File(materialPath)),
            animationReference,
            processed.SubMeshes.Select((subMesh, index) => new CookedModelSubObject(
                subMesh.Name, index, subMesh.MaterialSlot, subMesh.NodeIndex, subMesh.SkinIndex, subMesh.SkinningBindTransform)).ToArray(),
            processed.BoundingBox,
            processed.BoundingSphere);
        CookedPackage.WriteModel(modelPath, manifest, options.ToolVersion);
        long serializationMs = timer.ElapsedMilliseconds;

        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { modelPath, meshPath, materialPath };
        if (animationReference is not null)
            outputPaths.Add(animationPath);
        foreach (ModelMaterial material in materials.Materials)
        foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(ModelTextureSlot) && p.CanRead))
        {
            if (property.GetValue(material) is not ModelTextureSlot { Source.FilePath: { } texturePath })
                continue;
            string absoluteTexturePath = Path.GetFullPath(Path.Combine(materialDirectory, texturePath));
            outputPaths.Add(absoluteTexturePath);
            outputPaths.Add(Path.ChangeExtension(absoluteTexturePath, ".njtex"));
        }
        if (!string.IsNullOrWhiteSpace(options.SigningPrivateKey))
        {
            foreach (string path in outputPaths.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray())
                outputPaths.Add(CookedPackageSigner.SignFile(path, options.SigningPrivateKey));
        }
        var outputs = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string path in outputPaths.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            outputs[NormalizeRelative(outputRoot, path)] = CookedHash.File(path);

        var report = new AssetCookReport(
            sourcePath,
            manifest.AssetId,
            "Succeeded",
            import.Backend,
            importMs,
            meshMs,
            textureMs,
            serializationMs,
            processed.SubMeshes.Count,
            materials.Materials.Count,
            textureReports.Count,
            model.Skeletons.Count,
            model.Skins.Count,
            model.AnimationClips.Count,
            mesh.VertexPositions.Length,
            mesh.Indices.Length,
            mesh.MeshletsLod0.Length,
            textureReports,
            warnings,
            outputs)
        {
            MeshletLod1Count = mesh.MeshletsLod1.Length,
            MeshletLod2Count = mesh.MeshletsLod2.Length
        };
        WriteJsonAtomic(reportPath, report);
        database.Assets[databaseKey] = new CookedAssetDatabaseEntry
        {
            SourcePath = sourcePath,
            SourceHash = sourceHash,
            ImportSettingsHash = settingsHash,
            DependencyHash = dependencyHash,
            ToolVersion = options.ToolVersion,
            Status = "Succeeded",
            CookedAtUtc = DateTimeOffset.UtcNow,
            Dependencies = dependencies,
            Outputs = outputs
        };
        database.SaveAtomic(databasePath);
        return new AssetCookResult(report, false);
    }

    public IReadOnlyList<AssetCookResult> CookFolder(string sourceFolder, string outputRoot, ModelCookOptions? options = null)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder '{sourceFolder}' was not found.");
        return Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => CookModel(path, outputRoot, options))
            .ToArray();
    }

    public int CleanStale(string outputRoot, string? platform = null, bool usePlatformSubdirectory = true)
    {
        outputRoot = usePlatformSubdirectory ? CookedPlatform.ResolveOutputRoot(outputRoot, platform) : Path.GetFullPath(outputRoot);
        string databasePath = Path.Combine(outputRoot, "assetdb.njassetdb");
        CookedAssetDatabase database = CookedAssetDatabase.Load(databasePath);
        string[] removedSources = database.Assets
            .Where(pair => !File.Exists(pair.Value.SourcePath))
            .Select(pair => pair.Key)
            .ToArray();
        foreach (string key in removedSources)
            database.Assets.Remove(key);
        var referenced = database.Assets.Values.SelectMany(entry => entry.Outputs.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelative(outputRoot, file);
            if (relative.EndsWith(".njassetdb", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".cook-report.json", StringComparison.OrdinalIgnoreCase) || referenced.Contains(relative))
                continue;
            if (Path.GetExtension(file) is ".njmodel" or ".njmesh" or ".njmat" or ".njanim" or ".njtex" or ".ktx2" or ".sig")
            {
                File.Delete(file);
                deleted++;
            }
        }
        if (removedSources.Length > 0)
            database.SaveAtomic(databasePath);
        return deleted;
    }

    private CookedMaterialTable CookMaterials(
        IReadOnlyList<ModelMaterial> sourceMaterials,
        string materialDirectory,
        string textureDirectory,
        TextureCookOptions defaultOptions,
        uint toolVersion,
        List<CookedTextureReport> reports)
    {
        ModelMaterial[] materials = sourceMaterials.Count == 0
            ? [ModelMaterial.Default]
            : sourceMaterials.Select(CloneMaterial).ToArray();
        var cookedTextures = new Dictionary<string, (string Path, CookedTextureReport Report)>(StringComparer.Ordinal);
        foreach (ModelMaterial material in materials)
        {
            foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(ModelTextureSlot) && p.CanRead && p.CanWrite))
            {
                ModelTextureSlot? slot = property.GetValue(material) as ModelTextureSlot;
                if (slot?.Source is null)
                    continue;
                ModelTextureSource source = slot.Source;
                byte[] sourceBytes = source.Bytes is { Length: > 0 }
                    ? source.Bytes
                    : File.ReadAllBytes(Path.GetFullPath(source.FilePath ?? throw new InvalidDataException($"Texture '{source.CacheIdentity}' has no source data.")));
                ulong sourceHash = CookedHash.Bytes(sourceBytes);
                string identity = string.IsNullOrWhiteSpace(source.CacheIdentity) ? source.DebugName : source.CacheIdentity;
                TextureSemantic semantic = ClassifyTextureSemantic(property.Name, slot.ColorSpace);
                string key = $"{identity}|{slot.ColorSpace}|{semantic}|{defaultOptions.TargetFormatPolicy}|{sourceHash:x16}";
                if (!cookedTextures.TryGetValue(key, out var cooked))
                {
                    string textureStem = SanitizeName(string.IsNullOrWhiteSpace(source.DebugName) ? "texture" : Path.GetFileNameWithoutExtension(source.DebugName));
                    string suffix = CookedHash.Bytes(Encoding.UTF8.GetBytes(key)).ToString("x16")[..8];
                    string ktxPath = Path.Combine(textureDirectory, $"{textureStem}_{suffix}.ktx2");
                    var textureOptions = defaultOptions with { ColorSpace = slot.ColorSpace, Semantic = semantic };
                    CookedTextureReport textureReport = _textureCooker.Cook(source, ktxPath, textureOptions);
                    string metaPath = Path.ChangeExtension(ktxPath, ".njtex");
                    var meta = new CookedTextureMeta(
                        CookedPackage.StableAssetId(key), identity, sourceHash, Path.GetFileName(ktxPath), slot.ColorSpace, slot.Sampler,
                        textureReport.OriginalWidth, textureReport.OriginalHeight, textureReport.CookedWidth, textureReport.CookedHeight,
                        textureReport.MipCount, textureReport.VulkanFormat, textureReport.CookedBytes);
                    CookedPackage.WriteTextureMeta(metaPath, meta, toolVersion);
                    cooked = (ktxPath, textureReport);
                    cookedTextures.Add(key, cooked);
                    reports.Add(textureReport);
                }
                string relativeTexturePath = NormalizeRelative(materialDirectory, cooked.Path);
                property.SetValue(material, CookedPackage.CloneSlot(slot, relativeTexturePath));
                if (property.Name == nameof(ModelMaterial.BaseColorTexture) &&
                    cooked.Report.LinearAverageColor is { } linearAverageColor)
                {
                    material.DdgiBaseColorTextureAverageLinear = linearAverageColor;
                }
                if (semantic == TextureSemantic.Normal && cooked.Report.VulkanFormat == 141)
                    material.FeatureFlags |= 1u << 23;
            }
            foreach (System.Reflection.PropertyInfo pathProperty in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("TexturePath", StringComparison.Ordinal) && p.CanWrite))
                pathProperty.SetValue(material, null);
        }
        return new CookedMaterialTable(materials)
        {
            Pipelines = materials.Select(ClassifyMaterial).ToArray(),
            Fallbacks = materials.Select(material => new CookedMaterialFallback(material.Name, GetFallbackFlags(material))).ToArray()
        };
    }

    private static ModelMaterial CloneMaterial(ModelMaterial source)
    {
        var clone = new ModelMaterial();
        foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(clone, property.GetValue(source));
        return clone;
    }

    private static TextureSemantic ClassifyTextureSemantic(string propertyName, TextureColorSpace colorSpace)
    {
        if (colorSpace == TextureColorSpace.HdrLinear)
            return TextureSemantic.Hdr;
        if (propertyName.Contains("Normal", StringComparison.Ordinal))
            return TextureSemantic.Normal;
        if (propertyName.Contains("Occlusion", StringComparison.Ordinal) ||
            propertyName.Contains("Roughness", StringComparison.Ordinal) && !propertyName.Contains("MetallicRoughness", StringComparison.Ordinal) ||
            propertyName is "TransmissionTexture" or "ThicknessTexture" or "SpecularTexture" or "IridescenceTexture" or "IridescenceThicknessTexture")
            return TextureSemantic.Scalar;
        if (colorSpace == TextureColorSpace.Srgb)
            return TextureSemantic.Color;
        return TextureSemantic.Data;
    }

    private static CookedMaterialPipeline ClassifyMaterial(ModelMaterial material)
    {
        if (material.IsGeometryDecal)
            return CookedMaterialPipeline.Decal;
        if (material.Unlit)
            return CookedMaterialPipeline.Unlit;
        if ((material.FeatureFlags & (1u << 22)) != 0 || ContainsFoliageToken(material.Name))
            return CookedMaterialPipeline.Foliage;
        return material.AlphaMode switch
        {
            ModelAlphaMode.Mask => CookedMaterialPipeline.Masked,
            ModelAlphaMode.Blend => CookedMaterialPipeline.Blended,
            _ => CookedMaterialPipeline.Opaque
        };
    }

    private static CookedMaterialFallbackFlags GetFallbackFlags(ModelMaterial material)
    {
        CookedMaterialFallbackFlags flags = CookedMaterialFallbackFlags.None;
        if (material.BaseColorTexture?.Source is null) flags |= CookedMaterialFallbackFlags.BaseColorWhite;
        if (material.NormalTexture?.Source is null) flags |= CookedMaterialFallbackFlags.NormalDefault;
        if (material.MetallicRoughnessTexture?.Source is null) flags |= CookedMaterialFallbackFlags.MetallicRoughnessWhite;
        if (material.EmissiveTexture?.Source is null) flags |= CookedMaterialFallbackFlags.EmissiveBlack;
        if (material.OcclusionTexture?.Source is null) flags |= CookedMaterialFallbackFlags.OcclusionWhite;
        return flags;
    }

    private static bool ContainsFoliageToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && new[] { "foliage", "grass", "leaf", "leaves", "tree", "ivy", "billboard" }
            .Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, ulong> DiscoverDependencies(string sourcePath)
    {
        var result = new SortedDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        if (Path.GetExtension(sourcePath).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            string directory = Path.GetDirectoryName(sourcePath)!;
            foreach (string line in File.ReadLines(sourcePath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("mtllib ", StringComparison.OrdinalIgnoreCase))
                    continue;
                string dependency = Path.GetFullPath(Path.Combine(directory, trimmed[7..].Trim()));
                result[dependency.Replace('\\', '/')] = File.Exists(dependency) ? CookedHash.File(dependency) : 0;
            }
            return result;
        }
        if (!Path.GetExtension(sourcePath).Equals(".gltf", StringComparison.OrdinalIgnoreCase))
            return result;
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(sourcePath));
            string directory = Path.GetDirectoryName(sourcePath)!;
            foreach (JsonNode? node in (root?["buffers"] as JsonArray ?? []).Concat(root?["images"] as JsonArray ?? []))
            {
                string? uri = node?["uri"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;
                string dependency = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(uri)));
                if (File.Exists(dependency))
                    result[dependency.Replace('\\', '/')] = CookedHash.File(dependency);
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"glTF dependency scan failed for '{sourcePath}'.", ex);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ulong> DiscoverModelDependencies(ModelMesh model)
    {
        var result = new SortedDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelMaterial material in model.Materials)
        foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(ModelTextureSlot) && p.CanRead))
        {
            if (property.GetValue(material) is not ModelTextureSlot { Source.FilePath: { } filePath })
                continue;
            string path = Path.GetFullPath(filePath);
            result[path.Replace('\\', '/')] = File.Exists(path) ? CookedHash.File(path) : 0;
        }
        return result;
    }

    private static bool OutputsAreCurrent(string outputRoot, IReadOnlyDictionary<string, ulong> outputs) =>
        outputs.Count > 0 && outputs.All(pair =>
        {
            string path = Path.Combine(outputRoot, pair.Key);
            return File.Exists(path) && CookedHash.File(path) == pair.Value;
        });

    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
            builder.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        string result = builder.ToString().Trim('_');
        return string.IsNullOrEmpty(result) ? "asset" : result;
    }

    private static string NormalizeRelative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        string temporary = path + ".tmp";
        var options = new JsonSerializerOptions(CookedJson.Options) { WriteIndented = true };
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static AssetCookReport CreateSkippedReport(string sourcePath, IReadOnlyDictionary<string, ulong> outputs) => new(
        sourcePath, CookedPackage.StableAssetId(sourcePath), "Succeeded", ModelImportBackend.Auto,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        Array.Empty<CookedTextureReport>(), ["Incremental cook skipped: source, settings, dependencies, tool version, and outputs are unchanged."], outputs);

    public void Dispose()
    {
        if (_disposed) return;
        _importer.Dispose();
        _disposed = true;
    }
}
