using Hexa.NET.ImGui;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NumericsVector3 = System.Numerics.Vector3;
using CoreQuaternion = Njulf.Core.Math.Quaternion;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Editor;

/// <summary>Dockable editor shell: save/add tools, hierarchy, and live inspectors.</summary>
public sealed class EditorImGuiPanels
{
    private const float MaximumEditableEmissionStrength = 65_504f;
    private string _filter = string.Empty;
    private string _lightFilter = string.Empty;
    private LightType _newLightType = LightType.Point;
    private Guid _iesEditingLight;
    private string _iesProfilePath = string.Empty;
    private string _saveAsPath = string.Empty;
    private string? _lastError;
    private int _selectedDependency;
    private Guid _materialEditingObject;
    private readonly string[] _materialTexturePaths = new string[5];
    private readonly EditorDockLayout _dockLayout = new();
    private readonly GlobalIlluminationEditorPanel _globalIlluminationPanel = new();
    private readonly MaterialEditorPanel _materialPanel = new();
    private readonly RenderingSettingsEditorPanel _renderingSettingsPanel = new();
    private readonly ShadowEditorPanel _shadowPanel = new();

    public void Render(EditorController editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (!editor.Enabled)
            return;

        _dockLayout.Render();
        RenderMainMenu(editor);
        RenderHierarchy(editor);
        RenderInspector(editor);
        RenderSceneLights(editor);
        _renderingSettingsPanel.Render(editor);
        _globalIlluminationPanel.Render(editor);
        _materialPanel.Render(editor);
        _shadowPanel.Render(editor);
    }

    private void RenderMainMenu(EditorController editor)
    {
        if (!ImGui.Begin(EditorDockLayout.SceneManagementWindow))
        {
            ImGui.End();
            return;
        }
        ImGui.Text(editor.IsDirty ? "Scene *" : "Scene");
        ImGui.BeginDisabled(editor.Gizmos.IsDragging);
        if (ImGui.RadioButton("Move", editor.Gizmos.Mode == GizmoMode.Move)) editor.Gizmos.Mode = GizmoMode.Move;
        ImGui.SameLine();
        if (ImGui.RadioButton("Rotate", editor.Gizmos.Mode == GizmoMode.Rotate)) editor.Gizmos.Mode = GizmoMode.Rotate;
        ImGui.SameLine();
        if (ImGui.RadioButton("Scale", editor.Gizmos.Mode == GizmoMode.Scale)) editor.Gizmos.Mode = GizmoMode.Scale;
        if (ImGui.RadioButton("World", editor.Gizmos.Space == GizmoSpace.World)) editor.Gizmos.Space = GizmoSpace.World;
        ImGui.SameLine();
        if (ImGui.RadioButton("Local", editor.Gizmos.Space == GizmoSpace.Local)) editor.Gizmos.Space = GizmoSpace.Local;
        ImGui.EndDisabled();
        ImGui.TextDisabled("Drag a handle; Escape cancels. Scale uses local axes.");
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
                    if (ImGui.Selectable($"{dependency.Path} : {dependency.SubObject}##dependency{index}",
                            index == _selectedDependency))
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
        if (ImGui.Button("Add Rectangle Light"))
            Run(() => editor.AddLightAtCamera(LightType.Rectangle));
        ImGui.SameLine();
        if (ImGui.Button("Add Disk Light"))
            Run(() => editor.AddLightAtCamera(LightType.Disk));
        ImGui.SameLine();
        if (ImGui.Button("Add Tube Light"))
            Run(() => editor.AddLightAtCamera(LightType.Tube));

        ImGui.SeparatorText("Imported model lights");
        ImportedModelLightEditorStatus importedLights =
            editor.GetImportedModelLightStatus();
        RenderImportedShadowToggle(editor);
        bool importedLightsEnabled = importedLights.Enabled;
        if (ImGui.Checkbox(
                "Enable all imported model lights (except directional)",
                ref importedLightsEnabled))
        {
            Run(() => editor.SetImportedModelLightsEnabled(
                importedLightsEnabled));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Enables or disables imported lights except directional lights. " +
                "Use the separate directional light toggle to replace the scene sun.");
        }

