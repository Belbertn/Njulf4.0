using Njulf.Assets;
using Njulf.Core.Scene;

namespace NjulfHelloGame;

internal enum SampleSceneResidencyState
{
    None,
    FirstViewReady,
    FullyResident
}

/// <summary>
/// Scene-level policy over ContentManager's authoritative object cache. It
/// never creates a second ownership model: eviction delegates to Unload, and
/// shared assets remain resident while any tracked scene group references
/// them.
/// </summary>
internal sealed class SampleSceneResidencyCache
{
    internal const ulong MaximumUnpinnedBytes =
        1024UL * 1024UL * 1024UL;
    internal const double BudgetFraction = 0.20;
    internal const double TrimUsageFraction = 0.65;

    private readonly Func<SampleAssetReference, Model> _load;
    private readonly Action<Model> _unload;
    private readonly Dictionary<SampleSceneKind, SceneGroup> _groups = [];
    private readonly Dictionary<string, AssetEntry> _assets =
        new(StringComparer.OrdinalIgnoreCase);
    private long _clock;
    private SampleSceneKind? _active;
    private SampleSceneKind? _pending;

    public SampleSceneResidencyCache(ContentManager content)
        : this(
            CreateLoadDelegate(content),
            CreateUnloadDelegate(content))
    {
    }

    internal SampleSceneResidencyCache(
        Func<SampleAssetReference, Model> load,
        Action<Model> unload)
    {
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _unload = unload ?? throw new ArgumentNullException(nameof(unload));
    }

    public bool Contains(SampleSceneKind kind) => _groups.ContainsKey(kind);

    public SampleSceneResidencyState GetState(SampleSceneKind kind) =>
        _groups.TryGetValue(kind, out SceneGroup? group)
            ? group.State
            : SampleSceneResidencyState.None;

    public ulong GetEstimatedBytes(SampleSceneKind kind) =>
        _groups.TryGetValue(kind, out SceneGroup? group)
            ? group.EstimatedBytes
            : 0;

    public void MarkPending(SampleSceneKind? kind)
    {
        _pending = kind;
        if (kind.HasValue && _groups.TryGetValue(kind.Value, out SceneGroup? group))
            group.LastUse = NextClock();
    }

    public void MarkActive(SampleSceneKind kind)
    {
        _active = kind;
        _pending = null;
        if (_groups.TryGetValue(kind, out SceneGroup? group))
            group.LastUse = NextClock();
    }

    public void Capture(
        SampleSceneKind kind,
        SampleAssetManifest? manifest,
        ulong estimatedBytes)
    {
        if (manifest == null)
            return;

        Capture(
            kind,
            manifest.EnumerateAssets(),
            estimatedBytes,
            SampleSceneResidencyState.FullyResident);
    }

