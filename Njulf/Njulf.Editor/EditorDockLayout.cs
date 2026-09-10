using Hexa.NET.ImGui;

namespace Njulf.Editor;

/// <summary>Initial editor layout; subsequent frames preserve ImGui's saved docking state.</summary>
internal sealed class EditorDockLayout
{
    internal const string SceneManagementWindow = "Scene Management";
    internal const string HierarchyWindow = "Hierarchy";
    internal const string SceneLightsWindow = "Scene Lights";
    internal const string InspectorWindow = "Inspector";
    internal const string RenderingSettingsWindow = "Rendering Settings";
    internal const string GlobalIlluminationWindow = "Global Illumination";
    internal const string MaterialsWindow = "Materials";
    internal const string ShadowsWindow = "Shadows";

    private bool _resetRequested;
    private bool _focusDefaultTab;

    internal void RequestReset() => _resetRequested = true;

    internal void Render()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("Layout"))
            {
                if (ImGui.MenuItem("Reset layout"))
                    RequestReset();
                ImGui.EndMenu();
            }

            ImGui.TextDisabled("Drag tabs to rearrange; drag dividers to resize.");
            ImGui.EndMainMenuBar();
        }

        // Use a context-independent ID so ini settings survive editor/context recreation.
        uint dockspaceId = ImGuiP.ImHashStr("NjulfEditorDockSpace");
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        if (_resetRequested || ImGuiP.DockBuilderGetNode(dockspaceId).IsNull)
        {
            BuildDefaultLayout(dockspaceId, viewport);
            _resetRequested = false;
            _focusDefaultTab = true;
        }

        // The renderer draws behind the overlay. Leave the center visible and allow
        // mouse input through it for scene picking and camera controls.
        ImGui.DockSpaceOverViewport(dockspaceId, viewport,
            ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.NoDockingOverCentralNode);

        // On first use the windows do not exist until the shell submits them.
        // Focus once they exist, after ImGui has created the dock's tab bar.
        if (_focusDefaultTab && !ImGuiP.FindWindowByName(RenderingSettingsWindow).IsNull)
        {
            ImGui.SetWindowFocus(RenderingSettingsWindow);
            _focusDefaultTab = false;
        }
    }

    private static unsafe void BuildDefaultLayout(uint dockspaceId, ImGuiViewportPtr viewport)
    {
        ImGuiP.DockBuilderRemoveNode(dockspaceId);
        ImGuiP.DockBuilderAddNode(dockspaceId, (ImGuiDockNodeFlags)ImGuiDockNodeFlagsPrivate.Space);
        ImGuiP.DockBuilderSetNodePos(dockspaceId, viewport.WorkPos);
        ImGuiP.DockBuilderSetNodeSize(dockspaceId, viewport.WorkSize);

        uint center = dockspaceId;
        uint bottom, left, right, hierarchy, lights;
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Down, 0.26f, &bottom, &center);
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Left, 0.22f, &left, &center);
        // Split ratios apply to the remaining area; reserve 30% of the full width on the right.
        ImGuiP.DockBuilderSplitNode(center, ImGuiDir.Right, 0.30f / (1f - 0.22f), &right, &center);
        ImGuiP.DockBuilderSplitNode(left, ImGuiDir.Up, 0.55f, &hierarchy, &lights);

        ImGuiP.DockBuilderDockWindow(SceneManagementWindow, bottom);
        ImGuiP.DockBuilderDockWindow(HierarchyWindow, hierarchy);
        ImGuiP.DockBuilderDockWindow(SceneLightsWindow, lights);
        ImGuiP.DockBuilderDockWindow(RenderingSettingsWindow, right);
        ImGuiP.DockBuilderDockWindow(InspectorWindow, right);
        ImGuiP.DockBuilderDockWindow(GlobalIlluminationWindow, right);
        ImGuiP.DockBuilderDockWindow(MaterialsWindow, right);
        ImGuiP.DockBuilderDockWindow(ShadowsWindow, right);
        ImGuiP.DockBuilderFinish(dockspaceId);
    }
}
