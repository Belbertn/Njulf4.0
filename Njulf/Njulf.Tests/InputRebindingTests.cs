using Njulf.Input;
using NUnit.Framework;
using Silk.NET.Input;

namespace Njulf.Tests;

[TestFixture]
public sealed class InputRebindingTests
{
    [Test]
    public void Capture_IgnoresHeldControlsAndSuppressesActionsAndPublicText()
    {
        using var rig = new InputTestRig();
        var target = rig.Input.CreateAction("Jump"); target.AddBinding(new(InputKey.Space));
        var global = rig.Input.CreateAction("Global"); global.AddBinding(new(InputKey.B));
        string text = "", raw = ""; rig.Input.TextInput += c => text += c; rig.Input.RawTextInput += c => raw += c;
        rig.Keys.Add(Key.A); rig.Input.Update();
        using var session = target.BeginRebind(0);
        Assert.That(() => global.BeginRebind(0), Throws.InvalidOperationException);
        rig.Input.Update(); Assert.That(session.Status, Is.EqualTo(InputRebindStatus.Pending));
        rig.Text('b'); rig.Keys.Add(Key.B); rig.Input.Update();
        Assert.That(session.Status, Is.EqualTo(InputRebindStatus.Completed));
        Assert.That(target.Bindings[0].InputCode, Is.EqualTo((int)InputKey.B));
        Assert.That(global.IsDown || target.IsDown, Is.False);
        rig.Input.Update(); Assert.That(global.IsDown || target.IsDown, Is.False);
        rig.Keys.Clear(); rig.Input.Update(); rig.Keys.Add(Key.B); rig.Input.Update();
        Assert.That(global.WasPressed && target.WasPressed, Is.True);
        Assert.That(text, Is.Empty); Assert.That(raw, Is.EqualTo("b"));
    }

    [Test]
    public void CancellationAndTargetChanges_DoNotOverwriteBindings()
    {
        using var rig = new InputTestRig();
        var action = rig.Input.CreateAction("Jump"); var original = new InputBinding(InputKey.Space); action.AddBinding(original);
        using (var capture = action.BeginRebind(0))
        {
            rig.Keys.Add(Key.Escape); rig.Input.Update();
            Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Cancelled));
            Assert.That(action.Bindings[0], Is.SameAs(original));
        }
        rig.Keys.Clear(); rig.Input.Update();
        using (var capture = action.BeginRebind(0, cancelKey: null))
        {
            rig.Keys.Add(Key.Escape); rig.Input.Update();
            Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Completed));
            Assert.That(action.Bindings[0].InputCode, Is.EqualTo((int)InputKey.Escape));
        }
        rig.Keys.Clear(); rig.Input.Update();
        using (var capture = action.BeginRebind(0))
        {
            action.ReplaceBinding(0, original); rig.Keys.Add(Key.B); rig.Input.Update();
            Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Cancelled));
            Assert.That(action.Bindings[0], Is.SameAs(original));
        }
        using var focusCapture = action.BeginRebind(0);
        rig.Input.SetFocused(false); Assert.That(focusCapture.Status, Is.EqualTo(InputRebindStatus.Cancelled));
    }

    [Test]
    public void CompositeCapture_ReplacesOnlySelectedDirection()
    {
        using var rig = new InputTestRig();
        var move = rig.Input.CreateVector2Action("Move");
        move.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D), new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
        using var capture = move.BeginRebind(0, InputBindingPart.Up);
        rig.Keys.Add(Key.I); rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Completed));
        rig.Keys.Clear(); rig.Input.Update(); rig.Keys.UnionWith([Key.I, Key.D]); rig.Input.Update();
        InputGameplayTests.AssertVector(move.Value, new(MathF.Sqrt(.5f), MathF.Sqrt(.5f)));
        rig.Keys.Clear(); rig.Keys.Add(Key.W); rig.Input.Update(); InputGameplayTests.AssertVector(move.Value, default);
        Assert.That(() => move.BeginRebind(0, InputBindingPart.Positive), Throws.ArgumentException);
    }

    [Test]
    public void AnalogCapture_WaitsForNeutralAndUsesStandardGamepadControls()
    {
        using var rig = new InputTestRig();
        rig.JoystickState.Methods["get_Index"] = _ => 0; // Same physical pad appears in both native families.
        var move = rig.Input.CreateVector2Action("Move"); move.AddBinding(new(GamepadStick.Right));
        rig.Sticks[0] = new(0, .7f, 0);
        using var capture = move.BeginRebind(0);
        rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Pending));
        rig.Sticks[0] = new(0, .1f, 0); rig.Input.Update();
        rig.Sticks[0] = new(0, .51f, 0); rig.Axes[0] = new(0, .8f); rig.Input.Update();
        Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Completed));
        rig.Sticks[0] = new(0, 0, 0); rig.Input.Update();
        rig.Sticks[0] = new(0, 1, 0); rig.Input.Update(); InputGameplayTests.AssertVector(move.Value, new(1, 0));
        rig.Sticks[0] = new(0, 0, 0); rig.Input.Update(); InputGameplayTests.AssertVector(move.Value, default);
    }

    [Test]
    public void ButtonAxisCapture_DoesNotCaptureInitiallyHeldNegativeDirection()
    {
        using var rig = new InputTestRig();
        var action = rig.Input.CreateAction("Left"); action.AddBinding(new(InputKey.A));
        rig.Sticks[0] = new(0, -.9f, 0);
        using var capture = action.BeginRebind(0);
        rig.Input.Update(); rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Pending));
        rig.Sticks[0] = new(0, 0, 0); rig.Input.Update(); rig.Sticks[0] = new(0, -.9f, 0); rig.Input.Update();
        Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Completed));
        Assert.That(action.Bindings[0].DeviceType, Is.EqualTo(BindingDeviceType.Gamepad));
        Assert.That(action.Bindings[0].IsNegative, Is.True);
    }

    [Test]
    public void DeltaCapture_FiltersModesAndAccumulatesMouseThreshold()
    {
        using var rig = new InputTestRig();
        var look = rig.Input.CreateVector2Action("Look", mode: InputValueMode.Delta);
        look.AddBinding(InputVector2Binding.MouseMotion(scale: .25f));
        rig.Move(0, 0); rig.Move(100, 100);
        using var capture = look.BeginRebind(0);
        rig.Keys.Add(Key.B); rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Pending));
        rig.Move(104, 100); rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Pending));
        rig.Move(108, 100); rig.Input.Update(); Assert.That(capture.Status, Is.EqualTo(InputRebindStatus.Completed));
        InputGameplayTests.AssertVector(look.Value, default);
        rig.Move(120, 100); rig.Input.Update(); InputGameplayTests.AssertVector(look.Value, new(3, 0));
        var wheel = rig.Input.CreateFloatAction("Wheel", mode: InputValueMode.Delta); wheel.AddBinding(new(MouseAxis.X));
        using var wheelCapture = wheel.BeginRebind(0);
        rig.Wheel(0, -1); rig.Input.Update(); Assert.That(wheelCapture.Status, Is.EqualTo(InputRebindStatus.Completed));
        rig.Wheel(0, 2); rig.Input.Update(); Assert.That(wheel.Value, Is.EqualTo(2));
    }
}