    public void Capture(
        SampleSceneKind kind,
        IEnumerable<SampleAssetReference> assets,
        ulong estimatedBytes,
        SampleSceneResidencyState state)
    {
        ArgumentNullException.ThrowIfNull(assets);
        if (state == SampleSceneResidencyState.None)
            throw new ArgumentOutOfRangeException(nameof(state));

        SampleAssetReference[] references = assets
            .GroupBy(CreateAssetKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        if (references.Length == 0)
            return;

        ulong perAsset = Math.Max(
            1,
            estimatedBytes / checked((ulong)references.Length));
        var createdKeys = new List<string>(references.Length);
        try
        {
            foreach (SampleAssetReference reference in references)
            {
                string key = CreateAssetKey(reference);
                if (!_assets.TryGetValue(key, out AssetEntry? entry))
                {
                    Model model = _load(reference);
                    entry = new AssetEntry(model, perAsset);
                    _assets.Add(key, entry);
                    createdKeys.Add(key);
                }
                else
                {
                    entry.EstimatedBytes = Math.Max(
                        entry.EstimatedBytes,
                        perAsset);
                }
            }

            if (!_groups.TryGetValue(kind, out SceneGroup? group))
            {
                group = new SceneGroup(
                    Array.Empty<string>(),
                    estimatedBytes,
                    NextClock(),
                    state);
                _groups.Add(kind, group);
            }
            else
            {
                group.EstimatedBytes = Math.Max(
                    group.EstimatedBytes,
                    estimatedBytes);
                group.LastUse = NextClock();
                group.State = (SampleSceneResidencyState)Math.Max(
                    (int)group.State,
                    (int)state);
            }

            foreach (SampleAssetReference reference in references)
            {
                string key = CreateAssetKey(reference);
                _assets[key].Groups.Add(kind);
                group.AssetKeys.Add(key);
            }
        }
        catch (Exception captureFailure)
        {
            List<Exception>? rollbackFailures = null;
            foreach (string key in createdKeys.AsEnumerable().Reverse())
            {
                if (!_assets.TryGetValue(key, out AssetEntry? entry))
                    continue;
                if (entry.Groups.Count != 0)
                    continue;

                try
                {
                    _unload(entry.Model);
                    _assets.Remove(key);
                }
                catch (Exception rollbackFailure)
                {
                    (rollbackFailures ??= []).Add(rollbackFailure);
                }
            }

            if (rollbackFailures is { Count: > 0 })
            {
                throw new AggregateException(
                    "Scene residency capture failed and cache ownership rollback was incomplete.",
                    new[] { captureFailure }.Concat(rollbackFailures));
            }

            throw;
        }
    }

    public IReadOnlyList<SampleSceneKind> Trim(
        ulong effectiveBudgetBytes,
        ulong currentUsageBytes,
        SampleSceneKind? protectedKind = null)
    {
        if (effectiveBudgetBytes == 0)
            return Array.Empty<SampleSceneKind>();

        ulong cacheBudget = Math.Min(
            MaximumUnpinnedBytes,
            (ulong)Math.Floor(effectiveBudgetBytes * BudgetFraction));
        ulong usageTarget = (ulong)Math.Floor(
            effectiveBudgetBytes * TrimUsageFraction);
        ulong unpinned = CalculateUnpinnedBytes(protectedKind);
        ulong projectedUsage = currentUsageBytes;
        var evicted = new List<SampleSceneKind>();
        while (unpinned > cacheBudget || projectedUsage > usageTarget)
        {
            KeyValuePair<SampleSceneKind, SceneGroup>? candidate = _groups
                .Where(pair => !IsPinned(pair.Key, protectedKind))
                .OrderBy(pair => pair.Value.LastUse)
                .Cast<KeyValuePair<SampleSceneKind, SceneGroup>?>()
                .FirstOrDefault();
            if (!candidate.HasValue)
                break;

            SampleSceneKind kind = candidate.Value.Key;
            ulong freedBytes = Evict(kind);
            evicted.Add(kind);
            unpinned = CalculateUnpinnedBytes(protectedKind);
            projectedUsage = projectedUsage > freedBytes
                ? projectedUsage - freedBytes
                : 0;
        }

        return evicted;
    }

    public void EvictAllUnpinned()
    {
        SampleSceneKind[] candidates = _groups.Keys
            .Where(kind => !IsPinned(kind))
            .ToArray();
        foreach (SampleSceneKind kind in candidates)
            Evict(kind);
    }

    /// <summary>
    /// Releases the active scene's cached model ownership incrementally. The
    /// caller must first detach every live scene instance so partially retired
    /// templates cannot be observed by rendering or scene construction.
    /// </summary>
    /// <returns>True once the active residency group is fully released.</returns>
    public bool ReleaseActiveAssetsStep(int maximumRenderObjects)
    {
        if (maximumRenderObjects <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRenderObjects));
        if (!_active.HasValue)
            return true;

        SampleSceneKind kind = _active.Value;
        if (!_groups.TryGetValue(kind, out SceneGroup? group))
        {
            _active = null;
            return true;
        }

        int releasedRenderObjects = 0;
        foreach (string key in group.AssetKeys.ToArray())
        {
            if (!_assets.TryGetValue(key, out AssetEntry? entry))
            {
                group.AssetKeys.Remove(key);
                continue;
            }
            if (entry.Groups.Count > 1)
            {
                entry.Groups.Remove(kind);
                group.AssetKeys.Remove(key);
                continue;
            }

            while (entry.Model.RenderObjects.Count > 0 &&
                   releasedRenderObjects < maximumRenderObjects)
            {
                RenderObject renderObject =
                    entry.Model.RenderObjects[^1];
                entry.Model.Remove(renderObject);
                releasedRenderObjects++;
            }
            if (entry.Model.RenderObjects.Count > 0)
                return false;

            // Unload completes model-level release actions and removes the
            // ContentManager cache aliases. Keep residency ownership intact
            // until that operation succeeds so a failed release is retryable.
            _unload(entry.Model);
            entry.Groups.Remove(kind);
            _assets.Remove(key);
            group.AssetKeys.Remove(key);

            if (releasedRenderObjects >= maximumRenderObjects &&
                group.AssetKeys.Count > 0)
            {
                return false;
            }
        }

        _groups.Remove(kind);
        _active = null;
        if (_pending == kind)
            _pending = null;
        return true;
    }