        bool importedDirectionalEnabled = importedLights.DirectionalEnabled;
        ImGui.BeginDisabled(importedLights.DirectionalDefinitionCount == 0 &&
                            !importedDirectionalEnabled);
        if (ImGui.Checkbox("Use imported directional light", ref importedDirectionalEnabled))
            Run(() => editor.SetImportedDirectionalLightEnabled(importedDirectionalEnabled));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Replaces the existing directional light with one imported directional light. " +
                "Turning this off restores the existing light. Independent of the bulk toggle.");
        }

        ImGui.EndDisabled();
        if (importedLights.DirectionalDefinitionCount > 1)
            ImGui.TextDisabled("Multiple imported directional lights found; only one is used.");
        if (importedLights.ZeroIntensityDefinitionCount > 0)
        {
            ImGui.TextDisabled(
                $"{importedLights.ZeroIntensityDefinitionCount} lights have zero source intensity; " +
                "enabling uses default brightness.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Default intensity: local lights 100, directional light 1. " +
                                 "Positive source intensities are preserved.");
        }

        ImGui.TextDisabled(
            $"{importedLights.ActiveLightCount} active lights across " +
            $"{importedLights.ModelPlacementsWithLightsCount}/" +
            $"{importedLights.ModelPlacementCount} model placements");
        if (importedLights.ModelPlacementCount > 0 &&
            importedLights.ImportedLightDefinitionCount == 0)
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.75f, 0.2f, 1f),
                "No imported lights found; recook/reimport the model assets.");
        }

        if (!string.IsNullOrWhiteSpace(importedLights.Error))
        {
            ImGui.TextColored(
                new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f),
                importedLights.Error);
        }

        if (editor.RendererSettings is { } renderSettings)
        {
            ImGui.SeparatorText("Environment");
            bool animateSun = editor.EditableEnvironment.AnimateTimeOfDay;
            if (ImGui.Checkbox("Animate sun (direct light)", ref animateSun))
            {
                editor.EditableEnvironment.AnimateTimeOfDay = animateSun;
                editor.CommitEnvironmentEdits();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Advances the procedural sky clock and rotates its directional sun.");
        }

        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f), _lastError);
        ImGui.End();
    }

    private void RenderHierarchy(EditorController editor)
    {
        if (!ImGui.Begin(EditorDockLayout.HierarchyWindow))
        {
            ImGui.End();
            return;
        }
        ImGui.InputText("Filter", ref _filter, (nuint)256);
        RenderEntities(editor, "Objects", EditorSelectionKind.Object, editor.Scene.RenderObjects);
        RenderEntities(editor, "Reflection Probes", EditorSelectionKind.ReflectionProbe, editor.Scene.ReflectionProbes);
        if (ImGui.Button("Add scene DDGI volume"))
            Run(() => editor.AddGlobalIlluminationProbeVolumeAtCamera());
        ImGui.SameLine();
        ImGui.TextDisabled($"Authored volumes: {editor.Scene.GlobalIlluminationProbeVolumes.Count}");
        RenderEntities(editor, "Scene DDGI Volumes", EditorSelectionKind.GiVolume,
            editor.Scene.GlobalIlluminationProbeVolumes);
        RenderEntities(editor, "Foliage Prototypes", EditorSelectionKind.FoliagePrototype,
            editor.Scene.FoliagePrototypes);
        RenderEntities(editor, "Foliage Patches", EditorSelectionKind.FoliagePatch, editor.Scene.FoliagePatches);
        RenderEntities(editor, "Particle Effects", EditorSelectionKind.ParticleEffect, editor.Scene.ParticleEffects);
        RenderEntities(editor, "Instance Batches", EditorSelectionKind.InstanceBatch,
            editor.Scene.StaticInstanceBatches);
        IReadOnlyList<LightRecord> lights = editor.GetLights();
        if (ImGui.CollapsingHeader($"Scene Lights ({lights.Count})"))
        {
            foreach (LightRecord light in lights)
            {
                string name = light.Name ?? "Light";
                if (!MatchesFilter(name, light.Id)) continue;
                bool selected = editor.Selection.Kind == EditorSelectionKind.Light && editor.Selection.Id == light.Id;
                if (ImGui.Selectable($"{name}##{light.Id}", selected))
                    editor.SelectEntity(EditorSelectionKind.Light, light.Id);
                ShowIdTooltip(light.Id);
            }
        }

        ImGui.End();
    }

    private void RenderEntities<T>(EditorController editor, string label, EditorSelectionKind kind,
        IReadOnlyList<T> entities)
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
        if (!ImGui.Begin(EditorDockLayout.InspectorWindow))
        {
            ImGui.End();
            return;
        }
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
            ImGui.BeginDisabled(editor.Selection.Kind == EditorSelectionKind.Light &&
                                (editor.SelectedLightIsImported || editor.IsSceneLightSuspended(editor.Selection.Id)));
            if (ImGui.Button("Delete Selected"))
                editor.DeleteSelection();
            ImGui.EndDisabled();
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
        GlobalIlluminationProbeVolumeQualityClass quality = volume.QualityClass;
        int priority = volume.Priority;
        float blendDistance = volume.BlendDistance;
        int streamingCell = volume.StreamingCellId;
        int countX = volume.ProbeCountX;
        int countY = volume.ProbeCountY;
        int countZ = volume.ProbeCountZ;
        int rays = volume.RaysPerProbe;
        int dirtyRays = volume.DirtyRaysPerProbe;
        int maxUpdates = volume.MaxProbeUpdatesPerFrame;
        int updatePriority = volume.UpdatePriority;
        float normalBias = volume.NormalBias;
        float viewBias = volume.ViewBias;
        float maxDistance = volume.MaxRayDistance;
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
        if (ImGui.BeginCombo("Quality class", quality.ToString()))
        {
            foreach (GlobalIlluminationProbeVolumeQualityClass candidate in
                     Enum.GetValues<GlobalIlluminationProbeVolumeQualityClass>())
            {
                if (ImGui.Selectable(candidate.ToString(), candidate == quality))
                {
                    quality = candidate;
                    changed = true;
                }
            }

            ImGui.EndCombo();
        }

        changed |= ImGui.DragInt("Priority", ref priority, 1f, -1024, 1024);
        changed |= ImGui.DragFloat("Blend distance", ref blendDistance, 0.01f, 0f, 1000f);
        changed |= ImGui.DragInt("Streaming cell", ref streamingCell, 1f, 0, int.MaxValue);
        ImGui.SeparatorText("Probe lattice");
        changed |= ImGui.DragInt("Probe count X", ref countX, 1f, GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Probe count Y", ref countY, 1f, GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Probe count Z", ref countZ, 1f, GlobalIlluminationProbeVolume.MinProbeCountPerAxis,
            GlobalIlluminationProbeVolume.MaxProbeCountPerAxis);
        changed |= ImGui.DragInt("Rays per probe", ref rays, 1f, GlobalIlluminationProbeVolume.MinRaysPerProbe,
            GlobalIlluminationProbeVolume.MaxRaysPerProbe);
        changed |= ImGui.DragInt("Dirty rays per probe", ref dirtyRays, 1f,
            GlobalIlluminationProbeVolume.MinRaysPerProbe, GlobalIlluminationProbeVolume.MaxRaysPerProbe);
        changed |= ImGui.DragInt("Max probe updates/frame", ref maxUpdates, 1f, 0, 1_000_000);
        changed |= ImGui.DragInt("Update priority", ref updatePriority, 1f, 0, 1_000_000);
        ImGui.SeparatorText("Sampling and blending");
        changed |= ImGui.DragFloat("Normal bias", ref normalBias, 0.005f, 0f, 10f);
        changed |= ImGui.DragFloat("View bias", ref viewBias, 0.005f, 0f, 10f);
        changed |= ImGui.DragFloat("Max ray distance", ref maxDistance, 0.05f, 0.1f, 1000f);
        changed |= ImGui.DragFloat("Intensity", ref intensity, 0.01f, 0f, 16f);
        changed |= ImGui.DragFloat("Hysteresis", ref hysteresis, 0.001f, 0f, 0.999f);
        changed |= ImGui.DragFloat("Steady hysteresis", ref steadyHysteresis, 0.001f, 0f, 0.999f);
        changed |= ImGui.DragFloat("Dirty hysteresis", ref dirtyHysteresis, 0.001f, 0f, 0.999f);
        ImGui.TextDisabled(
            $"Total probes: {volume.ProbeCount:N0}    Spacing: {volume.ProbeSpacing.X:0.###}, {volume.ProbeSpacing.Y:0.###}, {volume.ProbeSpacing.Z:0.###}");

        if (!changed)
            return;
        volume.Name = name;
        volume.Enabled = enabled;
        volume.Interior = interior;
        volume.Origin = ToCore(origin);
        volume.Size = ToCore(size);
        volume.QualityClass = quality;
        volume.Priority = priority;
        volume.BlendDistance = blendDistance;
        volume.StreamingCellId = streamingCell;
        volume.ProbeCountX = countX;
        volume.ProbeCountY = countY;
        volume.ProbeCountZ = countZ;
        volume.RaysPerProbe = rays;
        volume.DirtyRaysPerProbe = dirtyRays;
        volume.MaxProbeUpdatesPerFrame = maxUpdates;
        volume.UpdatePriority = updatePriority;
        volume.NormalBias = normalBias;
        volume.ViewBias = viewBias;
        volume.MaxRayDistance = maxDistance;
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

        if (editor.TryGetSelectedMaterialInspection(out EditorMaterialInspection? inspection) &&
            inspection != null)
        {
            ImGui.SeparatorText("Material");
            RenderMaterialInspector(editor, inspection);
        }
    }

    private void RenderImportedShadowToggle(EditorController editor)
    {
        bool enabled = editor.GetImportedModelLightStatus().ShadowsEnabled;
        if (ImGui.Checkbox("Enable shadows on all imported lights", ref enabled))
            Run(() => editor.SetImportedModelLightShadowsEnabled(enabled));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Enables shadows for current and subsequently enabled imported lights, including the imported sun. " +
                "Also enables the required renderer passes, respecting configured point/spot count and memory limits. " +
                "Turning this off restores individual shadow settings and the previous renderer gates.");
        if (enabled && editor.RendererDiagnostics is { } diagnostics)
        {
            ImGui.TextDisabled(
                $"Scene shadow selection: point {diagnostics.PointShadowSelectedCount}/{diagnostics.PointShadowCandidateCount}, " +
                $"spot {diagnostics.SpotShadowSelectedCount}/{diagnostics.SpotShadowCandidateCount}, " +
                $"area {diagnostics.AreaShadowSelectedCount}/{diagnostics.AreaShadowCandidateCount}");
            if (diagnostics.PointShadowRejectedByBudgetCount + diagnostics.SpotShadowRejectedByBudgetCount > 0)
                ImGui.TextWrapped(
                    "Some lights exceed the configured count or memory budget. Adjust the limits in Shadows; per-light status shows the reason.");
            ImGui.TextDisabled(
                $"{diagnostics.LocalShadowDowngradedCount} reduced resolutions; {diagnostics.LocalShadowCacheHitCount} cached maps reused.");
        }
    }

    private void RenderSceneLights(EditorController editor)
    {
        if (!ImGui.Begin(EditorDockLayout.SceneLightsWindow))
        {
            ImGui.End();
            return;
        }
        RenderImportedShadowToggle(editor);
        IReadOnlyList<LightRecord> lights = editor.GetLights();
        ImGui.Text($"{lights.Count} lights in the scene");
        ImGui.InputText("Filter lights", ref _lightFilter, (nuint)256);
        if (ImGui.BeginCombo("New light type", _newLightType.ToString()))
        {
            foreach (LightType type in Enum.GetValues<LightType>())
                if (ImGui.Selectable(type.ToString(), type == _newLightType))
                    _newLightType = type;
            ImGui.EndCombo();
        }

        if (ImGui.Button("Add light at camera"))
            Run(() => editor.AddLightAtCamera(_newLightType));
        if (ImGui.BeginTable("SceneLightList", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
                new System.Numerics.Vector2(0f, 220f)))
        {
            ImGui.TableSetupColumn("Name");
            ImGui.TableSetupColumn("Type");
            ImGui.TableSetupColumn("Source");
            ImGui.TableSetupColumn("Intensity");
            ImGui.TableSetupColumn("Shadows");
            ImGui.TableHeadersRow();
            foreach (LightRecord record in lights)
            {
                string name = record.Name ?? "Light";
                string source = editor.IsSceneLightImported(record.Id) ? "Imported" :
                    editor.IsSceneLightSuspended(record.Id) ? "Suspended" : "Scene";
                string searchable = $"{name} {record.Light.Type} {source} {record.Id}";
                if (!string.IsNullOrWhiteSpace(_lightFilter) &&
                    !searchable.Contains(_lightFilter, StringComparison.OrdinalIgnoreCase)) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{name}##{record.Id}",
                        editor.Selection.Kind == EditorSelectionKind.Light && editor.Selection.Id == record.Id,
                        ImGuiSelectableFlags.SpanAllColumns))
                    editor.SelectEntity(EditorSelectionKind.Light, record.Id);
                ShowIdTooltip(record.Id);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(record.Light.Type.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(source);
                ImGui.TableNextColumn();
                ImGui.Text($"{record.Light.Intensity:G5}");
                ImGui.TableNextColumn();
                var shadowState = editor.RendererDiagnostics?.LocalShadowLights.FirstOrDefault(x =>
                    x.Identity == LightManager.GetStableIdentity(record.Handle));
                ImGui.TextUnformatted(shadowState == null ? (record.Light.CastsShadows ? "On" : "Off") :
                    shadowState.Resident ? $"{shadowState.EffectiveResolution}px - {shadowState.Status}" :
                    shadowState.Status);
            }

            ImGui.EndTable();
        }

        if (editor.RendererSettings is { } settings && settings.Environment.Enabled)
        {
            if (ImGui.BeginCombo("Sun control", settings.Environment.SunDriver.ToString()))
            {
                foreach (ProceduralSkySunDriver driver in Enum.GetValues<ProceduralSkySunDriver>())
                    if (ImGui.Selectable(driver.ToString(), settings.Environment.SunDriver == driver))
                        settings.Environment.SunDriver = driver;
                ImGui.EndCombo();
            }

            if (settings.Environment.SunDriver != ProceduralSkySunDriver.SceneDirectionalLight)
                ImGui.TextWrapped("The procedural sky drives sun and moon brightness and direction. " +
                                  "Choose SceneDirectionalLight for manual sun control. The imported sun toggle takes precedence.");
        }

        ImGui.SeparatorText("Selected light");
        if (editor.TryGetSelectedLight(out Light light))
        {
            RenderLightInspector(editor, light);
            if (!editor.SelectedLightIsImported && !editor.IsSceneLightSuspended(editor.Selection.Id) &&
                ImGui.Button("Delete light"))
                Run(() => editor.DeleteSelection());
        }
        else
            ImGui.TextWrapped(
                "Select a light above. Imported lights appear when enabled in the imported model lights controls.");

        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.35f, 0.25f, 1f), _lastError);
        ImGui.End();
    }

    private void RenderLightInspector(EditorController editor, Light light)
    {
        string name = editor.GetLights().FirstOrDefault(item => item.Id == editor.Selection.Id).Name ?? "Light";
        if (editor.IsSceneLightSuspended(editor.Selection.Id))
            ImGui.TextWrapped("Temporarily replaced by the imported sun. Edits apply when you turn off " +
                              "Use imported directional light. Restore it before changing its type or deleting it.");
        if (editor.SelectedLightIsImported)
        {
            ImGui.TextWrapped("Imported light: edits are saved with the scene. Set intensity to zero to turn it off. " +
                              "Edited positions and directions are in world space.");
            if (editor.SelectedLightHasOverrides && ImGui.Button("Reset imported light to asset values"))
            {
                Run(editor.ResetSelectedLightOverrides);
                return;
            }
        }

        if (ImGui.InputText("Name", ref name, (nuint)256))
            Run(() => editor.SetSelectedLightName(name));

        if (ImGui.BeginCombo("Type", light.Type.ToString()))
        {
            foreach (LightType type in Enum.GetValues<LightType>())
            {
                if (editor.IsSceneLightSuspended(editor.Selection.Id) && type != LightType.Directional) continue;
                if (editor.SelectedLightIsImported &&
                    (type == LightType.Directional) != (light.Type == LightType.Directional)) continue;
                if (ImGui.Selectable(type.ToString(), type == light.Type))
                {
                    light.Type = type;
                    if (!AnalyticalLightGeometry.IsPunctual(type))
                        light.PhotometricProfile = default;
                    if (type == LightType.Spot)
                    {
                        light.SpotAngle = Math.Clamp(light.SpotAngle, 0.01f, MathF.PI / 2f - 0.001f);
                        light.InnerSpotAngle = Math.Clamp(light.InnerSpotAngle, 0f, light.SpotAngle);
                    }

                    if (AnalyticalLightGeometry.IsArea(type))
                    {
                        System.Numerics.Vector2 defaults = type == LightType.Tube
                            ? new System.Numerics.Vector2(2f, 0.25f)
                            : System.Numerics.Vector2.One;
                        light.Size = new System.Numerics.Vector2(
                            float.IsFinite(light.Size.X) && light.Size.X > 0f
                                ? light.Size.X
                                : defaults.X,
                            float.IsFinite(light.Size.Y) && light.Size.Y > 0f
                                ? light.Size.Y
                                : defaults.Y);
                        if (type == LightType.Disk)
                            light.Size.Y = light.Size.X;

                        NumericsVector3 selectedDirection =
                            IsFiniteNonZero(light.Direction)
                                ? NumericsVector3.Normalize(light.Direction)
                                : -NumericsVector3.UnitY;
                        NumericsVector3 selectedUp = IsFiniteNonZero(light.Up)
                            ? NumericsVector3.Normalize(light.Up)
                            : NumericsVector3.UnitZ;
                        var orientation = new Light
                        {
                            Direction = selectedDirection,
                            Up = selectedUp
                        };
                        if (AnalyticalLightGeometry.TryGetFrame(
                                orientation,
                                out NumericsVector3 axis,
                                out NumericsVector3 frameUp,
                                out _))
                        {
                            light.Direction = axis;
                            light.Up = frameUp;
                        }
                    }

                    Run(() => editor.UpdateSelectedLight(light));
                }
            }

            ImGui.EndCombo();
        }

        NumericsVector3 position = light.Position;
        NumericsVector3 direction = light.Direction;
        NumericsVector3 up = light.Up;
        System.Numerics.Vector2 size = light.Size;
        NumericsVector3 color = light.Color;
        float intensity = light.Intensity;
        float range = light.Range;
        float spotDegrees = light.SpotAngle * 180f / MathF.PI;
        float innerSpotDegrees = light.InnerSpotAngle * 180f / MathF.PI;
        LightAttenuationMode attenuation = light.AttenuationMode;
        float attenuationConstant = light.AttenuationConstant;
        float attenuationLinear = light.AttenuationLinear;
        float attenuationQuadratic = light.AttenuationQuadratic;
        int shadowResolution = (int)light.ShadowMapSizeOverride;
        int shadowPriority = light.ShadowPriority;
        float iesRotationDegrees = light.IesRotationRadians * 180f / MathF.PI;
        float shadowStrength = light.ShadowStrength;
        float shadowNear = light.ShadowNearPlane;
        float shadowFar = light.ShadowFarPlane;
        bool shadows = light.CastsShadows;
        bool twoSided = light.TwoSided;
        bool changed = ImGui.DragFloat3("Position", ref position, 0.05f);
        changed |= ImGui.DragFloat3("Direction", ref direction, 0.01f);
        if (AnalyticalLightGeometry.IsArea(light.Type))
            changed |= ImGui.DragFloat3("Up", ref up, 0.01f);
        if (ImGui.Button("Set from camera") && editor.Camera != null)
        {
            position = ToNumerics(editor.Camera.Position);
            direction = ToNumerics(editor.Camera.Forward);
            changed = true;
        }

        changed |= ImGui.ColorEdit3("Color", ref color);
        changed |= ImGui.DragFloat("Intensity", ref intensity, 0.05f, 0f, 100000f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Set to zero to turn off this light's output.");
        if (light.Type != LightType.Directional)
            changed |= ImGui.DragFloat("Range", ref range, 0.05f, 0.01f, 100000f);
        if (light.Type == LightType.Spot)
        {
            changed |= ImGui.DragFloat("Outer cone half-angle (degrees)", ref spotDegrees, 0.25f, 0.01f, 89.9f);
            changed |= ImGui.DragFloat("Inner cone half-angle (degrees)", ref innerSpotDegrees, 0.25f, 0f, spotDegrees);
        }

        if (AnalyticalLightGeometry.IsPunctual(light.Type))
        {
            if (ImGui.BeginCombo("Attenuation", attenuation.ToString()))
            {
                foreach (LightAttenuationMode mode in Enum.GetValues<LightAttenuationMode>())
                    if (ImGui.Selectable(mode.ToString(), mode == attenuation))
                    {
                        attenuation = mode;
                        changed = true;
                    }

                ImGui.EndCombo();
            }

            if (attenuation == LightAttenuationMode.Polynomial)
            {
                changed |= ImGui.DragFloat("Constant attenuation", ref attenuationConstant, 0.01f, 0f, 100000f);
                changed |= ImGui.DragFloat("Linear attenuation", ref attenuationLinear, 0.01f, 0f, 100000f);
                changed |= ImGui.DragFloat("Quadratic attenuation", ref attenuationQuadratic, 0.01f, 0f, 100000f);
            }
        }

        if (AnalyticalLightGeometry.IsArea(light.Type))
        {
            string sizeLabel = light.Type switch
            {
                LightType.Rectangle => "Width / height",
                LightType.Disk => "Diameter",
                _ => "Length / diameter"
            };
            changed |= ImGui.DragFloat2(sizeLabel, ref size, 0.01f, 0.001f, 100000f);
            if (light.Type == LightType.Disk && size.X != size.Y)
                size.Y = size.X;
            if (light.Type is LightType.Rectangle or LightType.Disk)
                changed |= ImGui.Checkbox("Two-sided", ref twoSided);
        }

        bool forcedImportedShadows = editor.SelectedLightIsImported &&
                                     editor.GetImportedModelLightStatus().ShadowsEnabled;
        ImGui.BeginDisabled(forcedImportedShadows);
        changed |= ImGui.Checkbox("Casts shadows", ref shadows);
        ImGui.EndDisabled();
        if (forcedImportedShadows)
            ImGui.TextDisabled("Shadows are enabled by the imported lights toggle.");
        changed |= ImGui.DragFloat("Shadow strength", ref shadowStrength, 0.01f, 0f, 1f);
        changed |= ImGui.DragFloat("Shadow near", ref shadowNear, 0.01f, 0.001f, 1000f);
        changed |= ImGui.DragFloat("Shadow far", ref shadowFar, 0.1f, 0.01f, 100000f);
        if (light.Type is LightType.Point or LightType.Spot)
        {
            if (ImGui.BeginCombo("Shadow map resolution",
                    shadowResolution == 0 ? "Use default" : $"{shadowResolution}px"))
            {
                foreach (int resolution in new[] { 0, 128, 256, 512, 1024, 2048 })
                    if (ImGui.Selectable(resolution == 0 ? "Use default" : $"{resolution}px",
                            shadowResolution == resolution))
                    {
                        shadowResolution = resolution;
                        changed = true;
                    }

                ImGui.EndCombo();
            }

            var selectedRecord = editor.GetLights().FirstOrDefault(x => x.Id == editor.Selection.Id);
            var state = editor.RendererDiagnostics?.LocalShadowLights.FirstOrDefault(x =>
                x.Identity == LightManager.GetStableIdentity(selectedRecord.Handle));
            if (state != null)
                ImGui.TextWrapped(
                    $"Requested {state.RequestedResolution}px; effective {state.EffectiveResolution}px. {state.Status}" +
                    (state.Resident && state.EffectiveResolution < state.RequestedResolution
                        ? " (reduced to fit memory budget)"
                        : ""));
        }
        else changed |= ImGui.DragInt("Shadow map resolution (0 = default)", ref shadowResolution, 64f, 0, 16384);

        changed |= ImGui.DragInt("Shadow priority", ref shadowPriority, 1f, -10000, 10000);
        if (AnalyticalLightGeometry.IsPunctual(light.Type))
        {
            if (_iesEditingLight != editor.Selection.Id)
            {
                _iesEditingLight = editor.Selection.Id;
                _iesProfilePath = editor.GetSelectedLightDocument()?.IesProfile?.Path ?? string.Empty;
            }

            ImGui.InputText("IES profile path", ref _iesProfilePath, (nuint)1024);
            if (ImGui.Button("Load IES profile"))
                Run(() => editor.SetSelectedLightIesProfile(_iesProfilePath));
            ImGui.SameLine();
            if (ImGui.Button("Clear IES profile"))
            {
                Run(() => editor.SetSelectedLightIesProfile(null));
                _iesProfilePath = string.Empty;
            }

            changed |= ImGui.DragFloat("IES rotation (degrees)", ref iesRotationDegrees, 1f, -360f, 360f);
        }

        if (changed)
        {
            light.Position = position;
            NumericsVector3 resolvedDirection = direction.LengthSquared() > 0f
                ? NumericsVector3.Normalize(direction)
                : -NumericsVector3.UnitY;
            NumericsVector3 resolvedUp = up.LengthSquared() > 0f
                ? NumericsVector3.Normalize(up)
                : NumericsVector3.UnitZ;
            if (AnalyticalLightGeometry.IsArea(light.Type))
            {
                var orientation = new Light
                {
                    Direction = resolvedDirection,
                    Up = resolvedUp
                };
                if (AnalyticalLightGeometry.TryGetFrame(
                        orientation,
                        out NumericsVector3 axis,
                        out NumericsVector3 frameUp,
                        out _))
                {
                    resolvedDirection = axis;
                    resolvedUp = frameUp;
                }
            }

            light.Direction = resolvedDirection;
            light.Up = resolvedUp;
            light.Size = System.Numerics.Vector2.Max(size, new System.Numerics.Vector2(0.001f));
            if (light.Type == LightType.Disk)
                light.Size.Y = light.Size.X;
            light.TwoSided = twoSided;
            light.Color = color;
            light.Intensity = Math.Max(0f, intensity);
            light.Range = Math.Max(0.01f, range);
            light.SpotAngle = Math.Clamp(spotDegrees, 0.01f, 89.9f) * MathF.PI / 180f;
            light.InnerSpotAngle = Math.Clamp(innerSpotDegrees, 0f, Math.Clamp(spotDegrees, 0.01f, 89.9f)) * MathF.PI /
                                   180f;
            light.AttenuationMode = attenuation;
            light.AttenuationConstant = Math.Max(0f, attenuationConstant);
            light.AttenuationLinear = Math.Max(0f, attenuationLinear);
            light.AttenuationQuadratic = Math.Max(0f, attenuationQuadratic);
            if (attenuation == LightAttenuationMode.Polynomial &&
                light.AttenuationConstant + light.AttenuationLinear + light.AttenuationQuadratic == 0f)
                light.AttenuationConstant = 1f;
            light.CastsShadows = shadows;
            light.ShadowStrength = Math.Clamp(shadowStrength, 0f, 1f);
            light.ShadowNearPlane = Math.Max(0.001f, shadowNear);
            light.ShadowFarPlane = Math.Max(light.ShadowNearPlane + 0.001f, shadowFar);
            light.ShadowMapSizeOverride = (uint)Math.Clamp(shadowResolution, 0, 16384);
            light.ShadowPriority = shadowPriority;
            light.IesRotationRadians = iesRotationDegrees * MathF.PI / 180f;
            Run(() => editor.UpdateSelectedLight(light));
        }
    }

    private static bool IsFiniteNonZero(NumericsVector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && value.LengthSquared() > 1e-12f;

    private void RenderMaterialInspector(EditorController editor, EditorMaterialInspection inspection)
    {
        bool shared = editor.MaterialScope == MaterialEditScope.SharedMaterial;
        if (ImGui.BeginCombo("Edit scope", shared ? "Shared material" : "This object"))
        {
            if (ImGui.Selectable("This object", !shared)) editor.MaterialScope = MaterialEditScope.ThisObject;
            if (ImGui.Selectable("Shared material", shared)) editor.MaterialScope = MaterialEditScope.SharedMaterial;
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(editor.MaterialScope == MaterialEditScope.SharedMaterial
            ? "Edits affect every user of this material, including deduplicated aliases."
            : "Edits affect only this object; shared materials are copied when needed.");
        RenderMaterialTextureAssignments(editor);
        // A texture edit above can replace the selected object's borrowed material view.
        MaterialDefinition material = editor.TryGetSelectedMaterialDefinition(out var current) ? current! : inspection.Definition;
        string name = material.Name;
        NumericsVector3 baseColor = new(
            material.BaseColorFactor.X,
            material.BaseColorFactor.Y,
            material.BaseColorFactor.Z);
        float baseOpacity = material.BaseColorFactor.W;
        NumericsVector3 emissiveColor = new(
            material.EmissiveFactor.X,
            material.EmissiveFactor.Y,
            material.EmissiveFactor.Z);
        float emissiveStrength = material.EmissiveStrength;
        EmissivePhotometricUnit emissiveUnit = material.EmissiveUnit;
        float emissiveArtisticMultiplier = material.EmissiveArtisticMultiplier;
        float metallic = material.MetallicFactor;
        float roughness = material.RoughnessFactor;
        float occlusion = material.OcclusionStrength;
        float normalScale = material.NormalScale;
        MaterialAlphaMode alphaMode = material.AlphaMode;
        float alphaCutoff = material.AlphaCutoff;
        bool doubleSided = material.DoubleSided;
        bool receivesShadows = material.ReceivesShadows;
        bool automaticPlanarReflection =
            material.AutomaticPlanarReflectionEnabled;
        MaterialBlendMode? blendOverride = material.RenderBlendModeOverride;
        MaterialShadingModel shadingModel = material.ShadingModel;
        GiParticipationOverride diffuseGi = material.DiffuseGiParticipation;
        GiParticipationOverride emissionGi = material.EmissionGiParticipation;
        GiTransmissionPolicy transmissionPolicy = material.Extensions.TransmissionPolicy;
        float transmissionFactor = material.Extensions.TransmissionFactor;
        NumericsVector3 transmissionTint = new(
            material.Extensions.ThinTransmissionTint.X,
            material.Extensions.ThinTransmissionTint.Y,
            material.Extensions.ThinTransmissionTint.Z);
        float ior = material.Extensions.Ior;
        float thickness = material.Extensions.ThicknessFactor;
        float attenuationDistance = float.IsPositiveInfinity(
            material.Extensions.AttenuationDistance)
            ? 0f
            : material.Extensions.AttenuationDistance;
        NumericsVector3 attenuationColor = new(
            material.Extensions.AttenuationColor.X,
            material.Extensions.AttenuationColor.Y,
            material.Extensions.AttenuationColor.Z);
        OpticalBoundaryKind opticalBoundary =
            material.Extensions.OpticalBoundary;
        GiCausticCasterPolicy causticCasterPolicy =
            material.Extensions.CausticCasterPolicy;
        float waterVelocity0X = material.Extensions.WaterNormalVelocity0.X;
        float waterVelocity0Y = material.Extensions.WaterNormalVelocity0.Y;
        float waterVelocity1X = material.Extensions.WaterNormalVelocity1.X;
        float waterVelocity1Y = material.Extensions.WaterNormalVelocity1.Y;
        float waterUvScale0 = material.Extensions.WaterNormalUvScale0;
        float waterUvScale1 = material.Extensions.WaterNormalUvScale1;
        float dispersion = material.Extensions.Dispersion;

        bool changed = ImGui.InputText("Name##material", ref name, (nuint)256);
        changed |= ImGui.ColorEdit3("Base color", ref baseColor);
        changed |= ImGui.DragFloat("Base opacity", ref baseOpacity, 0.01f, 0f, 1f);
        changed |= ImGui.ColorEdit3("Emission color", ref emissiveColor);
        changed |= RenderEnumCombo("Emission unit", ref emissiveUnit);
        changed |= ImGui.DragFloat(
            emissiveUnit == EmissivePhotometricUnit.LuminanceNits
                ? "Emission luminance (nits)"
                : "Emission radiance scale",
            ref emissiveStrength,
            0.01f,
            0f,
            MaximumEditableEmissionStrength);
        changed |= ImGui.DragFloat(
            "Emission artistic multiplier",
            ref emissiveArtisticMultiplier,
            0.01f,
            0f,
            MaximumEditableEmissionStrength);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Applied after photometric conversion; physical energy changes by this factor.");
        changed |= ImGui.DragFloat("Metallic", ref metallic, 0.01f, 0f, 1f);
        changed |= ImGui.DragFloat(
            "Roughness",
            ref roughness,
            0.01f,
            0f,
            1f);
        changed |= ImGui.DragFloat("Occlusion strength", ref occlusion, 0.01f, 0f, 1f);
        changed |= ImGui.DragFloat("Normal scale", ref normalScale, 0.01f, 0f, 4f);

        ImGui.SeparatorText("Surface policy");
        changed |= RenderEnumCombo("Shading model", ref shadingModel);
        changed |= RenderEnumCombo("Alpha mode", ref alphaMode);
        changed |= ImGui.DragFloat(
            "Alpha cutoff",
            ref alphaCutoff,
            0.01f,
            0f,
            float.MaxValue);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Mask comparison is alpha >= cutoff. Values above 1 are valid and reject all normalized alpha.");
        changed |= ImGui.Checkbox("Double sided", ref doubleSided);
        ImGui.SameLine();
        changed |= ImGui.Checkbox("Receives shadows", ref receivesShadows);
        changed |= RenderOptionalBlendModeCombo("Blend policy", ref blendOverride);
        changed |= ImGui.Checkbox(
            "Automatic planar reflection",
            ref automaticPlanarReflection);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Allows this material's rigid planar surfaces to compete for " +
                "the automatic planar capture budget. Disabled by default.");
        }

        ImGui.SeparatorText("GI participation");
        changed |= RenderEnumCombo("Diffuse GI", ref diffuseGi);
        changed |= RenderEnumCombo("Emission GI", ref emissionGi);
        changed |= RenderEnumCombo("GI transmission", ref transmissionPolicy);
        changed |= ImGui.DragFloat("Transmission", ref transmissionFactor, 0.01f, 0f, 1f);
        changed |= ImGui.ColorEdit3("Transmission tint", ref transmissionTint);
        changed |= ImGui.DragFloat("IOR", ref ior, 0.01f, 1f, 4f);
        changed |= ImGui.DragFloat("Thickness fallback (m)", ref thickness,
            0.01f, 0f, 10_000f);
        changed |= ImGui.DragFloat("Attenuation distance (0 = infinite)",
            ref attenuationDistance, 0.01f, 0f, 10_000f);
        changed |= ImGui.ColorEdit3("Attenuation color", ref attenuationColor);
        changed |= RenderEnumCombo("Optical boundary", ref opticalBoundary);
        changed |= RenderEnumCombo("Caustic caster", ref causticCasterPolicy);
        if (opticalBoundary == OpticalBoundaryKind.WaterSurface)
        {
            changed |= ImGui.DragFloat("Water flow 0 X", ref waterVelocity0X,
                0.001f, -32f, 32f);
            changed |= ImGui.DragFloat("Water flow 0 Y", ref waterVelocity0Y,
                0.001f, -32f, 32f);
            changed |= ImGui.DragFloat("Water flow 1 X", ref waterVelocity1X,
                0.001f, -32f, 32f);
            changed |= ImGui.DragFloat("Water flow 1 Y", ref waterVelocity1Y,
                0.001f, -32f, 32f);
            changed |= ImGui.DragFloat("Water normal UV scale 0", ref waterUvScale0,
                0.01f, 0.001f, 1024f);
            changed |= ImGui.DragFloat("Water normal UV scale 1", ref waterUvScale1,
                0.01f, 0.001f, 1024f);
        }

        changed |= ImGui.DragFloat("Dispersion (20 / Vd)", ref dispersion,
            0.01f, 0f, 4f);

        RenderMaterialTextureBindings(material);
        RenderMaterialTransportState(inspection);

        if (!changed)
            return;

        MaterialFeatureFlags editedFeatureFlags = material.FeatureFlags;
        if (transmissionFactor > 0f ||
            transmissionPolicy != GiTransmissionPolicy.None)
        {
            editedFeatureFlags |= MaterialFeatureFlags.Transmission;
        }

        if (transmissionPolicy == GiTransmissionPolicy.Volume)
        {
            editedFeatureFlags |= MaterialFeatureFlags.VolumeApproximation |
                                  MaterialFeatureFlags.Ior;
        }

        if (dispersion > 0f)
            editedFeatureFlags |= MaterialFeatureFlags.Dispersion;

        var updated = material with
        {
            Name = name,
            BaseColorFactor = new CoreVector4(
                baseColor.X,
                baseColor.Y,
                baseColor.Z,
                baseOpacity),
            EmissiveFactor = new CoreVector3(
                emissiveColor.X,
                emissiveColor.Y,
                emissiveColor.Z),
            EmissiveStrength = emissiveStrength,
            EmissiveUnit = emissiveUnit,
            EmissiveArtisticMultiplier = emissiveArtisticMultiplier,
            MetallicFactor = metallic,
            RoughnessFactor = roughness,
            OcclusionStrength = occlusion,
            NormalScale = normalScale,
            AlphaMode = alphaMode,
            AlphaCutoff = alphaCutoff,
            DoubleSided = doubleSided,
            ReceivesShadows = receivesShadows,
            AutomaticPlanarReflectionEnabled = automaticPlanarReflection,
            RenderBlendModeOverride = blendOverride,
            ShadingModel = shadingModel,
            DiffuseGiParticipation = diffuseGi,
            EmissionGiParticipation = emissionGi,
            FeatureFlags = editedFeatureFlags,
            Extensions = material.Extensions with
            {
                TransmissionPolicy = transmissionPolicy,
                TransmissionFactor = transmissionFactor,
                Ior = ior,
                ThicknessFactor = thickness,
                AttenuationDistance = attenuationDistance <= 0f
                    ? float.PositiveInfinity
                    : attenuationDistance,
                AttenuationColor = new CoreVector3(
                    attenuationColor.X,
                    attenuationColor.Y,
                    attenuationColor.Z),
                OpticalBoundary = opticalBoundary,
                CausticCasterPolicy = causticCasterPolicy,
                WaterNormalVelocity0 = new Njulf.Core.Math.Vector2(
                    waterVelocity0X, waterVelocity0Y),
                WaterNormalVelocity1 = new Njulf.Core.Math.Vector2(
                    waterVelocity1X, waterVelocity1Y),
                WaterNormalUvScale0 = waterUvScale0,
                WaterNormalUvScale1 = waterUvScale1,
                Dispersion = dispersion,
                ThinTransmissionTint = new CoreVector3(
                    transmissionTint.X,
                    transmissionTint.Y,
                    transmissionTint.Z)
            }
        };
        Run(() => editor.UpdateSelectedMaterialDefinition(updated));
    }

    private static bool RenderEnumCombo<T>(string label, ref T value)
        where T : struct, Enum
    {
        bool changed = false;
        if (!ImGui.BeginCombo(label, value.ToString()))
            return false;

        foreach (T candidate in Enum.GetValues<T>())
        {
            if (ImGui.Selectable(candidate.ToString(), EqualityComparer<T>.Default.Equals(candidate, value)))
            {
                value = candidate;
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    private static bool RenderOptionalBlendModeCombo(
        string label,
        ref MaterialBlendMode? value)
    {
        bool changed = false;
        if (!ImGui.BeginCombo(label, value?.ToString() ?? "Automatic"))
            return false;

        if (ImGui.Selectable("Automatic", value == null))
        {
            value = null;
            changed = true;
        }

        foreach (MaterialBlendMode candidate in Enum.GetValues<MaterialBlendMode>())
        {
            if (ImGui.Selectable(candidate.ToString(), value == candidate))
            {
                value = candidate;
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    private static void RenderMaterialTextureBindings(MaterialDefinition material)
    {
        if (!ImGui.CollapsingHeader("Texture bindings"))
            return;

        RenderTextureBinding("Base color", material.BaseColor);
        RenderTextureBinding("Normal", material.Normal);
        RenderTextureBinding("Metallic/roughness", material.MetallicRoughness);
        RenderTextureBinding("Occlusion", material.Occlusion);
        RenderTextureBinding("Emission", material.Emissive);
    }

    private void RenderMaterialTextureAssignments(EditorController editor)
    {
        if (_materialEditingObject != editor.Selection.Id)
        {
            _materialEditingObject = editor.Selection.Id;
            for (int i = 0; i < _materialTexturePaths.Length; i++)
                _materialTexturePaths[i] = editor.GetSelectedMaterialTexturePath((MaterialTextureSlot)i) ?? string.Empty;
        }
        if (!ImGui.CollapsingHeader("Assign textures")) return;
        ImGui.TextWrapped("Enter a file path and press Assign. Relative paths use the current directory; saved paths are absolute.");
        for (int i = 0; i < _materialTexturePaths.Length; i++)
        {
            var slot = (MaterialTextureSlot)i;
            ImGui.PushID(i);
            ImGui.InputText(slot.ToString(), ref _materialTexturePaths[i], (nuint)2048);
            if (ImGui.Button("Assign"))
            {
                string path = _materialTexturePaths[i];
                Run(() =>
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(path);
                    editor.SetSelectedMaterialTexture(slot, path);
                    _materialTexturePaths[(int)slot] = editor.GetSelectedMaterialTexturePath(slot) ?? string.Empty;
                });
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
                Run(() =>
                {
                    editor.SetSelectedMaterialTexture(slot, string.Empty);
                    _materialTexturePaths[(int)slot] = string.Empty;
                });
            ImGui.PopID();
        }
    }

    private static void RenderTextureBinding(string label, MaterialTextureBinding binding)
    {
        if (!binding.IsBound)
        {
            ImGui.TextDisabled($"{label}: none");
            return;
        }

        ImGui.TextDisabled(
            $"{label}: texture {binding.Texture.Index}:{binding.Texture.Generation}, " +
            $"UV{binding.TexCoordSet}, offset {binding.Offset.X:0.###},{binding.Offset.Y:0.###}, " +
            $"scale {binding.Scale.X:0.###},{binding.Scale.Y:0.###}, rotation {binding.RotationRadians:0.###} rad");
    }

    private static void RenderMaterialTransportState(EditorMaterialInspection inspection)
    {
        if (!ImGui.CollapsingHeader("Derived GI transport (read-only)"))
            return;

        GiMaterialTransportProfile profile = inspection.TransportProfile;
        MaterialAspectRevisions revisions = inspection.AspectRevisions;
        ImGui.TextDisabled(
            $"Quality: {profile.Quality}  Algorithm: {profile.AlgorithmVersion}  " +
            $"Flags: {profile.Flags}");
        ImGui.TextDisabled(
            $"Diffuse mean: {profile.MeanDiffuseReflectance.X:0.####}, " +
            $"{profile.MeanDiffuseReflectance.Y:0.####}, {profile.MeanDiffuseReflectance.Z:0.####}");
        ImGui.TextDisabled(
            $"Emission mean: {profile.MeanEmissiveRadiance.X:0.####}, " +
            $"{profile.MeanEmissiveRadiance.Y:0.####}, {profile.MeanEmissiveRadiance.Z:0.####}  " +
            $"importance {profile.EmissiveImportance:0.####}");
        ImGui.TextDisabled(
            $"Photometry: {profile.EmissiveUnit}, effective scale {profile.EffectiveEmissiveScale:0.####}, " +
            $"artistic {profile.EmissiveArtisticMultiplier:0.####}x");
        ImGui.TextDisabled(
            $"Luminance: average {profile.AverageEmissiveLuminanceNits:0.###} nits, peak " +
            (profile.PeakEmissiveLuminanceValid
                ? $"<= {profile.PeakEmissiveLuminanceNits:0.###} nits"
                : "unavailable"));
        ImGui.TextDisabled(
            $"AO mean: {profile.MeanMaterialOcclusion:0.####}  " +
            $"alpha coverage: {profile.AlphaCoverage:0.####}  " +
            $"normal variance: {profile.NormalVariance:0.####}");
        ImGui.TextDisabled(
            $"Source hash: {profile.SourceContentHash:X16}  Primitive hash: {profile.PrimitiveContentHash:X16}");
        ImGui.TextDisabled(
            $"Revisions M:{revisions.Material} D:{revisions.DiffuseTransport} " +
            $"E:{revisions.Emission} A:{revisions.AlphaCoverage} " +
            $"S:{revisions.Sidedness} SM:{revisions.ShadingModel} FF:{revisions.FarField}");

        if (inspection.CompileDiagnostics.Count == 0)
        {
            ImGui.TextDisabled("Compiler diagnostics: none");
            return;
        }

        ImGui.TextDisabled($"Compiler diagnostics ({inspection.CompileDiagnostics.Count}):");
        foreach (string diagnostic in inspection.CompileDiagnostics)
            ImGui.TextWrapped($"- {diagnostic}");
    }

    private bool MatchesFilter(string name, Guid id) => string.IsNullOrWhiteSpace(_filter) ||
                                                        name.Contains(_filter, StringComparison.OrdinalIgnoreCase) ||
                                                        id.ToString().Contains(_filter,
                                                            StringComparison.OrdinalIgnoreCase);

    private static void ShowIdTooltip(Guid id)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(id.ToString());
    }

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
