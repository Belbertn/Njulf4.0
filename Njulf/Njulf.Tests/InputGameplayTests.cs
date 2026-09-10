using Njulf.Core.Math;
using Njulf.Input;
using NUnit.Framework;
using Silk.NET.Input;
using MouseButton = Njulf.Input.MouseButton;
using NativeCursorMode = Silk.NET.Input.CursorMode;

namespace Njulf.Tests;

[TestFixture]
public sealed class InputGameplayTests
{
    [Test]
    public void DigitalComposites_CancelNormalizeAndSelectStrongestAlternative()
    {
        using var rig = new InputTestRig();
        var axis = rig.Input.CreateFloatAction("Horizontal");
        axis.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D)));
        axis.AddBinding(new(GamepadAxis.LeftX, deadZone: 0));
        var move = rig.Input.CreateVector2Action("Move");
        move.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D), new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
        rig.Keys.UnionWith([Key.D, Key.W]); rig.Sticks[0] = new(0, -.5f, 0); rig.Input.Update();
        Assert.That(axis.Value, Is.EqualTo(1));
        AssertVector(move.Value, new(MathF.Sqrt(.5f), MathF.Sqrt(.5f)));
        rig.Keys.Add(Key.A); rig.Input.Update();
        Assert.That(axis.Value, Is.EqualTo(-.5f)); AssertVector(move.Value, Vector2.UnitY);
        rig.Keys.Add(Key.S); rig.Input.Update(); AssertVector(move.Value, Vector2.Zero);
        rig.Keys.Clear(); rig.Keys.Add(Key.D); rig.Sticks[0] = new(0, -1, 0); rig.Input.Update();
        Assert.That(axis.Value, Is.EqualTo(1), "First alternative wins ties.");
    }

    [Test]
    public void ScalarAndRadialDeadZones_RescaleAndRespectTriggerAndStickConventions()
    {
        using var rig = new InputTestRig();
        var trigger = rig.Input.CreateFloatAction("Throttle"); trigger.AddBinding(new(GamepadAxis.LeftTrigger));
        var x = rig.Input.CreateFloatAction("X"); x.AddBinding(new(GamepadAxis.LeftX, deadZone: .2f));
        var move = rig.Input.CreateVector2Action("Move"); move.AddBinding(new(GamepadStick.Left, deadZone: .2f));
        rig.Sticks[0] = new(0, .2f, 0); rig.Input.Update();
        Assert.That(trigger.Value, Is.Zero); Assert.That(x.Value, Is.Zero); AssertVector(move.Value, default);
        rig.Triggers[0] = new(0, 0); rig.Sticks[0] = new(0, .6f, -.8f); rig.Input.Update();
        Assert.That(trigger.Value, Is.EqualTo(.45f / .95f).Within(.00001));
        Assert.That(x.Value, Is.EqualTo(.5f).Within(.00001)); AssertVector(move.Value, new(.6f, .8f));
        rig.Triggers[0] = new(0, 1); rig.Sticks[0] = new(0, -.6f, 0); rig.Input.Update();
        Assert.That(trigger.Value, Is.EqualTo(1)); Assert.That(x.Value, Is.EqualTo(-.5f).Within(.00001));
        rig.Triggers[0] = new(0, float.NaN); rig.Input.Update(); Assert.That(trigger.Value, Is.Zero);
        Assert.That(rig.Pad.Deadzone.Value, Is.Zero, "Backend processing must not apply a second dead zone.");
    }

    [Test]
    public void AxisPairAndButtonScalar_ReadRealSources()
    {
        using var rig = new InputTestRig();
        var pair = rig.Input.CreateVector2Action("Joystick");
        pair.AddBinding(new(new InputFloatBinding(JoystickAxis.X, deadZone: 0), new InputFloatBinding(JoystickAxis.Y, deadZone: 0, scale: -1), deadZone: .2f));
        var scalar = rig.Input.CreateFloatAction("Button"); scalar.AddBinding(new(new InputBinding(JoystickAxis.X), scale: -.4f));
        rig.Axes[0] = new(0, .6f); rig.Axes[1] = new(1, 0); rig.Input.Update();
        AssertVector(pair.Value, new(.5f, 0)); Assert.That(scalar.Value, Is.EqualTo(-.4f));
        rig.Axes[0] = new(0, .5f); rig.Input.Update(); Assert.That(scalar.Value, Is.Zero);
    }

    [Test]
    public void MouseDeltaActions_PublishOnceWithoutConsumingLegacyMotion()
    {
        using var rig = new InputTestRig();
        IInputManager input = rig.Input;
        var look = input.CreateVector2Action("Look", mode: InputValueMode.Delta);
        look.AddBinding(InputVector2Binding.MouseMotion(scale: 2));
        var wheel = input.CreateFloatAction("Wheel", mode: InputValueMode.Delta);
        wheel.AddBinding(new(MouseAxis.WheelY)); wheel.AddBinding(new(MouseAxis.WheelX, scale: -2));
        rig.Move(10, 20); rig.Move(14, 23); rig.Wheel(1, 3); input.Update();
        AssertVector(look.Value, new(8, 6)); Assert.That(wheel.Value, Is.EqualTo(1));
        AssertVector(input.ConsumeMouseDelta(), new(4, 3)); AssertVector(look.Value, new(8, 6));
        input.Update(); AssertVector(look.Value, default); Assert.That(wheel.Value, Is.Zero);
        Assert.That(() => look.AddBinding(new(GamepadStick.Left)), Throws.ArgumentException);
        Assert.That(() => input.CreateFloatAction("Look"), Throws.ArgumentException);
        Assert.That(() => new InputFloatBinding(GamepadAxis.LeftX, deadZone: 1), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new InputVector2Binding(GamepadStick.Left, scale: float.NaN), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Contexts_SwitchAtSnapshotBoundaryAndGateHeldInput()
    {
        using var rig = new InputTestRig();
        var game = rig.Input.CreateContext("Gameplay"); var menu = rig.Input.CreateContext("Menu");
        var jump = rig.Input.CreateAction("Jump", game); jump.AddBinding(new(InputKey.Space));
        var accept = rig.Input.CreateAction("Accept", menu); accept.AddBinding(new(InputKey.Space));
        var move = rig.Input.CreateFloatAction("Move", game); move.AddBinding(new(new InputBinding(InputKey.W)));
        var toggle = rig.Input.CreateAction("Toggle"); toggle.AddBinding(new(InputKey.Escape));
        int released = 0; jump.Released += () => released++;
        toggle.Pressed += () => rig.Input.ActiveContext = menu;
        rig.Input.ActiveContext = game; rig.Input.Update();
        rig.Keys.UnionWith([Key.Space, Key.W, Key.Escape]); rig.Input.Update();
        Assert.That(jump.WasPressed, Is.True); Assert.That(move.Value, Is.EqualTo(1));
        Assert.That(accept.IsDown, Is.False); rig.Input.Update();
        Assert.That(jump.WasReleased, Is.True); Assert.That(move.Value, Is.Zero); Assert.That(accept.IsDown, Is.False);
        rig.Input.Update(); Assert.That(released, Is.EqualTo(1));
        rig.Keys.Clear(); rig.Input.Update(); rig.Keys.Add(Key.Space); rig.Input.Update(); Assert.That(accept.WasPressed, Is.True);
        using var other = new InputManager(new TestInputContext());
        Assert.That(() => other.ActiveContext = game, Throws.ArgumentException);
    }

    [Test]
    public void GamepadDisconnect_ReleasesWithoutRenumberingAndRestoresBorrowedSettings()
    {
        using var rig = new InputTestRig();
        var press = rig.Input.CreateAction("Press"); press.AddBinding(new(GamepadButton.A));
        var trigger = rig.Input.CreateAction("Trigger"); trigger.AddBinding(new(GamepadAxis.RightTrigger));
        // Native list order is deliberately different from enum order.
        rig.Buttons.Add(new(ButtonName.B, 77, false)); rig.Buttons.Add(new(ButtonName.A, 13, true));
        rig.Triggers[1] = new(1, 1); rig.Input.Update(); Assert.That(press.IsDown && trigger.IsDown, Is.True);
        rig.Context.Connect(rig.Pad, false); rig.Input.Update();
        Assert.That(press.WasReleased && trigger.WasReleased, Is.True);
        rig.Input.Update(); Assert.That(press.WasReleased, Is.False);
        rig.Context.Connect(rig.Pad, true); rig.Input.Update(); Assert.That(press.WasPressed, Is.True);
        rig.Input.Dispose();
        Assert.That(rig.Pad.Deadzone.Value, Is.EqualTo(.2f));
        Assert.That(rig.Joystick.Deadzone.Value, Is.EqualTo(.2f));
        Assert.That(rig.Context.SubscriptionCount, Is.Zero);
        Assert.That(rig.KeyboardState.SubscriptionCount + rig.MouseState.SubscriptionCount, Is.Zero);
        Assert.That(() => trigger.IsDown, Throws.TypeOf<ObjectDisposedException>());
    }

    [Test]
    public void CursorTextAndFocus_WorkThroughOrdinaryInterface()
    {
        using var rig = new InputTestRig();
        IInputManager input = rig.Input;
        string text = "", raw = ""; input.TextInput += c => text += c; rig.Input.RawTextInput += c => raw += c;
        var action = input.CreateAction("Held"); action.AddBinding(new(InputKey.A));
        input.SetCursorMode(InputCursorMode.Captured);
        Assert.That(rig.NativeCursor, Is.EqualTo(NativeCursorMode.Disabled), "Unsupported raw motion falls back to relative capture.");
        rig.RawCursorSupported = true; input.SetCursorMode(InputCursorMode.Captured);
        Assert.That(rig.NativeCursor, Is.EqualTo(NativeCursorMode.Raw));
        rig.Keys.Add(Key.A); input.Update(); Assert.That(action.IsDown, Is.True);
        rig.Text('ø'); rig.Input.SetFocused(false); rig.Text('x'); input.Update();
        Assert.That(action.WasReleased, Is.True); Assert.That(rig.NativeCursor, Is.EqualTo(NativeCursorMode.Normal));
        rig.Move(50, 50); rig.Input.SetFocused(true); rig.Move(900, 900); input.Update();
        AssertVector(input.ConsumeMouseDelta(), default); Assert.That(action.IsDown, Is.False);
        Assert.That(input.CursorMode, Is.EqualTo(InputCursorMode.Captured)); Assert.That(rig.NativeCursor, Is.EqualTo(NativeCursorMode.Raw));
        rig.Keys.Clear(); input.Update(); rig.Keys.Add(Key.A); input.Update(); Assert.That(action.WasPressed, Is.True);
        Assert.That(text, Is.EqualTo("ø")); Assert.That(raw, Is.EqualTo("øx"));
    }

    internal static void AssertVector(Vector2 actual, Vector2 expected)
    {
        Assert.That(actual.X, Is.EqualTo(expected.X).Within(.00001));
        Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(.00001));
    }

    [Test]
    public void HotPlug_NewDevicesKeepExistingSlotsAndRestoreRequestedCursor()
    {
        using var rig = new InputTestRig();
        var (second, state) = InputDeviceProxy.Create<IGamepad>();
        state.Methods["get_Buttons"] = _ => new Button[] { new(ButtonName.A, 0, true) };
        var firstAction = rig.Input.CreateAction("First"); firstAction.AddBinding(new(GamepadButton.A));
        var secondAction = rig.Input.CreateAction("Second"); secondAction.AddBinding(new(GamepadButton.A, 1));
        rig.Context.Connect(second, true); rig.Input.Update(); Assert.That(secondAction.IsDown, Is.True);
        rig.Context.Connect(rig.Pad, false); rig.Input.Update();
        Assert.That(firstAction.IsDown, Is.False); Assert.That(secondAction.IsDown, Is.True);
        rig.Input.SetCursorMode(InputCursorMode.Captured); rig.Context.Connect(rig.Mouse, false);
        rig.NativeCursor = NativeCursorMode.Normal; rig.Context.Connect(rig.Mouse, true);
        Assert.That(rig.NativeCursor, Is.EqualTo(NativeCursorMode.Disabled));
        rig.Input.Dispose(); Assert.That(second.Deadzone.Value, Is.EqualTo(.2f));
    }

    [Test]
    public void InactiveContextDelta_IsDiscardedAndValueActionsRespectLifetimeAndThread()
    {
        using var rig = new InputTestRig();
        var gameplay = rig.Input.CreateContext("Gameplay");
        var look = rig.Input.CreateVector2Action("Look", gameplay, InputValueMode.Delta);
        look.AddBinding(InputVector2Binding.MouseMotion());
        rig.Move(0, 0); rig.Move(30, 20); rig.Input.Update(); AssertVector(look.Value, default);
        rig.Input.ActiveContext = gameplay; rig.Input.Update(); AssertVector(look.Value, default);
        rig.Move(32, 23); rig.Input.Update(); AssertVector(look.Value, new(2, 3));
        Assert.That(rig.Input.GetVector2Action("Look"), Is.SameAs(look)); Assert.That(rig.Input.GetFloatAction("Look"), Is.Null);
        Exception? error = null;
        var thread = new Thread(() => { try { _ = look.Value; } catch (Exception e) { error = e; } });
        thread.Start(); thread.Join(); Assert.That(error, Is.TypeOf<InvalidOperationException>());
        rig.Input.Dispose(); Assert.That(() => look.Value, Throws.TypeOf<ObjectDisposedException>());
    }
}

