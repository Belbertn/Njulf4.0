using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics;

public sealed class ContentFallbackDiagnostics
{
    private readonly List<ContentFallbackEvent> _events = new();

    public IReadOnlyList<ContentFallbackEvent> Events => _events;

    public void Record(
        string assetKind,
        string assetPath,
        string? materialName,
        string? objectName,
        ContentFallbackPolicy policy,
        string reason)
    {
        _events.Add(new ContentFallbackEvent(
            DateTimeOffset.UtcNow,
            assetKind,
            System.IO.Path.GetFullPath(assetPath),
            materialName,
            objectName,
            policy,
            reason));
    }
}
