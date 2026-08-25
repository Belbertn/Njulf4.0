using System.Reflection;
using System.Text;
using Hexa.NET.ImGui;
using Njulf.Rendering.Data;
using CoreVector3 = Njulf.Core.Math.Vector3;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Editor;

/// <summary>
/// Live controls for the renderer settings most commonly tuned while authoring a scene.
/// Specialized GI, material, and shadow controls remain in their dedicated panels.
/// </summary>
internal sealed unsafe class RenderingSettingsEditorPanel
{
    private static readonly SettingEditor[] ExposureEditors = BuildEditors<RenderSettings>(
        static property => property.Name is
            nameof(RenderSettings.Exposure) or
            nameof(RenderSettings.ToneMapper) or
            nameof(RenderSettings.ShowRawHdrSceneColor));

    private static readonly SettingEditor[] ResolutionEditors = BuildEditors<RenderSettings>(
        static property => property.Name == nameof(RenderSettings.ResolutionScale));

    private static readonly SettingEditor[] DynamicResolutionEditors = BuildEditors<DynamicResolutionSettings>();
    private static readonly SettingEditor[] AutoExposureEditors = BuildEditors<AutoExposureSettings>();
    private static readonly SettingEditor[] BloomEditors = BuildEditors<BloomSettings>();
    private static readonly SettingEditor[] EnvironmentEditors = BuildEditors<EnvironmentSettings>();
    private static readonly SettingEditor[] ReflectionEditors = BuildEditors<ReflectionSettings>();
    private static readonly SettingEditor[] AmbientOcclusionEditors = BuildEditors<AmbientOcclusionSettings>();
    private static readonly SettingEditor[] AntiAliasingEditors = BuildEditors<AntiAliasingSettings>();
    private static readonly SettingEditor[] FogEditors = BuildEditors<FogSettings>();
    private static readonly SettingEditor[] TransparencyEditors =
        BuildEditors<TransparencySettings>();

    private static readonly PropertyInfo[] AllEditableProperties =
    [
        .. ExposureEditors.Select(static editor => editor.Property),
        .. ResolutionEditors.Select(static editor => editor.Property),
        .. DynamicResolutionEditors.Select(static editor => editor.Property),
        .. AutoExposureEditors.Select(static editor => editor.Property),
        .. BloomEditors.Select(static editor => editor.Property),
        .. EnvironmentEditors.Select(static editor => editor.Property),
        .. ReflectionEditors.Select(static editor => editor.Property),
        .. AmbientOcclusionEditors.Select(static editor => editor.Property),
        .. AntiAliasingEditors.Select(static editor => editor.Property),
        .. FogEditors.Select(static editor => editor.Property),
        .. TransparencyEditors.Select(static editor => editor.Property)
    ];

    private string _filter = string.Empty;

    internal static IReadOnlyList<PropertyInfo> EditableProperties => AllEditableProperties;

    internal static bool IsSupportedSettingType(Type type) =>
        type == typeof(bool) ||
        type == typeof(string) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(CoreVector3) ||
        type.IsEnum;

    public void Render(EditorController editor)
    {
        ImGui.Begin("Rendering Settings");

        RenderSettings? settings = editor.RendererSettings;
        if (settings == null)
        {
            ImGui.TextWrapped("Live rendering settings are unavailable because this editor is not attached to a Vulkan renderer.");
            ImGui.End();
            return;
        }

        RenderRuntimeSummary(editor.RendererDiagnostics);
        if (ImGui.Button("Reset visualization overrides"))
            settings.ResetRenderViewOverrides();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clears raw HDR and all renderer debug views without changing physical rendering settings.");

        ImGui.SeparatorText("Live renderer settings");
        ImGui.InputText("Filter settings", ref _filter, (nuint)256);
        ImGui.TextDisabled(
            $"{AllEditableProperties.Length} settings; changes apply immediately. Hover a control for its property and current value.");

        RenderSection("Exposure and tone mapping", settings, ExposureEditors, defaultOpen: true);
        RenderResolutionSection(settings);
        RenderSection("Auto exposure", settings.AutoExposure, AutoExposureEditors, defaultOpen: true);
        RenderSection("Bloom", settings.Bloom, BloomEditors, defaultOpen: true);
        RenderSection("Environment lighting", settings.Environment, EnvironmentEditors);
        RenderSection("Reflections", settings.Reflections, ReflectionEditors);
        RenderSection("Ambient occlusion", settings.AmbientOcclusion, AmbientOcclusionEditors);
        RenderSection("Anti-aliasing", settings.AntiAliasing, AntiAliasingEditors);
        RenderSection("Fog", settings.Fog, FogEditors);
        RenderSection("Transparency and refraction", settings.Transparency,
            TransparencyEditors);

        ImGui.End();
    }

