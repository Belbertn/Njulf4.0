using Silk.NET.Input;

namespace Njulf.Input;

public sealed partial class InputManager
{
    internal InputRebindSession BeginRebind(InputActionState target, int index, InputBindingPart part, InputKey? cancelKey)
    {
        EnsureUsable(); BindingSpec.CodeOf(part);
        if (cancelKey.HasValue) BindingSpec.CodeOf(cancelKey.Value);
        if (!_focused) throw new InvalidOperationException("Cannot capture bindings while unfocused.");
        if (_rebind != null) throw new InvalidOperationException("A binding capture is already pending.");
        if (!_isInitialized) Initialize();
        var session = new InputRebindSession(target, index, part, cancelKey);
        // Do not let motion preceding the request satisfy capture's movement threshold.
        foreach (var motion in _mouseMotion.Values)
            motion.Pending = motion.Published = motion.PendingWheel = motion.PublishedWheel = default;
        _rebind = session;
        GateHeldActions(); return _rebind;
    }

    internal void EndRebind(InputRebindSession session)
    {
        if (_rebind == session) { _rebind = null; GateHeldActions(); }
    }

    internal IEnumerable<int> PressedKeyboards(InputKey key)
    {
        for (int i = 0; i < _keyboards.Count; i++)
            if (Device(_keyboards, i)?.IsKeyPressed((Key)key) == true) yield return i;
    }

    internal IEnumerable<BindingSpec> CaptureCandidates(BindingShape shape, InputValueMode mode)
    {
        if (mode == InputValueMode.Delta)
        {
            for (int i = 0; i < _mice.Count; i++)
            {
                if (Device(_mice, i) == null) continue;
                if (shape == BindingShape.Float)
                    foreach (var axis in Enum.GetValues<MouseAxis>()) yield return new(BindingKind.MouseAxis, (int)axis, i);
                else
                {
                    yield return new(BindingKind.MouseMotion, DeviceIndex: i);
                    yield return new(BindingKind.AxisPair, Code: 4, DeviceIndex: i, Parts:
                        [new(BindingKind.MouseAxis, (int)MouseAxis.WheelX, i), new(BindingKind.MouseAxis, (int)MouseAxis.WheelY, i)]);
                }
            }
            yield break;
        }

        if (shape != BindingShape.Vector2)
        {
            for (int i = 0; i < _keyboards.Count; i++)
                if (Device(_keyboards, i) != null)
                    foreach (var key in Enum.GetValues<InputKey>().Where(key => (int)key >= 0).Distinct())
                        yield return new(BindingKind.Key, (int)key, i);
            for (int i = 0; i < _mice.Count; i++)
                if (Device(_mice, i) != null)
                    foreach (var button in MouseButtons) yield return new(BindingKind.MouseButton, (int)button, i);
        }

        for (int i = 0; i < _gamepads.Count; i++)
        {
            if (Device(_gamepads, i) == null) continue;
            if (shape == BindingShape.Vector2)
            {
                foreach (var stick in Enum.GetValues<GamepadStick>()) yield return new(BindingKind.GamepadStick, (int)stick, i, DeadZone: .15f);
            }
            else
            {
                foreach (var button in Enum.GetValues<GamepadButton>()) yield return new(BindingKind.GamepadButton, (int)button, i);
                foreach (var axis in Enum.GetValues<GamepadAxis>())
                {
                    yield return new(BindingKind.GamepadAxis, (int)axis, i, DeadZone: shape == BindingShape.Button ? 0 : axis >= GamepadAxis.LeftTrigger ? .05f : .15f);
                    if (shape == BindingShape.Button && axis < GamepadAxis.LeftTrigger)
                        yield return new(BindingKind.GamepadAxis, (int)axis, i, IsNegative: true);
                }
            }
        }
        for (int i = 0; i < _joysticks.Count; i++)
        {
            var joystick = Device(_joysticks, i);
            // GLFW exposes mapped pads through both collections. Preserve their standard control names.
            if (joystick == null || _gamepads.Any(pad => _connected.Contains(pad) && pad.Index == joystick.Index)) continue;
            if (shape == BindingShape.Vector2)
            {
                foreach (int first in new[] { 0, 3 })
                    if (first + 1 < joystick.Axes.Count)
                        yield return new(BindingKind.AxisPair, first, i, DeadZone: .15f,
                            Parts: [new(BindingKind.JoystickAxis, first, i), new(BindingKind.JoystickAxis, first + 1, i, Scale: -1)]);
            }
            else
            {
                for (int button = 0; button < System.Math.Min(joystick.Buttons.Count, 16); button++)
                    yield return new(BindingKind.JoystickButton, button, i);
                for (int axis = 0; axis < System.Math.Min(joystick.Axes.Count, 6); axis++)
                {
                    yield return new(BindingKind.JoystickAxis, axis, i, DeadZone: shape == BindingShape.Button ? 0 : .15f);
                    if (shape == BindingShape.Button) yield return new(BindingKind.JoystickAxis, axis, i, IsNegative: true);
                }
            }
        }
    }
}
