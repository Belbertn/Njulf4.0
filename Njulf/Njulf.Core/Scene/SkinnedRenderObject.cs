using Njulf.Core.Animation;
using Njulf.Core.Math;

namespace Njulf.Core.Scene
{
    public sealed class SkinnedRenderObject : RenderObject
    {
        public SkinnedRenderObject()
        {
        }

        public SkinnedRenderObject(object mesh, object material)
            : base(mesh, material)
        {
        }

        public int SkinIndex { get; set; } = -1;
        public Animator? Animator { get; set; }
        public BoundingBox? AnimatedBoundingBox { get; set; }
        public uint SkinnedVertexOffset { get; set; }
        public bool SkinningEnabled { get; set; }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            Animator?.Update(deltaTime);
        }
    }
}
