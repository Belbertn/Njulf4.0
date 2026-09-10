namespace Njulf.Input;

/// <summary>An immutable scalar binding. Negative scale inverts its value.</summary>
public sealed class InputFloatBinding
{
    internal BindingSpec Spec { get; }
    /// <summary>Required action value mode.</summary>
    public InputValueMode Mode => Spec.Mode;
    /// <summary>Multiplier applied after dead-zone processing.</summary>
    public float Scale => Spec.Scale;
    /// <summary>Rescaled scalar dead zone.</summary>
    public float DeadZone => Spec.DeadZone;
    /// <summary>Uses a button as zero or one.</summary>
    public InputFloatBinding(InputBinding button, float scale = 1)
        : this(new(BindingKind.ButtonValue, Scale: scale, Parts: [(button ?? throw new ArgumentNullException(nameof(button))).Spec])) { }
    /// <summary>Uses opposing buttons as minus one and plus one; both pressed cancel.</summary>
    public InputFloatBinding(InputBinding negative, InputBinding positive, float scale = 1)
        : this(new(BindingKind.Digital, Scale: scale, Parts: [(negative ?? throw new ArgumentNullException(nameof(negative))).Spec,
            (positive ?? throw new ArgumentNullException(nameof(positive))).Spec])) { }
    /// <summary>Reads a joystick axis, retaining its native direction.</summary>
    public InputFloatBinding(JoystickAxis axis, int deviceIndex = 0, float deadZone = .15f, float scale = 1)
        : this(new(BindingKind.JoystickAxis, BindingSpec.CodeOf(axis), deviceIndex, Scale: scale, DeadZone: deadZone)) { }
    /// <summary>Reads a normalized gamepad axis. The default dead zone is .05 for triggers and .15 for sticks.</summary>
    public InputFloatBinding(GamepadAxis axis, int deviceIndex = 0, float? deadZone = null, float scale = 1)
        : this(new(BindingKind.GamepadAxis, BindingSpec.CodeOf(axis), deviceIndex, Scale: scale,
            DeadZone: deadZone ?? (axis >= GamepadAxis.LeftTrigger ? .05f : .15f))) { }
    /// <summary>Reads per-update mouse displacement in pixels or wheel steps.</summary>
    public InputFloatBinding(MouseAxis axis, int deviceIndex = 0, float scale = 1)
        : this(new(BindingKind.MouseAxis, BindingSpec.CodeOf(axis), deviceIndex, Scale: scale)) { }
    internal InputFloatBinding(BindingSpec spec) { spec.Validate(BindingShape.Float); Spec = spec; }
}

/// <summary>An immutable vector binding. State vectors use positive Y upward; mouse motion uses positive Y downward.</summary>
public sealed class InputVector2Binding
{
    internal BindingSpec Spec { get; }
    /// <summary>Required action value mode.</summary>
    public InputValueMode Mode => Spec.Mode;
    /// <summary>Multiplier applied after radial dead-zone processing.</summary>
    public float Scale => Spec.Scale;
    /// <summary>Rescaled radial dead zone.</summary>
    public float DeadZone => Spec.DeadZone;
    /// <summary>Combines four buttons; opposing directions cancel and diagonals are normalized.</summary>
    public InputVector2Binding(InputBinding left, InputBinding right, InputBinding down, InputBinding up, float scale = 1)
        : this(new(BindingKind.FourButtons, Scale: scale, Parts: [(left ?? throw new ArgumentNullException(nameof(left))).Spec,
            (right ?? throw new ArgumentNullException(nameof(right))).Spec, (down ?? throw new ArgumentNullException(nameof(down))).Spec,
            (up ?? throw new ArgumentNullException(nameof(up))).Spec])) { }
    /// <summary>Pairs scalar sources with matching modes. Set their scalar dead zones to zero when using a radial dead zone here.</summary>
    public InputVector2Binding(InputFloatBinding x, InputFloatBinding y, float deadZone = 0, float scale = 1)
        : this(new(BindingKind.AxisPair, Scale: scale, DeadZone: deadZone,
            Parts: [(x ?? throw new ArgumentNullException(nameof(x))).Spec, (y ?? throw new ArgumentNullException(nameof(y))).Spec])) { }
    /// <summary>Reads a standard stick with a radial dead zone.</summary>
    public InputVector2Binding(GamepadStick stick, int deviceIndex = 0, float deadZone = .15f, float scale = 1)
        : this(new(BindingKind.GamepadStick, BindingSpec.CodeOf(stick), deviceIndex, Scale: scale, DeadZone: deadZone)) { }
    /// <summary>Creates an unbounded per-update mouse-motion binding, in pixels.</summary>
    public static InputVector2Binding MouseMotion(int deviceIndex = 0, float scale = 1) =>
        new(new(BindingKind.MouseMotion, DeviceIndex: deviceIndex, Scale: scale));
    internal InputVector2Binding(BindingSpec spec) { spec.Validate(BindingShape.Vector2); Spec = spec; }
}
