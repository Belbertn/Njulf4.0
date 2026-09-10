using System.Reflection;
using Njulf.Input;
using NUnit.Framework;
using Silk.NET.Input;
using MouseButton = Njulf.Input.MouseButton;
using NativeButton = Silk.NET.Input.MouseButton;

namespace Njulf.Tests;

[TestFixture]
public sealed class InputActionTests
{
    [Test]
    public void CombinedBindings_PublishOneEdgePerUpdate()
    {
        var (keyboard, state) = InputDeviceProxy.Create<IKeyboard>();
        var keys = new HashSet<Key>();
        state.Methods["IsKeyPressed"] = args => keys.Contains((Key)args[0]!);
        using var input = new InputManager(new TestInputContext([keyboard]));
        var jump = input.CreateAction("Jump");
        jump.AddBinding(new InputBinding(InputKey.Space));
        jump.AddBinding(new InputBinding(InputKey.Enter));
        int pressed = 0, released = 0;
        jump.Pressed += () => pressed++;
        input.ActionReleased += action => { Assert.That(action, Is.SameAs(jump)); released++; };
        input.Update();
        AssertState(jump, false, false, false);
        keys.Add(Key.Space); input.Update();
        AssertState(jump, true, true, false);
        input.Update(); AssertState(jump, true, false, false);
        keys.Clear(); keys.Add(Key.Enter); input.Update();
        AssertState(jump, true, false, false);
        keys.Clear(); input.Update(); AssertState(jump, false, false, true);
        input.Update(); AssertState(jump, false, false, false);
        Assert.That((pressed, released), Is.EqualTo((1, 1)));
    }

    [Test]
    public void Callbacks_SeeTheCompleteSnapshot_AndCannotNestUpdates()
    {
        var (keyboard, state) = InputDeviceProxy.Create<IKeyboard>();
        state.Methods["IsKeyPressed"] = _ => true;
        using var input = new InputManager(new TestInputContext([keyboard]));
        var first = input.CreateAction("First");
        var second = input.CreateAction("Second");
        first.AddBinding(new InputBinding(InputKey.Space));
        second.AddBinding(new InputBinding(InputKey.Space));
        first.Pressed += () =>
        {
            Assert.That(second.WasPressed, Is.True);
            Assert.That(() => input.Update(), Throws.InvalidOperationException);
        };
        input.Update();
    }

    [Test]
    public void MouseEdgesAndScroll_ArePublishedOnce_AndMotionRemainsConsumable()
    {
        var (mouse, state) = InputDeviceProxy.Create<IMouse>();
        bool held = false;
        state.Methods["IsButtonPressed"] = args => held && (NativeButton)args[0]! == NativeButton.Left;
        using var input = new InputManager(new TestInputContext(mice: [mouse]));
        input.Initialize();
        state.Raise("MouseMove", mouse, new System.Numerics.Vector2(10, 20));
        state.Raise("MouseMove", mouse, new System.Numerics.Vector2(14, 23));
        state.Raise("Scroll", mouse, new ScrollWheel(0, 2));
        held = true; input.Update();
        Assert.Multiple(() =>
        {
            Assert.That(input.IsMouseButtonDown(MouseButton.Left), Is.True);
            Assert.That(input.IsMouseButtonPressed(MouseButton.Left), Is.True);
            Assert.That(input.IsMouseButtonReleased(MouseButton.Left), Is.False);
            Assert.That(input.MouseScrollDelta, Is.EqualTo(2));
        });
        input.Update();
        Assert.That(input.IsMouseButtonPressed(MouseButton.Left), Is.False);
        Assert.That(input.MouseScrollDelta, Is.Zero);
        held = false; input.Update();
        Assert.That(input.IsMouseButtonReleased(MouseButton.Left), Is.True);
        input.Update(); Assert.That(input.IsMouseButtonReleased(MouseButton.Left), Is.False);
        Assert.That(input.ConsumeMouseDelta(), Is.EqualTo(new Njulf.Core.Math.Vector2(4, 3)));
        Assert.That(input.ConsumeMouseDelta(), Is.EqualTo(Njulf.Core.Math.Vector2.Zero));
    }

