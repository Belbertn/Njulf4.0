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
    public class ContentManager : IContentManager, IDisposable
    {
        private readonly Dictionary<string, object> _cache = new();
        private readonly Lazy<ModelImporter> _modelImporter;
        private readonly Lazy<ProcessedMeshAssetBuilder> _processedMeshAssetBuilder;
        private readonly IModelRenderUploadService? _modelRenderUploadService;
        private readonly string _rootDirectory;
        private readonly CookedContentResolver _cookedResolver;
        private readonly List<CookedContentDiagnosticEntry> _cookedDiagnosticEntries = new();
        private readonly object _diagnosticsLock = new();
        private bool _disposed;

        public ContentManager(
            string? rootDirectory = null,
            IModelRenderUploadService? modelRenderUploadService = null)
        {
            _rootDirectory = rootDirectory ?? AppContext.BaseDirectory!;
            _modelImporter = new Lazy<ModelImporter>(() => new ModelImporter(), LazyThreadSafetyMode.ExecutionAndPublication);
            _processedMeshAssetBuilder = new Lazy<ProcessedMeshAssetBuilder>(() => new ProcessedMeshAssetBuilder(), LazyThreadSafetyMode.ExecutionAndPublication);
            _modelRenderUploadService = modelRenderUploadService;
            _cookedResolver = new CookedContentResolver(_rootDirectory);
        }

        public CookedContentDiagnostics CookedDiagnostics
        {
            get
            {
                lock (_diagnosticsLock)
                {
                    CookedContentDiagnosticEntry[] entries = _cookedDiagnosticEntries.ToArray();
                    return new CookedContentDiagnostics(
                        entries.Count(entry => entry.UsedCooked),
                        entries.Where(entry => entry.UsedCooked).Sum(entry => entry.BytesRead),
                        entries.Where(entry => entry.UsedCooked).Sum(entry => entry.LoadMilliseconds),
                        entries.Where(entry => entry.UsedCooked).Sum(entry => entry.UploadMilliseconds),
                        entries.Count(entry => !entry.UsedCooked),
                        entries.Count(entry => entry.Reason.Contains("hash", StringComparison.OrdinalIgnoreCase) || entry.Reason.Contains("version", StringComparison.OrdinalIgnoreCase)),
                        entries);
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
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            options ??= ContentLoadOptions.Default;
            string fullPath = GetFullPath(path);

            if (typeof(T) == typeof(Model))
            {
                bool strict = CookedRuntimePolicy.Strict;
                CookedResolution resolution = _cookedResolver.ResolveModel(path, fullPath, strict);
                if (resolution.Status == CookedResolutionStatus.Found)
                {
                    string cookedPath = resolution.PackagePath!;
                    ulong packageHash = CookedHash.File(cookedPath);
                    string cookedKey = $"{typeof(T).FullName}|{cookedPath}|hash={packageHash:x16}|version={resolution.Header!.Value.FormatMajor}.{resolution.Header.Value.FormatMinor}";
                    if (_cache.TryGetValue(cookedKey, out object? cookedCached))
                        return (T)cookedCached;
                    if (_modelRenderUploadService == null)
                        throw new InvalidOperationException("Loading a cooked Model requires an IModelRenderUploadService.");
                    var stopwatch = Stopwatch.StartNew();
                    CookedAssetReaderFlags readerFlags = CookedRuntimePolicy.ReaderFlags;
                    if (!strict)
                        readerFlags &= ~CookedAssetReaderFlags.StrictSourceHash;
                    CookedModelAsset package = CookedPackage.LoadModel(cookedPath, readerFlags, File.Exists(fullPath) ? CookedHash.File(fullPath) : null);
                    double loadMs = stopwatch.Elapsed.TotalMilliseconds;
                    stopwatch.Restart();
                    Model cookedModel = _modelRenderUploadService.UploadCookedModel(package);
                    double uploadMs = stopwatch.Elapsed.TotalMilliseconds;
                    _cache[cookedKey] = cookedModel;
                    RecordCookedDiagnostic(new CookedContentDiagnosticEntry(path, cookedPath, true, resolution.Reason, package.BytesRead, loadMs, uploadMs));
                    return (T)(object)cookedModel;
                }

                bool allowFallback = CookedRuntimePolicy.AllowSourceFallback;
                if (!allowFallback)
                {
                    throw new FileNotFoundException(
                        $"Cooked model package is required for '{path}', but {resolution.Reason}. " +
                        "Cook the asset with Njulf.AssetTool or set NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD=true for development fallback.",
                        resolution.PackagePath);
                }
                RecordCookedDiagnostic(new CookedContentDiagnosticEntry(path, resolution.PackagePath, false, resolution.Reason, 0, 0, 0));
            }

            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Source asset file was not found and no usable cooked package was resolved.", fullPath);

            string cacheKey = CreateCacheKey<T>(fullPath, options);

            if (_cache.TryGetValue(cacheKey, out var cached))
                return (T)cached;

            object result = LoadInternal<T>(fullPath, path, options);
            _cache[cacheKey] = result;

            return (T)result;
        }

        private object LoadInternal<T>(string fullPath, string path, ContentLoadOptions options)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();

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

                    return (T)(object)_modelRenderUploadService.UploadModel(modelMesh);
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

        public void Unload<T>(T asset)
        {
            if (asset == null) return;

            string? cacheKey = null;
            foreach (var kvp in _cache)
                if (ReferenceEquals(kvp.Value, asset))
                    cacheKey = kvp.Key;

            if (cacheKey != null)
                _cache.Remove(cacheKey);

            if (asset is IDisposable disposable)
                disposable.Dispose();
        }

        public void Clear()
        {
            foreach (var obj in _cache.Values)
            {
                if (obj is IDisposable disposable)
                    disposable.Dispose();
            }
            _cache.Clear();
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

        private static bool IsEnvironmentEnabled(string name, bool defaultValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                if (_modelImporter.IsValueCreated)
                    _modelImporter.Value.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
