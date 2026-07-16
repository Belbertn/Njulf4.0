namespace Njulf.Rendering.Resources;

public sealed class FoliageDebugFallbackOptions
{
    private int _maxInstancesPerPatch = 512;
    private float _instanceScale = 1f;

    public int MaxInstancesPerPatch
    {
        get => _maxInstancesPerPatch;
        set => _maxInstancesPerPatch = value < 0 ? 0 : value;
    }

    public float InstanceScale
    {
        get => _instanceScale;
        set => _instanceScale = !float.IsFinite(value) || value < 0f ? 0f : value;
    }

    public bool IncludeHiddenPatches { get; set; }
}
