using Njulf.Core.Math;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class CharacterControllerTests
{
    private const float Dt = 1f / 60;
    private static readonly CharacterControllerSettings Settings = new() { Radius = .3f, Length = 1, StepHeight = .3f };
    private static ColliderHandle Box(PhysicsScene world, Vector3 position, Vector3 size, BodyKind kind = BodyKind.Static) =>
        world.Register(Guid.NewGuid(), [ColliderShape.Box(size)], new PhysicsPose(position), new BodySettings { Kind = kind });
    private static void Tick(PhysicsScene world, CharacterController character, Vector3 velocity = default, bool jump = false)
    { character.Move(velocity, jump, Dt); world.Step(Dt); }

    [Test]
    public void RecoversGroundsJumpsLandsAndStopsAtWallAndCeiling()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Box(world, new(0, -.5f, 0), new(30, 1, 30));
        using var character = new CharacterController(world, Guid.NewGuid(), new(0, .7f, 0), Settings);
        Tick(world, character);
        Assert.That(character.RecoveryFailed, Is.False);
        Assert.That(character.IsGrounded, Is.True);
        Assert.That(character.Pose.Position.Y, Is.EqualTo(.82f).Within(.025));
        Tick(world, character, jump: true);
        Assert.That(character.IsGrounded, Is.False);
        float maxY = character.Pose.Position.Y;
        for (int i = 0; i < 90; i++) { Tick(world, character); maxY = MathF.Max(maxY, character.Pose.Position.Y); }
        Assert.That(maxY, Is.GreaterThan(1.8f)); Assert.That(character.IsGrounded, Is.True);
        Box(world, new(2, 2, 0), new(1, 4, 10));
        for (int i = 0; i < 90; i++) Tick(world, character, new(3, 0, 0));
        Assert.That(character.Pose.Position.X, Is.InRange(1.1f, 1.21f));
        Box(world, new(0, 2.2f, 0), new(10, .2f, 10));
        character.Teleport(new(0, .82f, 0)); Tick(world, character); Tick(world, character, jump: true);
        maxY = character.Pose.Position.Y;
        for (int i = 0; i < 60; i++) { Tick(world, character); maxY = MathF.Max(maxY, character.Pose.Position.Y); }
        Assert.That(maxY, Is.InRange(1.1f, 1.32f)); Assert.That(character.IsGrounded, Is.True);
    }

    [TestCase(.2f, true)]
    [TestCase(.6f, false)]
    public void StepsRespectHeightLimit(float height, bool climbs)
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Box(world, new(0, -.5f, 0), new(30, 1, 30));
        Box(world, new(2.5f, height / 2, 0), new(3, height, 4));
        using var character = new CharacterController(world, Guid.NewGuid(), new(0, .82f, 0), Settings);
        for (int i = 0; i < 90; i++) Tick(world, character, new(2, 0, 0));
        Assert.That(character.RecoveryFailed, Is.False);
        Assert.That(character.IsGrounded, Is.True);
        if (climbs)
        {
            Assert.That(character.Pose.Position.X, Is.GreaterThan(2));
            Assert.That(character.Pose.Position.Y, Is.EqualTo(.82f + height).Within(.04));
        }
        else Assert.That(character.Pose.Position.X, Is.LessThan(.75f));
    }

    [TestCase(30, true)]
    [TestCase(65, false)]
    public void SlopesOnlyAllowWalkingBelowLimit(float degrees, bool walkable)
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        float angle = degrees * MathF.PI / 180;
        world.Register(Guid.NewGuid(), [ColliderShape.Box(new(20, .2f, 10))],
            new PhysicsPose(Vector3.Zero, new Quaternion(Vector3.UnitZ, -angle)));
        float initialY = -MathF.Tan(angle) + .5f + (.1f + .3f + Settings.SkinWidth) / MathF.Cos(angle);
        using var character = new CharacterController(world, Guid.NewGuid(), new(-1, initialY, 0), Settings);
        for (int i = 0; i < 30; i++) Tick(world, character, new(2, 0, 0));
        Assert.That(character.RecoveryFailed, Is.False);
        if (walkable)
        {
            Assert.That(character.IsGrounded, Is.True);
            Assert.That(character.Pose.Position.X, Is.GreaterThan(-.5f));
            Assert.That(character.Pose.Position.Y, Is.GreaterThan(initialY + .25f));
            Assert.That(character.GroundNormal.Y, Is.EqualTo(MathF.Cos(angle)).Within(.02));
        }
        else
        {
            Assert.That(character.IsGrounded, Is.False);
            Assert.That(character.Pose.Position.X, Is.LessThan(-.75f));
            Assert.That(character.Pose.Position.Y, Is.LessThan(initialY - .1f), "Gravity slides down a steep surface.");
        }
    }

    [Test]
    public void PlatformsCarryRotateAndTransferVelocityWithoutTeleportLaunch()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        var platform = Box(world, new(0, -.5f, 0), new(6, 1, 6), BodyKind.Kinematic);
        using var character = new CharacterController(world, Guid.NewGuid(), new(1, .82f, 0), Settings);
        Tick(world, character);
        Assert.That(character.SupportingCollider, Is.EqualTo(platform));
        for (int i = 1; i <= 60; i++)
        {
            world.SetKinematicTarget(platform, new(new(i * .02f, -.5f, 0), new Quaternion(Vector3.UnitY, -i * MathF.PI / 180)));
            Tick(world, character);
        }
        Vector3 expected = Vector3.UnitX * new Quaternion(Vector3.UnitY, -MathF.PI / 3).ToMatrix4x4() + new Vector3(1.2f, .82f, 0);
        Assert.That((character.Pose.Position - expected).Length(), Is.LessThan(.06f));
        var beforeJump = character.Pose.Position;
        world.SetKinematicTarget(platform, new(new(1.22f, -.5f, 0), new Quaternion(Vector3.UnitY, -MathF.PI / 3)));
        Tick(world, character, jump: true);
        Assert.That(character.IsGrounded, Is.False);
        for (int i = 0; i < 10; i++) Tick(world, character);
        Assert.That(character.Pose.Position.X, Is.GreaterThan(beforeJump.X + .18f), "Jump inherits platform velocity.");
        character.Teleport(new(1.22f, .82f, 0)); Tick(world, character);
        Assert.That(character.IsGrounded, Is.True);
        var beforeTeleport = character.Pose.Position;
        world.Teleport(platform, new(new(100, -.5f, 0)));
        Tick(world, character);
        Assert.That(character.IsGrounded, Is.False);
        Assert.That(MathF.Abs(character.Pose.Position.X - beforeTeleport.X), Is.LessThan(.01f));
        Assert.That(character.SupportingCollider, Is.EqualTo(default(ColliderHandle)));
    }

    [Test]
    public void RecoveryFailureStopsAndSceneRemovalInvalidatesController()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Box(world, new(0, -.5f, 0), new(10, 1, 10));
        Box(world, new(0, 1.5f, 0), new(10, 1, 10)); // One-unit gap is too short for the capsule.
        using var character = new CharacterController(world, Guid.NewGuid(), new(0, .5f, 0), Settings);
        Tick(world, character, Vector3.UnitX);
        Assert.That(character.RecoveryFailed, Is.True);
        Assert.That(character.Pose.Position, Is.EqualTo(new Vector3(0, .5f, 0)));
        world.Clear();
        Assert.Throws<ArgumentException>(() => character.Move(Vector3.Zero, false, Dt));
        character.Dispose();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RemovingOrDisablingSupportDetachesWithoutCarrying(bool remove)
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        var platform = Box(world, new(0, -.5f, 0), new(4, 1, 4), BodyKind.Kinematic);
        using var character = new CharacterController(world, Guid.NewGuid(), new(0, .82f, 0), Settings);
        Tick(world, character);
        if (remove) world.Remove(platform); else world.SetEnabled(platform, false);
        Tick(world, character);
        Assert.That(character.IsGrounded, Is.False);
        Assert.That(character.SupportingCollider, Is.EqualTo(default(ColliderHandle)));
        Assert.That(character.Pose.Position.Y, Is.LessThan(.82f));
    }

    [Test]
    public void StepCannotLiftCapsuleThroughLowCeiling()
    {
        using var world = new PhysicsScene(PhysicsMode.Simulation);
        Box(world, new(0, -.5f, 0), new(10, 1, 10));
        Box(world, new(2, .1f, 0), new(2, .2f, 4));
        Box(world, new(0, 1.8f, 0), new(10, .2f, 10));
        using var character = new CharacterController(world, Guid.NewGuid(), new(0, .82f, 0), Settings);
        for (int i = 0; i < 60; i++) Tick(world, character, new(2, 0, 0));
        Assert.That(character.Pose.Position.X, Is.LessThan(.8f));
        Assert.That(character.Pose.Position.Y + .8f, Is.LessThan(1.71f));
        Assert.That(character.RecoveryFailed, Is.False);
    }
}
