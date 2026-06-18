namespace Njulf.Core.Foliage;

public sealed class FoliageLodSettings
{
    private float _lod0Distance = 20f;
    private float _lod1Distance = 60f;
    private float _lod2Distance = 140f;

    public float Lod0Distance
    {
        get => _lod0Distance;
        set => _lod0Distance = ClampDistance(value);
    }

    public float Lod1Distance
    {
        get => _lod1Distance;
        set => _lod1Distance = ClampDistance(value);
    }

    public float Lod2Distance
    {
        get => _lod2Distance;
        set => _lod2Distance = ClampDistance(value);
    }

    private static float ClampDistance(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return value < 0f ? 0f : value;
    }
}
