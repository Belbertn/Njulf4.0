namespace Njulf.Core.Foliage;

public sealed class FoliageWindSettings
{
    private float _strength = 0.35f;
    private float _frequency = 0.7f;
    private float _flutter = 0.15f;

    public event Action? Changed;

    public float Strength
    {
        get => _strength;
        set => Set(ref _strength, Clamp01(value));
    }

    public float Frequency
    {
        get => _frequency;
        set => Set(ref _frequency, ClampNonNegative(value));
    }

    public float Flutter
    {
        get => _flutter;
        set => Set(ref _flutter, Clamp01(value));
    }

    private void Set(ref float field, float value)
    {
        if (field == value)
            return;
        field = value;
        Changed?.Invoke();
    }

    private static float Clamp01(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        if (value < 0f)
            return 0f;
        return value > 1f ? 1f : value;
    }

    private static float ClampNonNegative(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        return value < 0f ? 0f : value;
    }
}
