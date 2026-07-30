using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using Njulf.Assets.Cooked;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets
{
    /// <summary>
    /// Result of decoding and uploading one immutable cooked model snapshot.
    /// Both CPU semantic validation and the runtime model are derived from the
    /// same package bytes identified by <see cref="Snapshot"/>.
    /// </summary>
    public sealed record CookedModelSnapshotLoadResult(
        CookedModelPackageSnapshot Snapshot,
        CookedModelAsset CookedAsset,
        Model RuntimeModel);

    public class ContentManager : IContentManager, IDisposable
    {
        private readonly Dictionary<string, object> _cache =
            new(StringComparer.Ordinal);
        private readonly Lazy<ModelImporter> _modelImporter;
        private readonly Lazy<ProcessedMeshAssetBuilder> _processedMeshAssetBuilder;
        private readonly IModelRenderUploadService? _modelRenderUploadService;
        private readonly Func<string, CookedModelPackageSnapshot>
            _modelSnapshotFactory;
        private readonly string _rootDirectory;
        private readonly CookedContentResolver _cookedResolver;
        private readonly List<CookedContentDiagnosticEntry> _cookedDiagnosticEntries = new();
        private readonly object _stateLock = new();
        private readonly object _diagnosticsLock = new();
        private long _snapshotOwnershipSequence;
        private bool _disposed;

        public ContentManager(
            string? rootDirectory = null,
            IModelRenderUploadService? modelRenderUploadService = null)
            : this(
                rootDirectory,
                modelRenderUploadService,
                static path =>
                    CookedPackage.CaptureModelSnapshot(path))
        {
        }

        internal ContentManager(
            string? rootDirectory,
            IModelRenderUploadService? modelRenderUploadService,
            Func<string, CookedModelPackageSnapshot> modelSnapshotFactory)
        {
            ArgumentNullException.ThrowIfNull(modelSnapshotFactory);
            _rootDirectory = rootDirectory ?? AppContext.BaseDirectory!;
            _modelImporter = new Lazy<ModelImporter>(() => new ModelImporter(), LazyThreadSafetyMode.ExecutionAndPublication);
            _processedMeshAssetBuilder = new Lazy<ProcessedMeshAssetBuilder>(() => new ProcessedMeshAssetBuilder(), LazyThreadSafetyMode.ExecutionAndPublication);
            _modelRenderUploadService = modelRenderUploadService;
            _modelSnapshotFactory = modelSnapshotFactory;
            _cookedResolver = new CookedContentResolver(_rootDirectory);
        }

        public CookedContentDiagnostics CookedDiagnostics
        {
            get
            {
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    lock (_diagnosticsLock)
                    {
                        CookedContentDiagnosticEntry[] entries =
                            _cookedDiagnosticEntries.ToArray();
                        return new CookedContentDiagnostics(
                            entries.Count(entry => entry.UsedCooked),
                            entries.Where(entry => entry.UsedCooked)
                                .Sum(entry => entry.BytesRead),
                            entries.Where(entry => entry.UsedCooked)
                                .Sum(entry => entry.LoadMilliseconds),
                            entries.Where(entry => entry.UsedCooked)
                                .Sum(entry => entry.UploadMilliseconds),
                            entries.Count(entry => !entry.UsedCooked),
                            entries.Count(entry =>
                                entry.Reason.Contains(
                                    "hash",
                                    StringComparison.OrdinalIgnoreCase) ||
                                entry.Reason.Contains(
                                    "version",
                                    StringComparison.OrdinalIgnoreCase)),
                            entries);
                    }
                }
            }
        }

        public T Load<T>(string path)
        {
            return Load<T>(path, ContentLoadOptions.Default);
        }

        public T Load<T>(string path, ContentLoadOptions? options)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (string.IsNullOrEmpty(path))
                {
                    throw new ArgumentException(
                        "Path cannot be null or empty",
                        nameof(path));
                }

                options ??= ContentLoadOptions.Default;
                string fullPath = GetFullPath(path);

                if (typeof(T) == typeof(Model))
                {
                    bool strict = CookedRuntimePolicy.Strict;
                    CookedResolution resolution =
                        _cookedResolver.ResolveModel(path, fullPath, strict);
                    if (resolution.Status == CookedResolutionStatus.Found)
                    {
                        return LoadResolvedCookedModel<T>(
                            path,
                            fullPath,
                            resolution,
                            strict);
                    }

                    bool allowFallback =
                        CookedRuntimePolicy.AllowSourceFallback;
                    if (!allowFallback)
                    {
                        throw new FileNotFoundException(
                            $"Cooked model package is required for '{path}', but {resolution.Reason}. " +
                            "Cook the asset with Njulf.AssetTool or set NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true for development fallback.",
                            resolution.PackagePath);
                    }

                    RecordCookedDiagnostic(
                        new CookedContentDiagnosticEntry(
                            path,
                            resolution.PackagePath,
                            false,
                            resolution.Reason,
                            0,
                            0,
                            0));
                }

                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        "Source asset file was not found and no usable cooked package was resolved.",
                        fullPath);
                }

                string cacheKey = CreateCacheKey<T>(fullPath, options);

                if (_cache.TryGetValue(cacheKey, out object? cached))
                    return (T)cached;

                object result = LoadInternal<T>(fullPath, options);
                PublishOwnedAsset(cacheKey, result);

                return (T)result;
            }
        }

        private T LoadResolvedCookedModel<T>(
            string requestedPath,
            string sourcePath,
            CookedResolution resolution,
            bool strict)
        {
            if (_modelRenderUploadService == null)
            {
                throw new InvalidOperationException(
                    "Loading a cooked Model requires an IModelRenderUploadService.");
            }

            string cookedPath = resolution.PackagePath!;
            bool packageRequestedDirectly = Path.GetExtension(requestedPath)
                .Equals(".njmodel", StringComparison.OrdinalIgnoreCase);
            CookedAssetReaderFlags readerFlags =
                CookedRuntimePolicy.ReaderFlags;
            if (!strict)
                readerFlags &= ~CookedAssetReaderFlags.StrictSourceHash;
            ulong? expectedSourceHash =
                !packageRequestedDirectly && File.Exists(sourcePath)
                    ? CookedHash.File(sourcePath)
                    : null;

            var stopwatch = Stopwatch.StartNew();
            CookedModelPackageSnapshot snapshot =
                _modelSnapshotFactory(cookedPath) ??
                throw new InvalidOperationException(
                    "The cooked model snapshot factory returned null.");
            string cookedKey = CreateCookedCacheKey<T>(
                snapshot,
                readerFlags,
                expectedSourceHash);
            if (_cache.TryGetValue(cookedKey, out object? cookedCached))
                return (T)cookedCached;

            CookedModelAsset package = CookedPackage.LoadModel(
                snapshot,
                readerFlags,
                expectedSourceHash);
            double loadMs = stopwatch.Elapsed.TotalMilliseconds;

            stopwatch.Restart();
            Model cookedModel =
                _modelRenderUploadService.UploadCookedModel(package) ??
                throw new InvalidOperationException(
                    "The model upload service returned a null cooked model.");
            double uploadMs = stopwatch.Elapsed.TotalMilliseconds;
            PublishOwnedAsset(cookedKey, cookedModel);
            RecordCookedDiagnostic(
                new CookedContentDiagnosticEntry(
                    requestedPath,
                    snapshot.PackagePath,
                    true,
                    resolution.Reason,
                    package.BytesRead,
                    loadMs,
                    uploadMs));
            return (T)(object)cookedModel;
        }

        /// <summary>
        /// Decodes, validates, and uploads one caller-captured model package
        /// snapshot without reopening its package path. The validator runs
        /// after all referenced cooked payloads have been decoded and before
        /// any renderer resources are created.
        /// </summary>
        public CookedModelSnapshotLoadResult LoadCookedModelSnapshot(
            CookedModelPackageSnapshot snapshot,
            Action<CookedModelAsset>? validator = null)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                ArgumentNullException.ThrowIfNull(snapshot);
                if (_modelRenderUploadService == null)
                {
                    throw new InvalidOperationException(
                        "Loading a cooked Model snapshot requires an IModelRenderUploadService.");
                }

                var stopwatch = Stopwatch.StartNew();
                CookedAssetReaderFlags readerFlags =
                    CookedRuntimePolicy.ReaderFlags;
                if (!CookedRuntimePolicy.Strict)
                {
                    readerFlags &=
                        ~CookedAssetReaderFlags.StrictSourceHash;
                }

                CookedModelAsset package = CookedPackage.LoadModel(
                    snapshot,
                    readerFlags);
                validator?.Invoke(package);
                double loadMs = stopwatch.Elapsed.TotalMilliseconds;

                stopwatch.Restart();
                Model runtimeModel =
                    _modelRenderUploadService.UploadCookedModel(package) ??
                    throw new InvalidOperationException(
                        "The model upload service returned a null cooked model.");
                double uploadMs = stopwatch.Elapsed.TotalMilliseconds;
                // This evidence path deliberately never reuses a runtime
                // model: dependency packages are decoded into this exact
                // CookedModelAsset immediately before upload. Register a
                // unique ownership key only so Clear/Dispose releases it.
                long ownershipId = ++_snapshotOwnershipSequence;
                PublishOwnedAsset(
                    $"{typeof(Model).FullName}|snapshot-ownership=" +
                    ownershipId.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    runtimeModel);
                RecordCookedDiagnostic(
                    new CookedContentDiagnosticEntry(
                        snapshot.PackagePath,
                        snapshot.PackagePath,
                        true,
                        "cooked package loaded from one immutable snapshot",
                        package.BytesRead,
                        loadMs,
                        uploadMs));
                return new CookedModelSnapshotLoadResult(
                    snapshot,
                    package,
                    runtimeModel);
            }
        }

        private object LoadInternal<T>(
            string fullPath,
            ContentLoadOptions options)
        {
            if (typeof(T) == typeof(ModelMesh) ||
                typeof(T) == typeof(MeshletMesh) ||
                typeof(T) == typeof(Model) ||
                typeof(T) == typeof(ProcessedMeshAsset))
            {
                var modelMesh = _modelImporter.Value.Import(fullPath, options.ImporterOptions);

                if (typeof(T) == typeof(ProcessedMeshAsset))
                    return (T)(object)_processedMeshAssetBuilder.Value.Build(modelMesh, fullPath);

                if (typeof(T) == typeof(Model))
                {
                    if (_modelRenderUploadService == null)
                    {
                        throw new InvalidOperationException(
                            "Loading Njulf.Core.Scene.Model requires an IModelRenderUploadService. " +
                            "Register the rendering services before building the service provider, or load ModelMesh for CPU-only asset data.");
                    }

                    return (T)(object)(
                        _modelRenderUploadService.UploadModel(modelMesh) ??
                        throw new InvalidOperationException(
                            "The model upload service returned a null model."));
                }

                if (typeof(T) == typeof(MeshletMesh))
                {
                    var meshletBuilder = new MeshletBuilder();
                    var meshletMesh = meshletBuilder.BuildMeshlets(
                        modelMesh.Vertices,
                        modelMesh.Indices,
                        modelMesh.Normals,
                        modelMesh.Tangents,
                        modelMesh.Bitangents,
                        modelMesh.TexCoords,
                        modelMesh.Name);
                    meshletMesh.BoundingBox = modelMesh.BoundingBox;
                    meshletMesh.BoundingSphere = modelMesh.BoundingSphere;
                    return (T)(object)meshletMesh;
                }
                return (T)(object)modelMesh;
            }

            // Add more type handlers as needed
            throw new NotSupportedException($"Type {typeof(T).Name} is not supported for loading");
        }

        private static string CreateCacheKey<T>(string fullPath, ContentLoadOptions options)
        {
            ImporterOptions importer = options.ImporterOptions ?? ImporterOptions.Default;
            ModelImportBackend backend = ModelImporter.ResolveBackend(fullPath, importer);
            return string.Join(
                '|',
                typeof(T).FullName,
                Path.GetFullPath(fullPath),
                $"backend={backend}",
                $"policy={options.ImportPolicy}",
                $"highTextureBytes={options.HighTextureMemoryBytes}",
                $"flipUvs={importer.FlipUVs}",
                $"generateNormals={importer.GenerateNormals}",
                $"generateTangents={importer.GenerateTangents}",
                $"triangulate={importer.Triangulate}",
                $"joinVertices={importer.JoinIdenticalVertices}",
                $"sortPrimitives={importer.SortByPrimitiveType}",
                $"bounds={importer.CalculateBoundingBoxes}",
                $"scale={importer.GlobalScale:R}",
                $"flipWinding={importer.FlipWindingOrder}",
                $"format={importer.PreferredFormat}");
        }

        private static string CreateCookedCacheKey<T>(
            CookedModelPackageSnapshot snapshot,
            CookedAssetReaderFlags readerFlags,
            ulong? expectedSourceHash)
        {
            string sourceIdentity = expectedSourceHash.HasValue
                ? expectedSourceHash.Value.ToString(
                    "x16",
                    System.Globalization.CultureInfo.InvariantCulture)
                : "none";
            return string.Join(
                '|',
                typeof(T).FullName,
                snapshot.PackagePath,
                $"sha256={snapshot.Sha256}",
                $"readerFlags={(uint)readerFlags}",
                $"expectedSourceHash={sourceIdentity}");
        }

        private void PublishOwnedAsset(string cacheKey, object asset)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
            ArgumentNullException.ThrowIfNull(asset);

            try
            {
                _cache.Add(cacheKey, asset);
            }
            catch (Exception publicationFailure)
            {
                if (asset is not IDisposable disposable)
                    throw;

                try
                {
                    disposable.Dispose();
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Content publication failed and the unpublished asset could not be disposed.",
                        publicationFailure,
                        rollbackFailure);
                }

                throw;
            }
        }

        public void Unload<T>(T asset)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (asset is null)
                    return;

                // Keep every authoritative cache entry until disposal
                // succeeds. A retryable release failure must not orphan the
                // manager's only ownership record.
                if (asset is IDisposable disposable)
                    disposable.Dispose();

                RemoveCacheEntries(asset);
            }
        }

        public void Clear()
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                ClearOwnedAssets();
            }
        }

        private void ClearOwnedAssets()
        {
            var ownershipGroups =
                new Dictionary<object, List<string>>(
                    ReferenceEqualityComparer.Instance);
            foreach ((string key, object asset) in _cache)
            {
                if (!ownershipGroups.TryGetValue(
                        asset,
                        out List<string>? keys))
                {
                    keys = new List<string>();
                    ownershipGroups.Add(asset, keys);
                }

                keys.Add(key);
            }

            KeyValuePair<object, List<string>>[] groups =
                ownershipGroups.ToArray();
            List<Exception>? failures = null;
            for (int index = groups.Length - 1; index >= 0; index--)
            {
                object asset = groups[index].Key;
                try
                {
                    if (asset is IDisposable disposable)
                        disposable.Dispose();
                }
                catch (Exception disposeFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(disposeFailure);
                    continue;
                }

                foreach (string cacheKey in groups[index].Value)
                {
                    if (_cache.TryGetValue(
                            cacheKey,
                            out object? current) &&
                        ReferenceEquals(current, asset))
                    {
                        _cache.Remove(cacheKey);
                    }
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more content assets could not be disposed. " +
                    "Their cache ownership entries were retained for retry.",
                    failures);
            }
        }

        private void RemoveCacheEntries(object asset)
        {
            List<string>? keys = null;
            foreach ((string key, object cachedAsset) in _cache)
            {
                if (!ReferenceEquals(cachedAsset, asset))
                    continue;

                (keys ??= new List<string>()).Add(key);
            }

            if (keys == null)
                return;
            foreach (string key in keys)
                _cache.Remove(key);
        }

        private string GetFullPath(string path)
        {
            if (Path.IsPathRooted(path))
                return path;

            return Path.Combine(_rootDirectory, path);
        }

        private void RecordCookedDiagnostic(CookedContentDiagnosticEntry entry)
        {
            lock (_diagnosticsLock)
                _cookedDiagnosticEntries.Add(entry);
            System.Diagnostics.Debug.WriteLine(
                entry.UsedCooked
                    ? $"Cooked asset loaded: {entry.RequestedPath} -> {entry.PackagePath}, read={entry.BytesRead} bytes, load={entry.LoadMilliseconds:F2}ms, upload={entry.UploadMilliseconds:F2}ms"
                    : $"Cooked asset source fallback: {entry.RequestedPath}: {entry.Reason}");
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    GC.SuppressFinalize(this);
                    return;
                }

                ClearOwnedAssets();
                if (_modelImporter.IsValueCreated)
                    _modelImporter.Value.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
