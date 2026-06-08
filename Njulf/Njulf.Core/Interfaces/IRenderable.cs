using Njulf.Core.Math;

namespace Njulf.Core.Interfaces
{
    public interface IRenderable
    {
        Matrix4x4 WorldMatrix { get; set; }
        bool Visible { get; set; }
        
        void Draw();
    }
}
