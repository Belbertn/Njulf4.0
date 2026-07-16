namespace NjulfHelloGame;

public sealed record SampleMissingAssetScenario(
    string Name,
    string AssetKind,
    string AssetPath,
    bool Required);
