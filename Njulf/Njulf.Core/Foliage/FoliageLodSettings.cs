namespace Njulf.Core.Foliage;

public sealed class FoliageLodSettings
{
    private float _lod0Distance = 20f;
    private float _lod1Distance = 60f;
    private float _lod2Distance = 140f;

    public event Action? Changed;

    public float Lod0Distance
    {
        get => _lod0Distance;
        set
        {
            float next = System.Math.Min(ClampDistance(value), _lod1Distance);
            Set(ref _lod0Distance, next);
        }
    }

    public float Lod1Distance
    {
        get => _lod1Distance;
        set
        {
            float next = System.Math.Clamp(
                ClampDistance(value),
                _lod0Distance,
                _lod2Distance);
            Set(ref _lod1Distance, next);
        }
    }

    public float Lod2Distance
    {
        get => _lod2Distance;
        set
        {
            float next = System.Math.Max(ClampDistance(value), _lod1Distance);
            Set(ref _lod2Distance, next);
        }
    }

    public void SetDistances(float lod0, float lod1, float lod2)
    {
        lod0 = ClampDistance(lod0);
        lod1 = System.Math.Max(lod0, ClampDistance(lod1));
        lod2 = System.Math.Max(lod1, ClampDistance(lod2));
        if (_lod0Distance == lod0 && _lod1Distance == lod1 &&
            _lod2Distance == lod2)
            return;
        _lod0Distance = lod0;
        _lod1Distance = lod1;
        _lod2Distance = lod2;
        Changed?.Invoke();
    }

    private void Set(ref float field, float value)
    {
        if (field == value)
            return;
        field = value;
        Changed?.Invoke();
    }

    private static float ClampDistance(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return value < 0f ? 0f : value;
    }
}
