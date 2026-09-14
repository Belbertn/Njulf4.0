namespace Njulf.Rendering.Data;

/// <summary>Bounded reconstruction of transparent indirect optical lighting.</summary>
public sealed class OpticalDenoisingSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Export and validate layers without changing the rendered image.</summary>
    public bool BypassFilter { get; set; }
    /// <summary>Two layers at half width and height; deeper layers use a spatial approximation.</summary>
    public bool CompactLayers { get; set; }
    private int _layerLimit = 4;
    public int LayerLimit { get => _layerLimit; set => _layerLimit = System.Math.Clamp(value, 1, 4); }
    private int _memoryBudgetMiB = 512;
    public int MemoryBudgetMiB { get => _memoryBudgetMiB; set => _memoryBudgetMiB = System.Math.Clamp(value, 16, 2048); }
}
