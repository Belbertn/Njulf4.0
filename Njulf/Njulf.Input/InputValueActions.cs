using Njulf.Core.Math;

namespace Njulf.Input;

/// <summary>A cached scalar action. State alternatives use greatest magnitude; delta alternatives sum.</summary>
public sealed class InputFloatAction
{
    internal InputActionState<InputFloatBinding> State { get; }
    internal InputFloatAction(string name, InputManager owner, InputActionContext? context, InputValueMode mode)
    {
        State = new(owner, name, context, mode, BindingShape.Float, b => b.Spec, s => new(s));
        State.ActionObject = this;
    }
    /// <summary>Case-sensitive registration name.</summary>
    public string Name => State.Name;
    /// <summary>Owning context, or null for a global action.</summary>
    public InputActionContext? Context => State.Context;
    /// <summary>Held state or per-update displacement.</summary>
    public InputValueMode Mode => State.Mode;
    /// <summary>Latest value. State is bounded to [-1,1]; delta retains scaled source units.</summary>
    public float Value { get { State.Owner.EnsureUsable(); return State.Value.X; } }
    /// <summary>Immutable bindings in evaluation and persistence order.</summary>
    public IReadOnlyList<InputFloatBinding> Bindings { get { State.Owner.EnsureUsable(); return State.Bindings; } }
    /// <summary>Adds a mode-compatible alternative binding.</summary>
    public void AddBinding(InputFloatBinding binding) => State.Add(binding);
    /// <summary>Removes a binding; effective at the next input update.</summary>
    public void RemoveBinding(InputFloatBinding binding) => State.Remove(binding);
    /// <summary>Replaces a binding; effective at the next input update.</summary>
    public void ReplaceBinding(int index, InputFloatBinding binding) => State.Replace(index, binding);
    /// <summary>Removes all bindings; effective at the next input update.</summary>
    public void ClearBindings() { State.Owner.EnsureUsable(); State.Clear(); }
    /// <summary>Captures a complete binding or a negative/positive button component. Escape cancels by default.</summary>
    public InputRebindSession BeginRebind(int bindingIndex, InputBindingPart part = InputBindingPart.Whole, InputKey? cancelKey = InputKey.Escape) =>
        State.Owner.BeginRebind(State, bindingIndex, part, cancelKey);
}

/// <summary>A cached vector action. State alternatives use greatest magnitude; delta alternatives sum.</summary>
public sealed class InputVector2Action
{
    internal InputActionState<InputVector2Binding> State { get; }
    internal InputVector2Action(string name, InputManager owner, InputActionContext? context, InputValueMode mode)
    {
        State = new(owner, name, context, mode, BindingShape.Vector2, b => b.Spec, s => new(s));
        State.ActionObject = this;
    }
    /// <summary>Case-sensitive registration name.</summary>
    public string Name => State.Name;
    /// <summary>Owning context, or null for a global action.</summary>
    public InputActionContext? Context => State.Context;
    /// <summary>Held state or per-update displacement.</summary>
    public InputValueMode Mode => State.Mode;
    /// <summary>Latest vector. State has maximum length one; delta retains scaled source units.</summary>
    public Vector2 Value { get { State.Owner.EnsureUsable(); return State.Value; } }
    /// <summary>Immutable bindings in evaluation and persistence order.</summary>
    public IReadOnlyList<InputVector2Binding> Bindings { get { State.Owner.EnsureUsable(); return State.Bindings; } }
    /// <summary>Adds a mode-compatible alternative binding.</summary>
    public void AddBinding(InputVector2Binding binding) => State.Add(binding);
    /// <summary>Removes a binding; effective at the next input update.</summary>
    public void RemoveBinding(InputVector2Binding binding) => State.Remove(binding);
    /// <summary>Replaces a binding; effective at the next input update.</summary>
    public void ReplaceBinding(int index, InputVector2Binding binding) => State.Replace(index, binding);
    /// <summary>Removes all bindings; effective at the next input update.</summary>
    public void ClearBindings() { State.Owner.EnsureUsable(); State.Clear(); }
    /// <summary>Captures a complete binding or one named composite component. Escape cancels by default.</summary>
    public InputRebindSession BeginRebind(int bindingIndex, InputBindingPart part = InputBindingPart.Whole, InputKey? cancelKey = InputKey.Escape) =>
        State.Owner.BeginRebind(State, bindingIndex, part, cancelKey);
}
