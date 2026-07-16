using System;
using Hexa.NET.ImGui;
using Njulf.Core.Math;
using Njulf.Input;
using Silk.NET.Input;

namespace Njulf.Editor;

/// <summary>Forwards raw Silk input to ImGui without changing the action-based game input model.</summary>
public sealed class EditorInputBridge : IDisposable
{
    private readonly InputManager _input;
    private readonly ImGuiEditorOverlayHost _host;
    private bool _disposed;

    public EditorInputBridge(InputManager input, ImGuiEditorOverlayHost host)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input.RawKeyDown += KeyDown;
        _input.RawKeyUp += KeyUp;
        _input.RawTextInput += TextInput;
        _input.RawMouseButtonChanged += MouseButton;
        _input.RawMouseMoved += MouseMove;
        _input.RawMouseScrolled += MouseScroll;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _input.RawKeyDown -= KeyDown;
        _input.RawKeyUp -= KeyUp;
        _input.RawTextInput -= TextInput;
        _input.RawMouseButtonChanged -= MouseButton;
        _input.RawMouseMoved -= MouseMove;
        _input.RawMouseScrolled -= MouseScroll;
        _disposed = true;
    }

    private void KeyDown(Key key, char character)
    {
        if (TryMap(key, out ImGuiKey mapped)) _host.AddKey(mapped, true);
    }
    private void KeyUp(Key key) { if (TryMap(key, out ImGuiKey mapped)) _host.AddKey(mapped, false); }
    private void TextInput(char character) { if (!char.IsControl(character)) _host.AddText(character); }
    private void MouseButton(int button, bool down) { if (button is >= 0 and <= 4) _host.AddMouseButton(button, down); }
    private void MouseMove(Vector2 position) => _host.AddMousePosition(new System.Numerics.Vector2(position.X, position.Y));
    private void MouseScroll(Vector2 scroll) => _host.AddMouseWheel(new System.Numerics.Vector2(scroll.X, scroll.Y));

    private static bool TryMap(Key key, out ImGuiKey mapped)
    {
        mapped = key switch
        {
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.SuperLeft => ImGuiKey.LeftSuper,
            Key.SuperRight => ImGuiKey.RightSuper,
            Key.Enter => ImGuiKey.Enter,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Delete => ImGuiKey.Delete,
            Key.Tab => ImGuiKey.Tab,
            Key.Escape => ImGuiKey.Escape,
            Key.Space => ImGuiKey.Space,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            _ => ImGuiKey.None
        };
        return mapped != ImGuiKey.None || Enum.TryParse(key.ToString(), true, out mapped);
    }
}
