using System;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    /// <summary>Mutable joint-local transforms and derived matrices for one skeleton pose.</summary>
    public sealed class AnimationPose
    {
        private readonly AnimationTransform[] _localTransforms;
        private readonly Matrix4x4[] _localMatrices;
        private readonly Matrix4x4[] _globalMatrices;
        private readonly byte[] _buildState;

        /// <summary>Allocates zero-initialized storage for the specified joint count. Call ResetToBindPose before use.</summary>
        public AnimationPose(int jointCount)
        {
            if (jointCount < 0)
                throw new ArgumentOutOfRangeException(nameof(jointCount));

            _localTransforms = new AnimationTransform[jointCount];
            _localMatrices = new Matrix4x4[jointCount];
            _globalMatrices = new Matrix4x4[jointCount];
            _buildState = new byte[jointCount];
        }

        /// <summary>Borrows joint-local transforms indexed by skeleton joint.</summary>
        public ReadOnlySpan<AnimationTransform> LocalTransforms => _localTransforms;
        /// <summary>Borrows local matrices; contents change when transforms are edited.</summary>
        public ReadOnlySpan<Matrix4x4> LocalMatrices => _localMatrices;
        /// <summary>Borrows hierarchy-composed matrices, refreshed by BuildGlobalMatrices.</summary>
        public ReadOnlySpan<Matrix4x4> GlobalMatrices => _globalMatrices;

        /// <summary>Copies the skeleton's bind transforms and rebuilds global matrices.</summary>
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
            }
            BuildGlobalMatrices(skeleton);
        }

        /// <summary>Changes a local transform and its matrix. Call BuildGlobalMatrices after a batch of standalone edits.</summary>
        /// <remarks>For an animator-owned pose use Animator.EditPose so skin matrices and rendering revisions also update.</remarks>
        public void SetLocalTransform(int jointIndex, AnimationTransform transform)
        {
            if ((uint)jointIndex >= (uint)_localTransforms.Length) throw new ArgumentOutOfRangeException(nameof(jointIndex));
            _localTransforms[jointIndex] = transform;
            _localMatrices[jointIndex] = transform.ToMatrix();
        }

        public AnimationTransform GetLocalTransform(int jointIndex)
        {
            return _localTransforms[jointIndex];
        }

        /// <summary>Rebuilds globals from local matrices, including parents stored after their children.</summary>
        public void BuildGlobalMatrices(Skeleton skeleton)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            if (skeleton.Joints.Count != _localTransforms.Length)
                throw new ArgumentException("Skeleton joint count does not match this pose.", nameof(skeleton));
            Array.Clear(_buildState, 0, _buildState.Length);
            for (int i = 0; i < _globalMatrices.Length; i++)
                BuildJoint(i, skeleton);
        }

        private void BuildJoint(int index, Skeleton skeleton)
        {
            if (_buildState[index] == 2) return;
            if (_buildState[index] == 1) throw new ArgumentException("Skeleton hierarchy contains a cycle.", nameof(skeleton));
            _buildState[index] = 1;
            int parent = skeleton.Joints[index].ParentIndex;
            if (parent < -1 || parent >= _globalMatrices.Length)
                throw new ArgumentException("Skeleton parent index is out of range.", nameof(skeleton));
            if (parent >= 0)
            {
                BuildJoint(parent, skeleton);
            }
            _globalMatrices[index] = parent >= 0 ? _localMatrices[index] * _globalMatrices[parent] : _localMatrices[index];
            _buildState[index] = 2;
        }
    }
}
