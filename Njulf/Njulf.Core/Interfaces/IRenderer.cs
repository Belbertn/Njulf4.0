using Njulf.Core.Math;

namespace Njulf.Core.Interfaces
{
    public interface IRenderer
    {
        void Initialize();
        bool BeginFrame();
        void EndFrame();
        void Clear(Color color);
        void DrawScene(Scene.Scene scene, ICamera camera);
        void Resize(int width, int height);
        void Dispose();
    }
}
