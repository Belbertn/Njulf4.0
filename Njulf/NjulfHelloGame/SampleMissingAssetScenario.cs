namespace NjulfHelloGame;

internal sealed record SampleMissingAssetScenario(
    string Name,
    string AssetKind,
    string AssetPath,
    bool Required);