internal sealed class InputTestRig : IDisposable
{
    internal readonly HashSet<Key> Keys = [];
    internal readonly HashSet<Silk.NET.Input.MouseButton> MouseButtons = [];
    internal readonly List<Thumbstick> Sticks = [new(0, 0, 0), new(1, 0, 0)];
    internal readonly List<Trigger> Triggers = [new(0, -1), new(1, -1)];
    internal readonly List<Button> Buttons = [];
    internal readonly List<Axis> Axes = [new(0, 0), new(1, 0)];
    internal readonly IKeyboard Keyboard;
    internal readonly IMouse Mouse;
    internal readonly IGamepad Pad;
    internal readonly IJoystick Joystick;
    internal readonly InputDeviceProxy KeyboardState, MouseState, JoystickState;
    internal readonly TestInputContext Context;
    internal readonly InputManager Input;
    internal NativeCursorMode NativeCursor;
    internal bool RawCursorSupported;
    internal InputTestRig()
    {
        (Keyboard, KeyboardState) = InputDeviceProxy.Create<IKeyboard>();
        KeyboardState.Methods["IsKeyPressed"] = args => Keys.Contains((Key)args[0]!);
        (Mouse, MouseState) = InputDeviceProxy.Create<IMouse>();
        MouseState.Methods["IsButtonPressed"] = args => MouseButtons.Contains((Silk.NET.Input.MouseButton)args[0]!);
        var (cursor, cursorState) = InputDeviceProxy.Create<ICursor>();
        cursorState.Methods["IsSupported"] = args => (NativeCursorMode)args[0]! != NativeCursorMode.Raw || RawCursorSupported;
        cursorState.Methods["get_CursorMode"] = _ => NativeCursor;
        cursorState.Methods["set_CursorMode"] = args => { NativeCursor = (NativeCursorMode)args[0]!; return null; };
        MouseState.Methods["get_Cursor"] = _ => cursor;
        var (pad, padState) = InputDeviceProxy.Create<IGamepad>(); Pad = pad;
        padState.Methods["get_Thumbsticks"] = _ => Sticks; padState.Methods["get_Triggers"] = _ => Triggers;
        padState.Methods["get_Buttons"] = _ => Buttons;
        (Joystick, JoystickState) = InputDeviceProxy.Create<IJoystick>();
        JoystickState.Methods["get_Axes"] = _ => Axes; JoystickState.Methods["get_Buttons"] = _ => Array.Empty<Button>();
        JoystickState.Methods["get_Index"] = _ => 1;
        Context = new([Keyboard], [Mouse], [Joystick], [Pad]); Input = new(Context); Input.Initialize();
    }
    internal void Move(float x, float y) => MouseState.Raise("MouseMove", Mouse, new System.Numerics.Vector2(x, y));
    internal void Wheel(float x, float y) => MouseState.Raise("Scroll", Mouse, new ScrollWheel(x, y));
    internal void Text(char c) => KeyboardState.Raise("KeyChar", Keyboard, c);
    public void Dispose() => Input.Dispose();
}
