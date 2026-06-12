using System;
using System.Collections.Generic;

namespace Njulf.Core.Animation
{
    public sealed class Skeleton
    {
        public string Name { get; init; } = string.Empty;
        public IReadOnlyList<SkeletonJoint> Joints { get; init; } = Array.Empty<SkeletonJoint>();
        public int RootJointIndex { get; init; } = -1;
    }
}
