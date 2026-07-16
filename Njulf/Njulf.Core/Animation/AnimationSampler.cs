using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public enum AnimationInterpolation
    {
        Step,
        Linear,
        CubicSpline
    }

    public sealed class AnimationSampler
    {
        public IReadOnlyList<float> InputTimes { get; init; } = Array.Empty<float>();
        public IReadOnlyList<Vector4> OutputValues { get; init; } = Array.Empty<Vector4>();
        public AnimationInterpolation Interpolation { get; init; } = AnimationInterpolation.Linear;
    }
}
