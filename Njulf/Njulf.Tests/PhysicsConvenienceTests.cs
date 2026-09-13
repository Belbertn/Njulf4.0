using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PhysicsConvenienceTests
{
    private static ColliderHandle Register(PhysicsScene world, Vector3 position, ColliderShape shape,
        BodyKind kind = BodyKind.Static, bool trigger = false) => world.Register(Guid.NewGuid(), [shape], new PhysicsPose(position),
            new BodySettings { Kind = kind, IsTrigger = trigger, AffectedByGravity = false });

    [TestCase(false)]
    [TestCase(true)]
    public void ImpactIsCopiedOrientedAndSurvivesRemoval(bool reverseOrder)
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        ColliderHandle floor = default, ball = default;
        if (!reverseOrder) floor = Register(world, new(0, -.5f, 0), ColliderShape.Box(new(10, 1, 10)));
        ball = Register(world, new(0, .49f, 0), ColliderShape.Sphere(.5f), BodyKind.Dynamic);
        if (reverseOrder) floor = Register(world, new(0, -.5f, 0), ColliderShape.Box(new(10, 1, 10)));
        world.SetVelocity(ball, new(0, -3, 0));
        PhysicsContact? saved = null;
        world.Contact += e => { if (e.Began) { saved = e; world.Remove(ball); } };
        world.Step(1f / 60);
        Assert.That(saved.HasValue, Is.True);
        var contact = saved!.Value;
        Assert.That(contact.Details.HasValue, Is.True);
        var details = contact.Details!.Value;
        Assert.That(details.Position.Y, Is.EqualTo(0).Within(.025));
        Assert.That(details.Normal.Y, Is.EqualTo(reverseOrder ? -1 : 1).Within(.001));
        Assert.That(details.ClosingSpeed, Is.EqualTo(3).Within(.05));
        Assert.That(world.IsValid(ball), Is.False);
        Assert.That(world.IsValid(floor), Is.True);
        Assert.That(world.IsValid(default), Is.False);
        PhysicsContact? ended = null;
        world.Contact += e => { if (!e.Began) ended = e; };
        world.Step(1f / 60);
        Assert.That(ended!.Value.Details, Is.Null);
        Assert.That(saved.Value.Details, Is.EqualTo(details));
    }

    [Test]
    public void CompoundImpactSelectsGreatestClosingSpeedIncludingAngularMotion()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Register(world, new(0, -.5f, 0), ColliderShape.Box(new(10, 1, 10)));
        var body = world.Register(Guid.NewGuid(),
            [ColliderShape.Sphere(.25f).WithLocalTransform(Matrix4x4.CreateTranslation(-Vector3.UnitX)),
             ColliderShape.Sphere(.25f).WithLocalTransform(Matrix4x4.CreateTranslation(Vector3.UnitX))],
            new PhysicsPose(new(0, .24f, 0)), new BodySettings { Kind = BodyKind.Dynamic, AffectedByGravity = false });
        world.SetVelocity(body, Vector3.Zero, new(0, 0, 2));
        var contacts = new List<PhysicsContact>(); world.Contact += contacts.Add;
        world.Step(1f / 60);
        Assert.That(contacts.Count(e => e.Began), Is.EqualTo(1));
        var details = contacts.Single(e => e.Began).Details!.Value;
        Assert.That(details.Position.X, Is.GreaterThan(.7f));
        Assert.That(details.ClosingSpeed, Is.EqualTo(2).Within(.1f));
    }

    [Test]
    public void TriggersDeduplicateFilterAndCloseTheirLifetime()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        var trigger = world.Register(Guid.NewGuid(), [ColliderShape.Box(new(2)), ColliderShape.Sphere(1)],
            body: new BodySettings { IsTrigger = true });
        var other = Register(world, Vector3.Zero, ColliderShape.Sphere(.3f));
        var events = new List<PhysicsTrigger>(); world.Trigger += events.Add;
        world.Step(.1f); world.Step(.1f);
        Assert.That(events.Count, Is.EqualTo(1));
        Assert.That(events[0].Entered, Is.True);
        Assert.That(world.Raycast(new(0, 0, -3), Vector3.UnitZ, 2.1f, out _), Is.True);
        Assert.That(world.Raycast(new(0, 0, -3), Vector3.UnitZ, 2.1f, out _, QueryFilter.All with { IncludeTriggers = false }), Is.False);
        world.SetFilter(other, 2, 2); world.Step(.1f);
        Assert.That(events.Count, Is.EqualTo(2)); Assert.That(events[^1].Entered, Is.False);
        world.SetFilter(other, 1, uint.MaxValue); world.Step(.1f);
        Assert.That(events[^1].Entered, Is.True);
        world.SetEnabled(trigger, false); world.Step(.1f);
        Assert.That(events[^1].Entered, Is.False);
        world.SetEnabled(trigger, true); world.Step(.1f);
        world.Remove(other);
        int before = events.Count; world.Step(.1f);
        Assert.That(events.Count, Is.EqualTo(before + 1));
        Assert.That(events[^1].Entered, Is.False); Assert.That(world.IsValid(events[^1].B), Is.False);
        world.Clear(); world.Step(.1f); Assert.That(events.Count, Is.EqualTo(before + 1));
    }

    [Test]
    public void DynamicBodyPassesThroughTriggerAndKinematicOverlapAlsoEnters()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Register(world, Vector3.Zero, ColliderShape.Box(new(1, 4, 4)), trigger: true);
        var mover = Register(world, new(-2, 0, 0), ColliderShape.Sphere(.2f), BodyKind.Dynamic);
        world.SetVelocity(mover, new(2, 0, 0));
        var events = new List<PhysicsTrigger>(); world.Trigger += events.Add;
        int contacts = 0; world.Contact += _ => contacts++;
        for (int i = 0; i < 120; i++) world.Step(1f / 60);
        Assert.That(world.GetPose(mover).Position.X, Is.GreaterThan(1.5));
        Assert.That(events.Select(e => e.Entered), Is.EqualTo(new[] { true, false }));
        Assert.That(contacts, Is.Zero);
        var kinematic = Register(world, new(-2, 0, 0), ColliderShape.Sphere(.2f), BodyKind.Kinematic);
        world.SetKinematicTarget(kinematic, new(Vector3.Zero)); world.Step(.1f);
        Assert.That(events[^1].Entered, Is.True);
        Assert.That(events[^1].B, Is.EqualTo(kinematic));
    }

    [Test]
    public void ClearFromTriggerHandlerDiscardsRemainingNotifications()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Register(world, Vector3.Zero, ColliderShape.Sphere(2), trigger: true);
        Register(world, Vector3.Zero, ColliderShape.Sphere(.2f));
        Register(world, Vector3.UnitX, ColliderShape.Sphere(.2f));
        int count = 0;
        world.Trigger += _ => { count++; world.Clear(); };
        world.Step(.1f); world.Step(.1f);
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public void PresentationUsesCompletedHistoryWithoutMovingPhysicsAndSnapsOnReset()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        var physicsNode = new SceneNode();
        var visual = new SceneNode { LocalMatrix = Matrix4x4.CreateScale(new Vector3(2)) };
        var body = world.Register(Guid.NewGuid(), [ColliderShape.Sphere(.2f)], node: physicsNode,
            body: new BodySettings { Kind = BodyKind.Kinematic });
        world.BindPresentation(body, visual);
        Assert.That(world.GetInterpolatedPose(body, .5f).Position, Is.EqualTo(Vector3.Zero));
        var target = new PhysicsPose(new(2, 0, 0), new Quaternion(Vector3.UnitY, MathF.PI / 2));
        world.SetKinematicTarget(body, target);
        Assert.That(world.GetInterpolatedPose(body, 1).Position, Is.EqualTo(Vector3.Zero), "Pending targets are not completed poses.");
        world.Step(.1f);
        Assert.That(world.GetInterpolatedPose(body, 0).Position.X, Is.Zero);
        Assert.That(world.GetInterpolatedPose(body, 1).Position.X, Is.EqualTo(2));
        var module = new PhysicsHostModule(world);
        module.Update(new GameModuleFrame(default, false, default, default, default) { InterpolationAlpha = .5f });
        Assert.That(visual.WorldMatrix.Translation.X, Is.EqualTo(1).Within(.001));
        Assert.That((Vector3.UnitX * world.GetInterpolatedPose(body, .5f).Rotation.ToMatrix4x4()).Z,
            Is.EqualTo(MathF.Sqrt(.5f)).Within(.001));
        Assert.That(new Vector3(visual.WorldMatrix.M11, visual.WorldMatrix.M12, visual.WorldMatrix.M13).Length(), Is.EqualTo(2).Within(.001));
        Assert.That(physicsNode.WorldMatrix.Translation.X, Is.EqualTo(2).Within(.001));
        Assert.That(world.GetPose(body).Position.X, Is.EqualTo(2));
        Assert.That(world.Raycast(new(2, 0, -2), Vector3.UnitZ, 3, out _), Is.True);
        Assert.That(world.Raycast(new(1, 0, -2), Vector3.UnitZ, 3, out _), Is.False);
        module.Update(new GameModuleFrame(default, true, default, default, default) { InterpolationAlpha = .1f });
        module.Update(new GameModuleFrame(default, false, default, default, default) { InterpolationAlpha = 0 });
        Assert.That(visual.WorldMatrix.Translation.X, Is.EqualTo(2).Within(.001));
        world.Teleport(body, new(new(9, 0, 0)));
        Assert.That(visual.WorldMatrix.Translation.X, Is.EqualTo(9).Within(.001));
        Assert.That(world.GetInterpolatedPose(body, 0).Position.X, Is.EqualTo(9));
        Assert.Throws<ArgumentException>(() => world.BindPresentation(body, physicsNode));
        Assert.Throws<ArgumentException>(() => world.Register(Guid.NewGuid(), [ColliderShape.Sphere(.2f)], node: visual));
        physicsNode.SetParent(visual);
        Assert.Throws<ArgumentException>(() => world.UpdatePresentation(.5f));
        physicsNode.SetParent(null);
        world.Remove(body); world.UpdatePresentation(.5f);
        Assert.That(visual.WorldMatrix.Translation.X, Is.EqualTo(9).Within(.001));
    }

    [Test]
    public void DynamicHistoryKeepsOnlyLatestTwoStepsAndQuaternionTakesShortPath()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        var body = Register(world, Vector3.Zero, ColliderShape.Sphere(.2f), BodyKind.Dynamic);
        world.SetVelocity(body, new(2, 0, 0)); world.Step(.1f);
        var first = world.GetPose(body); world.Step(.1f); var second = world.GetPose(body);
        Assert.That(world.GetInterpolatedPose(body, 0).Position, Is.EqualTo(first.Position));
        Assert.That(world.GetInterpolatedPose(body, 1).Position, Is.EqualTo(second.Position));
        Assert.That(world.GetInterpolatedPose(body, .5f).Position.X, Is.EqualTo((first.Position.X + second.Position.X) / 2).Within(.001));
        var platform = Register(world, new(5, 0, 0), ColliderShape.Sphere(.2f), BodyKind.Kinematic);
        world.SetKinematicTarget(platform, new(new(5, 0, 0), -Quaternion.Identity)); world.Step(.1f);
        Assert.That(MathF.Abs(Quaternion.Dot(world.GetInterpolatedPose(platform, .5f).Rotation, Quaternion.Identity)), Is.EqualTo(1).Within(.001));
    }
}
