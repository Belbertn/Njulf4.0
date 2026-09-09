using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>Resolved storage for one light. Identity is independent of its packed GPU index.</summary>
public sealed record LocalShadowLightDiagnostics(uint Identity, LightType Type, uint RequestedResolution,
    uint EffectiveResolution, bool Resident, string Status)
{
    public static string DescribeStatus(Light light, ShadowSettings settings, bool selected, bool resident, bool allocationFailed)
    {
        if (!light.CastsShadows) return "Shadows off";
        if (!(light.Intensity > 0)) return "Light intensity must be greater than zero";
        if (!(light.Range > 0)) return "Light range must be greater than zero";
        bool point = light.Type == LightType.Point;
        if (!(point ? settings.PointShadowsEnabled : settings.SpotShadowsEnabled))
            return point
                ? "Point shadows disabled (Shadows > Point > Point Shadows Enabled)"
                : "Spot shadows disabled (Shadows > Spot > Spot Shadows Enabled)";
        if (!point && !(light.SpotAngle > 0.01f && light.SpotAngle < MathF.PI * 0.99f)) return "Invalid spot cone angle";
        if (allocationFailed) return "GPU allocation failed";
        if (!selected)
        {
            int limit = point ? settings.MaxShadowedPointLights : settings.MaxShadowedSpotLights;
            string type = point ? "Point" : "Spot";
            return limit == 0 ? $"{type} shadow count limit is 0" : $"{type} shadow count limit reached ({limit})";
        }
        return resident ? "Pending" : $"Local shadow memory budget ({settings.LocalShadowMemoryBudgetMiB} MiB)";
    }
}

public sealed record LocalShadowAllocation(uint Identity, SelectedLocalShadow Selected,
    uint RequestedResolution, uint Resolution, SpotShadowAtlasRect Region);

/// <summary>Plans real depth storage before publishing any shadow indices.</summary>
public sealed class LocalShadowLayout
{
    private Dictionary<uint, SpotShadowAtlasRect> _previousSpots = new();
    public LocalShadowAllocation[] Points { get; private set; } = [];
    public LocalShadowAllocation[] Spots { get; private set; } = [];
    public uint SpotAtlasSize { get; private set; }
    public ulong ImageBytes { get; private set; }
    public int DowngradedCount => Points.Concat(Spots).Count(x => x.Resolution < x.RequestedResolution);

    public void Clear() { _previousSpots.Clear(); Points = []; Spots = []; SpotAtlasSize = 0; ImageBytes = 0; }

    public LocalShadowSelection Plan(LocalShadowSelection selection, ReadOnlySpan<uint> identities, ShadowSettings settings)
    {
        var requests = new List<LocalShadowAllocation>();
        foreach (var selected in selection.PointLights.Concat(selection.SpotLights))
        {
            uint identity = selected.LightIndex < identities.Length ? identities[selected.LightIndex] : selected.StableIdentity;
            uint requested = ResolveResolution(selected.Light.ShadowMapSizeOverride,
                selected.Light.Type == LightType.Point ? settings.PointShadowMapSize : settings.SpotShadowTileSize);
            requests.Add(new(identity, selected, requested, requested, default));
        }
        // Preserve explicit priority before geometric importance. Stable ties avoid resolution churn.
        requests.Sort((a, b) => {
            int order = b.Selected.Light.ShadowPriority.CompareTo(a.Selected.Light.ShadowPriority);
            return order != 0 ? order : a.Identity.CompareTo(b.Identity);
        });
        ulong budget = (ulong)settings.LocalShadowMemoryBudgetMiB * 1024 * 1024;
        // Existing shared metadata buffers are resident independently of light count.
        const ulong metadataBytes = (432UL + 112UL + 16UL) * LightManager.MaxLights;
        budget = budget > metadataBytes ? budget - metadataBytes : 0;
        Dictionary<uint, SpotShadowAtlasRect> regions = new();
        uint atlasSize = 0;
        ulong bytes = 0;
        while (requests.Count > 0)
        {
            bool packed = false;
            var spots = requests.Where(x => x.Selected.Light.Type == LightType.Spot).ToArray();
            atlasSize = 0;
            if (spots.Length == 0) { regions = new(); packed = true; }
            else for (uint size = 1024; size <= settings.SpotShadowAtlasSize; size *= 2)
            {
                if (TryPack(spots, size, _previousSpots, out regions) || TryPack(spots, size, null, out regions))
                { atlasSize = size; packed = true; break; }
            }
            bytes = requests.Where(x => x.Selected.Light.Type == LightType.Point)
                .Aggregate(0UL, (sum, x) => sum + PointImageBytes(x.Resolution)) + 8UL * atlasSize * atlasSize;
            if (packed && bytes <= budget) break;
            int index = requests.FindLastIndex(x => x.Resolution > 128 &&
                (packed || x.Selected.Light.Type == LightType.Spot));
            if (index >= 0) requests[index] = requests[index] with { Resolution = requests[index].Resolution / 2 };
            else if (!packed)
                requests.RemoveAt(requests.FindLastIndex(x => x.Selected.Light.Type == LightType.Spot));
            else requests.RemoveAt(requests.Count - 1);
        }
        if (requests.Count == 0) { atlasSize = 0; bytes = 0; regions.Clear(); }
        var admitted = requests.ToDictionary(x => x.Selected.LightIndex);
        // Preserve selected order for the existing priority-based foliage budget.
        LocalShadowAllocation[] Resolve(SelectedLocalShadow[] lights) => lights
            .Select(x => admitted.GetValueOrDefault(x.LightIndex))
            .Where(x => x != null).Select(x => x!).ToArray();
        Points = Resolve(selection.PointLights);
        Spots = Resolve(selection.SpotLights).Select(x => x with { Region = regions[x.Identity] }).ToArray();
        _previousSpots = regions;
        SpotAtlasSize = atlasSize;
        ImageBytes = bytes;
        return ApplyTo(selection);
    }

