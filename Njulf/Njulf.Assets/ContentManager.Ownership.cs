using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Assets.Scenes;

namespace Njulf.Assets;

public partial class ContentManager
{
    /// <summary>Optional device provider for standalone graphics assets. Invoked on the device thread.</summary>
    public Func<GraphicsDevice>? GraphicsDeviceProvider { get; init; }

    private sealed class ContentOwner
    {
        public readonly HashSet<object> Assets = new(ReferenceEqualityComparer.Instance);
        public CancellationTokenSource Cancellation = new();
        public long Generation;
        public bool Closed;
    }

    private sealed class Acquisition(ContentOwner destination, long generation, CancellationTokenSource cancellation)
    {
        public ContentOwner Destination { get; } = destination;
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ContentOwner Pins { get; } = new() { Closed = true };
    }

    private readonly ContentOwner _rootOwner = new();
    private readonly HashSet<ContentOwner> _contentOwners = [];
    private readonly Dictionary<object, HashSet<ContentOwner>> _assetOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, IContentScope> _assetDependencies = new(ReferenceEqualityComparer.Instance);
    private readonly AsyncLocal<ContentOwner?> _loadingOwner = new();
    private readonly AsyncLocal<Acquisition?> _currentAcquisition = new();

    public IContentScope CreateScope()
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();
            var owner = new ContentOwner();
            _contentOwners.Add(owner);
            return new ContentScope(this, owner);
        }
    }

    private Acquisition BeginAcquisition(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();
            ContentOwner owner = _loadingOwner.Value ?? _rootOwner;
            ObjectDisposedException.ThrowIf(owner.Closed, typeof(IContentScope));
            var result = new Acquisition(owner, owner.Generation,
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner.Cancellation.Token, _shutdownCancellation.Token));
            _contentOwners.Add(result.Pins);
            _activeOperations++;
            return result;
        }
    }

    private static void ValidateAcquisition(Acquisition acquisition)
    {
        acquisition.Cancellation.Token.ThrowIfCancellationRequested();
        if (acquisition.Destination.Closed || acquisition.Destination.Generation != acquisition.Generation)
            throw new OperationCanceledException("The content owner was unloaded while this asset was loading.");
    }

    private T LoadOwned<T>(Func<T> load)
    {
        Acquisition acquisition = BeginAcquisition(default);
        Acquisition? previous = _currentAcquisition.Value;
        _currentAcquisition.Value = acquisition;
        try
        {
            T result = load();
            CommitAcquisition(acquisition);
            return result;
        }
        catch (Exception failure)
        {
            try { ReleaseOwner(acquisition.Pins); }
            catch (Exception cleanup) { throw new AggregateException("Content loading and rollback failed.", failure, cleanup); }
            throw;
        }
        finally
        {
            _currentAcquisition.Value = previous;
            acquisition.Cancellation.Dispose();
            lock (_stateLock) _activeOperations--;
        }
    }

    private async Task<T> LoadOwnedAsync<T>(Func<Task<T>> load, CancellationToken cancellationToken)
    {
        Acquisition acquisition = BeginAcquisition(cancellationToken);
        Acquisition? previous = _currentAcquisition.Value;
        _currentAcquisition.Value = acquisition;
        try
        {
            T result = await load().ConfigureAwait(false);
            CommitAcquisition(acquisition);
            return result;
        }
        catch (Exception failure)
        {
            try
            {
                bool needsRelease;
                lock (_stateLock) needsRelease = acquisition.Pins.Assets.Count != 0;
                if (needsRelease && _contentUploadDispatcher != null)
                    await ReleaseAfterFailedLoadAsync(() => ReleaseOwner(acquisition.Pins)).ConfigureAwait(false);
                else ReleaseOwner(acquisition.Pins);
            }
            catch (Exception cleanup) { throw new AggregateException("Content loading and rollback failed.", failure, cleanup); }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            _currentAcquisition.Value = previous;
            acquisition.Cancellation.Dispose();
            lock (_stateLock) _activeOperations--;
        }
    }

    private async Task ReleaseAfterFailedLoadAsync(Action release)
    {
        if (_contentUploadDispatcher == null) { release(); return; }
        try
        {
            await _contentUploadDispatcher.DispatchAsync(() => { release(); return true; }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (_stopping)
        {
            // Shutdown can close upload admission before cancellation unwinds. Every
            // acquisition remains in the ownership tables for final manager disposal.
        }
    }

    private void RetainFailedPublication(object asset)
    {
        _cache.Add($"failed-publication|{++_snapshotOwnershipSequence}", asset);
        ClaimAsset(asset, _rootOwner);
    }

    private void PublishWithDependencies(string key, object asset, IContentScope? dependencies)
    {
        if (dependencies != null) _assetDependencies.Add(asset, dependencies);
        try { PublishOwnedAsset(key, asset); }
        catch
        {
            if (!_assetOwners.ContainsKey(asset)) _assetDependencies.Remove(asset);
            throw;
        }
    }

    private void ReleaseUnusedDependencies(IContentScope? dependencies)
    {
        if (dependencies == null) return;
        lock (_stateLock)
            if (_assetDependencies.Values.Contains(dependencies)) return;
        dependencies.Dispose();
    }

    private void CommitAcquisition(Acquisition acquisition)
    {
        lock (_stateLock)
        {
            ValidateAcquisition(acquisition);
            foreach (object asset in acquisition.Pins.Assets)
            {
                ClaimAsset(asset, acquisition.Destination);
                _assetOwners[asset].Remove(acquisition.Pins);
            }
            acquisition.Pins.Assets.Clear();
            _contentOwners.Remove(acquisition.Pins);
            acquisition.Pins.Cancellation.Dispose();
        }
    }

    private void ClaimAsset(object asset, ContentOwner owner)
    {
        if (!_assetOwners.TryGetValue(asset, out var owners))
            _assetOwners.Add(asset, owners = []);
        owners.Add(owner);
        owner.Assets.Add(asset);
        _contentOwners.Add(owner);
    }

    private void ClaimCurrentAsset(object asset)
    {
        Acquisition? acquisition = _currentAcquisition.Value;
        if (acquisition != null) ValidateAcquisition(acquisition);
        ClaimAsset(asset, acquisition?.Pins ?? _rootOwner);
    }

    private bool TryGetCachedAsset(string key, [NotNullWhen(true)] out object? asset)
    {
        if (!_cache.TryGetValue(key, out asset)) return false;
        if (asset is IGraphicsResource { IsDisposed: true })
            throw new InvalidOperationException("This content asset has been released; retry its owner's failed unload before loading it again.");
        // A cancelled operation may still need to retire a losing cooperative upload.
        // Pin the winner until unwind finishes; only commit/publication validates admission.
        ClaimAsset(asset, _currentAcquisition.Value?.Pins ?? _rootOwner);
        return true;
    }

    private void ReleaseAsset(ContentOwner owner, object asset)
    {
        lock (_stateLock)
        {
            if (!owner.Assets.Contains(asset))
                throw new ArgumentException("Only assets acquired by this content owner can be unloaded.", nameof(asset));
            HashSet<ContentOwner> owners = _assetOwners[asset];
            if (owners.Count == 1)
            {
                // Preserve authoritative records until both releases succeed, allowing retry.
                (asset as IDisposable)?.Dispose();
                if (_assetDependencies.TryGetValue(asset, out var dependencies))
                    dependencies.Dispose();
                _assetDependencies.Remove(asset);
                RemoveCacheEntries(asset);
                _assetOwners.Remove(asset);
            }
            owners.Remove(owner);
            owner.Assets.Remove(asset);
        }
    }

    private void ReleaseOwner(ContentOwner owner)
    {
        lock (_stateLock)
        {
            List<Exception>? failures = null;
            foreach (object asset in owner.Assets.OrderBy(a => a is Scene ? 0 : a is Material ? 1 : 2).ToArray())
            {
                try { ReleaseAsset(owner, asset); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            if (failures != null) throw new AggregateException("Content release failed; ownership was retained for retry.", failures);
            if (owner.Closed) owner.Cancellation.Dispose();
            if (owner.Closed || !ReferenceEquals(owner, _rootOwner)) _contentOwners.Remove(owner);
        }
    }

    private void UnloadOwner(ContentOwner owner, bool close)
    {
        CancellationTokenSource? cancellation = null;
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (owner.Closed && close && owner.Assets.Count == 0) return;
                if (owner.Closed && !close) throw new ObjectDisposedException(nameof(IContentScope));
                owner.Generation++;
                owner.Closed |= close;
                cancellation = owner.Cancellation;
                owner.Cancellation = new CancellationTokenSource();
                ReleaseOwner(owner);
            }
        }
        finally
        {
            // Cancel outside the state lock, after the old claims have been removed.
            // Loads admitted into the new generation cannot be swept up by UnloadAll.
            if (cancellation != null)
            {
                try { cancellation.Cancel(); }
                finally { cancellation.Dispose(); }
            }
        }
    }

    private T InScope<T>(ContentOwner owner, Func<T> action)
    {
        ContentOwner? previousOwner = _loadingOwner.Value;
        Acquisition? previousAcquisition = _currentAcquisition.Value;
        _loadingOwner.Value = owner;
        _currentAcquisition.Value = null;
        try { return action(); }
        finally { _loadingOwner.Value = previousOwner; _currentAcquisition.Value = previousAcquisition; }
    }

    private sealed class ContentScope(ContentManager manager, ContentOwner owner) : IContentScope
    {
        public IContentScope CreateScope() { ObjectDisposedException.ThrowIf(owner.Closed, this); return manager.CreateScope(); }
        public T Load<T>(string path) => Load<T>(path, ContentLoadOptions.Default);
        public T Load<T>(string path, ContentLoadOptions options) => manager.InScope(owner, () => manager.Load<T>(path, options));
        public Task<T> LoadAsync<T>(string path, ContentLoadOptions? options = null, CancellationToken cancellationToken = default) =>
            manager.InScope(owner, () => manager.LoadAsync<T>(path, options, cancellationToken));
        public Task<ContentPreloadResult<T>> PreloadAsync<T>(IEnumerable<ContentPreloadRequest> requests, ContentPreloadOptions? options = null, CancellationToken cancellationToken = default) =>
            manager.InScope(owner, () => manager.PreloadAsync<T>(requests, options, cancellationToken));
        public Scene LoadScene(string path, SceneLoadOptions? options = null) => manager.InScope(owner, () => manager.LoadScene(path, options));
        public Task<Scene> LoadSceneAsync(string path, SceneLoadOptions? options = null, CancellationToken cancellationToken = default) =>
            manager.InScope(owner, () => manager.LoadSceneAsync(path, options, cancellationToken));
        public void Unload<T>(T asset) { ObjectDisposedException.ThrowIf(manager._disposed || owner.Closed, this); if (asset != null) manager.ReleaseAsset(owner, asset); }
        public void UnloadAll() => manager.UnloadOwner(owner, false);
        public void Dispose() { if (!manager._disposed) manager.UnloadOwner(owner, true); }
    }

    // The host executes queued callbacks without the caller's ExecutionContext. Carry only
    // the acquisition explicitly, including cooperative work that spans multiple frames.
    private sealed class OwnershipUploadDispatcher(ContentManager manager, IContentUploadDispatcher inner) : IContentUploadDispatcher
    {
        private T Invoke<T>(Acquisition? acquisition, Func<T> callback)
        {
            Acquisition? previous = manager._currentAcquisition.Value;
            manager._currentAcquisition.Value = acquisition;
            try { return callback(); }
            finally { manager._currentAcquisition.Value = previous; }
        }
        public Task<T> DispatchAsync<T>(Func<T> callback, CancellationToken cancellationToken)
        {
            Acquisition? acquisition = manager._currentAcquisition.Value;
            return inner.DispatchAsync(() => Invoke(acquisition, callback), cancellationToken);
        }
        public Task<T> DispatchAsync<T>(IContentUploadWork<T> work, CancellationToken cancellationToken) =>
            inner.DispatchAsync(new OwnedWork<T>(this, manager._currentAcquisition.Value, work), cancellationToken);
        private sealed class OwnedWork<T>(OwnershipUploadDispatcher dispatcher, Acquisition? acquisition, IContentUploadWork<T> work) : IContentUploadWork<T>
        {
            public ContentUploadStepResult ExecuteStep(in ContentUploadSliceBudget budget)
            {
                var copy = budget;
                return dispatcher.Invoke(acquisition, () => work.ExecuteStep(copy));
            }
            public T GetResult() => dispatcher.Invoke(acquisition, work.GetResult);
            public void RequestCancellation() => dispatcher.Invoke(acquisition, () => { work.RequestCancellation(); return true; });
        }
    }
}
