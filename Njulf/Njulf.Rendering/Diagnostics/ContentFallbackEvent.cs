using System;

namespace Njulf.Rendering.Diagnostics;

public sealed record ContentFallbackEvent(
    DateTimeOffset TimestampUtc,
    string AssetKind,
    string AssetPath,
    string? MaterialName,
    string? ObjectName,
    ContentFallbackPolicy Policy,
    string Reason);
