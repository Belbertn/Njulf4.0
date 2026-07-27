using Hexa.NET.ImGui;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NumericsVector3 = System.Numerics.Vector3;
using CoreQuaternion = Njulf.Core.Math.Quaternion;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Editor;

/// <summary>Dockable v1 editor shell: save/add tools, hierarchy, and live inspectors.</summary>
public sealed class EditorImGuiPanels
{
    private string _filter = string.Empty;
    private string _saveAsPath = string.Empty;
    private string? _lastError;
    private int _selectedDependency;
    private readonly GlobalIlluminationEditorPanel _globalIlluminationPanel = new();
    private readonly ShadowEditorPanel _shadowPanel = new();

    public void Render(EditorController editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.Enabled)
            return;

        RenderMainMenu(editor);
        RenderHierarchy(editor);
        RenderInspector(editor);
        _globalIlluminationPanel.Render(editor);
        _shadowPanel.Render(editor);
    }

    private void RenderMainMenu(EditorController editor)
    {
        ImGui.Begin("Njulf Editor");
        ImGui.Text(editor.IsDirty ? "Scene *" : "Scene");
        ImGui.SameLine();
        ImGui.TextDisabled(editor.ScenePath ?? "Unsaved code-built scene");

        if (ImGui.Button("Save") && editor.ScenePath != null)
            Run(editor.Save);
        ImGui.SameLine();
        if (ImGui.Button("Reload") && editor.ScenePath != null)
            Run(editor.Reload);

        if (string.IsNullOrWhiteSpace(_saveAsPath))
            _saveAsPath = editor.ScenePath ?? Path.Combine(Environment.CurrentDirectory, "Scene.njscene.json");
        ImGui.SetNextItemWidth(420f);
        ImGui.InputText("Save As", ref _saveAsPath, (nuint)1024);
        ImGui.SameLine();
        if (ImGui.Button("Write") && !string.IsNullOrWhiteSpace(_saveAsPath))
            Run(() => editor.SaveAs(_saveAsPath));

        IReadOnlyList<SceneAssetReference> dependencies = editor.GetModelDependencies();
        if (dependencies.Count > 0)
        {
            _selectedDependency = Math.Clamp(_selectedDependency, 0, dependencies.Count - 1);
            SceneAssetReference selected = dependencies[_selectedDependency];
            if (ImGui.BeginCombo("Model", $"{Path.GetFileName(selected.Path)} : {selected.SubObject}"))
            {
                for (int index = 0; index < dependencies.Count; index++)
                {
                    SceneAssetReference dependency = dependencies[index];
                    if (ImGui.Selectable($"{dependency.Path} : {dependency.SubObject}##dependency{index}", index == _selectedDependency))
                        _selectedDependency = index;
                }
                ImGui.EndCombo();
            }
            if (ImGui.Button("Add Object"))
                Run(() => editor.AddObjectAtCamera(dependencies[_selectedDependency]));
            ImGui.SameLine();
        }
        if (ImGui.Button("Add Point Light"))
            Run(() => editor.AddLightAtCamera(LightType.Point));
        ImGui.SameLine();
        if (ImGui.Button("Add Spot Light"))
            Run(() => editor.AddLightAtCamera(LightType.Spot));
        ImGui.SameLine();
        if (ImGui.Button("Add Directional Light"))
            Run(() => editor.AddLightAtCamera(LightType.Directional));

        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f), _lastError);
        ImGui.End();
    }

    private void RenderHierarchy(EditorController editor)
    {
        ImGui.Begin("Hierarchy");
        ImGui.InputText("Filter", ref _filter, (nuint)256);
        RenderEntities(editor, "Objects", EditorSelectionKind.Object, editor.Scene.RenderObjects);
        RenderEntities(editor, "Reflection Probes", EditorSelectionKind.ReflectionProbe, editor.Scene.ReflectionProbes);
        if (ImGui.Button("Add scene DDGI volume"))
            Run(() => editor.AddGlobalIlluminationProbeVolumeAtCamera());
        if (editor.Scene.GlobalIlluminationProbeVolumes.Count == 0)
            ImGui.TextDisabled("No authored volumes; automatic Simple-DDGI rings remain active.");
        RenderEntities(editor, "Scene DDGI Volumes", EditorSelectionKind.GiVolume, editor.Scene.GlobalIlluminationProbeVolumes);
        RenderEntities(editor, "Foliage Prototypes", EditorSelectionKind.FoliagePrototype, editor.Scene.FoliagePrototypes);
        RenderEntities(editor, "Foliage Patches", EditorSelectionKind.FoliagePatch, editor.Scene.FoliagePatches);
        RenderEntities(editor, "Particle Effects", EditorSelectionKind.ParticleEffect, editor.Scene.ParticleEffects);
        RenderEntities(editor, "Instance Batches", EditorSelectionKind.InstanceBatch, editor.Scene.StaticInstanceBatches);
        IReadOnlyList<LightRecord> lights = editor.GetLights();
        if (ImGui.CollapsingHeader($"Lights ({lights.Count})"))
        {
            foreach (LightRecord light in lights)
            {
                string name = light.Name ?? "Light";
                if (!MatchesFilter(name, light.Id)) continue;
                bool selected = editor.Selection.Kind == EditorSelectionKind.Light && editor.Selection.LightHandle == light.Handle;
                if (ImGui.Selectable($"{name}##{light.Id}", selected))
                    editor.SelectEntity(EditorSelectionKind.Light, light.Id);
                ShowIdTooltip(light.Id);
            }
        }
        ImGui.End();
    }

    private void RenderEntities<T>(EditorController editor, string label, EditorSelectionKind kind, IReadOnlyList<T> entities)
        where T : IIdentifiedSceneEntity
    {
        if (!ImGui.CollapsingHeader($"{label} ({entities.Count})"))
            return;
        foreach (T entity in entities)
        {
            string name = DisplayName(entity);
            if (!MatchesFilter(name, entity.Id)) continue;
            bool selected = editor.Selection.Kind == kind && editor.Selection.Id == entity.Id;
            if (ImGui.Selectable($"{name}##{entity.Id}", selected))
                editor.SelectEntity(kind, entity.Id);
            ShowIdTooltip(entity.Id);
        }
    }

    private void RenderInspector(EditorController editor)
    {
        ImGui.Begin("Inspector");
        if (editor.Selection.IsEmpty)
            ImGui.Text("No selection");
        else if (editor.Selection.Kind == EditorSelectionKind.Object)
        {
            if (editor.TryGetSelectedObject(out RenderObject? target) && target != null)
                RenderObjectInspector(editor, target);
        }
        else if (editor.Selection.Kind == EditorSelectionKind.Light && editor.TryGetSelectedLight(out Light light))
            RenderLightInspector(editor, light);
        else if (editor.Selection.Kind == EditorSelectionKind.GiVolume &&
                 editor.Scene.FindById(editor.Selection.Id) is GlobalIlluminationProbeVolume volume)
            RenderGlobalIlluminationProbeVolumeInspector(editor, volume);
        else if (editor.Scene.FindById(editor.Selection.Id) is { } entity)
        {
            ImGui.Text(DisplayName(entity));
            ImGui.TextDisabled(entity.Id.ToString());
            ImGui.Separator();
            ImGui.TextWrapped("This v1 entity type is selectable and highlighted; its inspector is read-only.");
        }

        if (!editor.Selection.IsEmpty)
        {
            ImGui.Separator();
            if (ImGui.Button("Delete Selected"))
                editor.DeleteSelection();
        }
        ImGui.End();
    }

    private static void RenderGlobalIlluminationProbeVolumeInspector(
        EditorController editor,
        GlobalIlluminationProbeVolume volume)
    {
        ImGui.TextDisabled(volume.Id.ToString());
        string name = volume.Name;
        bool enabled = volume.Enabled;
        bool interior = volume.Interior;
        NumericsVector3 origin = ToNumerics(volume.Origin);
        NumericsVector3 size = ToNumerics(volume.Size);
        GlobalIlluminationProbeVolumeQualityClass qualityClass = volume.QualityClass;
        int priority = volume.Priority;
        float blendDistance = volume.BlendDistance;
        int streamingCellId = volume.StreamingCellId;
        int probeCountX = volume.ProbeCountX;
        int probeCountY = volume.ProbeCountY;
        int probeCountZ = volume.ProbeCountZ;
        int raysPerProbe = volume.RaysPerProbe;
        int maxProbeUpdates = volume.MaxProbeUpdatesPerFrame;
        int dirtyRaysPerProbe = volume.DirtyRaysPerProbe;
        int updatePriority = volume.UpdatePriority;
        float normalBias = volume.NormalBias;
        float viewBias = volume.ViewBias;
        float maxRayDistance = volume.MaxRayDistance;
        float intensity = volume.Intensity;
        float hysteresis = volume.Hysteresis;
        float steadyHysteresis = volume.SteadyHysteresis;
        float dirtyHysteresis = volume.DirtyHysteresis;

        bool changed = ImGui.InputText("Name", ref name, (nuint)256);
        changed |= ImGui.Checkbox("Enabled", ref enabled);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Interior", ref interior);
        changed |= ImGui.DragFloat3("Origin", ref origin, 0.05f);
        changed |= ImGui.DragFloat3("Size", ref size, 0.05f);
        if (ImGui.BeginCombo("Quality class", qualityClass.ToString()))
        {
            foreach (GlobalIlluminationProbeVolumeQualityClass candidate in
                     Enum.GetValues<GlobalIlluminationProbeVolumeQualityClass>())
            {
                if (ImGui.Selectable(candidate.ToString(), candidate == qualityClass))
                {
                    qualityClass = candidate;
                    changed = true;
                }
            }
            ImGui.EndCombo();
        }

        changed |= ImGui.DragInt("Priority", ref priority, 1f, -1024, 1024);
        changed |= ImGui.DragFloat("Blend distance", ref blendDistance, 0.01f, 0f, 1000f);
        changed |= ImGui.DragInt("Streaming cell", ref streamingCellId, 1f, 0, int.MaxValue);

        ImGui.SeparatorText("Probe lattice");
        changed |= ImGui.DragInt("Probe count X", ref probeCountX, 1f,
            GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Probe count Y", ref probeCountY, 1f,
            GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Probe count Z", ref probeCountZ, 1f,
            GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Rays per probe", ref raysPerProbe, 1f,
            GlobalIlluminationProbeVolume.MinRaysPerProbe,
            GlobalIlluminationProbeVolume.MaxRaysPerProbe);
        changed |= ImGui.DragInt("Dirty rays per probe", ref dirtyRaysPerProbe, 1f,
            GlobalIlluminationProbeVolume.MinRaysPerProbe,
            GlobalIlluminationProbeVolume.MaxRaysPerProbe);
        changed |= ImGui.DragInt("Max probe updates/frame", ref maxProbeUpdates, 1f, 0, 1_000_000);
        changed |= ImGui.DragInt("Update priority", ref updatePriority, 1f, 0, 1_000_000);

        ImGui.SeparatorText("Sampling and blending");
        changed |= ImGui.DragFloat("Normal bias", ref normalBias, 0.005f, 0f, 10f);
        changed |= ImGui.DragFloat("View bias", ref viewBias, 0.005f, 0f, 10f);
        changed |= ImGui.DragFloat("Max ray distance", ref maxRayDistance, 0.05f, 0.1f, 1000f);
        changed |= ImGui.DragFloat("Intensity", ref intensity, 0.01f, 0f, 16f);
        changed |= ImGui.DragFloat("Hysteresis", ref hysteresis, 0.001f, 0f, 0.999f);
        changed |= ImGui.DragFloat("Steady hysteresis", ref steadyHysteresis, 0.001f, 0f, 0.999f);
        changed |= ImGui.DragFloat("Dirty hysteresis", ref dirtyHysteresis, 0.001f, 0f, 0.999f);

        ImGui.TextDisabled($"Total probes: {volume.ProbeCount:N0}    Spacing: {volume.ProbeSpacing.X:0.###}, {volume.ProbeSpacing.Y:0.###}, {volume.ProbeSpacing.Z:0.###}");
        if (!changed)
            return;

        volume.Name = name;
        volume.Enabled = enabled;
        volume.Interior = interior;
        volume.Origin = ToCore(origin);
        volume.Size = ToCore(size);
        volume.QualityClass = qualityClass;
        volume.Priority = priority;
        volume.BlendDistance = blendDistance;
        volume.StreamingCellId = streamingCellId;
        volume.ProbeCountX = probeCountX;
        volume.ProbeCountY = probeCountY;
        volume.ProbeCountZ = probeCountZ;
        volume.RaysPerProbe = raysPerProbe;
        volume.DirtyRaysPerProbe = dirtyRaysPerProbe;
        volume.MaxProbeUpdatesPerFrame = maxProbeUpdates;
        volume.UpdatePriority = updatePriority;
        volume.NormalBias = normalBias;
        volume.ViewBias = viewBias;
        volume.MaxRayDistance = maxRayDistance;
        volume.Intensity = intensity;
        volume.Hysteresis = hysteresis;
        volume.SteadyHysteresis = steadyHysteresis;
        volume.DirtyHysteresis = dirtyHysteresis;
        editor.MarkDirty(editor.Selection);
    }

    private void RenderObjectInspector(EditorController editor, RenderObject target)
    {
        ImGui.TextDisabled(target.Id.ToString());
        string name = target.Name;
        bool visible = target.Visible;
        bool isStatic = target.IsStatic;
        NumericsVector3 position = ToNumerics(target.Position);
        NumericsVector3 rotationDegrees = ToNumerics(target.Rotation.ToEulerAngles()) * (180f / MathF.PI);
        NumericsVector3 scale = ToNumerics(target.Scale);
        bool changed = ImGui.InputText("Name", ref name, (nuint)256);
        changed |= ImGui.Checkbox("Visible", ref visible);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Static", ref isStatic);
        changed |= ImGui.DragFloat3("Position", ref position, 0.05f);
        changed |= ImGui.DragFloat3("Rotation (degrees)", ref rotationDegrees, 0.25f);
        changed |= ImGui.DragFloat3("Scale", ref scale, 0.02f);
        if (changed)
        {
            editor.UpdateSelectedObject(
                name,
                visible,
                isStatic,
                ToCore(position),
                new CoreQuaternion(ToCore(rotationDegrees * (MathF.PI / 180f))),
                ToCore(scale));
        }

        if (editor.TryGetSelectedMaterial(out GPUMaterialData material))
        {
            ImGui.SeparatorText("Material");
            RenderMaterialInspector(editor, material);
        }
    }

    private void RenderLightInspector(EditorController editor, Light light)
    {
        string name = editor.GetLights().FirstOrDefault(item => item.Handle == editor.Selection.LightHandle).Name ?? "Light";
        if (ImGui.InputText("Name", ref name, (nuint)256))
            editor.SetSelectedLightName(name);

        if (ImGui.BeginCombo("Type", light.Type.ToString()))
        {
            foreach (LightType type in Enum.GetValues<LightType>())
            {
                if (ImGui.Selectable(type.ToString(), type == light.Type))
                {
                    light.Type = type;
                    editor.UpdateSelectedLight(light);
                }
            }
            ImGui.EndCombo();
        }

        NumericsVector3 position = light.Position;
        NumericsVector3 direction = light.Direction;
        NumericsVector3 color = light.Color;
        float intensity = light.Intensity;
        float range = light.Range;
        float spotDegrees = light.SpotAngle * 180f / MathF.PI;
        float shadowStrength = light.ShadowStrength;
        float shadowNear = light.ShadowNearPlane;
        float shadowFar = light.ShadowFarPlane;
        bool shadows = light.CastsShadows;
        bool changed = ImGui.DragFloat3("Position", ref position, 0.05f);
        changed |= ImGui.DragFloat3("Direction", ref direction, 0.01f);
        if (ImGui.Button("Set from camera") && editor.Camera != null)
        {
            position = ToNumerics(editor.Camera.Position);
            direction = ToNumerics(editor.Camera.Forward);
            changed = true;
        }
        changed |= ImGui.ColorEdit3("Color", ref color);
        changed |= ImGui.DragFloat("Intensity", ref intensity, 0.05f, 0f, 100000f);
        changed |= ImGui.DragFloat("Range", ref range, 0.05f, 0.01f, 100000f);
        if (light.Type == LightType.Spot)
            changed |= ImGui.DragFloat("Spot angle", ref spotDegrees, 0.25f, 1f, 179f);
        changed |= ImGui.Checkbox("Casts shadows", ref shadows);
        changed |= ImGui.DragFloat("Shadow strength", ref shadowStrength, 0.01f, 0f, 1f);
        changed |= ImGui.DragFloat("Shadow near", ref shadowNear, 0.01f, 0.001f, 1000f);
        changed |= ImGui.DragFloat("Shadow far", ref shadowFar, 0.1f, 0.01f, 100000f);
        if (changed)
        {
            light.Position = position;
            light.Direction = direction.LengthSquared() > 0f ? NumericsVector3.Normalize(direction) : -NumericsVector3.UnitY;
            light.Color = color;
            light.Intensity = Math.Max(0f, intensity);
            light.Range = Math.Max(0.01f, range);
            light.SpotAngle = spotDegrees * MathF.PI / 180f;
            light.CastsShadows = shadows;
            light.ShadowStrength = Math.Clamp(shadowStrength, 0f, 1f);
            light.ShadowNearPlane = Math.Max(0.001f, shadowNear);
            light.ShadowFarPlane = Math.Max(light.ShadowNearPlane + 0.001f, shadowFar);
            editor.UpdateSelectedLight(light);
        }
    }

    private void RenderMaterialInspector(EditorController editor, GPUMaterialData material)
    {
        NumericsVector3 albedo = new(material.Albedo.X, material.Albedo.Y, material.Albedo.Z);
        NumericsVector3 emissive = new(material.Emissive.X, material.Emissive.Y, material.Emissive.Z);
        float emissiveStrength = Math.Max(emissive.X, Math.Max(emissive.Y, emissive.Z));
        NumericsVector3 emissiveColor = emissiveStrength > 0.00001f ? emissive / emissiveStrength : NumericsVector3.Zero;
        float metallic = material.MetallicRoughnessAO.X;
        float roughness = material.MetallicRoughnessAO.Y;
        float normalScale = material.NormalScaleBias.X;
        float alphaCutoff = material.NormalScaleBias.Z;
        bool changed = ImGui.ColorEdit3("Albedo", ref albedo);
        bool emissiveColorChanged = ImGui.ColorEdit3("Emissive", ref emissiveColor);
        bool emissiveStrengthChanged = ImGui.DragFloat("Emissive strength", ref emissiveStrength, 0.01f, 0f, 100000f);
        changed |= emissiveColorChanged || emissiveStrengthChanged;
        changed |= ImGui.DragFloat("Metallic", ref metallic, 0.01f, 0f, 1f);
        changed |= ImGui.DragFloat("Roughness", ref roughness, 0.01f, 0.04f, 1f);
        changed |= ImGui.DragFloat("Normal scale", ref normalScale, 0.01f, 0f, 4f);
        changed |= ImGui.DragFloat("Alpha cutoff", ref alphaCutoff, 0.01f, 0f, 1f);
        ImGui.TextDisabled($"Textures: albedo {material.AlbedoTextureIndex}, normal {material.NormalTextureIndex}, MR {material.MetallicRoughnessTextureIndex}, emissive {material.EmissiveTextureIndex}");
        if (!changed) return;
        material.Albedo = new CoreVector4(albedo.X, albedo.Y, albedo.Z, material.Albedo.W);
        emissive = emissiveColor * Math.Max(0f, emissiveStrength);
        material.Emissive = new CoreVector4(emissive.X, emissive.Y, emissive.Z, material.Emissive.W);
        material.MetallicRoughnessAO.X = Math.Clamp(metallic, 0f, 1f);
        material.MetallicRoughnessAO.Y = Math.Clamp(roughness, 0.04f, 1f);
        material.NormalScaleBias.X = Math.Max(0f, normalScale);
        material.NormalScaleBias.Z = Math.Clamp(alphaCutoff, 0f, 1f);
        Run(() => editor.UpdateSelectedMaterial(material));
    }

    private bool MatchesFilter(string name, Guid id) => string.IsNullOrWhiteSpace(_filter) ||
        name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
        id.ToString().Contains(_filter, StringComparison.OrdinalIgnoreCase);

    private static void ShowIdTooltip(Guid id)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(id.ToString());
    }

    private void Run(Action action)
    {
        try { action(); _lastError = null; }
        catch (Exception error) { _lastError = error.Message; }
    }

    private static string DisplayName(IIdentifiedSceneEntity entity) => entity switch
    {
        RenderObject value => value.Name,
        ReflectionProbe value => value.Name,
        GlobalIlluminationProbeVolume value => value.Name,
        ParticleEffectInstance value => value.Name,
        StaticInstanceBatch value => value.Name,
        Njulf.Core.Foliage.FoliagePrototype value => value.Name,
        Njulf.Core.Foliage.FoliagePatch value => value.Name,
        _ => entity.Id.ToString()
    };

    private static NumericsVector3 ToNumerics(CoreVector3 value) => new(value.X, value.Y, value.Z);
    private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);
}
