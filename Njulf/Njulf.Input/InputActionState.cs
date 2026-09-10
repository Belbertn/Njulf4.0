using Njulf.Core.Math;

namespace Njulf.Input;

internal abstract class InputActionState(InputManager owner, string name, InputActionContext? context, InputValueMode mode, BindingShape shape)
{
    internal InputManager Owner { get; } = owner;
    internal string Name { get; } = name;
    internal InputActionContext? Context { get; } = context;
    internal InputValueMode Mode { get; } = mode;
    internal BindingShape Shape { get; } = shape;
    internal object ActionObject { get; set; } = null!;
    internal Vector2 Value, Previous;
    internal bool WaitForNeutral;
    private bool _wasEnabled = context is null;
    internal abstract int Count { get; }
    internal abstract BindingSpec GetSpec(int index);
    internal abstract void ReplaceSpec(int index, BindingSpec spec);
    internal abstract Action PrepareBindings(BindingSpec[] specs);
    internal abstract void Clear();
    internal Action? Notify;
    internal Action? OnInvalidate;

    internal void ValidateMode(BindingSpec spec)
    {
        spec.Validate(Shape);
        if (spec.Mode != Mode) throw new ArgumentException("The binding and action must use the same value mode.");
    }

    internal void Sample(bool enabled)
    {
        Previous = Value;
        Vector2 raw = Vector2.Zero;
        for (int i = 0; i < Count; i++)
        {
            var value = Owner.Evaluate(GetSpec(i), Shape);
            if (Mode == InputValueMode.Delta) raw += value;
            else if (value.LengthSquared() > raw.LengthSquared()) raw = value;
        }
        if (!enabled || !_wasEnabled) WaitForNeutral = Mode == InputValueMode.State;
        _wasEnabled = enabled;
        if (raw.LengthSquared() == 0) WaitForNeutral = false;
        Value = enabled && !WaitForNeutral ? raw : Vector2.Zero;
    }

    internal void Invalidate()
    {
        Clear(); OnInvalidate?.Invoke(); Notify = null; OnInvalidate = null;
        Value = Previous = Vector2.Zero;
    }
}

internal sealed class InputActionState<TBinding> : InputActionState where TBinding : class
{
    private readonly List<TBinding> _bindings = [];
    private readonly Func<TBinding, BindingSpec> _describe;
    private readonly Func<BindingSpec, TBinding> _create;
    internal IReadOnlyList<TBinding> Bindings { get; }
    internal InputActionState(InputManager owner, string name, InputActionContext? context, InputValueMode mode,
        BindingShape shape, Func<TBinding, BindingSpec> describe, Func<BindingSpec, TBinding> create)
        : base(owner, name, context, mode, shape)
    { _describe = describe; _create = create; Bindings = _bindings.AsReadOnly(); }
    internal override int Count => _bindings.Count;
    internal override BindingSpec GetSpec(int index) => _describe(_bindings[index]);
    internal void Add(TBinding binding)
    {
        Owner.EnsureUsable(); ArgumentNullException.ThrowIfNull(binding);
        ValidateMode(_describe(binding)); _bindings.Add(binding);
    }
    internal void Remove(TBinding binding)
    { Owner.EnsureUsable(); ArgumentNullException.ThrowIfNull(binding); _bindings.Remove(binding); }
    internal void Replace(int index, TBinding binding)
    {
        Owner.EnsureUsable(); ArgumentNullException.ThrowIfNull(binding);
        ValidateMode(_describe(binding)); _bindings[index] = binding;
    }
    internal override void ReplaceSpec(int index, BindingSpec spec) => Replace(index, _create(spec));
    internal override void Clear() => _bindings.Clear();
    internal override Action PrepareBindings(BindingSpec[] specs)
    {
        var bindings = specs.Select(spec => { ValidateMode(spec); return _create(spec); }).ToArray();
        return () => { _bindings.Clear(); _bindings.AddRange(bindings); WaitForNeutral = Mode == InputValueMode.State; };
    }
}