    public void RetainAvailable(IReadOnlySet<uint> pointIdentities, bool spotsAvailable)
    {
        Points = Points.Where(x => pointIdentities.Contains(x.Identity)).ToArray();
        if (!spotsAvailable) Spots = [];
    }

    public LocalShadowSelection ApplyTo(LocalShadowSelection selection)
    {
        return new LocalShadowSelection
        {
            PointLights = Points.Select(x => x.Selected).ToArray(),
            SpotLights = Spots.Select(x => x.Selected).ToArray(), AreaLights = selection.AreaLights,
            PointCandidateCount = selection.PointCandidateCount, SpotCandidateCount = selection.SpotCandidateCount,
            AreaCandidateCount = selection.AreaCandidateCount,
            PointRejectedByBudgetCount = selection.PointCandidateCount - Points.Length,
            SpotRejectedByBudgetCount = selection.SpotCandidateCount - Spots.Length,
            AreaRejectedByBudgetCount = selection.AreaRejectedByBudgetCount,
            SpotAtlasCapacity = SpotAtlasSize == 0 ? 0 : (int)(SpotAtlasSize / 128 * (SpotAtlasSize / 128))
        };
    }

    public static uint ResolveResolution(uint requested, uint fallback)
    {
        uint value = Math.Clamp(requested == 0 ? fallback : requested, 128u, 2048u);
        uint size = 128;
        while (size < value) size *= 2;
        return size;
    }
    public static ulong PointImageBytes(uint size) => 6UL * size * size * 4 * 2;

    // An aligned minimum-tile occupancy grid implements the same square splits as a quadtree,
    // without allocating nodes. Retain surviving regions before placing new requests.
    private static bool TryPack(LocalShadowAllocation[] requests, uint size,
        Dictionary<uint, SpotShadowAtlasRect>? previous, out Dictionary<uint, SpotShadowAtlasRect> result)
    {
        result = new();
        int side = (int)(size / 128);
        var occupied = new bool[side, side];
        void Mark(SpotShadowAtlasRect rect)
        {
            for (int y = (int)(rect.Y / 128); y < (rect.Y + rect.Height) / 128; y++)
            for (int x = (int)(rect.X / 128); x < (rect.X + rect.Width) / 128; x++) occupied[x, y] = true;
        }
        foreach (var request in requests)
            if (previous != null && previous.TryGetValue(request.Identity, out var rect) &&
                rect.Width == request.Resolution && rect.X + rect.Width <= size && rect.Y + rect.Height <= size)
            { result.Add(request.Identity, rect); Mark(rect); }
        foreach (var request in requests.OrderByDescending(x => x.Resolution).ThenBy(x => x.Identity))
        {
            if (result.ContainsKey(request.Identity)) continue;
            int width = (int)(request.Resolution / 128);
            bool placed = false;
            for (int y = 0; y + width <= side && !placed; y += width)
            for (int x = 0; x + width <= side && !placed; x += width)
            {
                bool free = true;
                for (int yy = y; yy < y + width && free; yy++)
                for (int xx = x; xx < x + width; xx++) if (occupied[xx, yy]) { free = false; break; }
                if (!free) continue;
                var rect = new SpotShadowAtlasRect((uint)x * 128, (uint)y * 128, request.Resolution, request.Resolution);
                result.Add(request.Identity, rect); Mark(rect); placed = true;
            }
            if (!placed) return false;
        }
        return true;
    }
}
