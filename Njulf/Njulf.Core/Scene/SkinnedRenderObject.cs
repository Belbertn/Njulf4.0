using Njulf.Core.Animation;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public sealed class SkinnedRenderObject : RenderObject
    {
        private int _skinIndex = -1;
        private Animator? _animator;
        private Matrix4x4 _skinningBindTransform = Matrix4x4.Identity;
        private BoundingBox? _animatedBoundingBox;
        private uint _skinnedVertexOffset;
        private bool _skinningEnabled;

        public SkinnedRenderObject()
        {
        }

        public SkinnedRenderObject(object mesh, object material)
            : base(mesh, material)
        {
        }

        public int SkinIndex
        {
            get => _skinIndex;
            set
            {
                if (_skinIndex == value)
                    return;
                _skinIndex = value;
                PublishDerivedChange(SceneMutationKind.Animation | SceneMutationKind.Geometry);
            }
        }

        public Animator? Animator
        {
            get => _animator;
            set
            {
                if (ReferenceEquals(_animator, value))
                    return;
                _animator = value;
                PublishDerivedChange(SceneMutationKind.Animation);
            }
        }

        public Matrix4x4 SkinningBindTransform
        {
            get => _skinningBindTransform;
            set
            {
                if (_skinningBindTransform.Equals(value))
                    return;
                _skinningBindTransform = value;
                PublishDerivedChange(SceneMutationKind.Animation | SceneMutationKind.Geometry);
            }
        }

        public BoundingBox? AnimatedBoundingBox
        {
            get => _animatedBoundingBox;
            set
            {
                if (_animatedBoundingBox.Equals(value))
                    return;
                _animatedBoundingBox = value;
                PublishDerivedChange(SceneMutationKind.Animation | SceneMutationKind.Geometry);
            }
        }

        public uint SkinnedVertexOffset
        {
            get => _skinnedVertexOffset;
            set
            {
                if (_skinnedVertexOffset == value)
                    return;
                _skinnedVertexOffset = value;
                PublishDerivedChange(SceneMutationKind.Animation | SceneMutationKind.Geometry);
            }
        }

        public bool SkinningEnabled
        {
            get => _skinningEnabled;
            set
            {
                if (_skinningEnabled == value)
                    return;
                _skinningEnabled = value;
                PublishDerivedChange(SceneMutationKind.Animation | SceneMutationKind.Geometry);
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            Animator?.Update(deltaTime);
        }
    }
}
