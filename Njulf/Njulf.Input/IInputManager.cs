using Njulf.Core.Math;

namespace Njulf.Input;

/// <summary>Game-thread input service. The host updates it before each gameplay update.</summary>
/// <remarks>Cache actions returned by CreateAction instead of looking them up while polling.
/// Mouse coordinates and motion are pixels; scroll is in platform wheel steps.</remarks>
public interface IInputManager
{
    /// <summary>Creates a manager-owned button action with a unique, case-sensitive name.</summary>
    InputAction CreateAction(string name);
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
