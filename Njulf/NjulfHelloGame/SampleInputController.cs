using System;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Debug;
using Njulf.Input;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using Silk.NET.Input;

namespace NjulfHelloGame;

internal sealed class SampleInputController
{
    private const string MoveForward = "move_forward";
    private const string MoveBackward = "move_backward";
    private const string MoveLeft = "move_left";
    private const string MoveRight = "move_right";
    private const string MoveUp = "move_up";
    private const string MoveDown = "move_down";
    private const string LookLeft = "look_left";
    private const string LookRight = "look_right";
    private const string LookUp = "look_up";
    private const string LookDown = "look_down";
    private const string ExitGame = "exit";
    private const string FullModelView = "full_model_view";
    private const string InteriorView = "interior_view";
    private const string ForestFoliageView = "forest_foliage_view";
    private const string ToggleHiZ = "toggle_hiz";
    private const string ToggleTransparent = "toggle_transparent";
    private const string ToggleMeshletDebug = "toggle_meshlet_debug";
    private const string CycleToneMapper = "cycle_tone_mapper";
    private const string ToggleRawHdr = "toggle_raw_hdr";
    private const string ToggleBloom = "toggle_bloom";
    private const string ToggleShadows = "toggle_shadows";
    private const string ToggleSpotShadows = "toggle_spot_shadows";
    private const string TogglePointShadows = "toggle_point_shadows";
    private const string CycleShadowDebug = "cycle_shadow_debug";
    private const string CycleShadowCascadeCount = "cycle_shadow_cascade_count";
    private const string SpotShadowBudgetDown = "spot_shadow_budget_down";
    private const string SpotShadowBudgetUp = "spot_shadow_budget_up";
    private const string PointShadowBudgetDown = "point_shadow_budget_down";
    private const string PointShadowBudgetUp = "point_shadow_budget_up";
    private const string ShadowNormalBiasDown = "shadow_normal_bias_down";
    private const string ShadowNormalBiasUp = "shadow_normal_bias_up";
    private const string SpotShadowBiasDown = "spot_shadow_bias_down";
    private const string SpotShadowBiasUp = "spot_shadow_bias_up";
    private const string PointShadowBiasDown = "point_shadow_bias_down";
    private const string PointShadowBiasUp = "point_shadow_bias_up";
    private const string CycleBloomDebug = "cycle_bloom_debug";
    private const string CycleBloomDebugMip = "cycle_bloom_debug_mip";
    private const string BloomIntensityDown = "bloom_intensity_down";
    private const string BloomIntensityUp = "bloom_intensity_up";
    private const string BloomThresholdDown = "bloom_threshold_down";
    private const string BloomThresholdUp = "bloom_threshold_up";
    private const string BloomRadiusDown = "bloom_radius_down";
    private const string BloomRadiusUp = "bloom_radius_up";
    private const string ExposureDown = "exposure_down";
    private const string ExposureUp = "exposure_up";
    private const string ToggleAmbientOcclusion = "toggle_ambient_occlusion";
    private const string CycleAmbientOcclusionDebug = "cycle_ambient_occlusion_debug";
    private const string AmbientOcclusionRadiusDown = "ambient_occlusion_radius_down";
    private const string AmbientOcclusionRadiusUp = "ambient_occlusion_radius_up";
    private const string AmbientOcclusionIntensityDown = "ambient_occlusion_intensity_down";
    private const string AmbientOcclusionIntensityUp = "ambient_occlusion_intensity_up";
    private const string CycleAntiAliasingMode = "cycle_anti_aliasing_mode";
    private const string CycleAntiAliasingDebug = "cycle_anti_aliasing_debug";
    private const string ToggleFog = "toggle_fog";
    private const string CycleFogDebug = "cycle_fog_debug";
    private const string FogDensityDown = "fog_density_down";
    private const string FogDensityUp = "fog_density_up";
    private const string FogHeightDensityDown = "fog_height_density_down";
    private const string FogHeightDensityUp = "fog_height_density_up";
    private const string FogStartDistanceDown = "fog_start_distance_down";
    private const string FogStartDistanceUp = "fog_start_distance_up";
    private const string ToggleFogInscattering = "toggle_fog_inscattering";
    private const string ToggleReflections = "toggle_reflections";
    private const string CycleReflectionMode = "cycle_reflection_mode";
    private const string CycleReflectionDebug = "cycle_reflection_debug";
    private const string ToggleReflectionBoxProjection = "toggle_reflection_box_projection";
    private const string ToggleParticles = "toggle_particles";
    private const string CycleParticleDebug = "cycle_particle_debug";
    private const string PauseParticles = "pause_particles";
    private const string RestartParticlesFixedSeed = "restart_particles_fixed_seed";
    private const string ToggleSoftParticles = "toggle_soft_particles";
    private const string ToggleDebugTooling = "toggle_debug_tooling";
    private const string CycleDebugOverlay = "cycle_debug_overlay";
    private const string RequestScreenshot = "request_screenshot";
    private const string RequestRenderDocCapture = "request_renderdoc_capture";
    private const string PrintSelectedObject = "print_selected_object";
    private const float CameraSpeed = 3.0f;
    private const float KeyboardLookSpeed = 1.75f;
    private const float MouseSensitivity = 0.0025f;
    private static readonly Vector3 FullModelPosition = new(0f, 5.5f, 18f);
    private const float FullModelYaw = 0f;
    private const float FullModelPitch = -0.22f;
    private static readonly Vector3 InteriorPosition = new(0f, 1.25f, 5.5f);
    private const float InteriorYaw = 0f;
    private const float InteriorPitch = -0.12f;
    private static readonly Vector3 ForestFoliagePosition = new(0f, 1.6f, 5.5f);
    private const float ForestFoliageYaw = 0f;
    private const float ForestFoliagePitch = -0.14f;
    private static readonly SampleActionBinding[] DefaultActionBindings =
    [
        new(MoveForward, Key.W),
        new(MoveBackward, Key.S),
        new(MoveLeft, Key.A),
        new(MoveRight, Key.D),
        new(MoveUp, Key.E),
        new(MoveDown, Key.Q),
        new(LookLeft, Key.Left),
        new(LookRight, Key.Right),
        new(LookUp, Key.Up),
        new(LookDown, Key.Down),
        new(ExitGame, Key.Escape),
        new(FullModelView, Key.Number1),
        new(InteriorView, Key.Number2),
        new(ForestFoliageView, Key.Number3),
        new(CycleToneMapper, Key.F4),
        new(ToggleBloom, Key.F5),
        new(ToggleShadows, Key.F1),
        new(ToggleSpotShadows, Key.F12),
        new(TogglePointShadows, Key.Number4),
        new(ToggleAmbientOcclusion, Key.Number5),
        new(CycleAmbientOcclusionDebug, Key.Number6),
        new(CycleAntiAliasingMode, Key.Number7),
        new(CycleAntiAliasingDebug, Key.Number8),
        new(CycleShadowDebug, Key.F2),
        new(CycleShadowCascadeCount, Key.F3),
        new(CycleBloomDebug, Key.F6),
        new(CycleBloomDebugMip, Key.F7),
        new(ToggleRawHdr, Key.F11),
        new(ToggleHiZ, Key.F8),
        new(ToggleTransparent, Key.F9),
        new(ToggleMeshletDebug, Key.F10),
        new(BloomIntensityDown, Key.PageDown),
        new(BloomIntensityUp, Key.PageUp),
        new(BloomThresholdDown, Key.End),
        new(BloomThresholdUp, Key.Home),
        new(BloomRadiusDown, Key.Delete),
        new(BloomRadiusUp, Key.Insert),
        new(ExposureDown, Key.LeftBracket),
        new(ExposureUp, Key.RightBracket),
        new(AmbientOcclusionRadiusDown, Key.J),
        new(AmbientOcclusionRadiusUp, Key.U),
        new(AmbientOcclusionIntensityDown, Key.M),
        new(AmbientOcclusionIntensityUp, Key.I),
        new(ShadowNormalBiasDown, Key.Comma),
        new(ShadowNormalBiasUp, Key.Period),
        new(SpotShadowBudgetDown, Key.Minus),
        new(SpotShadowBudgetUp, Key.Equal),
        new(PointShadowBudgetDown, Key.Semicolon),
        new(PointShadowBudgetUp, Key.Apostrophe),
        new(SpotShadowBiasDown, Key.K),
        new(SpotShadowBiasUp, Key.L),
        new(PointShadowBiasDown, Key.O),
        new(PointShadowBiasUp, Key.P),
        new(ToggleFog, Key.Z),
        new(CycleFogDebug, Key.X),
        new(FogDensityDown, Key.C),
        new(FogDensityUp, Key.V),
        new(FogHeightDensityDown, Key.B),
        new(FogHeightDensityUp, Key.N),
        new(FogStartDistanceDown, Key.G),
        new(FogStartDistanceUp, Key.H),
        new(ToggleFogInscattering, Key.T),
        new(ToggleReflections, Key.Number0),
        new(CycleReflectionDebug, Key.Number9),
        new(CycleReflectionMode, Key.Y),
        new(ToggleReflectionBoxProjection, Key.R),
        new(ToggleParticles, Key.F),
        new(CycleParticleDebug, Key.Tab),
        new(PauseParticles, Key.Space),
        new(RestartParticlesFixedSeed, Key.Backspace),
        new(ToggleSoftParticles, Key.BackSlash),
        new(ToggleDebugTooling, Key.CapsLock),
        new(CycleDebugOverlay, Key.GraveAccent),
        new(RequestScreenshot, Key.PrintScreen),
        new(RequestRenderDocCapture, Key.ScrollLock),
        new(PrintSelectedObject, Key.Slash)
    ];

