using Njulf.Core.Math;
using Silk.NET.Input;

namespace Njulf.Input.Advanced;

/// <summary>Advanced borrowed native input hooks for UI/editor integration, implemented by InputManager.</summary>
/// <remarks>Use an explicit interface cast from Game.Input. Call on the game thread and unsubscribe before
/// host shutdown. Raw events retain platform timing and bypass gameplay text suppression during rebinding
/// and focus loss. This interface does not own the input manager or native devices.</remarks>
public interface INativeInputIntegration
{
    /// <summary>Native key press and its platform character hint.</summary>
    event Action<Key, char>? RawKeyDown;
    /// <summary>Native key release.</summary>
    event Action<Key>? RawKeyUp;
    /// <summary>Unfiltered platform text character.</summary>
    event Action<char>? RawTextInput;
    /// <summary>Native button code and pressed state.</summary>
    event Action<int, bool>? RawMouseButtonChanged;
    /// <summary>Client-area cursor position in pixels.</summary>
    event Action<Vector2>? RawMouseMoved;
    /// <summary>Horizontal and vertical platform scroll increments.</summary>
    event Action<Vector2>? RawMouseScrolled;
    /// <summary>Polls a native key; missing or disconnected keyboards return false.</summary>
    bool IsPhysicalKeyDown(Key key, int keyboardIndex = 0);
    /// <summary>Sets the native cursor mode on connected mice and clears accumulated motion.</summary>
    void SetCursorMode(CursorMode mode);
}
