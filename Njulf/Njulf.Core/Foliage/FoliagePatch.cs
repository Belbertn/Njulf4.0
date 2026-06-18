using System;
using Njulf.Core.Math;

namespace Njulf.Core.Foliage;

public sealed class FoliagePatch
{
    private string _name = "FoliagePatch";
    private FoliagePrototype _prototype;
    private BoundingBox _bounds;
    private Vector3 _instancePosition = Vector3.Zero;
    private float _instanceScale = 1f;
    private float _density = 1f;
    private uint _seed = 1;
    private object? _densityTexture;
    private bool _visible = true;
    private uint _revision = 1;

    public FoliagePatch(FoliagePrototype prototype, BoundingBox bounds)
    {
        _prototype = prototype ?? throw new ArgumentNullException(nameof(prototype));
        _bounds = bounds;
    }

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

            _bounds = value;
            IncrementRevision();
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

    public object? DensityTexture
    {
        get => _densityTexture;
        set
        {
            if (Equals(_densityTexture, value))
                return;

            _densityTexture = value;
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
    public uint ContentRevision => CombineRevision(_revision, _prototype.Revision);

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

    private static uint CombineRevision(uint patchRevision, uint prototypeRevision)
    {
        unchecked
        {
            uint hash = 2166136261u;
            hash = (hash ^ patchRevision) * 16777619u;
            hash = (hash ^ prototypeRevision) * 16777619u;
            return hash == 0 ? 1u : hash;
        }
    }

    private void IncrementRevision()
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
    }
}
