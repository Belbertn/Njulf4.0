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
    Joystick,
    /// <summary>Standard gamepad controls.</summary>
    Gamepad
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
    internal BindingSpec Spec { get; }
    /// <summary>Device family.</summary>
    public BindingDeviceType DeviceType => Spec.Kind switch
    {
        BindingKind.Key => BindingDeviceType.Keyboard,
        BindingKind.MouseButton => BindingDeviceType.Mouse,
        BindingKind.GamepadButton or BindingKind.GamepadAxis => BindingDeviceType.Gamepad,
        _ => BindingDeviceType.Joystick
    };
    /// <summary>Zero-based index within the device family.</summary>
    public int DeviceIndex => Spec.DeviceIndex;
    /// <summary>Typed enum's numeric code; joystick buttons and axes are separate binding kinds.</summary>
    public int InputCode => Spec.Code;
    /// <summary>Whether the joystick axis uses the negative threshold.</summary>
    public bool IsNegative => Spec.IsNegative;
    /// <summary>Binds a keyboard key on device zero by default.</summary>
    public InputBinding(InputKey key, int deviceIndex = 0)
        : this(new(BindingKind.Key, BindingSpec.CodeOf(key), deviceIndex)) { }
    /// <summary>Binds a mouse button on device zero by default.</summary>
    public InputBinding(MouseButton button, int deviceIndex = 0)
        : this(new(BindingKind.MouseButton, BindingSpec.CodeOf(button), deviceIndex)) { }
    /// <summary>Binds a joystick button on device zero by default.</summary>
    public InputBinding(JoystickButton button, int deviceIndex = 0)
        : this(new(BindingKind.JoystickButton, BindingSpec.CodeOf(button), deviceIndex)) { }
    /// <summary>Binds one joystick axis direction using a strict ±0.5 threshold.</summary>
    public InputBinding(JoystickAxis axis, int deviceIndex = 0, bool isNegative = false)
        : this(new(BindingKind.JoystickAxis, BindingSpec.CodeOf(axis), deviceIndex, isNegative)) { }
    /// <summary>Binds a standard gamepad button.</summary>
    public InputBinding(GamepadButton button, int deviceIndex = 0)
        : this(new(BindingKind.GamepadButton, BindingSpec.CodeOf(button), deviceIndex)) { }
    /// <summary>Binds a normalized gamepad axis direction using a strict ±0.5 threshold.</summary>
    public InputBinding(GamepadAxis axis, int deviceIndex = 0, bool isNegative = false)
        : this(new(BindingKind.GamepadAxis, BindingSpec.CodeOf(axis), deviceIndex, isNegative)) { }
    internal InputBinding(BindingSpec spec) { spec.Validate(BindingShape.Button); Spec = spec; }
    /// <summary>Returns a diagnostic description including device, code and direction.</summary>
    public override string ToString() => $"{DeviceType}:{DeviceIndex}:{InputCode}{(Spec.Kind == BindingKind.JoystickButton ? "(button)" : IsNegative ? "(-)" : "")}";
}
