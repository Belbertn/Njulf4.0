namespace Njulf.Core.Interfaces
{
    /// <summary>A scene component receiving elapsed seconds from its caller.</summary>
    public interface IUpdateable
    {
        bool Enabled { get; set; }
        int UpdateOrder { get; set; }
        
        /// <summary>Updates using elapsed seconds supplied by the caller.</summary>
        /// <param name="deltaTime">Under the default Game host, scaled elapsed seconds in variable mode or the full fixed interval in fixed mode.</param>
        /// <remarks>Default Game scene updates stop during effective pause (explicit pause, zero TimeScale, or configured focus pause).
        /// Fixed mode advances the scene from FixedUpdate, not also Update; scaling changes fixed-step frequency, not its interval.
        /// This interface carries neither total nor unscaled time. GameTime at the host callbacks provides both.
        /// Direct callers choose their own timing and pause policy. See <see href="../docs/GameTiming.md">the timing guide</see>.</remarks>
        void Update(float deltaTime);
    }
}