    [Test]
    public void JoystickButtonsAndAxes_AreDistinct_AndUnavailableDevicesAreInactive()
    {
        var (joystick, state) = InputDeviceProxy.Create<IJoystick>();
        var buttons = new List<Button> { new((ButtonName)0, 0, true) };
        var axes = new List<Axis> { new(0, 0) };
        state.Methods["get_Buttons"] = _ => buttons;
        state.Methods["get_Axes"] = _ => axes;
        using var input = new InputManager(new TestInputContext(joysticks: [joystick]));
        var button = input.CreateAction("Button"); button.AddBinding(new InputBinding(JoystickButton.Button0));
        var axis = input.CreateAction("Axis"); axis.AddBinding(new InputBinding(JoystickAxis.X, isNegative: true));
        var missing = input.CreateAction("Missing"); missing.AddBinding(new InputBinding(JoystickButton.Button0, 3));
        input.Update();
        Assert.That(button.IsDown, Is.True); Assert.That(axis.IsDown, Is.False); Assert.That(missing.IsDown, Is.False);
        buttons[0] = new((ButtonName)0, 0, false); axes[0] = new(0, -0.5f); input.Update();
        Assert.That(button.WasReleased, Is.True); Assert.That(axis.IsDown, Is.False);
        axes[0] = new(0, -0.6f); input.Update(); Assert.That(axis.WasPressed, Is.True);
    }

    [Test]
    public void RegistrationValidationAndShutdown_ArePredictable()
    {
        var (keyboard, state) = InputDeviceProxy.Create<IKeyboard>();
        state.Methods["IsKeyPressed"] = _ => false;
        var input = new InputManager(new TestInputContext([keyboard]));
        var action = input.CreateAction("Jump");
        Assert.That(input.GetAction("Jump"), Is.SameAs(action));
        Assert.That(input.GetAction("jump"), Is.Null);
        Assert.That(() => input.CreateAction("Jump"), Throws.ArgumentException);
        Assert.That(() => new InputBinding((InputKey)int.MaxValue), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new InputBinding(MouseButton.Left, -1), Throws.TypeOf<ArgumentOutOfRangeException>());
        input.Initialize(); input.Dispose(); input.Dispose();
        Assert.That(state.SubscriptionCount, Is.Zero);
        Assert.That(() => action.IsDown, Throws.TypeOf<ObjectDisposedException>());
        Assert.That(() => action.AddBinding(new InputBinding(InputKey.Space)), Throws.TypeOf<ObjectDisposedException>());
    }

    private static void AssertState(InputAction action, bool down, bool pressed, bool released) =>
        Assert.That((action.IsDown, action.WasPressed, action.WasReleased), Is.EqualTo((down, pressed, released)));
}

// Only device calls used by the public input integration are implemented; unexpected calls fail the test.
public class InputDeviceProxy : DispatchProxy
{
    internal readonly Dictionary<string, Func<object?[], object?>> Methods = new();
    private readonly Dictionary<string, Delegate?> _events = new();
    internal int SubscriptionCount => _events.Values.Count(value => value != null);
    internal static (T, InputDeviceProxy) Create<T>() where T : class
    {
        T device = Create<T, InputDeviceProxy>();
        var proxy = (InputDeviceProxy)(object)device;
        if (typeof(IInputDevice).IsAssignableFrom(typeof(T)))
        {
            Deadzone deadzone = new(.2f, DeadzoneMethod.Traditional);
            proxy.Methods["get_Deadzone"] = _ => deadzone;
            proxy.Methods["set_Deadzone"] = args => { deadzone = (Deadzone)args[0]!; return null; };
            proxy.Methods["get_Index"] = _ => 0;
        }
        return (device, proxy);
    }
    internal void Raise(string name, params object?[] arguments) => _events.GetValueOrDefault(name)?.DynamicInvoke(arguments);
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        string name = method!.Name;
        if (name.StartsWith("add_") || name.StartsWith("remove_"))
        {
            string eventName = name[(name.IndexOf('_') + 1)..];
            _events[eventName] = name.StartsWith("add_")
                ? Delegate.Combine(_events.GetValueOrDefault(eventName), (Delegate?)args![0])
                : Delegate.Remove(_events.GetValueOrDefault(eventName), (Delegate?)args![0]);
            return null;
        }
        return Methods.TryGetValue(name, out var invoke) ? invoke(args ?? []) : throw new NotSupportedException(name);
    }
}

internal sealed class TestInputContext(IReadOnlyList<IKeyboard>? keyboards = null,
    IReadOnlyList<IMouse>? mice = null, IReadOnlyList<IJoystick>? joysticks = null, IReadOnlyList<IGamepad>? gamepads = null) : IInputContext
{
    public IntPtr Handle => IntPtr.Zero;
    public IReadOnlyList<IKeyboard> Keyboards => keyboards ?? [];
    public IReadOnlyList<IMouse> Mice => mice ?? [];
    public IReadOnlyList<IJoystick> Joysticks => joysticks ?? [];
    public IReadOnlyList<IGamepad> Gamepads => gamepads ?? [];
    public IReadOnlyList<IInputDevice> OtherDevices => [];
    public event Action<IInputDevice, bool>? ConnectionChanged;
    internal void Connect(IInputDevice device, bool connected) => ConnectionChanged?.Invoke(device, connected);
    internal int SubscriptionCount => ConnectionChanged?.GetInvocationList().Length ?? 0;
    public void Dispose() { }
}
