namespace Njulf.Editor;

/// <summary>
/// Rendering/input integration point for an immediate-mode overlay. The engine remains editor-free
/// unless a host implementing this interface is registered by an application.
/// </summary>
public interface IEditorOverlayHost
{
    bool WantCaptureMouse { get; }
    bool WantCaptureKeyboard { get; }
    void SetEnabled(bool enabled);
}
