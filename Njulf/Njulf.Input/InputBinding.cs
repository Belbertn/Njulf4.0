using Silk.NET.Input;

namespace Njulf.Input;

/// <summary>The device family supplying a button binding.</summary>
public enum BindingDeviceType
{
    /// <summary>Keyboard physical binding code.</summary>
    Keyboard,
    /// <summary>Mouse physical binding code.</summary>
    Mouse,
    /// <summary>Joystick physical binding code.</summary>
    Joystick
}
/// <summary>Zero-based joystick button codes.</summary>
public enum JoystickButton
{
    /// <summary>Button0 physical binding code.</summary>
    Button0,
    /// <summary>Button1 physical binding code.</summary>
    Button1,
    /// <summary>Button2 physical binding code.</summary>
    Button2,
    /// <summary>Button3 physical binding code.</summary>
    Button3,
    /// <summary>Button4 physical binding code.</summary>
    Button4,
    /// <summary>Button5 physical binding code.</summary>
    Button5,
    /// <summary>Button6 physical binding code.</summary>
    Button6,
    /// <summary>Button7 physical binding code.</summary>
    Button7,
    /// <summary>Button8 physical binding code.</summary>
    Button8,
    /// <summary>Button9 physical binding code.</summary>
    Button9,
    /// <summary>Button10 physical binding code.</summary>
    Button10,
    /// <summary>Button11 physical binding code.</summary>
    Button11,
    /// <summary>Button12 physical binding code.</summary>
    Button12,
    /// <summary>Button13 physical binding code.</summary>
    Button13,
    /// <summary>Button14 physical binding code.</summary>
    Button14,
    /// <summary>Button15 physical binding code.</summary>
    Button15
}
/// <summary>Joystick axes available as thresholded button bindings.</summary>
public enum JoystickAxis
{
    /// <summary>X physical binding code.</summary>
    X,
    /// <summary>Y physical binding code.</summary>
    Y,
    /// <summary>Z physical binding code.</summary>
    Z,
    /// <summary>Rx physical binding code.</summary>
    Rx,
    /// <summary>Ry physical binding code.</summary>
    Ry,
    /// <summary>Rz physical binding code.</summary>
    Rz
}

/// <summary>An immutable physical binding for a button action.</summary>
/// <remarks>Device zero is the first device in its family. Unavailable devices are inactive.
/// A joystick axis acts as a button above +0.5, or below -0.5 for its negative direction.</remarks>
public sealed class InputBinding
{
    private readonly bool _joystickButton;
    /// <summary>Device family.</summary>
    public BindingDeviceType DeviceType { get; }
    /// <summary>Zero-based index within the device family.</summary>
    public int DeviceIndex { get; }
    /// <summary>Typed enum's numeric code; joystick buttons and axes are separate binding kinds.</summary>
    public int InputCode { get; }
    /// <summary>Whether the joystick axis uses the negative threshold.</summary>
    public bool IsNegative { get; }
    /// <summary>Binds a keyboard key on device zero by default.</summary>
    public InputBinding(InputKey key, int deviceIndex = 0)
        : this(BindingDeviceType.Keyboard, Validate(key), deviceIndex, false, false) { }
    /// <summary>Binds a mouse button on device zero by default.</summary>
    public InputBinding(MouseButton button, int deviceIndex = 0)
        : this(BindingDeviceType.Mouse, Validate(button), deviceIndex, false, false) { }
    /// <summary>Binds a joystick button on device zero by default.</summary>
    public InputBinding(JoystickButton button, int deviceIndex = 0)
        : this(BindingDeviceType.Joystick, Validate(button), deviceIndex, false, true) { }
    /// <summary>Binds one joystick axis direction using a strict ±0.5 threshold.</summary>
    public InputBinding(JoystickAxis axis, int deviceIndex = 0, bool isNegative = false)
        : this(BindingDeviceType.Joystick, Validate(axis), deviceIndex, isNegative, false) { }
    private InputBinding(BindingDeviceType type, int code, int index, bool negative, bool button)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        DeviceType = type; InputCode = code; DeviceIndex = index; IsNegative = negative; _joystickButton = button;
    }
    private static int Validate<T>(T code) where T : struct, Enum
    {
        int value = Convert.ToInt32(code);
        if (value < 0 || !Enum.IsDefined(code)) throw new ArgumentOutOfRangeException(nameof(code));
        return value;
    }
    internal bool IsActive(IReadOnlyList<IKeyboard> keyboards, IReadOnlyList<IMouse> mice, IReadOnlyList<IJoystick> joysticks)
    {
        if (DeviceType == BindingDeviceType.Keyboard)
            return DeviceIndex < keyboards.Count && keyboards[DeviceIndex].IsKeyPressed((Key)InputCode);
        if (DeviceType == BindingDeviceType.Mouse)
            return DeviceIndex < mice.Count && mice[DeviceIndex].IsButtonPressed((Silk.NET.Input.MouseButton)InputCode);
        if (DeviceIndex >= joysticks.Count) return false;
        var joystick = joysticks[DeviceIndex];
        if (_joystickButton) return InputCode < joystick.Buttons.Count && joystick.Buttons[InputCode].Pressed;
        if (InputCode >= joystick.Axes.Count) return false;
        float value = joystick.Axes[InputCode].Position;
        return IsNegative ? value < -0.5f : value > 0.5f;
    }
    /// <summary>Returns a diagnostic description including device, code and direction.</summary>
    public override string ToString() => $"{DeviceType}:{DeviceIndex}:{InputCode}{(_joystickButton ? "(button)" : IsNegative ? "(-)" : "")}";
}