    private readonly FirstPersonCamera _camera;
    private readonly IInputManager _input;
    private readonly InputManager? _rawInput;
    private readonly System.Action _exit;
    private readonly Njulf.Rendering.VulkanRenderer? _renderer;
    private readonly LightManager? _lightManager;
    private readonly IReadOnlyList<ParticleEffectInstance> _particleEffects;
    private readonly SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private SampleLightingMode _lightingMode;
    private bool _fullModelPressed;
    private bool _interiorPressed;
    private bool _forestFoliagePressed;
    private bool _toggleHiZPressed;
    private bool _toggleTransparentPressed;
    private bool _toggleMeshletDebugPressed;
    private bool _cycleToneMapperPressed;
    private bool _toggleRawHdrPressed;
    private bool _toggleBloomPressed;
    private bool _toggleShadowsPressed;
    private bool _toggleSpotShadowsPressed;
    private bool _togglePointShadowsPressed;
    private bool _cycleShadowDebugPressed;
    private bool _cycleShadowCascadeCountPressed;
    private bool _cycleLightingModePressed;
    private bool _spotShadowBudgetDownPressed;
    private bool _spotShadowBudgetUpPressed;
    private bool _pointShadowBudgetDownPressed;
    private bool _pointShadowBudgetUpPressed;
    private bool _shadowNormalBiasDownPressed;
    private bool _shadowNormalBiasUpPressed;
    private bool _spotShadowBiasDownPressed;
    private bool _spotShadowBiasUpPressed;
    private bool _pointShadowBiasDownPressed;
    private bool _pointShadowBiasUpPressed;
    private bool _cycleBloomDebugPressed;
    private bool _cycleBloomDebugMipPressed;
    private bool _bloomIntensityDownPressed;
    private bool _bloomIntensityUpPressed;
    private bool _bloomThresholdDownPressed;
    private bool _bloomThresholdUpPressed;
    private bool _bloomRadiusDownPressed;
    private bool _bloomRadiusUpPressed;
    private bool _toggleAutoExposurePressed;
    private bool _exposureDownPressed;
    private bool _exposureUpPressed;
    private bool _toggleAmbientOcclusionPressed;
    private bool _cycleAmbientOcclusionDebugPressed;
    private bool _ambientOcclusionRadiusDownPressed;
    private bool _ambientOcclusionRadiusUpPressed;
    private bool _ambientOcclusionIntensityDownPressed;
    private bool _ambientOcclusionIntensityUpPressed;
    private bool _cycleAntiAliasingModePressed;
    private bool _cycleAntiAliasingDebugPressed;
    private bool _toggleFogPressed;
    private bool _cycleFogDebugPressed;
    private bool _fogDensityDownPressed;
    private bool _fogDensityUpPressed;
    private bool _fogHeightDensityDownPressed;
    private bool _fogHeightDensityUpPressed;
    private bool _fogStartDistanceDownPressed;
    private bool _fogStartDistanceUpPressed;
    private bool _toggleFogInscatteringPressed;
    private bool _toggleReflectionsPressed;
    private bool _cycleReflectionModePressed;
    private bool _cycleReflectionDebugPressed;
    private bool _toggleReflectionBoxProjectionPressed;
    private bool _toggleParticlesPressed;
    private bool _cycleParticleDebugPressed;
    private bool _pauseParticlesPressed;
    private bool _restartParticlesFixedSeedPressed;
    private bool _toggleSoftParticlesPressed;
    private bool _toggleDebugToolingPressed;
    private bool _cycleDebugOverlayPressed;
    private bool _requestScreenshotPressed;
    private bool _requestRenderDocCapturePressed;
    private bool _cycleBudgetProfilePressed;
    private bool _exportPerformanceSnapshotPressed;
    private bool _cyclePerformanceScenarioPressed;
    private bool _toggleGpuTimingPressed;
    private bool _cycleQualityPresetPressed;
    private bool _cycleFeatureIsolationPressed;
    private bool _toggleSecondaryCommandBuffersPressed;
    private bool _toggleFoliageIndirectDispatchPressed;
    private bool _toggleFoliageFarImpostorsPressed;
    private bool _cycleFoliageDebugPressed;
    private bool _toggleSceneGpuCompactionPressed;
    private bool _toggleSceneIndirectDispatchPressed;
    private bool _cycleMaterialDebugPressed;
    private bool _cycleAnimationDebugPressed;
    private bool _previousSelectedObjectPressed;
    private bool _nextSelectedObjectPressed;
    private bool _printSelectedObjectPressed;
    private bool _particlesPaused;

    public SampleInputController(
        FirstPersonCamera camera,
        IInputManager input,
        System.Action exit,
        Njulf.Rendering.VulkanRenderer? renderer = null,
        LightManager? lightManager = null,
        SampleLightingMode lightingMode = SampleLightingMode.DirectionalKey,
        IReadOnlyList<ParticleEffectInstance>? particleEffects = null,
        SamplePerformanceScenarioRunner? performanceScenarioRunner = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _rawInput = input as InputManager;
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _renderer = renderer;
        _lightManager = lightManager;
        _lightingMode = lightingMode;
        _particleEffects = particleEffects ?? Array.Empty<ParticleEffectInstance>();
        _performanceScenarioRunner = performanceScenarioRunner;
    }

    public static void Configure(InputManager input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        foreach (SampleActionBinding binding in DefaultActionBindings)
            CreateKeyboardAction(input, binding.ActionId, binding.Key);
    }

