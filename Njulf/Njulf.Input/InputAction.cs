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
    internal InputActionState<InputBinding> State { get; }
    internal InputAction(string name, InputManager owner, InputActionContext? context)
    {
        State = new(owner, name, context, InputValueMode.State, BindingShape.Button, b => b.Spec, s => new(s));
        State.ActionObject = this;
        State.Notify = Notify;
        State.OnInvalidate = () => { Pressed = null; Released = null; };
    }
    /// <summary>Case-sensitive registration name, retained for configuration and diagnostics.</summary>
    public string Name => State.Name;
    /// <summary>Owning context, or null for an always-active global action.</summary>
    public InputActionContext? Context => State.Context;
    /// <summary>Current immutable bindings in evaluation and persistence order.</summary>
    public IReadOnlyList<InputBinding> Bindings { get { State.Owner.EnsureUsable(); return State.Bindings; } }
    /// <summary>Whether at least one binding is currently active.</summary>
    public bool IsDown { get { State.Owner.EnsureUsable(); return State.Value.X != 0; } }
    /// <summary>True only for the update that changes from up to down.</summary>
    public bool WasPressed { get { State.Owner.EnsureUsable(); return State.Previous.X == 0 && State.Value.X != 0; } }
    /// <summary>True only for the update that changes from down to up.</summary>
    public bool WasReleased { get { State.Owner.EnsureUsable(); return State.Previous.X != 0 && State.Value.X == 0; } }
    /// <summary>Raised once on an up-to-down transition, on the game thread.</summary>
    public event Action? Pressed;
    /// <summary>Raised once on a down-to-up transition, on the game thread.</summary>
    public event Action? Released;
    /// <summary>Adds an alternative binding. The immutable binding remains caller-accessible.</summary>
    public void AddBinding(InputBinding binding)
    { State.Add(binding); }
    /// <summary>Removes the supplied binding; the state changes at the next input update.</summary>
    public void RemoveBinding(InputBinding binding)
    { State.Remove(binding); }
    /// <summary>Replaces one binding; effective at the next input update.</summary>
    public void ReplaceBinding(int index, InputBinding binding) => State.Replace(index, binding);
    /// <summary>Removes all bindings; effective at the next input update.</summary>
    public void ClearBindings() { State.Owner.EnsureUsable(); State.Clear(); }
    /// <summary>Captures a fresh control into an existing slot. Escape cancels unless overridden or null.</summary>
    public InputRebindSession BeginRebind(int bindingIndex, InputKey? cancelKey = InputKey.Escape) =>
        State.Owner.BeginRebind(State, bindingIndex, InputBindingPart.Whole, cancelKey);
    /// <summary>Clears current and previous state without firing events; keeps bindings.</summary>
    public void Reset() { State.Owner.EnsureUsable(); State.Value = State.Previous = default; }
    internal void Notify()
    {
        if (WasPressed) { Pressed?.Invoke(); State.Owner.NotifyPressed(this); }
        else if (WasReleased) { Released?.Invoke(); State.Owner.NotifyReleased(this); }
    }
}
