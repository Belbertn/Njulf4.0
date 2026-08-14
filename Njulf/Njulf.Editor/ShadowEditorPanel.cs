using System.Reflection;
using System.Text;
using Hexa.NET.ImGui;
using Njulf.Rendering.Data;

namespace Njulf.Editor;

/// <summary>
/// Live shadow configuration and diagnostics. The panel deliberately keeps the GPU scene-
/// submission switches beside the shadow settings because they select the directional static-
/// cache draw path under investigation.
/// </summary>
internal sealed unsafe class ShadowEditorPanel
{
    private static readonly string[] ShadowGroupOrder =
    [
        "Directional",
        "Static cache / debug",
        "Spot",
        "Point"
    ];

    private static readonly SettingEditor[] ShadowEditors = BuildEditors<ShadowSettings>(ResolveShadowGroup);
    private static readonly SettingEditor[] SceneSubmissionEditors =
        BuildEditors<SceneSubmissionSettings>(static _ => "GPU scene submission");

    private string _filter = string.Empty;

    internal static IReadOnlyList<PropertyInfo> EditableShadowProperties =>
        ShadowEditors.Select(static editor => editor.Property).ToArray();

    internal static IReadOnlyList<PropertyInfo> EditableSceneSubmissionProperties =>
        SceneSubmissionEditors.Select(static editor => editor.Property).ToArray();

    internal static bool IsSupportedScalarType(Type type) =>
        type == typeof(bool) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(float) ||
        type.IsEnum;