    public void Update(float deltaTime, int viewportWidth, int viewportHeight)
    {
        if (_input.IsKeyDown(ExitGame))
            _exit();

        if (WasPressed(FullModelView, ref _fullModelPressed))
        {
            ApplyPerformanceScenario(SamplePerformanceScenario.Normal);
            MoveCamera(FullModelPosition, FullModelYaw, FullModelPitch);
        }

        if (WasPressed(InteriorView, ref _interiorPressed))
        {
            ApplyPerformanceScenario(SamplePerformanceScenario.Normal);
            MoveCamera(InteriorPosition, InteriorYaw, InteriorPitch);
        }

        if (WasPressed(ForestFoliageView, ref _forestFoliagePressed))
        {
            ApplyPerformanceScenario(SamplePerformanceScenario.ForestFoliage);
            MoveCamera(ForestFoliagePosition, ForestFoliageYaw, ForestFoliagePitch);
        }

        if (_renderer != null && WasPressed(ToggleHiZ, ref _toggleHiZPressed))
        {
            _renderer.EnableHiZOcclusion = !_renderer.EnableHiZOcclusion;
            Console.WriteLine($"Hi-Z occlusion: {(_renderer.EnableHiZOcclusion ? "enabled" : "disabled")}");
        }

        if (_renderer != null && WasPressed(ToggleTransparent, ref _toggleTransparentPressed))
        {
            _renderer.EnableTransparentPass = !_renderer.EnableTransparentPass;
            _renderer.Settings.Transparency.Enabled = _renderer.EnableTransparentPass;
            PrintTransparencySettings("Transparent pass");
        }

        if (_renderer != null && WasPressed(ToggleMeshletDebug, ref _toggleMeshletDebugPressed))
        {
            _renderer.EnableMeshletDebugView = !_renderer.EnableMeshletDebugView;
            Console.WriteLine($"Meshlet debug view: {(_renderer.EnableMeshletDebugView ? "enabled" : "disabled")}");
        }

        if (_renderer != null && WasChordPressed(Key.F1, ref _cycleBudgetProfilePressed))
            CyclePerformanceBudgetProfile();

        if (_renderer != null && WasChordPressed(Key.F2, ref _exportPerformanceSnapshotPressed))
            ExportPerformanceSnapshotFile();

        if (WasChordPressed(Key.F3, ref _cyclePerformanceScenarioPressed))
            CyclePerformanceScenarioSet();

        if (_lightManager != null && WasChordPressed(Key.Number3, ref _cycleLightingModePressed))
            CycleLightingModeSet();

        if (_renderer != null && WasChordPressed(Key.F4, ref _toggleGpuTimingPressed))
        {
            _renderer.Settings.Debug.AllowGpuTiming = !_renderer.Settings.Debug.AllowGpuTiming;
            RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
            Console.WriteLine(
                $"GPU timing: {(_renderer.Settings.Debug.AllowGpuTiming ? "enabled" : "disabled")}, " +
                $"supported={diagnostics.GpuTimingSupported}, valid={diagnostics.GpuTimingValid}, reason='{diagnostics.GpuTimingUnavailableReason}'");
        }

        if (_renderer != null && WasChordPressed(Key.F5, ref _cycleQualityPresetPressed))
            CycleQualityPreset();

        if (_renderer != null && WasChordPressed(Key.F6, ref _cycleFeatureIsolationPressed))
            CycleFeatureIsolation();

        if (_renderer != null && WasChordPressed(Key.F7, ref _toggleSecondaryCommandBuffersPressed))
        {
            _renderer.Settings.UseSecondaryCommandBuffers = !_renderer.Settings.UseSecondaryCommandBuffers;
            RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
            Console.WriteLine(
                $"Secondary command buffers: {(_renderer.Settings.UseSecondaryCommandBuffers ? "enabled" : "disabled")}, " +
                $"passes={diagnostics.SecondaryCommandBufferPassCount}, secondaryRecordUs={diagnostics.CpuSecondaryCommandRecordMicroseconds}");
        }

        if (_renderer != null && WasChordPressed(Key.F8, ref _toggleFoliageIndirectDispatchPressed))
        {
            _renderer.Settings.Foliage.IndirectMeshletDispatchEnabled = !_renderer.Settings.Foliage.IndirectMeshletDispatchEnabled;
            PrintFoliageSettings("Foliage dispatch");
        }

        if (_renderer != null && WasChordPressed(Key.F9, ref _toggleFoliageFarImpostorsPressed))
        {
            _renderer.Settings.Foliage.FarImpostorsEnabled = !_renderer.Settings.Foliage.FarImpostorsEnabled;
            PrintFoliageSettings("Foliage impostors");
        }

        if (_renderer != null && WasChordPressed(Key.F10, ref _cycleFoliageDebugPressed))
        {
            _renderer.Settings.Foliage.DebugView = NextFoliageDebugView(_renderer.Settings.Foliage.DebugView);
            PrintFoliageSettings("Foliage debug");
        }

        if (_renderer != null && WasChordPressed(Key.F11, ref _toggleSceneGpuCompactionPressed))
        {
            _renderer.Settings.SceneSubmission.GpuCompactionEnabled = !_renderer.Settings.SceneSubmission.GpuCompactionEnabled;
            PrintSceneSubmissionSettings("Scene GPU compaction");
        }

        if (_renderer != null && WasChordPressed(Key.F12, ref _toggleSceneIndirectDispatchPressed))
        {
            _renderer.Settings.SceneSubmission.IndirectMeshletDispatchEnabled = !_renderer.Settings.SceneSubmission.IndirectMeshletDispatchEnabled;
            PrintSceneSubmissionSettings("Scene indirect dispatch");
        }

        if (_renderer != null && WasChordPressed(Key.M, ref _cycleMaterialDebugPressed))
        {
            _renderer.Settings.Materials.DebugView = NextMaterialDebugView(_renderer.Settings.Materials.DebugView);
            PrintMaterialSettings("Material debug");
        }

        if (_renderer != null && WasChordPressed(Key.A, ref _cycleAnimationDebugPressed))
        {
            _renderer.Settings.Animation.DebugView = NextAnimationDebugView(_renderer.Settings.Animation.DebugView);
            PrintAnimationSettings("Animation debug");
        }

        if (_renderer != null && WasPressed(CycleToneMapper, ref _cycleToneMapperPressed))
        {
            _renderer.Settings.ToneMapper = _renderer.Settings.ToneMapper switch
            {
                ToneMapper.AcesFitted => ToneMapper.None,
                ToneMapper.None => ToneMapper.Reinhard,
                _ => ToneMapper.AcesFitted
            };
            Console.WriteLine($"Tone mapper: {_renderer.Settings.ToneMapper}");
        }

        if (_renderer != null && WasPressed(ToggleRawHdr, ref _toggleRawHdrPressed))
        {
            _renderer.Settings.ShowRawHdrSceneColor = !_renderer.Settings.ShowRawHdrSceneColor;
            Console.WriteLine($"Raw HDR view: {(_renderer.Settings.ShowRawHdrSceneColor ? "enabled" : "disabled")}");
        }

        if (_renderer != null && WasChordPressed(Key.LeftBracket, ref _toggleAutoExposurePressed))
        {
            _renderer.Settings.AutoExposure.Enabled = !_renderer.Settings.AutoExposure.Enabled;
            PrintExposureSettings("Auto exposure");
        }

        if (_renderer != null && WasPressed(ToggleBloom, ref _toggleBloomPressed))
        {
            _renderer.Settings.Bloom.Enabled = !_renderer.Settings.Bloom.Enabled;
            PrintBloomSettings("Bloom");
        }

        if (_renderer != null && WasPressed(ToggleShadows, ref _toggleShadowsPressed))
        {
            _renderer.Settings.Shadows.DirectionalShadowsEnabled = !_renderer.Settings.Shadows.DirectionalShadowsEnabled;
            PrintShadowSettings("Shadows");
        }

        if (_renderer != null && WasPressed(ToggleSpotShadows, ref _toggleSpotShadowsPressed))
        {
            _renderer.Settings.Shadows.SpotShadowsEnabled = !_renderer.Settings.Shadows.SpotShadowsEnabled;
            PrintShadowSettings("Spot shadows");
        }

        if (_renderer != null && WasPressed(TogglePointShadows, ref _togglePointShadowsPressed))
        {
            _renderer.Settings.Shadows.PointShadowsEnabled = !_renderer.Settings.Shadows.PointShadowsEnabled;
            PrintShadowSettings("Point shadows");
        }

        if (_renderer != null && WasPressed(ToggleAmbientOcclusion, ref _toggleAmbientOcclusionPressed))
        {
            _renderer.Settings.AmbientOcclusion.Enabled = !_renderer.Settings.AmbientOcclusion.Enabled;
            PrintAmbientOcclusionSettings("AO");
        }

        if (_renderer != null && WasPressed(ToggleFog, ref _toggleFogPressed))
        {
            _renderer.Settings.Fog.Enabled = !_renderer.Settings.Fog.Enabled;
            PrintFogSettings("Fog");
        }

        if (_renderer != null && WasPressed(ToggleReflections, ref _toggleReflectionsPressed))
        {
            _renderer.Settings.Reflections.Enabled = !_renderer.Settings.Reflections.Enabled;
            PrintReflectionSettings("Reflections");
        }

        if (_renderer != null && WasPressed(ToggleParticles, ref _toggleParticlesPressed))
        {
            _renderer.Settings.Particles.Enabled = !_renderer.Settings.Particles.Enabled;
            PrintParticleSettings("Particles");
        }

        if (_renderer != null && WasPressed(ToggleDebugTooling, ref _toggleDebugToolingPressed))
        {
            _renderer.Settings.Debug.Enabled = !_renderer.Settings.Debug.Enabled;
            _renderer.Settings.Debug.CpuSnapshotsEnabled = _renderer.Settings.Debug.Enabled;
            _renderer.DebugDraw.Enabled = _renderer.Settings.Debug.Enabled;
            PrintDebugSettings("Debug tooling");
        }

        if (_renderer != null && WasPressed(CycleDebugOverlay, ref _cycleDebugOverlayPressed))
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.Settings.Debug.Mode = NextDebugOverlay(_renderer.Settings.Debug.Mode);
            _renderer.Settings.Debug.CpuSnapshotsEnabled = RequiresCpuSnapshots(_renderer.Settings.Debug.Mode);
            PrintDebugSettings("Debug overlay");
        }

