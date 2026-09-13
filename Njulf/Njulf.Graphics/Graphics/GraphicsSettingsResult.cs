using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Completion receipt for a frame-boundary settings request; inspect Outcome and per-field reasons.</summary>
/// <param name="Outcome">Whether values were unchanged, applied, rebuilt, deferred to restart, rejected or failed.</param>
/// <param name="Requested">Requested settings snapshot; actual execution is reported by graphics capabilities.</param>
/// <param name="Fields">Classification and explanation for each evaluated field.</param>
/// <param name="Reason">Overall explanation when supplied by the controller.</param>
public sealed record GraphicsSettingsResult(GraphicsSettingsOutcome Outcome,
    GraphicsSettingsSnapshot Requested, IReadOnlyList<GraphicsSettingsFieldResult> Fields, string? Reason = null);
