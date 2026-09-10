namespace Njulf.Assets;

public sealed class ContentLoadOptions
{
    /// <summary>Interpretation of standalone image channels; does not affect model import identity.</summary>
    public Njulf.Graphics.TextureColorSpace TextureColorSpace { get; init; } = Njulf.Graphics.TextureColorSpace.Srgb;
    public ImporterOptions ImporterOptions { get; init; } = ImporterOptions.Default;
    public AssetValidationPolicy ImportPolicy { get; init; } = AssetValidationPolicy.GameDefault;
    public ulong HighTextureMemoryBytes { get; init; } = 256UL * 1024UL * 1024UL;

    /// <summary>
    /// Requires a current cooked model package for this request even when the
    /// process-wide development policy permits source import fallback.
    /// </summary>
    public bool RequireCooked { get; init; }

    /// <summary>Observes this request; does not affect cache identity.</summary>
    public IProgress<ContentLoadProgressEvent>? Progress { get; init; }

    public static ContentLoadOptions Default { get; } = new();
}
