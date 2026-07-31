using Hexa.NET.ImGui;
using Njulf.Rendering.Data;

namespace Njulf.Editor;

/// <summary>
/// Direct selection for interactive material diagnostics. Capture-only material
/// modes remain owned by the automated conformance capture path.
/// </summary>
internal sealed class MaterialEditorPanel
{
    private static readonly MaterialDebugView[] InteractiveViews = Enum
        .GetValues<MaterialDebugView>()
        .Where(static view => !MaterialDebugViewPolicy.IsLinearDirectCapture(view))
        .ToArray();

    internal static IReadOnlyList<MaterialDebugView> InteractiveDebugViews => InteractiveViews;

    public void Render(EditorController editor)
    {
        ImGui.Begin("Materials");

        RenderSettings? renderSettings = editor.RendererSettings;
        if (renderSettings == null)
        {
            ImGui.TextWrapped("Live material settings are unavailable because this editor is not attached to a Vulkan renderer.");
            ImGui.End();
            return;
        }

        MaterialSettings materials = renderSettings.Materials;
        ImGui.SeparatorText("Material debug view");
        ImGui.TextDisabled("Select a view directly; changes apply immediately.");

        MaterialDebugView current = materials.DebugView;
        if (ImGui.BeginCombo("View##MaterialDebugView", current.ToString()))
        {
            foreach (MaterialDebugView candidate in InteractiveViews)
            {
                if (ImGui.Selectable(candidate.ToString(), candidate == current))
                    materials.DebugView = candidate;
            }

            ImGui.EndCombo();
        }

        if (ImGui.Button("Clear material debug view"))
            materials.DebugView = MaterialDebugView.None;

        ImGui.SeparatorText("GI receiver inspection");
        if (ImGui.Button("Material occlusion"))
            materials.DebugView = MaterialDebugView.MaterialOcclusion;
        ImGui.SameLine();
        if (ImGui.Button("Canonical diffuse reflectance"))
            materials.DebugView = MaterialDebugView.CanonicalDiffuseReflectance;

        RenderViewDescription(materials.DebugView);

        if (materials.DebugView != MaterialDebugView.None &&
            renderSettings.Animation.DebugView != AnimationDebugView.None)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.72f, 0.2f, 1f),
                $"Animation debug view {renderSettings.Animation.DebugView} has higher render priority.");
        }
        else if (materials.DebugView != MaterialDebugView.None &&
                 renderSettings.GlobalIllumination.DebugView != GlobalIlluminationDebugView.None)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.72f, 0.2f, 1f),
                $"Material debug is overriding GI debug view {renderSettings.GlobalIllumination.DebugView}.");
        }

        ImGui.End();
    }

    private static void RenderViewDescription(MaterialDebugView view)
    {
        string description = view switch
        {
            MaterialDebugView.MaterialOcclusion =>
                "Material occlusion: white is unoccluded receiver energy; black is fully occluded.",
            MaterialDebugView.CanonicalDiffuseReflectance =>
                "Canonical diffuse reflectance: the linear receiver color multiplied into indirect irradiance.",
            MaterialDebugView.None =>
                "Normal rendering is active.",
            _ =>
                $"Showing {view} ({(uint)view})."
        };
        ImGui.TextWrapped(description);
    }
}
