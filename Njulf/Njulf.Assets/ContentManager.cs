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

    public class ContentManager : IContentManager, IAsyncContentManager, IDisposable
    {
        private readonly Dictionary<string, object> _cache =
            new(StringComparer.Ordinal);
        private readonly Lazy<ModelImporter> _modelImporter;
        private readonly Lazy<ProcessedMeshAssetBuilder> _processedMeshAssetBuilder;
        private readonly IModelRenderUploadService? _modelRenderUploadService;
        private readonly IContentUploadDispatcher? _contentUploadDispatcher;
        private readonly Func<string, CookedModelPackageSnapshot>
            _modelSnapshotFactory;
        private readonly bool _useResolverSnapshots;
        private readonly string _rootDirectory;
        private readonly CookedContentResolver _cookedResolver;
        private readonly List<CookedContentDiagnosticEntry> _cookedDiagnosticEntries = new();
        private readonly object _stateLock = new();
        private readonly object _diagnosticsLock = new();
        private readonly object _uploadLock = new();
        private readonly Dictionary<string, ModelLoadGate> _modelLoadGates =
            new(StringComparer.Ordinal);
        private long _snapshotOwnershipSequence;
        private long _cacheGeneration;
        private bool _disposed;

        private sealed class ModelLoadGate
        {
            public SemaphoreSlim Semaphore { get; } = new(1, 1);
            public int LeaseCount { get; set; }
        }

        private sealed record CookedModelAsyncPreparation(
            string RequestedPath,
            CookedResolution Resolution,
            CookedModelPackageSnapshot Snapshot,
            CookedModelAsset? Package,
            string? CacheKey,
            long CacheGeneration,
            double LoadMilliseconds,
            Model? CachedModel,
            bool UseSourceFallback);

        private sealed class ContentByteBudget
        {
            private readonly object _gate = new();
            private readonly long _maximumBytes;
            private long _inflightBytes;

            public ContentByteBudget(long maximumBytes)
            {
                if (maximumBytes <= 0)
                    throw new ArgumentOutOfRangeException(nameof(maximumBytes));
                _maximumBytes = maximumBytes;
            }

            public async ValueTask<Lease> AcquireAsync(
                long estimatedBytes,
                CancellationToken cancellationToken)
            {
                long reservation = Math.Min(
                    Math.Max(1, estimatedBytes),
                    _maximumBytes);
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_inflightBytes + reservation <= _maximumBytes)
                        {
                            _inflightBytes += reservation;
                            return new Lease(this, reservation);
                        }
                    }

                    await Task.Delay(10, cancellationToken).ConfigureAwait(false);
                }
            }

            private void Release(long reservation)
            {
                lock (_gate)
                    _inflightBytes -= reservation;
            }

            public sealed class Lease : IDisposable
            {
                private ContentByteBudget? _owner;
                private readonly long _reservation;

                internal Lease(ContentByteBudget owner, long reservation)
                {
                    _owner = owner;
                    _reservation = reservation;
                }

                public void Dispose()
                {
                    ContentByteBudget? owner = Interlocked.Exchange(
                        ref _owner,
                        null);
                    owner?.Release(_reservation);
                }
            }
        }

        public ContentManager(
            string? rootDirectory = null,
            IModelRenderUploadService? modelRenderUploadService = null,
            IContentUploadDispatcher? contentUploadDispatcher = null)
            : this(
                rootDirectory,
                modelRenderUploadService,
                static path =>
                    CookedPackage.CaptureModelSnapshot(path),
                useResolverSnapshots: true,
                contentUploadDispatcher: contentUploadDispatcher)
        {
        }

        internal ContentManager(
            string? rootDirectory,
            IModelRenderUploadService? modelRenderUploadService,
            Func<string, CookedModelPackageSnapshot> modelSnapshotFactory)
            : this(
                rootDirectory,
                modelRenderUploadService,
                modelSnapshotFactory,
                useResolverSnapshots: false,
                contentUploadDispatcher: null)
        {
        }

        private ContentManager(
            string? rootDirectory,
            IModelRenderUploadService? modelRenderUploadService,
            Func<string, CookedModelPackageSnapshot> modelSnapshotFactory,
            bool useResolverSnapshots,
            IContentUploadDispatcher? contentUploadDispatcher)
        {
            ArgumentNullException.ThrowIfNull(modelSnapshotFactory);
            _rootDirectory = rootDirectory ?? AppContext.BaseDirectory!;
            _modelImporter = new Lazy<ModelImporter>(() => new ModelImporter(), LazyThreadSafetyMode.ExecutionAndPublication);
            _processedMeshAssetBuilder = new Lazy<ProcessedMeshAssetBuilder>(() => new ProcessedMeshAssetBuilder(), LazyThreadSafetyMode.ExecutionAndPublication);
            _modelRenderUploadService = modelRenderUploadService;
            _contentUploadDispatcher = contentUploadDispatcher;
            _modelSnapshotFactory = modelSnapshotFactory;
            _useResolverSnapshots = useResolverSnapshots;
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
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException(
                    "Path cannot be null or empty",
                    nameof(path));
            }

            options ??= ContentLoadOptions.Default;
            string fullPath = GetFullPath(path);
            lock (_stateLock)
            {
                ThrowIfDisposed();
            }

            if (typeof(T) == typeof(Model))
            {
                return LoadModelPipeline<T>(path, fullPath, options);
            }

            lock (_stateLock)
            {
                ThrowIfDisposed();
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

        /// <summary>
        /// Asynchronous entry point that preserves renderer ownership. When a
        /// host supplies an <see cref="IContentUploadDispatcher"/>, immutable
        /// resolver/read/decode work runs independently and only the final
        /// upload/publication callback is dispatched to that approved context.
        /// Without a dispatcher, this deliberately follows the existing
        /// synchronous path rather than uploading from an arbitrary pool thread.
        /// </summary>
        public async Task<T> LoadAsync<T>(
            string path,
            ContentLoadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException(
                    "Path cannot be null or empty",
                    nameof(path));
            }

            options ??= ContentLoadOptions.Default;
            string fullPath = GetFullPath(path);
            lock (_stateLock)
            {
                ThrowIfDisposed();
            }

            if (typeof(T) == typeof(Model) &&
                _contentUploadDispatcher is not null)
            {
                return await LoadModelPipelineAsync<T>(
                    path,
                    fullPath,
                    options,
                    _contentUploadDispatcher,
                    cancellationToken).ConfigureAwait(false);
            }

            // Without an owner-approved dispatcher there is no safe way to
            // put renderer mutation on a pool thread. Preserve synchronous
            // ownership semantics rather than merely wrapping Load in Task.Run.
            return Load<T>(path, options);
        }

        /// <summary>
        /// Preloads a prioritized group with bounded logical concurrency and
        /// byte admission. Failed or cancelled items are reported in the
        /// result while already-ready assets remain manager-owned.
        /// </summary>
        public async Task<ContentPreloadResult<T>> PreloadAsync<T>(
            IEnumerable<ContentPreloadRequest> requests,
            ContentPreloadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requests);
            options ??= new ContentPreloadOptions();
            if (options.MaxConcurrency <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxConcurrency must be positive.");
            if (options.MaxInflightBytes <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    "MaxInflightBytes must be positive.");

            // Materialize once: callers may provide a one-shot enumerable.
            // Results deliberately retain this original input order even while
            // admission below follows priority.
            ContentPreloadRequest[] input = requests
                .Select(request => request ?? throw new ArgumentException(
                    "Preload requests cannot contain null items.",
                    nameof(requests)))
                .ToArray();
            var ordered = input
                .Select((request, index) => new { Request = request, Index = index })
                .OrderByDescending(item => item.Request.Priority)
                .ThenBy(item => item.Index)
                .ToArray();
            var results = new ContentPreloadItemResult<T>[input.Length];

            using var concurrency = new SemaphoreSlim(options.MaxConcurrency);
            var byteBudget = new ContentByteBudget(options.MaxInflightBytes);
            Task[] work = ordered
                .Select(item => PreloadOneAsync(item.Request, item.Index))
                .ToArray();
            await Task.WhenAll(work).ConfigureAwait(false);
            return new ContentPreloadResult<T>(results);

            async Task PreloadOneAsync(
                ContentPreloadRequest request,
                int resultIndex)
            {
                long estimate = request.EstimatedBytes;
                if (estimate < 0)
                {
                    results[resultIndex] = new ContentPreloadItemResult<T>(
                        request,
                        default,
                        new ArgumentOutOfRangeException(
                            nameof(request.EstimatedBytes)),
                        Cancelled: false);
                    ReportContentProgress(
                        options.Progress,
                        request,
                        ContentLoadStage.Failed,
                        "EstimatedBytes cannot be negative.");
                    return;
                }

                // An unknown request takes a conservative admission unit; a
                // caller with package byte metadata can supply the exact value.
                if (estimate == 0)
                    estimate = Math.Min(options.MaxInflightBytes, 16L * 1024L * 1024L);
                ReportContentProgress(options.Progress, request, ContentLoadStage.Queued, null);
                ContentByteBudget.Lease? lease = null;
                bool entered = false;
                try
                {
                    lease = await byteBudget.AcquireAsync(
                        estimate,
                        cancellationToken).ConfigureAwait(false);
                    await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;
                    ReportContentProgress(options.Progress, request, ContentLoadStage.Started, null);
                    if (typeof(T) == typeof(Model) && _contentUploadDispatcher is not null)
                    {
                        ReportContentProgress(
                            options.Progress,
                            request,
                            ContentLoadStage.WaitingForUpload,
                            null);
                    }

                    T asset = await LoadAsync<T>(
                        request.Path,
                        options.LoadOptions,
                        cancellationToken).ConfigureAwait(false);
                    results[resultIndex] = new ContentPreloadItemResult<T>(
                        request,
                        asset,
                        Failure: null,
                        Cancelled: false);
                    ReportContentProgress(options.Progress, request, ContentLoadStage.Ready, null);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    results[resultIndex] = new ContentPreloadItemResult<T>(
                        request,
                        default,
                        Failure: null,
                        Cancelled: true);
                    ReportContentProgress(options.Progress, request, ContentLoadStage.Cancelled, null);
                }
                catch (Exception exception)
                {
                    results[resultIndex] = new ContentPreloadItemResult<T>(
                        request,
                        default,
                        exception,
                        Cancelled: false);
                    ReportContentProgress(
                        options.Progress,
                        request,
                        ContentLoadStage.Failed,
                        exception.Message);
                }
                finally
                {
                    if (entered)
                        concurrency.Release();
                    lease?.Dispose();
                }
            }
        }

        private T LoadModelPipeline<T>(
            string requestedPath,
            string sourcePath,
            ContentLoadOptions options)
        {
            bool strict = CookedRuntimePolicy.Strict;
            string gateKey = CreateModelLoadGateKey<T>(
                requestedPath,
                sourcePath,
                strict);
            ModelLoadGate gate = AcquireModelLoadGate(gateKey);
            gate.Semaphore.Wait();
            try
            {
                long cacheGeneration;
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    cacheGeneration = _cacheGeneration;
                }

                CookedResolution resolution =
                    _cookedResolver.ResolveModel(
                        requestedPath,
                        sourcePath,
                        strict,
                        _useResolverSnapshots);
                if (resolution.Status == CookedResolutionStatus.Found)
                {
                    return LoadResolvedCookedModel<T>(
                        requestedPath,
                        resolution,
                        strict,
                        cacheGeneration);
                }

                bool allowFallback = CookedRuntimePolicy.AllowSourceFallback;
                if (!allowFallback)
                {
                    throw new FileNotFoundException(
                        $"Cooked model package is required for '{requestedPath}', but {resolution.Reason}. " +
                        "Cook the asset with Njulf.AssetTool or set NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true for development fallback.",
                        resolution.PackagePath);
                }

                RecordCookedDiagnostic(
                    new CookedContentDiagnosticEntry(
                        requestedPath,
                        resolution.PackagePath,
                        false,
                        resolution.Reason,
                        0,
                        0,
                        0));

                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException(
                            "Source asset file was not found and no usable cooked package was resolved.",
                            sourcePath);
                    }

                    string cacheKey = CreateCacheKey<T>(sourcePath, options);
                    if (_cache.TryGetValue(cacheKey, out object? cached))
                        return (T)cached;

                    object result = LoadInternal<T>(sourcePath, options);
                    PublishOwnedAsset(cacheKey, result);
                    return (T)result;
                }
            }
            finally
            {
                gate.Semaphore.Release();
                ReleaseModelLoadGate(gateKey, gate);
            }
        }

        private async Task<T> LoadModelPipelineAsync<T>(
            string requestedPath,
            string sourcePath,
            ContentLoadOptions options,
            IContentUploadDispatcher uploadDispatcher,
            CancellationToken cancellationToken)
        {
            bool strict = CookedRuntimePolicy.Strict;
            string gateKey = CreateModelLoadGateKey<T>(
                requestedPath,
                sourcePath,
                strict);
            ModelLoadGate gate = AcquireModelLoadGate(gateKey);
            bool entered = false;
            try
            {
                await gate.Semaphore.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                entered = true;
                long cacheGeneration;
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    cacheGeneration = _cacheGeneration;
                }

                CookedModelAsyncPreparation preparation = await Task.Run(
                    () => PrepareCookedModelForAsyncLoad<T>(
                        requestedPath,
                        sourcePath,
                        strict,
                        cacheGeneration),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (preparation.CachedModel is not null)
                    return (T)(object)preparation.CachedModel;

                if (preparation.UseSourceFallback)
                {
                    return await uploadDispatcher.DispatchAsync(
                        () => LoadSourceFallbackModel<T>(sourcePath, options),
                        cancellationToken).ConfigureAwait(false);
                }

                return await uploadDispatcher.DispatchAsync(
                    () => UploadPreparedCookedModel<T>(preparation),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (entered)
                    gate.Semaphore.Release();
                ReleaseModelLoadGate(gateKey, gate);
            }
        }

        private CookedModelAsyncPreparation PrepareCookedModelForAsyncLoad<T>(
            string requestedPath,
            string sourcePath,
            bool strict,
            long cacheGeneration)
        {
            CookedResolution resolution = _cookedResolver.ResolveModel(
                requestedPath,
                sourcePath,
                strict,
                _useResolverSnapshots);
            if (resolution.Status != CookedResolutionStatus.Found)
            {
                if (!CookedRuntimePolicy.AllowSourceFallback)
                {
                    throw new FileNotFoundException(
                        $"Cooked model package is required for '{requestedPath}', but {resolution.Reason}. " +
                        "Cook the asset with Njulf.AssetTool or set NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true for development fallback.",
                        resolution.PackagePath);
                }

                RecordCookedDiagnostic(new CookedContentDiagnosticEntry(
                    requestedPath,
                    resolution.PackagePath,
                    false,
                    resolution.Reason,
                    0,
                    0,
                    0));
                return new CookedModelAsyncPreparation(
                    requestedPath,
                    resolution,
                    Snapshot: null!,
                    Package: null,
                    CacheKey: null,
                    cacheGeneration,
                    LoadMilliseconds: 0,
                    CachedModel: null,
                    UseSourceFallback: true);
            }

            if (_modelRenderUploadService is null)
            {
                throw new InvalidOperationException(
                    "Loading a cooked Model requires an IModelRenderUploadService.");
            }

            CookedAssetReaderFlags readerFlags = CookedRuntimePolicy.ReaderFlags;
            if (!strict)
                readerFlags &= ~CookedAssetReaderFlags.StrictSourceHash;
            ulong? expectedSourceHash = resolution.ExpectedSourceHash;
            string cookedPath = resolution.PackagePath!;
            var stopwatch = Stopwatch.StartNew();
            CookedModelPackageSnapshot snapshot = resolution.ModelSnapshot ??
                _modelSnapshotFactory(cookedPath) ??
                throw new InvalidOperationException(
                    "The cooked model snapshot factory returned null.");
            string cookedKey = CreateCookedCacheKey<T>(
                snapshot,
                readerFlags,
                expectedSourceHash);
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (_cache.TryGetValue(cookedKey, out object? cached))
                {
                    return new CookedModelAsyncPreparation(
                        requestedPath,
                        resolution,
                        snapshot,
                        Package: null,
                        cookedKey,
                        cacheGeneration,
                        stopwatch.Elapsed.TotalMilliseconds,
                        CachedModel: (Model)cached,
                        UseSourceFallback: false);
                }
            }

            CookedModelAsset package = CookedPackage.LoadModel(
                snapshot,
                readerFlags,
                expectedSourceHash);
            return new CookedModelAsyncPreparation(
                requestedPath,
                resolution,
                snapshot,
                package,
                cookedKey,
                cacheGeneration,
                stopwatch.Elapsed.TotalMilliseconds,
                CachedModel: null,
                UseSourceFallback: false);
        }

        private T UploadPreparedCookedModel<T>(
            CookedModelAsyncPreparation preparation)
        {
            if (_modelRenderUploadService is null ||
                preparation.Package is null ||
                string.IsNullOrWhiteSpace(preparation.CacheKey))
            {
                throw new InvalidOperationException(
                    "The cooked model upload preparation is incomplete.");
            }

            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (_cache.TryGetValue(preparation.CacheKey, out object? cached))
                    return (T)cached;
            }

            double uploadMs;
            Model cookedModel;
            lock (_uploadLock)
            {
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    if (_cache.TryGetValue(preparation.CacheKey, out object? cached))
                        return (T)cached;
                }

                var stopwatch = Stopwatch.StartNew();
                cookedModel =
                    _modelRenderUploadService.UploadCookedModel(preparation.Package) ??
                    throw new InvalidOperationException(
                        "The model upload service returned a null cooked model.");
                uploadMs = stopwatch.Elapsed.TotalMilliseconds;
                bool publicationInvoked = false;
                try
                {
                    lock (_stateLock)
                    {
                        ThrowIfDisposed();
                        if (_cacheGeneration != preparation.CacheGeneration)
                        {
                            throw new OperationCanceledException(
                                "The content cache was cleared while this cooked model was loading.");
                        }

                        publicationInvoked = true;
                        PublishOwnedAsset(preparation.CacheKey, cookedModel);
                    }
                }
                catch (Exception publicationFailure)
                {
                    if (!publicationInvoked)
                        DisposeUnpublishedAsset(cookedModel, publicationFailure);
                    throw;
                }
            }

            RecordCookedDiagnostic(new CookedContentDiagnosticEntry(
                preparation.RequestedPath,
                preparation.Snapshot.PackagePath,
                true,
                preparation.Resolution.Reason,
                preparation.Package.BytesRead,
                preparation.LoadMilliseconds,
                uploadMs));
            return (T)(object)cookedModel;
        }

        private T LoadSourceFallbackModel<T>(
            string sourcePath,
            ContentLoadOptions options)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        "Source asset file was not found and no usable cooked package was resolved.",
                        sourcePath);
                }

                string cacheKey = CreateCacheKey<T>(sourcePath, options);
                if (_cache.TryGetValue(cacheKey, out object? cached))
                    return (T)cached;

                object result = LoadInternal<T>(sourcePath, options);
                PublishOwnedAsset(cacheKey, result);
                return (T)result;
            }
        }

        private T LoadResolvedCookedModel<T>(
            string requestedPath,
            CookedResolution resolution,
            bool strict,
            long cacheGeneration)
        {
            if (_modelRenderUploadService == null)
            {
                throw new InvalidOperationException(
                    "Loading a cooked Model requires an IModelRenderUploadService.");
            }

            string cookedPath = resolution.PackagePath!;
            CookedAssetReaderFlags readerFlags =
                CookedRuntimePolicy.ReaderFlags;
            if (!strict)
                readerFlags &= ~CookedAssetReaderFlags.StrictSourceHash;
            ulong? expectedSourceHash = resolution.ExpectedSourceHash;

            var stopwatch = Stopwatch.StartNew();
            CookedModelPackageSnapshot snapshot =
                resolution.ModelSnapshot ??
                _modelSnapshotFactory(cookedPath) ??
                throw new InvalidOperationException(
                    "The cooked model snapshot factory returned null.");
            string cookedKey = CreateCookedCacheKey<T>(
                snapshot,
                readerFlags,
                expectedSourceHash);
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (_cache.TryGetValue(cookedKey, out object? cookedCached))
                    return (T)cookedCached;
            }

            CookedModelAsset package = CookedPackage.LoadModel(
                snapshot,
                readerFlags,
                expectedSourceHash);
            double loadMs = stopwatch.Elapsed.TotalMilliseconds;

            Model cookedModel;
            double uploadMs;
            lock (_uploadLock)
            {
                lock (_stateLock)
                {
                    ThrowIfDisposed();
                    if (_cache.TryGetValue(cookedKey, out object? cached))
                        return (T)cached;
                }

                stopwatch.Restart();
                cookedModel =
                    _modelRenderUploadService.UploadCookedModel(package) ??
                    throw new InvalidOperationException(
                        "The model upload service returned a null cooked model.");
                uploadMs = stopwatch.Elapsed.TotalMilliseconds;
                bool publicationInvoked = false;
                try
                {
                    lock (_stateLock)
                    {
                        ThrowIfDisposed();
                        if (_cacheGeneration != cacheGeneration)
                        {
                            throw new OperationCanceledException(
                                "The content cache was cleared while this cooked model was loading.");
                        }

                        publicationInvoked = true;
                        PublishOwnedAsset(cookedKey, cookedModel);
                    }
                }
                catch (Exception publicationFailure)
                {
                    if (!publicationInvoked)
                        DisposeUnpublishedAsset(cookedModel, publicationFailure);
                    throw;
                }
            }
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

        private static string CreateModelLoadGateKey<T>(
            string requestedPath,
            string sourcePath,
            bool strict)
        {
            bool requestedCooked = Path.GetExtension(requestedPath).Equals(
                ".njmodel",
                StringComparison.OrdinalIgnoreCase);
            return string.Join(
                '|',
                typeof(T).FullName,
                Path.GetFullPath(sourcePath),
                $"requestedCooked={requestedCooked}",
                $"strict={strict}",
                $"readerFlags={(uint)CookedRuntimePolicy.ReaderFlags}");
        }

        private ModelLoadGate AcquireModelLoadGate(string gateKey)
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                if (!_modelLoadGates.TryGetValue(gateKey, out ModelLoadGate? gate))
                {
                    gate = new ModelLoadGate();
                    _modelLoadGates.Add(gateKey, gate);
                }
                gate.LeaseCount++;
                return gate;
            }
        }

        private void ReleaseModelLoadGate(string gateKey, ModelLoadGate gate)
        {
            lock (_stateLock)
            {
                gate.LeaseCount--;
                if (gate.LeaseCount == 0 &&
                    _modelLoadGates.TryGetValue(gateKey, out ModelLoadGate? current) &&
                    ReferenceEquals(current, gate))
                {
                    _modelLoadGates.Remove(gateKey);
                    gate.Semaphore.Dispose();
                }
            }
        }

        private static void DisposeUnpublishedAsset(
            object asset,
            Exception publicationFailure)
        {
            if (asset is not IDisposable disposable)
                return;

            try
            {
                disposable.Dispose();
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Content publication was invalidated and the unpublished asset could not be disposed.",
                    publicationFailure,
                    rollbackFailure);
            }
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
                _cacheGeneration++;
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

        private static void ReportContentProgress(
            IContentLoadProgressSink? sink,
            ContentPreloadRequest request,
            ContentLoadStage stage,
            string? message)
        {
            if (sink is null)
                return;

            try
            {
                sink.Report(new ContentLoadProgressEvent(
                    request.Path,
                    request.Priority,
                    stage,
                    request.EstimatedBytes,
                    message));
            }
            catch
            {
                // A diagnostic observer must not change content ownership or
                // turn a successfully loaded asset into a failed preload.
            }
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
                _cacheGeneration++;
                if (_modelImporter.IsValueCreated)
                    _modelImporter.Value.Dispose();
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }
    }
}