        if (_renderer != null && WasPressed(RequestScreenshot, ref _requestScreenshotPressed))
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.Settings.Debug.AllowScreenshots = true;
            _renderer.RequestScreenshot();
            Console.WriteLine("Screenshot requested.");
        }

        if (_renderer != null && WasPressed(RequestRenderDocCapture, ref _requestRenderDocCapturePressed))
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.Settings.Debug.AllowRenderDocCapture = true;
            _renderer.RequestRenderDocCapture();
            Console.WriteLine(_renderer.LastDiagnostics.LastRenderDocCaptureMessage.Length == 0
                ? "RenderDoc capture requested."
                : _renderer.LastDiagnostics.LastRenderDocCaptureMessage);
        }

        if (_renderer != null && WasChordPressed(Key.Left, ref _previousSelectedObjectPressed))
            SelectDebugObject(-1);

        if (_renderer != null && WasChordPressed(Key.Right, ref _nextSelectedObjectPressed))
            SelectDebugObject(1);

        if (_renderer != null && WasPressed(PrintSelectedObject, ref _printSelectedObjectPressed))
            PrintSelectedObjectInspection();

        if (_renderer != null && WasPressed(CycleParticleDebug, ref _cycleParticleDebugPressed))
        {
            _renderer.Settings.Particles.DebugView = _renderer.Settings.Particles.DebugView switch
            {
                ParticleDebugView.None => ParticleDebugView.Bounds,
                ParticleDebugView.Bounds => ParticleDebugView.SoftParticleFade,
                ParticleDebugView.SoftParticleFade => ParticleDebugView.FlipbookFrame,
                ParticleDebugView.FlipbookFrame => ParticleDebugView.SortOrder,
                ParticleDebugView.SortOrder => ParticleDebugView.Lifetime,
                ParticleDebugView.Lifetime => ParticleDebugView.Velocity,
                ParticleDebugView.Velocity => ParticleDebugView.BudgetHeatmap,
                _ => ParticleDebugView.None
            };
            PrintParticleSettings("Particle debug");
        }

        if (WasPressed(PauseParticles, ref _pauseParticlesPressed))
        {
            _particlesPaused = !_particlesPaused;
            for (int i = 0; i < _particleEffects.Count; i++)
            {
                if (_particlesPaused)
                    _particleEffects[i].Pause();
                else
                    _particleEffects[i].Play();
            }
            Console.WriteLine($"Particles playback: {(_particlesPaused ? "paused" : "playing")}");
        }

        if (WasPressed(RestartParticlesFixedSeed, ref _restartParticlesFixedSeedPressed))
        {
            for (int i = 0; i < _particleEffects.Count; i++)
                _particleEffects[i].Restart((uint)(1000 + i * 101));
            _particlesPaused = false;
            Console.WriteLine("Particles restarted with fixed sample seeds.");
        }

        if (_renderer != null && WasPressed(ToggleSoftParticles, ref _toggleSoftParticlesPressed))
        {
            _renderer.Settings.Particles.SoftParticlesEnabled = !_renderer.Settings.Particles.SoftParticlesEnabled;
            PrintParticleSettings("Soft particles");
        }

        if (_renderer != null && WasPressed(CycleShadowDebug, ref _cycleShadowDebugPressed))
        {
            _renderer.Settings.Shadows.DebugView = _renderer.Settings.Shadows.DebugView switch
            {
                ShadowDebugView.None => ShadowDebugView.CascadeOverlay,
                ShadowDebugView.CascadeOverlay => ShadowDebugView.ReceiverFactor,
                ShadowDebugView.ReceiverFactor => ShadowDebugView.ShadowMapPreview,
                ShadowDebugView.ShadowMapPreview => ShadowDebugView.SpotAtlasPreview,
                ShadowDebugView.SpotAtlasPreview => ShadowDebugView.LocalShadowSelection,
                _ => ShadowDebugView.None
            };
            PrintShadowSettings("Shadow debug");
        }

        if (_renderer != null && WasPressed(CycleShadowCascadeCount, ref _cycleShadowCascadeCountPressed))
        {
            _renderer.Settings.Shadows.DirectionalCascadeCount =
                _renderer.Settings.Shadows.DirectionalCascadeCount % ShadowSettings.MaxDirectionalCascades + 1;
            PrintShadowSettings("Shadow cascades");
        }

        if (_renderer != null && WasPressed(CycleBloomDebug, ref _cycleBloomDebugPressed))
        {
            _renderer.Settings.Bloom.DebugView = _renderer.Settings.Bloom.DebugView switch
            {
                BloomDebugView.None => BloomDebugView.ExtractMask,
                BloomDebugView.ExtractMask => BloomDebugView.DownsampleMip,
                BloomDebugView.DownsampleMip => BloomDebugView.UpsampleResult,
                BloomDebugView.UpsampleResult => BloomDebugView.BloomOnly,
                _ => BloomDebugView.None
            };
            PrintBloomSettings("Bloom debug");
        }

        if (_renderer != null && WasPressed(CycleBloomDebugMip, ref _cycleBloomDebugMipPressed))
        {
            _renderer.Settings.Bloom.DebugMipLevel = (_renderer.Settings.Bloom.DebugMipLevel + 1) % _renderer.Settings.Bloom.MipCount;
            PrintBloomSettings("Bloom debug mip");
        }

        if (_renderer != null && WasPressed(CycleAmbientOcclusionDebug, ref _cycleAmbientOcclusionDebugPressed))
        {
            _renderer.Settings.AmbientOcclusion.DebugView = _renderer.Settings.AmbientOcclusion.DebugView switch
            {
                AmbientOcclusionDebugView.None => AmbientOcclusionDebugView.RawAo,
                AmbientOcclusionDebugView.RawAo => AmbientOcclusionDebugView.BlurredAo,
                AmbientOcclusionDebugView.BlurredAo => AmbientOcclusionDebugView.FinalAo,
                AmbientOcclusionDebugView.FinalAo => AmbientOcclusionDebugView.ReconstructedNormal,
                AmbientOcclusionDebugView.ReconstructedNormal => AmbientOcclusionDebugView.LinearDepth,
                _ => AmbientOcclusionDebugView.None
            };
            PrintAmbientOcclusionSettings("AO debug");
        }

        if (_renderer != null && WasPressed(CycleFogDebug, ref _cycleFogDebugPressed))
        {
            _renderer.Settings.Fog.DebugView = _renderer.Settings.Fog.DebugView switch
            {
                FogDebugView.None => FogDebugView.FogFactor,
                FogDebugView.FogFactor => FogDebugView.Transmittance,
                FogDebugView.Transmittance => FogDebugView.DistanceFog,
                FogDebugView.DistanceFog => FogDebugView.HeightFog,
                FogDebugView.HeightFog => FogDebugView.Inscattering,
                FogDebugView.Inscattering => FogDebugView.LinearDepth,
                FogDebugView.LinearDepth => FogDebugView.WorldHeight,
                FogDebugView.WorldHeight => FogDebugView.FoggedScene,
                _ => FogDebugView.None
            };
            PrintFogSettings("Fog debug");
        }

        if (_renderer != null && WasPressed(CycleReflectionDebug, ref _cycleReflectionDebugPressed))
        {
            _renderer.Settings.Reflections.DebugView = _renderer.Settings.Reflections.DebugView switch
            {
                ReflectionDebugView.None => ReflectionDebugView.ProbeInfluence,
                ReflectionDebugView.ProbeInfluence => ReflectionDebugView.ProbeIndex,
                ReflectionDebugView.ProbeIndex => ReflectionDebugView.ProbeBlendWeights,
                ReflectionDebugView.ProbeBlendWeights => ReflectionDebugView.ProbeCubemapFace,
                ReflectionDebugView.ProbeCubemapFace => ReflectionDebugView.ProbePrefilterMip,
                ReflectionDebugView.ProbePrefilterMip => ReflectionDebugView.BoxProjectionDirection,
                ReflectionDebugView.BoxProjectionDirection => ReflectionDebugView.LocalReflectionOnly,
                ReflectionDebugView.LocalReflectionOnly => ReflectionDebugView.GlobalFallbackOnly,
                _ => ReflectionDebugView.None
            };
            PrintReflectionSettings("Reflection debug");
        }

        if (_renderer != null && WasPressed(CycleReflectionMode, ref _cycleReflectionModePressed))
        {
            _renderer.Settings.Reflections.Mode = _renderer.Settings.Reflections.Mode switch
            {
                ReflectionMode.GlobalEnvironmentOnly => ReflectionMode.StaticProbes,
                ReflectionMode.StaticProbes => ReflectionMode.GlobalEnvironmentOnly,
                _ => ReflectionMode.StaticProbes
            };
            PrintReflectionSettings("Reflection mode");
        }

        if (_renderer != null && WasPressed(ToggleReflectionBoxProjection, ref _toggleReflectionBoxProjectionPressed))
        {
            _renderer.Settings.Reflections.BoxProjectionEnabled = !_renderer.Settings.Reflections.BoxProjectionEnabled;
            PrintReflectionSettings("Reflection box projection");
        }

        if (_renderer != null && WasPressed(CycleAntiAliasingMode, ref _cycleAntiAliasingModePressed))
        {
            _renderer.Settings.AntiAliasing.Mode = _renderer.Settings.AntiAliasing.Mode switch
            {
                AntiAliasingMode.None => AntiAliasingMode.Fxaa,
                AntiAliasingMode.Fxaa => AntiAliasingMode.SmaaLow,
                AntiAliasingMode.SmaaLow => AntiAliasingMode.SmaaMedium,
                AntiAliasingMode.SmaaMedium => AntiAliasingMode.SmaaHigh,
                AntiAliasingMode.SmaaHigh => AntiAliasingMode.Taa,
                _ => AntiAliasingMode.None
            };
            PrintAntiAliasingSettings("AA mode");
        }

        if (_renderer != null && WasPressed(CycleAntiAliasingDebug, ref _cycleAntiAliasingDebugPressed))
        {
            _renderer.Settings.AntiAliasing.DebugView = _renderer.Settings.AntiAliasing.DebugView switch
            {
                AntiAliasingDebugView.None => AntiAliasingDebugView.InputColor,
                AntiAliasingDebugView.InputColor => AntiAliasingDebugView.FxaaLuma,
                AntiAliasingDebugView.FxaaLuma => AntiAliasingDebugView.SmaaEdges,
                AntiAliasingDebugView.SmaaEdges => AntiAliasingDebugView.SmaaBlendWeights,
                AntiAliasingDebugView.SmaaBlendWeights => AntiAliasingDebugView.MotionVectors,
                AntiAliasingDebugView.MotionVectors => AntiAliasingDebugView.JitterPattern,
                AntiAliasingDebugView.JitterPattern => AntiAliasingDebugView.TaaHistory,
                _ => AntiAliasingDebugView.None
            };
            PrintAntiAliasingSettings("AA debug");
        }

        if (_renderer != null && WasPressed(BloomIntensityDown, ref _bloomIntensityDownPressed))
        {
            _renderer.Settings.Bloom.Intensity -= 0.02f;
            PrintBloomSettings("Bloom intensity");
        }

        if (_renderer != null && WasPressed(BloomIntensityUp, ref _bloomIntensityUpPressed))
        {
            _renderer.Settings.Bloom.Intensity += 0.02f;
            PrintBloomSettings("Bloom intensity");
        }

        if (_renderer != null && WasPressed(BloomThresholdDown, ref _bloomThresholdDownPressed))
        {
            _renderer.Settings.Bloom.Threshold -= 0.1f;
            PrintBloomSettings("Bloom threshold");
        }

        if (_renderer != null && WasPressed(BloomThresholdUp, ref _bloomThresholdUpPressed))
        {
            _renderer.Settings.Bloom.Threshold += 0.1f;
            PrintBloomSettings("Bloom threshold");
        }

        if (_renderer != null && WasPressed(BloomRadiusDown, ref _bloomRadiusDownPressed))
        {
            _renderer.Settings.Bloom.Radius -= 0.05f;
            PrintBloomSettings("Bloom radius");
        }

        if (_renderer != null && WasPressed(BloomRadiusUp, ref _bloomRadiusUpPressed))
        {
            _renderer.Settings.Bloom.Radius += 0.05f;
            PrintBloomSettings("Bloom radius");
        }

        if (_renderer != null && !IsControlDown() && WasPressed(ExposureDown, ref _exposureDownPressed))
            AdjustExposure(0.9f);

        if (_renderer != null && !IsControlDown() && WasPressed(ExposureUp, ref _exposureUpPressed))
            AdjustExposure(1.1f);

        if (_renderer != null && WasPressed(AmbientOcclusionRadiusDown, ref _ambientOcclusionRadiusDownPressed))
        {
            _renderer.Settings.AmbientOcclusion.Radius -= 0.05f;
            PrintAmbientOcclusionSettings("AO radius");
        }

        if (_renderer != null && WasPressed(AmbientOcclusionRadiusUp, ref _ambientOcclusionRadiusUpPressed))
        {
            _renderer.Settings.AmbientOcclusion.Radius += 0.05f;
            PrintAmbientOcclusionSettings("AO radius");
        }

        if (_renderer != null && WasPressed(AmbientOcclusionIntensityDown, ref _ambientOcclusionIntensityDownPressed))
        {
            _renderer.Settings.AmbientOcclusion.Intensity -= 0.05f;
            PrintAmbientOcclusionSettings("AO intensity");
        }

        if (_renderer != null && WasPressed(AmbientOcclusionIntensityUp, ref _ambientOcclusionIntensityUpPressed))
        {
            _renderer.Settings.AmbientOcclusion.Intensity += 0.05f;
            PrintAmbientOcclusionSettings("AO intensity");
        }

        if (_renderer != null && WasPressed(FogDensityDown, ref _fogDensityDownPressed))
        {
            _renderer.Settings.Fog.Density -= 0.0025f;
            PrintFogSettings("Fog density");
        }

        if (_renderer != null && WasPressed(FogDensityUp, ref _fogDensityUpPressed))
        {
            _renderer.Settings.Fog.Density += 0.0025f;
            PrintFogSettings("Fog density");
        }

        if (_renderer != null && WasPressed(FogHeightDensityDown, ref _fogHeightDensityDownPressed))
        {
            _renderer.Settings.Fog.HeightDensity -= 0.005f;
            PrintFogSettings("Fog height density");
        }

        if (_renderer != null && WasPressed(FogHeightDensityUp, ref _fogHeightDensityUpPressed))
        {
            _renderer.Settings.Fog.HeightDensity += 0.005f;
            PrintFogSettings("Fog height density");
        }

        if (_renderer != null && WasPressed(FogStartDistanceDown, ref _fogStartDistanceDownPressed))
        {
            _renderer.Settings.Fog.StartDistance -= 1.0f;
            PrintFogSettings("Fog start distance");
        }

        if (_renderer != null && WasPressed(FogStartDistanceUp, ref _fogStartDistanceUpPressed))
        {
            _renderer.Settings.Fog.StartDistance += 1.0f;
            PrintFogSettings("Fog start distance");
        }

        if (_renderer != null && WasPressed(ToggleFogInscattering, ref _toggleFogInscatteringPressed))
        {
            _renderer.Settings.Fog.DirectionalInscatteringEnabled = !_renderer.Settings.Fog.DirectionalInscatteringEnabled;
            PrintFogSettings("Fog inscattering");
        }

        if (_renderer != null && WasPressed(ShadowNormalBiasDown, ref _shadowNormalBiasDownPressed))
        {
            _renderer.Settings.Shadows.NormalBias -= 0.005f;
            PrintShadowSettings("Shadow normal bias");
        }

        if (_renderer != null && WasPressed(ShadowNormalBiasUp, ref _shadowNormalBiasUpPressed))
        {
            _renderer.Settings.Shadows.NormalBias += 0.005f;
            PrintShadowSettings("Shadow normal bias");
        }

        if (_renderer != null && WasPressed(SpotShadowBudgetDown, ref _spotShadowBudgetDownPressed))
        {
            _renderer.Settings.Shadows.MaxShadowedSpotLights--;
            PrintShadowSettings("Spot shadow budget");
        }

        if (_renderer != null && WasPressed(SpotShadowBudgetUp, ref _spotShadowBudgetUpPressed))
        {
            _renderer.Settings.Shadows.MaxShadowedSpotLights++;
            PrintShadowSettings("Spot shadow budget");
        }

        if (_renderer != null && WasPressed(PointShadowBudgetDown, ref _pointShadowBudgetDownPressed))
        {
            _renderer.Settings.Shadows.MaxShadowedPointLights--;
            PrintShadowSettings("Point shadow budget");
        }

        if (_renderer != null && WasPressed(PointShadowBudgetUp, ref _pointShadowBudgetUpPressed))
        {
            _renderer.Settings.Shadows.MaxShadowedPointLights++;
            PrintShadowSettings("Point shadow budget");
        }

        if (_renderer != null && WasPressed(SpotShadowBiasDown, ref _spotShadowBiasDownPressed))
        {
            _renderer.Settings.Shadows.SpotNormalBias -= 0.005f;
            PrintShadowSettings("Spot shadow bias");
        }

        if (_renderer != null && WasPressed(SpotShadowBiasUp, ref _spotShadowBiasUpPressed))
        {
            _renderer.Settings.Shadows.SpotNormalBias += 0.005f;
            PrintShadowSettings("Spot shadow bias");
        }

        if (_renderer != null && WasPressed(PointShadowBiasDown, ref _pointShadowBiasDownPressed))
        {
            _renderer.Settings.Shadows.PointNormalBias -= 0.005f;
            PrintShadowSettings("Point shadow bias");
        }

        if (_renderer != null && WasPressed(PointShadowBiasUp, ref _pointShadowBiasUpPressed))
        {
            _renderer.Settings.Shadows.PointNormalBias += 0.005f;
            PrintShadowSettings("Point shadow bias");
        }

        float distance = CameraSpeed * deltaTime;

        if (_input.IsKeyDown(MoveForward))
            _camera.MoveForward(distance);
        if (_input.IsKeyDown(MoveBackward))
            _camera.MoveBackward(distance);
        if (_input.IsKeyDown(MoveLeft))
            _camera.MoveLeft(distance);
        if (_input.IsKeyDown(MoveRight))
            _camera.MoveRight(distance);
        if (_input.IsKeyDown(MoveUp))
            _camera.MoveUp(distance);
        if (_input.IsKeyDown(MoveDown))
            _camera.MoveDown(distance);

        float lookDelta = KeyboardLookSpeed * deltaTime;
        float yawDelta = 0f;
        float pitchDelta = 0f;

        if (_input.IsKeyDown(LookLeft))
            yawDelta -= lookDelta;
        if (_input.IsKeyDown(LookRight))
            yawDelta += lookDelta;
        if (_input.IsKeyDown(LookUp))
            pitchDelta -= lookDelta;
        if (_input.IsKeyDown(LookDown))
            pitchDelta += lookDelta;

        if (yawDelta != 0f || pitchDelta != 0f)
            _camera.RotateYawPitch(yawDelta, pitchDelta);

        Vector2 mouseDelta = _input.ConsumeMouseDelta();
        if (_input.IsMouseButtonDown((int)MouseButton.Right))
        {
            _camera.RotateYawPitch(mouseDelta.X * MouseSensitivity, mouseDelta.Y * MouseSensitivity);
        }

        if (viewportHeight > 0)
            _camera.AspectRatio = (float)viewportWidth / viewportHeight;

        _camera.Update();
    }

    private static void CreateKeyboardAction(InputManager input, string name, Key key)
    {
        Njulf.Input.Action action = input.CreateAction(name);
        action.AddBinding(new InputBinding(key));
    }

    private bool WasPressed(string actionName, ref bool previousState)
    {
        bool currentState = _input.IsKeyDown(actionName);
        bool pressed = currentState && !previousState && !IsControlDown();
        previousState = currentState;
        return pressed;
    }

    private bool WasChordPressed(Key key, ref bool previousState)
    {
        bool currentState = IsControlDown() && IsPhysicalKeyDown(key);
        bool pressed = currentState && !previousState;
        previousState = currentState;
        return pressed;
    }

    private bool IsControlDown()
    {
        return IsPhysicalKeyDown(Key.ControlLeft) || IsPhysicalKeyDown(Key.ControlRight);
    }

    private bool IsPhysicalKeyDown(Key key)
    {
        return _rawInput?.IsPhysicalKeyDown(key) == true;
    }

    private void MoveCamera(Vector3 position, float yaw, float pitch)
    {
        _camera.Position = position;
        _camera.Yaw = yaw;
        _camera.Pitch = pitch;
        _camera.Update();
    }

    public void ApplyBaselineScenario(SamplePerformanceScenario scenario)
    {
        switch (scenario)
        {
            case SamplePerformanceScenario.Normal:
                ApplyPerformanceScenario(SamplePerformanceScenario.Normal);
                MoveCamera(InteriorPosition, InteriorYaw, InteriorPitch);
                break;
            case SamplePerformanceScenario.ForestFoliage:
                ApplyPerformanceScenario(SamplePerformanceScenario.ForestFoliage);
                MoveCamera(ForestFoliagePosition, ForestFoliageYaw, ForestFoliagePitch);
                break;
            default:
                throw new ArgumentException($"Unsupported baseline scenario '{scenario}'.", nameof(scenario));
        }
    }

    private void ApplyPerformanceScenario(SamplePerformanceScenario scenario)
    {
        if (_performanceScenarioRunner == null || _performanceScenarioRunner.CurrentScenario == scenario)
            return;

        PrintPerformanceScenarioSummary(_performanceScenarioRunner.Apply(scenario));
    }

    private void AdjustExposure(float multiplier)
    {
        if (_renderer == null)
            return;

        _renderer.Settings.AutoExposure.Enabled = false;
        _renderer.Settings.Exposure *= multiplier;
        PrintExposureSettings("Exposure");
    }

    private void PrintExposureSettings(string prefix)
    {
        if (_renderer == null)
            return;

        AutoExposureSettings auto = _renderer.Settings.AutoExposure;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        Console.WriteLine(
            $"{prefix}: auto={(auto.Enabled ? "enabled" : "disabled")}, manual={_renderer.Settings.Exposure:F2}, " +
            $"effective={diagnostics.Exposure:F2}, avgLum={diagnostics.AutoExposureAverageLuminance:F4}, " +
            $"target={diagnostics.AutoExposureTargetExposure:F2}, key={auto.TargetLuminance:F2}, " +
            $"range={auto.MinExposure:F2}-{auto.MaxExposure:F2}, speed={auto.AdaptationSpeed:F2}, stride={auto.SamplingStride}");
    }

    private void PrintBloomSettings(string prefix)
    {
        if (_renderer == null)
            return;

        BloomSettings bloom = _renderer.Settings.Bloom;
        Console.WriteLine(
            $"{prefix}: {(bloom.Enabled ? "enabled" : "disabled")}, intensity={bloom.Intensity:F2}, " +
            $"threshold={bloom.Threshold:F2}, knee={bloom.Knee:F2}, radius={bloom.Radius:F2}, " +
            $"debug={bloom.DebugView}, debugMip={bloom.DebugMipLevel}");
    }

    private void PrintShadowSettings(string prefix)
    {
        if (_renderer == null)
            return;

        ShadowSettings shadows = _renderer.Settings.Shadows;
        FoliageSettings foliage = _renderer.Settings.Foliage;
        Console.WriteLine(
            $"{prefix}: {(shadows.DirectionalShadowsEnabled ? "enabled" : "disabled")}, " +
            $"map={shadows.DirectionalShadowMapSize}, cascades={shadows.DirectionalCascadeCount}, " +
            $"normalBias={shadows.NormalBias:F4}, slopeBias={shadows.SlopeScaledDepthBias:F2}, " +
            $"spot={(shadows.SpotShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedSpotLights}@{shadows.SpotShadowTileSize}, " +
            $"point={(shadows.PointShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedPointLights}@{shadows.PointShadowMapSize}, " +
            $"spotBias={shadows.SpotNormalBias:F4}, pointBias={shadows.PointNormalBias:F4}, " +
            $"foliage={(foliage.CastShadows ? "on" : "off")}:{foliage.GrassShadowDistance:F1}m@{foliage.GrassShadowDensityScale:F2}, " +
            $"foliageIndirect={(foliage.IndirectMeshletDispatchEnabled ? "on" : "off")}, " +
            $"farImpostors={(foliage.FarImpostorsEnabled ? "on" : "off")}, " +
            $"foliageLocal={(foliage.LocalShadowsEnabled ? "on" : "off")}:{foliage.MaxLocalShadowedSpotLights}/{foliage.MaxLocalShadowedPointLights}, " +
            $"foliageMotion={(foliage.MotionVectorsEnabled ? "on" : "off")}, " +
            $"debug={shadows.DebugView}");
    }

    private void PrintFoliageSettings(string prefix)
    {
        if (_renderer == null)
            return;

        FoliageSettings foliage = _renderer.Settings.Foliage;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        ulong foliageBytes = diagnostics.FoliageInstanceBufferBytes +
            diagnostics.FoliageClusterBufferBytes +
            diagnostics.FoliageDrawBufferBytes +
            diagnostics.FoliageImpostorAtlasBytes;
        Console.WriteLine(
            $"{prefix}: enabled={(foliage.Enabled ? "on" : "off")}, gpuDriven={(foliage.GpuDrivenEnabled ? "on" : "off")}, " +
            $"hiz={(foliage.HiZCullingEnabled ? "on" : "off")}, indirect={(foliage.IndirectMeshletDispatchEnabled ? "on" : "off")}, " +
            $"farImpostors={(foliage.FarImpostorsEnabled ? "on" : "off")}, debug={foliage.DebugView}, " +
            $"density={foliage.DensityScale:F2}, drawDistance={foliage.MaxDrawDistance:F1}, shadows={(foliage.CastShadows ? "on" : "off")}:{foliage.GrassShadowDistance:F1}m@{foliage.GrassShadowDensityScale:F2}, " +
            $"localShadows={(foliage.LocalShadowsEnabled ? "on" : "off")}:{foliage.MaxLocalShadowedSpotLights}/{foliage.MaxLocalShadowedPointLights}, " +
            $"patches={diagnostics.FoliagePatchCount}, prototypes={diagnostics.FoliagePrototypeCount}, clusters={diagnostics.FoliageClusterCount}, " +
            $"visibleClusters={diagnostics.FoliageVisibleClusterCount}, meshletDraws={diagnostics.FoliageVisibleMeshletDrawCount}, blades={diagnostics.FoliageGrassBladeEstimate}, " +
            $"lod={diagnostics.FoliageLod0VisibleCount}/{diagnostics.FoliageLod1VisibleCount}/{diagnostics.FoliageLod2VisibleCount}, " +
            $"hizRejected={diagnostics.FoliageHiZRejectedCount}/{diagnostics.FoliageHiZTestedCount}, overflow={diagnostics.FoliageOverflowCount}/{diagnostics.FoliageMeshletDrawOverflowCount}, " +
            $"farVisible={diagnostics.FoliageFarImpostorVisibleCount}, bytes={foliageBytes}, " +
            $"cpuUs={diagnostics.CpuFoliageBuildMicroseconds}/{diagnostics.CpuFoliageUploadMicroseconds}, " +
            $"gpuUs={diagnostics.GpuFoliageCullMicroseconds}/{diagnostics.GpuFoliageDepthMicroseconds}/{diagnostics.GpuFoliageForwardMicroseconds}/{diagnostics.GpuFoliageShadowMicroseconds}");
    }

    private void PrintSceneSubmissionSettings(string prefix)
    {
        if (_renderer == null)
            return;

        SceneSubmissionSettings submission = _renderer.Settings.SceneSubmission;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        Console.WriteLine(
            $"{prefix}: compaction={(submission.GpuCompactionEnabled ? "on" : "off")}, " +
            $"indirect={(submission.IndirectMeshletDispatchEnabled ? "on" : "off")}, " +
            $"gpuLod={(submission.GpuLodSelectionEnabled ? "on" : "off")}, " +
            $"shadowCompaction={(submission.GpuShadowCompactionEnabled ? "on" : "off")}, " +
            $"validation={(submission.ValidationCompareCpuGpuLists ? "on" : "off")}, " +
            $"mode={diagnostics.SceneSubmissionActiveMode}, cpuCandidates={diagnostics.SceneSubmissionCpuCandidateCount}, " +
            $"gpuEmitted={diagnostics.SceneSubmissionGpuEmittedCount}, indirectTasks={diagnostics.SceneSubmissionIndirectTaskCount}, " +
            $"fallback='{diagnostics.SceneSubmissionFallbackReason}', cpuOpaque={diagnostics.OpaqueMeshletCount}, cpuSubmittedFallback={diagnostics.MeshletCountSubmittedCpu}, " +
            $"gpuActive={diagnostics.SceneSubmissionGpuCompactionActive}, gpuCandidates={diagnostics.SceneSubmissionGpuOpaqueCandidateCount}, " +
            $"gpuFrustumRejected={diagnostics.SceneSubmissionGpuOpaqueFrustumRejectedCount}, gpuOverflow={diagnostics.SceneSubmissionGpuOpaqueOverflowCount}, " +
            $"gpuLodEmitted={diagnostics.SceneSubmissionGpuLod0EmittedCount}/{diagnostics.SceneSubmissionGpuLod1EmittedCount}/{diagnostics.SceneSubmissionGpuLod2EmittedCount}, " +
            $"gpuMissingLodFallback={diagnostics.SceneSubmissionGpuMissingLodFallbackCount}, " +
            $"gpuDepth={diagnostics.SceneSubmissionGpuCompactedSolidDepthMeshletCount}/{diagnostics.SceneSubmissionGpuCompactedMaskedDepthMeshletCount}, " +
            $"gpuDepthCandidates={diagnostics.SceneSubmissionGpuDepthSolidCandidateCount}/{diagnostics.SceneSubmissionGpuDepthMaskedCandidateCount}, " +
            $"gpuDepthOverflow={diagnostics.SceneSubmissionGpuDepthOverflowCount}, " +
            $"gpuDirShadow={diagnostics.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount}/{diagnostics.SceneSubmissionGpuDirectionalShadowCandidateCount}, " +
            $"gpuDirShadowOverflow={diagnostics.SceneSubmissionGpuDirectionalShadowOverflowCount}, " +
            $"gpuDirShadowCascades='{diagnostics.SceneSubmissionGpuDirectionalShadowCascadeSummary}', " +
            $"localShadowGpuJustified={diagnostics.SceneSubmissionLocalShadowGpuCompactionJustified}, " +
            $"localShadowTests={diagnostics.SceneSubmissionSpotShadowMeshletLightTests}/{diagnostics.SceneSubmissionPointShadowMeshletFaceTests}, " +
            $"localShadowStatus='{diagnostics.SceneSubmissionLocalShadowGpuCompactionStatus}', " +
            $"gpuCapacity={diagnostics.SceneSubmissionGpuCompactedOpaqueCapacity}, " +
            $"validationStatus='{diagnostics.SceneSubmissionValidationStatus}', validationMismatches={diagnostics.SceneSubmissionValidationMismatchCount}, " +
            $"validationCounts={diagnostics.SceneSubmissionValidationCpuOpaqueCount}/{diagnostics.SceneSubmissionValidationGpuOpaqueCount}, " +
            $"gpuShadow={diagnostics.SceneSubmissionGpuCompactedShadowMeshletCount}, " +
            $"indirectBytes={diagnostics.SceneSubmissionOpaqueIndirectDispatchBufferSize}, " +
            $"stableUploadBytes={diagnostics.StableSceneInputUploadBytes}, candidateUploadBytes={diagnostics.CpuCandidateListUploadBytes}, " +
            $"cameraRebuiltCpuLists={diagnostics.CameraDrivenCpuDrawListRebuilt}");
    }

    private void PrintTransparencySettings(string prefix)
    {
        if (_renderer == null)
            return;

        TransparencySettings transparency = _renderer.Settings.Transparency;
        DecalSettings decals = _renderer.Settings.Decals;
        Console.WriteLine(
            $"{prefix}: {(transparency.Enabled ? "enabled" : "disabled")}, mode={transparency.Mode}, " +
            $"debug={transparency.DebugView}, receiveShadows={(transparency.ReceiveShadows ? "on" : "off")}, " +
            $"sampleReflections={(transparency.SampleReflections ? "on" : "off")}, sortPerMeshlet={(transparency.SortPerMeshlet ? "on" : "off")}, " +
            $"maxMeshlets={transparency.MaxTransparentMeshlets}, alphaDiscard={transparency.AlphaDiscardThreshold:F4}, " +
            $"geometryDecals={(decals.GeometryDecalsEnabled ? "on" : "off")}, decalDebug={decals.DebugView}, " +
            $"decalBias={decals.GeometryDepthBias:F5}, decalSlopeBias={decals.GeometrySlopeScaledDepthBias:F2}");
    }

    private void PrintAmbientOcclusionSettings(string prefix)
    {
        if (_renderer == null)
            return;

        AmbientOcclusionSettings ao = _renderer.Settings.AmbientOcclusion;
        Console.WriteLine(
            $"{prefix}: {(ao.Enabled ? "enabled" : "disabled")}, mode={ao.Mode}, scale={ao.ResolutionScale:F2}, " +
            $"radius={ao.Radius:F2}, intensity={ao.Intensity:F2}, bias={ao.Bias:F3}, samples={ao.SampleCount}, " +
            $"blur={ao.BlurRadius}, debug={ao.DebugView}");
    }

    private void PrintAntiAliasingSettings(string prefix)
    {
        if (_renderer == null)
            return;

        AntiAliasingSettings aa = _renderer.Settings.AntiAliasing;
        Console.WriteLine(
            $"{prefix}: mode={aa.Mode}, effective={aa.EffectiveMode}, debug={aa.DebugView}, " +
            $"fxaaSubpixel={aa.FxaaSubpixelBlending:F2}, smaaQuality={aa.EffectiveSmaaQuality}, " +
            $"smaaSpatialSamples={aa.EffectiveSmaaSpatialSampleCount}, smaaThreshold={aa.EffectiveSmaaThreshold:F3}, " +
            $"smaaSearch={aa.EffectiveSmaaMaxSearchSteps}/{aa.EffectiveSmaaMaxSearchStepsDiagonal}, " +
            $"smaaCorner={aa.EffectiveSmaaCornerRounding:F0}, " +
            $"jitter={(aa.JitterEnabled ? "on" : "off")}");
    }

    private void PrintFogSettings(string prefix)
    {
        if (_renderer == null)
            return;

        FogSettings fog = _renderer.Settings.Fog;
        Console.WriteLine(
            $"{prefix}: {(fog.Enabled ? "enabled" : "disabled")}, mode={fog.Mode}, colorMode={fog.ColorMode}, " +
            $"density={fog.Density:F3}, start={fog.StartDistance:F1}, end={fog.EndDistance:F1}, " +
            $"height={fog.Height:F1}, heightDensity={fog.HeightDensity:F3}, falloff={fog.HeightFalloff:F3}, " +
            $"maxOpacity={fog.MaxOpacity:F2}, inscatter={(fog.DirectionalInscatteringEnabled ? "on" : "off")}, " +
            $"debug={fog.DebugView}");
    }

    private void PrintReflectionSettings(string prefix)
    {
        if (_renderer == null)
            return;

        ReflectionSettings reflections = _renderer.Settings.Reflections;
        Console.WriteLine(
            $"{prefix}: {(reflections.Enabled ? "enabled" : "disabled")}, mode={reflections.Mode}, " +
            $"max={reflections.MaxProbes}, perPixel={reflections.MaxProbesPerPixel}, resolution={reflections.ProbeResolution}, " +
            $"intensity={reflections.Intensity:F2}, fallback={reflections.GlobalFallbackIntensity:F2}, " +
            $"boxProjection={(reflections.BoxProjectionEnabled ? "on" : "off")}, blending={(reflections.ProbeBlendingEnabled ? "on" : "off")}, " +
            $"debug={reflections.DebugView}, probe={reflections.DebugProbeIndex}, face={reflections.DebugCubemapFace}, mip={reflections.DebugMipLevel}");
    }

    private void PrintParticleSettings(string prefix)
    {
        if (_renderer == null)
            return;

        ParticleSettings particles = _renderer.Settings.Particles;
        Console.WriteLine(
            $"{prefix}: {(particles.Enabled ? "enabled" : "disabled")}, mode={particles.SimulationMode}, debug={particles.DebugView}, " +
            $"maxParticles={particles.MaxParticles}, maxEmitters={particles.MaxEmitters}, soft={(particles.SoftParticlesEnabled ? "on" : "off")}, " +
            $"softDistance={particles.SoftParticleDistance:F2}, spawnScale={particles.GlobalSpawnRateScale:F2}, " +
            $"velocityScale={particles.GlobalVelocityScale:F2}, emissiveScale={particles.GlobalEmissiveScale:F2}");
    }

    private void PrintMaterialSettings(string prefix)
    {
        if (_renderer == null)
            return;

        MaterialSettings materials = _renderer.Settings.Materials;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        Console.WriteLine(
            $"{prefix}: debug={materials.DebugView}, materials={diagnostics.MaterialCount}, " +
            $"extensions={diagnostics.MaterialExtensionDataCount}, extensionBytes={diagnostics.MaterialExtensionUploadBytes}");
    }

    private void PrintAnimationSettings(string prefix)
    {
        if (_renderer == null)
            return;

        AnimationSettings animation = _renderer.Settings.Animation;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        Console.WriteLine(
            $"{prefix}: enabled={(animation.Enabled ? "on" : "off")}, skinning={animation.SkinningMode}, debug={animation.DebugView}, " +
            $"skinnedObjects={diagnostics.SkinnedObjectCount}, playing={diagnostics.PlayingAnimatorCount}, dispatches={diagnostics.SkinningDispatchCount}");
    }

    private void PrintDebugSettings(string prefix)
    {
        if (_renderer == null)
            return;

        DebugOverlaySettings debug = _renderer.Settings.Debug;
        Console.WriteLine(
            $"{prefix}: {(debug.Enabled ? "enabled" : "disabled")}, overlay={debug.Mode}, " +
            $"cpuSnapshots={(debug.CpuSnapshotsEnabled ? "on" : "off")}, selected={debug.SelectedObjectIndex}, " +
            $"debugLines={_renderer.DebugDraw.Snapshot().LineCount}/{debug.MaxDebugLineSegments}");
    }

    private void CyclePerformanceBudgetProfile()
    {
        if (_renderer == null)
            return;

        RenderBudgetProfileKind[] profiles = Enum.GetValues<RenderBudgetProfileKind>();
        RenderBudgetSettings settings = _renderer.Settings.PerformanceBudgets;
        int index = Array.IndexOf(profiles, settings.ActiveProfile);
        index = index < 0 ? 0 : (index + 1) % profiles.Length;
        settings.ActiveProfile = profiles[index];
        Console.WriteLine($"Performance budget profile: {settings.Profile.Name}");
    }

    private void ExportPerformanceSnapshotFile()
    {
        if (_renderer == null)
            return;

        try
        {
            ExportPerformanceSnapshotFile(null, "Performance snapshot");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Performance snapshot export failed: {ex.Message}");
        }
    }

    public string ExportPerformanceSnapshotFile(string? directory, string label)
    {
        if (_renderer == null)
            throw new InvalidOperationException("Renderer is required to export a performance snapshot.");

        string path = _renderer.ExportPerformanceSnapshot(directory);
        Console.WriteLine($"{label} exported: {path}");
        return path;
    }

    private void CyclePerformanceScenarioSet()
    {
        if (_performanceScenarioRunner == null)
            return;

        PrintPerformanceScenarioSummary(_performanceScenarioRunner.CycleNext());
    }

    private static void PrintPerformanceScenarioSummary(SamplePerformanceScenarioSummary summary)
    {
        Console.WriteLine(
            $"Performance scenario: {summary.Scenario}, objects={summary.ObjectCount}, lights={summary.LightCount}, " +
            $"materials={summary.MaterialCount}, transparent={summary.TransparentObjectCount}, probes={summary.ReflectionProbeCount}, {summary.Notes}");
    }

    private void CycleLightingModeSet()
    {
        if (_lightManager == null)
            return;

        _lightingMode = _lightingMode switch
        {
            SampleLightingMode.DirectionalKey => SampleLightingMode.ThreePointDemo,
            SampleLightingMode.ThreePointDemo => SampleLightingMode.SpotShadowDemo,
            SampleLightingMode.SpotShadowDemo => SampleLightingMode.PointShadowDemo,
            _ => SampleLightingMode.DirectionalKey
        };
        if (_renderer != null)
            SampleLighting.ConfigureRenderSettings(_renderer.Settings, _lightingMode);
        SampleLighting.Configure(_lightManager, _lightingMode);
        Console.WriteLine($"Lighting mode: {_lightingMode}");
    }

    private void CycleQualityPreset()
    {
        if (_renderer == null)
            return;

        RenderQualityPreset[] presets = Enum.GetValues<RenderQualityPreset>();
        int index = Array.IndexOf(presets, _renderer.Settings.QualityPreset);
        index = index < 0 ? 0 : (index + 1) % presets.Length;
        ApplyQualityPreset(presets[index]);
        Console.WriteLine($"Quality preset: {_renderer.Settings.QualityPreset}");
    }

    private void CycleFeatureIsolation()
    {
        if (_renderer == null)
            return;

        RenderFeatureIsolationMode[] modes = Enum.GetValues<RenderFeatureIsolationMode>();
        int index = Array.IndexOf(modes, _renderer.Settings.FeatureIsolation);
        index = index < 0 ? 0 : (index + 1) % modes.Length;
        _renderer.Settings.FeatureIsolation = modes[index];
        Console.WriteLine($"Feature isolation: {_renderer.Settings.FeatureIsolation}");
    }

    private void ApplyQualityPreset(RenderQualityPreset preset)
    {
        if (_renderer == null)
            return;

        RenderSettings settings = _renderer.Settings;
        settings.ApplyQualityPreset(preset);
        SampleLighting.ConfigureRenderSettings(settings, _lightingMode);
    }

    private void SelectDebugObject(int direction)
    {
        if (_renderer == null)
            return;

        _renderer.Settings.Debug.Enabled = true;
        _renderer.Settings.Debug.CpuSnapshotsEnabled = true;
        int objectCount = _renderer.DebugObjectSnapshotCount;
        if (objectCount <= 0)
        {
            Console.WriteLine("Debug object selection: no CPU snapshot is available yet.");
            return;
        }

        int selected = _renderer.Settings.Debug.SelectedObjectIndex;
        selected = selected < 0 ? 0 : selected + direction;
        if (selected < 0)
            selected = objectCount - 1;
        if (selected >= objectCount)
            selected = 0;

        _renderer.Settings.Debug.SelectedObjectIndex = selected;
        PrintSelectedObjectInspection();
    }

    private void PrintSelectedObjectInspection()
    {
        if (_renderer == null)
            return;

        _renderer.Settings.Debug.Enabled = true;
        _renderer.Settings.Debug.CpuSnapshotsEnabled = true;
        if (!_renderer.TryInspectObject(_renderer.Settings.Debug.SelectedObjectIndex, out SelectedObjectInspection inspection))
        {
            Console.WriteLine("Selected object: none.");
            return;
        }

        MaterialInspectionResult material = inspection.MaterialInfo;
        Console.WriteLine(
            $"Selected object {inspection.ObjectIndex}: '{inspection.ObjectName}', visible={inspection.Visible}, cpuCulled={inspection.CpuCulled}, " +
            $"mesh={inspection.Mesh.Index}, material={inspection.Material.Index}, mode={material.RenderMode}, " +
            $"metallic={material.Metallic:F2}, roughness={material.Roughness:F2}, ao={material.AmbientOcclusion:F2}, normal={material.NormalStrength:F2}, " +
            $"textures={material.AlbedoTextureIndex}/{material.NormalTextureIndex}/{material.MetallicRoughnessTextureIndex}/{material.EmissiveTextureIndex}");
    }

    private static DebugOverlayMode NextDebugOverlay(DebugOverlayMode mode)
    {
        return mode switch
        {
            DebugOverlayMode.None => DebugOverlayMode.LightTiles,
            DebugOverlayMode.LightTiles => DebugOverlayMode.DirectionalShadowCascades,
            DebugOverlayMode.DirectionalShadowCascades => DebugOverlayMode.ReflectionProbeVolumes,
            DebugOverlayMode.ReflectionProbeVolumes => DebugOverlayMode.DecalVolumes,
            DebugOverlayMode.DecalVolumes => DebugOverlayMode.ObjectBounds,
            DebugOverlayMode.ObjectBounds => DebugOverlayMode.MeshletBounds,
            DebugOverlayMode.MeshletBounds => DebugOverlayMode.SelectedObject,
            DebugOverlayMode.SelectedObject => DebugOverlayMode.MaterialInspection,
            DebugOverlayMode.MaterialInspection => DebugOverlayMode.PassTimings,
            DebugOverlayMode.PassTimings => DebugOverlayMode.GpuMemory,
            _ => DebugOverlayMode.None
        };
    }

    private static bool RequiresCpuSnapshots(DebugOverlayMode mode)
    {
        return mode is DebugOverlayMode.ObjectBounds or
            DebugOverlayMode.MeshletBounds or
            DebugOverlayMode.SelectedObject or
            DebugOverlayMode.MaterialInspection;
    }

    private static MaterialDebugView NextMaterialDebugView(MaterialDebugView mode)
    {
        return mode switch
        {
            MaterialDebugView.None => MaterialDebugView.FeatureFlags,
            MaterialDebugView.FeatureFlags => MaterialDebugView.BaseColor,
            MaterialDebugView.BaseColor => MaterialDebugView.Metallic,
            MaterialDebugView.Metallic => MaterialDebugView.Roughness,
            MaterialDebugView.Roughness => MaterialDebugView.NormalStrength,
            MaterialDebugView.NormalStrength => MaterialDebugView.WorldNormal,
            MaterialDebugView.WorldNormal => MaterialDebugView.EmissiveIntensity,
            MaterialDebugView.EmissiveIntensity => MaterialDebugView.ClearcoatFactor,
            MaterialDebugView.ClearcoatFactor => MaterialDebugView.ClearcoatRoughness,
            MaterialDebugView.ClearcoatRoughness => MaterialDebugView.SheenColor,
            MaterialDebugView.SheenColor => MaterialDebugView.SheenRoughness,
            MaterialDebugView.SheenRoughness => MaterialDebugView.AnisotropyStrength,
            MaterialDebugView.AnisotropyStrength => MaterialDebugView.AnisotropyDirection,
            MaterialDebugView.AnisotropyDirection => MaterialDebugView.Transmission,
            MaterialDebugView.Transmission => MaterialDebugView.Ior,
            MaterialDebugView.Ior => MaterialDebugView.VolumeThickness,
            MaterialDebugView.VolumeThickness => MaterialDebugView.AttenuationColor,
            MaterialDebugView.AttenuationColor => MaterialDebugView.SubsurfaceStrength,
            MaterialDebugView.SubsurfaceStrength => MaterialDebugView.SpecularFactor,
            MaterialDebugView.SpecularFactor => MaterialDebugView.SpecularColor,
            MaterialDebugView.SpecularColor => MaterialDebugView.IridescenceFactor,
            MaterialDebugView.IridescenceFactor => MaterialDebugView.IridescenceThickness,
            MaterialDebugView.IridescenceThickness => MaterialDebugView.Dispersion,
            _ => MaterialDebugView.None
        };
    }

    private static AnimationDebugView NextAnimationDebugView(AnimationDebugView mode)
    {
        return mode switch
        {
            AnimationDebugView.None => AnimationDebugView.SkinnedObjects,
            _ => AnimationDebugView.None
        };
    }

    private static FoliageDebugView NextFoliageDebugView(FoliageDebugView mode)
    {
        return mode switch
        {
            FoliageDebugView.None => FoliageDebugView.Clusters,
            FoliageDebugView.Clusters => FoliageDebugView.LodBands,
            FoliageDebugView.LodBands => FoliageDebugView.DensityFade,
            FoliageDebugView.DensityFade => FoliageDebugView.WindStrength,
            FoliageDebugView.WindStrength => FoliageDebugView.HiZRejectedClusters,
            FoliageDebugView.HiZRejectedClusters => FoliageDebugView.ShadowCasting,
            FoliageDebugView.ShadowCasting => FoliageDebugView.AlphaCutoff,
            _ => FoliageDebugView.None
        };
    }
}
