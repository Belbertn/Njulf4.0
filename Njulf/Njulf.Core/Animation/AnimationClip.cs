using System;
using System.Collections.Generic;

namespace Njulf.Core.Animation
{
    public sealed class AnimationClip
    {
        public string Name { get; init; } = string.Empty;
        public float DurationSeconds { get; init; }
        public IReadOnlyList<AnimationChannel> Channels { get; init; } = Array.Empty<AnimationChannel>();
    }
}
