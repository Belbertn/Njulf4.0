namespace Njulf.Core.Animation
{
    public enum AnimationChannelPath
    {
        Translation,
        Rotation,
        Scale
    }

    public sealed class AnimationChannel
    {
        public int TargetNodeIndex { get; init; } = -1;
        public int TargetJointIndex { get; init; } = -1;
        public AnimationChannelPath Path { get; init; }
        public AnimationSampler Sampler { get; init; } = new();
    }
}
