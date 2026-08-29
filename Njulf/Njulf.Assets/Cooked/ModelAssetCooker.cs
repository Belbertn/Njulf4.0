using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Njulf.Assets.Validation;

namespace Njulf.Assets.Cooked;

public sealed class ModelAssetCooker : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gltf", ".glb", ".obj", ".fbx", ".dae", ".3ds", ".blend", ".ply", ".stl"
    };

    // Model material shape is process-invariant. Keeping these property lists
    // outside the inner material loop also makes progress denominators cheap.
    private static readonly System.Reflection.PropertyInfo[] TextureSlotProperties =
        typeof(ModelMaterial).GetProperties()
            .Where(property => property.PropertyType == typeof(ModelTextureSlot) &&
                               property.CanRead && property.CanWrite)
            .ToArray();

    private static readonly System.Reflection.PropertyInfo[] ReadableTextureSlotProperties =
        typeof(ModelMaterial).GetProperties()
            .Where(property => property.PropertyType == typeof(ModelTextureSlot) &&
                               property.CanRead)
            .ToArray();

    private static readonly System.Reflection.PropertyInfo[] WritableTexturePathProperties =
        typeof(ModelMaterial).GetProperties()
            .Where(property => property.PropertyType == typeof(string) &&
                               property.Name.EndsWith("TexturePath", StringComparison.Ordinal) &&
                               property.CanWrite)
            .ToArray();

    private readonly ModelImporter _importer;
    private readonly ProcessedMeshAssetBuilder _meshBuilder;
    private readonly ITextureCooker _textureCooker;
    private readonly bool _usesDefaultWorkerServices;
    private readonly RendererMeshletBuildProfile? _meshletBuildProfile;
    private bool _disposed;

    private sealed class CookProgressContext
    {
        private readonly IAssetCookProgressSink? _sink;
        private readonly Stopwatch _runTimer;
        private readonly Stopwatch _assetTimer = Stopwatch.StartNew();
        private AssetCookStage? _activeStage;

        public CookProgressContext(
            IAssetCookProgressSink? sink,
            Stopwatch runTimer,
            string sourcePath,
            int? assetIndex,
            int? assetCount,
            CancellationToken cancellationToken)
        {
            _sink = sink;
            _runTimer = runTimer;
            SourcePath = sourcePath;
            AssetIndex = assetIndex;
            AssetCount = assetCount;
            CancellationToken = cancellationToken;
        }

        public string SourcePath { get; private set; }
        public int? AssetIndex { get; }
        public int? AssetCount { get; }
        public CancellationToken CancellationToken { get; }

        public void SetSourcePath(string sourcePath) => SourcePath = sourcePath;

        public void ThrowIfCancellationRequested() =>
            CancellationToken.ThrowIfCancellationRequested();

        public void Report(AssetCookProgressEvent progress)
        {
            if (_sink is null)
                return;

            try
            {
                _sink.Report(progress with
                {
                    SourcePath = progress.SourcePath ?? SourcePath,
                    AssetIndex = progress.AssetIndex ?? AssetIndex,
                    AssetCount = progress.AssetCount ?? AssetCount,
                    TotalElapsedMilliseconds =
                        progress.TotalElapsedMilliseconds ?? _runTimer.ElapsedMilliseconds
                });
            }
            catch
            {
                // Progress is diagnostic. A closed log pipe or a third-party
                // observer must not corrupt an otherwise valid cook.
            }
        }

        public void ReportStageStart(AssetCookStage stage, int? materialCount = null, int? textureSlotCount = null)
        {
            _activeStage = stage;
            Report(new AssetCookProgressEvent(AssetCookProgressEventKind.StageStarted)
            {
                Stage = stage,
                MaterialCount = materialCount,
                TextureSlotCount = textureSlotCount
            });
        }

        public void ReportStageCompleted(AssetCookStage stage, long elapsedMilliseconds, string? backend = null)
        {
            Report(new AssetCookProgressEvent(AssetCookProgressEventKind.StageCompleted)
            {
                Stage = stage,
                StageElapsedMilliseconds = elapsedMilliseconds,
                Backend = backend
            });
            if (_activeStage == stage)
                _activeStage = null;
        }

        public void ReportAssetStarted() =>
            Report(new AssetCookProgressEvent(AssetCookProgressEventKind.AssetStarted));

        public void ReportAssetOutcome(
            AssetCookProgressEventKind kind,
            AssetCookProgressOutcome outcome,
            int? meshCount = null,
            int? textureCount = null,
            int? warningCount = null,
            string? message = null) =>
            Report(new AssetCookProgressEvent(kind)
            {
                Stage = _activeStage,
                Outcome = outcome,
                MeshCount = meshCount,
                TextureCount = textureCount,
                WarningCount = warningCount,
                Message = message,
                AssetElapsedMilliseconds = _assetTimer.ElapsedMilliseconds
            });
    }

    /// <summary>
    /// State shared by every asset in one folder invocation. The database is
    /// loaded once and each successful asset still takes its own locked,
    /// atomic checkpoint. All mutable per-asset package work remains local.
    /// </summary>
    private sealed class CookRunSession
    {
        private readonly object _databaseGate = new();
        private readonly ConcurrentDictionary<string, object> _textureKeyLocks =
            new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, ulong> _artifactHashes =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Lazy<string>> _artifactSignatures =
            new(StringComparer.OrdinalIgnoreCase);
        private CookedAssetDatabase? _database;

        public CookRunSession(string outputRoot, ModelCookOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            OutputRoot = options.UsePlatformSubdirectory
                ? CookedPlatform.ResolveOutputRoot(outputRoot, options.Platform)
                : Path.GetFullPath(outputRoot);
            ModelDirectory = Path.Combine(OutputRoot, "models");
            MaterialDirectory = Path.Combine(OutputRoot, "materials");
            TextureDirectory = Path.Combine(OutputRoot, "textures");
            ReportDirectory = Path.Combine(OutputRoot, "reports");
            DatabasePath = Path.Combine(OutputRoot, "assetdb.njassetdb");
            PlatformTextureOptions = options.TextureOptions with
            {
                TargetFormatPolicy = CookedPlatform.ResolveTexturePolicy(
                    options.Platform,
                    options.TextureOptions.TargetFormatPolicy)
            };
            SettingsHash = ComputeSettingsHash(options, PlatformTextureOptions);
        }

        public string OutputRoot { get; }
        public string ModelDirectory { get; }
        public string MaterialDirectory { get; }
        public string TextureDirectory { get; }
        public string ReportDirectory { get; }
        public string DatabasePath { get; }
        public TextureCookOptions PlatformTextureOptions { get; }
        public ulong SettingsHash { get; }

        public void EnsureInitialized()
        {
            lock (_databaseGate)
            {
                if (_database is not null)
                    return;

                Directory.CreateDirectory(OutputRoot);
                Directory.CreateDirectory(ModelDirectory);
                Directory.CreateDirectory(MaterialDirectory);
                Directory.CreateDirectory(TextureDirectory);
                Directory.CreateDirectory(ReportDirectory);
                _database = CookedAssetDatabase.Load(DatabasePath);
            }
        }

        public CookedAssetDatabaseEntry? GetEntry(string databaseKey)
        {
            EnsureInitialized();
            lock (_databaseGate)
            {
                return _database!.Assets.TryGetValue(
                    databaseKey,
                    out CookedAssetDatabaseEntry? entry)
                    ? entry
                    : null;
            }
        }

        public void CommitEntry(
            string databaseKey,
            CookedAssetDatabaseEntry entry)
        {
            ArgumentNullException.ThrowIfNull(entry);
            EnsureInitialized();
            lock (_databaseGate)
            {
                _database!.Assets[databaseKey] = entry;
                _database.SaveAtomic(DatabasePath);
            }
        }

        public object GetTextureKeyLock(string key) =>
            _textureKeyLocks.GetOrAdd(key, static _ => new object());

        public ulong GetOrRecordArtifactHash(string path)
        {
            string fullPath = Path.GetFullPath(path);
            return _artifactHashes.GetOrAdd(fullPath, static candidate =>
                CookedHash.File(candidate));
        }

        public void InvalidateArtifactHash(string path) =>
            _artifactHashes.TryRemove(Path.GetFullPath(path), out _);

        public void SetArtifactHash(string path, ulong contentHash) =>
            _artifactHashes[Path.GetFullPath(path)] = contentHash;

        public string SignArtifact(string path, string signingPrivateKey)
        {
            string fullPath = Path.GetFullPath(path);
            Lazy<string> operation = _artifactSignatures.GetOrAdd(
                fullPath,
                candidate => new Lazy<string>(
                    () => CookedPackageSigner.SignFile(
                        candidate,
                        signingPrivateKey),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                string signaturePath = operation.Value;
                InvalidateArtifactHash(signaturePath);
                return signaturePath;
            }
            catch
            {
                if (_artifactSignatures.TryGetValue(fullPath, out Lazy<string>? current) &&
                    ReferenceEquals(current, operation))
                {
                    _artifactSignatures.TryRemove(fullPath, out _);
                }
                throw;
            }
        }
    }

    /// <summary>
    /// Keeps one stable encoded source snapshot only while later material
    /// slots can still consume it. This avoids repeat file reads/hashes for
    /// common shared textures without retaining every source image for the
    /// entire cook or trusting timestamps as identity.
    /// </summary>
    private sealed class TextureSourceSnapshotCache
    {
        private const long MaximumRetainedBytes = 128L * 1024L * 1024L;
        private readonly Dictionary<string, int> _remainingUses =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, TextureSourceSnapshot> _snapshots =
            new(StringComparer.Ordinal);
        private readonly Dictionary<ModelTextureSource, string> _anonymousKeys =
            new(ReferenceEqualityComparer.Instance);
        private long _retainedBytes;
        private int _anonymousSequence;

        public TextureSourceSnapshotCache(IReadOnlyList<ModelMaterial> materials)
        {
            foreach (ModelMaterial material in materials)
            {
                foreach (System.Reflection.PropertyInfo property in TextureSlotProperties)
                {
                    if (property.GetValue(material) is not ModelTextureSlot
                        {
                            Source: { } source
                        })
                    {
                        continue;
                    }

                    string key = GetKey(source);
                    _remainingUses.TryGetValue(key, out int count);
                    _remainingUses[key] = checked(count + 1);
                }
            }
        }

        public string GetKey(ModelTextureSource source)
        {
            if (!string.IsNullOrWhiteSpace(source.FilePath))
                return "file:" + Path.GetFullPath(source.FilePath);
            // Embedded data has no independently re-openable stable path.
            // Cache only the exact source object; a logical CacheIdentity can
            // legitimately be reused by distinct importer-owned byte buffers.
            if (_anonymousKeys.TryGetValue(source, out string? existing))
                return existing;

            string created = "object:" +
                (++_anonymousSequence).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            _anonymousKeys.Add(source, created);
            return created;
        }

        public TextureSourceSnapshot Capture(
            ModelTextureSource source,
            string key)
        {
            if (_snapshots.TryGetValue(key, out TextureSourceSnapshot snapshot))
                return snapshot;

            byte[] bytes = ReadStableTextureSourceBytes(source);
            snapshot = new TextureSourceSnapshot(bytes, CookedHash.Bytes(bytes));
            if (_remainingUses.TryGetValue(key, out int remaining) &&
                remaining > 1 &&
                bytes.LongLength <= MaximumRetainedBytes &&
                _retainedBytes <= MaximumRetainedBytes - bytes.LongLength)
            {
                _snapshots.Add(key, snapshot);
                _retainedBytes += bytes.LongLength;
            }
            return snapshot;
        }

        public void Release(string key)
        {
            if (!_remainingUses.TryGetValue(key, out int remaining))
                return;

            remaining--;
            if (remaining > 0)
            {
                _remainingUses[key] = remaining;
                return;
            }

            _remainingUses.Remove(key);
            if (_snapshots.Remove(key, out TextureSourceSnapshot snapshot))
                _retainedBytes -= snapshot.Bytes.LongLength;
        }
    }

    private readonly record struct TextureSourceSnapshot(
        byte[] Bytes,
        ulong ContentHash);

    /// <summary>
    /// Reuses immutable decoded transport images for compatible texture slots
    /// across materials. Pixels are the expensive representation, so this is
    /// deliberately hard-capped and simply declines new entries once full.
    /// </summary>
    private sealed class BoundedTransportImageCache
    {
        private const long MaximumRetainedBytes = 128L * 1024L * 1024L;
        private readonly Dictionary<string, TextureTransportImage?> _images =
            new(StringComparer.Ordinal);
        private long _retainedBytes;

        public bool TryGet(string key, out TextureTransportImage? image) =>
            _images.TryGetValue(key, out image);

        public void Add(string key, TextureTransportImage? image)
        {
            if (_images.ContainsKey(key))
                return;

            long bytes = EstimateBytes(image);
            if (bytes > MaximumRetainedBytes ||
                _retainedBytes > MaximumRetainedBytes - bytes)
            {
                return;
            }

            _images.Add(key, image);
            _retainedBytes += bytes;
        }

        private static long EstimateBytes(TextureTransportImage? image)
        {
            if (image is null || image.Width <= 0 || image.Height <= 0)
                return 0;
            return checked((long)image.Width * image.Height * 4 * sizeof(double));
        }
    }

    public ModelAssetCooker()
        : this(RendererMeshletBuildProfiles.Production)
    {
    }

    public ModelAssetCooker(RendererMeshletBuildProfile meshletBuildProfile)
        : this(
            new ModelImporter(),
            new ProcessedMeshAssetBuilder(
                meshletBuildProfile ??
                throw new ArgumentNullException(nameof(meshletBuildProfile))),
            new TextureCooker(),
            usesDefaultWorkerServices: true,
            meshletBuildProfile)
    {
    }

    public ModelAssetCooker(ModelImporter importer, ProcessedMeshAssetBuilder meshBuilder, ITextureCooker textureCooker)
        : this(
            importer,
            meshBuilder,
            textureCooker,
            usesDefaultWorkerServices: false,
            meshletBuildProfile: null)
    {
    }

    private ModelAssetCooker(
        ModelImporter importer,
        ProcessedMeshAssetBuilder meshBuilder,
        ITextureCooker textureCooker,
        bool usesDefaultWorkerServices,
        RendererMeshletBuildProfile? meshletBuildProfile)
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _meshBuilder = meshBuilder ?? throw new ArgumentNullException(nameof(meshBuilder));
        _textureCooker = textureCooker ?? throw new ArgumentNullException(nameof(textureCooker));
        _usesDefaultWorkerServices = usesDefaultWorkerServices;
        _meshletBuildProfile = meshletBuildProfile;
    }

    public AssetCookResult CookModel(
        string sourcePath,
        string outputRoot,
        ModelCookOptions? options = null,
        IAssetCookProgressSink? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new ModelCookOptions();
        var runTimer = Stopwatch.StartNew();
        var context = new CookProgressContext(
            progress,
            runTimer,
            sourcePath,
            assetIndex: 1,
            assetCount: 1,
            cancellationToken);
        var session = new CookRunSession(outputRoot, options);
        return CookModelCore(sourcePath, options, session, context);
    }

    private AssetCookResult CookModelCore(
        string sourcePath,
        ModelCookOptions options,
        CookRunSession session,
        CookProgressContext progress)
    {
        try
        {
        sourcePath = Path.GetFullPath(sourcePath);
        progress.SetSourcePath(sourcePath);
        progress.ReportAssetStarted();
        progress.ThrowIfCancellationRequested();
        string outputRoot = session.OutputRoot;

        progress.ReportStageStart(AssetCookStage.Prepare);
        var stageTimer = Stopwatch.StartNew();
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Model source was not found.", sourcePath);
        if (!SupportedExtensions.Contains(Path.GetExtension(sourcePath)))
            throw new NotSupportedException($"Asset cooker does not support model extension '{Path.GetExtension(sourcePath)}'.");

        session.EnsureInitialized();
        string modelDirectory = session.ModelDirectory;
        string materialDirectory = session.MaterialDirectory;
        string textureDirectory = session.TextureDirectory;
        string reportDirectory = session.ReportDirectory;

        TextureCookOptions platformTextureOptions = session.PlatformTextureOptions;

        string stem = SanitizeName(Path.GetFileNameWithoutExtension(sourcePath));
        string modelPath = Path.Combine(modelDirectory, stem + ".njmodel");
        string reportPath = Path.Combine(reportDirectory, stem + ".cook-report.json");
        string databaseKey = NormalizeRelative(outputRoot, sourcePath);
        CookedAssetDatabaseEntry? previousEntry =
            session.GetEntry(databaseKey);
        if (File.Exists(modelPath))
        {
            try
            {
                using var existingReader = new CookedAssetReader(
                    modelPath,
                    CookedAssetKind.Model);
                CookedModelManifest existingManifest =
                    CookedJson.Deserialize<CookedModelManifest>(
                        existingReader.GetRequiredSection(
                            CookedSectionIds.Manifest).Span,
                        modelPath,
                        "manifest");
                if (!Path.GetFullPath(existingManifest.SourcePath).Equals(
                        sourcePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Cook output collision: source '{sourcePath}' and '{existingManifest.SourcePath}' both map to '{modelPath}'. " +
                        "Cook them to separate output roots or give the source files distinct base names.");
                }
            }
            catch (CookedAssetFormatException) when (
                PreviousEntryOwnsModelOutput(
                    previousEntry,
                    sourcePath,
                    outputRoot,
                    modelPath))
            {
                // A hard format boundary must reject the old package at
                // runtime, but a source-backed cook still needs to replace it.
                // Database ownership preserves the output-collision guard.
            }
        }
        long prepareMs = stageTimer.ElapsedMilliseconds;
        progress.ReportStageCompleted(AssetCookStage.Prepare, prepareMs);
        progress.ThrowIfCancellationRequested();

        progress.ReportStageStart(AssetCookStage.IncrementalCheck);
        stageTimer.Restart();
        ulong sourceHash = CookedHash.File(sourcePath);
        ulong importContractHash = CookedModelImportContract.Compute(
            sourcePath,
            options.ImporterOptions);
        ulong settingsHash = CookedHash.Ordered(new[]
        {
            ("cook-settings", session.SettingsHash),
            ("model-import-contract", importContractHash)
        });
        var dependencies = new SortedDictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, ulong hash) in DiscoverDependencies(sourcePath))
            dependencies[path] = hash;
        if (previousEntry is not null)
        {
            foreach (string dependencyPath in previousEntry.Dependencies.Keys)
                dependencies[dependencyPath] = File.Exists(dependencyPath) ? CookedHash.File(dependencyPath) : 0;
        }
        ulong dependencyHash = CookedHash.Ordered(dependencies.Select(pair => (pair.Key, pair.Value)));
        AssetCookIncrementalReason incrementalReason = DetermineIncrementalReason(
            options,
            previousEntry,
            sourceHash,
            settingsHash,
            dependencyHash,
            outputRoot);
        long incrementalMs = stageTimer.ElapsedMilliseconds;
        bool skipIncremental = incrementalReason == AssetCookIncrementalReason.Unchanged;
        progress.Report(new AssetCookProgressEvent(AssetCookProgressEventKind.IncrementalCompleted)
        {
            Stage = AssetCookStage.IncrementalCheck,
            IncrementalDecision = skipIncremental
                ? AssetCookIncrementalDecision.Skip
                : AssetCookIncrementalDecision.Cook,
            IncrementalReason = incrementalReason,
            StageElapsedMilliseconds = incrementalMs
        });
        progress.ReportStageCompleted(AssetCookStage.IncrementalCheck, incrementalMs);
        progress.ThrowIfCancellationRequested();
        if (skipIncremental)
        {
            AssetCookReport skippedReport = File.Exists(reportPath)
                ? AssetCookReportJson.Read(reportPath)
                : CreateSkippedReport(sourcePath, previousEntry!.Outputs);
            var skipped = new AssetCookResult(skippedReport, true);
            progress.ReportAssetOutcome(
                AssetCookProgressEventKind.AssetSkipped,
                AssetCookProgressOutcome.Skipped,
                meshCount: skippedReport.SubMeshCount,
                textureCount: skippedReport.TextureCount,
                warningCount: skippedReport.Warnings.Count);
            return skipped;
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
        progress.ReportStageStart(AssetCookStage.Import);
        var timer = Stopwatch.StartNew();
        ModelImportResult import = _importer.ImportDetailed(sourcePath, options.ImporterOptions);
        ModelMesh model = import.EnsureImported();
        foreach ((string path, ulong hash) in DiscoverModelDependencies(model))
            dependencies[path] = hash;
        dependencyHash = CookedHash.Ordered(dependencies.Select(pair => (pair.Key, pair.Value)));
        long importMs = timer.ElapsedMilliseconds;
        progress.ReportStageCompleted(
            AssetCookStage.Import,
            importMs,
            import.Backend.ToString());
        progress.ThrowIfCancellationRequested();
        foreach (AssetImportMessage message in import.Diagnostics.Messages.Where(message => message.Severity != AssetImportSeverity.Info))
            warnings.Add($"{message.Code}: {message.Message}");

        progress.ReportStageStart(AssetCookStage.Mesh);
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
        progress.ReportStageCompleted(AssetCookStage.Mesh, meshMs);
        progress.ThrowIfCancellationRequested();

        int textureSlotCount = CountOccupiedTextureSlots(model);
        progress.ReportStageStart(
            AssetCookStage.MaterialsTextures,
            materialCount: Math.Max(1, model.Materials.Count),
            textureSlotCount: textureSlotCount);
        timer.Restart();
        CookedMaterialTable materials = CookMaterials(
            model,
            materialDirectory,
            textureDirectory,
            platformTextureOptions,
            options.ToolVersion,
            textureReports,
            progress,
            textureSlotCount,
            session);
        long textureMs = timer.ElapsedMilliseconds;
        progress.ReportStageCompleted(AssetCookStage.MaterialsTextures, textureMs);
        progress.ThrowIfCancellationRequested();

        progress.ReportStageStart(AssetCookStage.Serialize);
        timer.Restart();
        string meshletSidecarPath = CookedPackage.WriteMeshWithSidecar(
            meshPath,
            mesh,
            sourceHash,
            settingsHash,
            dependencyHash,
            options.ToolVersion,
            CookedPlatform.SupportsMeshOptimizer(options.Platform));
        generationArtifacts.Add(meshletSidecarPath);
        session.InvalidateArtifactHash(meshPath);
        session.InvalidateArtifactHash(meshletSidecarPath);
        ulong meshContentHash = session.GetOrRecordArtifactHash(meshPath);
        CookedPackage.WriteMaterials(materialPath, materials, sourceHash, settingsHash, dependencyHash, options.ToolVersion);
        session.InvalidateArtifactHash(materialPath);
        ulong materialContentHash = session.GetOrRecordArtifactHash(materialPath);
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
            session.InvalidateArtifactHash(animationPath);
            animationReference = new CookedAssetReference(
                Path.GetFileName(animationPath),
                session.GetOrRecordArtifactHash(animationPath));
        }
        var manifest = new CookedModelManifest(
            CookedPackage.StableAssetId(sourcePath),
            model.Name,
            sourcePath.Replace('\\', '/'),
            sourceHash,
            importContractHash,
            dependencyHash,
            new CookedAssetReference(Path.GetFileName(meshPath), meshContentHash),
            new CookedAssetReference(NormalizeRelative(modelDirectory, materialPath), materialContentHash),
            animationReference,
            processed.SubMeshes.Select((subMesh, index) => new CookedModelSubObject(
                subMesh.Name, index, subMesh.MaterialSlot, subMesh.NodeIndex, subMesh.SkinIndex, subMesh.SkinningBindTransform)).ToArray(),
            processed.BoundingBox,
            processed.BoundingSphere)
        {
            Lights = model.Lights.ToArray()
        };
        CookedOpacityMicromapModelChunk? opacityMicromapChunk =
            TryProduceOpacityMicromapChunk(
                options,
                sourcePath,
                manifest,
                settingsHash,
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
        progress.ReportStageCompleted(AssetCookStage.Serialize, serializationMs);
        progress.ThrowIfCancellationRequested();

        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            meshPath,
            meshletSidecarPath,
            materialPath
        };
        if (animationReference is not null)
            outputPaths.Add(animationPath);
        foreach (ModelMaterial material in materials.Materials)
            foreach (System.Reflection.PropertyInfo property in ReadableTextureSlotProperties)
            {
                if (property.GetValue(material) is not ModelTextureSlot { Source.FilePath: { } texturePath })
                    continue;
                string absoluteTexturePath = Path.GetFullPath(Path.Combine(materialDirectory, texturePath));
                outputPaths.Add(absoluteTexturePath);
                outputPaths.Add(Path.ChangeExtension(absoluteTexturePath, ".njtex"));
            }
        if (!string.IsNullOrWhiteSpace(options.SigningPrivateKey))
        {
            progress.ReportStageStart(AssetCookStage.Sign);
            timer.Restart();
            foreach (string path in outputPaths.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray())
                outputPaths.Add(session.SignArtifact(path, options.SigningPrivateKey));
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
            session.InvalidateArtifactHash(publishedSignaturePath);
            signaturePublished = true;
            outputPaths.Add(publishedSignaturePath);
            long signingMs = timer.ElapsedMilliseconds;
            progress.ReportStageCompleted(AssetCookStage.Sign, signingMs);
            progress.ThrowIfCancellationRequested();
        }

        progress.ReportStageStart(AssetCookStage.Publish);
        timer.Restart();
        File.Move(stagedModelPath, modelPath, overwrite: true);
        session.InvalidateArtifactHash(modelPath);
        modelPublished = true;
        if (previousSignatureBackupPath != null)
            TryDeleteUnpublishedArtifact(previousSignatureBackupPath);
        outputPaths.Add(modelPath);
        long publishMs = timer.ElapsedMilliseconds;
        progress.ReportStageCompleted(AssetCookStage.Publish, publishMs);

        // Once the stable model publication point has moved, finish the
        // report/database checkpoint instead of letting cancellation leave a
        // valid published package untracked by incremental cooking.
        progress.ReportStageStart(AssetCookStage.ReportDatabase);
        timer.Restart();
        var outputs = new SortedDictionary<string, ulong>(StringComparer.Ordinal);
        foreach (string path in outputPaths.Where(File.Exists).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            outputs[NormalizeRelative(outputRoot, path)] =
                session.GetOrRecordArtifactHash(path);

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
        session.CommitEntry(databaseKey, new CookedAssetDatabaseEntry
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
        });
        long reportDatabaseMs = timer.ElapsedMilliseconds;
        progress.ReportStageCompleted(AssetCookStage.ReportDatabase, reportDatabaseMs);
        var result = new AssetCookResult(report, false);
        progress.ReportAssetOutcome(
            AssetCookProgressEventKind.AssetCompleted,
            AssetCookProgressOutcome.Succeeded,
            meshCount: processed.SubMeshes.Count,
            textureCount: textureReports.Count,
            warningCount: warnings.Count);
        return result;
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
        catch (OperationCanceledException)
        {
            progress.ReportAssetOutcome(
                AssetCookProgressEventKind.AssetCancelled,
                AssetCookProgressOutcome.Cancelled,
                message: "Cook cancelled.");
            throw;
        }
        catch (Exception exception)
        {
            progress.ReportAssetOutcome(
                AssetCookProgressEventKind.AssetFailed,
                AssetCookProgressOutcome.Failed,
                message: exception.Message);
            throw;
        }
    }

    public IReadOnlyList<AssetCookResult> CookFolder(
        string sourceFolder,
        string outputRoot,
        ModelCookOptions? options = null,
        IAssetCookProgressSink? progress = null,
        CancellationToken cancellationToken = default,
        AssetCookFolderOptions? folderOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        sourceFolder = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(sourceFolder))
            throw new DirectoryNotFoundException($"Source folder '{sourceFolder}' was not found.");

        folderOptions ??= new AssetCookFolderOptions();
        if (folderOptions.MaxDegreeOfParallelism <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(folderOptions),
                "MaxDegreeOfParallelism must be positive.");
        }
        if (folderOptions.MaxInflightBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(folderOptions),
                "MaxInflightBytes must be positive.");
        }

        options ??= new ModelCookOptions();
        var session = new CookRunSession(outputRoot, options);

        var runTimer = Stopwatch.StartNew();
        var folderProgress = new CookProgressContext(
            progress,
            runTimer,
            sourceFolder,
            assetIndex: null,
            assetCount: null,
            cancellationToken);
        folderProgress.Report(new AssetCookProgressEvent(
            AssetCookProgressEventKind.DiscoveryStarted));
        string[] sources = DiscoverFolderSources(sourceFolder);
        folderProgress.Report(new AssetCookProgressEvent(
            AssetCookProgressEventKind.DiscoveryCompleted)
        {
            AssetCount = sources.Length
        });
        ValidateFolderOutputCollisions(sources, session.ModelDirectory);

        if (folderOptions.MaxDegreeOfParallelism > 1 && !_usesDefaultWorkerServices)
        {
            throw new InvalidOperationException(
                "Parallel folder cooking requires default worker-local cooker services. " +
                "Use one job with an injected ModelAssetCooker or construct the default cooker for parallel work.");
        }

        if (sources.Length > 1 && folderOptions.MaxDegreeOfParallelism > 1)
        {
            return CookFolderParallel(
                sources,
                options,
                session,
                progress,
                runTimer,
                cancellationToken,
                folderOptions);
        }

        var results = new AssetCookResult[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            folderProgress.ThrowIfCancellationRequested();
            var assetProgress = new CookProgressContext(
                progress,
                runTimer,
                sources[index],
                index + 1,
                sources.Length,
                cancellationToken);
            results[index] = CookModelCore(
                sources[index],
                options,
                session,
                assetProgress);
        }

        return results;
    }

    internal static string[] DiscoverFolderSources(string sourceFolder)
    {
        sourceFolder = Path.GetFullPath(sourceFolder);
        return Directory.EnumerateFiles(
                sourceFolder,
                "*",
                SearchOption.AllDirectories)
            .Where(path => !IsBuildOutputPath(sourceFolder, path))
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsBuildOutputPath(string sourceFolder, string path)
    {
        string relativePath = Path.GetRelativePath(sourceFolder, path);
        return relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part =>
                string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<AssetCookResult> CookFolderParallel(
        IReadOnlyList<string> sources,
        ModelCookOptions options,
        CookRunSession session,
        IAssetCookProgressSink? progress,
        Stopwatch runTimer,
        CancellationToken cancellationToken,
        AssetCookFolderOptions folderOptions)
    {
        var results = new AssetCookResult[sources.Count];
        var queue = new Queue<int>(Enumerable.Range(0, sources.Count));
        var schedulerGate = new object();
        long inflightBytes = 0;
        ExceptionDispatchInfo? failure = null;
        using var workerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int workerCount = Math.Min(
            folderOptions.MaxDegreeOfParallelism,
            sources.Count);
        var workers = new Task[workerCount];

        for (int workerIndex = 0; workerIndex < workerCount; workerIndex++)
        {
            int capturedWorkerIndex = workerIndex;
            workers[workerIndex] = Task.Run(() =>
            {
                ModelAssetCooker worker = capturedWorkerIndex == 0
                    ? this
                    : new ModelAssetCooker(
                        _meshletBuildProfile ??
                        RendererMeshletBuildProfiles.Production);
                bool ownsWorker = !ReferenceEquals(worker, this);
                try
                {
                    while (true)
                    {
                        int sourceIndex;
                        long reservation;
                        lock (schedulerGate)
                        {
                            while (true)
                            {
                                if (workerCancellation.IsCancellationRequested)
                                    return;
                                if (queue.Count == 0)
                                    return;

                                sourceIndex = queue.Peek();
                                reservation = GetInflightReservation(
                                    sources[sourceIndex],
                                    folderOptions.MaxInflightBytes);
                                if (inflightBytes + reservation <=
                                    folderOptions.MaxInflightBytes)
                                {
                                    queue.Dequeue();
                                    inflightBytes += reservation;
                                    break;
                                }

                                // The reservation for one over-budget input is
                                // clamped to the whole budget, so it runs alone
                                // after active work drains rather than starving.
                                Monitor.Wait(schedulerGate, millisecondsTimeout: 100);
                            }
                        }

                        try
                        {
                            var assetProgress = new CookProgressContext(
                                progress,
                                runTimer,
                                sources[sourceIndex],
                                sourceIndex + 1,
                                sources.Count,
                                workerCancellation.Token);
                            results[sourceIndex] = worker.CookModelCore(
                                sources[sourceIndex],
                                options,
                                session,
                                assetProgress);
                        }
                        catch (OperationCanceledException)
                            when (workerCancellation.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception exception)
                        {
                            lock (schedulerGate)
                            {
                                failure ??= ExceptionDispatchInfo.Capture(exception);
                            }
                            workerCancellation.Cancel();
                            return;
                        }
                        finally
                        {
                            lock (schedulerGate)
                            {
                                inflightBytes -= reservation;
                                Monitor.PulseAll(schedulerGate);
                            }
                        }
                    }
                }
                finally
                {
                    if (ownsWorker)
                        worker.Dispose();
                }
            });
        }

        Task.WaitAll(workers);
        failure?.Throw();
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    private static long GetInflightReservation(string sourcePath, long maximumBytes)
    {
        long sourceBytes;
        try
        {
            sourceBytes = new FileInfo(sourcePath).Length;
        }
        catch (IOException)
        {
            // The worker will produce the authoritative source-read failure.
            // Reserve a small unit rather than masking it in scheduling.
            sourceBytes = 1;
        }
        catch (UnauthorizedAccessException)
        {
            sourceBytes = 1;
        }

        sourceBytes = Math.Max(1, sourceBytes);
        return Math.Min(sourceBytes, maximumBytes);
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
        bool databaseChanged = removedSources.Length > 0;
        var referenced = database.Assets.Values
            .SelectMany(entry => entry.Outputs.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, CookedAssetDatabaseEntry entry) in
                 database.Assets.ToArray())
        {
            var outputs = new SortedDictionary<string, ulong>(
                StringComparer.Ordinal);
            foreach ((string path, ulong hash) in entry.Outputs)
                outputs.Add(path, hash);
            if (outputs.Keys.Any(path => path.EndsWith(
                    ".pages",
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (string relativeMeshPath in outputs.Keys
                         .Where(path => path.EndsWith(
                             ".njmesh",
                             StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                string meshPath = Path.GetFullPath(Path.Combine(
                    outputRoot,
                    relativeMeshPath));
                if (!File.Exists(meshPath))
                    continue;
                string sidecarPath = ResolveMeshletSidecarPath(meshPath);
                string relativeSidecarPath = NormalizeRelative(
                    outputRoot,
                    sidecarPath);
                outputs[relativeSidecarPath] = CookedHash.File(sidecarPath);
                referenced.Add(relativeSidecarPath);
                databaseChanged = true;
            }

            if (outputs.Count != entry.Outputs.Count)
            {
                database.Assets[key] = entry with
                {
                    Outputs = outputs
                };
            }
        }
        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(outputRoot, "*", SearchOption.AllDirectories))
        {
            string relative = NormalizeRelative(outputRoot, file);
            if (relative.EndsWith(".njassetdb", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".cook-report.json", StringComparison.OrdinalIgnoreCase) || referenced.Contains(relative))
                continue;
            if (Path.GetExtension(file) is ".njmodel" or ".njmesh" or ".njmat" or ".njanim" or ".njtex" or ".ktx2" or ".pages" or ".sig")
            {
                File.Delete(file);
                deleted++;
            }
        }
        if (databaseChanged)
            database.SaveAtomic(databasePath);
        return deleted;
    }

    private static ulong ComputeSettingsHash(
        ModelCookOptions options,
        TextureCookOptions platformTextureOptions) =>
        CookedHash.Bytes(CookedJson.Serialize(new
        {
            options.ImporterOptions,
            TextureOptions = platformTextureOptions,
            options.ToolVersion,
            options.Platform,
            CookedModelImportContract.MaterialTransportMetadataRevision,
            CookedModelImportContract.MaterialTexturePolicyRevision,
            AmazonBistroMaterialProfileRevision =
                options.ImporterOptions.AssimpMaterialTextureConvention ==
                    AssimpMaterialTextureConvention.AmazonBistro
                    ? AmazonBistroMaterialProfile.ProfileRevision
                    : string.Empty,
            CookedModelImportContract.MeshLodAlgorithmRevision,
            CausticTopologyAlgorithmVersion =
                ModelGiCausticHeroTopologyAnalyzer.CurrentAlgorithmVersion,
            OpacityMicromapPayloadProducer =
                CreateOpacityMicromapProducerSettingsHashInput(
                    options.OpacityMicromapPayloadProducer),
            TextureStatisticsAlgorithmVersion =
                TextureTransportStatistics.CurrentAlgorithmVersion,
            PrimitiveTransportAlgorithmVersion =
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            TextureTransportStatistics.StbDecoderVersion,
            TextureTransportStatistics.WebPDecoderVersion,
            TextureTransportStatistics.BcDecoderVersion,
            TextureTransportStatistics.DdsDecoderVersion,
            TextureTransportStatistics.KtxStatisticsDecoderVersion
        }));

    private static CookedOpacityMicromapModelChunk? TryProduceOpacityMicromapChunk(
        ModelCookOptions options,
        string sourcePath,
        CookedModelManifest manifest,
        ulong cookSettingsHash,
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
                cookSettingsHash,
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
        List<CookedTextureReport> reports,
        CookProgressContext progress,
        int textureSlotCount,
        CookRunSession session)
    {
        IReadOnlyList<ModelMaterial> sourceMaterials = model.Materials;
        ModelMaterial[] materials = sourceMaterials.Count == 0
            ? [ModelMaterial.Default]
            : sourceMaterials.Select(CloneMaterial).ToArray();
        var sourceSnapshots = new TextureSourceSnapshotCache(materials);
        var cookedTextures = new Dictionary<string, (string Path, CookedTextureReport Report)>(StringComparer.Ordinal);
        var transportImageCache = new BoundedTransportImageCache();
        var opacityMicromapTextureArtifacts =
            new List<OpacityMicromapCookedTextureArtifact>();
        IReadOnlyList<ModelSubMesh> primitiveSubMeshes =
            GetPrimitiveTransportSubMeshes(model);
        var primitiveProfiles =
            new GiPrimitiveTransportProfile?[primitiveSubMeshes.Count];
        int textureSlotIndex = 0;
        for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
        {
            progress.ThrowIfCancellationRequested();
            ModelMaterial material = materials[materialIndex];
            string materialName = string.IsNullOrWhiteSpace(material.Name)
                ? $"material-{materialIndex + 1}"
                : material.Name;
            progress.Report(new AssetCookProgressEvent(AssetCookProgressEventKind.MaterialStarted)
            {
                ItemIndex = materialIndex + 1,
                ItemCount = materials.Length,
                ItemName = materialName
            });
            var materialTimer = Stopwatch.StartNew();
            var materialImages =
                new Dictionary<(int MaterialIndex, string PropertyName), TextureTransportImage>();
            var materialTransportImages =
                new Dictionary<string, TextureTransportImage?>(StringComparer.Ordinal);
            foreach (System.Reflection.PropertyInfo property in TextureSlotProperties)
            {
                ModelTextureSlot? slot = property.GetValue(material) as ModelTextureSlot;
                if (slot?.Source is null)
                    continue;
                ModelTextureSource source = slot.Source;
                progress.ThrowIfCancellationRequested();
                int currentTextureSlot = ++textureSlotIndex;
                string textureName = string.IsNullOrWhiteSpace(source.DebugName)
                    ? ResolveTextureSourceIdentity(source)
                    : source.DebugName;
                progress.Report(new AssetCookProgressEvent(AssetCookProgressEventKind.TextureStarted)
                {
                    ItemIndex = currentTextureSlot,
                    ItemCount = textureSlotCount,
                    ItemName = textureName
                });
                var textureTimer = Stopwatch.StartNew();
                string sourceSnapshotKey = sourceSnapshots.GetKey(source);
                TextureSourceSnapshot sourceSnapshot = sourceSnapshots.Capture(
                    source,
                    sourceSnapshotKey);
                byte[] sourceBytes = sourceSnapshot.Bytes;
                ulong sourceHash = sourceSnapshot.ContentHash;
                string identity = ResolveTextureSourceIdentity(source);
                TextureSemantic semantic = ClassifyTextureSemantic(property.Name, slot.ColorSpace);
                ModelTextureMipPolicy mipPolicy =
                    property.Name == nameof(ModelMaterial.BaseColorTexture)
                        ? ModelMaterialTexturePolicy.ResolveBaseColorMipPolicy(material)
                        : ModelTextureMipPolicy.Standard;
                bool preserveAlphaCoverage = mipPolicy.PreserveAlphaCoverage;
                string alphaCoverageKey = preserveAlphaCoverage
                    ? $"enabled:{mipPolicy.AlphaCutoff:R}"
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
                    $"{TextureTransportStatistics.DdsDecoderVersion}|" +
                    $"{TextureTransportStatistics.KtxStatisticsDecoderVersion}|{sourceHash:x16}";
                var textureOptions = defaultOptions with
                {
                    ColorSpace = slot.ColorSpace,
                    Semantic = semantic,
                    PreserveAlphaCoverage = preserveAlphaCoverage,
                    AlphaCutoff = mipPolicy.AlphaCutoff
                };
                TextureTransportImage? transportImage;
                AssetCookProgressOutcome textureOutcome;
                if (!cookedTextures.TryGetValue(key, out var cooked))
                {
                    // Generation-qualified model sidecars are per asset, but
                    // compatible cooked texture paths are shared. Serialize
                    // only this producer/reuse contract to prevent concurrent
                    // workers from racing a KTX2/.njtex publication pair.
                    lock (session.GetTextureKeyLock(key))
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
                            session,
                            out textureReport,
                            out transportImage))
                    {
                        session.InvalidateArtifactHash(ktxPath);
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
                            Ktx2ContentHash = session.GetOrRecordArtifactHash(ktxPath),
                            Semantic = semantic,
                            TransportStatistics = textureReport.TransportStatistics,
                            AlphaCoveragePreserved = textureReport.AlphaCoveragePreserved,
                            AlphaCoverageCutoff = textureReport.AlphaCoveragePreserved
                                ? textureReport.AlphaCutoff
                                : null
                        };
                        session.InvalidateArtifactHash(metaPath);
                        CookedPackage.WriteTextureMeta(metaPath, meta, toolVersion);
                        textureOutcome = AssetCookProgressOutcome.Cooked;
                    }
                    else
                    {
                        textureOutcome = AssetCookProgressOutcome.Reused;
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
                }
                else if (!materialTransportImages.TryGetValue(
                             key,
                             out transportImage) &&
                         !transportImageCache.TryGet(key, out transportImage))
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
                    textureOutcome = AssetCookProgressOutcome.Deduplicated;
                }
                else
                {
                    textureOutcome = AssetCookProgressOutcome.Deduplicated;
                }
                transportImageCache.Add(key, transportImage);
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
                progress.Report(new AssetCookProgressEvent(AssetCookProgressEventKind.TextureCompleted)
                {
                    Outcome = textureOutcome,
                    ItemIndex = currentTextureSlot,
                    ItemCount = textureSlotCount,
                    ItemName = textureName,
                    ItemElapsedMilliseconds = textureTimer.ElapsedMilliseconds
                });
                sourceSnapshots.Release(sourceSnapshotKey);
                progress.ThrowIfCancellationRequested();
            }
            foreach (System.Reflection.PropertyInfo pathProperty in WritableTexturePathProperties)
                pathProperty.SetValue(material, null);
            BuildPrimitiveTransportProfilesForMaterial(
                materialIndex,
                primitiveSubMeshes,
                materials,
                materialImages,
                primitiveProfiles);
            progress.Report(new AssetCookProgressEvent(AssetCookProgressEventKind.MaterialCompleted)
            {
                ItemIndex = materialIndex + 1,
                ItemCount = materials.Length,
                ItemName = materialName,
                ItemElapsedMilliseconds = materialTimer.ElapsedMilliseconds
            });
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
        foreach (System.Reflection.PropertyInfo property in
                 typeof(ModelMaterial).GetProperties().Where(property =>
                     property.CanRead && property.CanWrite))
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
        CookRunSession session,
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
            session.SetArtifactHash(ktxPath, authenticated.Ktx2ContentHash);
            session.SetArtifactHash(metaPath, authenticated.MetadataContentHash);
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
        if ((material.FeatureFlags & ModelMaterialFeatureBits.Foliage) != 0)
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
            foreach (System.Reflection.PropertyInfo property in ReadableTextureSlotProperties)
            {
                if (property.GetValue(material) is not ModelTextureSlot { Source.FilePath: { } filePath })
                    continue;
                string path = Path.GetFullPath(filePath);
                result[path.Replace('\\', '/')] = File.Exists(path) ? CookedHash.File(path) : 0;
            }
        return result;
    }

    private enum CookedOutputsCurrentState
    {
        Current,
        Missing,
        HashMismatch
    }

    private static AssetCookIncrementalReason DetermineIncrementalReason(
        ModelCookOptions options,
        CookedAssetDatabaseEntry? existing,
        ulong sourceHash,
        ulong settingsHash,
        ulong dependencyHash,
        string outputRoot)
    {
        if (options.Force)
            return AssetCookIncrementalReason.Forced;
        if (existing is null)
            return AssetCookIncrementalReason.DatabaseMiss;
        if (existing.SourceHash != sourceHash)
            return AssetCookIncrementalReason.SourceChanged;
        if (existing.ImportSettingsHash != settingsHash)
            return AssetCookIncrementalReason.SettingsChanged;
        if (existing.DependencyHash != dependencyHash)
            return AssetCookIncrementalReason.DependencyChanged;
        if (existing.ToolVersion != options.ToolVersion)
            return AssetCookIncrementalReason.ToolChanged;
        if (!string.Equals(existing.Status, "Succeeded", StringComparison.Ordinal))
            return AssetCookIncrementalReason.PreviousStatus;

        return GetOutputsCurrentState(outputRoot, existing.Outputs) switch
        {
            CookedOutputsCurrentState.Current => AssetCookIncrementalReason.Unchanged,
            CookedOutputsCurrentState.Missing => AssetCookIncrementalReason.OutputMissing,
            _ => AssetCookIncrementalReason.OutputHashMismatch
        };
    }

    private static CookedOutputsCurrentState GetOutputsCurrentState(
        string outputRoot,
        IReadOnlyDictionary<string, ulong> outputs)
    {
        if (outputs.Count == 0)
            return CookedOutputsCurrentState.Missing;

        foreach ((string relativePath, ulong expectedHash) in outputs)
        {
            string path = Path.Combine(outputRoot, relativePath);
            if (!File.Exists(path))
                return CookedOutputsCurrentState.Missing;
            string extension = Path.GetExtension(path);
            if (extension.Equals(
                    ".njmodel",
                    StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(
                    ".njmesh",
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var reader = new CookedAssetReader(path);
                }
                catch (CookedAssetFormatException)
                {
                    return CookedOutputsCurrentState.HashMismatch;
                }
            }
            if (CookedHash.File(path) != expectedHash)
                return CookedOutputsCurrentState.HashMismatch;
        }

        return CookedOutputsCurrentState.Current;
    }

    private static int CountOccupiedTextureSlots(ModelMesh model)
    {
        IReadOnlyList<ModelMaterial> materials = model.Materials.Count == 0
            ? [ModelMaterial.Default]
            : model.Materials;
        int count = 0;
        foreach (ModelMaterial material in materials)
        {
            foreach (System.Reflection.PropertyInfo property in TextureSlotProperties)
            {
                if (property.GetValue(material) is ModelTextureSlot { Source: not null })
                    count++;
            }
        }
        return count;
    }

    private static void ValidateFolderOutputCollisions(
        IReadOnlyList<string> sources,
        string modelDirectory)
    {
        foreach (IGrouping<string, string> collision in sources
                     .GroupBy(
                         source => SanitizeName(Path.GetFileNameWithoutExtension(source)),
                         StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Skip(1).Any()))
        {
            string[] paths = collision
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string modelPath = Path.Combine(modelDirectory, collision.Key + ".njmodel");
            throw new InvalidOperationException(
                $"Cook output collision: {string.Join(", ", paths.Select(path => $"'{path}'"))} " +
                $"all map to '{modelPath}'. Cook them to separate output roots or give the source files distinct base names.");
        }
    }

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

    private static string ResolveMeshletSidecarPath(string meshPath)
    {
        using var reader = new CookedAssetReader(
            meshPath,
            CookedAssetKind.Mesh);
        MeshletStreamingManifest manifest =
            CookedJson.Deserialize<MeshletStreamingManifest>(
                reader.GetRequiredSection(
                    CookedSectionIds.MeshletStreamingManifest).Span,
                meshPath,
                "meshlet streaming manifest");
        manifest.Validate(meshPath);
        using var pageFile = MeshletStreamingPageFile.Open(
            meshPath,
            manifest);
        return Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(meshPath)!,
            manifest.SidecarFileName));
    }

    private static bool PreviousEntryOwnsModelOutput(
        CookedAssetDatabaseEntry? previousEntry,
        string sourcePath,
        string outputRoot,
        string modelPath)
    {
        if (previousEntry is null ||
            !Path.GetFullPath(previousEntry.SourcePath).Equals(
                sourcePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relativeModelPath = NormalizeRelative(
            outputRoot,
            modelPath);
        return previousEntry.Outputs.Keys.Any(output =>
            output.Equals(
                relativeModelPath,
                StringComparison.OrdinalIgnoreCase));
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
