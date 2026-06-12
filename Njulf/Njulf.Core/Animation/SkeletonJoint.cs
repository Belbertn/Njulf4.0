using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public readonly struct SkeletonJoint
    {
        public string Name { get; init; }
        public int ParentIndex { get; init; }
        public AnimationTransform LocalBindPose { get; init; }
        public Matrix4x4 LocalBindTransform { get; init; }
        public Matrix4x4 InverseBindMatrix { get; init; }
    }
}
