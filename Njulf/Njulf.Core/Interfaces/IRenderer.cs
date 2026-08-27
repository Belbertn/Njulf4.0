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

    /// <summary>
    /// Optional renderer capability for compiling the pipelines required by a
    /// fully loaded scene before its first frame is recorded.
    /// </summary>
    public interface IScenePipelinePreparer
    {
        void PrepareScene(Scene.Scene scene, ICamera camera);
    }

    /// <summary>
    /// Optional renderer capability for classifying and enforcing the
    /// application time from <see cref="Game.Run"/> to the first completed
    /// presentation.
    /// </summary>
    public interface IStartupLatencyReporter
    {
        void ReportFirstPresent(long elapsedMicroseconds);
    }
}
