using System;
using System.Collections.Generic;

namespace Njulf.Core.Animation
{
    /// <summary>Reusable animation data; mutable playback state belongs to Animator.</summary>
    public sealed class AnimationClip
    {
        /// <summary>Authored clip name; empty when unnamed.</summary>
        public string Name { get; init; } = string.Empty;
        /// <summary>Clip duration in seconds.</summary>
        public float DurationSeconds { get; init; }
        /// <summary>Joint-property channels sampled during playback.</summary>
        public IReadOnlyList<AnimationChannel> Channels { get; init; } = Array.Empty<AnimationChannel>();
    }
}
