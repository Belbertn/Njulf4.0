using System.Text.Json.Nodes;
using Njulf.Input;
using NUnit.Framework;
using Silk.NET.Input;

namespace Njulf.Tests;

[TestFixture]
public sealed class InputPersistenceTests
{
    private string _directory = null!;
    private string BindingPath => Path.Combine(_directory, "bindings.json");
    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(AppContext.BaseDirectory, "input-test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }
    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void RoundTrip_PreservesCompositeProcessingAndExplicitUnbinding()
    {
        using var rig = new InputTestRig();
        var game = rig.Input.CreateContext("Game");
        var move = rig.Input.CreateVector2Action("Move", game);
        move.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D), new InputBinding(InputKey.S), new InputBinding(InputKey.W), scale: .5f));
        move.AddBinding(new(GamepadStick.Right, deadZone: .25f));
        var look = rig.Input.CreateVector2Action("Look", mode: InputValueMode.Delta);
        look.AddBinding(new(new InputFloatBinding(MouseAxis.X, scale: .2f), new InputFloatBinding(MouseAxis.Y, scale: -.3f)));
        var trigger = rig.Input.CreateFloatAction("Trigger"); trigger.AddBinding(new(GamepadAxis.RightTrigger, deadZone: .1f, scale: .5f));
        var empty = rig.Input.CreateAction("Unbound");
        var axisButton = rig.Input.CreateAction("AxisButton"); axisButton.AddBinding(new(JoystickAxis.X, isNegative: true));
        rig.Input.SaveBindings(BindingPath);
        move.ClearBindings(); look.ClearBindings(); trigger.ClearBindings(); axisButton.ClearBindings(); empty.AddBinding(new(InputKey.Space));
        rig.Input.ActiveContext = game;
        Assert.That(rig.Input.LoadBindings(BindingPath), Is.True); Assert.That(empty.Bindings, Is.Empty);
        Assert.That(move.Context, Is.SameAs(game)); Assert.That(rig.Input.ActiveContext, Is.SameAs(game));
        rig.Input.Update(); rig.Keys.Add(Key.W); rig.Move(0, 0); rig.Move(10, 20); rig.Triggers[1] = new(1, 1); rig.Axes[0] = new(0, -.8f); rig.Input.Update();
        InputGameplayTests.AssertVector(move.Value, new(0, .5f)); InputGameplayTests.AssertVector(look.Value, new(2, -6));
        Assert.That(trigger.Value, Is.EqualTo(.5f)); Assert.That(axisButton.IsDown, Is.True);
        rig.Input.SaveBindings(BindingPath); Assert.That(Directory.GetFiles(_directory).Length, Is.EqualTo(1));
    }

    [Test]
    public void MissingFilesAndUnknownActions_LeaveRegisteredDefaultsUsable()
    {
        using var rig = new InputTestRig();
        var action = rig.Input.CreateAction("Jump"); action.AddBinding(new(InputKey.Space));
        Assert.That(rig.Input.LoadBindings(BindingPath), Is.False);
        rig.Input.SaveBindings(BindingPath);
        JsonNode document = JsonNode.Parse(File.ReadAllText(BindingPath))!;
        document["Actions"]!["RemovedAction"] = JsonValue.Create("unrecognized future payload");
        File.WriteAllText(BindingPath, document.ToJsonString()); Assert.That(rig.Input.LoadBindings(BindingPath), Is.True);
        rig.Input.Update(); rig.Keys.Add(Key.Space); rig.Input.Update(); Assert.That(action.WasPressed, Is.True);
    }

    [TestCase("bad-binding")]
    [TestCase("wrong-type")]
    [TestCase("wrong-mode")]
    [TestCase("version")]
    [TestCase("malformed")]
    [TestCase("missing-kind")]
    [TestCase("missing-type")]
    public void InvalidLoads_AreAtomic(string fault)
    {
        using var rig = new InputTestRig();
        var first = rig.Input.CreateAction("First"); first.AddBinding(new(InputKey.Space));
        var second = rig.Input.CreateFloatAction("Second"); second.AddBinding(new(GamepadAxis.LeftX));
        rig.Input.SaveBindings(BindingPath);
        first.ReplaceBinding(0, new(InputKey.B)); second.ReplaceBinding(0, new(GamepadAxis.RightX));
        JsonNode document = JsonNode.Parse(File.ReadAllText(BindingPath))!;
        switch (fault)
        {
            case "bad-binding": document["Actions"]!["Second"]!["Bindings"]![0]!["DeadZone"] = 1; break;
            case "wrong-type": document["Actions"]!["Second"]!["Type"] = "Button"; break;
            case "wrong-mode": document["Actions"]!["Second"]!["Mode"] = "Delta"; break;
            case "version": document["Version"] = 99; break;
            case "missing-kind": document["Actions"]!["Second"]!["Bindings"]![0]!.AsObject().Remove("Kind"); break;
            case "missing-type": document["Actions"]!["Second"]!.AsObject().Remove("Type"); break;
        }
        File.WriteAllText(BindingPath, fault == "malformed" ? "{" : document.ToJsonString());
        Assert.That(() => rig.Input.LoadBindings(BindingPath), Throws.Exception);
        rig.Keys.Add(Key.B); rig.Sticks[1] = new(1, 1, 0); rig.Input.Update();
        Assert.That(first.WasPressed, Is.True); Assert.That(second.Value, Is.EqualTo(1));
    }
}
