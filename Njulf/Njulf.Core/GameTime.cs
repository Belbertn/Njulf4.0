namespace Njulf.Core;

/// <summary>Scaled game time and unscaled wall time, excluding initial content loading.</summary>
/// <param name="TotalGameTime">Scaled time; in FixedUpdate, the sum of executed simulation steps.</param>
/// <param name="ElapsedGameTime">Scaled callback interval, or the fixed simulation step.</param>
/// <remarks>Update and Draw have independent elapsed intervals, initially zero. FixedUpdate always receives a full step.</remarks>
public readonly record struct GameTime(TimeSpan TotalGameTime, TimeSpan ElapsedGameTime)
{
    /// <summary>Wall time since LoadAsync completed, including pauses and discarded catch-up time.</summary>
    public TimeSpan UnscaledTotalGameTime { get; init; } = TotalGameTime;
    /// <summary>Wall interval since this callback stream's previous invocation; initially zero. Batched fixed callbacks share a timestamp.</summary>
    public TimeSpan UnscaledElapsedGameTime { get; init; } = ElapsedGameTime;
}
