namespace Njulf.Core.Foliage;

public sealed class FoliageLightingSettings
{
    private float _wrapDiffuse = 0.35f;
    private float _backlight = 0.25f;
    private float _normalBend = 0.5f;

    public event Action? Changed;

    public float WrapDiffuse
    {
        get => _wrapDiffuse;
        set => Set(ref _wrapDiffuse, Clamp01(value));
    }

    public float Backlight
    {
        get => _backlight;
        set => Set(ref _backlight, Clamp01(value));
    }

    public float NormalBend
    {
        get => _normalBend;
        set => Set(ref _normalBend, Clamp01(value));
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
}
