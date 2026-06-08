using System;
using System.Collections.Generic;
using System.IO;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Assets
{
    public class ContentManager : IContentManager, IDisposable
    {
        private readonly Dictionary<string, object> _cache = new();
        private readonly Dictionary<Type, object> _typeCache = new();
        private readonly ModelImporter _modelImporter;
        private readonly string _rootDirectory;
        private bool _disposed;

        public ContentManager(string? rootDirectory = null)
        {
            _rootDirectory = rootDirectory ?? AppContext.BaseDirectory!;
            _modelImporter = new ModelImporter();
        }

        public T Load<T>(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("Path cannot be null or empty", nameof(path));

            string fullPath = GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("File not found", fullPath);

            string cacheKey = fullPath;

            if (_cache.TryGetValue(cacheKey, out var cached))
                return (T)cached;

            object result = LoadInternal<T>(fullPath, path);
            _cache[cacheKey] = result;

            if (!_typeCache.ContainsKey(typeof(T)))
                _typeCache[typeof(T)] = result;

            return (T)result;
        }

        private object LoadInternal<T>(string fullPath, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (typeof(T) == typeof(ModelMesh) || typeof(T) == typeof(MeshletMesh))
            {
                var modelMesh = _modelImporter.Import(fullPath);
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

        public void Unload<T>(T asset)
        {
            if (asset == null) return;

            foreach (var kvp in _cache)
            {
                if (kvp.Value == (object)asset)
                {
                    _cache.Remove(kvp.Key);
                    break;
                }
            }

            if (_typeCache.TryGetValue(typeof(T), out var typed) && typed == (object)asset)
                _typeCache.Remove(typeof(T));

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
            _typeCache.Clear();
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
        }

        ~ContentManager()
        {
            Dispose();
        }
    }
}
