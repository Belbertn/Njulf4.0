using System;

namespace Njulf.Core.Foliage;

public sealed class FoliagePrototype :
    Njulf.Core.Scene.IIdentifiedSceneEntity,
    IDisposable
{
    private string _name = "FoliagePrototype";
    private object? _mesh;
    private object? _material;
    private FoliageGeometryMode _geometryMode;
    private float _cardHeight = 1.0f;
    private float _cardWidth = 0.08f;
    private bool _farImpostorEnabled;
    private FoliageImpostorAsset? _impostor;
    private bool _castShadows = true;
    private bool _twoSided = true;
    private uint _revision = 1;
    private Njulf.Core.Scene.RenderObject? _resourceOwner;

    public FoliagePrototype()
    {
        Lod.Changed += IncrementRevision;
        Wind.Changed += IncrementRevision;
        Lighting.Changed += IncrementRevision;
    }

    public event Action<FoliagePrototype>? Changed;

    public Guid Id { get; set; } = Guid.NewGuid();

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

    /// <summary>Source model identity used by scene serialization.</summary>
    public Njulf.Core.Scene.SceneAssetReference? AssetReference { get; set; }

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

    public bool FarImpostorEnabled
    {
        get => _farImpostorEnabled;
        set
        {
            if (_farImpostorEnabled == value)
                return;

            _farImpostorEnabled = value;
            IncrementRevision();
        }
    }

    public FoliageImpostorAsset? Impostor
    {
        get => _impostor;
        set
        {
            if (ReferenceEquals(_impostor, value))
                return;
            _impostor = value;
            IncrementRevision();
        }
    }

    public bool CastShadows
    {
        get => _castShadows;
        set
        {
            if (_castShadows == value)
                return;
            _castShadows = value;
            IncrementRevision();
        }
    }

    public bool TwoSided
    {
        get => _twoSided;
        set
        {
            if (_twoSided == value)
                return;
            _twoSided = value;
            IncrementRevision();
        }
    }

    public FoliageLodSettings Lod { get; } = new();
    public FoliageWindSettings Wind { get; } = new();
    public FoliageLightingSettings Lighting { get; } = new();
    public uint Revision => _revision;

    /// <summary>
    /// Transfers a render object's mesh/material leases to this prototype.
    /// The source remains the retryable lifetime owner until disposal.
    /// </summary>
    public void AdoptResourceOwner(
        Njulf.Core.Scene.RenderObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_resourceOwner != null)
        {
            throw new InvalidOperationException(
                "Foliage resource ownership is already attached.");
        }
        if (!Equals(Mesh, source.Mesh) ||
            !Equals(Material, source.Material))
        {
            throw new InvalidOperationException(
                "Foliage handles must match the transferred resource owner.");
        }

        _resourceOwner = source;
    }

    public void Dispose()
    {
        if (_resourceOwner == null)
            return;

        _resourceOwner.Dispose();
        _resourceOwner = null;
        _mesh = null;
        _material = null;
        IncrementRevision();
    }

    public void MarkSettingsChanged()
    {
        IncrementRevision();
    }

    public void Validate()
    {
        if (!Enum.IsDefined(GeometryMode))
            throw new InvalidOperationException("Unsupported foliage geometry mode.");
        if (!float.IsFinite(CardHeight) || CardHeight <= 0f ||
            !float.IsFinite(CardWidth) || CardWidth <= 0f)
            throw new InvalidOperationException(
                "Foliage blade/card dimensions must be finite and positive.");
        if (Lod.Lod0Distance > Lod.Lod1Distance ||
            Lod.Lod1Distance > Lod.Lod2Distance)
            throw new InvalidOperationException(
                "Foliage LOD distances must be monotonic.");
        if (FarImpostorEnabled &&
            GeometryMode == FoliageGeometryMode.AuthoredMeshlets &&
            (Impostor is null || !Impostor.IsComplete))
            throw new InvalidOperationException(
                "An enabled authored foliage impostor requires complete offline atlas metadata.");
    }

    private void IncrementRevision()
    {
        _revision++;
        if (_revision == 0)
            _revision = 1;
        Changed?.Invoke(this);
    }

    private static float ClampPositive(float value, float fallback)
    {
        if (!float.IsFinite(value) || value <= 0f)
            return fallback;
        return value;
    }
}
