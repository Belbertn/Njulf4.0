using Njulf.Core.Math;

namespace Njulf.Input;

/// <summary>Game-thread input service. The host updates it before each gameplay update.</summary>
/// <remarks>Cache actions returned by CreateAction instead of looking them up while polling.
/// Mouse coordinates and motion are pixels; scroll is in platform wheel steps.</remarks>
public interface IInputManager
{
    /// <summary>Creates a manager-owned button action with a unique, case-sensitive name.</summary>
    InputAction CreateAction(string name);
    /// <summary>Creates a button action in a manager-owned context; null means global.</summary>
    InputAction CreateAction(string name, InputActionContext? context);
    /// <summary>Creates a scalar action with a globally unique name.</summary>
    InputFloatAction CreateFloatAction(string name, InputActionContext? context = null, InputValueMode mode = InputValueMode.State);
    /// <summary>Creates a vector action with a globally unique name.</summary>
    InputVector2Action CreateVector2Action(string name, InputActionContext? context = null, InputValueMode mode = InputValueMode.State);
    /// <summary>Finds a scalar action, or returns null when absent or of another type.</summary>
    InputFloatAction? GetFloatAction(string name);
    /// <summary>Finds a vector action, or returns null when absent or of another type.</summary>
    InputVector2Action? GetVector2Action(string name);
    /// <summary>Creates a uniquely named exclusive context.</summary>
    InputActionContext CreateContext(string name);
    /// <summary>Requested exclusive context, applied at the next input update. Null enables only global actions.</summary>
    InputActionContext? ActiveContext { get; set; }
    /// <summary>Requested cursor mode. Capture prefers raw motion and falls back to relative motion.</summary>
    InputCursorMode CursorMode { get; }
    /// <summary>Sets the cursor mode for ordinary games without exposing native types.</summary>
    void SetCursorMode(InputCursorMode mode);
    /// <summary>Platform text characters on the game thread. Suppressed during rebinding or focus loss.</summary>
    event Action<char>? TextInput;
    /// <summary>Saves current bindings to versioned JSON using a temporary sibling file and replacement.</summary>
    void SaveBindings(string path);
    /// <summary>Loads bindings for registered actions atomically. Returns false for a missing file; invalid files throw.</summary>
    bool LoadBindings(string path);
    /// <summary>Finds a registered action, or returns null. Names are configuration identifiers.</summary>
    InputAction? GetAction(string name);
    /// <summary>Whether any mouse holds the button in the current update snapshot.</summary>
    bool IsMouseButtonDown(MouseButton button);
    /// <summary>Whether the button changed from up to down in the current update.</summary>
    bool IsMouseButtonPressed(MouseButton button);
    /// <summary>Whether the button changed from down to up in the current update.</summary>
    bool IsMouseButtonReleased(MouseButton button);
    /// <summary>Latest window-relative pointer position in pixels, with origin at the top left.</summary>
    Vector2 MousePosition { get; }
    /// <summary>Accumulated motion in pixels since ConsumeMouseDelta; input updates do not clear it.</summary>
    Vector2 MouseDelta { get; }
    /// <summary>Vertical wheel steps received since the previous input update.</summary>
    float MouseScrollDelta { get; }
    /// <summary>Raised on the game thread after an action transitions to down.</summary>
    event Action<InputAction>? ActionPressed;
    /// <summary>Raised on the game thread after an action transitions to up.</summary>
    event Action<InputAction>? ActionReleased;
    /// <summary>Publishes one input snapshot. Ordinary games let the host call this.</summary>
    void Update();
    /// <summary>Returns accumulated pixel motion and clears it. Call on the game thread.</summary>
    Vector2 ConsumeMouseDelta();
}
