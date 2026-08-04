using System.Reflection;
using System.Text;
using Hexa.NET.ImGui;
using Njulf.Rendering.Data;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Editor;

/// <summary>
/// Live editor for renderer-owned Simple DDGI configuration.
/// </summary>
internal sealed unsafe class GlobalIlluminationEditorPanel
{
    private static readonly string[] GroupOrder =
    [
        "General",
        "Simple DDGI",
        "Far Field",
        "Ray Query / Acceleration Structures",
        "Effective State"
    ];

    private static readonly GiPropertyEditor[] PropertyEditors = BuildPropertyEditors();

    private string _filter = string.Empty;
    private string _settingsPath = string.Empty;
    private string? _lastError;

    internal static IReadOnlyList<PropertyInfo> EditableProperties => PropertyEditors
        .Where(static editor => editor.Editable)
        .Select(static editor => editor.Property)
        .ToArray();

    internal static bool IsSupportedScalarType(Type type) =>
        type == typeof(bool) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type.IsEnum;

    public void Render(EditorController editor)
    {
        ImGui.Begin("Global Illumination");
        RenderRuntimeSummary(editor);

        RenderSettings? renderSettings = editor.RendererSettings;
        if (renderSettings == null)
        {
            ImGui.TextWrapped("Live GI settings are unavailable because this editor is not attached to a Vulkan renderer.");
            ImGui.End();
            return;
        }

        GlobalIlluminationSettings settings = renderSettings.GlobalIllumination;
        RenderPersistence(editor);
        ImGui.SeparatorText("Live renderer settings");
        ImGui.InputText("Filter settings", ref _filter, (nuint)256);
        ImGui.TextDisabled($"{PropertyEditors.Count(static item => item.Editable)} writable GI settings; changes apply immediately.");
        RenderScalarSettings(settings);
        RenderLayeredReceiverSettings(renderSettings, editor.RendererDiagnostics);
        RenderSimpleDdgiAuthoredVolumes(editor, settings);

        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f), _lastError);
        ImGui.End();
    }

    private static void RenderLayeredReceiverSettings(
        RenderSettings settings,
        RendererDiagnostics? diagnostics)
    {
        ImGui.SeparatorText("Transparent and decal receivers");
        ImGui.TextWrapped(
            "Layered receivers sample Simple DDGI directly.");

        bool transparentGi = settings.Transparency.ReceiveGlobalIllumination;
        if (ImGui.Checkbox("Transparent materials receive DDGI", ref transparentGi))
            settings.Transparency.ReceiveGlobalIllumination = transparentGi;

        bool decalGi = settings.Decals.ReceiveGlobalIllumination;
        if (ImGui.Checkbox("Geometry decals receive DDGI", ref decalGi))
            settings.Decals.ReceiveGlobalIllumination = decalGi;

        if (diagnostics == null)
            return;

        ImGui.TextDisabled(
            $"Transparent samples: {diagnostics.DdgiTransparentReceiverSampleCount:N0}, " +
            $"irradiance L: {diagnostics.DdgiTransparentReceiverIrradianceLuminanceAverage:0.####}, " +
            $"final L: {diagnostics.DdgiTransparentReceiverFinalLuminanceAverage:0.####}");
        ImGui.TextDisabled(
            $"Decal samples: {diagnostics.DdgiDecalReceiverSampleCount:N0}, " +
            $"irradiance L: {diagnostics.DdgiDecalReceiverIrradianceLuminanceAverage:0.####}, " +
            $"final L: {diagnostics.DdgiDecalReceiverFinalLuminanceAverage:0.####}");
    }

    private void RenderRuntimeSummary(EditorController editor)
    {
        RendererDiagnostics? diagnostics = editor.RendererDiagnostics;
        ImGui.Text($"Authored scene DDGI volumes: {editor.Scene.GlobalIlluminationProbeVolumes.Count}");
        if (editor.Scene.GlobalIlluminationProbeVolumes.Count == 0)
            ImGui.TextDisabled("Automatic Simple-DDGI rings remain available when no authored volume is present.");
        if (ImGui.Button("Add scene DDGI volume"))
            Run(editor.AddGlobalIlluminationProbeVolumeAtCamera);

        if (diagnostics != null)
        {
            int runtimeVolumeCount = diagnostics.DdgiVolumes.Count > 0
                ? diagnostics.DdgiVolumes.Count
                : diagnostics.DdgiProbeVolumeCount;
            string backend = diagnostics.SimpleDdgiActive != 0
                ? "Simple DDGI"
                : "Inactive";
            ImGui.Text($"Active backend: {backend} ({diagnostics.GlobalIlluminationMode})");
            ImGui.Text($"Runtime DDGI volumes: {runtimeVolumeCount}    Probes: {Math.Max(diagnostics.SimpleDdgiProbeCount, diagnostics.DdgiProbeCount)}");
        }
    }

    private void RenderPersistence(EditorController editor)
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
            _settingsPath = Path.Combine(Environment.CurrentDirectory, "render-settings.json");

        ImGui.SetNextItemWidth(420f);
        ImGui.InputText("Render settings file", ref _settingsPath, (nuint)1024);
        ImGui.SameLine();
        if (ImGui.Button("Save render settings"))
            Run(() => editor.SaveRenderSettings(_settingsPath));
    }

    private void RenderScalarSettings(GlobalIlluminationSettings settings)
    {
        bool filtering = !string.IsNullOrWhiteSpace(_filter);
        foreach (string group in GroupOrder)
        {
            GiPropertyEditor[] visible = PropertyEditors
                .Where(item => item.Group == group && MatchesFilter(item))
                .ToArray();
            if (visible.Length == 0)
                continue;

            ImGuiTreeNodeFlags flags = filtering || group == "General"
                ? ImGuiTreeNodeFlags.DefaultOpen
                : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{group} ({visible.Length})", flags))
                continue;

            foreach (GiPropertyEditor item in visible)
                RenderProperty(settings, item);
        }
    }

    private static void RenderProperty(GlobalIlluminationSettings settings, GiPropertyEditor item)
    {
        object? current = item.Property.GetValue(settings);
        if (!item.Editable)
        {
            ImGui.TextDisabled($"{item.Label}: {FormatValue(current)}");
            return;
        }

        Type type = item.Property.PropertyType;
        bool changed = false;
        object? next = current;
        if (type == typeof(bool))
        {
            bool value = (bool)(current ?? false);
            changed = ImGui.Checkbox(item.Label, ref value);
            next = value;
        }
        else if (type == typeof(int))
        {
            int value = (int)(current ?? 0);
            changed = ImGui.DragInt(item.Label, ref value, 1f);
            next = value;
        }
        else if (type == typeof(float))
        {
            float value = (float)(current ?? 0f);
            changed = ImGui.DragFloat(item.Label, ref value, ResolveFloatSpeed(item.Property.Name, value));
            next = value;
        }
        else if (type == typeof(uint))
        {
            uint value = (uint)(current ?? 0u);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.U32, &value);
            next = value;
        }
        else if (type == typeof(long))
        {
            long value = (long)(current ?? 0L);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.S64, &value);
            next = value;
        }
        else if (type == typeof(ulong))
        {
            ulong value = (ulong)(current ?? 0UL);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.U64, &value);
            next = value;
        }
        else if (type == typeof(double))
        {
            double value = (double)(current ?? 0d);
            changed = ImGui.InputScalar(item.Label, ImGuiDataType.Double, &value);
            next = value;
        }
        else if (type.IsEnum)
        {
            if (ImGui.BeginCombo(item.Label, current?.ToString() ?? string.Empty))
            {
                foreach (object value in Enum.GetValues(type))
                {
                    bool selected = Equals(value, current);
                    if (ImGui.Selectable(value.ToString() ?? string.Empty, selected))
                    {
                        next = value;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }
        }

        if (changed)
        {
            if (item.Property.Name == nameof(GlobalIlluminationSettings.DdgiQualityTier) && next is DdgiQualityTier tier)
                settings.ApplyDdgiQualityTier(tier);
            else
                item.Property.SetValue(settings, next);
        }

        if (ImGui.IsItemHovered())
        {
            string valueText = FormatValue(item.Property.GetValue(settings));
            if (item.Property.PropertyType == typeof(ulong) && item.Property.Name.EndsWith("Bytes", StringComparison.Ordinal))
            {
                double mebibytes = Convert.ToDouble(item.Property.GetValue(settings)) / (1024d * 1024d);
                valueText += $" ({mebibytes:0.##} MiB)";
            }
            ImGui.SetTooltip($"{item.Property.Name} = {valueText}");
        }
    }

    private void RenderSimpleDdgiAuthoredVolumes(EditorController editor, GlobalIlluminationSettings settings)
    {
        ImGui.SeparatorText($"Simple DDGI local overrides ({settings.SimpleDdgiAuthoredVolumes.Count})");
        ImGui.TextWrapped("Optional local quality boxes layered over the automatic near/mid/far camera-relative rings.");
        if (ImGui.Button("Add local override at camera"))
            Run(editor.AddSimpleDdgiAuthoredVolumeAtCamera);

        int removeIndex = -1;
        for (int index = 0; index < settings.SimpleDdgiAuthoredVolumes.Count; index++)
        {
            SimpleDdgiAuthoredVolume source = settings.SimpleDdgiAuthoredVolumes[index];
            if (!ImGui.CollapsingHeader($"Override {index + 1}: {source.Purpose}##simpleDdgiOverride{index}"))
                continue;

            ImGui.PushID(index);
            NumericsVector3 min = ToNumerics(source.Min);
            NumericsVector3 max = ToNumerics(source.Max);
            NumericsVector3 phase = ToNumerics(source.LatticePhase);
            float spacing = source.Spacing;
            int priority = source.Priority;
            SimpleDdgiVolumePurpose purpose = source.Purpose;
            bool changed = ImGui.DragFloat3("Min", ref min, 0.05f);
            changed |= ImGui.DragFloat3("Max", ref max, 0.05f);
            changed |= ImGui.DragFloat("Spacing", ref spacing, 0.01f, 0.25f, 8f);
            changed |= ImGui.DragFloat3("Lattice phase", ref phase, 0.01f);
            changed |= ImGui.DragInt("Priority", ref priority, 1f);
            if (ImGui.BeginCombo("Purpose", purpose.ToString()))
            {
                foreach (SimpleDdgiVolumePurpose candidate in Enum.GetValues<SimpleDdgiVolumePurpose>())
                {
                    if (ImGui.Selectable(candidate.ToString(), candidate == purpose))
                    {
                        purpose = candidate;
                        changed = true;
                    }
                }
                ImGui.EndCombo();
            }

            if (changed)
            {
                CoreVector3 first = ToCore(min);
                CoreVector3 second = ToCore(max);
                CoreVector3 normalizedMin = new(
                    MathF.Min(first.X, second.X),
                    MathF.Min(first.Y, second.Y),
                    MathF.Min(first.Z, second.Z));
                CoreVector3 normalizedMax = new(
                    MathF.Max(first.X, second.X),
                    MathF.Max(first.Y, second.Y),
                    MathF.Max(first.Z, second.Z));
                editor.UpdateSimpleDdgiAuthoredVolume(index, new SimpleDdgiAuthoredVolume(
                    normalizedMin,
                    normalizedMax,
                    Math.Clamp(spacing, 0.25f, 8f),
                    ToCore(phase),
                    purpose,
                    priority));
            }

            if (ImGui.Button("Remove override"))
                removeIndex = index;
            ImGui.PopID();
        }

        if (removeIndex >= 0)
            editor.RemoveSimpleDdgiAuthoredVolume(removeIndex);
    }

    private bool MatchesFilter(GiPropertyEditor item) =>
        string.IsNullOrWhiteSpace(_filter) ||
        item.Property.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        item.Group.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private void Run(Action action)
    {
        try
        {
            action();
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = error.Message;
        }
    }

    private void Run<T>(Func<T> action)
    {
        try
        {
            _ = action();
            _lastError = null;
        }
        catch (Exception error)
        {
            _lastError = error.Message;
        }
    }

    private static GiPropertyEditor[] BuildPropertyEditors()
    {
        var result = new List<GiPropertyEditor>();
        foreach (PropertyInfo property in typeof(GlobalIlluminationSettings)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.MetadataToken))
        {
            if (property.GetIndexParameters().Length != 0 ||
                property.Name == nameof(GlobalIlluminationSettings.SimpleDdgiAuthoredVolumes))
            {
                continue;
            }

            bool editable = property.SetMethod?.IsPublic == true;
            if (!IsSupportedScalarType(property.PropertyType))
            {
                if (editable)
                    throw new NotSupportedException($"GI editor does not support writable property '{property.Name}' ({property.PropertyType.Name}).");
                continue;
            }

            result.Add(new GiPropertyEditor(
                property,
                ResolveGroup(property.Name, editable),
                FormatLabel(property.Name),
                editable));
        }
        return result.ToArray();
    }

    private static string ResolveGroup(string name, bool editable)
    {
        if (!editable || name.StartsWith("Effective", StringComparison.Ordinal))
            return "Effective State";
        if (name.StartsWith("SimpleDdgi", StringComparison.Ordinal))
            return "Simple DDGI";
        if (name.StartsWith("FarField", StringComparison.Ordinal))
            return "Far Field";
        if (name.StartsWith("GiAcceleration", StringComparison.Ordinal) ||
            name.StartsWith("StreamedGi", StringComparison.Ordinal) ||
            name == nameof(GlobalIlluminationSettings.UseRayQueryBackend))
        {
            return "Ray Query / Acceleration Structures";
        }
        if (name.StartsWith("Ddgi", StringComparison.Ordinal) || name == nameof(GlobalIlluminationSettings.UseDdgi))
            return "Simple DDGI";
        return "General";
    }

    private static string FormatLabel(string name)
    {
        var builder = new StringBuilder(name.Length + 12);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) &&
                (char.IsLower(name[index - 1]) ||
                 index + 1 < name.Length && char.IsLower(name[index + 1])))
            {
                builder.Append(' ');
            }
            builder.Append(current);
        }
        return builder.ToString()
            .Replace("Ddgi", "DDGI", StringComparison.Ordinal)
            .Replace("Gpu", "GPU", StringComparison.Ordinal)
            .Replace("Gi ", "GI ", StringComparison.Ordinal);
    }

    private static float ResolveFloatSpeed(string name, float value)
    {
        if (name.Contains("Fraction", StringComparison.Ordinal) ||
            name.Contains("Threshold", StringComparison.Ordinal) ||
            name.Contains("Hysteresis", StringComparison.Ordinal) ||
            name.Contains("Bias", StringComparison.Ordinal) ||
            name.Contains("Intensity", StringComparison.Ordinal) ||
            name.Contains("Scale", StringComparison.Ordinal))
        {
            return 0.01f;
        }
        return MathF.Max(0.01f, MathF.Abs(value) * 0.01f);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        float number => number.ToString("0.####"),
        double number => number.ToString("0.####"),
        _ => value.ToString() ?? string.Empty
    };

    private static NumericsVector3 ToNumerics(CoreVector3 value) => new(value.X, value.Y, value.Z);
    private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);

    private sealed record GiPropertyEditor(PropertyInfo Property, string Group, string Label, bool Editable);
}
