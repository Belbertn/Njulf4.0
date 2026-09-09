using Njulf.Core.Math;

namespace Njulf.Core.Scene;

public enum SceneLightType { Point, Directional, Spot, Rectangle, Disk, Tube }
public enum SceneLightAttenuationMode { LegacyWindowed, InverseSquare, Polynomial }

/// <summary>Scene-owned authored light. GPU registrations are borrowed renderer state.</summary>
public sealed class SceneLight : IIdentifiedSceneEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ulong Revision { get; private set; }
    public event Action<SceneLight>? Changed;
    private string _name = "Light";
    public string Name { get => _name; set => Set(ref _name, value); }
    private SceneLightType _type = SceneLightType.Point;
    public SceneLightType Type { get => _type; set => Set(ref _type, value); }
    private Vector3 _position = default;
    public Vector3 Position { get => _position; set => Set(ref _position, value); }
    private Vector3 _direction = new(0, -1, 0);
    public Vector3 Direction { get => _direction; set => Set(ref _direction, value); }
    private Vector3 _up = new(0, 0, 1);
    public Vector3 Up { get => _up; set => Set(ref _up, value); }
    private Vector2 _size = new(1, 1);
    public Vector2 Size { get => _size; set => Set(ref _size, value); }
    private bool _twoSided = false;
    public bool TwoSided { get => _twoSided; set => Set(ref _twoSided, value); }
    private Vector3 _color = new(1, 1, 1);
    public Vector3 Color { get => _color; set => Set(ref _color, value); }
    private float _intensity = 1f;
    public float Intensity { get => _intensity; set => Set(ref _intensity, value); }
    private float _range = 10f;
    public float Range { get => _range; set => Set(ref _range, value); }
    private float _spotAngle = 0.5f;
    public float SpotAngle { get => _spotAngle; set => Set(ref _spotAngle, value); }
    private float _innerSpotAngle = 0f;
    public float InnerSpotAngle { get => _innerSpotAngle; set => Set(ref _innerSpotAngle, value); }
    private SceneLightAttenuationMode _attenuationMode = SceneLightAttenuationMode.LegacyWindowed;
    public SceneLightAttenuationMode AttenuationMode { get => _attenuationMode; set => Set(ref _attenuationMode, value); }
    private float _attenuationConstant = 1f;
    public float AttenuationConstant { get => _attenuationConstant; set => Set(ref _attenuationConstant, value); }
    private float _attenuationLinear = 0f;
    public float AttenuationLinear { get => _attenuationLinear; set => Set(ref _attenuationLinear, value); }
    private float _attenuationQuadratic = 0f;
    public float AttenuationQuadratic { get => _attenuationQuadratic; set => Set(ref _attenuationQuadratic, value); }
    private bool _castsShadows = false;
    public bool CastsShadows { get => _castsShadows; set => Set(ref _castsShadows, value); }
    private float _shadowStrength = 1f;
    public float ShadowStrength { get => _shadowStrength; set => Set(ref _shadowStrength, value); }
    private uint _shadowMapSizeOverride = 0;
    public uint ShadowMapSizeOverride { get => _shadowMapSizeOverride; set => Set(ref _shadowMapSizeOverride, value); }
    private float _shadowNearPlane = 0.1f;
    public float ShadowNearPlane { get => _shadowNearPlane; set => Set(ref _shadowNearPlane, value); }
    private float _shadowFarPlane = 100f;
    public float ShadowFarPlane { get => _shadowFarPlane; set => Set(ref _shadowFarPlane, value); }
    private int _shadowPriority = 0;
    public int ShadowPriority { get => _shadowPriority; set => Set(ref _shadowPriority, value); }
    private SceneAssetReference? _iesProfile = null;
    public SceneAssetReference? IesProfile { get => _iesProfile; set => Set(ref _iesProfile, value); }
    private float _iesRotationRadians = 0f;
    public float IesRotationRadians { get => _iesRotationRadians; set => Set(ref _iesRotationRadians, value); }
    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Revision++;
        Changed?.Invoke(this);
    }
}