    private static void RenderRuntimeSummary(RendererDiagnostics? diagnostics)
    {
        if (diagnostics == null)
        {
            ImGui.TextDisabled("Runtime rendering diagnostics are not available yet.");
            return;
        }

        ImGui.Text(
            $"Effective exposure: {diagnostics.Exposure:0.###}  Tone mapper: {diagnostics.ToneMapper}");
        if (diagnostics.AutoExposureEnabled != 0)
        {
            ImGui.TextDisabled(
                $"Auto exposure: luminance {diagnostics.AutoExposureAverageLuminance:0.####}, " +
                $"target {diagnostics.AutoExposureTargetExposure:0.###}, " +
                $"samples {diagnostics.AutoExposureSampleCount:N0}");
        }

        ImGui.TextDisabled(
            $"Resolution scale: requested {diagnostics.RequestedDynamicResolutionScale:0.###}, " +
            $"committed {diagnostics.CommittedRenderTargetScale:0.###}");
    }

    private void RenderResolutionSection(RenderSettings settings)
    {
        const string sectionName = "Resolution";
        SettingEditor[] visibleTopLevel = ResolutionEditors
            .Where(editor => MatchesFilter(editor, sectionName))
            .ToArray();
        SettingEditor[] visibleDynamic = DynamicResolutionEditors
            .Where(editor => MatchesFilter(editor, sectionName))
            .ToArray();
        int visibleCount = visibleTopLevel.Length + visibleDynamic.Length;
        if (visibleCount == 0)
            return;

        if (!ImGui.CollapsingHeader(
                $"{sectionName} ({visibleCount})##RenderingSettings.{sectionName}",
                ImGuiTreeNodeFlags.DefaultOpen))
            return;

        RenderEditors(settings, visibleTopLevel);
        RenderEditors(settings.DynamicResolution, visibleDynamic);
        ImGui.TextDisabled($"Effective scale: {settings.EffectiveResolutionScale:0.###}");
    }

    private void RenderSection(
        string sectionName,
        object target,
        IReadOnlyList<SettingEditor> editors,
        bool defaultOpen = false)
    {
        SettingEditor[] visible = editors
            .Where(editor => MatchesFilter(editor, sectionName))
            .ToArray();
        if (visible.Length == 0)
            return;

        ImGuiTreeNodeFlags flags = IsFiltering || defaultOpen
            ? ImGuiTreeNodeFlags.DefaultOpen
            : ImGuiTreeNodeFlags.None;
        if (ImGui.CollapsingHeader(
                $"{sectionName} ({visible.Length})##RenderingSettings.{sectionName}",
                flags))
        {
            RenderEditors(target, visible);
        }
    }

    private static void RenderEditors(object target, IReadOnlyList<SettingEditor> editors)
    {
        foreach (SettingEditor editor in editors)
            RenderProperty(target, editor);
    }

