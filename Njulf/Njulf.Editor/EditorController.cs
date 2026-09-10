using System;
using Njulf.Assets;
using Njulf.Assets.Scenes;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
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
    private Scene _scene;
    private readonly IContentManager _content;
    private readonly LightManager _lightManager;
    private readonly MaterialManager _materialManager;
    private SceneLightStore _lightStore;
    private readonly LightManagerSceneLightStore _lightCodec;
    private readonly ISceneMaterialOverrideStore _materialStore;
    private readonly SceneDocumentWriter _writer = new();
    private readonly IEditorOverlayHost? _overlay;
    private readonly VulkanRenderer? _renderer;
    private readonly AdvancedGiEditorStartupContext _advancedGiStartup;
    private readonly Action<string>? _requestAdvancedGiRestart;

    private readonly Action<AdvancedGiFeatureSelection>?
        _requestAdvancedGiFeatureRestart;

    private readonly Func<string, Model>? _loadModel;
    private bool _previousDebugEnabled;
    private bool _previousCpuSnapshotsEnabled;
    private readonly EnvironmentSettings _environmentDraft = new();
    private SceneEnvironment? _environmentDraftSource;
    private bool _environmentDraftInitialized;

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
            requestAdvancedGiFeatureRestart = null,
        Func<string, Model>? loadModel = null)
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
        _loadModel = loadModel;
        Camera = camera;
        _lightStore = new SceneLightStore(_scene);
        _lightCodec = new LightManagerSceneLightStore(_lightManager);
        _materialStore = new MaterialManagerSceneMaterialOverrideStore(_materialManager);
        ModelLightRuntimeController.Attach(
            _scene,
            _content,
            _lightStore,
            _loadModel);
    }

    public bool Enabled { get; private set; }
    public bool IsDirty { get; private set; }
    public string? ScenePath { get; private set; }
    public EditorSelection Selection { get; private set; } = EditorSelection.None;
    public MaterialEditScope MaterialScope { get; set; } = MaterialEditScope.ThisObject;
    public Scene Scene => _scene;
    public FirstPersonCamera? Camera { get; set; }
    public RenderSettings? RendererSettings => _renderer?.Settings;

    public EnvironmentSettings EditableEnvironment
    {
        get
        {
            if (!_environmentDraftInitialized || !ReferenceEquals(_environmentDraftSource, _scene.Environment))
            {
                SceneEnvironmentSettings.Apply(_scene.Environment ??
                                               SceneEnvironmentSettings.Capture(_renderer?.Settings.Environment ??
                                                   new EnvironmentSettings()),
                    _environmentDraft);
                _environmentDraftSource = _scene.Environment;
                _environmentDraftInitialized = true;
            }

            return _environmentDraft;
        }
    }

    public void CommitEnvironmentEdits()
    {
        SceneEnvironment next = SceneEnvironmentSettings.Capture(EditableEnvironment);
        if (Equals(next, _scene.Environment)) return;
        _scene.Environment = next;
        _environmentDraftSource = next;
        IsDirty = true;
    }

    public Njulf.Graphics.GraphicsSettingsController? GraphicsSettings => _renderer?.GraphicsDevice.Settings;
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

    public bool SuppressGameInput =>
        Enabled && (Gizmos.IsDragging || _overlay?.WantCaptureKeyboard == true || _overlay?.WantCaptureMouse == true);

    public EditorGizmoController Gizmos { get; } = new();

    public event Action<EditorSelection>? SelectionChanged;

    public void Toggle() => SetEnabled(!Enabled);

    /// <summary>
    /// Rebinds editor commands after an application-level transactional scene
    /// handoff. Selection cannot cross scene ownership boundaries.
    /// </summary>
    public void SetScene(Scene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (ReferenceEquals(_scene, scene))
            return;

        Gizmos.Cancel(this);
        _scene = scene;
        _environmentDraftInitialized = false;
        _lightStore = new SceneLightStore(scene);
        Selection = EditorSelection.None;
        MaterialScope = MaterialEditScope.ThisObject;
        IsDirty = false;
        SelectionChanged?.Invoke(Selection);
        ModelLightRuntimeController.Attach(
            _scene,
            _content,
            _lightStore,
            _loadModel);
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
            return;
        Enabled = enabled;
        if (!enabled) Gizmos.Cancel(this);
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
        if (!Enabled || Gizmos.ConsumesPointer || _overlay?.WantCaptureMouse == true)
            return false;
        Ray ray = camera.ScreenPointToRay(screenPosition, viewportSize);
        if (ScenePicker.TryPickRenderObject(_scene, ray, out RenderObject? objectHit, out float nearest))
        {
            Select(EditorSelection.ForEntity(EditorSelectionKind.Object, objectHit!.Id));
            return true;
        }

        foreach (LightRecord light in GetLights())
        {
            var sphere =
                new BoundingSphere(new Vector3(light.Light.Position.X, light.Light.Position.Y, light.Light.Position.Z),
                    0.35f);
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
        Model source = _content.Load<Model>(reference.Path) ??
                       throw new InvalidOperationException($"Could not load model '{reference.Path}'.");
        RenderObject? objectToAdd = SelectOne(source, reference.SubObject);
        if (objectToAdd == null)
            throw new InvalidOperationException(
                $"Model '{reference.Path}' does not contain sub-object '{reference.SubObject}'.");
        try
        {
            objectToAdd.AssetReference = reference;
            objectToAdd.Position = position;
            objectToAdd.IsStatic = objectToAdd is not SkinnedRenderObject;
            _scene.Add(objectToAdd);
        }
        catch
        {
            objectToAdd.Dispose();
            throw;
        }

        MarkDirty(EditorSelection.ForEntity(EditorSelectionKind.Object, objectToAdd.Id));
        return objectToAdd;
    }

    public Guid AddLight(Light light, string? name = null)
    {
        Guid id = Guid.NewGuid();
        _lightStore.Add(id, _lightCodec.Describe(id, name, light));
        MarkDirty(EditorSelection.ForLight(id, default));
        return id;
    }

    public bool DeleteSelection()
    {
        Gizmos.Cancel(this);
        bool deleted = Selection.Kind switch
        {
            EditorSelectionKind.Object => RemoveSelectedObject(),
            EditorSelectionKind.ReflectionProbe =>
                Remove<ReflectionProbe>(_scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.GiVolume => Remove<GlobalIlluminationProbeVolume>(_scene.FindById(Selection.Id),
                _scene.Remove),
            EditorSelectionKind.FoliagePatch => Remove<Njulf.Core.Foliage.FoliagePatch>(_scene.FindById(Selection.Id),
                _scene.Remove),
            EditorSelectionKind.FoliagePrototype => Remove<Njulf.Core.Foliage.FoliagePrototype>(
                _scene.FindById(Selection.Id), _scene.Remove),
            EditorSelectionKind.ParticleEffect => Remove<ParticleEffectInstance>(_scene.FindById(Selection.Id),
                _scene.Remove),
            EditorSelectionKind.InstanceBatch => Remove<StaticInstanceBatch>(_scene.FindById(Selection.Id),
                _scene.Remove),
            EditorSelectionKind.Light when !IsImportedModelLight(Selection.Id) =>
                _lightStore.TryRemove(Selection.Id),
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
        if (Selection.Kind != EditorSelectionKind.Light || GetSelectedLightDocument() is not { } source)
            return false;
        return UpdateLightDocument(
            LightManagerSceneLightStore.Describe(source.Id, source.Name, light, source.IesProfile));
    }

    public bool SetSelectedLightName(string name)
    {
        if (Selection.Kind != EditorSelectionKind.Light || GetSelectedLightDocument() is not { } source)
            return false;
        var authored = SceneLightStore.FromDocument(source.Id, source);
        authored.Name = name;
        return UpdateLightDocument(SceneLightStore.ToDocument(authored));
    }

    private bool UpdateLightDocument(SceneLightDocument document)
    {
        bool updated = IsSceneLightSuspended(document.Id)
            ? GetImportedModelLightController().TryUpdateSuspendedDirectionalLight(document)
            : IsImportedModelLight(document.Id)
                ? GetImportedModelLightController().TryUpdateImportedLight(document)
                : _lightStore.TryUpdate(document.Id, document);
        if (updated) IsDirty = true;
        return updated;
    }

    /// <summary>Returns scene lights, including imported lights and temporarily replaced scene suns.</summary>
    public IReadOnlyList<LightRecord> GetLights()
    {
        return _lightStore.Enumerate()
            .Concat(GetImportedModelLightController().GetSuspendedDirectionalLights())
            .Select(light =>
            {
                _lightManager.TryGetLightHandle(light.Id, out LightHandle handle);
                return new LightRecord(handle, light.Id, light.Name, _lightCodec.Resolve(light));
            }).ToArray();
    }

    public bool SelectedLightIsImported => IsImportedModelLight(Selection.Id);
    public bool SelectedLightHasOverrides => GetImportedModelLightController().HasLightOverride(Selection.Id);
    public bool IsSceneLightImported(Guid id) => IsImportedModelLight(id);

    public bool IsSceneLightSuspended(Guid id) =>
        GetImportedModelLightController().GetSuspendedDirectionalLights().Any(light => light.Id == id);

    public void ResetSelectedLightOverrides()
    {
        if (GetImportedModelLightController().ResetLightOverride(Selection.Id))
            IsDirty = true;
    }

    public SceneLightDocument? GetSelectedLightDocument() =>
        _lightStore.Enumerate().FirstOrDefault(light => light.Id == Selection.Id) ??
        GetImportedModelLightController().GetSuspendedDirectionalLights()
            .FirstOrDefault(light => light.Id == Selection.Id);

    public void SetSelectedLightIesProfile(string? path)
    {
        if (!TryGetSelectedLight(out var light) || GetSelectedLightDocument() is not { } source) return;
        if (!AnalyticalLightGeometry.IsPunctual(light.Type))
            throw new InvalidOperationException("IES profiles apply to point and spot lights.");
        SceneAssetReferenceDocument? profile = string.IsNullOrWhiteSpace(path)
            ? null
            : new SceneAssetReferenceDocument(path);
        if (profile is not null && _lightManager.PhotometricProfiles is { } profiles &&
            !profiles.TryResolve(profile, out _))
            throw new InvalidOperationException("Could not load the IES profile. Check the path and file format.");
        UpdateLightDocument(LightManagerSceneLightStore.Describe(source.Id, source.Name, light, profile));
    }

    public ImportedModelLightEditorStatus GetImportedModelLightStatus()
    {
        ModelLightRuntimeController controller = GetImportedModelLightController();
        return new ImportedModelLightEditorStatus(
            controller.ImportedModelLightsEnabled,
            controller.ModelPlacementCount,
            controller.ModelPlacementsWithLightsCount,
            controller.ImportedLightDefinitionCount,
            controller.ActiveLightCount,
            controller.LastError)
        {
            DirectionalEnabled = controller.ImportedDirectionalLightEnabled,
            ShadowsEnabled = controller.ImportedModelLightShadowsEnabled,
            DirectionalDefinitionCount = controller.ImportedDirectionalLightDefinitionCount,
            ZeroIntensityDefinitionCount = controller.ImportedZeroIntensityLightDefinitionCount
        };
    }

    public void SetImportedModelLightsEnabled(bool enabled)
    {
        ModelLightRuntimeController controller = GetImportedModelLightController();
        if (controller.ImportedModelLightsEnabled == enabled)
            return;
        controller.SetImportedModelLightsEnabled(enabled);
        IsDirty = true;
    }

    public void SetImportedModelLightShadowsEnabled(bool enabled)
    {
        ModelLightRuntimeController controller = GetImportedModelLightController();
        if (controller.ImportedModelLightShadowsEnabled == enabled) return;
        controller.SetImportedModelLightShadowsEnabled(enabled);
        IsDirty = true;
    }

    public void SetImportedDirectionalLightEnabled(bool enabled)
    {
        ModelLightRuntimeController controller = GetImportedModelLightController();
        if (controller.ImportedDirectionalLightEnabled == enabled)
            return;
        controller.SetImportedDirectionalLightEnabled(enabled);
        IsDirty = true;
    }

    public bool TryGetSelectedLight(out Light light)
    {
        if (Selection.Kind == EditorSelectionKind.Light && GetSelectedLightDocument() is { } source)
        {
            light = _lightCodec.Resolve(source);
            return true;
        }

        light = default;
        return false;
    }

    public bool UpdateSelectedMaterialDefinition(MaterialDefinition definition)
        => ApplySelectedMaterial(definition, default);

    private bool ApplySelectedMaterial(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!TryGetSelectedObject(out RenderObject? target) || target!.Material is not { } material)
            return false;
        switch (MaterialScope)
        {
            case MaterialEditScope.ThisObject: target.UpdateMaterial(definition, textures); break;
            case MaterialEditScope.SharedMaterial: material.UpdateShared(definition, textures); break;
            default: throw new ArgumentOutOfRangeException(nameof(MaterialScope));
        }
        IsDirty = true;
        return true;
    }

    /// <summary>Assigns a file texture, or clears the slot when path is empty, using the current edit scope.</summary>
    public bool SetSelectedMaterialTexture(MaterialTextureSlot slot, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (slot is < MaterialTextureSlot.BaseColor or > MaterialTextureSlot.Emissive)
            throw new ArgumentOutOfRangeException(nameof(slot));
        if (!TryGetSelectedMaterialDefinition(out MaterialDefinition? definition)) return false;
        using Texture? texture = path.Length == 0 ? null : _materialManager.LoadMaterialTexture(path, slot);
        return ApplySelectedMaterial(definition!, [new(slot, texture)]);
    }

    public string? GetSelectedMaterialTexturePath(MaterialTextureSlot slot) =>
        TryGetSelectedObject(out RenderObject? target) && target!.Material is { } material
            ? _materialManager.GetMaterialTexturePath(material, slot) : null;

    public bool TryGetSelectedObject(out RenderObject? renderObject)
    {
        renderObject = Selection.Kind == EditorSelectionKind.Object
            ? _scene.FindById(Selection.Id) as RenderObject
            : null;
        return renderObject != null;
    }

    public bool TryGetSelectedMaterialDefinition(out MaterialDefinition? material)
    {
        if (TryGetSelectedObject(out RenderObject? target) &&
            (target?.Material).TryGetMaterialHandle(out MaterialHandle handle))
        {
            try
            {
                material = target!.Material!.Definition;
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
        if (TryGetSelectedObject(out RenderObject? target) &&
            (target?.Material).TryGetMaterialHandle(out MaterialHandle handle))
        {
            try
            {
                inspection = new EditorMaterialInspection(
                    handle,
                    target!.Material!.Definition,
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

    public Guid AddLightAtCamera(LightType type)
    {
        FirstPersonCamera camera =
            Camera ?? throw new InvalidOperationException("An editor camera is required to add a light.");
        return AddLight(new Light
            {
                Type = type,
                Position = new System.Numerics.Vector3(camera.Position.X, camera.Position.Y, camera.Position.Z),
                Direction = new System.Numerics.Vector3(camera.Forward.X, camera.Forward.Y, camera.Forward.Z),
                Up = new System.Numerics.Vector3(camera.Up.X, camera.Up.Y, camera.Up.Z),
                Size = type switch
                {
                    LightType.Tube => new System.Numerics.Vector2(2f, 0.25f),
                    _ => new System.Numerics.Vector2(1f, 1f)
                },
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
                                              throw new InvalidOperationException(
                                                  "A Vulkan renderer is required to edit live GI settings.");
        if (settings.SimpleDdgiAuthoredVolumes.Count >= GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount)
            throw new InvalidOperationException(
                $"Simple DDGI supports at most {GlobalIlluminationSettings.MaxSimpleDdgiVolumeCount} authored overrides.");

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
                                  throw new InvalidOperationException(
                                      "A Vulkan renderer is required to save live render settings.");
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
        FirstPersonCamera camera =
            Camera ?? throw new InvalidOperationException("An editor camera is required to add an object.");
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
                _renderer.DebugDraw.Sphere(new Vector3(light.Position.X, light.Position.Y, light.Position.Z), 0.45f,
                    color, depthMode: DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.ReflectionProbe when _scene.FindById(Selection.Id) is ReflectionProbe probe:
                _renderer.DebugDraw.OrientedBox(
                    probe.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(probe.Position), probe.BoxExtents, color,
                    depthMode: DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.GiVolume
                when _scene.FindById(Selection.Id) is GlobalIlluminationProbeVolume volume:
                _renderer.DebugDraw.Box(new BoundingBox(volume.Origin, volume.Origin + volume.Size), color,
                    DebugDrawDepthMode.XRay);
                break;
            case EditorSelectionKind.FoliagePatch
                when _scene.FindById(Selection.Id) is Njulf.Core.Foliage.FoliagePatch patch:
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
        Gizmos.Cancel(this);
        if (ScenePath == null)
            throw new InvalidOperationException(
                "No scene path is configured. Use Save As before saving a code-built scene.");
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
        Gizmos.Cancel(this);
        if (ScenePath == null)
            throw new InvalidOperationException("No scene path is configured.");
        SceneDocument document = SceneDocumentJson.Read(ScenePath);
        _scene.Clear();
        _scene.Id = document.Id;
        _lightStore.Clear();
        new SceneDocumentLoader(_content).Populate(document, _scene, _lightStore, materials: _materialStore);
        ModelLightRuntimeController.Attach(
            _scene,
            _content,
            _lightStore,
            _loadModel);
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
        } while (_scene.GlobalIlluminationProbeVolumes.Any(volume =>
                     string.Equals(volume.Name, name, StringComparison.OrdinalIgnoreCase)));

        return name;
    }

    public bool SelectEntity(EditorSelectionKind kind, Guid id)
    {
        if (kind == EditorSelectionKind.Light)
        {
            if (!_scene.Lights.Any(light => light.Id == id) && !IsSceneLightSuspended(id))
                return false;
            Select(EditorSelection.ForLight(id, default));
            return true;
        }

        if (_scene.FindById(id) == null)
            return false;
        Select(EditorSelection.ForEntity(kind, id));
        return true;
    }

    public bool UpdateSelectedObject(string name, bool visible, bool isStatic, Vector3 position, Quaternion rotation,
        Vector3 scale)
    {
        if (Selection.Kind != EditorSelectionKind.Object || _scene.FindById(Selection.Id) is not RenderObject target)
            return false;
        target.Name = name;
        target.Visible = visible;
        target.IsStatic = isStatic;
        ApplyGizmoTransform(target, new(position, rotation, scale), true);
        return true;
    }

    internal void ApplyGizmoTransform(RenderObject target, GizmoTransform value, bool markDirty)
    {
        target.Position = value.Position; target.Rotation = value.Rotation; target.Scale = value.Scale;
        if (markDirty) IsDirty = true;
    }

    private void Select(EditorSelection selection)
    {
        if (Selection == selection)
            return;
        Gizmos.Cancel(this);
        Selection = selection;
        MaterialScope = MaterialEditScope.ThisObject;
        SelectionChanged?.Invoke(selection);
    }

    private ModelLightRuntimeController GetImportedModelLightController() =>
        _scene.GetComponent<ModelLightRuntimeController>() ??
        ModelLightRuntimeController.Attach(
            _scene,
            _content,
            _lightStore,
            _loadModel);

    private bool IsImportedModelLight(Guid id) =>
        _scene.GetComponent<ModelLightRuntimeController>()?.IsImportedLight(id) == true;

    private static RenderObject? SelectOne(Model model, string selector)
    {
        if (selector == "*")
            return model.RenderObjects.Count == 1 ? model.CreateRenderObjectInstance(0) : null;
        if (int.TryParse(selector, out int index))
            return index >= 0 && index < model.RenderObjects.Count ? model.CreateRenderObjectInstance(index) : null;
        for (int i = 0; i < model.RenderObjects.Count; i++)
            if (string.Equals(model.RenderObjects[i].Name, selector, StringComparison.Ordinal))
                return model.CreateRenderObjectInstance(i);
        return null;
    }

    private bool RemoveSelectedObject()
    {
        if (_scene.FindById(Selection.Id) is not RenderObject child) return false;
        if (_scene.FindOwningInstance(child) is { } instance)
            _scene.Remove(instance);
        else
            _scene.Remove(child);
        return true;
    }

    private static bool Remove<T>(IIdentifiedSceneEntity? value, Action<T> remove)
        where T : class, IIdentifiedSceneEntity
    {
        if (value is not T typed)
            return false;
        remove(typed);
        return true;
    }
}

public readonly record struct ImportedModelLightEditorStatus(
    bool Enabled,
    int ModelPlacementCount,
    int ModelPlacementsWithLightsCount,
    int ImportedLightDefinitionCount,
    int ActiveLightCount,
    string? Error)
{
    public bool DirectionalEnabled { get; init; }
    public bool ShadowsEnabled { get; init; }
    public int DirectionalDefinitionCount { get; init; }
    public int ZeroIntensityDefinitionCount { get; init; }
}
