using System;
using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

public sealed class FoliagePatch : Njulf.Core.Scene.IIdentifiedSceneEntity
{
    private string _name = "FoliagePatch";
    private FoliagePrototype _prototype;
    private BoundingBox _bounds;
    private Vector3 _instancePosition = Vector3.Zero;
    private float _instanceScale = 1f;
    private float _density = 1f;
    private uint _seed = 1;
    private FoliagePlacementMode _placementMode =
        FoliagePlacementMode.ProceduralSurface;
    private FoliageDensityMapReference? _densityMap;
    private string? _legacyDensityTexturePath;
    private ITerrainFoliageQuery? _terrainQuery;
    private bool _visible = true;
    private uint _revision = 1;

    public event Action<FoliagePatch, BoundingBox>? Changed;

    public FoliagePatch(FoliagePrototype prototype, BoundingBox bounds)
    {
        _prototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
        _bounds = bounds;
        _placementMode = prototype.GeometryMode ==
            FoliageGeometryMode.AuthoredMeshlets
                ? FoliagePlacementMode.SingleInstance
                : FoliagePlacementMode.ProceduralSurface;
        Placement.Changed += () => IncrementRevision();
    }

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "FoliagePatch" : value;
            if (_name == next)
                return;

            _name = next;
            IncrementRevision();
        }
    }

    public FoliagePrototype Prototype
    {
        get => _prototype;
        set
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (ReferenceEquals(_prototype, value))
                return;

            _prototype = value;
            IncrementRevision();
        }
    }

    public BoundingBox Bounds
    {
        get => _bounds;
        set
        {
            if (_bounds.Equals(value))
                return;

            BoundingBox previousBounds = _bounds;
            _bounds = value;
            IncrementRevision(previousBounds);
        }
    }

    public Vector3 InstancePosition
    {
        get => _instancePosition;
        set
        {
            if (_instancePosition.Equals(value))
                return;

            _instancePosition = value;
            IncrementRevision();
        }
    }

    public float InstanceScale
    {
        get => _instanceScale;
        set
        {
            float next = SanitizeScale(value);
            if (_instanceScale == next)
                return;

            _instanceScale = next;
            IncrementRevision();
        }
    }

    public float Density
    {
        get => _density;
        set
        {
            float next = ClampDensity(value);
            if (_density == next)
                return;

            _density = next;
            IncrementRevision();
        }
    }

    public uint Seed
    {
        get => _seed;
        set
        {
            if (_seed == value)
                return;

            _seed = value;
            IncrementRevision();
        }
    }

    public FoliagePlacementMode PlacementMode
    {
        get => _placementMode;
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_placementMode == value)
                return;
            _placementMode = value;
            IncrementRevision();
        }
    }

    public FoliagePlacementSettings Placement { get; } = new();

    public FoliageDensityMapReference? DensityMap
    {
        get => _densityMap;
        set
        {
            if (ReferenceEquals(_densityMap, value))
                return;
            _densityMap = value;
            _legacyDensityTexturePath = value?.SourcePath;
            IncrementRevision();
        }
    }

    /// <summary>Optional source path retained by scene documents for density texture reloads.</summary>
    public string? DensityTexturePath
    {
        get => _densityMap?.SourcePath ?? _legacyDensityTexturePath;
        set
        {
            if (string.Equals(DensityTexturePath, value, StringComparison.Ordinal))
                return;
            _densityMap = null;
            _legacyDensityTexturePath = value;
            IncrementRevision();
        }
    }

    public ITerrainFoliageQuery? TerrainQuery
    {
        get => _terrainQuery;
        set
        {
            if (ReferenceEquals(_terrainQuery, value))
                return;
            _terrainQuery = value;
            IncrementRevision();
        }
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;

            _visible = value;
            IncrementRevision();
        }
    }

    public uint Revision => _revision;
    public uint ContentRevision => CombineRevision(
        _revision,
        _prototype.Revision,
        Placement.Revision,
        DensityMap?.Revision ?? 0u,
        unchecked((uint)(TerrainQuery?.Revision ?? 0UL)),
        unchecked((uint)((TerrainQuery?.Revision ?? 0UL) >> 32)));

    private static float ClampDensity(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return value < 0f ? 0f : value;
    }

    private static float SanitizeScale(float value)
    {
        if (!float.IsFinite(value) || value <= 0f)
            return 1f;
        return value;
    }

    private static uint CombineRevision(params uint[] revisions)
    {
        unchecked
        {
            uint hash = 2166136261u;
            foreach (uint revision in revisions)
                hash = (hash ^ revision) * 16777619u;
            return hash == 0 ? 1u : hash;
        }
    }

    private void IncrementRevision(BoundingBox? previousBounds = null)
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
        Changed?.Invoke(this, previousBounds ?? _bounds);
    }
}
