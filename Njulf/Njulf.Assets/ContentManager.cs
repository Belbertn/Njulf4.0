using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Assets
{
    public class ContentManager : IContentManager, IDisposable
    {
        private readonly Dictionary<string, object> _cache = new();
        private readonly ModelImporter _modelImporter;
        private readonly IModelRenderUploadService? _modelRenderUploadService;
        private readonly string _rootDirectory;
        private bool _disposed;

        public ContentManager(
            string? rootDirectory = null,
            IModelRenderUploadService? modelRenderUploadService = null)
        {
            _rootDirectory = rootDirectory ?? AppContext.BaseDirectory!;
            _modelImporter = new ModelImporter();
            _modelRenderUploadService = modelRenderUploadService;
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
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("File not found", fullPath);

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

            if (typeof(T) == typeof(ModelMesh) || typeof(T) == typeof(MeshletMesh) || typeof(T) == typeof(Model))
            {
                var modelMesh = _modelImporter.Import(fullPath, options.ImporterOptions);

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

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _modelImporter.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }
}
