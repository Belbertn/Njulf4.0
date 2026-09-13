namespace Njulf.Assets;

public partial class ContentManager
{
    private readonly Dictionary<Type, (Func<string, object> Load, int Thread)> _loaders = [];

    /// <summary>Registers an optional synchronous, cache-owned asset loader. Paths are absolute;
    /// registration and loads must use the same device thread. Register each type once before loading it.</summary>
    public void RegisterLoader<T>(Func<string, T> loader) where T : class, IDisposable
    {
        ArgumentNullException.ThrowIfNull(loader);
        lock (_stateLock)
        {
            ThrowIfDisposed();
            if (!_loaders.TryAdd(typeof(T), (path => loader(path), Environment.CurrentManagedThreadId)))
                throw new InvalidOperationException($"A loader for {typeof(T).Name} is already registered.");
        }
    }

    private bool TryLoadRegistered<T>(string path, out T result)
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();
            if (!_loaders.TryGetValue(typeof(T), out var loader)) { result = default!; return false; }
            if (Environment.CurrentManagedThreadId != loader.Thread)
                throw new InvalidOperationException("Registered content loaders require their registering device thread.");
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            string fullPath = Path.GetFullPath(GetFullPath(path));
            string identity = OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;
            string key = $"registered|{typeof(T).AssemblyQualifiedName}|{identity}";
            if (TryGetCachedAsset(key, out var cached)) { result = (T)cached; return true; }
            object asset = loader.Load(fullPath) ?? throw new InvalidOperationException("The content loader returned null.");
            PublishOwnedAsset(key, asset);
            result = (T)asset;
            return true;
        }
    }
}
