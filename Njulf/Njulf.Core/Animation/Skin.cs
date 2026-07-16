using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public sealed class Skin
    {
        public string Name { get; init; } = string.Empty;
        public Skeleton Skeleton { get; init; } = new();
        public IReadOnlyList<int> JointIndices { get; init; } = Array.Empty<int>();
        public IReadOnlyList<Matrix4x4> InverseBindMatrices { get; init; } = Array.Empty<Matrix4x4>();
    }
}
