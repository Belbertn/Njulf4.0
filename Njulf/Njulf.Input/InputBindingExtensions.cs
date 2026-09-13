namespace Njulf.Input;

/// <summary>Shortcuts that create ordinary, rebindable actions and bindings.</summary>
public static class InputBindingExtensions
{
    /// <summary>Creates a keyboard button with optional fixed-step press buffering.</summary>
    public static InputAction CreateButton(this IInputManager input, string name, InputKey key,
        InputActionContext? context = null, bool bufferPresses = false) =>
        CreateButton(input, name, new InputBinding(key), context, bufferPresses);

    /// <summary>Creates a mouse button with optional fixed-step press buffering.</summary>
    public static InputAction CreateButton(this IInputManager input, string name, MouseButton button,
        InputActionContext? context = null, bool bufferPresses = false) =>
        CreateButton(input, name, new InputBinding(button), context, bufferPresses);

    /// <summary>Creates a standard gamepad button on the selected device.</summary>
    public static InputAction CreateButton(this IInputManager input, string name, GamepadButton button,
        InputActionContext? context = null, bool bufferPresses = false, int deviceIndex = 0) =>
        CreateButton(input, name, new InputBinding(button, deviceIndex), context, bufferPresses);

    /// <summary>Creates a button with its first binding and optional press buffering.</summary>
    public static InputAction CreateButton(this IInputManager input, string name, InputBinding binding,
        InputActionContext? context = null, bool bufferPresses = false)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(binding);
        var action = input.CreateAction(name, context);
        action.AddBinding(binding); action.BufferPresses = bufferPresses;
        return action;
    }

    /// <summary>Adds an alternative keyboard binding and returns the same action.</summary>
    public static InputAction Bind(this InputAction action, InputKey key)
    { action.AddBinding(new(key)); return action; }
    /// <summary>Adds an alternative mouse binding and returns the same action.</summary>
    public static InputAction Bind(this InputAction action, MouseButton button)
    { action.AddBinding(new(button)); return action; }
    /// <summary>Adds an alternative gamepad binding and returns the same action.</summary>
    public static InputAction Bind(this InputAction action, GamepadButton button, int deviceIndex = 0)
    { action.AddBinding(new(button, deviceIndex)); return action; }

    /// <summary>Creates normalized WASD movement: right is +X, forward is +Y.</summary>
    public static InputVector2Action CreateWasd(this IInputManager input, string name, InputActionContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.CreateVector2Action(name, context).BindWasd();
    }

    /// <summary>Adds normalized WASD movement to a state action and returns it.</summary>
    public static InputVector2Action BindWasd(this InputVector2Action action)
    {
        action.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D), new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
        return action;
    }

    /// <summary>Creates mouse displacement in scaled pixels, +X right and +Y down. No time multiplication is applied.</summary>
    public static InputVector2Action CreateMouseLook(this IInputManager input, string name, float sensitivity = 1,
        InputActionContext? context = null, bool bufferDeltas = false, int deviceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(input);
        var binding = InputVector2Binding.MouseMotion(deviceIndex, sensitivity);
        var action = input.CreateVector2Action(name, context, InputValueMode.Delta);
        action.AddBinding(binding); action.BufferDeltas = bufferDeltas;
        return action;
    }

    /// <summary>Creates a stick state action with radial dead zone, +X right and +Y up.</summary>
    public static InputVector2Action CreateGamepadStick(this IInputManager input, string name, GamepadStick stick,
        InputActionContext? context = null, int deviceIndex = 0, float deadZone = .15f)
    {
        ArgumentNullException.ThrowIfNull(input);
        var binding = new InputVector2Binding(stick, deviceIndex, deadZone);
        var action = input.CreateVector2Action(name, context);
        action.AddBinding(binding);
        return action;
    }

    /// <summary>Adds an alternative stick binding to a state action and returns it.</summary>
    public static InputVector2Action BindGamepadStick(this InputVector2Action action, GamepadStick stick,
        int deviceIndex = 0, float deadZone = .15f)
    {
        action.AddBinding(new(stick, deviceIndex, deadZone));
        return action;
    }
}
