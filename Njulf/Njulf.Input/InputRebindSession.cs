using Njulf.Core.Math;

namespace Njulf.Input;

/// <summary>The state of one interactive binding capture.</summary>
public enum InputRebindStatus
{
    /// <summary>Waiting for a fresh compatible control.</summary>
    Pending,
    /// <summary>The target binding was replaced.</summary>
    Completed,
    /// <summary>Cancelled without replacing the target.</summary>
    Cancelled
}

/// <summary>A game-thread capture operation. Poll Status during Update; disposing a pending session cancels it.</summary>
public sealed class InputRebindSession : IDisposable
{
    private readonly InputActionState _target;
    private readonly int _index, _partIndex;
    private readonly BindingSpec _original;
    private readonly InputKey? _cancelKey;
    private readonly BindingShape _shape;
    private readonly InputValueMode _mode;
    private readonly HashSet<(BindingKind, int, int, bool)> _blocked = [];
    private readonly Dictionary<(BindingKind, int, int, bool), Vector2> _motion = [];
    private readonly HashSet<int> _cancelBlocked = [];
    private InputRebindStatus _status;
    /// <summary>Current operation state. Completion is published before game Update.</summary>
    public InputRebindStatus Status { get { _target.Owner.EnsureUsable(); return _status; } }
    internal bool Pending => _status == InputRebindStatus.Pending;

    internal InputRebindSession(InputActionState target, int index, InputBindingPart part, InputKey? cancelKey)
    {
        _target = target; _index = index; _cancelKey = cancelKey; _original = target.GetSpec(index);
        _partIndex = part == InputBindingPart.Whole ? -1 : _original.PartIndex(part);
        _shape = _partIndex < 0 ? target.Shape : _original.Kind == BindingKind.AxisPair ? BindingShape.Float : BindingShape.Button;
        _mode = _partIndex < 0 ? target.Mode : _original.Parts![_partIndex].Mode;
        foreach (var candidate in target.Owner.CaptureCandidates(_shape, _mode))
            if (_mode == InputValueMode.State && Magnitude(candidate) > NeutralThreshold(candidate)) _blocked.Add(Key(candidate));
        if (cancelKey.HasValue)
            foreach (int keyboard in target.Owner.PressedKeyboards(cancelKey.Value)) _cancelBlocked.Add(keyboard);
    }

    private static (BindingKind, int, int, bool) Key(BindingSpec spec) => (spec.Kind, spec.Code, spec.DeviceIndex, spec.IsNegative);
    private float Magnitude(BindingSpec spec) => _target.Owner.Evaluate(spec with { DeadZone = 0 }, _shape).Length();
    private float NeutralThreshold(BindingSpec spec) => _shape == BindingShape.Button ? 0
        : spec.Kind == BindingKind.GamepadAxis && spec.Code >= (int)GamepadAxis.LeftTrigger ? .05f : .15f;

    internal void Sample()
    {
        if (_target.Count <= _index || !ReferenceEquals(_original, _target.GetSpec(_index))) { Cancel(); return; }
        if (_cancelKey.HasValue)
        {
            var pressed = _target.Owner.PressedKeyboards(_cancelKey.Value).ToHashSet();
            _cancelBlocked.IntersectWith(pressed);
            if (pressed.Any(index => !_cancelBlocked.Contains(index))) { Cancel(); return; }
        }
        foreach (var candidate in _target.Owner.CaptureCandidates(_shape, _mode))
        {
            var key = Key(candidate);
            Vector2 value = _target.Owner.Evaluate(candidate with { DeadZone = 0 }, _shape);
            float magnitude = value.Length();
            if (_blocked.Contains(key))
            {
                if (magnitude <= NeutralThreshold(candidate)) _blocked.Remove(key);
                continue;
            }
            bool mouseMotion = candidate.Kind == BindingKind.MouseMotion || candidate.Kind == BindingKind.MouseAxis && candidate.Code <= (int)MouseAxis.Y;
            if (mouseMotion)
            {
                value += _motion.GetValueOrDefault(key); _motion[key] = value;
                if (value.Length() < 8) continue;
            }
            else if (_mode == InputValueMode.Delta ? magnitude == 0 : magnitude <= .5f) continue;

            BindingSpec replacement = candidate;
            BindingSpec previous = _partIndex < 0 ? _original : _original.Parts![_partIndex];
            // Keep user sensitivity/inversion and analog dead-zone preferences when changing a source.
            if (_shape != BindingShape.Button)
                replacement = replacement with { Scale = previous.Scale,
                    DeadZone = replacement.Mode == InputValueMode.State && previous.Kind == replacement.Kind ? previous.DeadZone : replacement.DeadZone };
            if (_partIndex >= 0)
            {
                var parts = (BindingSpec[])_original.Parts!.Clone(); parts[_partIndex] = replacement;
                replacement = _original with { Parts = parts };
            }
            _target.ReplaceSpec(_index, replacement);
            _status = InputRebindStatus.Completed;
            _target.Owner.EndRebind(this);
            return;
        }
    }

    /// <summary>Cancels a pending operation without changing bindings. Repeated cancellation is harmless.</summary>
    public void Cancel()
    {
        _target.Owner.EnsureUsable();
        if (!Pending) return;
        _status = InputRebindStatus.Cancelled; _target.Owner.EndRebind(this);
    }
    /// <summary>Cancels this operation if still pending.</summary>
    public void Dispose() { if (Pending) Cancel(); }
}
