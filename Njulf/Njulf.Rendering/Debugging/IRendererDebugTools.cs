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

    public interface IRendererRuntimeControls : IRendererDebugTools
    {
        RenderSettings Settings { get; }
        bool EnableHiZOcclusion { get; set; }
        bool EnableTransparentPass { get; set; }
        bool EnableMeshletDebugView { get; set; }
        int DebugObjectSnapshotCount { get; }

        string ExportPerformanceSnapshot(string? directory = null);
        bool TryInspectObject(int index, out SelectedObjectInspection inspection);
    }
}
