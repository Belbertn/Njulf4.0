using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

public enum VolumetricDensityVolumeShape : uint
{
    Box = 0,
    Sphere = 1
}

/// <summary>
/// Authored bounded participating medium. Values use scene metres and inverse
/// metres so the renderer can combine overlapping volumes without depending on
/// draw order.
/// </summary>
public sealed class VolumetricDensityVolume : IIdentifiedSceneEntity
{
    private Guid _id = Guid.NewGuid();
    private string _name = "Density Volume";
    private bool _enabled = true;
    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private VolumetricDensityVolumeShape _shape;
    private Vector3 _boxExtents = new(5f, 5f, 5f);
    private float _radius = 5f;
    private float _edgeFade = 1f;
    private float _densityMultiplier = 1f;
    private float _extinctionPerMeter = 0.08f;
    private Vector3 _scatteringAlbedo = new(0.9f, 0.9f, 0.9f);
    private float _anisotropy = 0.2f;
    private int _priority;
    private float _noiseScale = 0.1f;
    private float _noiseStrength = 0.5f;
    private float _noiseContrast = 1f;
    private uint _noiseSeed = 1u;
    private Vector3 _flowVelocity;
    private ulong _revision;

    public event Action<VolumetricDensityVolume, BoundingBox>? Changed;

    public Guid Id { get => _id; set => Set(ref _id, value == Guid.Empty ? Guid.NewGuid() : value); }
    public string Name { get => _name; set => Set(ref _name, value ?? string.Empty); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public Vector3 Position { get => _position; set => SetSpatial(ref _position, FiniteOrZero(value)); }
    public Quaternion Rotation { get => _rotation; set => SetSpatial(ref _rotation, Normalize(value)); }
    public VolumetricDensityVolumeShape Shape { get => _shape; set => SetSpatial(ref _shape, Enum.IsDefined(value) ? value : VolumetricDensityVolumeShape.Box); }

    public Vector3 BoxExtents
    {
        get => _boxExtents;
        set => SetSpatial(ref _boxExtents, new Vector3(
            MathF.Max(0.001f, FiniteOr(value.X, 0.001f)),
            MathF.Max(0.001f, FiniteOr(value.Y, 0.001f)),
            MathF.Max(0.001f, FiniteOr(value.Z, 0.001f))));
    }

    public float Radius { get => _radius; set => SetSpatial(ref _radius, MathF.Max(0.001f, FiniteOr(value, 0.001f))); }
    public float EdgeFade { get => _edgeFade; set => Set(ref _edgeFade, ClampFinite(value, 0f, 1000f)); }
    public float DensityMultiplier { get => _densityMultiplier; set => Set(ref _densityMultiplier, ClampFinite(value, 0f, 64f)); }
    public float ExtinctionPerMeter { get => _extinctionPerMeter; set => Set(ref _extinctionPerMeter, ClampFinite(value, 0f, 64f)); }
    public Vector3 ScatteringAlbedo { get => _scatteringAlbedo; set => Set(ref _scatteringAlbedo, Clamp01(value)); }
    public float Anisotropy { get => _anisotropy; set => Set(ref _anisotropy, ClampFinite(value, -0.9f, 0.9f)); }
    public int Priority { get => _priority; set => Set(ref _priority, value); }
    public float NoiseScale { get => _noiseScale; set => Set(ref _noiseScale, ClampFinite(value, 0.0001f, 1000f)); }
    public float NoiseStrength { get => _noiseStrength; set => Set(ref _noiseStrength, ClampFinite(value, 0f, 1f)); }
    public float NoiseContrast { get => _noiseContrast; set => Set(ref _noiseContrast, ClampFinite(value, 0.01f, 8f)); }
    public uint NoiseSeed { get => _noiseSeed; set => Set(ref _noiseSeed, value == 0u ? 1u : value); }
    public Vector3 FlowVelocity { get => _flowVelocity; set => Set(ref _flowVelocity, FiniteOrZero(value)); }
    public ulong Revision => _revision;

    /// <summary>A conservative world AABB used by clustering and mutation consumers.</summary>
    public BoundingBox Bounds
    {
        get
        {
            float extent = Shape == VolumetricDensityVolumeShape.Sphere
                ? Radius
                : MathF.Sqrt(BoxExtents.LengthSquared());
            var e = new Vector3(extent);
            return new BoundingBox(Position - e, Position + e);
        }
    }

    private void SetSpatial<T>(ref T field, T value)
    {
        BoundingBox oldBounds = Bounds;
        if (Equals(field, value))
            return;
        field = value;
        Publish(oldBounds);
    }

    private void Set<T>(ref T field, T value)
    {
        if (Equals(field, value))
            return;
        BoundingBox oldBounds = Bounds;
        field = value;
        Publish(oldBounds);
    }

    private void Publish(BoundingBox oldBounds)
    {
        _revision = _revision == ulong.MaxValue ? 1UL : _revision + 1UL;
        Changed?.Invoke(this, oldBounds);
    }

    private static Quaternion Normalize(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W) &&
        value.LengthSquared() > 1e-12f
            ? value.Normalized()
            : Quaternion.Identity;

    private static Vector3 Clamp01(Vector3 value) => new(
        ClampFinite(value.X, 0f, 1f),
        ClampFinite(value.Y, 0f, 1f),
        ClampFinite(value.Z, 0f, 1f));

    private static Vector3 FiniteOrZero(Vector3 value) => new(
        FiniteOr(value.X, 0f),
        FiniteOr(value.Y, 0f),
        FiniteOr(value.Z, 0f));

    private static float ClampFinite(float value, float minimum, float maximum) =>
        System.Math.Clamp(FiniteOr(value, minimum), minimum, maximum);

    private static float FiniteOr(float value, float fallback) =>
        float.IsFinite(value) ? value : fallback;
}
