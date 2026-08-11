using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Njulf.Assets.Validation;

namespace Njulf.Assets.Cooked;

public sealed class ModelAssetCooker : IDisposable
{
    private const int MaterialTransportMetadataRevision = 2;
    // Included in the incremental-cook identity. Bump whenever generated mesh
    // topology or LOD policy changes without changing the binary file layout.
    private const int MeshLodAlgorithmRevision = 1;

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
            MaterialTransportMetadataRevision,
            MeshLodAlgorithmRevision,
            CausticTopologyAlgorithmVersion =
                ModelGiCausticHeroTopologyAnalyzer.CurrentAlgorithmVersion,
            OpacityMicromapPayloadProducer =
                CreateOpacityMicromapProducerSettingsHashInput(
                    options.OpacityMicromapPayloadProducer),
            TextureStatisticsAlgorithmVersion = TextureTransportStatistics.CurrentAlgorithmVersion,
            PrimitiveTransportAlgorithmVersion = GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            TextureTransportStatistics.StbDecoderVersion,
            TextureTransportStatistics.WebPDecoderVersion,
            TextureTransportStatistics.BcDecoderVersion,
            TextureTransportStatistics.KtxStatisticsDecoderVersion
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
                ? AssetCookReportJson.Read(reportPath)
                : CreateSkippedReport(sourcePath, existing.Outputs);
            return new AssetCookResult(skippedReport, true);
        }

        // A stable .njmodel is the single package publication point. Every
        // mutable sidecar receives a new generation name so the previously
        // published manifest can never observe partially overwritten bytes.
        // Stale generations are reclaimed by CleanStale after the database
        // commits the new output set.
        string generation = Guid.NewGuid().ToString("N");
        string meshPath = Path.Combine(
            modelDirectory,
            $"{stem}.{generation}.meshes.njmesh");
        string materialPath = Path.Combine(
            materialDirectory,
            $"{stem}.{generation}.materials.njmat");
        string animationPath = Path.Combine(
            modelDirectory,
            $"{stem}.{generation}.anim.njanim");
        string stagedModelPath =
            AssetArtifactFileIo.CreateSiblingTemporaryPath(
                modelPath,
                "publishing");
        var generationArtifacts = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            meshPath,
            materialPath,
            animationPath,
            stagedModelPath
        };
        bool modelPublished = false;
        string? publishedSignaturePath = null;
        string? previousSignatureBackupPath = null;
        bool signaturePublished = false;

        try
        {
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
        foreach (ProcessedSubMeshAsset subMesh in processed.SubMeshes)
        {
            ModelGiCausticHeroValidation validation =
                subMesh.CausticAuthoringValidation;
            if (!validation.IsEligible &&
                validation.Reason != ModelGiCausticHeroValidationReason.Disabled)
            {
                warnings.Add(
                    $"C4_HERO_REJECTED: Submesh '{subMesh.Name}' was rejected " +
                    $"before runtime work ({validation.Reason}: {validation.Detail}; " +
                    $"{subMesh.CausticTopologyDetail}).");
            }
        }
        CookedMeshPayload mesh = CookedMeshBuilder.Build(processed);
        long meshMs = timer.ElapsedMilliseconds;

        timer.Restart();
        CookedMaterialTable materials = CookMaterials(model, materialDirectory, textureDirectory, platformTextureOptions, options.ToolVersion, textureReports);
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
        CookedOpacityMicromapModelChunk? opacityMicromapChunk =
            TryProduceOpacityMicromapChunk(
                options,
                sourcePath,
                manifest,
                model,
                processed,
                mesh,
                materials,
                warnings);
        CookedPackage.WriteModel(
            stagedModelPath,
            manifest,
            options.ToolVersion,
            opacityMicromapChunk);
        long serializationMs = timer.ElapsedMilliseconds;

        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { meshPath, materialPath };
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
            string stagedSignature = CookedPackageSigner.SignFile(
                stagedModelPath,
                options.SigningPrivateKey);
            generationArtifacts.Add(stagedSignature);
            publishedSignaturePath =
                CookedPackageSigner.SignaturePath(modelPath);
            if (File.Exists(publishedSignaturePath))
            {
                previousSignatureBackupPath =
                    AssetArtifactFileIo.CreateSiblingTemporaryPath(
                        publishedSignaturePath,
                        "previous");
                File.Copy(
                    publishedSignaturePath,
                    previousSignatureBackupPath);
                generationArtifacts.Add(previousSignatureBackupPath);
            }
            File.Move(
                stagedSignature,
                publishedSignaturePath,
                overwrite: true);
            signaturePublished = true;
            outputPaths.Add(publishedSignaturePath);
        }
        File.Move(stagedModelPath, modelPath, overwrite: true);
        modelPublished = true;
        if (previousSignatureBackupPath != null)
            TryDeleteUnpublishedArtifact(previousSignatureBackupPath);
        outputPaths.Add(modelPath);
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
        AssetCookReportJson.WriteAtomic(reportPath, report);
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
        catch (Exception cookFailure)
        {
            List<Exception>? rollbackFailures = null;
            if (!modelPublished)
            {
                if (signaturePublished &&
                    publishedSignaturePath != null)
                {
                    try
                    {
                        if (previousSignatureBackupPath != null &&
                            File.Exists(previousSignatureBackupPath))
                        {
                            File.Move(
                                previousSignatureBackupPath,
                                publishedSignaturePath,
                                overwrite: true);
                        }
                        else
                        {
                            TryDeleteUnpublishedArtifact(
                                publishedSignaturePath);
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        (rollbackFailures ??= []).Add(
                            rollbackFailure);
                    }
                }
                foreach (string artifact in generationArtifacts)
                {
                    TryDeleteUnpublishedArtifact(artifact);
                    TryDeleteUnpublishedArtifact(
                        CookedPackageSigner.SignaturePath(artifact));
                }
            }

            if (rollbackFailures != null)
            {
                throw new AggregateException(
                    "Model cook failed and restoring the previously published " +
                    "package signature was incomplete.",
                    [cookFailure, .. rollbackFailures]);
            }
            throw;
        }
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

    private static CookedOpacityMicromapModelChunk? TryProduceOpacityMicromapChunk(
        ModelCookOptions options,
        string sourcePath,
        CookedModelManifest manifest,
        ModelMesh model,
        ProcessedMeshAsset processed,
        CookedMeshPayload mesh,
        CookedMaterialTable materials,
        ICollection<string> warnings)
    {
        IOpacityMicromapModelPayloadProducer? producer =
            options.OpacityMicromapPayloadProducer;
        if (producer is null)
            return null;

        OpacityMicromapPayloadProducerIdentity identity = producer.Identity;
        if (!identity.TryValidate(out string identityDetail))
        {
            warnings.Add(
                "OpacityMicromap: optional payload disabled because " +
                BoundedOptionalDiagnostic(identityDetail));
            return null;
        }

        try
        {
            var context = new OpacityMicromapModelCookContext(
                sourcePath,
                manifest.AssetId,
                manifest.SourceHash,
                manifest.ImportSettingsHash,
                manifest.DependencyListHash,
                options.ToolVersion,
                model,
                processed,
                mesh,
                materials);
            OpacityMicromapPayloadProductionResult result =
                producer.Produce(context);
            if (result.Status != OpacityMicromapPayloadProductionStatus.Produced ||
                result.Payload is null)
            {
                warnings.Add(
                    "OpacityMicromap: optional payload not published: " +
                    BoundedOptionalDiagnostic(result.Detail));
                return null;
            }
            if (result.Payload.CookAbi != identity.CookAbi)
            {
                warnings.Add(
                    "OpacityMicromap: optional payload rejected because its cook ABI " +
                    "does not match the producer identity.");
                return null;
            }
            if (result.Payload.SdkProvenanceHash != identity.SdkProvenanceHash)
            {
                warnings.Add(
                    "OpacityMicromap: optional payload rejected because its SDK " +
                    "provenance does not match the producer identity.");
                return null;
            }
            if (!CookedOpacityMicromapModelChunk.TryValidateModelAttachment(
                    result.Payload,
                    mesh,
                    materials,
                    out _,
                    out string attachmentDetail))
            {
                warnings.Add(
                    "OpacityMicromap: optional payload rejected: " +
                    BoundedOptionalDiagnostic(attachmentDetail));
                return null;
            }
            if (!CookedOpacityMicromapModelChunk.TryCreate(
                    result.Payload,
                    out CookedOpacityMicromapModelChunk? chunk,
                    out string chunkDetail))
            {
                warnings.Add(
                    "OpacityMicromap: optional payload rejected: " +
                    BoundedOptionalDiagnostic(chunkDetail));
                return null;
            }

            return chunk;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
                                           not StackOverflowException and
                                           not AccessViolationException)
        {
            warnings.Add(
                "OpacityMicromap: optional producer failed; ordinary alpha " +
                "candidate path retained.");
            return null;
        }
    }

    private static string BoundedOptionalDiagnostic(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "no-detail";
        const int maximumCharacters = 192;
        string sanitized = detail
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return sanitized.Length <= maximumCharacters
            ? sanitized
            : sanitized[..maximumCharacters];
    }

    private static object? CreateOpacityMicromapProducerSettingsHashInput(
        IOpacityMicromapModelPayloadProducer? producer)
    {
        if (producer is null)
            return null;

        OpacityMicromapPayloadProducerIdentity identity = producer.Identity;
        // OpacityMicromapContentKey deliberately keeps its raw bytes private.
        // Serialize its canonical hexadecimal form here rather than relying on
        // the JSON serializer's public-property discovery (which would only
        // observe IsZero and could therefore alias distinct non-zero keys).
        return new
        {
            identity.Name,
            identity.CookAbi,
            identity.PolicyRevision,
            SdkProvenanceHash = identity.SdkProvenanceHash.ToString()
        };
    }

    private CookedMaterialTable CookMaterials(
        ModelMesh model,
        string materialDirectory,
        string textureDirectory,
        TextureCookOptions defaultOptions,
        uint toolVersion,
        List<CookedTextureReport> reports)
    {
        IReadOnlyList<ModelMaterial> sourceMaterials = model.Materials;
        ModelMaterial[] materials = sourceMaterials.Count == 0
            ? [ModelMaterial.Default]
            : sourceMaterials.Select(CloneMaterial).ToArray();
        var cookedTextures = new Dictionary<string, (string Path, CookedTextureReport Report)>(StringComparer.Ordinal);
        var opacityMicromapTextureArtifacts =
            new List<OpacityMicromapCookedTextureArtifact>();
        IReadOnlyList<ModelSubMesh> primitiveSubMeshes =
            GetPrimitiveTransportSubMeshes(model);
        var primitiveProfiles =
            new GiPrimitiveTransportProfile?[primitiveSubMeshes.Count];
        for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
        {
            ModelMaterial material = materials[materialIndex];
            var materialImages =
                new Dictionary<(int MaterialIndex, string PropertyName), TextureTransportImage>();
            var materialTransportImages =
                new Dictionary<string, TextureTransportImage?>(StringComparer.Ordinal);
            foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(ModelTextureSlot) && p.CanRead && p.CanWrite))
            {
                ModelTextureSlot? slot = property.GetValue(material) as ModelTextureSlot;
                if (slot?.Source is null)
                    continue;
                ModelTextureSource source = slot.Source;
                byte[] sourceBytes = ReadStableTextureSourceBytes(source);
                ulong sourceHash = CookedHash.Bytes(sourceBytes);
                string identity = ResolveTextureSourceIdentity(source);
                TextureSemantic semantic = ClassifyTextureSemantic(property.Name, slot.ColorSpace);
                bool foliageMaterial =
                    (material.FeatureFlags & (1u << 22)) != 0 ||
                    ContainsFoliageToken(material.Name);
                bool preserveAlphaCoverage =
                    property.Name == nameof(ModelMaterial.BaseColorTexture) &&
                    (material.AlphaMode == ModelAlphaMode.Mask || foliageMaterial);
                string alphaCoverageKey = preserveAlphaCoverage
                    ? $"enabled:{material.AlphaCutoff:R}"
                    : "disabled";
                string samplerAnisotropy = slot.Sampler.MaxAnisotropy.ToString(
                    "R",
                    System.Globalization.CultureInfo.InvariantCulture);
                string key =
                    $"{identity}|{slot.ColorSpace}|{semantic}|{defaultOptions.TargetFormatPolicy}|" +
                    $"sampler:{slot.Sampler.WrapU}:{slot.Sampler.WrapV}:" +
                    $"{slot.Sampler.MinFilter}:{slot.Sampler.MagFilter}:" +
                    $"{slot.Sampler.MipFilter}:{samplerAnisotropy}|" +
                    $"alpha:{alphaCoverageKey}|" +
                    $"stats:{TextureTransportStatistics.CurrentAlgorithmVersion}|" +
                    $"decoder:{TextureTransportStatistics.StbDecoderVersion}|" +
                    $"{TextureTransportStatistics.WebPDecoderVersion}|" +
                    $"{TextureTransportStatistics.KtxStatisticsDecoderVersion}|{sourceHash:x16}";
                var textureOptions = defaultOptions with
                {
                    ColorSpace = slot.ColorSpace,
                    Semantic = semantic,
                    PreserveAlphaCoverage = preserveAlphaCoverage,
                    AlphaCutoff = material.AlphaCutoff
                };
                TextureTransportImage? transportImage;
                if (!cookedTextures.TryGetValue(key, out var cooked))
                {
                    string textureStem = SanitizeName(string.IsNullOrWhiteSpace(source.DebugName) ? "texture" : Path.GetFileNameWithoutExtension(source.DebugName));
                    string suffix = CookedHash.Bytes(Encoding.UTF8.GetBytes(key)).ToString("x16")[..8];
                    string ktxPath = Path.Combine(textureDirectory, $"{textureStem}_{suffix}.ktx2");
                    CookedTextureReport textureReport;
                    if (!TryReuseCookedTexture(
                            ktxPath,
                            source,
                            sourceBytes,
                            sourceHash,
                            identity,
                            semantic,
                            slot,
                            preserveAlphaCoverage,
                            textureOptions,
                            toolVersion,
                            out textureReport,
                            out transportImage))
                    {
                        textureReport = _textureCooker.Cook(
                            CreateMemoryBackedTextureSource(source, sourceBytes),
                            ktxPath,
                            textureOptions);
                        transportImage = textureReport.SourceTransportImage;
                        string metaPath = Path.ChangeExtension(ktxPath, ".njtex");
                        var meta = new CookedTextureMeta(
                            CookedPackage.StableAssetId(key), identity, sourceHash, Path.GetFileName(ktxPath),
                            textureReport.TransportStatistics.ColorSpace, slot.Sampler,
                            textureReport.OriginalWidth, textureReport.OriginalHeight, textureReport.CookedWidth, textureReport.CookedHeight,
                            textureReport.MipCount, textureReport.VulkanFormat, textureReport.CookedBytes)
                        {
                            Ktx2ContentHash = CookedHash.File(ktxPath),
                            Semantic = semantic,
                            TransportStatistics = textureReport.TransportStatistics,
                            AlphaCoveragePreserved = textureReport.AlphaCoveragePreserved,
                            AlphaCoverageCutoff = textureReport.AlphaCoveragePreserved
                                ? textureReport.AlphaCutoff
                                : null
                        };
                        CookedPackage.WriteTextureMeta(metaPath, meta, toolVersion);
                    }
                    textureReport = textureReport with
                    {
                        // Source-resolution pixels are transient primitive-
                        // integration input. Keeping them in cook reports
                        // retains hundreds of MiB per 4K texture even though
                        // the property is excluded from report JSON.
                        SourceTransportImage = null
                    };
                    cooked = (ktxPath, textureReport);
                    cookedTextures.Add(key, cooked);
                    reports.Add(textureReport);
                }
                else if (!materialTransportImages.TryGetValue(
                             key,
                             out transportImage))
                {
                    TextureTransportSourceAnalysis analysis =
                        TextureCooker.AnalyzeTransportSource(
                            sourceBytes,
                            source.ContainerKind,
                            identity,
                            textureOptions with
                            {
                                ColorSpace =
                                    cooked.Report.TransportStatistics.ColorSpace
                            },
                            AssetArtifactFileIo.MaximumCookSourceBytes,
                            int.MaxValue);
                    if (analysis.Statistics.SourceContentHash != sourceHash)
                    {
                        throw new InvalidDataException(
                            $"Texture '{identity}' transport analysis hash " +
                            $"0x{analysis.Statistics.SourceContentHash:x16} " +
                            $"does not match source hash 0x{sourceHash:x16}.");
                    }
                    transportImage = analysis.Image;
                }
                materialTransportImages[key] = transportImage;
                string relativeTexturePath = NormalizeRelative(materialDirectory, cooked.Path);
                property.SetValue(
                    material,
                    CookedPackage.CloneSlot(
                        slot,
                        relativeTexturePath,
                        identity));
                if (transportImage is not null)
                    materialImages[(materialIndex, property.Name)] = transportImage;
                if (property.Name == nameof(ModelMaterial.BaseColorTexture) &&
                    cooked.Report.LinearAverageColor is { } linearAverageColor)
                {
                    material.DdgiBaseColorTextureAverageLinear = linearAverageColor;
                }
                if (semantic == TextureSemantic.Normal && cooked.Report.VulkanFormat == 141)
                    material.FeatureFlags |= 1u << 23;

                if (property.Name == nameof(ModelMaterial.BaseColorTexture))
                {
                    string exactPath = Path.GetFullPath(cooked.Path);
                    byte[] digest;
                    using (FileStream textureStream = new(
                               exactPath,
                               FileMode.Open,
                               FileAccess.Read,
                               FileShare.Read,
                               bufferSize: 128 * 1024,
                               FileOptions.SequentialScan))
                    {
                        digest = SHA256.HashData(textureStream);
                    }
                    opacityMicromapTextureArtifacts.Add(
                        new OpacityMicromapCookedTextureArtifact(
                            materialIndex,
                            exactPath,
                            OpacityMicromapContentKey.FromSha256(digest),
                            cooked.Report.VulkanFormat,
                            cooked.Report.CookedWidth,
                            cooked.Report.CookedHeight,
                            cooked.Report.MipCount,
                            cooked.Report.TransportStatistics.ColorSpace,
                            slot.Sampler,
                            cooked.Report.AlphaCoveragePreserved,
                            cooked.Report.AlphaCoveragePreserved
                                ? cooked.Report.AlphaCutoff
                                : null));
                }
            }
            foreach (System.Reflection.PropertyInfo pathProperty in typeof(ModelMaterial).GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name.EndsWith("TexturePath", StringComparison.Ordinal) && p.CanWrite))
                pathProperty.SetValue(material, null);
            BuildPrimitiveTransportProfilesForMaterial(
                materialIndex,
                primitiveSubMeshes,
                materials,
                materialImages,
                primitiveProfiles);
        }
        GiPrimitiveTransportProfile[] completedProfiles = primitiveProfiles
            .Select(
                (profile, subMeshIndex) => profile ??
                    throw new InvalidDataException(
                        $"Primitive transport profile {subMeshIndex} was not generated."))
            .ToArray();
        IReadOnlyList<GiPrimitiveTransportProfile> boundedProfiles =
            GiPrimitiveTransportProfileGenerator.ApplyPackageEmissiveRecordBudget(
                completedProfiles);
        return new CookedMaterialTable(materials)
        {
            Pipelines = materials.Select(ClassifyMaterial).ToArray(),
            Fallbacks = materials.Select(material => new CookedMaterialFallback(material.Name, GetFallbackFlags(material))).ToArray(),
            PrimitiveTransportProfiles = boundedProfiles,
            PrimitiveTransportAlgorithmVersion = GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            HasCompleteTransportMetadata =
                boundedProfiles.Count > 0 &&
                boundedProfiles.All(profile => profile.IsComplete),
            OpacityMicromapTextureArtifacts =
                opacityMicromapTextureArtifacts.ToArray()
        };
    }

    private static IReadOnlyList<ModelSubMesh> GetPrimitiveTransportSubMeshes(
        ModelMesh model)
    {
        if (model.SubMeshes.Count > 0)
            return model.SubMeshes;
        return
        [
            new ModelSubMesh
            {
                Name = string.IsNullOrWhiteSpace(model.Name) ? "Mesh" : model.Name,
                MaterialIndex = 0,
                Vertices = model.Vertices,
                Normals = model.Normals,
                Tangents = model.Tangents,
                Bitangents = model.Bitangents,
                TexCoords = model.TexCoords,
                TexCoords1 = model.TexCoords1,
                VertexColors = model.VertexColors,
                JointIndices0 = model.JointIndices0,
                JointWeights0 = model.JointWeights0,
                Indices = model.Indices,
                BoundingBox = model.BoundingBox,
                BoundingSphere = model.BoundingSphere
            }
        ];
    }

    private static void BuildPrimitiveTransportProfilesForMaterial(
        int materialIndex,
        IReadOnlyList<ModelSubMesh> subMeshes,
        IReadOnlyList<ModelMaterial> materials,
        IReadOnlyDictionary<(int MaterialIndex, string PropertyName), TextureTransportImage> materialImages,
        GiPrimitiveTransportProfile?[] profiles)
    {
        for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
        {
            ModelSubMesh subMesh = subMeshes[subMeshIndex];
            int effectiveMaterialIndex =
                subMesh.MaterialIndex >= 0 &&
                subMesh.MaterialIndex < materials.Count
                ? subMesh.MaterialIndex
                : 0;
            if (effectiveMaterialIndex != materialIndex)
                continue;
            var textures = new GiPrimitiveTextureInputs(
                BaseColor: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.BaseColorTexture)),
                MetallicRoughness: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.MetallicRoughnessTexture)),
                Occlusion: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.OcclusionTexture)),
                Emissive: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.EmissiveTexture)),
                Normal: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.NormalTexture)),
                Clearcoat: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.ClearcoatTexture)),
                SheenColor: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.SheenColorTexture)),
                Transmission: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.TransmissionTexture)),
                Specular: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.SpecularTexture)),
                SpecularColor: GetImage(materialImages, effectiveMaterialIndex, nameof(ModelMaterial.SpecularColorTexture)));
            profiles[subMeshIndex] = GiPrimitiveTransportProfileGenerator.Generate(
                subMeshIndex,
                subMesh,
                materials[effectiveMaterialIndex],
                textures);
        }
    }

    private static TextureTransportImage? GetImage(
        IReadOnlyDictionary<(int MaterialIndex, string PropertyName), TextureTransportImage> images,
        int materialIndex,
        string propertyName) =>
        images.TryGetValue((materialIndex, propertyName), out TextureTransportImage? image) ? image : null;

    private static ModelMaterial CloneMaterial(ModelMaterial source)
    {
        var clone = new ModelMaterial();
        foreach (System.Reflection.PropertyInfo property in typeof(ModelMaterial).GetProperties().Where(p => p.CanRead && p.CanWrite))
            property.SetValue(clone, property.GetValue(source));
        return clone;
    }

    private static byte[] ReadStableTextureSourceBytes(ModelTextureSource source)
    {
        if (source.Bytes is { Length: > 0 } bytes)
        {
            if (WebPTextureDecoder.IsDeclaredWebP(source, bytes) &&
                bytes.Length > WebPTextureDecoder.DefaultMaximumEncodedBytes)
            {
                throw new NotSupportedException(
                    $"WebP texture '{ResolveTextureSourceIdentity(source)}' contains " +
                    $"{bytes.Length} encoded bytes, exceeding the decode limit " +
                    $"{WebPTextureDecoder.DefaultMaximumEncodedBytes}.");
            }

            return bytes.ToArray();
        }

        if (string.IsNullOrWhiteSpace(source.FilePath))
        {
            throw new InvalidDataException(
                $"Texture '{ResolveTextureSourceIdentity(source)}' has no source data.");
        }

        string fullPath = Path.GetFullPath(source.FilePath);
        if (WebPTextureDecoder.IsDeclaredWebP(source) ||
            WebPTextureDecoder.FileHasWebPSignature(fullPath))
        {
            return WebPTextureDecoder.ReadBoundedFile(
                fullPath,
                ResolveTextureSourceIdentity(source));
        }

        return AssetArtifactFileIo.ReadBoundedSnapshot(
            fullPath,
            AssetArtifactFileIo.MaximumCookSourceBytes,
            "Texture source");
    }

    private static ModelTextureSource CreateMemoryBackedTextureSource(
        ModelTextureSource source,
        byte[] encoded) =>
        new()
        {
            DebugName = source.DebugName,
            SourceKind = source.SourceKind,
            Bytes = encoded,
            MimeType = source.MimeType,
            CacheIdentity = source.CacheIdentity,
            ContainerKind = source.ContainerKind,
            EncodedByteLength = encoded.Length
        };

    private static bool TryReuseCookedTexture(
        string ktxPath,
        ModelTextureSource source,
        byte[] sourceBytes,
        ulong sourceHash,
        string identity,
        TextureSemantic semantic,
        ModelTextureSlot slot,
        bool preserveAlphaCoverage,
        TextureCookOptions textureOptions,
        uint toolVersion,
        out CookedTextureReport report,
        out TextureTransportImage? transportImage)
    {
        report = null!;
        transportImage = null;
        string metaPath = Path.ChangeExtension(ktxPath, ".njtex");
        if (!File.Exists(ktxPath) || !File.Exists(metaPath))
            return false;

        try
        {
            using (var metadataReader = new CookedAssetReader(
                       metaPath,
                       CookedAssetKind.Texture))
            {
                if (metadataReader.Header.BuildToolVersion != toolVersion)
                    return false;
            }

            byte[] ktxBytes = AssetArtifactFileIo.ReadBoundedSnapshot(
                ktxPath,
                AssetArtifactFileIo.MaximumCookSourceBytes,
                "Reusable cooked texture");
            var contract = new CookedTextureRuntimeContract(
                identity,
                semantic,
                slot.ColorSpace,
                slot.Sampler,
                preserveAlphaCoverage,
                preserveAlphaCoverage ? textureOptions.AlphaCutoff : null);
            AuthenticatedCookedTexture authenticated =
                CookedTextureAuthentication.Authenticate(
                    ktxPath,
                    ktxBytes,
                    contract);
            CookedTextureMeta metadata = authenticated.Metadata;
            if (metadata.SourceHash != sourceHash)
                return false;

            TextureTransportSourceAnalysis analysis =
                TextureCooker.AnalyzeTransportSource(
                    sourceBytes,
                    source.ContainerKind,
                    identity,
                    textureOptions with
                    {
                        ColorSpace = metadata.ColorSpace
                    },
                    AssetArtifactFileIo.MaximumCookSourceBytes,
                    int.MaxValue);
            if (analysis.Statistics.SourceContentHash != sourceHash ||
                analysis.Statistics.IsValid !=
                metadata.TransportStatistics.IsValid)
            {
                return false;
            }

            transportImage = analysis.Image;
            report = new CookedTextureReport(
                metadata.SourceIdentity,
                metadata.OriginalWidth,
                metadata.OriginalHeight,
                metadata.CookedWidth,
                metadata.CookedHeight,
                metadata.VulkanFormat,
                metadata.MipCount,
                sourceBytes.LongLength,
                metadata.EncodedBytes,
                PassedThrough: false)
            {
                TransportStatistics = metadata.TransportStatistics,
                AlphaCoveragePreserved =
                    metadata.AlphaCoveragePreserved,
                AlphaCutoff = textureOptions.AlphaCutoff,
                LinearAverageColor =
                    metadata.TransportStatistics.IsValid
                        ? metadata.TransportStatistics.LinearChannelMean.ToVector4()
                        : null,
                SourceTransportImage = transportImage
            };
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or
                  InvalidDataException or
                  NotSupportedException or
                  ArgumentException or
                  OverflowException)
        {
            return false;
        }
    }

    private static string ResolveTextureSourceIdentity(ModelTextureSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.CacheIdentity))
            return source.CacheIdentity;
        if (!string.IsNullOrWhiteSpace(source.FilePath))
            return Path.GetFullPath(source.FilePath);
        if (!string.IsNullOrWhiteSpace(source.DebugName))
            return source.DebugName;
        return "UnnamedTexture";
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
            byte[] sourceJson = AssetArtifactFileIo.ReadBoundedSnapshot(
                sourcePath,
                AssetArtifactFileIo.MaximumCookSourceBytes,
                "glTF dependency document");
            JsonNode? root = JsonNode.Parse(sourceJson);
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

    private static void TryDeleteUnpublishedArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original cook failure. Generation-qualified
            // leftovers are unreferenced and CleanStale can reclaim them.
        }
    }

    private static string NormalizeRelative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

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
