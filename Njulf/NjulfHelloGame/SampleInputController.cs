using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private const string CycleScene = "cycle_scene";
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
    private const string ToggleGlobalIllumination = "toggle_global_illumination";
    private const string CycleGlobalIlluminationMode = "cycle_global_illumination_mode";
    private const string CycleGlobalIlluminationDebug = "cycle_global_illumination_debug";
    private const string ToggleDdgiDiagnosticsFilter = "toggle_ddgi_diagnostics_filter";
    private const string ToggleSimpleDdgi = "toggle_simple_ddgi";
    private const string GlobalIlluminationIntensityDown = "global_illumination_intensity_down";
    private const string GlobalIlluminationIntensityUp = "global_illumination_intensity_up";
    private const string GlobalIlluminationDistanceDown = "global_illumination_distance_down";
    private const string GlobalIlluminationDistanceUp = "global_illumination_distance_up";
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
    private const int RuntimeBenchmarkWarmupFrameCount = 30;
    private const int RuntimeBenchmarkMeasureFrameCount = 120;
    // Renderer-target screenshots are asynchronous. A bounded settlement phase
    // makes a missing capture an explicit failed run instead of a false pass.
    private const int SponzaGiRendererScreenshotVerificationTimeoutFrames = 600;
    private static readonly Vector3 FullModelPosition = new(0f, 5.5f, 18f);
    private const float FullModelYaw = 0f;
    private const float FullModelPitch = -0.22f;
    private static readonly Vector3 InteriorPosition = new(0f, 1.25f, 5.5f);
    private const float InteriorYaw = 0f;
    private const float InteriorPitch = -0.12f;
    // Six metres away from the façade along the camera's backward axis exposes
    // enough floor for the directional-shadow distance transition to be visible.
    private static readonly Vector3 SponzaRightWallPosition = new(6f, 1.35f, 0f);
    // Turn the right-wall scenario a quarter turn into the courtyard. Keep this
    // aligned with the deterministic capture's low/high bookmarks.
    private const float SponzaRightWallYaw = -MathF.PI * 0.5f;
    private const float SponzaRightWallPitch = -0.16f;
    private static readonly Vector3 ForestFoliagePosition = new(0f, 1.6f, 5.5f);
    private const float ForestFoliageYaw = 0f;
    private const float ForestFoliagePitch = -0.14f;
    private static readonly Vector3 CornellRoomPosition = new(0f, 1.45f, -1.6f);
    private const float CornellRoomYaw = 0f;
    private const float CornellRoomPitch = -0.05f;
    private static readonly Vector3 VerticalityRingsPosition = new(0f, 4.0f, 15.0f);
    private const float VerticalityRingsYaw = 0f;
    private const float VerticalityRingsPitch = 0.22f;
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
        new(CycleScene, Key.Number3),
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
        new(PointShadowBudgetUp, Key.GraveAccent),
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
    private IReadOnlyList<ParticleEffectInstance> _particleEffects;
    private SamplePerformanceScenarioRunner? _performanceScenarioRunner;
    private readonly System.Action? _cycleScene;
    private readonly System.Action<SampleSceneKind>? _loadSceneKind;
    private readonly System.Action? _toggleDdgiDiagnosticsFilter;
    private readonly Func<SampleDiagnosticsFilter>? _getDiagnosticsFilter;
    private readonly System.Action? _restoreSceneRenderSettings;
    private readonly Func<string, bool>? _requestDiagnosticScreenshotCapture;
    private readonly Func<bool>? _suppressGameInput;
    private SampleLightingMode _lightingMode;
    private bool _fullModelPressed;
    private bool _interiorPressed;
    private bool _cycleScenePressed;
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
    private bool _toggleGlobalIlluminationPressed;
    private bool _cycleGlobalIlluminationModePressed;
    private bool _toggleSimpleDdgiPressed;
    private bool _cycleGlobalIlluminationDebugPressed;
    private bool _cycleGlobalIlluminationFocusDebugPressed;
    private bool _clearGlobalIlluminationDebugPressed;
    private bool _cycleDdgiDebugPressed;
    private bool _toggleDdgiDiagnosticsFilterPressed;
    private bool _cycleDdgiInvestigationViewPressed;
    private bool _resetNormalRenderViewPressed;
    private bool _cycleDdgiQualityTierPressed;
    private bool _toggleDdgiProbeL1MetadataPressed;
    private bool _printDdgiDiagnosticsPressed;
    private bool _globalIlluminationIntensityDownPressed;
    private bool _globalIlluminationIntensityUpPressed;
    private bool _globalIlluminationDistanceDownPressed;
    private bool _globalIlluminationDistanceUpPressed;
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
    private bool _requestDiagnosticSnapshotPressed;
    private bool _cycleDebugOverlayPressed;
    private bool _requestScreenshotPressed;
    private bool _requestRenderDocCapturePressed;
    private bool _cycleBudgetProfilePressed;
    private bool _exportPerformanceSnapshotPressed;
    private bool _cyclePerformanceScenarioPressed;
    private bool _loadCornellPerformanceScenePressed;
    private bool _loadVerticalityPerformanceScenePressed;
    private bool _loadSponzaPerformanceScenePressed;
    private bool _startSponzaGiCapturePressed;
    private bool _startRuntimeBenchmarkPressed;
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
    private ShadowToggleState? _savedShadowToggleState;
    private bool _hasSavedDdgiForwardEstimateCounterState;
    private bool _savedDdgiForwardEstimateCountersEnabled;
    private SampleRuntimeBenchmarkCapture? _runtimeBenchmarkCapture;
    private SampleSponzaGiCaptureSequence? _sponzaGiCaptureSequence;
    private string? _sponzaGiCaptureDirectory;
    private bool _sponzaGiCaptureExitWhenComplete;
    private SponzaGiCaptureRestoreState? _sponzaGiCaptureRestoreState;
    private readonly List<SampleSponzaGiCapturedArtifact> _sponzaGiCaptureArtifacts = [];
    private readonly List<SponzaGiPendingRendererScreenshot> _sponzaGiPendingRendererScreenshots = [];
    private SampleSponzaGiCaptureMode _sponzaGiCaptureMode;
    private int _sponzaGiScreenshotVerificationFrames;

    public SampleInputController(
        FirstPersonCamera camera,
        IInputManager input,
        System.Action exit,
        Njulf.Rendering.VulkanRenderer? renderer = null,
        LightManager? lightManager = null,
        SampleLightingMode lightingMode = SampleLightingMode.DirectionalKey,
        IReadOnlyList<ParticleEffectInstance>? particleEffects = null,
        SamplePerformanceScenarioRunner? performanceScenarioRunner = null,
        System.Action? cycleScene = null,
        System.Action<SampleSceneKind>? loadSceneKind = null,
        System.Action? toggleDdgiDiagnosticsFilter = null,
        Func<SampleDiagnosticsFilter>? getDiagnosticsFilter = null,
        System.Action? restoreSceneRenderSettings = null,
        Func<string, bool>? requestDiagnosticScreenshotCapture = null,
        Func<bool>? suppressGameInput = null)
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
        _cycleScene = cycleScene;
        _loadSceneKind = loadSceneKind;
        _toggleDdgiDiagnosticsFilter = toggleDdgiDiagnosticsFilter;
        _getDiagnosticsFilter = getDiagnosticsFilter;
        _restoreSceneRenderSettings = restoreSceneRenderSettings;
        _requestDiagnosticScreenshotCapture = requestDiagnosticScreenshotCapture;
        _suppressGameInput = suppressGameInput;
        if (_renderer != null && _performanceScenarioRunner != null)
            _renderer.CaptureScenario = _performanceScenarioRunner.CurrentScenario.ToString();
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

        if (_suppressGameInput?.Invoke() == true)
        {
            _ = _input.ConsumeMouseDelta();
            if (viewportHeight > 0)
                _camera.AspectRatio = (float)viewportWidth / viewportHeight;
            _camera.Update();
            return;
        }

        if (_renderer != null && _sponzaGiCaptureSequence == null &&
            WasChordPressed(Key.F11, ref _startSponzaGiCapturePressed))
        {
            string outputDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "SponzaGiCaptures",
                $"closure-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}");
            _loadSceneKind?.Invoke(SampleSceneKind.SponzaPlaza);
            StartSponzaGiCapture(
                outputDirectory,
                exitWhenComplete: false,
                captureMode: SampleSponzaGiCaptureMode.DetailedDiagnostics);
        }

        if (_sponzaGiCaptureSequence != null)
        {
            UpdateSponzaGiCapture(viewportWidth, viewportHeight);
            return;
        }

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

        if (WasPressed(CycleScene, ref _cycleScenePressed))
            _cycleScene?.Invoke();

        if (_renderer != null && WasPressed(ToggleHiZ, ref _toggleHiZPressed))
        {
            _renderer.EnableHiZOcclusion = !_renderer.EnableHiZOcclusion;
            _renderer.Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled = _renderer.EnableHiZOcclusion;
            _renderer.Settings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled = _renderer.EnableHiZOcclusion;
            Console.WriteLine(
                $"Hi-Z occlusion: {(_renderer.EnableHiZOcclusion ? "enabled" : "disabled")} " +
                $"previous-frame={(_renderer.Settings.HiZOcclusion.PreviousFrameSceneSubmissionEnabled ? "on" : "off")} " +
                $"current-frame={(_renderer.Settings.HiZOcclusion.CurrentFrameForwardVisibilityEnabled ? "on" : "off")}");
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

        if (WasChordPressed(Key.Number1, ref _loadCornellPerformanceScenePressed))
            LoadPerformanceScenarioPreset(
                SamplePerformanceScenario.GiCornellRoom,
                CornellRoomPosition,
                CornellRoomYaw,
                CornellRoomPitch);

        if (WasChordPressed(Key.Number2, ref _loadVerticalityPerformanceScenePressed))
            LoadPerformanceScenarioPreset(
                SamplePerformanceScenario.GiVerticalityRings,
                VerticalityRingsPosition,
                VerticalityRingsYaw,
                VerticalityRingsPitch);

        if (WasChordPressed(Key.Number4, ref _loadSponzaPerformanceScenePressed))
        {
            _loadSceneKind?.Invoke(SampleSceneKind.SponzaPlaza);
            LoadPerformanceScenarioPreset(
                SamplePerformanceScenario.GiSponzaRightWallStationary,
                SponzaRightWallPosition,
                SponzaRightWallYaw,
                SponzaRightWallPitch);
        }

        if (_renderer != null && WasChordPressed(Key.B, ref _startRuntimeBenchmarkPressed))
            StartRuntimeBenchmarkCapture();

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

        if (_renderer != null && WasChordPressed(Key.K, ref _cycleMaterialDebugPressed))
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
            ToggleAllShadowsForDiagnostics();
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

        if (_renderer != null && WasChordPressed(Key.Number5, ref _toggleGlobalIlluminationPressed))
        {
            _renderer.Settings.GlobalIllumination.Enabled = !_renderer.Settings.GlobalIllumination.Enabled;
            if (!_renderer.Settings.GlobalIllumination.Enabled)
                _renderer.Settings.GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
            PrintGlobalIlluminationSettings("GI");
        }

        if (_renderer != null && WasChordPressed(Key.D, ref _cycleDdgiDebugPressed))
            CycleDdgiDebugView();

        if (_renderer != null && WasChordPressed(Key.S, ref _toggleSimpleDdgiPressed))
        {
            _renderer.Settings.GlobalIllumination.DdgiSimpleEnabled = !_renderer.Settings.GlobalIllumination.DdgiSimpleEnabled;
            PrintGlobalIlluminationSettings("Simple DDGI");
        }

        if (WasChordPressed(Key.F, ref _toggleDdgiDiagnosticsFilterPressed))
        {
            _toggleDdgiDiagnosticsFilter?.Invoke();
            ApplyDdgiDiagnosticsCounterState(_getDiagnosticsFilter?.Invoke() ?? SampleDiagnosticsFilter.FullFrame);
        }

        if (_renderer != null && WasChordPressed(Key.V, ref _cycleDdgiInvestigationViewPressed))
            CycleDdgiInvestigationView();

        if (_renderer != null && WasChordPressed(Key.P, ref _resetNormalRenderViewPressed))
            ResetNormalRenderView();

        if (_renderer != null && WasChordPressed(Key.T, ref _cycleDdgiQualityTierPressed))
            CycleDdgiQualityTier();

        if (_renderer != null && WasChordPressed(Key.L, ref _toggleDdgiProbeL1MetadataPressed))
            ToggleDdgiProbeL1Metadata();

        if (_renderer != null && WasChordPressed(Key.R, ref _printDdgiDiagnosticsPressed))
            PrintDdgiDiagnostics("DDGI diagnostics");

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

        if (_renderer != null && WasChordPressed(Key.Keypad0, ref _requestDiagnosticSnapshotPressed))
            RequestDiagnosticSnapshot();

        if (_renderer != null && WasChordPressed(Key.Keypad9, ref _cycleDebugOverlayPressed))
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.DebugDraw.Enabled = true;
            _renderer.Settings.Debug.Mode = NextDebugOverlay(_renderer.Settings.Debug.Mode);
            _renderer.Settings.Debug.CpuSnapshotsEnabled = RequiresCpuSnapshots(_renderer.Settings.Debug.Mode);
            PrintDebugSettings("Debug overlay");
        }

        if (_renderer != null && WasPressed(RequestScreenshot, ref _requestScreenshotPressed))
        {
            _renderer.Settings.Debug.Enabled = true;
            _renderer.Settings.Debug.AllowScreenshots = true;
            string? screenshotPath = CreateScreenshotOutputPath();
            _renderer.RequestScreenshot(screenshotPath);
            Console.WriteLine(screenshotPath == null ? "Screenshot requested." : $"Screenshot requested: {screenshotPath}");
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

        if (_renderer != null && WasChordPressed(Key.Number6, ref _cycleGlobalIlluminationDebugPressed))
        {
            _renderer.Settings.GlobalIllumination.DebugView = NextGlobalIlluminationDebugView(_renderer.Settings.GlobalIllumination.DebugView);
            PrintGlobalIlluminationSettings("GI debug");
            PrintDdgiDebugLegend(_renderer.Settings.GlobalIllumination.DebugView);
        }

        if (_renderer != null && WasChordPressed(Key.G, ref _cycleGlobalIlluminationFocusDebugPressed))
        {
            ConfigureDdgiOnly(_renderer.Settings.GlobalIllumination);
            _renderer.Settings.GlobalIllumination.DebugView =
                NextFocusedGlobalIlluminationDebugView(_renderer.Settings.GlobalIllumination.DebugView);
            PrintGlobalIlluminationSettings("GI focus debug");
            PrintDdgiDebugLegend(_renderer.Settings.GlobalIllumination.DebugView);
        }

        if (_renderer != null && WasChordPressed(Key.Backspace, ref _clearGlobalIlluminationDebugPressed))
        {
            _renderer.Settings.GlobalIllumination.DebugView = GlobalIlluminationDebugView.None;
            PrintGlobalIlluminationSettings("GI debug clear");
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

        if (_renderer != null && WasChordPressed(Key.Y, ref _cycleGlobalIlluminationModePressed))
        {
            GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
            gi.Mode = NextGlobalIlluminationMode(gi.Mode);
            gi.Enabled = gi.Mode != GlobalIlluminationMode.Disabled;
            gi.UseSsgi = ModeUsesSsgi(gi.Mode);
            gi.UseDdgi = ModeUsesDdgi(gi.Mode);
            gi.UseRayQueryBackend = ModeUsesDdgi(gi.Mode);
            PrintGlobalIlluminationSettings("GI mode");
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

        if (_renderer != null && WasChordPressed(Key.J, ref _globalIlluminationDistanceDownPressed))
        {
            _renderer.Settings.GlobalIllumination.MaxBounceDistance -= 0.5f;
            PrintGlobalIlluminationSettings("GI distance");
        }

        if (_renderer != null && WasChordPressed(Key.U, ref _globalIlluminationDistanceUpPressed))
        {
            _renderer.Settings.GlobalIllumination.MaxBounceDistance += 0.5f;
            PrintGlobalIlluminationSettings("GI distance");
        }

        if (_renderer != null && WasChordPressed(Key.M, ref _globalIlluminationIntensityDownPressed))
        {
            _renderer.Settings.GlobalIllumination.IndirectIntensity -= 0.05f;
            PrintGlobalIlluminationSettings("GI intensity");
        }

        if (_renderer != null && WasChordPressed(Key.I, ref _globalIlluminationIntensityUpPressed))
        {
            _renderer.Settings.GlobalIllumination.IndirectIntensity += 0.05f;
            PrintGlobalIlluminationSettings("GI intensity");
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

    public void SetParticleEffects(IReadOnlyList<ParticleEffectInstance>? particleEffects)
    {
        _particleEffects = particleEffects ?? Array.Empty<ParticleEffectInstance>();
    }

    public void SetLightingMode(SampleLightingMode lightingMode)
    {
        _lightingMode = lightingMode;
    }

    public void SetPerformanceScenarioRunner(SamplePerformanceScenarioRunner? performanceScenarioRunner)
    {
        _performanceScenarioRunner = performanceScenarioRunner;
        if (_renderer != null && performanceScenarioRunner != null)
            _renderer.CaptureScenario = performanceScenarioRunner.CurrentScenario.ToString();
    }

    /// <summary>
    /// Starts the fixed low/high Sponza closure capture. The caller supplies an
    /// explicit directory so CI and a reviewer can retain the JSON, image, and
    /// contract artifacts together. Runtime input may also launch the same
    /// sequence with Ctrl+F11.
    /// </summary>
    public void StartSponzaGiCapture(string outputDirectory, bool exitWhenComplete) =>
        StartSponzaGiCapture(
            outputDirectory,
            exitWhenComplete,
            SampleSponzaGiCaptureMode.ProductionTiming);

    /// <summary>
    /// Starts the locked Sponza sequence with an explicit evidence/timing
    /// classification. Production timing keeps only beauty endpoint timing
    /// eligible; the interactive detailed mode disables timing collection so
    /// debug views are never reported as production performance.
    /// </summary>
    public void StartSponzaGiCapture(
        string outputDirectory,
        bool exitWhenComplete,
        SampleSponzaGiCaptureMode captureMode)
    {
        if (_renderer == null)
            throw new InvalidOperationException("A renderer is required to start a Sponza GI capture.");
        if (_lightManager == null)
            throw new InvalidOperationException("A light manager is required to start a Sponza GI capture.");
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("A Sponza GI capture output directory is required.", nameof(outputDirectory));
        if (_sponzaGiCaptureSequence != null)
            throw new InvalidOperationException("A Sponza GI capture is already running.");
        if (!Enum.IsDefined(captureMode))
            throw new ArgumentOutOfRangeException(nameof(captureMode));

        SampleSponzaGiCaptureContract contract = SampleSponzaGiCaptureContract.Default;
        if (_runtimeBenchmarkCapture != null)
        {
            Console.WriteLine("Runtime benchmark canceled: starting the locked Sponza GI capture.");
            _runtimeBenchmarkCapture = null;
        }

        // Scenario metadata is mutable capture context rather than scene state.
        // Save it before applying the locked Sponza scenario so interactive
        // sessions resume with their actual prior metadata after the run.
        string previousCaptureScenario = _renderer.CaptureScenario;

        // Recreate the static world inputs first, then reapply the canonical
        // profile and only its deterministic capture overlay. This intentionally
        // avoids a broad preset after the Sponza profile has established its
        // physical GI configuration.
        SampleLighting.Configure(_lightManager, SampleLightingMode.DirectionalKey);
        SampleLighting.ConfigureRenderSettings(_renderer.Settings, SampleLightingMode.DirectionalKey);
        SampleEnvironment.Configure(_renderer, SampleEnvironmentMode.ProceduralOutdoor);
        ApplyPerformanceScenario(contract.Scenario);
        SampleGlobalIlluminationValidation.ConfigureRenderSettings(_renderer.Settings, contract.Scenario);

        _sponzaGiCaptureRestoreState = new SponzaGiCaptureRestoreState(
            _renderer.Settings.GlobalIllumination.Enabled,
            _renderer.Settings.GlobalIllumination.DebugView,
            _renderer.Settings.Debug.Enabled,
            _renderer.Settings.Debug.AllowScreenshots,
            _renderer.Settings.Debug.CpuSnapshotsEnabled,
            _renderer.Settings.Particles.Enabled,
            _renderer.Settings.Animation.Enabled,
            _renderer.Settings.FeatureIsolation,
            _renderer.Settings.Debug.AllowGpuTiming,
            _renderer.Settings.Diagnostics.DdgiForwardEstimateCountersEnabled,
            _renderer.Settings.Environment.DiffuseIntensity,
            _renderer.Settings.Environment.SpecularIntensity,
            previousCaptureScenario);
        _renderer.Settings.Particles.Enabled = false;
        _renderer.Settings.Animation.Enabled = false;
        _renderer.Settings.FeatureIsolation = RenderFeatureIsolationMode.FullFrame;
        _renderer.Settings.Debug.AllowGpuTiming = captureMode == SampleSponzaGiCaptureMode.ProductionTiming;
        _renderer.Settings.Diagnostics.DdgiForwardEstimateCountersEnabled =
            SampleSponzaGiCaptureContract.UsesDetailedInvestigationCounters(captureMode);
        _renderer.CaptureScenario = contract.Scenario.ToString();
        for (int i = 0; i < _particleEffects.Count; i++)
            _particleEffects[i].Restart(contract.RandomSeed + (uint)(i * 101));
        _particlesPaused = false;

        IReadOnlyList<string> lockViolations = contract.ValidateLockedSettings(_renderer.Settings);
        if (lockViolations.Count != 0)
        {
            RestoreSponzaGiCaptureState();
            throw new InvalidOperationException(
                "Sponza GI capture settings are not locked: " + string.Join(" ", lockViolations));
        }

        IReadOnlyList<string> lightingViolations =
            contract.ValidateLockedLighting(_lightManager.GetLightSnapshot());
        if (lightingViolations.Count != 0)
        {
            RestoreSponzaGiCaptureState();
            throw new InvalidOperationException(
                "Sponza GI capture lighting is not locked: " + string.Join(" ", lightingViolations));
        }

        SimpleDdgiReceiverCoverageReport coverageReport = SimpleDdgiReceiverCoverageValidator.Validate(
            _renderer.Settings.GlobalIllumination,
            contract.SceneBounds,
            contract.CreateCoverageRegions(),
            contract.CreateCoverageCameraPath());
        if (!coverageReport.IsCovered)
        {
            RestoreSponzaGiCaptureState();
            throw new InvalidOperationException(
                "Sponza GI receiver coverage failed across the complete locked vertical trajectory: " +
                string.Join(" ", coverageReport.Issues.Select(static issue => issue.Message)));
        }

        _sponzaGiCaptureSequence = new SampleSponzaGiCaptureSequence(contract);
        _sponzaGiCaptureDirectory = Path.GetFullPath(outputDirectory);
        _sponzaGiCaptureExitWhenComplete = exitWhenComplete;
        _sponzaGiCaptureMode = captureMode;
        _sponzaGiScreenshotVerificationFrames = 0;
        _sponzaGiCaptureArtifacts.Clear();
        _sponzaGiPendingRendererScreenshots.Clear();

        contract.WriteContract(_sponzaGiCaptureDirectory);
        contract.WriteVisualMetricGate(_sponzaGiCaptureDirectory, captureMode);
        contract.WriteCoverageOracleReport(_sponzaGiCaptureDirectory, coverageReport);
        AddVerifiedSponzaGiArtifact(
            contract,
            string.Empty,
            string.Empty,
            "capture-contract",
            "sponza-gi-capture-contract.json");
        AddVerifiedSponzaGiArtifact(
            contract,
            string.Empty,
            string.Empty,
            "visual-metric-gate",
            "sponza-gi-visual-metric-gate.json");
        AddVerifiedSponzaGiArtifact(
            contract,
            string.Empty,
            string.Empty,
            "coverage-oracle",
            "sponza-gi-coverage-oracle.json");
        contract.WriteRunManifest(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts,
            "running",
            captureMode: _sponzaGiCaptureMode);
        Console.WriteLine(
            $"Locked Sponza GI capture started: directory={_sponzaGiCaptureDirectory}, " +
            $"warmup={contract.WarmupFrames} frames, path={contract.VerticalPathDurationSeconds}s at " +
            $"{contract.FramesPerSecond}fps, outputs={contract.Outputs.Count} per bookmark, " +
            $"mode={captureMode}, timing={GetSponzaGiTimingLabel(captureMode)}, fingerprint={contract.Fingerprint}.");
    }

    private void UpdateSponzaGiCapture(int viewportWidth, int viewportHeight)
    {
        SampleSponzaGiCaptureSequence sequence = _sponzaGiCaptureSequence
            ?? throw new InvalidOperationException("The Sponza GI capture sequence was not initialized.");
        SampleSponzaGiCaptureContract contract = sequence.Contract;
        if (viewportWidth != contract.Width || viewportHeight != contract.Height)
        {
            AbortSponzaGiCapture(
                $"Locked resolution is {contract.Width}x{contract.Height}, but the current viewport is {viewportWidth}x{viewportHeight}.");
            return;
        }

        SampleSponzaGiCaptureInstruction instruction = sequence.CurrentInstruction;
        ApplySponzaGiCaptureCamera(instruction.Camera, viewportWidth, viewportHeight);
        ApplySponzaGiCaptureOutput(instruction.Output);
        QueueSponzaGiRendererScreenshot(instruction);
    }

    private void ApplySponzaGiCaptureCamera(
        SampleSponzaGiCameraBookmark bookmark,
        int viewportWidth,
        int viewportHeight)
    {
        _camera.Position = bookmark.Position;
        _camera.Yaw = bookmark.Yaw;
        _camera.Pitch = bookmark.Pitch;
        _camera.FieldOfView = bookmark.FieldOfView;
        _camera.NearPlane = bookmark.NearPlane;
        _camera.FarPlane = bookmark.FarPlane;
        if (viewportHeight > 0)
            _camera.AspectRatio = (float)viewportWidth / viewportHeight;
        _camera.Update();
    }

    private void ApplySponzaGiCaptureOutput(SampleSponzaGiCaptureOutput? output)
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        bool normalGiEnabled = _sponzaGiCaptureRestoreState?.GlobalIlluminationEnabled ?? true;
        gi.Enabled = output is { DisableGlobalIllumination: true } ? false : normalGiEnabled;
        gi.DebugView = output?.DebugView ?? GlobalIlluminationDebugView.None;
        bool disableEnvironmentLighting = output is { DisableEnvironmentLighting: true };
        _renderer.Settings.Environment.DiffuseIntensity = disableEnvironmentLighting
            ? 0.0f
            : _sponzaGiCaptureRestoreState?.EnvironmentDiffuseIntensity ?? 1.0f;
        _renderer.Settings.Environment.SpecularIntensity = disableEnvironmentLighting
            ? 0.0f
            : _sponzaGiCaptureRestoreState?.EnvironmentSpecularIntensity ?? 1.0f;
        _renderer.Settings.Debug.Enabled = true;
        _renderer.Settings.Debug.AllowScreenshots = true;
        _renderer.Settings.Debug.CpuSnapshotsEnabled = false;
        _renderer.Settings.Debug.AllowGpuTiming =
            _sponzaGiCaptureMode == SampleSponzaGiCaptureMode.ProductionTiming &&
            string.Equals(output?.Name, "beauty", StringComparison.Ordinal);
    }

    /// <summary>
    /// Queues the renderer-target screenshot before the frame associated with
    /// an endpoint output is rendered. This is intentionally separate from
    /// the post-render window capture: a queue request issued after rendering
    /// would observe the next output after the state machine advances.
    /// </summary>
    private void QueueSponzaGiRendererScreenshot(SampleSponzaGiCaptureInstruction instruction)
    {
        if (_renderer == null || _sponzaGiCaptureSequence == null ||
            string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory) || instruction.Output == null)
        {
            return;
        }

        SampleSponzaGiCaptureContract contract = _sponzaGiCaptureSequence.Contract;
        string imagePath = contract.GetRelativeImagePath(instruction.BookmarkName, instruction.Output);
        string rendererRelativePath = Path.ChangeExtension(imagePath, ".renderer.png");
        if (HasQueuedSponzaGiRendererScreenshot(
                instruction.BookmarkName,
                instruction.Output.Name,
                rendererRelativePath))
        {
            return;
        }

        string rendererPath = Path.Combine(_sponzaGiCaptureDirectory, rendererRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(rendererPath) ?? _sponzaGiCaptureDirectory);
        _renderer.RequestScreenshot(rendererPath);
        int rendererArtifactIndex = _sponzaGiCaptureArtifacts.Count;
        _sponzaGiCaptureArtifacts.Add(new SampleSponzaGiCapturedArtifact(
            instruction.BookmarkName,
            instruction.Output.Name,
            "renderer-screenshot-request",
            rendererRelativePath,
            VerificationStatus: "requested"));
        _sponzaGiPendingRendererScreenshots.Add(new SponzaGiPendingRendererScreenshot(
            rendererArtifactIndex,
            instruction.BookmarkName,
            instruction.Output.Name,
            rendererRelativePath));
        contract.WriteRunManifest(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts,
            "running",
            captureMode: _sponzaGiCaptureMode);
    }

    private bool HasQueuedSponzaGiRendererScreenshot(
        string bookmark,
        string output,
        string relativePath)
    {
        return _sponzaGiCaptureArtifacts.Any(artifact =>
            string.Equals(artifact.Bookmark, bookmark, StringComparison.Ordinal) &&
            string.Equals(artifact.Output, output, StringComparison.Ordinal) &&
            string.Equals(
                artifact.RelativePath.Replace('\\', '/'),
                relativePath.Replace('\\', '/'),
                StringComparison.Ordinal) &&
            (string.Equals(artifact.Kind, "renderer-screenshot-request", StringComparison.Ordinal) ||
                string.Equals(artifact.Kind, "renderer-screenshot-observed", StringComparison.Ordinal) ||
                string.Equals(artifact.Kind, "renderer-screenshot", StringComparison.Ordinal)));
    }

    private void AdvanceSponzaGiCaptureAfterRenderedFrame()
    {
        if (_sponzaGiCaptureSequence == null || _renderer == null || string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory))
            return;

        if (!VerifyPendingRendererScreenshotArtifacts())
            return;

        if (_sponzaGiCaptureSequence.IsComplete)
        {
            CompleteSponzaGiCapture();
            return;
        }

        SampleSponzaGiCaptureInstruction instruction = _sponzaGiCaptureSequence.CurrentInstruction;
        if (instruction.Output != null &&
            instruction.CaptureWindowAfterRenderedFrame &&
            !CaptureSponzaGiOutput(instruction))
            return;

        if (_sponzaGiCaptureSequence == null)
            return;

        if (_sponzaGiCaptureSequence.AdvanceAfterRenderedFrame())
            CompleteSponzaGiCapture();
    }

    private bool CaptureSponzaGiOutput(SampleSponzaGiCaptureInstruction instruction)
    {
        if (_renderer == null || _sponzaGiCaptureSequence == null ||
            string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory) || instruction.Output == null)
        {
            return false;
        }

        SampleSponzaGiCaptureContract contract = _sponzaGiCaptureSequence.Contract;
        SampleSponzaGiCaptureOutput output = instruction.Output;
        string relativeImagePath = contract.GetRelativeImagePath(instruction.BookmarkName, output);
        string imagePath = Path.Combine(_sponzaGiCaptureDirectory, relativeImagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(imagePath) ?? _sponzaGiCaptureDirectory);
        if (_requestDiagnosticScreenshotCapture == null)
        {
            AbortSponzaGiCapture("The locked capture requires an immediate screenshot service, but none was configured.");
            return false;
        }

        bool windowCaptured;
        try
        {
            // Game invokes this callback before VulkanRenderer.EndFrame submits
            // the current command buffer. The sequence therefore holds every
            // endpoint output for three frames: one presents it, one spans the
            // timestamp latency, and this final frame captures the identical
            // already-presented state with settled telemetry.
            windowCaptured = _requestDiagnosticScreenshotCapture(imagePath);
        }
        catch (Exception ex)
        {
            AbortSponzaGiCapture($"Immediate screenshot capture threw: {ex.Message}");
            return false;
        }
        if (!windowCaptured)
        {
            AbortSponzaGiCapture($"Immediate screenshot capture failed for '{relativeImagePath}'.");
            return false;
        }
        if (!TryAddVerifiedSponzaGiArtifact(
                contract,
                instruction.BookmarkName,
                output.Name,
                "window-screenshot",
                relativeImagePath,
                out string windowVerificationFailure))
        {
            AbortSponzaGiCapture(windowVerificationFailure);
            return false;
        }

        // The renderer-target request is deliberately queued by
        // UpdateSponzaGiCapture before this frame starts rendering. Queuing it
        // here would capture the next debug view after the state machine has
        // advanced, so verify that the pre-render request exists instead.
        string rendererRelativePath = Path.ChangeExtension(relativeImagePath, ".renderer.png");
        if (!HasQueuedSponzaGiRendererScreenshot(
                instruction.BookmarkName,
                output.Name,
                rendererRelativePath))
        {
            AbortSponzaGiCapture(
                $"Renderer screenshot was not queued before rendering '{instruction.BookmarkName}' / '{output.Name}'.");
            return false;
        }

        if (string.Equals(output.Name, "beauty", StringComparison.Ordinal))
        {
            string snapshotDirectory = Path.Combine(
                _sponzaGiCaptureDirectory,
                Path.GetDirectoryName(relativeImagePath) ?? string.Empty);
            string snapshotPath;
            try
            {
                snapshotPath = ExportPerformanceSnapshotFile(
                    snapshotDirectory,
                    $"Sponza GI {instruction.BookmarkName} capture metadata");
            }
            catch (Exception ex)
            {
                AbortSponzaGiCapture($"Performance snapshot export failed for '{instruction.BookmarkName}': {ex.Message}");
                return false;
            }
            if (!TryAddVerifiedSponzaGiArtifact(
                    contract,
                    instruction.BookmarkName,
                    output.Name,
                    "performance-snapshot",
                    Path.GetRelativePath(_sponzaGiCaptureDirectory, snapshotPath),
                    out string snapshotVerificationFailure))
            {
                AbortSponzaGiCapture(snapshotVerificationFailure);
                return false;
            }
        }

        contract.WriteRunManifest(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts,
            "running",
            captureMode: _sponzaGiCaptureMode);
        return true;
    }

    /// <summary>
    /// Converts renderer screenshot requests into hash-verified artifacts only
    /// after the requested target exists and is a readable PNG. A request is
    /// intentionally not treated as evidence: it may still be queued, fail, or
    /// be only partially written when this callback first observes it.
    /// </summary>
    private bool VerifyPendingRendererScreenshotArtifacts()
    {
        if (_sponzaGiCaptureSequence == null || string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory))
            return true;

        SampleSponzaGiCaptureContract contract = _sponzaGiCaptureSequence.Contract;
        for (int pendingIndex = _sponzaGiPendingRendererScreenshots.Count - 1; pendingIndex >= 0; pendingIndex--)
        {
            SponzaGiPendingRendererScreenshot pending = _sponzaGiPendingRendererScreenshots[pendingIndex];
            if (pending.ArtifactIndex < 0 || pending.ArtifactIndex >= _sponzaGiCaptureArtifacts.Count)
            {
                AbortSponzaGiCapture("Renderer screenshot verification lost its requested artifact record.");
                return false;
            }

            SampleSponzaGiCapturedArtifact requested = _sponzaGiCaptureArtifacts[pending.ArtifactIndex];
            if (!contract.TryVerifyArtifact(
                    _sponzaGiCaptureDirectory,
                    requested,
                    out SampleSponzaGiCapturedArtifact verified,
                    out _))
            {
                // Missing, locked, zero-byte, or incomplete files remain in the
                // bounded settlement phase. Completion will report/timeout them
                // explicitly instead of treating an asynchronous request as done.
                if (pending.StableObservationCount != 0)
                {
                    // A previously observed target mutated before the second
                    // frame. Discard its old hash and begin a fresh stability
                    // observation once the writer settles.
                    _sponzaGiCaptureArtifacts[pending.ArtifactIndex] = requested with
                    {
                        Kind = "renderer-screenshot-request",
                        Sha256 = null,
                        ByteLength = 0,
                        VerificationStatus = "requested"
                    };
                    _sponzaGiPendingRendererScreenshots[pendingIndex] = pending with
                    {
                        StableObservationCount = 0
                    };
                }
                continue;
            }

            if (pending.StableObservationCount == 0)
            {
                // Require the same content hash on the following rendered
                // frame. This prevents a writer that has merely created its
                // destination file from racing the terminal manifest.
                _sponzaGiCaptureArtifacts[pending.ArtifactIndex] = verified with
                {
                    Kind = "renderer-screenshot-observed",
                    Bookmark = pending.Bookmark,
                    Output = pending.Output
                };
                _sponzaGiPendingRendererScreenshots[pendingIndex] = pending with
                {
                    StableObservationCount = 1
                };
                continue;
            }

            _sponzaGiCaptureArtifacts[pending.ArtifactIndex] = verified with
            {
                Kind = "renderer-screenshot",
                Bookmark = pending.Bookmark,
                Output = pending.Output
            };
            _sponzaGiPendingRendererScreenshots.RemoveAt(pendingIndex);
        }

        return true;
    }

    private void AddVerifiedSponzaGiArtifact(
        SampleSponzaGiCaptureContract contract,
        string bookmark,
        string output,
        string kind,
        string relativePath)
    {
        if (!TryAddVerifiedSponzaGiArtifact(
                contract,
                bookmark,
                output,
                kind,
                relativePath,
                out string failureReason))
        {
            throw new InvalidOperationException(failureReason);
        }
    }

    private bool TryAddVerifiedSponzaGiArtifact(
        SampleSponzaGiCaptureContract contract,
        string bookmark,
        string output,
        string kind,
        string relativePath,
        out string failureReason)
    {
        if (string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory))
            throw new InvalidOperationException("The Sponza GI capture output directory was not initialized.");

        var artifact = new SampleSponzaGiCapturedArtifact(bookmark, output, kind, relativePath);
        if (!contract.TryVerifyArtifact(_sponzaGiCaptureDirectory, artifact, out SampleSponzaGiCapturedArtifact verified, out failureReason))
        {
            failureReason = $"Required {kind} artifact '{relativePath}' was not verified: {failureReason}";
            return false;
        }

        _sponzaGiCaptureArtifacts.Add(verified);
        return true;
    }

    private static string GetSponzaGiTimingLabel(SampleSponzaGiCaptureMode captureMode) => captureMode switch
    {
        SampleSponzaGiCaptureMode.ProductionTiming => "production beauty timing only",
        SampleSponzaGiCaptureMode.DetailedDiagnostics => "diagnostic evidence only; timing disabled",
        _ => throw new ArgumentOutOfRangeException(nameof(captureMode))
    };

    private void CompleteSponzaGiCapture()
    {
        if (_sponzaGiCaptureSequence == null || string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory))
            return;

        SampleSponzaGiCaptureContract contract = _sponzaGiCaptureSequence.Contract;
        if (_sponzaGiPendingRendererScreenshots.Count != 0)
        {
            _sponzaGiScreenshotVerificationFrames++;
            if (_sponzaGiScreenshotVerificationFrames > SponzaGiRendererScreenshotVerificationTimeoutFrames)
            {
                AbortSponzaGiCapture(
                    $"Renderer screenshot verification timed out after {SponzaGiRendererScreenshotVerificationTimeoutFrames} frames: " +
                    DescribePendingSponzaGiRendererScreenshots());
                return;
            }

            if (_sponzaGiScreenshotVerificationFrames == 1 ||
                _sponzaGiScreenshotVerificationFrames % _sponzaGiCaptureSequence.Contract.FramesPerSecond == 0)
            {
                contract.WriteRunManifest(
                    _sponzaGiCaptureDirectory,
                    _sponzaGiCaptureArtifacts,
                    "awaiting-renderer-screenshots",
                    DescribePendingSponzaGiRendererScreenshots(),
                    _sponzaGiCaptureMode);
            }
            return;
        }

        IReadOnlyList<string> blockers = contract.GetCompletionBlockers(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts);
        if (blockers.Count != 0)
        {
            AbortSponzaGiCapture(
                "The Sponza GI completion gate rejected the final artifact set: " +
                string.Join(" ", blockers));
            return;
        }

        contract.WriteRunManifest(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts,
            "completed",
            captureMode: _sponzaGiCaptureMode);
        Console.WriteLine(
            $"Locked Sponza GI capture completed: {_sponzaGiCaptureDirectory} " +
            $"mode={_sponzaGiCaptureMode}, timing={GetSponzaGiTimingLabel(_sponzaGiCaptureMode)}.");
        bool exitWhenComplete = _sponzaGiCaptureExitWhenComplete;
        RestoreSponzaGiCaptureState();
        if (exitWhenComplete)
            _exit();
    }

    private string DescribePendingSponzaGiRendererScreenshots()
    {
        return string.Join(
            " ",
            _sponzaGiPendingRendererScreenshots.Select(static pending =>
                $"Awaiting renderer screenshot '{pending.RelativePath}' for '{pending.Bookmark}' / '{pending.Output}' " +
                $"(stability observations={pending.StableObservationCount})."));
    }

    private void AbortSponzaGiCapture(string reason)
    {
        if (_sponzaGiCaptureSequence == null || string.IsNullOrWhiteSpace(_sponzaGiCaptureDirectory))
            return;

        _sponzaGiCaptureSequence.Contract.WriteRunManifest(
            _sponzaGiCaptureDirectory,
            _sponzaGiCaptureArtifacts,
            "failed",
            reason,
            _sponzaGiCaptureMode);
        Console.WriteLine($"Locked Sponza GI capture aborted: {reason}");
        bool exitWhenComplete = _sponzaGiCaptureExitWhenComplete;
        RestoreSponzaGiCaptureState();
        if (exitWhenComplete)
            _exit();
    }

    private void RestoreSponzaGiCaptureState()
    {
        if (_renderer != null && _sponzaGiCaptureRestoreState != null)
        {
            SponzaGiCaptureRestoreState state = _sponzaGiCaptureRestoreState;
            _renderer.Settings.GlobalIllumination.Enabled = state.GlobalIlluminationEnabled;
            _renderer.Settings.GlobalIllumination.DebugView = state.GlobalIlluminationDebugView;
            _renderer.Settings.Debug.Enabled = state.DebugEnabled;
            _renderer.Settings.Debug.AllowScreenshots = state.AllowScreenshots;
            _renderer.Settings.Debug.CpuSnapshotsEnabled = state.CpuSnapshotsEnabled;
            _renderer.Settings.Particles.Enabled = state.ParticlesEnabled;
            _renderer.Settings.Animation.Enabled = state.AnimationEnabled;
            _renderer.Settings.FeatureIsolation = state.FeatureIsolation;
            _renderer.Settings.Debug.AllowGpuTiming = state.AllowGpuTiming;
            _renderer.Settings.Diagnostics.DdgiForwardEstimateCountersEnabled =
                state.DdgiForwardEstimateCountersEnabled;
            _renderer.Settings.Environment.DiffuseIntensity = state.EnvironmentDiffuseIntensity;
            _renderer.Settings.Environment.SpecularIntensity = state.EnvironmentSpecularIntensity;
            _renderer.CaptureScenario = state.CaptureScenario;
        }

        _sponzaGiCaptureSequence = null;
        _sponzaGiCaptureDirectory = null;
        _sponzaGiCaptureExitWhenComplete = false;
        _sponzaGiCaptureRestoreState = null;
        _sponzaGiCaptureArtifacts.Clear();
        _sponzaGiPendingRendererScreenshots.Clear();
        _sponzaGiScreenshotVerificationFrames = 0;
        _sponzaGiCaptureMode = SampleSponzaGiCaptureMode.ProductionTiming;
    }

    public void OnFrameRendered(int frameIndex, RendererDiagnostics diagnostics, RenderBudgetSnapshot budget)
    {
        AdvanceSponzaGiCaptureAfterRenderedFrame();

        if (_runtimeBenchmarkCapture == null)
            return;

        if (!_runtimeBenchmarkCapture.OnFrameRendered(frameIndex, diagnostics, budget))
            return;

        SampleBenchmarkReport? report = _runtimeBenchmarkCapture.Report;
        string? path = _runtimeBenchmarkCapture.ReportPath;
        if (report != null && !string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine(
                $"Runtime benchmark exported: {path} scenario={report.Scenario} " +
                $"cpuP95={report.CpuFrameMilliseconds.P95Milliseconds:F3}ms " +
                $"gpuP95={report.GpuFrameMilliseconds.P95Milliseconds:F3}ms " +
                $"top='{report.Findings.FirstOrDefault()?.Subject ?? "none"}'");
        }

        _runtimeBenchmarkCapture = null;
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
            case SamplePerformanceScenario.GiSponzaRightWallStationary:
                ApplyPerformanceScenario(SamplePerformanceScenario.GiSponzaRightWallStationary);
                MoveCamera(SponzaRightWallPosition, SponzaRightWallYaw, SponzaRightWallPitch);
                break;
            default:
                throw new ArgumentException($"Unsupported baseline scenario '{scenario}'.", nameof(scenario));
        }
    }

    private void ApplyPerformanceScenario(SamplePerformanceScenario scenario)
    {
        if (_renderer != null)
            _renderer.CaptureScenario = scenario.ToString();
        if (_performanceScenarioRunner == null)
            return;

        if (_runtimeBenchmarkCapture != null && _performanceScenarioRunner.CurrentScenario != scenario)
        {
            Console.WriteLine($"Runtime benchmark canceled: switching to {scenario}.");
            _runtimeBenchmarkCapture = null;
        }

        if (_performanceScenarioRunner.CurrentScenario == scenario)
        {
            RestoreGlobalIlluminationValidationSettings(scenario);
            return;
        }

        PrintPerformanceScenarioSummary(_performanceScenarioRunner.Apply(scenario));
        RestoreGlobalIlluminationValidationSettings(scenario);
    }

    private void LoadPerformanceScenarioPreset(SamplePerformanceScenario scenario, Vector3 position, float yaw, float pitch)
    {
        ApplyPerformanceScenario(scenario);
        MoveCamera(position, yaw, pitch);
        Console.WriteLine($"Runtime performance scene loaded: {scenario}. Press Ctrl+B to capture a benchmark.");
    }

    private void StartRuntimeBenchmarkCapture()
    {
        if (_renderer == null || _performanceScenarioRunner == null)
            return;

        SamplePerformanceScenario scenario = _performanceScenarioRunner.CurrentScenario;
        if (scenario == SamplePerformanceScenario.Normal)
        {
            Console.WriteLine("Runtime benchmark skipped: load a performance scenario first with Ctrl+1, Ctrl+2, or Ctrl+F3.");
            return;
        }

        _renderer.Settings.Debug.AllowGpuTiming = true;
        _runtimeBenchmarkCapture = new SampleRuntimeBenchmarkCapture(
            scenario,
            RuntimeBenchmarkWarmupFrameCount,
            RuntimeBenchmarkMeasureFrameCount);
        Console.WriteLine(
            $"Runtime benchmark started: scenario={scenario}, warmup={_runtimeBenchmarkCapture.WarmupFrameCount}, " +
            $"measure={_runtimeBenchmarkCapture.MeasureFrameCount}, gpuTiming=enabled.");
    }

    private void RestoreGlobalIlluminationValidationSettings(SamplePerformanceScenario scenario)
    {
        if (_renderer == null || !SampleGlobalIlluminationValidation.IsValidationScenario(scenario))
            return;

        SampleGlobalIlluminationValidation.ConfigureRenderSettings(_renderer.Settings, scenario);
        PrintGlobalIlluminationSettings("GI validation");
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
        TransparencySettings transparency = _renderer.Settings.Transparency;
        Console.WriteLine(
            $"{prefix}: {(shadows.DirectionalShadowsEnabled ? "enabled" : "disabled")}, " +
            $"map={shadows.DirectionalShadowMapSize}, cascades={shadows.DirectionalCascadeCount}, " +
            $"pcf={shadows.PcfRadius}/{shadows.SpotPcfRadius}/{shadows.PointPcfRadius}, " +
            $"normalBias={shadows.NormalBias:F4}, slopeBias={shadows.SlopeScaledDepthBias:F2}, " +
            $"spot={(shadows.SpotShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedSpotLights}@{shadows.SpotShadowTileSize}, " +
            $"point={(shadows.PointShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedPointLights}@{shadows.PointShadowMapSize}, " +
            $"spotBias={shadows.SpotNormalBias:F4}, pointBias={shadows.PointNormalBias:F4}, " +
            $"foliage={(foliage.CastShadows ? "on" : "off")}:{foliage.GrassShadowDistance:F1}m@{foliage.GrassShadowDensityScale:F2}, " +
            $"foliageIndirect={(foliage.IndirectMeshletDispatchEnabled ? "on" : "off")}, " +
            $"farImpostors={(foliage.FarImpostorsEnabled ? "on" : "off")}, " +
            $"foliageLocal={(foliage.LocalShadowsEnabled ? "on" : "off")}:{foliage.MaxLocalShadowedSpotLights}/{foliage.MaxLocalShadowedPointLights}, " +
            $"transparentReceive={(transparency.ReceiveShadows ? "on" : "off")}, " +
            $"foliageMotion={(foliage.MotionVectorsEnabled ? "on" : "off")}, " +
            $"debug={shadows.DebugView}");
    }

    private void ToggleAllShadowsForDiagnostics()
    {
        if (_renderer == null)
            return;

        ShadowSettings shadows = _renderer.Settings.Shadows;
        FoliageSettings foliage = _renderer.Settings.Foliage;
        TransparencySettings transparency = _renderer.Settings.Transparency;
        bool anyShadowEnabled =
            shadows.DirectionalShadowsEnabled ||
            shadows.SpotShadowsEnabled ||
            shadows.PointShadowsEnabled ||
            foliage.CastShadows ||
            foliage.LocalShadowsEnabled ||
            transparency.ReceiveShadows;

        if (anyShadowEnabled)
        {
            _savedShadowToggleState = new ShadowToggleState(
                shadows.DirectionalShadowsEnabled,
                shadows.SpotShadowsEnabled,
                shadows.PointShadowsEnabled,
                foliage.CastShadows,
                foliage.LocalShadowsEnabled,
                transparency.ReceiveShadows);
            shadows.DirectionalShadowsEnabled = false;
            shadows.SpotShadowsEnabled = false;
            shadows.PointShadowsEnabled = false;
            foliage.CastShadows = false;
            foliage.LocalShadowsEnabled = false;
            transparency.ReceiveShadows = false;
            PrintShadowSettings("All shadows disabled for diagnostics");
            return;
        }

        ShadowToggleState restore = _savedShadowToggleState ?? new ShadowToggleState(
            true,
            true,
            true,
            true,
            true,
            true);
        shadows.DirectionalShadowsEnabled = restore.Directional;
        shadows.SpotShadowsEnabled = restore.Spot;
        shadows.PointShadowsEnabled = restore.Point;
        foliage.CastShadows = restore.FoliageCast;
        foliage.LocalShadowsEnabled = restore.FoliageLocal;
        transparency.ReceiveShadows = restore.TransparentReceive;
        PrintShadowSettings("All shadows restored");
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
            $"gpuLod={(submission.GpuLodSelectionEnabled ? "on" : "off")} " +
            $"ratios={submission.GpuLod1DistanceRatio:F1}/{submission.GpuLod2DistanceRatio:F1}, " +
            $"shadowCompaction={(submission.GpuShadowCompactionEnabled ? "on" : "off")}, " +
            $"shadowLodBias={submission.GpuShadowLodBias}, " +
            $"validation={(submission.ValidationCompareCpuGpuLists ? "on" : "off")}, " +
            $"mode={diagnostics.SceneSubmissionActiveMode}, forwardPath={diagnostics.SceneSubmissionForwardPath}, taskShader={diagnostics.SceneSubmissionForwardTaskShader}, " +
            $"cpuCandidates={diagnostics.SceneSubmissionCpuCandidateCount}, " +
            $"gpuEmitted={diagnostics.SceneSubmissionGpuEmittedCount}, indirectTasks={diagnostics.SceneSubmissionIndirectTaskCount}, " +
            $"forwardBuckets={diagnostics.ForwardSimpleMeshletCount}/{diagnostics.ForwardFullMaterialMeshletCount}/{diagnostics.ForwardLocalProbeMeshletCount}, " +
            $"tileLights={diagnostics.AverageLightsPerNonEmptyTile:F1}/{diagnostics.MaxLightsInAnyTile}/{diagnostics.LightTileSaturationCount}, " +
            $"lightCullRejected={diagnostics.LightCullRejectedPointCount}/{diagnostics.LightCullRejectedSpotCount}, " +
            $"tileClearBytes={diagnostics.TiledLightHeaderBufferClearBytes}/{diagnostics.TiledLightIndexBufferClearBytes}, " +
            $"fallback='{diagnostics.SceneSubmissionFallbackReason}', compactionSkip='{diagnostics.SceneSubmissionCompactionSkipReason}', " +
            $"indirectSkip='{diagnostics.SceneSubmissionIndirectDispatchSkipReason}', " +
            $"cpuOpaque={diagnostics.OpaqueMeshletCount}, cpuSubmittedFallback={diagnostics.MeshletCountSubmittedCpu}, " +
            $"gpuActive={diagnostics.SceneSubmissionGpuCompactionActive}, gpuCandidates={diagnostics.SceneSubmissionGpuOpaqueCandidateCount}, " +
            $"gpuFrustumRejected={diagnostics.SceneSubmissionGpuOpaqueFrustumRejectedCount}, gpuOverflow={diagnostics.SceneSubmissionGpuOpaqueOverflowCount}, " +
            $"gpuLodEmitted={diagnostics.SceneSubmissionGpuLod0EmittedCount}/{diagnostics.SceneSubmissionGpuLod1EmittedCount}/{diagnostics.SceneSubmissionGpuLod2EmittedCount}, " +
            $"gpuMissingLodFallback={diagnostics.SceneSubmissionGpuMissingLodFallbackCount}, " +
            $"gpuDepth={diagnostics.SceneSubmissionGpuCompactedSolidDepthMeshletCount}/{diagnostics.SceneSubmissionGpuCompactedMaskedDepthMeshletCount}, " +
            $"gpuDepthCandidates={diagnostics.SceneSubmissionGpuDepthSolidCandidateCount}/{diagnostics.SceneSubmissionGpuDepthMaskedCandidateCount}, " +
            $"gpuDepthOverflow={diagnostics.SceneSubmissionGpuDepthOverflowCount}, " +
            $"gpuDirShadow={diagnostics.SceneSubmissionGpuCompactedDirectionalShadowMeshletCount}/{diagnostics.SceneSubmissionGpuDirectionalShadowCandidateCount}, " +
            $"gpuDirShadowOverflow={diagnostics.SceneSubmissionGpuDirectionalShadowOverflowCount}, " +
            $"gpuDirShadowLod0Fallback={diagnostics.SceneSubmissionGpuDirectionalShadowLodFallbackCount}, " +
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
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        Console.WriteLine(
            $"{prefix}: {(ao.Enabled ? "enabled" : "disabled")}, mode={ao.Mode}, scale={ao.ResolutionScale:F2}, " +
            $"radius={ao.Radius:F2}, intensity={ao.Intensity:F2}, bias={ao.Bias:F3}, samples={ao.SampleCount}, " +
            $"blur={ao.BlurRadius}, forwardSampling={diagnostics.AmbientOcclusionForwardSamplingMode}, " +
            $"forwardDepthAwareSamples={diagnostics.AmbientOcclusionForwardDepthAwareSamples}, debug={ao.DebugView}");
    }

    private void PrintGlobalIlluminationSettings(string prefix)
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        long gpuMicroseconds = diagnostics.GpuSsgiTraceMicroseconds +
            diagnostics.GpuSsgiTemporalMicroseconds +
            diagnostics.GpuSsgiDenoiseMicroseconds +
            diagnostics.GpuDdgiUpdateMicroseconds +
            diagnostics.GpuGiCompositeMicroseconds;
        ulong giBytes = diagnostics.GlobalIlluminationRenderTargetBytes +
            diagnostics.DdgiTextureBytes +
            diagnostics.DdgiBufferBytes +
            diagnostics.AccelerationStructureBytes;
        Console.WriteLine(
            $"{prefix}: {(gi.Enabled ? "enabled" : "disabled")}, mode={gi.Mode}, lastEffectiveMode={diagnostics.GlobalIlluminationMode}, debug={gi.DebugView}, " +
            $"scale={gi.ResolutionScale:F2}, intensity={gi.IndirectIntensity:F2}, fallback={gi.EnvironmentFallbackIntensity:F2}, " +
            $"selfShadowBias={gi.DdgiSelfShadowBiasScale:F2}, hysteresisResponse={gi.DdgiHysteresisResponse:F2}, " +
            $"distance={gi.MaxBounceDistance:F1}, ssgi={(gi.EffectiveUseSsgi ? "on" : "off")}, " +
            $"ssgiSize={diagnostics.SsgiWidth}x{diagnostics.SsgiHeight}, ssgiRays={diagnostics.SsgiRayCount}, " +
            $"ssgiHistoryValid={diagnostics.SsgiHistoryValid}, ssgiRejected={diagnostics.SsgiRejectedHistoryPixelCount}, " +
            $"ddgi={(gi.EffectiveUseDdgi ? "legacy" : gi.EffectiveUseSimpleDdgi ? "simple" : "off")}, simpleActive={diagnostics.SimpleDdgiActive != 0}, ddgiProbes={diagnostics.DdgiActiveProbeCount}/{diagnostics.DdgiProbeCount}, " +
            $"ddgiUpdated={diagnostics.DdgiProbesUpdated}, ddgiRays={diagnostics.DdgiRaysPerProbe}, " +
            $"relocation={diagnostics.DdgiProbeRelocationCount}, classification={diagnostics.DdgiProbeClassificationCount}, l1Metadata={(gi.DdgiProbeL1MetadataEnabled ? "on" : "off")}, " +
            $"temporal={(gi.TemporalEnabled ? "on" : "off")}, denoise={(gi.DenoiserEnabled ? "on" : "off")}, " +
            $"rayQuerySupported={diagnostics.GlobalIlluminationRayQuerySupported != 0}, rayQueryActive={diagnostics.GlobalIlluminationRayQueryActive != 0}, " +
            $"cpuSsgiUs={diagnostics.CpuSsgiRecordMicroseconds}, cpuDdgiUs={diagnostics.CpuDdgiRecordMicroseconds}, " +
            $"gpuTrace/Temporal/Denoise/Ddgi/CompositeUs={diagnostics.GpuSsgiTraceMicroseconds}/{diagnostics.GpuSsgiTemporalMicroseconds}/{diagnostics.GpuSsgiDenoiseMicroseconds}/{diagnostics.GpuDdgiUpdateMicroseconds}/{diagnostics.GpuGiCompositeMicroseconds}, " +
            $"gpuUs={gpuMicroseconds}, bytes={giBytes} " +
            $"(targets={diagnostics.GlobalIlluminationRenderTargetBytes}, ddgiTex={diagnostics.DdgiTextureBytes}, ddgiBuf={diagnostics.DdgiBufferBytes}, as={diagnostics.AccelerationStructureBytes})");
    }

    private void PrintDdgiDiagnostics(string prefix)
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        RendererDiagnostics diagnostics = _renderer.LastDiagnostics;
        DdgiRuntimeSnapshot snapshot = diagnostics.DdgiRuntimeSnapshot;
        ulong currentAtlasBytes = diagnostics.DdgiCurrentIrradianceAtlasBytes + diagnostics.DdgiCurrentVisibilityAtlasBytes;
        Console.WriteLine(
            $"{prefix}: preset={_renderer.Settings.QualityPreset}, tier={gi.DdgiQualityTier}, mode={gi.Mode}, " +
            $"effective={diagnostics.GlobalIlluminationMode}, enabled={gi.Enabled}, ddgi={(gi.EffectiveUseDdgi ? "legacy" : gi.EffectiveUseSimpleDdgi ? "simple" : "off")}, " +
            $"simpleActive={diagnostics.SimpleDdgiActive != 0}, simpleProbes={diagnostics.SimpleDdgiProbeCount}, simpleUpdated={diagnostics.SimpleDdgiProbesUpdated}, simpleRays={diagnostics.SimpleDdgiRaysPerFrame}, ssgi={gi.EffectiveUseSsgi}, " +
            $"simpleRecenter={diagnostics.SimpleDdgiRecentered}, simplePreserve={diagnostics.SimpleDdgiAtlasPreservedOnRecenter}, simpleClear={diagnostics.SimpleDdgiAtlasCleared}, simpleFresh={diagnostics.SimpleDdgiAtlasFresh}, " +
            $"rayQuery={gi.EffectiveUseRayQueryBackend}/{diagnostics.GlobalIlluminationRayQueryActive}, debug={gi.DebugView}, async={diagnostics.DdgiAsyncComputeEnabled != 0}");
        Console.WriteLine(
            $"{prefix}: investigation simpleEvents recenter/clear/preserve/framesSinceClear/framesSinceRecenter={diagnostics.SimpleDdgiRecenterCount}/{diagnostics.SimpleDdgiAtlasClearCount}/{diagnostics.SimpleDdgiAtlasPreserveOnRecenterCount}/{diagnostics.SimpleDdgiFramesSinceLastClear}/{diagnostics.SimpleDdgiFramesSinceLastRecenter}, " +
            $"simpleForward fresh/zero/nonzero/avgIrrLum/avgVisibility/lowVisibility={diagnostics.SimpleDdgiFreshAtlasForwardSampleCount}/{diagnostics.SimpleDdgiZeroIrradianceSampleCount}/{diagnostics.SimpleDdgiNonzeroIrradianceSampleCount}/{diagnostics.SimpleDdgiAverageSampledIrradianceLuminance:F5}/{diagnostics.SimpleDdgiAverageVisibility:F3}/{diagnostics.SimpleDdgiLowVisibilitySampleCount}, " +
            $"update full/partial/fraction/start/end/skipped={diagnostics.DdgiFullRefreshFrameCount}/{diagnostics.DdgiPartialRefreshFrameCount}/{diagnostics.DdgiUpdatedProbeFraction:F3}/{diagnostics.DdgiProbeUpdateStartIndex}/{diagnostics.DdgiProbeUpdateEndIndex}/{diagnostics.DdgiSkippedProbeCount}, " +
            $"age p50/p95/max={diagnostics.DdgiFramesSinceProbeUpdatedP50:F1}/{diagnostics.DdgiFramesSinceProbeUpdatedP95:F1}/{diagnostics.DdgiFramesSinceProbeUpdatedMax:F1}, invalidated={diagnostics.DdgiNewlyInvalidatedProbeCount}, reasons={diagnostics.DdgiRefreshReasonRecenterProbeCount}/{diagnostics.DdgiRefreshReasonDirtyProbeCount}/{diagnostics.DdgiRefreshReasonAgeProbeCount}/{diagnostics.DdgiRefreshReasonVisibilityProbeCount}/{diagnostics.DdgiRefreshReasonFullRefreshProbeCount}, " +
            $"forward={diagnostics.DdgiForwardSimplePathSampleCount}/{diagnostics.DdgiForwardLegacyPathSampleCount}/{diagnostics.DdgiForwardZeroFinalIndirectCount}/{diagnostics.DdgiForwardZeroDdgiButNonzeroIblCount}/{diagnostics.DdgiForwardZeroDdgiAndZeroIblCount}/{diagnostics.DdgiForwardOutOfGridSampleCount}/{diagnostics.DdgiForwardClampedProbeSampleCount}/{diagnostics.DdgiForwardNanOrInfSampleCount}, " +
            $"atlas={diagnostics.DdgiIrradianceAtlasZeroTexelSampleCount}/{diagnostics.DdgiVisibilityAtlasZeroMomentSampleCount}/{diagnostics.DdgiAtlasWriteProbeCount}/{diagnostics.DdgiAtlasWriteTexelCount}/{diagnostics.DdgiBlendZeroRayWeightProbeCount}/{diagnostics.DdgiBlendNonzeroIrradianceProbeCount}/{diagnostics.DdgiBlendPreviousAtlasUsedCount}/{diagnostics.DdgiBlendHysteresisZeroFrameCount}, " +
            $"trace={diagnostics.DdgiSimpleTraceHitCount}/{diagnostics.DdgiSimpleTraceMissCount}/{diagnostics.DdgiSimpleTraceZeroRadianceHitCount}/{diagnostics.DdgiSimpleTraceDirectLightHitCount}/{diagnostics.DdgiSimpleTraceEmissiveHitCount}/{diagnostics.DdgiSimpleTraceFarFieldHitCount}/{diagnostics.DdgiSimpleTraceFarFieldMissCount}/{diagnostics.DdgiSimpleTraceTlasUnavailableFrameCount}, " +
            $"simpleFar={diagnostics.SimpleDdgiSkyVisibilitySampleCount}/{diagnostics.SimpleDdgiAverageSkyVisibility:F3}/{diagnostics.FarFieldSunShadowSampleCount}/{diagnostics.FarFieldSunShadowOccludedCount}/{diagnostics.SimpleDdgiRoughSpecularSampleCount}/{diagnostics.SimpleDdgiRoughSpecularNonzeroCount}, " +
            $"farSteps={diagnostics.DdgiSimpleTraceFarFieldStepBucket0Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket1Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket2Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket3Count}/{diagnostics.DdgiSimpleTraceFarFieldStepBucket4Count}, " +
            $"black={diagnostics.DdgiBlackFrameSuspect}/{diagnostics.DdgiBlackFrameAfterRecenter}/{diagnostics.DdgiBlackFrameAfterAtlasClear}/{diagnostics.DdgiBlackFrameDuringFreshAtlas}/{diagnostics.DdgiBlackFrameMovementClass}");
        Console.WriteLine(
            $"{prefix}: volumes={diagnostics.DdgiProbeVolumeCount}, cascades={diagnostics.DdgiCascadeCount}, probes={diagnostics.DdgiActiveProbeCount}/{diagnostics.DdgiProbeCount}, " +
            $"updated={diagnostics.DdgiProbesUpdated}, raysPerProbe={diagnostics.DdgiRaysPerProbe}, scheduledPrimaryRays={diagnostics.DdgiScheduledPrimaryRayCount}, " +
            $"shadowRayUpper={diagnostics.DdgiEstimatedShadowRayUpperBound}, updateBudget={diagnostics.DdgiMaxProbeUpdatesPerFrame}, rayBudget={diagnostics.DdgiProbeUpdatePrimaryRayBudget}, " +
            $"gatherFallback={diagnostics.DdgiGatherFallbackTileCount}, forwardFallback={diagnostics.DdgiForwardGatherFallbackUsed}/{diagnostics.DdgiForwardGatherFallbackDisabled}, emptyTiles={diagnostics.DdgiForwardGatherTileEmpty}");
        Console.WriteLine(
            $"{prefix}: snapshot volumes={snapshot.VolumeCount}, active={snapshot.ActiveProbeCount}, scheduled={snapshot.ScheduledProbeUpdates}, " +
            $"scheduler candidates/requests/rejected={snapshot.SchedulerCandidateCount}/{snapshot.SchedulerRequestCount}/{snapshot.SchedulerBudgetRejectedCount}, " +
            $"scheduleUs/p95={snapshot.SchedulerGpuMicroseconds}/{snapshot.SchedulerGpuP95Microseconds}, " +
            $"estimate spatial/support/data/visibility/effective/reloc/inactive={snapshot.EstimateSpatialCoverage:F3}/{snapshot.EstimateSupportCoverage:F3}/{snapshot.EstimateDataConfidence:F3}/{snapshot.EstimateVisibilityConfidence:F3}/{snapshot.EstimateEffectiveWeight:F3}/{snapshot.EstimateRelocationMagnitude:F3}/{snapshot.EstimateInactiveProbeCount}, " +
            $"tiles local/clipmap/fallback/empty={snapshot.SelectedLocalTileCount}/{snapshot.SelectedClipmapTileCount}/{snapshot.GatherFallbackTileCount}/{snapshot.EmptyGatherTileCount}");
        Console.WriteLine(
            $"{prefix}: warmup state={diagnostics.DdgiWarmupState}, warmed visible/local/cascade0={diagnostics.DdgiWarmedVisibleProbeFraction:P1}/{diagnostics.DdgiWarmedLocalProbeFraction:P1}/{diagnostics.DdgiWarmedCascade0ProbeFraction:P1}");
        Console.WriteLine(
            $"{prefix}: forwardEstimate valid={diagnostics.DdgiForwardEstimateCountersReadbackValid}, samples={diagnostics.DdgiForwardEstimateSampleCount}, " +
            $"zeroSupportSpatial={diagnostics.DdgiForwardEstimateZeroVisibleButCoveredCount}, zeroEffectiveSpatial={diagnostics.DdgiForwardEstimateZeroEffectiveButCoveredCount}, highOwnershipLowIndirect={diagnostics.DdgiForwardEstimateHighOwnershipLowDeliveredIndirectCount}, " +
            $"sampledIrrLum={diagnostics.DdgiForwardEstimateSampledIrradianceLuminance:F4}, ddgiDiffuseLum={diagnostics.DdgiForwardEstimateRawDiffuseLuminance:F4}, " +
            $"hybridFinalLum={diagnostics.DdgiForwardEstimateFinalDiffuseLuminance:F4}, fallbackWeight={diagnostics.DdgiForwardEstimateEnvironmentFallbackWeight:F3}, " +
            $"sampledProbes currentFrustum/sideRear/staleAge={diagnostics.DdgiSampledProbeCurrentFrustumCount}/{diagnostics.DdgiSampledProbeSideRearCount}/{diagnostics.DdgiSampledProbeStaleAgeCount}");
        Console.WriteLine(
            $"{prefix}: probeHitShadows rays/occluded/near(<spacing)/avgCommittedDistance=" +
            $"{diagnostics.DdgiShadowVisibilityRayCount}/{diagnostics.DdgiShadowVisibilityOccludedCount}/{diagnostics.DdgiShadowVisibilityNearHitCount}/{diagnostics.DdgiShadowVisibilityCommittedHitDistanceAverage:F3}");
        Console.WriteLine(
            $"{prefix}: visibilityMoments samples={diagnostics.DdgiVisibilityMomentSampleCount}, mean/variance/distance={diagnostics.DdgiVisibilityMomentMeanAverage:F3}/{diagnostics.DdgiVisibilityMomentVarianceAverage:F3}/{diagnostics.DdgiVisibilityProbeDistanceAverage:F3}, " +
            $"largeMargin={diagnostics.DdgiVisibilityLargeDistanceMarginCount}, zeroTransport={diagnostics.DdgiVisibilityZeroTransportCount}, zeroTransportWithIrradiance={diagnostics.DdgiVisibilityZeroTransportWithIrradianceCount}");
        if (diagnostics.DdgiDiagnosticWarnings.Count > 0)
            Console.WriteLine($"{prefix}: warnings={string.Join("; ", diagnostics.DdgiDiagnosticWarnings)}");
        Console.WriteLine(
            $"{prefix}: ddgiLightMode={diagnostics.DdgiLightSelectionMode}, selectedDirHits={diagnostics.DdgiSelectedDirectionalHitCount}, " +
            $"selectedLocalHits={diagnostics.DdgiSelectedLocalHitCount}, visibilityRays={diagnostics.DdgiVisibilityRayCount}, skippedLocalHits={diagnostics.DdgiSkippedLocalLightCount}, " +
            $"emissiveSources={diagnostics.DdgiEmissiveSourceCount}, emissiveRevision={diagnostics.DdgiEmissiveSourceRevision}");
        Console.WriteLine(
            $"{prefix}: updates new/dirty/frustum/safety/age={diagnostics.DdgiNewProbeCount}/{diagnostics.DdgiDirtyBoundsProbeUpdateCount}/" +
            $"{diagnostics.DdgiVisibleFrustumProbeUpdateCount}/{diagnostics.DdgiOutsideFrustumSafetyProbeUpdateCount}/{diagnostics.DdgiAgeRefreshProbeUpdateCount}, " +
            $"frustum={diagnostics.DdgiFrustumUpdatePercentage:F1}%, outside={diagnostics.DdgiOutsideFrustumUpdatePercentage:F1}%, stale={diagnostics.DdgiStaleProbeCount}, " +
            $"avgAge={diagnostics.DdgiAverageProbeAge:F1}, maxAge={diagnostics.DdgiMaxProbeAge}, scrolls={diagnostics.DdgiScrollCount}, movement={diagnostics.DdgiCameraMovementClass}");
        Console.WriteLine(
            $"{prefix}: adaptive scale={diagnostics.DdgiAdaptiveBudgetScale:F2}, reduced={diagnostics.DdgiAdaptiveBudgetReduced}, " +
            $"emergency={diagnostics.DdgiEmergencyDegradeActive}, reason='{diagnostics.DdgiAdaptiveBudgetReason}', " +
            $"reinit={diagnostics.DdgiResourceReinitializationCount}/{diagnostics.DdgiTotalResourceReinitializationCount}, cacheClear='{diagnostics.DdgiCacheClearReason}', " +
            $"localSlots={diagnostics.DdgiActiveLocalSlotCount}, localGen={diagnostics.DdgiLocalSlotGeneration}, eviction='{diagnostics.DdgiLocalVolumeEvictionReason}', " +
            $"shadedLights={diagnostics.DdgiEffectiveMaxShadedLights}");
        Console.WriteLine(
            $"{prefix}: memory currentAtlas={currentAtlasBytes}/{diagnostics.DdgiAtlasMemoryBudgetBytes}, rayScratch={diagnostics.DdgiRayScratchBytes}, updatedAtlas={diagnostics.DdgiUpdatedAtlasBytes}, latencyFrames={diagnostics.DdgiPublishedCacheLatencyFrames}, " +
            $"cacheGen={diagnostics.DdgiCacheGeneration}, cacheFrame={diagnostics.DdgiLastUpdatedFrameSerial}, cacheWarmup={diagnostics.DdgiCacheWarmupState}, " +
            $"updateExec={diagnostics.DdgiUpdateExecuted}:'{diagnostics.DdgiUpdateSkipReason}', publishExec={diagnostics.DdgiPublishExecuted}:'{diagnostics.DdgiPublishSkipReason}', " +
            $"probeVolume={diagnostics.DdgiProbeVolumeBufferBytes}, probeState={diagnostics.DdgiProbeStateBufferBytes}, updateQueue={diagnostics.DdgiProbeUpdateQueueBytes}, relocationClassify={diagnostics.DdgiProbeRelocationClassificationBytes}, " +
            $"scheduler={diagnostics.DdgiGpuSchedulerBufferBytes}, gatherTiles={diagnostics.DdgiGatherTileBufferBytes}, localPool={diagnostics.DdgiLocalSlotReservedPoolBytes}, localSlotInit={diagnostics.DdgiLocalSlotInitBytes}, " +
            $"ddgiTex={diagnostics.DdgiTextureBytes}, ddgiBuf={diagnostics.DdgiBufferBytes}, ssgiTargets={diagnostics.SsgiRenderTargetBytes}, " +
            $"giTargets={diagnostics.GlobalIlluminationRenderTargetBytes}, as={diagnostics.AccelerationStructureBytes}, asScratch={diagnostics.AccelerationStructureScratchBytes}");
        Console.WriteLine(
            $"{prefix}: AS blas/tlas/instances={diagnostics.AccelerationStructureBottomLevelCount}/{diagnostics.AccelerationStructureTlasBuildCount}/{diagnostics.AccelerationStructureTopLevelInstanceCount}, " +
            $"blasBuilds={diagnostics.AccelerationStructureBlasBuildCount}, tlasUpdates={diagnostics.AccelerationStructureTlasUpdateCount}, tlasSkips={diagnostics.AccelerationStructureTlasSkipCount}, " +
            $"blasCompact=query:{diagnostics.AccelerationStructureBlasCompactionQueryCount},copy:{diagnostics.AccelerationStructureBlasCompactionCount},pending:{diagnostics.AccelerationStructureBlasCompactionPendingCount}," +
            $"savedFrame:{diagnostics.AccelerationStructureBlasCompactionBytesSaved},savedResident:{diagnostics.AccelerationStructureBlasCompactedResidentBytesSaved}," +
            $"queryOverflow:{diagnostics.AccelerationStructureBlasCompactionQueryOverflowCount},readbackFailure:{diagnostics.AccelerationStructureBlasCompactionQueryReadbackFailureCount}, " +
            $"fallback='{diagnostics.AccelerationStructureFallbackReason}'");
        Console.WriteLine(
            $"{prefix}: cpuUs ssgi/ddgi/as={diagnostics.CpuSsgiRecordMicroseconds}/{diagnostics.CpuDdgiRecordMicroseconds}/{diagnostics.CpuAccelerationStructureBuildMicroseconds}, " +
            $"gpuUs ssgiTrace/ssgiTemporal/ssgiDenoise/ddgiTrace/ddgiBlend/ddgiRelocateClassify/ddgiPublish/ddgiTotal/composite={diagnostics.GpuSsgiTraceMicroseconds}/{diagnostics.GpuSsgiTemporalMicroseconds}/" +
            $"{diagnostics.GpuSsgiDenoiseMicroseconds}/{diagnostics.GpuDdgiTraceMicroseconds}/{diagnostics.GpuDdgiBlendMicroseconds}/{diagnostics.GpuDdgiRelocateClassifyMicroseconds}/{diagnostics.GpuDdgiPublishMicroseconds}/{diagnostics.GpuDdgiUpdateMicroseconds}/{diagnostics.GpuGiCompositeMicroseconds}, " +
            $"gpuAS blas/tlas={diagnostics.GpuAccelerationStructureBlasMicroseconds}/{diagnostics.GpuAccelerationStructureTlasMicroseconds}");
    }

    private void CycleDdgiDebugView()
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        ConfigureDdgiOnly(gi);
        gi.DebugView = NextDdgiDebugView(gi.DebugView);
        PrintGlobalIlluminationSettings("DDGI debug");
        PrintDdgiDebugLegend(gi.DebugView);
    }

    private void CycleDdgiInvestigationView()
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        ConfigureDdgiOnly(gi);
        gi.DebugView = NextDdgiInvestigationDebugView(gi.DebugView);
        PrintGlobalIlluminationSettings("DDGI investigation debug");
        PrintDdgiDebugLegend(gi.DebugView);
    }

    private void ResetNormalRenderView()
    {
        if (_renderer == null)
            return;

        if (_restoreSceneRenderSettings != null)
            _restoreSceneRenderSettings();
        else
            ApplyQualityPreset(RenderQualityPreset.DdgiHigh);

        _renderer.Settings.ResetRenderViewOverrides();
        _renderer.EnableMeshletDebugView = false;
        _renderer.DebugDraw.Enabled = false;
        _savedShadowToggleState = null;
        PrintGlobalIlluminationSettings("Normal render view");
    }

    private void ToggleDdgiProbeL1Metadata()
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        gi.DdgiProbeL1MetadataEnabled = !gi.DdgiProbeL1MetadataEnabled;
        PrintGlobalIlluminationSettings("DDGI L1 metadata");
    }

    private void CycleDdgiQualityTier()
    {
        if (_renderer == null)
            return;

        GlobalIlluminationSettings gi = _renderer.Settings.GlobalIllumination;
        DdgiQualityTier[] tiers = Enum.GetValues<DdgiQualityTier>();
        int index = Array.IndexOf(tiers, gi.DdgiQualityTier);
        index = index < 0 ? 0 : (index + 1) % tiers.Length;
        gi.ApplyDdgiQualityTier(tiers[index]);
        ConfigureDdgiOnly(gi);
        PrintGlobalIlluminationSettings("DDGI tier");
    }

    private static void ConfigureDdgiOnly(GlobalIlluminationSettings gi)
    {
        gi.Enabled = true;
        gi.Mode = GlobalIlluminationMode.Ddgi;
        gi.UseSsgi = false;
        gi.UseDdgi = true;
        gi.DdgiCameraRelativeEnabled = true;
        gi.DdgiProbeClassificationEnabled = true;
        gi.DdgiProbeRelocationEnabled = true;
    }

    private void ApplyDdgiDiagnosticsCounterState(SampleDiagnosticsFilter filter)
    {
        if (_renderer == null)
            return;

        RenderDiagnosticsSettings diagnostics = _renderer.Settings.Diagnostics;
        if (filter == SampleDiagnosticsFilter.DdgiOnly)
        {
            if (!_hasSavedDdgiForwardEstimateCounterState)
            {
                _savedDdgiForwardEstimateCountersEnabled = diagnostics.DdgiForwardEstimateCountersEnabled;
                _hasSavedDdgiForwardEstimateCounterState = true;
            }

            diagnostics.DdgiForwardEstimateCountersEnabled = true;
            Console.WriteLine("DDGI forward estimate counters: enabled for DDGI-only diagnostics.");
            return;
        }

        if (_hasSavedDdgiForwardEstimateCounterState)
        {
            diagnostics.DdgiForwardEstimateCountersEnabled = _savedDdgiForwardEstimateCountersEnabled;
            _hasSavedDdgiForwardEstimateCounterState = false;
            Console.WriteLine($"DDGI forward estimate counters: restored to {diagnostics.DdgiForwardEstimateCountersEnabled}.");
        }
    }

    private static GlobalIlluminationMode NextGlobalIlluminationMode(GlobalIlluminationMode mode)
    {
        return mode switch
        {
            GlobalIlluminationMode.Disabled => GlobalIlluminationMode.Ssgi,
            GlobalIlluminationMode.Ssgi => GlobalIlluminationMode.Ddgi,
            GlobalIlluminationMode.Ddgi => GlobalIlluminationMode.Hybrid,
            GlobalIlluminationMode.Hybrid => GlobalIlluminationMode.RayQueryHybrid,
            _ => GlobalIlluminationMode.Disabled
        };
    }

    private static bool ModeUsesDdgi(GlobalIlluminationMode mode)
    {
        return mode is GlobalIlluminationMode.Ddgi
            or GlobalIlluminationMode.Hybrid
            or GlobalIlluminationMode.RayQueryHybrid;
    }

    private static bool ModeUsesSsgi(GlobalIlluminationMode mode)
    {
        return mode is GlobalIlluminationMode.Ssgi
            or GlobalIlluminationMode.Hybrid
            or GlobalIlluminationMode.RayQueryHybrid;
    }

    internal static GlobalIlluminationDebugView NextGlobalIlluminationDebugView(GlobalIlluminationDebugView mode)
    {
        return mode switch
        {
            GlobalIlluminationDebugView.None => GlobalIlluminationDebugView.FinalIndirect,
            GlobalIlluminationDebugView.FinalIndirect => GlobalIlluminationDebugView.SsgiRaw,
            GlobalIlluminationDebugView.SsgiRaw => GlobalIlluminationDebugView.SsgiFiltered,
            GlobalIlluminationDebugView.SsgiFiltered => GlobalIlluminationDebugView.SsgiHistory,
            GlobalIlluminationDebugView.SsgiHistory => GlobalIlluminationDebugView.SsgiRayHitMask,
            GlobalIlluminationDebugView.SsgiRayHitMask => GlobalIlluminationDebugView.SsgiHistoryRejection,
            GlobalIlluminationDebugView.SsgiHistoryRejection => GlobalIlluminationDebugView.DdgiIrradiance,
            GlobalIlluminationDebugView.DdgiIrradiance => GlobalIlluminationDebugView.DdgiSourceCacheRadiance,
            GlobalIlluminationDebugView.DdgiSourceCacheRadiance => GlobalIlluminationDebugView.DdgiSampledIrradiance,
            GlobalIlluminationDebugView.DdgiSampledIrradiance => GlobalIlluminationDebugView.DdgiFinalDiffuse,
            GlobalIlluminationDebugView.DdgiFinalDiffuse => GlobalIlluminationDebugView.DdgiRawDiffuse,
            GlobalIlluminationDebugView.DdgiRawDiffuse => GlobalIlluminationDebugView.DdgiConfidenceBypass,
            GlobalIlluminationDebugView.DdgiConfidenceBypass => GlobalIlluminationDebugView.DdgiSuppressionMask,
            GlobalIlluminationDebugView.DdgiSuppressionMask => GlobalIlluminationDebugView.DdgiEffectiveWeight,
            GlobalIlluminationDebugView.DdgiEffectiveWeight => GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight,
            GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight => GlobalIlluminationDebugView.DdgiVisibility,
            GlobalIlluminationDebugView.DdgiVisibility => GlobalIlluminationDebugView.DdgiVisibilityMoments,
            GlobalIlluminationDebugView.DdgiVisibilityMoments => GlobalIlluminationDebugView.DdgiProbeIndex,
            GlobalIlluminationDebugView.DdgiProbeIndex => GlobalIlluminationDebugView.DdgiProbeState,
            GlobalIlluminationDebugView.DdgiProbeState => GlobalIlluminationDebugView.DdgiProbeRelocation,
            GlobalIlluminationDebugView.DdgiProbeRelocation => GlobalIlluminationDebugView.DdgiRelocationNormalized,
            GlobalIlluminationDebugView.DdgiRelocationNormalized => GlobalIlluminationDebugView.DdgiProbeLogicalPosition,
            GlobalIlluminationDebugView.DdgiProbeLogicalPosition => GlobalIlluminationDebugView.DdgiProbeRelocatedPosition,
            GlobalIlluminationDebugView.DdgiProbeRelocatedPosition => GlobalIlluminationDebugView.DdgiProbeRelocationDirection,
            GlobalIlluminationDebugView.DdgiProbeRelocationDirection => GlobalIlluminationDebugView.DdgiClassificationInvalidScore,
            GlobalIlluminationDebugView.DdgiClassificationInvalidScore => GlobalIlluminationDebugView.DdgiLeakClamp,
            GlobalIlluminationDebugView.DdgiLeakClamp => GlobalIlluminationDebugView.DdgiCoverage,
            GlobalIlluminationDebugView.DdgiCoverage => GlobalIlluminationDebugView.DdgiSpatialCoverage,
            GlobalIlluminationDebugView.DdgiSpatialCoverage => GlobalIlluminationDebugView.DdgiSupportCoverage,
            GlobalIlluminationDebugView.DdgiSupportCoverage => GlobalIlluminationDebugView.DdgiDataConfidence,
            GlobalIlluminationDebugView.DdgiDataConfidence => GlobalIlluminationDebugView.DdgiDirectionalSupport,
            GlobalIlluminationDebugView.DdgiDirectionalSupport => GlobalIlluminationDebugView.DdgiVisibilityConfidence,
            GlobalIlluminationDebugView.DdgiVisibilityConfidence => GlobalIlluminationDebugView.DdgiConfidenceChain,
            GlobalIlluminationDebugView.DdgiConfidenceChain => GlobalIlluminationDebugView.DdgiCascadeSelection,
            GlobalIlluminationDebugView.DdgiCascadeSelection => GlobalIlluminationDebugView.DdgiCascadeBlendWeight,
            GlobalIlluminationDebugView.DdgiCascadeBlendWeight => GlobalIlluminationDebugView.DdgiUpdateReasons,
            GlobalIlluminationDebugView.DdgiUpdateReasons => GlobalIlluminationDebugView.DdgiRayBudget,
            GlobalIlluminationDebugView.DdgiRayBudget => GlobalIlluminationDebugView.DdgiGatherLocalVolume,
            GlobalIlluminationDebugView.DdgiGatherLocalVolume => GlobalIlluminationDebugView.DdgiGatherClipmap,
            GlobalIlluminationDebugView.DdgiGatherClipmap => GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight,
            GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight => GlobalIlluminationDebugView.DdgiGatherBlendWeight,
            GlobalIlluminationDebugView.DdgiGatherBlendWeight => GlobalIlluminationDebugView.DdgiGatherFallback,
            GlobalIlluminationDebugView.DdgiGatherFallback => GlobalIlluminationDebugView.RayQueryCost,
            GlobalIlluminationDebugView.RayQueryCost => GlobalIlluminationDebugView.FarFieldOccupancySlice,
            GlobalIlluminationDebugView.FarFieldOccupancySlice => GlobalIlluminationDebugView.FarFieldTraceResult,
            GlobalIlluminationDebugView.FarFieldTraceResult => GlobalIlluminationDebugView.FarFieldSkyVisibility,
            GlobalIlluminationDebugView.FarFieldSkyVisibility => GlobalIlluminationDebugView.FarFieldSunShadow,
            GlobalIlluminationDebugView.FarFieldSunShadow => GlobalIlluminationDebugView.MaterialTransportSourceOwnership,
            GlobalIlluminationDebugView.MaterialTransportSourceOwnership => GlobalIlluminationDebugView.HybridEstimatorOwnership,
            GlobalIlluminationDebugView.HybridEstimatorOwnership => GlobalIlluminationDebugView.HybridFinalComposition,
            GlobalIlluminationDebugView.HybridFinalComposition => GlobalIlluminationDebugView.MaterialTransportHitProvenance,
            GlobalIlluminationDebugView.MaterialTransportHitProvenance => GlobalIlluminationDebugView.None,
            _ => GlobalIlluminationDebugView.None
        };
    }

    private static GlobalIlluminationDebugView NextDdgiDebugView(GlobalIlluminationDebugView mode)
    {
        return mode switch
        {
            GlobalIlluminationDebugView.None => GlobalIlluminationDebugView.FinalIndirect,
            GlobalIlluminationDebugView.FinalIndirect => GlobalIlluminationDebugView.DdgiIrradiance,
            GlobalIlluminationDebugView.DdgiIrradiance => GlobalIlluminationDebugView.DdgiSourceCacheRadiance,
            GlobalIlluminationDebugView.DdgiSourceCacheRadiance => GlobalIlluminationDebugView.DdgiSampledIrradiance,
            GlobalIlluminationDebugView.DdgiSampledIrradiance => GlobalIlluminationDebugView.DdgiFinalDiffuse,
            GlobalIlluminationDebugView.DdgiFinalDiffuse => GlobalIlluminationDebugView.DdgiRawDiffuse,
            GlobalIlluminationDebugView.DdgiRawDiffuse => GlobalIlluminationDebugView.DdgiConfidenceBypass,
            GlobalIlluminationDebugView.DdgiConfidenceBypass => GlobalIlluminationDebugView.DdgiSuppressionMask,
            GlobalIlluminationDebugView.DdgiSuppressionMask => GlobalIlluminationDebugView.DdgiEffectiveWeight,
            GlobalIlluminationDebugView.DdgiEffectiveWeight => GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight,
            GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight => GlobalIlluminationDebugView.DdgiVisibility,
            GlobalIlluminationDebugView.DdgiVisibility => GlobalIlluminationDebugView.DdgiVisibilityMoments,
            GlobalIlluminationDebugView.DdgiVisibilityMoments => GlobalIlluminationDebugView.DdgiProbeIndex,
            GlobalIlluminationDebugView.DdgiProbeIndex => GlobalIlluminationDebugView.DdgiProbeState,
            GlobalIlluminationDebugView.DdgiProbeState => GlobalIlluminationDebugView.DdgiProbeRelocation,
            GlobalIlluminationDebugView.DdgiProbeRelocation => GlobalIlluminationDebugView.DdgiRelocationNormalized,
            GlobalIlluminationDebugView.DdgiRelocationNormalized => GlobalIlluminationDebugView.DdgiProbeLogicalPosition,
            GlobalIlluminationDebugView.DdgiProbeLogicalPosition => GlobalIlluminationDebugView.DdgiProbeRelocatedPosition,
            GlobalIlluminationDebugView.DdgiProbeRelocatedPosition => GlobalIlluminationDebugView.DdgiProbeRelocationDirection,
            GlobalIlluminationDebugView.DdgiProbeRelocationDirection => GlobalIlluminationDebugView.DdgiClassificationInvalidScore,
            GlobalIlluminationDebugView.DdgiClassificationInvalidScore => GlobalIlluminationDebugView.DdgiLeakClamp,
            GlobalIlluminationDebugView.DdgiLeakClamp => GlobalIlluminationDebugView.DdgiCoverage,
            GlobalIlluminationDebugView.DdgiCoverage => GlobalIlluminationDebugView.DdgiSpatialCoverage,
            GlobalIlluminationDebugView.DdgiSpatialCoverage => GlobalIlluminationDebugView.DdgiSupportCoverage,
            GlobalIlluminationDebugView.DdgiSupportCoverage => GlobalIlluminationDebugView.DdgiDataConfidence,
            GlobalIlluminationDebugView.DdgiDataConfidence => GlobalIlluminationDebugView.DdgiDirectionalSupport,
            GlobalIlluminationDebugView.DdgiDirectionalSupport => GlobalIlluminationDebugView.DdgiVisibilityConfidence,
            GlobalIlluminationDebugView.DdgiVisibilityConfidence => GlobalIlluminationDebugView.DdgiConfidenceChain,
            GlobalIlluminationDebugView.DdgiConfidenceChain => GlobalIlluminationDebugView.DdgiCascadeSelection,
            GlobalIlluminationDebugView.DdgiCascadeSelection => GlobalIlluminationDebugView.DdgiCascadeBlendWeight,
            GlobalIlluminationDebugView.DdgiCascadeBlendWeight => GlobalIlluminationDebugView.DdgiUpdateReasons,
            GlobalIlluminationDebugView.DdgiUpdateReasons => GlobalIlluminationDebugView.DdgiRayBudget,
            GlobalIlluminationDebugView.DdgiRayBudget => GlobalIlluminationDebugView.DdgiGatherLocalVolume,
            GlobalIlluminationDebugView.DdgiGatherLocalVolume => GlobalIlluminationDebugView.DdgiGatherClipmap,
            GlobalIlluminationDebugView.DdgiGatherClipmap => GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight,
            GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight => GlobalIlluminationDebugView.DdgiGatherBlendWeight,
            GlobalIlluminationDebugView.DdgiGatherBlendWeight => GlobalIlluminationDebugView.DdgiGatherFallback,
            GlobalIlluminationDebugView.DdgiGatherFallback => GlobalIlluminationDebugView.FinalIndirect,
            _ => GlobalIlluminationDebugView.None
        };
    }

    private static GlobalIlluminationDebugView NextDdgiInvestigationDebugView(GlobalIlluminationDebugView mode)
    {
        return mode switch
        {
            GlobalIlluminationDebugView.DdgiGatherClipmap => GlobalIlluminationDebugView.DdgiGatherBlendWeight,
            GlobalIlluminationDebugView.DdgiGatherBlendWeight => GlobalIlluminationDebugView.DdgiGatherFallback,
            GlobalIlluminationDebugView.DdgiGatherFallback => GlobalIlluminationDebugView.DdgiSupportCoverage,
            GlobalIlluminationDebugView.DdgiSupportCoverage => GlobalIlluminationDebugView.DdgiDataConfidence,
            GlobalIlluminationDebugView.DdgiDataConfidence => GlobalIlluminationDebugView.DdgiDirectionalSupport,
            GlobalIlluminationDebugView.DdgiDirectionalSupport => GlobalIlluminationDebugView.DdgiConfidenceChain,
            GlobalIlluminationDebugView.DdgiConfidenceChain => GlobalIlluminationDebugView.DdgiIrradiance,
            GlobalIlluminationDebugView.DdgiIrradiance => GlobalIlluminationDebugView.DdgiSourceCacheRadiance,
            GlobalIlluminationDebugView.DdgiSourceCacheRadiance => GlobalIlluminationDebugView.DdgiSampledIrradiance,
            GlobalIlluminationDebugView.DdgiSampledIrradiance => GlobalIlluminationDebugView.DdgiFinalDiffuse,
            GlobalIlluminationDebugView.DdgiFinalDiffuse => GlobalIlluminationDebugView.DdgiRawDiffuse,
            GlobalIlluminationDebugView.DdgiRawDiffuse => GlobalIlluminationDebugView.DdgiConfidenceBypass,
            GlobalIlluminationDebugView.DdgiConfidenceBypass => GlobalIlluminationDebugView.DdgiProbeLogicalPosition,
            GlobalIlluminationDebugView.DdgiProbeLogicalPosition => GlobalIlluminationDebugView.DdgiUpdateReasons,
            GlobalIlluminationDebugView.DdgiUpdateReasons => GlobalIlluminationDebugView.DdgiGatherClipmap,
            _ => GlobalIlluminationDebugView.DdgiGatherClipmap
        };
    }

    private static GlobalIlluminationDebugView NextFocusedGlobalIlluminationDebugView(GlobalIlluminationDebugView mode)
    {
        return mode switch
        {
            GlobalIlluminationDebugView.FinalIndirect => GlobalIlluminationDebugView.DdgiIrradiance,
            GlobalIlluminationDebugView.DdgiIrradiance => GlobalIlluminationDebugView.DdgiSourceCacheRadiance,
            GlobalIlluminationDebugView.DdgiSourceCacheRadiance => GlobalIlluminationDebugView.DdgiSampledIrradiance,
            GlobalIlluminationDebugView.DdgiSampledIrradiance => GlobalIlluminationDebugView.DdgiFinalDiffuse,
            GlobalIlluminationDebugView.DdgiFinalDiffuse => GlobalIlluminationDebugView.DdgiRawDiffuse,
            GlobalIlluminationDebugView.DdgiRawDiffuse => GlobalIlluminationDebugView.DdgiConfidenceBypass,
            GlobalIlluminationDebugView.DdgiConfidenceBypass => GlobalIlluminationDebugView.DdgiSuppressionMask,
            GlobalIlluminationDebugView.DdgiSuppressionMask => GlobalIlluminationDebugView.DdgiEffectiveWeight,
            GlobalIlluminationDebugView.DdgiEffectiveWeight => GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight,
            GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight => GlobalIlluminationDebugView.DdgiVisibilityMoments,
            GlobalIlluminationDebugView.DdgiVisibilityMoments => GlobalIlluminationDebugView.DdgiCoverage,
            GlobalIlluminationDebugView.DdgiCoverage => GlobalIlluminationDebugView.DdgiSupportCoverage,
            GlobalIlluminationDebugView.DdgiSupportCoverage => GlobalIlluminationDebugView.DdgiDataConfidence,
            GlobalIlluminationDebugView.DdgiDataConfidence => GlobalIlluminationDebugView.DdgiDirectionalSupport,
            GlobalIlluminationDebugView.DdgiDirectionalSupport => GlobalIlluminationDebugView.DdgiVisibilityConfidence,
            GlobalIlluminationDebugView.DdgiVisibilityConfidence => GlobalIlluminationDebugView.DdgiConfidenceChain,
            GlobalIlluminationDebugView.DdgiConfidenceChain => GlobalIlluminationDebugView.DdgiProbeLogicalPosition,
            GlobalIlluminationDebugView.DdgiProbeLogicalPosition => GlobalIlluminationDebugView.DdgiProbeRelocatedPosition,
            GlobalIlluminationDebugView.DdgiProbeRelocatedPosition => GlobalIlluminationDebugView.DdgiProbeRelocationDirection,
            GlobalIlluminationDebugView.DdgiProbeRelocationDirection => GlobalIlluminationDebugView.DdgiClassificationInvalidScore,
            GlobalIlluminationDebugView.DdgiClassificationInvalidScore => GlobalIlluminationDebugView.DdgiUpdateReasons,
            GlobalIlluminationDebugView.DdgiUpdateReasons => GlobalIlluminationDebugView.FinalIndirect,
            _ => GlobalIlluminationDebugView.FinalIndirect
        };
    }

    private static bool IsDdgiDebugView(GlobalIlluminationDebugView view)
    {
        return view is GlobalIlluminationDebugView.DdgiIrradiance
            or GlobalIlluminationDebugView.DdgiVisibility
            or GlobalIlluminationDebugView.DdgiProbeIndex
            or GlobalIlluminationDebugView.DdgiProbeState
            or GlobalIlluminationDebugView.DdgiProbeRelocation
            or GlobalIlluminationDebugView.DdgiLeakClamp
            or GlobalIlluminationDebugView.DdgiCoverage
            or GlobalIlluminationDebugView.DdgiCascadeSelection
            or GlobalIlluminationDebugView.DdgiCascadeBlendWeight
            or GlobalIlluminationDebugView.DdgiUpdateReasons
            or GlobalIlluminationDebugView.DdgiRayBudget
            or GlobalIlluminationDebugView.DdgiGatherLocalVolume
            or GlobalIlluminationDebugView.DdgiGatherClipmap
            or GlobalIlluminationDebugView.DdgiGatherClipmapBlendWeight
            or GlobalIlluminationDebugView.DdgiGatherBlendWeight
            or GlobalIlluminationDebugView.DdgiGatherFallback
            or GlobalIlluminationDebugView.DdgiRawDiffuse
            or GlobalIlluminationDebugView.DdgiSuppressionMask
            or GlobalIlluminationDebugView.DdgiEffectiveWeight
            or GlobalIlluminationDebugView.DdgiEnvironmentFallbackWeight
            or GlobalIlluminationDebugView.DdgiRelocationNormalized
            or GlobalIlluminationDebugView.DdgiClassificationInvalidScore
            or GlobalIlluminationDebugView.DdgiVisibilityMoments
            or GlobalIlluminationDebugView.DdgiSpatialCoverage
            or GlobalIlluminationDebugView.DdgiSupportCoverage
            or GlobalIlluminationDebugView.DdgiDataConfidence
            or GlobalIlluminationDebugView.DdgiDirectionalSupport
            or GlobalIlluminationDebugView.DdgiSourceCacheRadiance
            or GlobalIlluminationDebugView.DdgiVisibilityConfidence
            or GlobalIlluminationDebugView.DdgiConfidenceChain
            or GlobalIlluminationDebugView.DdgiProbeLogicalPosition
            or GlobalIlluminationDebugView.DdgiProbeRelocatedPosition
            or GlobalIlluminationDebugView.DdgiProbeRelocationDirection
            or GlobalIlluminationDebugView.DdgiSampledIrradiance
            or GlobalIlluminationDebugView.DdgiFinalDiffuse
            or GlobalIlluminationDebugView.DdgiConfidenceBypass;
    }

    private static void PrintDdgiDebugLegend(GlobalIlluminationDebugView view)
    {
        if (IsDdgiDebugView(view))
            Console.WriteLine($"DDGI debug legend: {DescribeDdgiDebugView(view)}");
    }

    private static string DescribeDdgiDebugView(GlobalIlluminationDebugView view)
    {
        return view switch
        {
            GlobalIlluminationDebugView.DdgiSupportCoverage =>
                "cyan border; grayscale valid probe-data support. Black means no accepted active probes.",
            GlobalIlluminationDebugView.DdgiDataConfidence =>
                "blue border; grayscale valid probe-data availability. Black means no accepted data, not unfavorable geometry.",
            GlobalIlluminationDebugView.DdgiDirectionalSupport =>
                "blue border; grayscale geometric directional authority. Dark receivers may still have complete probe data.",
            GlobalIlluminationDebugView.DdgiConfidenceChain =>
                "blue border; RGB = data availability / directional authority / transport visibility.",
            GlobalIlluminationDebugView.DdgiSampledIrradiance =>
                "orange border; raw linear sampled DDGI irradiance before albedo and metallic. Low nonzero values can look black; use DdgiIrradiance for the log-normalized diagnostic.",
            GlobalIlluminationDebugView.DdgiSourceCacheRadiance =>
                "orange border; log-normalized direct/emissive/sky source cache before recursive bounce. Source colour here but not in DdgiIrradiance isolates transport.",
            GlobalIlluminationDebugView.DdgiFinalDiffuse =>
                "orange border; owned DDGI diffuse after albedo and metallic, before environment fallback.",
            GlobalIlluminationDebugView.DdgiConfidenceBypass =>
                "blue border; final DDGI with confidence suppression bypassed but visibility and leak attenuation retained.",
            GlobalIlluminationDebugView.DdgiSuppressionMask =>
                "cyan border; structured RGB = valid / directional / visibility support.",
            GlobalIlluminationDebugView.DdgiGatherClipmap =>
                "magenta border; hashed color = selected primary clipmap volume.",
            GlobalIlluminationDebugView.DdgiGatherBlendWeight =>
                "magenta border; grayscale coarse-ring contribution share (secondary volume). Magenta means tile read failed.",
            GlobalIlluminationDebugView.DdgiGatherFallback =>
                "magenta border; red = fallback, green = fast gather.",
            GlobalIlluminationDebugView.DdgiProbeLogicalPosition =>
                "yellow border; repeated world-position bands. Useful to spot wrong clipmap addressing.",
            _ => "DDGI debug view; border/badge encodes view category and id."
        };
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

    private void RequestDiagnosticSnapshot()
    {
        if (_renderer == null)
            return;

        _renderer.Settings.Debug.Enabled = true;
        _renderer.Settings.Debug.CpuSnapshotsEnabled = true;

        string directory = Path.Combine(AppContext.BaseDirectory, "DiagnosticSnapshots");
        Directory.CreateDirectory(directory);
        string diagnosticsPath = ExportPerformanceSnapshotFile(directory, "Diagnostic output");
        string screenshotPath = Path.ChangeExtension(diagnosticsPath, ".png");
        _requestDiagnosticScreenshotCapture?.Invoke(screenshotPath);

        Console.WriteLine(
            $"Diagnostic snapshot requested: cpuSnapshots=on, currentObjects={_renderer.DebugObjectSnapshotCount}, " +
            $"diagnostics={diagnosticsPath}, screenshot={screenshotPath}. " +
            "The refreshed CPU object snapshot is available after the next rendered frame.");
    }

    private string? CreateScreenshotOutputPath()
    {
        if (_renderer == null)
            return null;

        GlobalIlluminationDebugView giDebugView = _renderer.Settings.GlobalIllumination.DebugView;
        SampleDiagnosticsFilter filter = _getDiagnosticsFilter?.Invoke() ?? SampleDiagnosticsFilter.FullFrame;
        string suffix = CreateScreenshotFileNameSuffix(giDebugView, filter);
        if (string.IsNullOrEmpty(suffix))
            return null;

        ScreenshotRequest defaultRequest = ScreenshotRequest.CreateDefault();
        string? directory = Path.GetDirectoryName(defaultRequest.OutputPath);
        string baseFileName = Path.GetFileNameWithoutExtension(defaultRequest.OutputPath);
        return Path.Combine(directory ?? AppContext.BaseDirectory, $"{baseFileName}{suffix}.png");
    }

    private static string CreateScreenshotFileNameSuffix(
        GlobalIlluminationDebugView giDebugView,
        SampleDiagnosticsFilter filter)
    {
        if (giDebugView == GlobalIlluminationDebugView.None &&
            filter == SampleDiagnosticsFilter.FullFrame)
            return string.Empty;

        string giSegment = $"-gi-{SanitizeFileNameSegment(giDebugView.ToString())}";
        string filterSegment = filter == SampleDiagnosticsFilter.DdgiOnly
            ? "-ddgi-filter"
            : "-full-frame-filter";

        return giSegment + filterSegment;
    }

    private static string SanitizeFileNameSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "None";

        char[] invalidChars = Path.GetInvalidFileNameChars();
        Span<char> buffer = value.Length <= 256
            ? stackalloc char[value.Length]
            : new char[value.Length];

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            buffer[i] = invalidChars.Contains(c) ? '-' : c;
        }

        return new string(buffer);
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

        if (_runtimeBenchmarkCapture != null)
        {
            Console.WriteLine("Runtime benchmark canceled: cycling performance scenario.");
            _runtimeBenchmarkCapture = null;
        }

        SamplePerformanceScenarioSummary summary = _performanceScenarioRunner.CycleNext();
        if (_renderer != null)
            _renderer.CaptureScenario = summary.Scenario.ToString();
        PrintPerformanceScenarioSummary(summary);
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
        if (_performanceScenarioRunner != null)
            SampleGlobalIlluminationValidation.ConfigureRenderSettings(settings, _performanceScenarioRunner.CurrentScenario);
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
            DebugOverlayMode.ReflectionProbeVolumes => DebugOverlayMode.DdgiProbeVolumes,
            DebugOverlayMode.DdgiProbeVolumes => DebugOverlayMode.DdgiProbeActivity,
            DebugOverlayMode.DdgiProbeActivity => DebugOverlayMode.DdgiUpdatedProbes,
            DebugOverlayMode.DdgiUpdatedProbes => DebugOverlayMode.DdgiProbeRelocation,
            DebugOverlayMode.DdgiProbeRelocation => DebugOverlayMode.DdgiProbeAge,
            DebugOverlayMode.DdgiProbeAge => DebugOverlayMode.DdgiPhysicalSlots,
            DebugOverlayMode.DdgiPhysicalSlots => DebugOverlayMode.DdgiCascadeBounds,
            DebugOverlayMode.DdgiCascadeBounds => DebugOverlayMode.DdgiNewlyExposedCells,
            DebugOverlayMode.DdgiNewlyExposedCells => DebugOverlayMode.DdgiFrustumPriority,
            DebugOverlayMode.DdgiFrustumPriority => DebugOverlayMode.DdgiSafetyRefresh,
            DebugOverlayMode.DdgiSafetyRefresh => DebugOverlayMode.DdgiCascadeBlend,
            DebugOverlayMode.DdgiCascadeBlend => DebugOverlayMode.DdgiUpdateReasons,
            DebugOverlayMode.DdgiUpdateReasons => DebugOverlayMode.DecalVolumes,
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

    internal static MaterialDebugView NextMaterialDebugView(MaterialDebugView mode)
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
            MaterialDebugView.Dispersion => MaterialDebugView.MaterialOcclusion,
            MaterialDebugView.MaterialOcclusion => MaterialDebugView.CanonicalDiffuseReflectance,
            MaterialDebugView.CanonicalDiffuseReflectance => MaterialDebugView.CompiledEmission,
            MaterialDebugView.CompiledEmission => MaterialDebugView.GeometricNormal,
            MaterialDebugView.GeometricNormal => MaterialDebugView.Opacity,
            MaterialDebugView.Opacity => MaterialDebugView.Sidedness,
            MaterialDebugView.Sidedness => MaterialDebugView.ShadingModel,
            MaterialDebugView.ShadingModel => MaterialDebugView.TransportProfile,
            MaterialDebugView.TransportProfile => MaterialDebugView.MaterialRevisions,
            MaterialDebugView.MaterialRevisions => MaterialDebugView.None,
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

    private sealed record ShadowToggleState(
        bool Directional,
        bool Spot,
        bool Point,
        bool FoliageCast,
        bool FoliageLocal,
        bool TransparentReceive);

    private sealed record SponzaGiCaptureRestoreState(
        bool GlobalIlluminationEnabled,
        GlobalIlluminationDebugView GlobalIlluminationDebugView,
        bool DebugEnabled,
        bool AllowScreenshots,
        bool CpuSnapshotsEnabled,
        bool ParticlesEnabled,
        bool AnimationEnabled,
        RenderFeatureIsolationMode FeatureIsolation,
        bool AllowGpuTiming,
        bool DdgiForwardEstimateCountersEnabled,
        float EnvironmentDiffuseIntensity,
        float EnvironmentSpecularIntensity,
        string CaptureScenario);

    private sealed record SponzaGiPendingRendererScreenshot(
        int ArtifactIndex,
        string Bookmark,
        string Output,
        string RelativePath,
        int StableObservationCount = 0);
}
