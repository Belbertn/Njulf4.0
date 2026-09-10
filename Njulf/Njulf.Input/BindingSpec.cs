using System.Text.Json.Serialization;

namespace Njulf.Input;

// The immutable description is shared by evaluation, capture and persistence. It never contains native objects.
internal enum BindingKind { Key, MouseButton, JoystickButton, JoystickAxis, GamepadButton, GamepadAxis, Digital, FourButtons, AxisPair, GamepadStick, MouseAxis, MouseMotion, ButtonValue }
internal enum BindingShape { Button, Float, Vector2 }
internal sealed record BindingSpec([property: JsonRequired] BindingKind Kind, int Code = 0, int DeviceIndex = 0,
    bool IsNegative = false, float Scale = 1, float DeadZone = 0, BindingSpec[]? Parts = null)
{
    internal InputValueMode Mode => Kind is BindingKind.MouseAxis or BindingKind.MouseMotion ? InputValueMode.Delta
        : Kind == BindingKind.AxisPair ? Parts![0].Mode : InputValueMode.State;

    internal static int CodeOf<T>(T code) where T : struct, Enum
    {
        if (!Enum.IsDefined(code) || Convert.ToInt32(code) < 0) throw new ArgumentOutOfRangeException(nameof(code));
        return Convert.ToInt32(code);
    }

    internal void Validate(BindingShape shape, int depth = 0)
    {
        if (depth > 3 || !Enum.IsDefined(Kind)) throw new ArgumentException("Invalid binding kind or nesting.");
        ArgumentOutOfRangeException.ThrowIfNegative(DeviceIndex);
        if (!float.IsFinite(Scale)) throw new ArgumentOutOfRangeException(nameof(Scale));
        if (!float.IsFinite(DeadZone) || DeadZone < 0 || DeadZone >= 1) throw new ArgumentOutOfRangeException(nameof(DeadZone));
        switch (Kind)
        {
            case BindingKind.Key: CodeOf((InputKey)Code); break;
            case BindingKind.MouseButton: CodeOf((MouseButton)Code); break;
            case BindingKind.JoystickButton: CodeOf((JoystickButton)Code); break;
            case BindingKind.JoystickAxis: CodeOf((JoystickAxis)Code); break;
            case BindingKind.GamepadButton: CodeOf((GamepadButton)Code); break;
            case BindingKind.GamepadAxis: CodeOf((GamepadAxis)Code); break;
            case BindingKind.GamepadStick: CodeOf((GamepadStick)Code); break;
            case BindingKind.MouseAxis: CodeOf((MouseAxis)Code); break;
        }
        bool valid = shape switch
        {
            BindingShape.Button => Kind is BindingKind.Key or BindingKind.MouseButton or BindingKind.JoystickButton
                or BindingKind.JoystickAxis or BindingKind.GamepadButton or BindingKind.GamepadAxis,
            BindingShape.Float => Kind is BindingKind.Key or BindingKind.MouseButton or BindingKind.JoystickButton
                or BindingKind.JoystickAxis or BindingKind.GamepadButton or BindingKind.GamepadAxis or BindingKind.Digital or BindingKind.MouseAxis or BindingKind.ButtonValue,
            _ => Kind is BindingKind.FourButtons or BindingKind.AxisPair or BindingKind.GamepadStick or BindingKind.MouseMotion
        };
        if (!valid) throw new ArgumentException($"{Kind} is not a {shape} binding.");
        int count = Kind == BindingKind.FourButtons ? 4 : Kind is BindingKind.Digital or BindingKind.AxisPair ? 2 : Kind == BindingKind.ButtonValue ? 1 : 0;
        if ((Parts?.Length ?? 0) != count) throw new ArgumentException("Invalid composite components.");
        if (count > 0)
        {
            foreach (var part in Parts!)
            {
                if (part is null) throw new ArgumentException("A composite component is missing.");
                part.Validate(Kind == BindingKind.AxisPair ? BindingShape.Float : BindingShape.Button, depth + 1);
            }
            if (Kind == BindingKind.AxisPair && Parts![0].Mode != Parts[1].Mode)
                throw new ArgumentException("Axis pair components must use the same value mode.");
        }
    }

    internal int PartIndex(InputBindingPart part) => (Kind, part) switch
    {
        (BindingKind.Digital, InputBindingPart.Negative) => 0,
        (BindingKind.Digital, InputBindingPart.Positive) => 1,
        (BindingKind.FourButtons, InputBindingPart.Left) => 0,
        (BindingKind.FourButtons, InputBindingPart.Right) => 1,
        (BindingKind.FourButtons, InputBindingPart.Down) => 2,
        (BindingKind.FourButtons, InputBindingPart.Up) => 3,
        (BindingKind.AxisPair, InputBindingPart.X) => 0,
        (BindingKind.AxisPair, InputBindingPart.Y) => 1,
        _ => throw new ArgumentException("The binding does not have that component.", nameof(part))
    };
}
