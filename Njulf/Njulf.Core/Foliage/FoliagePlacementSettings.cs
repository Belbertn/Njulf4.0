using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

/// <summary>
/// Deterministic, renderer-independent foliage placement policy. Every setter
/// sanitizes its value and publishes a revision so unchanged patches remain
/// upload-free while local edits reliably invalidate their generated cells.
/// </summary>
public sealed class FoliagePlacementSettings
{
    private float _density = 1f;
    private float _minimumSpacing = 1f;
    private Vector2 _scaleRange = new(0.85f, 1.15f);
    private Vector2 _yawRangeDegrees = new(0f, 360f);
    private bool _alignToSurfaceNormal;
    private Vector2 _altitudeRange = new(-100_000f, 100_000f);
    private Vector2 _slopeRangeDegrees = new(0f, 90f);
    private uint _biomeMask = uint.MaxValue;
    private bool _allowWater;
    private bool _allowRoads;
    private bool _respectExclusions = true;
    private uint _seed = 1;
    private float _cellSize = 16f;
    private uint _revision = 1;

    public event Action? Changed;

    public float Density { get => _density; set => Set(ref _density, NonNegative(value)); }
    public float MinimumSpacing { get => _minimumSpacing; set => Set(ref _minimumSpacing, Positive(value, 1f)); }
    public Vector2 ScaleRange { get => _scaleRange; set => Set(ref _scaleRange, OrderedPositive(value, new Vector2(0.85f, 1.15f))); }
    public Vector2 YawRangeDegrees { get => _yawRangeDegrees; set => Set(ref _yawRangeDegrees, OrderedFinite(value, new Vector2(0f, 360f))); }
    public bool AlignToSurfaceNormal { get => _alignToSurfaceNormal; set => Set(ref _alignToSurfaceNormal, value); }
    public Vector2 AltitudeRange { get => _altitudeRange; set => Set(ref _altitudeRange, OrderedFinite(value, new Vector2(-100_000f, 100_000f))); }
    public Vector2 SlopeRangeDegrees { get => _slopeRangeDegrees; set => Set(ref _slopeRangeDegrees, OrderedClamped(value, 0f, 90f)); }
    public uint BiomeMask { get => _biomeMask; set => Set(ref _biomeMask, value); }
    public bool AllowWater { get => _allowWater; set => Set(ref _allowWater, value); }
    public bool AllowRoads { get => _allowRoads; set => Set(ref _allowRoads, value); }
    public bool RespectExclusions { get => _respectExclusions; set => Set(ref _respectExclusions, value); }
    public uint Seed { get => _seed; set => Set(ref _seed, value); }
    public float CellSize { get => _cellSize; set => Set(ref _cellSize, Positive(value, 16f)); }
    public uint Revision => _revision;

    private void Set<T>(ref T field, T value) where T : IEquatable<T>
    {
        if (field.Equals(value))
            return;
        field = value;
        _revision = _revision == uint.MaxValue ? 1u : _revision + 1u;
        Changed?.Invoke();
    }

    private static float NonNegative(float value) =>
        float.IsFinite(value) && value > 0f ? value : 0f;

    private static float Positive(float value, float fallback) =>
        float.IsFinite(value) && value > 0f ? value : fallback;

    private static Vector2 OrderedPositive(Vector2 value, Vector2 fallback)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) ||
            value.X <= 0f || value.Y <= 0f)
            return fallback;
        return value.X <= value.Y ? value : new Vector2(value.Y, value.X);
    }

    private static Vector2 OrderedFinite(Vector2 value, Vector2 fallback)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y))
            return fallback;
        return value.X <= value.Y ? value : new Vector2(value.Y, value.X);
    }

    private static Vector2 OrderedClamped(Vector2 value, float min, float max)
    {
        float x = float.IsFinite(value.X) ? System.Math.Clamp(value.X, min, max) : min;
        float y = float.IsFinite(value.Y) ? System.Math.Clamp(value.Y, min, max) : max;
        return x <= y ? new Vector2(x, y) : new Vector2(y, x);
    }
}
