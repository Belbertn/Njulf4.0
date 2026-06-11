using System;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Input;
using Njulf.Rendering.Data;
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
    private bool _fullModelPressed;
    private bool _interiorPressed;
    private bool _toggleHiZPressed;
    private bool _toggleTransparentPressed;
    private bool _toggleMeshletDebugPressed;
    private bool _cycleToneMapperPressed;
    private bool _toggleRawHdrPressed;
    private bool _toggleBloomPressed;
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

    public SampleInputController(
        FirstPersonCamera camera,
        IInputManager input,
        System.Action exit,
        Njulf.Rendering.VulkanRenderer? renderer = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _exit = exit ?? throw new ArgumentNullException(nameof(exit));
        _renderer = renderer;
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
}
