using System;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public sealed class AnimationPose
    {
        private readonly AnimationTransform[] _localTransforms;
        private readonly Matrix4x4[] _localMatrices;
        private readonly Matrix4x4[] _globalMatrices;

        public AnimationPose(int jointCount)
        {
            if (jointCount < 0)
                throw new ArgumentOutOfRangeException(nameof(jointCount));

            _localTransforms = new AnimationTransform[jointCount];
            _localMatrices = new Matrix4x4[jointCount];
            _globalMatrices = new Matrix4x4[jointCount];
        }

        public ReadOnlySpan<AnimationTransform> LocalTransforms => _localTransforms;
        public ReadOnlySpan<Matrix4x4> LocalMatrices => _localMatrices;
        public ReadOnlySpan<Matrix4x4> GlobalMatrices => _globalMatrices;

        public void ResetToBindPose(Skeleton skeleton)
        {
            if (skeleton == null)
                throw new ArgumentNullException(nameof(skeleton));
            if (skeleton.Joints.Count != _localTransforms.Length)
                throw new ArgumentException("Skeleton joint count does not match this pose.", nameof(skeleton));

            for (int i = 0; i < _localTransforms.Length; i++)
            {
                _localTransforms[i] = skeleton.Joints[i].LocalBindPose;
                _localMatrices[i] = skeleton.Joints[i].LocalBindTransform;
                _globalMatrices[i] = Matrix4x4.Identity;
            }
        }

        internal void SetLocalTransform(int jointIndex, AnimationTransform transform)
        {
            _localTransforms[jointIndex] = transform;
            _localMatrices[jointIndex] = transform.ToMatrix();
        }

        internal AnimationTransform GetLocalTransform(int jointIndex)
        {
            return _localTransforms[jointIndex];
        }

        internal void BuildGlobalMatrices(Skeleton skeleton)
        {
            for (int i = 0; i < _globalMatrices.Length; i++)
            {
                int parent = skeleton.Joints[i].ParentIndex;
                _globalMatrices[i] = parent >= 0
                    ? _localMatrices[i] * _globalMatrices[parent]
                    : _localMatrices[i];
            }
        }
    }
}
