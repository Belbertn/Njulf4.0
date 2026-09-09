using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

public sealed record GraphicsSettingsResult(GraphicsSettingsOutcome Outcome,
    GraphicsSettingsSnapshot Requested, IReadOnlyList<GraphicsSettingsFieldResult> Fields, string? Reason = null);