    public void ResetAfterContentClear()
    {
        _groups.Clear();
        _assets.Clear();
        _active = null;
        _pending = null;
    }

    private ulong Evict(SampleSceneKind kind)
    {
        if (!_groups.TryGetValue(kind, out SceneGroup? group))
            return 0;

        ulong freedBytes = 0;
        foreach (string key in group.AssetKeys)
        {
            if (!_assets.TryGetValue(key, out AssetEntry? entry))
                continue;
            if (entry.Groups.Count > 1)
            {
                entry.Groups.Remove(kind);
                continue;
            }

            _unload(entry.Model);
            freedBytes = SaturatingAdd(
                freedBytes,
                entry.EstimatedBytes);
            entry.Groups.Remove(kind);
            _assets.Remove(key);
        }

        _groups.Remove(kind);
        return freedBytes;
    }

    private bool IsPinned(
        SampleSceneKind kind,
        SampleSceneKind? protectedKind = null) =>
        _active == kind || _pending == kind || protectedKind == kind;

    private ulong CalculateUnpinnedBytes(
        SampleSceneKind? protectedKind = null)
    {
        ulong total = 0;
        foreach (AssetEntry asset in _assets.Values)
        {
            if (asset.Groups.Any(kind => IsPinned(kind, protectedKind)))
                continue;
            total = SaturatingAdd(total, asset.EstimatedBytes);
        }
        return total;
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right
            ? ulong.MaxValue
            : left + right;

    private long NextClock() => _clock == long.MaxValue
        ? _clock = 1
        : ++_clock;

    private static string CreateAssetKey(SampleAssetReference asset) =>
        asset.CreateContentIdentity();

    private static Func<SampleAssetReference, Model> CreateLoadDelegate(
        ContentManager content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return reference => content.Load<Model>(
            reference.Path,
            reference.CreateLoadOptions());
    }

    private static Action<Model> CreateUnloadDelegate(ContentManager content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return content.Unload;
    }

    private sealed class SceneGroup
    {
        public SceneGroup(
            IReadOnlyList<string> assetKeys,
            ulong estimatedBytes,
            long lastUse,
            SampleSceneResidencyState state)
        {
            AssetKeys = new HashSet<string>(
                assetKeys,
                StringComparer.OrdinalIgnoreCase);
            EstimatedBytes = estimatedBytes;
            LastUse = lastUse;
            State = state;
        }

        public HashSet<string> AssetKeys { get; }
        public ulong EstimatedBytes { get; set; }
        public long LastUse { get; set; }
        public SampleSceneResidencyState State { get; set; }
    }

    private sealed class AssetEntry
    {
        public AssetEntry(Model model, ulong estimatedBytes)
        {
            Model = model;
            EstimatedBytes = estimatedBytes;
        }

        public Model Model { get; }
        public ulong EstimatedBytes { get; set; }
        public HashSet<SampleSceneKind> Groups { get; } = [];
    }
}
