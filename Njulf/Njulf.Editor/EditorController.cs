using System;
using Njulf.Assets.Scenes;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Resources;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace Njulf.Editor;

/// <summary>
/// UI-independent editor command surface. Panels and input bindings call this class, making all
/// mutations testable without a graphics device or an ImGui context.
/// </summary>
public sealed class EditorController
{
    private readonly Scene _scene;
    private readonly IContentManager _content;
    private readonly LightManager _lightManager;
    private readonly MaterialManager _materialManager;
    private readonly ISceneLightStore _lightStore;
    private readonly ISceneMaterialOverrideStore _materialStore;
    private readonly SceneDocumentWriter _writer = new();
    private readonly IEditorOverlayHost? _overlay;
    private readonly VulkanRenderer? _renderer;
    private readonly AdvancedGiEditorStartupContext _advancedGiStartup;
    private readonly Action<string>? _requestAdvancedGiRestart;
    private readonly Action<AdvancedGiFeatureSelection>?
        _requestAdvancedGiFeatureRestart;
    private bool _previousDebugEnabled;
    private bool _previousCpuSnapshotsEnabled;

    public EditorController(
        Scene scene,
        IContentManager content,
        LightManager lightManager,
        MaterialManager materialManager,
        IEditorOverlayHost? overlay = null,
        VulkanRenderer? renderer = null,
        FirstPersonCamera? camera = null,
        AdvancedGiEditorStartupContext? advancedGiStartup = null,
        Action<string>? requestAdvancedGiRestart = null,
        Action<AdvancedGiFeatureSelection>?
            requestAdvancedGiFeatureRestart = null)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _lightManager = lightManager ?? throw new ArgumentNullException(nameof(lightManager));
        _materialManager = materialManager ?? throw new ArgumentNullException(nameof(materialManager));
        _overlay = overlay;
        _renderer = renderer;
        _advancedGiStartup = advancedGiStartup ??
            AdvancedGiEditorStartupContext.Unconfigured;
        _requestAdvancedGiRestart = requestAdvancedGiRestart;
        _requestAdvancedGiFeatureRestart =
            requestAdvancedGiFeatureRestart;
        Camera = camera;
        _lightStore = new LightManagerSceneLightStore(_lightManager);
        _materialStore = new MaterialManagerSceneMaterialOverrideStore(_materialManager);
    }

    public bool Enabled { get; private set; }
    public bool IsDirty { get; private set; }
    public string? ScenePath { get; private set; }
    public EditorSelection Selection { get; private set; } = EditorSelection.None;
    public Scene Scene => _scene;
    public FirstPersonCamera? Camera { get; set; }
    public RenderSettings? RendererSettings => _renderer?.Settings;
    public RendererDiagnostics? RendererDiagnostics => _renderer?.LastDiagnostics;
    public AdvancedGiEditorStartupContext AdvancedGiStartup =>
        _advancedGiStartup;
    public AdvancedGiRuntimeContentState AdvancedGiRuntimeContentState =>
        _renderer?.AdvancedGiRuntimeContentState ??
        AdvancedGiRuntimeContentState.Unconfigured;
    public string AdvancedGiCandidateProfileStatus =>
        _renderer?.AdvancedGiCandidateProfileStatus ?? "renderer-unavailable";
    public bool CanRestartForAdvancedGi =>
        _requestAdvancedGiRestart is not null;
    public bool CanRestartAdvancedGiFeatures =>
        _requestAdvancedGiFeatureRestart is not null;
    public bool SuppressGameInput => Enabled && (_overlay?.WantCaptureKeyboard == true || _overlay?.WantCaptureMouse == true);

    public event Action<EditorSelection>? SelectionChanged;

    public void Toggle() => SetEnabled(!Enabled);

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return;
        Enabled = enabled;
        _overlay?.SetEnabled(enabled);
        if (_renderer != null)
        {
            if (enabled)
            {
                _previousDebugEnabled = _renderer.Settings.Debug.Enabled;
                _previousCpuSnapshotsEnabled = _renderer.Settings.Debug.CpuSnapshotsEnabled;
                _renderer.Settings.Debug.Enabled = true;
                _renderer.Settings.Debug.CpuSnapshotsEnabled = true;
            }
            else
            {
                _renderer.Settings.Debug.SelectedObjectIndex = -1;
                _renderer.Settings.Debug.Enabled = _previousDebugEnabled;
                _renderer.Settings.Debug.CpuSnapshotsEnabled = _previousCpuSnapshotsEnabled;
            }
        }
        if (!enabled)
            Select(EditorSelection.None);
    }

    public void SetScenePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ScenePath = Path.GetFullPath(path);
    }

    public bool TryPick(FirstPersonCamera camera, Vector2 screenPosition, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!Enabled || _overlay?.WantCaptureMouse == true)
            return false;
        Ray ray = camera.ScreenPointToRay(screenPosition, viewportSize);
        if (ScenePicker.TryPickRenderObject(_scene, ray, out RenderObject? objectHit, out float nearest))
        {
            Select(EditorSelection.ForEntity(EditorSelectionKind.Object, objectHit!.Id));
            return true;
        }
        foreach (LightRecord light in _lightManager.GetLightRecords())
        {
            var sphere = new BoundingSphere(new Vector3(light.Light.Position.X, light.Light.Position.Y, light.Light.Position.Z), 0.35f);
            if (!ray.Intersects(sphere, out float distance) || distance >= nearest)
                continue;
            nearest = distance;
            Select(EditorSelection.ForLight(light.Id, light.Handle));
        }
        if (float.IsPositiveInfinity(nearest))
            Select(EditorSelection.None);
        return !Selection.IsEmpty;
    }

    public RenderObject AddObject(SceneAssetReference reference, Vector3 position)
    {
        ArgumentNullException.ThrowIfNull(reference);
        reference.Validate();
        Model source = _content.Load<Model>(reference.Path) ?? throw new InvalidOperationException($"Could not load model '{reference.Path}'.");
        Model instance = source.CreateInstance();
        RenderObject? objectToAdd = SelectOne(instance, reference.SubObject);
        if (objectToAdd == null)
            throw new InvalidOperationException($"Model '{reference.Path}' does not contain sub-object '{reference.SubObject}'.");
        objectToAdd.AssetReference = reference;
        objectToAdd.Position = position;
        objectToAdd.IsStatic = objectToAdd is not SkinnedRenderObject;
        _scene.Add(objectToAdd);
        MarkDirty(EditorSelection.ForEntity(EditorSelectionKind.Object, objectToAdd.Id));
        return objectToAdd;
    }

    public LightHandle AddLight(Light light, string? name = null)
    {
        LightHandle handle = _lightManager.AddLightHandle(light, name);
        _lightManager.TryGetLightId(handle, out Guid id);
        MarkDirty(EditorSelection.ForLight(id, handle));
        return handle;
    }

    public bool DeleteSelection()
    {
        bool deleted = Selection.Kind switch
        {
            EditorSelectionKind.Object => Remove<RenderObject>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.ReflectionProbe => Remove<ReflectionProbe>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.GiVolume => Remove<GlobalIlluminationProbeVolume>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.FoliagePatch => Remove<Njulf.Core.Foliage.FoliagePatch>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.FoliagePrototype => Remove<Njulf.Core.Foliage.FoliagePrototype>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.ParticleEffect => Remove<ParticleEffectInstance>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.InstanceBatch => Remove<StaticInstanceBatch>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.Light => _lightManager.RemoveLight(Selection.LightHandle),
            _ => false
        };
        if (!deleted)
            return false;
        IsDirty = true;
        Select(EditorSelection.None);
        return true;
    }

    public bool UpdateSelectedLight(in Light light)
    {
        if (Selection.Kind != EditorSelectionKind.Light || !_lightManager.UpdateLight(Selection.LightHandle, light))
            return false;
        IsDirty = true;
        return true;
    }

    public bool SetSelectedLightName(string name)
    {
        if (Selection.Kind != EditorSelectionKind.Light || !_lightManager.SetLightName(Selection.LightHandle, name)) return false;
        IsDirty = true;
        return true;
    }

    public IReadOnlyList<LightRecord> GetLights() => _lightManager.GetLightRecords();

    public bool TryGetSelectedLight(out Light light)
    {
        if (Selection.Kind == EditorSelectionKind.Light)
            return _lightManager.TryGetLight(Selection.LightHandle, out light);
        light = default;
        return false;
    }

    public bool UpdateSelectedMaterialDefinition(MaterialDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (Selection.Kind != EditorSelectionKind.Object ||
            _scene.FindById(Selection.Id) is not RenderObject { Material: MaterialHandle handle } target)
            return false;

        // Validate before splitting a shared registration. This keeps an invalid editor draft from
        // changing object/material ownership and ensures every published definition is canonical.
        MaterialDefinition normalized = MaterialDefinitionValidator.ValidateAndNormalize(definition);
        _materialManager.UpdateRenderObjectMaterialDefinition(
            target,
            normalized);
        IsDirty = true;
        return true;
    }

    public bool TryGetSelectedObject(out RenderObject? renderObject)
    {
        renderObject = Selection.Kind == EditorSelectionKind.Object ? _scene.FindById(Selection.Id) as RenderObject : null;
        return renderObject != null;
    }

    public bool TryGetSelectedMaterialDefinition(out MaterialDefinition? material)
    {
        if (TryGetSelectedObject(out RenderObject? target) && target?.Material is MaterialHandle handle)
        {
            try
            {
                material = _materialManager.GetMaterialDefinition(handle);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
        }
        material = null;
        return false;
    }

    public bool TryGetSelectedMaterialInspection(out EditorMaterialInspection? inspection)
    {
        if (TryGetSelectedObject(out RenderObject? target) && target?.Material is MaterialHandle handle)
        {
            try
            {
                inspection = new EditorMaterialInspection(
                    handle,
                    _materialManager.GetMaterialDefinition(handle),
                    _materialManager.GetMaterialTransportProfile(handle),
                    _materialManager.GetMaterialAspectRevisions(handle),
                    _materialManager.GetMaterialCompileDiagnostics(handle));
                return true;
            }
            catch (InvalidOperationException)
            {
                // A stale generation can be observed for one editor frame while scene content is
                // reloaded. Treat it as unavailable rather than presenting mismatched derived data.
            }
        }

        inspection = null;
        return false;
    }

    public IReadOnlyList<SceneAssetReference> GetModelDependencies() => _scene.RenderObjects
        .Select(static item => item.AssetReference)
        .Concat(_scene.StaticInstanceBatches.Select(static item => item.AssetReference))
        .Concat(_scene.FoliagePrototypes.Select(static item => item.AssetReference))
        .Where(static item => item != null).Cast<SceneAssetReference>()
        .DistinctBy(static item => (item.Path, item.SubObject, item.ContentHash))
        .OrderBy(static item => item.Path, StringComparer.Ordinal)
        .ThenBy(static item => item.SubObject, StringComparer.Ordinal).ToArray();

    public LightHandle AddLightAtCamera(LightType type)
    {
        FirstPersonCamera camera = Camera ?? throw new InvalidOperationException("An editor camera is required to add a light.");
        return AddLight(new Light
        {
            Type = type,
            Position = new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
            Direction = new System.Numerics.Vector3(camera.Forward.X, camera.Forward.Y, camera.Forward.Z),
            Color = System.Numerics.Vector3.One,
            Intensity = type == LightType.Directional ? 3f : 10f,
            Range = 12f,
            SpotAngle = MathF.PI / 4f,
            ShadowStrength = 1f,
            ShadowNearPlane = 0.1f,
            ShadowFarPlane = 100f
        }, $"{type} Light");
    }

    public int AddSimpleDdgiAuthoredVolumeAtCamera()
    {
        GlobalIlluminationSettings settings = RendererSettings?.GlobalIllumination ??
            throw new InvalidOperationException("A Vulkan renderer is required to edit live GI settings.");
        if (settings.SimpleDdgiAuthoredVolumes.Count >= GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            throw new InvalidOperationException($"Simple DDGI supports at most {GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount} authored overrides.");

        Vector3 center = Camera?.Position ?? Vector3.Zero;
        var halfSize = new Vector3(6f, 3f, 6f);
        settings.SimpleDdgiAuthoredVolumes.Add(new SimpleDdgiAuthoredVolume(
            center - halfSize,
            center + halfSize,
            settings.SimpleDdgiProbeSpacing,
            purpose: SimpleDdgiVolumePurpose.ReceiverHero,
            priority: 0));
        return settings.SimpleDdgiAuthoredVolumes.Count - 1;
    }

    public GlobalIlluminationProbeVolume AddGlobalIlluminationProbeVolumeAtCamera()
    {
        var volume = new GlobalIlluminationProbeVolume
        {
            Name = NextGlobalIlluminationProbeVolumeName()
        };
        Vector3 center = Camera?.Position ?? Vector3.Zero;
        volume.Origin = center - volume.Size * 0.5f;
        _scene.Add(volume);
        MarkDirty(EditorSelection.ForEntity(EditorSelectionKind.GiVolume, volume.Id));
        return volume;
    }

    public bool UpdateSimpleDdgiAuthoredVolume(int index, SimpleDdgiAuthoredVolume volume)
    {
        IList<SimpleDdgiAuthoredVolume>? volumes = RendererSettings?.GlobalIllumination.SimpleDdgiAuthoredVolumes;
        if (volumes == null || index < 0 || index >= volumes.Count)
            return false;
        volumes[index] = volume;
        return true;
    }

    public bool RemoveSimpleDdgiAuthoredVolume(int index)
    {
        IList<SimpleDdgiAuthoredVolume>? volumes = RendererSettings?.GlobalIllumination.SimpleDdgiAuthoredVolumes;
        if (volumes == null || index < 0 || index >= volumes.Count)
            return false;
        volumes.RemoveAt(index);
        return true;
    }

    public void SaveRenderSettings(string path)
    {
        RenderSettings settings = RendererSettings ??
            throw new InvalidOperationException("A Vulkan renderer is required to save live render settings.");
        settings.Save(path);
    }

    /// <summary>
    /// Requests a clean renderer reconstruction with ordinary explicit
    /// Advanced GI modes. No startup profile or evidence file is created or
    /// loaded by this path.
    /// </summary>
    public void RestartAdvancedGiFeatures(
        in AdvancedGiFeatureSelection selection)
    {
        if (_renderer is null)
        {
            throw new InvalidOperationException(
                "A Vulkan renderer is required to change Advanced GI features.");
        }
        Action<AdvancedGiFeatureSelection> restart =
            _requestAdvancedGiFeatureRestart ??
            throw new InvalidOperationException(
                "This editor host does not provide a renderer restart callback.");
        restart(selection);
    }

    public AdvancedGiStartupProfilePreflightResult
        PreflightAdvancedGiStartupProfile(
            AdvancedGiEditorActivationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        RenderSettings live = RendererSettings ??
            throw new InvalidOperationException(
                "A Vulkan renderer is required to stage Advanced GI settings.");
        RenderSettings snapshot = draft.CreateSettingsSnapshot(live);
        return AdvancedGiStartupProfilePreflight.Evaluate(
            snapshot,
            draft.Profile,
            ResolveAdvancedGiRuntimeBuildIdentity());
    }

    public AdvancedGiStartupProfilePreflightResult
        SaveAdvancedGiStartupProfile(
            AdvancedGiEditorActivationDraft draft,
            bool restart)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (restart && _requestAdvancedGiRestart is null)
        {
            throw new InvalidOperationException(
                "This editor host does not provide a renderer restart callback.");
        }

        RenderSettings live = RendererSettings ??
            throw new InvalidOperationException(
                "A Vulkan renderer is required to stage Advanced GI settings.");
        RenderSettings snapshot = draft.CreateSettingsSnapshot(live);
        AdvancedGiStartupProfilePreflightResult result =
            AdvancedGiStartupProfilePreflight.SaveValidated(
                snapshot,
                draft.Profile,
                ResolveAdvancedGiRuntimeBuildIdentity());

        string profilePath = Path.GetFullPath(draft.Profile.ProfilePath);
        if (restart)
            _requestAdvancedGiRestart!(profilePath);
        return result;
    }

    private AdvancedGiRuntimeBuildIdentity?
        ResolveAdvancedGiRuntimeBuildIdentity()
    {
        RendererDiagnostics? diagnostics = RendererDiagnostics;
        if (diagnostics is null)
            return null;
        var identity = new AdvancedGiRuntimeBuildIdentity(
            diagnostics.CaptureRun.Commit,
            diagnostics.CaptureRun.ShaderBundleHash);
        return identity.IsWellFormed ? identity : null;
    }

    public RenderObject AddObjectAtCamera(SceneAssetReference reference, float forwardDistance = 3f)
    {
        FirstPersonCamera camera = Camera ?? throw new InvalidOperationException("An editor camera is required to add an object.");
        return AddObject(reference, camera.Position + camera.Forward * forwardDistance);
    }

    public void UpdateSelectionHighlight()
    {
        if (!Enabled || _renderer == null) return;
        _renderer.DebugDraw.Enabled = true;
        if (Selection.Kind == EditorSelectionKind.Object)
        {
            if (_renderer.TryFindObjectById(Selection.Id, out int index))
            {
                _renderer.Settings.Debug.SelectedObjectIndex = index;
                _renderer.Settings.Debug.Mode = DebugOverlayMode.SelectedObject;
            }
            return;
        }
        _renderer.Settings.Debug.SelectedObjectIndex = -1;
        var color = new Vector4(1f, 0.75f, 0.1f, 1f);
        switch (Selection.Kind)
        {
            case EditorSelectionKind.Light when TryGetSelectedLight(out Light light):
                _renderer.DebugDraw.Sphere(new Vector3(light.Position.X, light.Position.Y, light.Position.Z), 0.45f, color, depthMode: DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.ReflectionProbe when _scene.FindById(Selection.Id) is ReflectionProbe probe:
                _renderer.DebugDraw.OrientedBox(probe.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(probe.Position), probe.BoxExtents, color, depthMode: DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.GiVolume when _scene.FindById(Selection.Id) is GlobalIlluminationProbeVolume volume:
                _renderer.DebugDraw.Box(new BoundingBox(volume.Origin, volume.Origin + volume.Size), color, DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.FoliagePatch when _scene.FindById(Selection.Id) is Njulf.Core.Foliage.FoliagePatch patch:
                _renderer.DebugDraw.Box(patch.Bounds, color, DebugDrawDepthMode.XRay);
                break;
        }
    }

    public void MarkDirty(EditorSelection selection)
    {
        IsDirty = true;
        Select(selection);
    }

    public void Save()
    {
        if (ScenePath == null)
            throw new InvalidOperationException("No scene path is configured. Use Save As before saving a code-built scene.");
        _writer.Write(ScenePath, _scene, _lightStore, _materialStore);
        IsDirty = false;
    }

    public void SaveAs(string path)
    {
        SetScenePath(path);
        Save();
    }

    public void Reload()
    {
        if (ScenePath == null)
            throw new InvalidOperationException("No scene path is configured.");
        SceneDocument document = SceneDocumentJson.Read(ScenePath);
        _scene.ClearAndDispose();
        _scene.Id = document.Id;
        _lightStore.Clear();
        new SceneDocumentLoader(_content).Populate(document, _scene, _lightStore, materials: _materialStore);
        IsDirty = false;
        Select(EditorSelection.None);
    }

    private string NextGlobalIlluminationProbeVolumeName()
    {
        const string baseName = "GI Probe Volume";
        int suffix = _scene.GlobalIlluminationProbeVolumes.Count + 1;
        string name;
        do
        {
            name = $"{baseName} {suffix++}";
        }
        while (_scene.GlobalIlluminationProbeVolumes.Any(volume =>
            string.Equals(volume.Name, name, StringComparison.OrdinalIgnoreCase)));
        return name;
    }

    public bool SelectEntity(EditorSelectionKind kind, Guid id)
    {
        if (kind == EditorSelectionKind.Light)
        {
            if (!_lightManager.TryGetLightHandle(id, out LightHandle handle))
                return false;
            Select(EditorSelection.ForLight(id, handle));
            return true;
        }
        if (_scene.FindById(id) == null)
            return false;
        Select(EditorSelection.ForEntity(kind, id));
        return true;
    }

    public bool UpdateSelectedObject(string name, bool visible, bool isStatic, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        if (Selection.Kind != EditorSelectionKind.Object || _scene.FindById(Selection.Id) is not RenderObject target)
            return false;
        target.Name = name;
        target.Visible = visible;
        target.IsStatic = isStatic;
        target.Position = position;
        target.Rotation = rotation;
        target.Scale = scale;
        IsDirty = true;
        return true;
    }

    private void Select(EditorSelection selection)
    {
        if (Selection == selection)
            return;
        Selection = selection;
        SelectionChanged?.Invoke(selection);
    }

    private static RenderObject? SelectOne(Model model, string selector)
    {
        if (selector == "*")
            return model.RenderObjects.Count == 1 ? model.RenderObjects[0] : null;
        if (int.TryParse(selector, out int index))
            return index >= 0 && index < model.RenderObjects.Count ? model.RenderObjects[index] : null;
        foreach (RenderObject item in model.RenderObjects)
            if (string.Equals(item.Name, selector, StringComparison.Ordinal))
                return item;
        return null;
    }

    private static bool Remove<T>(IIdentifiedSceneEntity? value, Action<T> remove) where T : class, IIdentifiedSceneEntity
    {
        if (value is not T typed)
            return false;
        remove(typed);
        return true;
    }
}
