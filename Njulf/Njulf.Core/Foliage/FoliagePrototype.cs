using System;

namespace Njulf.Core.Foliage;

public sealed class FoliagePrototype
{
    private string _name = "FoliagePrototype";
    private object? _mesh;
    private object? _material;
    private FoliageGeometryMode _geometryMode;
    private uint _authoredMeshletStride = 1;
    private float _cardHeight = 1.0f;
    private float _cardWidth = 0.08f;
    private uint _revision = 1;

    public string Name
    {
        get => _name;
        set
        {
            string next = string.IsNullOrWhiteSpace(value) ? "FoliagePrototype" : value;
            if (_name == next)
                return;

            _name = next;
            IncrementRevision();
        }
    }

    public object? Mesh
    {
        get => _mesh;
        set
        {
            if (Equals(_mesh, value))
                return;

            _mesh = value;
            IncrementRevision();
        }
    }

    public object? Material
    {
        get => _material;
        set
        {
            if (Equals(_material, value))
                return;

            _material = value;
            IncrementRevision();
        }
    }

    public FoliageGeometryMode GeometryMode
    {
        get => _geometryMode;
        set
        {
            if (_geometryMode == value)
                return;

            _geometryMode = value;
            IncrementRevision();
        }
    }

    public uint AuthoredMeshletStride
    {
        get => _authoredMeshletStride;
        set
        {
            uint next = System.Math.Clamp(value, 1u, 64u);
            if (_authoredMeshletStride == next)
                return;

            _authoredMeshletStride = next;
            IncrementRevision();
        }
    }

    public float CardHeight
    {
        get => _cardHeight;
        set
        {
            float next = ClampPositive(value, 1.0f);
            if (_cardHeight == next)
                return;

            _cardHeight = next;
            IncrementRevision();
        }
    }

    public float CardWidth
    {
        get => _cardWidth;
        set
        {
            float next = ClampPositive(value, 0.08f);
            if (_cardWidth == next)
                return;

            _cardWidth = next;
            IncrementRevision();
        }
    }

    public FoliageLodSettings Lod { get; } = new();
    public FoliageWindSettings Wind { get; } = new();
    public FoliageLightingSettings Lighting { get; } = new();
    public uint Revision => _revision;

    public void MarkSettingsChanged()
    {
        IncrementRevision();
    }

    private void IncrementRevision()
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
    }

    private static float ClampPositive(float value, float fallback)
    {
        if (!float.IsFinite(value) || value <= 0f)
            return fallback;
        return value;
    }
}