    private static void RenderProperty(object target, SettingEditor editor)
    {
        PropertyInfo property = editor.Property;
        string controlLabel = $"{editor.Label}##{property.DeclaringType?.Name}.{property.Name}";
        object? current = property.GetValue(target);
        object? next = current;
        bool changed = false;

        if (property.PropertyType == typeof(bool))
        {
            bool value = (bool)(current ?? false);
            changed = ImGui.Checkbox(controlLabel, ref value);
            next = value;
        }
        else if (property.PropertyType == typeof(string))
        {
            string value = (string?)current ?? string.Empty;
            changed = ImGui.InputText(controlLabel, ref value, (nuint)1024);
            next = value;
        }
        else if (property.PropertyType == typeof(int))
        {
            int value = (int)(current ?? 0);
            changed = ImGui.DragInt(controlLabel, ref value, 1f);
            next = value;
        }
        else if (property.PropertyType == typeof(uint))
        {
            uint value = (uint)(current ?? 0u);
            changed = ImGui.InputScalar(controlLabel, ImGuiDataType.U32, &value);
            next = value;
        }
        else if (property.PropertyType == typeof(ulong))
        {
            ulong value = (ulong)(current ?? 0UL);
            changed = ImGui.InputScalar(controlLabel, ImGuiDataType.U64, &value);
            next = value;
        }
        else if (property.PropertyType == typeof(float))
        {
            float value = (float)(current ?? 0f);
            changed = ImGui.DragFloat(controlLabel, ref value, ResolveFloatSpeed(property.Name));
            next = value;
        }
        else if (property.PropertyType == typeof(CoreVector3))
        {
            CoreVector3 value = (CoreVector3)(current ?? CoreVector3.Zero);
            var editable = new NumericsVector3(value.X, value.Y, value.Z);
            changed = property.Name.Contains("Color", StringComparison.Ordinal)
                ? ImGui.ColorEdit3(controlLabel, ref editable)
                : ImGui.DragFloat3(controlLabel, ref editable, ResolveFloatSpeed(property.Name));
            next = new CoreVector3(editable.X, editable.Y, editable.Z);
        }
        else if (property.PropertyType.IsEnum &&
                 ImGui.BeginCombo(controlLabel, current?.ToString() ?? string.Empty))
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

    private bool MatchesFilter(SettingEditor editor, string sectionName) =>
        !IsFiltering ||
        editor.Property.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        editor.Label.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        sectionName.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private bool IsFiltering => !string.IsNullOrWhiteSpace(_filter);

    private static SettingEditor[] BuildEditors<T>(Func<PropertyInfo, bool>? include = null)
    {
        var editors = new List<SettingEditor>();
        foreach (PropertyInfo property in typeof(T)
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .OrderBy(static property => property.MetadataToken))
        {
            if (property.GetIndexParameters().Length != 0 ||
                property.SetMethod?.IsPublic != true ||
                include?.Invoke(property) == false)
            {
                continue;
            }

            if (!IsSupportedSettingType(property.PropertyType))
            {
                throw new NotSupportedException(
                    $"Rendering settings editor does not support writable property " +
                    $"'{typeof(T).Name}.{property.Name}' ({property.PropertyType.Name}).");
            }

            editors.Add(new SettingEditor(property, FormatLabel(property.Name)));
        }

        return editors.ToArray();
    }

    private static float ResolveFloatSpeed(string propertyName)
    {
        if (propertyName.Contains("Microseconds", StringComparison.Ordinal))
            return 10f;
        if (propertyName.Contains("Distance", StringComparison.Ordinal) ||
            propertyName.Contains("Seconds", StringComparison.Ordinal) ||
            propertyName.Contains("Exponent", StringComparison.Ordinal))
        {
            return 0.1f;
        }
        if (propertyName.Contains("LogLuminance", StringComparison.Ordinal) ||
            propertyName.Contains("TimeOfDay", StringComparison.Ordinal) ||
            propertyName.Contains("Degrees", StringComparison.Ordinal))
        {
            return 0.05f;
        }
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

    private sealed record SettingEditor(PropertyInfo Property, string Label);
}
