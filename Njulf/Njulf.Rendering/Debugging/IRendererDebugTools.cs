using Njulf.Rendering.Data;

namespace Njulf.Rendering.Debug
{
    public interface IRendererDebugTools
    {
        DebugDrawList DebugDraw { get; }
        DebugOverlaySettings DebugOverlays { get; }
        SelectedObjectInspection? SelectedObject { get; set; }
        RendererDiagnostics LastDiagnostics { get; }

        void RequestScreenshot(string? outputPath = null);
        void RequestRenderDocCapture();
    }
}