    public void Render(EditorController editor)
    {
        ImGui.Begin("Shadows");
        RenderRuntimeSummary(editor.RendererDiagnostics);

        RenderSettings? renderSettings = editor.RendererSettings;
        if (renderSettings == null)
        {
            ImGui.TextWrapped("Live shadow settings are unavailable because this editor is not attached to a Vulkan renderer.");
            ImGui.End();
            return;
        }

        ShadowSettings shadows = renderSettings.Shadows;
        ImGui.SeparatorText("Live renderer settings");
        ImGui.InputText("Filter settings", ref _filter, (nuint)256);
        ImGui.TextDisabled("Changes apply immediately. Hover a control for its settings property and current value.");

        if (ImGui.Button("Reset shadow diagnostics"))
        {
            shadows.DebugView = ShadowDebugView.None;
            shadows.DirectionalShadowPreviewCascade = 0;
            shadows.ForceStaticCascadeCacheRefresh = false;
            renderSettings.Diagnostics.DirectionalShadowReceiverCountersEnabled = false;
        }

        RenderGroups(shadows, ShadowEditors, ShadowGroupOrder);

        if (ImGui.CollapsingHeader("GPU scene submission", ImGuiTreeNodeFlags.DefaultOpen))
            RenderEditors(renderSettings.SceneSubmission, SceneSubmissionEditors);

        ImGui.SeparatorText("Receiver telemetry");
        bool receiverCounters = renderSettings.Diagnostics.DirectionalShadowReceiverCountersEnabled;
        if (ImGui.Checkbox("Directional receiver counters", ref receiverCounters))
            renderSettings.Diagnostics.DirectionalShadowReceiverCountersEnabled = receiverCounters;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Samples one pixel per 16x16 tile and records per-cascade receiver outcomes for snapshots.");

        if (shadows.ForceStaticCascadeCacheRefresh)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.72f, 0.2f, 1f),
                "Static cache force-refresh is ON; every active cascade is redrawn every frame.");
        }

        ImGui.End();
    }

    private static void RenderRuntimeSummary(RendererDiagnostics? diagnostics)
    {
        if (diagnostics == null)
        {
            ImGui.TextDisabled("Directional-shadow runtime diagnostics are not available yet.");
            return;
        }

        DirectionalShadowRuntimeDiagnostics runtime = diagnostics.DirectionalShadowRuntime;
        if (runtime.Enabled == 0)
        {
            ImGui.TextDisabled("Directional shadows are inactive in the latest completed frame.");
            return;
        }

        ImGui.Text(
            $"Mode: requested={runtime.RequestedMode}, effective={runtime.EffectiveMode}, " +
            $"qualification={runtime.QualificationLevel}");
        if (!string.IsNullOrWhiteSpace(runtime.QualificationDetail))
            ImGui.TextWrapped($"Qualification: {runtime.QualificationDetail}");
        if (!string.IsNullOrWhiteSpace(runtime.QualificationId))
        {
            ImGui.TextWrapped(
                $"Evidence: {runtime.QualificationId}  rule={runtime.QualificationDeviceRuleId}  " +
                $"track={runtime.QualificationTrackId}");
            ImGui.Text(
                $"Qualified budgets: GPU={runtime.QualifiedGpuBudgetMicroseconds:0} µs, " +
                $"memory={runtime.QualifiedMemoryBudgetBytes:N0} bytes");
        }
        if (runtime.FallbackReason != DirectionalShadowFallbackReason.None)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.72f, 0.2f, 1f),
                $"Fallback: {runtime.FallbackReason}");
            if (!string.IsNullOrWhiteSpace(runtime.FallbackDetail))
                ImGui.TextWrapped(runtime.FallbackDetail);
        }
        ImGui.Text(
            $"Receivers: opaque={runtime.OpaqueReceiverPolicy}, " +
            $"transparent={runtime.TransparentReceiverPolicy}, decal={runtime.DecalReceiverPolicy}");

        string splits = runtime.CascadeSplits.Length == 0
            ? "unavailable"
            : string.Join(", ", runtime.CascadeSplits.Select(static split => $"{split:0.##} m"));
        ImGui.Text($"Splits: {splits}");
        ImGui.Text(
            $"Static cache masks  active=0x{runtime.StaticCacheActiveMask:X}  " +
            $"valid=0x{runtime.StaticCacheValidMask:X}  refresh=0x{runtime.StaticCacheRefreshMask:X}  " +
            $"reuse=0x{runtime.StaticCacheReuseMask:X}");

        if (runtime.RayMaskEnabled != 0 || runtime.CsmTemporalEnabled != 0)
        {
            ImGui.Text(
                $"GPU µs: CSM={runtime.GpuCsmMicroseconds}, ray={runtime.GpuRayTraceMicroseconds}, " +
                $"temporal={runtime.GpuTemporalMicroseconds}, spatial={runtime.GpuSpatialMicroseconds}");
            ImGui.Text(
                $"History: valid={runtime.HistoryValid}, reset={runtime.HistoryResetReason}, " +
                $"bytes={runtime.HistoryBytes:N0}");
        }
        if (runtime.RayCounters.ReadbackValid != 0)
        {
            ImGui.Text(
                $"Opaque rays={runtime.RayCounters.OpaqueRaysIssued:N0}, " +
                $"hit={runtime.RayCounters.OpaqueHitRate:P1}, " +
                $"avg candidates={runtime.RayCounters.AverageOpaqueCandidates:0.00}, " +
                $"caps={runtime.RayCounters.OpaqueCandidateCapHits:N0}");
            ImGui.Text(
                $"Transparent rays={runtime.RayCounters.TransparentRaysIssued:N0}, " +
                $"hit={runtime.RayCounters.TransparentHitRate:P1}, " +
                $"avg candidates={runtime.RayCounters.AverageTransparentCandidates:0.00}");
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.SceneSubmissionGpuDirectionalShadowCascadeSummary))
            ImGui.TextWrapped(diagnostics.SceneSubmissionGpuDirectionalShadowCascadeSummary);
    }

    private void RenderGroups(
        object target,
        IReadOnlyList<SettingEditor> editors,
        IReadOnlyList<string> groupOrder)
    {
        bool filtering = !string.IsNullOrWhiteSpace(_filter);
        foreach (string group in groupOrder)
        {
            SettingEditor[] visible = editors
                .Where(editor => editor.Group == group && MatchesFilter(editor))
                .ToArray();
            if (visible.Length == 0)
                continue;

            ImGuiTreeNodeFlags flags = filtering || group == "Directional"
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.None;
            if (ImGui.CollapsingHeader($"{group} ({visible.Length})", flags))
                RenderEditors(target, visible);
        }
    }

    private void RenderEditors(object target, IReadOnlyList<SettingEditor> editors)
    {
        foreach (SettingEditor editor in editors)
        {
            if (!MatchesFilter(editor))
                continue;
            RenderProperty(target, editor);
        }
    }

    private static void RenderProperty(object target, SettingEditor editor)
    {
        PropertyInfo property = editor.Property;
        object? current = property.GetValue(target);
        object? next = current;
        bool changed = false;

        if (property.PropertyType == typeof(bool))
        {
            bool value = (bool)(current ?? false);
            changed = ImGui.Checkbox(editor.Label, ref value);
            next = value;
        }
        else if (property.PropertyType == typeof(int))
        {
            int value = (int)(current ?? 0);
            changed = ImGui.DragInt(editor.Label, ref value, 1f);
            next = value;
        }
        else if (property.PropertyType == typeof(uint))
        {
            uint value = (uint)(current ?? 0u);
            changed = ImGui.InputScalar(editor.Label, ImGuiDataType.U32, &value);
            next = value;
        }
        else if (property.PropertyType == typeof(float))
        {
            float value = (float)(current ?? 0f);
            changed = ImGui.DragFloat(editor.Label, ref value, ResolveFloatSpeed(property.Name));
            next = value;
        }
        else if (property.PropertyType.IsEnum && ImGui.BeginCombo(editor.Label, current?.ToString() ?? string.Empty))
        {
            foreach (object value in Enum.GetValues(property.PropertyType))
            {
                if (ImGui.Selectable(value.ToString() ?? string.Empty, Equals(value, current)))
                {
                    next = value;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        if (changed)
            property.SetValue(target, next);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"{property.DeclaringType?.Name}.{property.Name} = {property.GetValue(target)}");
    }

    private bool MatchesFilter(SettingEditor editor) =>
        string.IsNullOrWhiteSpace(_filter) ||
        editor.Property.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        editor.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        editor.Group.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private static SettingEditor[] BuildEditors<T>(Func<string, string> groupResolver)
    {
        var editors = new List<SettingEditor>();
        foreach (PropertyInfo property in typeof(T)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.MetadataToken))
        {
            if (property.GetIndexParameters().Length != 0 || property.SetMethod?.IsPublic != true)
                continue;
            if (!IsSupportedScalarType(property.PropertyType))
                throw new NotSupportedException(
                    $"Shadow editor does not support writable property '{typeof(T).Name}.{property.Name}' ({property.PropertyType.Name}).");

            editors.Add(new SettingEditor(property, FormatLabel(property.Name), groupResolver(property.Name)));
        }

        return editors.ToArray();
    }

    private static string ResolveShadowGroup(string propertyName)
    {
        if (propertyName is nameof(ShadowSettings.DebugView) or
            nameof(ShadowSettings.DirectionalShadowPreviewCascade) or
            nameof(ShadowSettings.ForceStaticCascadeCacheRefresh))
        {
            return "Static cache / debug";
        }

        if (propertyName.Contains("Spot", StringComparison.Ordinal))
            return "Spot";
        if (propertyName.Contains("Point", StringComparison.Ordinal))
            return "Point";
        return "Directional";
    }

    private static float ResolveFloatSpeed(string propertyName)
    {
        if (propertyName.Contains("ConstantDepthBias", StringComparison.Ordinal))
            return 0.00005f;
        if (propertyName.Contains("Bias", StringComparison.Ordinal) ||
            propertyName.Contains("BlendFraction", StringComparison.Ordinal))
        {
            return 0.005f;
        }
        if (propertyName.Contains("Ratio", StringComparison.Ordinal))
            return 0.05f;
        if (propertyName.Contains("Distance", StringComparison.Ordinal))
            return 0.5f;
        return 0.01f;
    }

    private static string FormatLabel(string propertyName)
    {
        var result = new StringBuilder(propertyName.Length + 8);
        for (int index = 0; index < propertyName.Length; index++)
        {
            char value = propertyName[index];
            if (index > 0 && char.IsUpper(value) &&
                (!char.IsUpper(propertyName[index - 1]) ||
                 (index + 1 < propertyName.Length && char.IsLower(propertyName[index + 1]))))
            {
                result.Append(' ');
            }
            result.Append(value);
        }
        return result.ToString();
    }

    private sealed record SettingEditor(PropertyInfo Property, string Label, string Group);
}
