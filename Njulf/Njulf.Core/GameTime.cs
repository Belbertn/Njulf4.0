namespace Njulf.Core;

/// <summary>Monotonic game time. Elapsed time belongs to the current update or draw callback stream.</summary>
/// <param name="TotalGameTime">Wall time since LoadAsync completed; startup is excluded.</param>
/// <param name="ElapsedGameTime">Interval since this stream's previous callback; zero on its first callback.</param>
/// <remarks>Timing is variable-step. Update and Draw have independent elapsed intervals and share the same origin.</remarks>
public readonly record struct GameTime(TimeSpan TotalGameTime, TimeSpan ElapsedGameTime);
