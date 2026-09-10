using Njulf.Core.Math;
using Silk.NET.Input;
using NativeCursorMode = Silk.NET.Input.CursorMode;

namespace Njulf.Input;

public sealed partial class InputManager
{
    private readonly List<IGamepad> _gamepads = [];
    private readonly HashSet<IInputDevice> _connected = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IInputDevice, Deadzone> _originalDeadZones = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IMouse, MouseMotionState> _mouseMotion = new(ReferenceEqualityComparer.Instance);
    private InputCursorMode _cursorMode;
    private sealed class MouseMotionState
    {
        internal Vector2 LastPosition, Pending, Published, PendingWheel, PublishedWheel;
        internal bool HasPosition;
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        EnsureUsable();
        if (connected) ConnectDevice(device);
        else
        {
            _connected.Remove(device);
            if (device is IMouse mouse && _mouseMotion.TryGetValue(mouse, out var motion))
            { motion.Pending = motion.Published = motion.PendingWheel = motion.PublishedWheel = default; motion.HasPosition = false; }
        }
    }

    private void ConnectDevice(IInputDevice device)
    {
        if (!_connected.Add(device)) return;
        switch (device)
        {
            case IKeyboard keyboard:
                if (_keyboards.Contains(keyboard)) break;
                _keyboards.Add(keyboard);
                keyboard.KeyDown += OnKeyDown; keyboard.KeyUp += OnKeyUp; keyboard.KeyChar += OnKeyChar;
                break;
            case IMouse mouse:
                if (!_mice.Contains(mouse))
                {
                    _mice.Add(mouse); _mouseMotion.Add(mouse, new());
                    mouse.MouseDown += OnMouseDown; mouse.MouseUp += OnMouseUp;
                    mouse.MouseMove += OnMouseMove; mouse.Scroll += OnMouseWheel;
                }
                if (_cursorMode != InputCursorMode.Normal) ApplyCursor(mouse, _focused ? _cursorMode : InputCursorMode.Normal);
                break;
            case IGamepad gamepad:
                if (!_gamepads.Contains(gamepad)) _gamepads.Add(gamepad);
                if (_originalDeadZones.TryAdd(gamepad, gamepad.Deadzone)) gamepad.Deadzone = new(0, DeadzoneMethod.Traditional);
                break;
            case IJoystick joystick:
                if (!_joysticks.Contains(joystick)) _joysticks.Add(joystick);
                if (_originalDeadZones.TryAdd(joystick, joystick.Deadzone)) joystick.Deadzone = new(0, DeadzoneMethod.Traditional);
                break;
        }
    }

    private void RestoreDeviceSettings()
    {
        foreach (var (device, deadzone) in _originalDeadZones)
        {
            if (device is IGamepad gamepad) gamepad.Deadzone = deadzone;
            else if (device is IJoystick joystick) joystick.Deadzone = deadzone;
        }
        _originalDeadZones.Clear();
        if (_cursorMode != InputCursorMode.Normal)
            foreach (var mouse in _mice)
                if (_connected.Contains(mouse)) ApplyCursor(mouse, InputCursorMode.Normal);
    }

    private T? Device<T>(List<T> devices, int index) where T : class, IInputDevice =>
        index < devices.Count && _connected.Contains(devices[index]) ? devices[index] : null;

    private void PublishMouseDeltas()
    {
        foreach (var motion in _mouseMotion.Values)
        {
            motion.Published = motion.Pending; motion.PublishedWheel = motion.PendingWheel;
            motion.Pending = motion.PendingWheel = default;
        }
    }

    private void ClearMotion()
    {
        _mouseDelta = default; _pendingScroll = _mouseScrollDelta = 0;
        foreach (var motion in _mouseMotion.Values)
        {
            motion.Pending = motion.Published = motion.PendingWheel = motion.PublishedWheel = default;
            motion.HasPosition = false;
        }
    }

