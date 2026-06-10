using System;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Input;
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
        CreateKeyboardAction(input, ToggleHiZ, Key.F6);
        CreateKeyboardAction(input, ToggleTransparent, Key.F7);
        CreateKeyboardAction(input, ToggleMeshletDebug, Key.F8);
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
}
