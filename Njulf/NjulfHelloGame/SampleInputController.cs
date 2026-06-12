using System;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Input;
using Njulf.Rendering.Data;
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
    private const string CycleLightingMode = "cycle_lighting_mode";
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
    private const float CameraSpeed = 3.0f;
    private const float KeyboardLookSpeed = 1.75f;
    private const float MouseSensitivity = 0.0025f;
    private static readonly Vector3 FullModelPosition = new(0f, 5.5f, 18f);
    private const float FullModelYaw = 0f;
    private const float FullModelPitch = -0.22f;
    private static readonly Vector3 InteriorPosition = new(0f, 1.25f, 5.5f);
    private const float InteriorYaw = 0f;
    private const float InteriorPitch = -0.12f;

    private readonly FirstPersonCamera _camera;
    private readonly IInputManager _input;
    private readonly System.Action _exit;
    private readonly Njulf.Rendering.VulkanRenderer? _renderer;
    private readonly LightManager? _lightManager;
    private SampleLightingMode _lightingMode;
    private bool _fullModelPressed;
    private bool _interiorPressed;
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

    public SampleInputController(
        FirstPersonCamera camera,
        IInputManager input,
        System.Action exit,
        Njulf.Rendering.VulkanRenderer? renderer = null,
        LightManager? lightManager = null,
        SampleLightingMode lightingMode = SampleLightingMode.DirectionalKey)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _renderer = renderer;
        _lightManager = lightManager;
        _lightingMode = lightingMode;
    }

    public static void Configure(InputManager input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        CreateKeyboardAction(input, MoveForward, Key.W);
        CreateKeyboardAction(input, MoveBackward, Key.S);
        CreateKeyboardAction(input, MoveLeft, Key.A);
        CreateKeyboardAction(input, MoveRight, Key.D);
        CreateKeyboardAction(input, MoveUp, Key.E);
        CreateKeyboardAction(input, MoveDown, Key.Q);
        CreateKeyboardAction(input, LookLeft, Key.Left);
        CreateKeyboardAction(input, LookRight, Key.Right);
        CreateKeyboardAction(input, LookUp, Key.Up);
        CreateKeyboardAction(input, LookDown, Key.Down);
        CreateKeyboardAction(input, ExitGame, Key.Escape);
        CreateKeyboardAction(input, FullModelView, Key.Number1);
        CreateKeyboardAction(input, InteriorView, Key.Number2);
        CreateKeyboardAction(input, CycleToneMapper, Key.F4);
        CreateKeyboardAction(input, ToggleBloom, Key.F5);
        CreateKeyboardAction(input, ToggleShadows, Key.F1);
        CreateKeyboardAction(input, ToggleSpotShadows, Key.F12);
        CreateKeyboardAction(input, TogglePointShadows, Key.Number4);
        CreateKeyboardAction(input, ToggleAmbientOcclusion, Key.Number5);
        CreateKeyboardAction(input, CycleAmbientOcclusionDebug, Key.Number6);
        CreateKeyboardAction(input, CycleAntiAliasingMode, Key.Number7);
        CreateKeyboardAction(input, CycleAntiAliasingDebug, Key.Number8);
        CreateKeyboardAction(input, CycleShadowDebug, Key.F2);
        CreateKeyboardAction(input, CycleShadowCascadeCount, Key.F3);
        CreateKeyboardAction(input, CycleLightingMode, Key.Number3);
        CreateKeyboardAction(input, CycleBloomDebug, Key.F6);
        CreateKeyboardAction(input, CycleBloomDebugMip, Key.F7);
        CreateKeyboardAction(input, ToggleRawHdr, Key.F11);
        CreateKeyboardAction(input, ToggleHiZ, Key.F8);
        CreateKeyboardAction(input, ToggleTransparent, Key.F9);
        CreateKeyboardAction(input, ToggleMeshletDebug, Key.F10);
        CreateKeyboardAction(input, BloomIntensityDown, Key.PageDown);
        CreateKeyboardAction(input, BloomIntensityUp, Key.PageUp);
        CreateKeyboardAction(input, BloomThresholdDown, Key.End);
        CreateKeyboardAction(input, BloomThresholdUp, Key.Home);
        CreateKeyboardAction(input, BloomRadiusDown, Key.Delete);
        CreateKeyboardAction(input, BloomRadiusUp, Key.Insert);
        CreateKeyboardAction(input, ExposureDown, Key.LeftBracket);
        CreateKeyboardAction(input, ExposureUp, Key.RightBracket);
        CreateKeyboardAction(input, AmbientOcclusionRadiusDown, Key.J);
        CreateKeyboardAction(input, AmbientOcclusionRadiusUp, Key.U);
        CreateKeyboardAction(input, AmbientOcclusionIntensityDown, Key.M);
        CreateKeyboardAction(input, AmbientOcclusionIntensityUp, Key.I);
        CreateKeyboardAction(input, ShadowNormalBiasDown, Key.Comma);
        CreateKeyboardAction(input, ShadowNormalBiasUp, Key.Period);
        CreateKeyboardAction(input, SpotShadowBudgetDown, Key.Minus);
        CreateKeyboardAction(input, SpotShadowBudgetUp, Key.Equal);
        CreateKeyboardAction(input, PointShadowBudgetDown, Key.Semicolon);
        CreateKeyboardAction(input, PointShadowBudgetUp, Key.Apostrophe);
        CreateKeyboardAction(input, SpotShadowBiasDown, Key.K);
        CreateKeyboardAction(input, SpotShadowBiasUp, Key.L);
        CreateKeyboardAction(input, PointShadowBiasDown, Key.O);
        CreateKeyboardAction(input, PointShadowBiasUp, Key.P);
    }

    public void Update(float deltaTime, int viewportWidth, int viewportHeight)
    {
        if (_input.IsKeyDown(ExitGame))
            _exit();

        if (WasPressed(FullModelView, ref _fullModelPressed))
            MoveCamera(FullModelPosition, FullModelYaw, FullModelPitch);

        if (WasPressed(InteriorView, ref _interiorPressed))
            MoveCamera(InteriorPosition, InteriorYaw, InteriorPitch);

        if (_renderer != null && WasPressed(ToggleHiZ, ref _toggleHiZPressed))
        {
            _renderer.EnableHiZOcclusion = !_renderer.EnableHiZOcclusion;
            Console.WriteLine($"Hi-Z occlusion: {(_renderer.EnableHiZOcclusion ? "enabled" : "disabled")}");
        }

        if (_renderer != null && WasPressed(ToggleTransparent, ref _toggleTransparentPressed))
        {
            _renderer.EnableTransparentPass = !_renderer.EnableTransparentPass;
            Console.WriteLine($"Transparent pass: {(_renderer.EnableTransparentPass ? "enabled" : "disabled")}");
        }

        if (_renderer != null && WasPressed(ToggleMeshletDebug, ref _toggleMeshletDebugPressed))
        {
            _renderer.EnableMeshletDebugView = !_renderer.EnableMeshletDebugView;
            Console.WriteLine($"Meshlet debug view: {(_renderer.EnableMeshletDebugView ? "enabled" : "disabled")}");
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

        if (_lightManager != null && WasPressed(CycleLightingMode, ref _cycleLightingModePressed))
        {
            _lightingMode = _lightingMode switch
            {
                SampleLightingMode.DirectionalKey => SampleLightingMode.ThreePointDemo,
                SampleLightingMode.ThreePointDemo => SampleLightingMode.SpotShadowDemo,
                SampleLightingMode.SpotShadowDemo => SampleLightingMode.PointShadowDemo,
                _ => SampleLightingMode.DirectionalKey
            };
            SampleLighting.Configure(_lightManager, _lightingMode);
            Console.WriteLine($"Lighting mode: {_lightingMode}");
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

        if (_renderer != null && WasPressed(CycleAntiAliasingMode, ref _cycleAntiAliasingModePressed))
        {
            _renderer.Settings.AntiAliasing.Mode = _renderer.Settings.AntiAliasing.Mode switch
            {
                AntiAliasingMode.None => AntiAliasingMode.Fxaa,
                AntiAliasingMode.Fxaa => AntiAliasingMode.Smaa1x,
                AntiAliasingMode.Smaa1x => AntiAliasingMode.Smaa2x,
                AntiAliasingMode.Smaa2x => AntiAliasingMode.Smaa4x,
                AntiAliasingMode.Smaa4x => AntiAliasingMode.Smaa8x,
                AntiAliasingMode.Smaa8x => AntiAliasingMode.Smaa16x,
                AntiAliasingMode.Smaa16x => AntiAliasingMode.Taa,
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
                AntiAliasingDebugView.SmaaBlendWeights => AntiAliasingDebugView.JitterPattern,
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

        if (_renderer != null && WasPressed(ExposureDown, ref _exposureDownPressed))
            AdjustExposure(0.9f);

        if (_renderer != null && WasPressed(ExposureUp, ref _exposureUpPressed))
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

        if (_input.IsMouseButtonDown((int)MouseButton.Right))
        {
            Vector2 mouseDelta = _input.MouseDelta;
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
        bool pressed = currentState && !previousState;
        previousState = currentState;
        return pressed;
    }

    private void MoveCamera(Vector3 position, float yaw, float pitch)
    {
        _camera.Position = position;
        _camera.Yaw = yaw;
        _camera.Pitch = pitch;
        _camera.Update();
    }

    private void AdjustExposure(float multiplier)
    {
        if (_renderer == null)
            return;

        _renderer.Settings.Exposure *= multiplier;
        Console.WriteLine($"Exposure: {_renderer.Settings.Exposure:F2}");
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
        Console.WriteLine(
            $"{prefix}: {(shadows.DirectionalShadowsEnabled ? "enabled" : "disabled")}, " +
            $"map={shadows.DirectionalShadowMapSize}, cascades={shadows.DirectionalCascadeCount}, " +
            $"normalBias={shadows.NormalBias:F4}, slopeBias={shadows.SlopeScaledDepthBias:F2}, " +
            $"spot={(shadows.SpotShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedSpotLights}@{shadows.SpotShadowTileSize}, " +
            $"point={(shadows.PointShadowsEnabled ? "on" : "off")}:{shadows.MaxShadowedPointLights}@{shadows.PointShadowMapSize}, " +
            $"spotBias={shadows.SpotNormalBias:F4}, pointBias={shadows.PointNormalBias:F4}, debug={shadows.DebugView}");
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
            $"fxaaSubpixel={aa.FxaaSubpixelBlending:F2}, smaaThreshold={aa.SmaaThreshold:F3}, " +
            $"smaaSearch={aa.SmaaMaxSearchSteps}, smaaSamples={aa.EffectiveSmaaSampleCount}, " +
            $"jitter={(aa.JitterEnabled ? "on" : "off")}");
    }
}