    /// <inheritdoc />
    public InputCursorMode CursorMode { get { EnsureUsable(); return _cursorMode; } }
    /// <inheritdoc />
    public void SetCursorMode(InputCursorMode mode)
    {
        EnsureUsable(); BindingSpec.CodeOf(mode);
        if (!_isInitialized) Initialize();
        foreach (var mouse in _mice)
            if (_connected.Contains(mouse)) ApplyCursor(mouse, _focused ? mode : InputCursorMode.Normal);
        _cursorMode = mode; ClearMotion();
    }

    private static void ApplyCursor(IMouse mouse, InputCursorMode mode)
    {
        NativeCursorMode native = mode switch
        {
            InputCursorMode.Normal => NativeCursorMode.Normal,
            InputCursorMode.Hidden => NativeCursorMode.Hidden,
            _ => mouse.Cursor.IsSupported(NativeCursorMode.Raw) ? NativeCursorMode.Raw : NativeCursorMode.Disabled
        };
        if (!mouse.Cursor.IsSupported(native)) throw new NotSupportedException($"Cursor mode {mode} is unavailable.");
        mouse.Cursor.CursorMode = native;
    }

    /// <summary>Host focus hook. Suspends input and releases capture while unfocused; held actions must return to neutral.</summary>
    public void SetFocused(bool focused)
    {
        EnsureUsable();
        if (_focused == focused) return;
        _focused = focused;
        _rebind?.Cancel();
        GateHeldActions(); ClearMotion();
        foreach (var mouse in _mice)
            if (_connected.Contains(mouse)) ApplyCursor(mouse, focused ? _cursorMode : InputCursorMode.Normal);
    }

    private void GateHeldActions()
    {
        foreach (var action in _actions.Values)
            if (action.Mode == InputValueMode.State) action.WaitForNeutral = true;
    }

    private float ReadAxis(BindingSpec spec)
    {
        if (spec.Kind == BindingKind.JoystickAxis)
        {
            var joystick = Device(_joysticks, spec.DeviceIndex);
            return joystick != null && spec.Code < joystick.Axes.Count ? Finite(joystick.Axes[spec.Code].Position) : 0;
        }
        var pad = Device(_gamepads, spec.DeviceIndex);
        if (pad == null) return 0;
        if (spec.Code >= (int)GamepadAxis.LeftTrigger)
        {
            int index = spec.Code - (int)GamepadAxis.LeftTrigger;
            if (index >= pad.Triggers.Count) return 0;
            // Silk 2.23's GLFW backend forwards GLFW's signed trigger axes unchanged.
            float raw = pad.Triggers[index].Position;
            return float.IsFinite(raw) ? System.Math.Clamp((raw + 1) * .5f, 0, 1) : 0;
        }
        int stick = spec.Code / 2;
        if (stick >= pad.Thumbsticks.Count) return 0;
        return Finite(spec.Code % 2 == 0 ? pad.Thumbsticks[stick].X : -pad.Thumbsticks[stick].Y);
    }

    private static float Finite(float value) => float.IsFinite(value) ? value : 0;
    private static float DeadZone(float value, float zone) => MathF.CopySign(MathF.Max(0, System.Math.Clamp(MathF.Abs(value), 0, 1) - zone) / (1 - zone), value);
    private bool ReadButton(BindingSpec spec)
    {
        switch (spec.Kind)
        {
            case BindingKind.Key:
                return Device(_keyboards, spec.DeviceIndex)?.IsKeyPressed((Key)spec.Code) == true;
            case BindingKind.MouseButton:
                return Device(_mice, spec.DeviceIndex)?.IsButtonPressed((Silk.NET.Input.MouseButton)spec.Code) == true;
            case BindingKind.JoystickButton:
                var joystick = Device(_joysticks, spec.DeviceIndex);
                return joystick != null && spec.Code < joystick.Buttons.Count && joystick.Buttons[spec.Code].Pressed;
            case BindingKind.GamepadButton:
                var pad = Device(_gamepads, spec.DeviceIndex);
                if (pad != null)
                    for (int i = 0; i < pad.Buttons.Count; i++)
                    {
                        var button = pad.Buttons[i];
                        if (button.Name == NativeButton((GamepadButton)spec.Code)) return button.Pressed;
                    }
                return false;
            default:
                float value = ReadAxis(spec);
                return spec.IsNegative ? value < -.5f : value > .5f;
        }
    }

