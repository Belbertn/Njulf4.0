namespace Njulf.Assets
{
    /// <summary>Loads cache-owned assets. Create model instances for independently owned placements.</summary>
    public interface IContentManager
    {
        /// <summary>Loads a cache-owned asset synchronously with default options on the device thread.</summary>
        /// <remarks>Repeated equivalent requests share the cached asset. Create a model instance for independent placement.</remarks>
        T Load<T>(string path);
        /// <summary>Loads synchronously using explicit import and progress options; the result belongs to the cache.</summary>
        T Load<T>(string path, ContentLoadOptions options);
        /// <summary>Loads through the same cache; the host pumps required device uploads.</summary>
        Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null,
            CancellationToken cancellationToken = default);
        /// <summary>Loads a batch through the same cache with bounded admission, priority, progress and cancellation.</summary>
        /// <remarks>The host pumps uploads. Returned assets remain cache-owned; cancellation does not transfer ownership.</remarks>
        Task<ContentPreloadResult<T>> PreloadAsync<T>(
            IEnumerable<ContentPreloadRequest> requests,
            ContentPreloadOptions? options = null,
            CancellationToken cancellationToken = default);
        /// <summary>Releases a cache-owned asset. Foreign assets and model instances are rejected.</summary>
        void Unload<T>(T asset);
        /// <summary>Invalidates pending publication and releases cached assets, retaining failed releases for retry.</summary>
        void UnloadAll();
    }
}
