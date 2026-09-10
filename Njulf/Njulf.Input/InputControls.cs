namespace Njulf.Input;

/// <summary>Whether an action represents a held state or a per-update displacement.</summary>
public enum InputValueMode
{
    /// <summary>Normalized held input.</summary>
    State,
    /// <summary>Unbounded displacement, cleared at each input update.</summary>
    Delta
}

/// <summary>Standard gamepad buttons, independent of native button indices.</summary>
public enum GamepadButton
{
    /// <summary>South face button.</summary>
    A,
    /// <summary>East face button.</summary>
    B,
    /// <summary>West face button.</summary>
    X,
    /// <summary>North face button.</summary>
    Y,
    /// <summary>Left shoulder.</summary>
    LeftBumper,
    /// <summary>Right shoulder.</summary>
    RightBumper,
    /// <summary>Back/select button.</summary>
    Back,
    /// <summary>Start/menu button.</summary>
    Start,
    /// <summary>Home/guide button, when available.</summary>
    Home,
    /// <summary>Left stick click.</summary>
    LeftStick,
    /// <summary>Right stick click.</summary>
    RightStick,
    /// <summary>Directional pad up.</summary>
    DPadUp,
    /// <summary>Directional pad right.</summary>
    DPadRight,
    /// <summary>Directional pad down.</summary>
    DPadDown,
    /// <summary>Directional pad left.</summary>
    DPadLeft
}

/// <summary>Standard gamepad axes. Stick Y is positive upward; triggers range from zero to one.</summary>
public enum GamepadAxis
{
    /// <summary>Left stick horizontal axis.</summary>
    LeftX,
    /// <summary>Left stick vertical axis.</summary>
    LeftY,
    /// <summary>Right stick horizontal axis.</summary>
    RightX,
    /// <summary>Right stick vertical axis.</summary>
    RightY,
    /// <summary>Left trigger.</summary>
    LeftTrigger,
    /// <summary>Right trigger.</summary>
    RightTrigger
}

/// <summary>A standard gamepad stick.</summary>
public enum GamepadStick
{
    /// <summary>Left stick.</summary>
    Left,
    /// <summary>Right stick.</summary>
    Right
}

/// <summary>Mouse displacement sources. Motion uses pixels; wheels use platform wheel steps.</summary>
public enum MouseAxis
{
    /// <summary>Horizontal motion, positive right.</summary>
    X,
    /// <summary>Vertical motion, positive down.</summary>
    Y,
    /// <summary>Horizontal wheel steps.</summary>
    WheelX,
    /// <summary>Vertical wheel steps.</summary>
    WheelY
}

/// <summary>A complete binding or one component of a composite to replace during rebinding.</summary>
public enum InputBindingPart
{
    /// <summary>Replace the complete binding.</summary>
    Whole,
    /// <summary>Negative button of a scalar composite.</summary>
    Negative,
    /// <summary>Positive button of a scalar composite.</summary>
    Positive,
    /// <summary>Left button of a four-button composite.</summary>
    Left,
    /// <summary>Right button of a four-button composite.</summary>
    Right,
    /// <summary>Down button of a four-button composite.</summary>
    Down,
    /// <summary>Up button of a four-button composite.</summary>
    Up,
    /// <summary>Horizontal axis of an axis-pair composite.</summary>
    X,
    /// <summary>Vertical axis of an axis-pair composite.</summary>
    Y
}

/// <summary>Ordinary cursor modes exposed through Game.Input.</summary>
public enum InputCursorMode
{
    /// <summary>Visible, unrestricted cursor.</summary>
    Normal,
    /// <summary>Invisible, unrestricted cursor.</summary>
    Hidden,
    /// <summary>Invisible relative-motion cursor, preferring raw motion.</summary>
    Captured
}

/// <summary>A manager-owned named group of actions. Only the active group and global actions run.</summary>
public sealed class InputActionContext
{
    internal InputManager Owner { get; }
    /// <summary>Case-sensitive configuration name.</summary>
    public string Name { get; }
    internal InputActionContext(string name, InputManager owner) { Name = name; Owner = owner; }
}
