namespace Njulf.Input;

/// <summary>A manager-owned button action combining its bindings with logical OR.</summary>
/// <remarks>Cache this reference and poll it during Update. Edge flags describe the latest input
/// update, not the latest render. All operations require the manager's game thread; state access
/// and binding changes throw ObjectDisposedException after the manager is disposed.</remarks>
/// <example><code>
/// var jump = Input.CreateAction("Jump");
/// jump.AddBinding(new InputBinding(InputKey.Space));
/// // In Update: if (jump.WasPressed) Jump();
/// </code></example>
public sealed class InputAction
{
    private readonly InputManager _owner;
    private readonly List<InputBinding> _bindings = new();
    private bool _down, _previous;
    internal InputAction(string name, InputManager owner) { Name = name; _owner = owner; }
    /// <summary>Case-sensitive registration name, retained for configuration and diagnostics.</summary>
    public string Name { get; }
    /// <summary>Whether at least one binding is currently active.</summary>
    public bool IsDown { get { _owner.EnsureUsable(); return _down; } }
    /// <summary>True only for the update that changes from up to down.</summary>
    public bool WasPressed { get { _owner.EnsureUsable(); return !_previous && _down; } }
    /// <summary>True only for the update that changes from down to up.</summary>
    public bool WasReleased { get { _owner.EnsureUsable(); return _previous && !_down; } }
    /// <summary>Raised once on an up-to-down transition, on the game thread.</summary>
    public event Action? Pressed;
    /// <summary>Raised once on a down-to-up transition, on the game thread.</summary>
    public event Action? Released;
    /// <summary>Adds an alternative binding. The immutable binding remains caller-accessible.</summary>
    public void AddBinding(InputBinding binding)
    { _owner.EnsureUsable(); ArgumentNullException.ThrowIfNull(binding); _bindings.Add(binding); }
    /// <summary>Removes the supplied binding; the state changes at the next input update.</summary>
    public void RemoveBinding(InputBinding binding)
    { _owner.EnsureUsable(); ArgumentNullException.ThrowIfNull(binding); _bindings.Remove(binding); }
    /// <summary>Clears current and previous state without firing events; keeps bindings.</summary>
    public void Reset() { _owner.EnsureUsable(); _down = _previous = false; }
    internal void Sample()
    {
        _previous = _down;
        _down = false;
        foreach (var binding in _bindings)
            if (_owner.IsActive(binding)) { _down = true; break; }
    }
    internal void Notify()
    {
        if (!_previous && _down) { Pressed?.Invoke(); _owner.NotifyPressed(this); }
        else if (_previous && !_down) { Released?.Invoke(); _owner.NotifyReleased(this); }
    }
    internal void Invalidate() { Pressed = null; Released = null; _bindings.Clear(); _down = _previous = false; }
}