    private static ButtonName NativeButton(GamepadButton button) => button switch
    {
        GamepadButton.A => ButtonName.A, GamepadButton.B => ButtonName.B,
        GamepadButton.X => ButtonName.X, GamepadButton.Y => ButtonName.Y,
        GamepadButton.LeftBumper => ButtonName.LeftBumper, GamepadButton.RightBumper => ButtonName.RightBumper,
        GamepadButton.Back => ButtonName.Back, GamepadButton.Start => ButtonName.Start, GamepadButton.Home => ButtonName.Home,
        GamepadButton.LeftStick => ButtonName.LeftStick, GamepadButton.RightStick => ButtonName.RightStick,
        GamepadButton.DPadUp => ButtonName.DPadUp, GamepadButton.DPadRight => ButtonName.DPadRight,
        GamepadButton.DPadDown => ButtonName.DPadDown, GamepadButton.DPadLeft => ButtonName.DPadLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(button))
    };

    internal Vector2 Evaluate(BindingSpec spec, BindingShape shape)
    {
        if (shape == BindingShape.Button) return new(ReadButton(spec) ? 1 : 0, 0);
        Vector2 value;
        switch (spec.Kind)
        {
            case BindingKind.JoystickAxis:
            case BindingKind.GamepadAxis:
                value = new(DeadZone(ReadAxis(spec), spec.DeadZone), 0); break;
            case BindingKind.Digital:
                value = new((ReadButton(spec.Parts![1]) ? 1 : 0) - (ReadButton(spec.Parts[0]) ? 1 : 0), 0); break;
            case BindingKind.ButtonValue:
                value = new(ReadButton(spec.Parts![0]) ? 1 : 0, 0); break;
            case BindingKind.FourButtons:
                value = new((ReadButton(spec.Parts![1]) ? 1 : 0) - (ReadButton(spec.Parts[0]) ? 1 : 0),
                    (ReadButton(spec.Parts[3]) ? 1 : 0) - (ReadButton(spec.Parts[2]) ? 1 : 0)); break;
            case BindingKind.AxisPair:
                value = new(Evaluate(spec.Parts![0], BindingShape.Float).X, Evaluate(spec.Parts[1], BindingShape.Float).X); break;
            case BindingKind.GamepadStick:
                var pad = Device(_gamepads, spec.DeviceIndex);
                value = pad != null && spec.Code < pad.Thumbsticks.Count
                    ? new(Finite(pad.Thumbsticks[spec.Code].X), -Finite(pad.Thumbsticks[spec.Code].Y)) : default; break;
            case BindingKind.MouseAxis:
            case BindingKind.MouseMotion:
                var mouse = Device(_mice, spec.DeviceIndex);
                if (mouse == null) return default;
                var motion = _mouseMotion[mouse];
                value = spec.Kind == BindingKind.MouseMotion ? motion.Published : new(spec.Code switch
                {
                    (int)MouseAxis.X => motion.Published.X, (int)MouseAxis.Y => motion.Published.Y,
                    (int)MouseAxis.WheelX => motion.PublishedWheel.X, _ => motion.PublishedWheel.Y
                }, 0); break;
            default:
                value = new(ReadButton(spec) ? 1 : 0, 0); break;
        }
        if (spec.Mode == InputValueMode.State && shape == BindingShape.Vector2)
        {
            float length = value.Length();
            value = length > 0 ? value / length * DeadZone(length, spec.DeadZone) : default;
        }
        value *= spec.Scale;
        if (spec.Mode == InputValueMode.State)
        {
            if (shape == BindingShape.Float) value.X = System.Math.Clamp(value.X, -1, 1);
            else if (value.LengthSquared() > 1) value = value.Normalized();
        }
        return value;
    }
}
