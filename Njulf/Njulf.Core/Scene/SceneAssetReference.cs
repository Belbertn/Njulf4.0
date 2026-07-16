using System;

namespace Njulf.Core.Scene;

/// <summary>Stable source/cooked asset identity used by authorable scene documents.</summary>
public sealed record SceneAssetReference
{
    public required string Path { get; init; }
    public string SubObject { get; init; } = "*";
    public string? ContentHash { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path))
            throw new InvalidOperationException("A scene asset reference requires a non-empty path.");
        if (string.IsNullOrWhiteSpace(SubObject))
            throw new InvalidOperationException("A scene asset reference requires a sub-object selector.");
    }
}
