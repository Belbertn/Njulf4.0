using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

public sealed class LocalShadowCacheEntry
{
    public bool StaticDirty { get; internal set; } = true;
    public bool HadDynamic { get; private set; }
    public string LastResult { get; internal set; } = "Pending";
    internal Light Light;
    internal object? Key;
    public void Commit(bool dynamic) { StaticDirty = false; HadDynamic = dynamic; LastResult = dynamic ? "Updated (dynamic)" : "Refreshed"; }
}

/// <summary>Scene changes invalidate depth, independently of packed light indices and camera order.</summary>
public sealed class LocalShadowCache : IDisposable
{
    private readonly Dictionary<uint, LocalShadowCacheEntry> _entries = new();
    private Scene? _scene;
    private readonly HashSet<Guid> _dynamicProducers = new();
    private MaterialManager? _materials;
    public void Attach(Scene scene, MaterialManager? materials = null)
    {
        if (ReferenceEquals(_scene, scene) && ReferenceEquals(_materials, materials)) return;
        Dispose(); _scene = scene; _materials = materials;
        foreach (var obj in scene.RenderObjects.OfType<SkinnedRenderObject>().Where(x => x.SkinningEnabled)) _dynamicProducers.Add(obj.Id);
        scene.Mutated += OnMutation;
        if (materials != null) materials.MaterialChanged += OnMaterialChanged;
    }

    public LocalShadowCacheEntry[] Prepare(LocalShadowAllocation[] allocations, ShadowSettings settings, uint atlasSize = 0)
    {
        var result = new LocalShadowCacheEntry[allocations.Length];
        for (int i = 0; i < allocations.Length; i++)
        {
            var allocation = allocations[i];
            if (!_entries.TryGetValue(allocation.Identity, out var entry))
                _entries.Add(allocation.Identity, entry = new());
            Light light = allocation.Selected.Light;
            bool point = light.Type == LightType.Point;
            var key = new DepthKey(light.Type, light.Position, point ? default : light.Direction,
                light.ShadowNearPlane, light.ShadowFarPlane, light.Range, point ? 0 : light.SpotAngle,
                allocation.Resolution, allocation.Region, atlasSize,
                point ? settings.PointConstantDepthBias : settings.SpotConstantDepthBias,
                point ? settings.PointSlopeScaledDepthBias : settings.SpotSlopeScaledDepthBias);
            if (!settings.LocalShadowCacheEnabled || !key.Equals(entry.Key)) entry.StaticDirty = true;
            entry.Key = key; entry.Light = light; result[i] = entry;
        }
        return result;
    }

    public void Retain(IEnumerable<uint> identities)
    {
        var live = identities.ToHashSet();
        foreach (uint id in _entries.Keys.Where(x => !live.Contains(x)).ToArray()) _entries.Remove(id);
    }

    private void OnMutation(SceneMutation mutation)
    {
        bool wasDynamic = _dynamicProducers.Contains(mutation.ProducerId);
        bool isDynamic = mutation.Producer is SkinnedRenderObject { SkinningEnabled: true } &&
            (mutation.Kind & SceneMutationKind.Removed) == 0;
        if (isDynamic) _dynamicProducers.Add(mutation.ProducerId); else _dynamicProducers.Remove(mutation.ProducerId);
        if (wasDynamic && isDynamic) return;
        const SceneMutationKind geometry = SceneMutationKind.Geometry | SceneMutationKind.Transform |
            SceneMutationKind.Visibility | SceneMutationKind.Material | SceneMutationKind.StaticInstances |
            SceneMutationKind.Added | SceneMutationKind.Removed | SceneMutationKind.Content | SceneMutationKind.Global;
        if ((mutation.Kind & geometry) == 0) return;
        // Skinned geometry is composed afresh; toggling skinning or membership still invalidates
        // the static cache to remove a formerly rigid caster.
        foreach (var entry in _entries.Values)
            if ((!mutation.OldWorldBounds.HasValue && (mutation.Kind & SceneMutationKind.Added) == 0) ||
                (!mutation.NewWorldBounds.HasValue && (mutation.Kind & SceneMutationKind.Removed) == 0) ||
                (mutation.Kind & SceneMutationKind.Global) != 0 ||
                Intersects(entry.Light, mutation.OldWorldBounds) || Intersects(entry.Light, mutation.NewWorldBounds))
                entry.StaticDirty = true;
    }
    public bool[] DynamicCasters(LocalShadowAllocation[] allocations)
    {
        var casters = _scene?.RenderObjects.OfType<SkinnedRenderObject>()
            .Where(x => x.Enabled && x.Visible && x.SkinningEnabled)
            .Select(x => x.AnimatedBoundingBox is { } bounds ? (BoundingBox?)BoundingBox.Transform(bounds, x.WorldMatrix) : null).ToArray() ?? [];
        return allocations.Select(x => casters.Any(bounds => !bounds.HasValue || Intersects(x.Selected.Light, bounds))).ToArray();
    }

    private void OnMaterialChanged(MaterialChangedEvent change)
    {
        if ((change.ChangeMask & (MaterialChangeMask.AlphaCoverage | MaterialChangeMask.Sidedness |
            MaterialChangeMask.TextureDependencies | MaterialChangeMask.ShadingModel)) != 0)
            foreach (var entry in _entries.Values) entry.StaticDirty = true;
    }
    private static bool Intersects(Light light, BoundingBox? bounds)
    {
        if (bounds is not { } box) return false;
        var position = light.Position;
        float x = position.X - Math.Clamp(position.X, box.Min.X, box.Max.X);
        float y = position.Y - Math.Clamp(position.Y, box.Min.Y, box.Max.Y);
        float z = position.Z - Math.Clamp(position.Z, box.Min.Z, box.Max.Z);
        float range = Math.Max(light.Range, light.ShadowFarPlane);
        return x * x + y * y + z * z <= range * range;
    }
    private readonly record struct DepthKey(LightType Type, System.Numerics.Vector3 Position,
        System.Numerics.Vector3 Direction, float Near, float Far, float Range, float Cone,
        uint Resolution, SpotShadowAtlasRect Region, uint AtlasSize, float DepthBias, float SlopeBias);

    public void Dispose()
    {
        if (_scene != null) _scene.Mutated -= OnMutation;
        if (_materials != null) _materials.MaterialChanged -= OnMaterialChanged;
        _scene = null; _materials = null; _entries.Clear(); _dynamicProducers.Clear();
    }
}
