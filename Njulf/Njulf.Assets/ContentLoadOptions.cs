namespace Njulf.Assets;

public sealed class ContentLoadOptions
{
    public ImporterOptions ImporterOptions { get; init; } = ImporterOptions.Default;
    public AssetValidationPolicy ImportPolicy { get; init; } = AssetValidationPolicy.GameDefault;
    public ulong HighTextureMemoryBytes { get; init; } = 256UL * 1024UL * 1024UL;

    public static ContentLoadOptions Default { get; } = new();
}
