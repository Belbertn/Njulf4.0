using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Physics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class PhysicsTests
{
    private static ColliderHandle Box(PhysicsScene physics, Vector3 position, BodyKind kind = BodyKind.Static, uint layer = 1, uint mask = uint.MaxValue) =>
        physics.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One)], new PhysicsPose(position), new BodySettings { Kind = kind }, layer, mask);
    private static void Steps(PhysicsScene physics, int count) { for (int i = 0; i < count; i++) physics.Step(1f / 60); }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void ClosestRangeLayersAndOwnerExclusion(PhysicsMode mode)
    {
        using var physics = new PhysicsScene(mode);
        Guid owner = Guid.NewGuid();
        var near = physics.Register(owner, [ColliderShape.Sphere(1)], new PhysicsPose(new(0, 0, 4)), layer: 2);
        var far = Box(physics, new(0, 0, 8), layer: 4);
        Assert.That(physics.Raycast(Vector3.Zero, new(0, 0, 100), 20, out var hit), Is.True);
        Assert.That(hit.Collider, Is.EqualTo(near)); Assert.That(hit.Distance, Is.EqualTo(3).Within(.001));
        Assert.That(hit.Point.Z, Is.EqualTo(3).Within(.001)); Assert.That(hit.Normal.Z, Is.EqualTo(-1).Within(.001));
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 2.9f, out _), Is.False);
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 20, out hit, new(4)), Is.True);
        Assert.That(hit.Collider, Is.EqualTo(far));
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 20, out hit, new(uint.MaxValue, owner)), Is.True);
        Assert.That(hit.Collider, Is.EqualTo(far));
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 20, out _, new(0)), Is.False);
        Assert.That(physics.HasDynamicsWorld, Is.EqualTo(mode == PhysicsMode.Simulation));
        Assert.That(physics.CompletedSteps, Is.Zero);
    }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void RotatedMovedDisabledRemovedWithoutStep(PhysicsMode mode)
    {
        using var physics = new PhysicsScene(mode);
        var pose = new PhysicsPose(new(2, 0, 5), new Quaternion(Vector3.UnitY, MathF.PI / 2));
        var handle = physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(4, 1, 1))], pose);
        Assert.That(physics.Raycast(new(2, 0, 0), Vector3.UnitZ, 20, out var hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(3).Within(.002));
        physics.SetPose(handle, new(new(20, 0, 5)));
        Assert.That(physics.Raycast(new(2, 0, 0), Vector3.UnitZ, 20, out _), Is.False);
        Assert.That(physics.Raycast(new(20, 0, 0), Vector3.UnitZ, 20, out _), Is.True);
        physics.SetEnabled(handle, false);
        Assert.That(physics.Raycast(new(20, 0, 0), Vector3.UnitZ, 20, out _), Is.False);
        physics.SetPose(handle, new(new(30, 0, 5)));
        physics.SetEnabled(handle, true);
        Assert.That(physics.Raycast(new(30, 0, 0), Vector3.UnitZ, 20, out _), Is.True);
        physics.Remove(handle);
        Assert.That(physics.Raycast(new(30, 0, 0), Vector3.UnitZ, 20, out _), Is.False);
        Assert.Throws<ArgumentException>(() => physics.GetPose(handle));
    }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void MeshGapsWindingAndExactOverlaps(PhysicsMode mode)
    {
        using var physics = new PhysicsScene(mode);
        var shape = ColliderShape.TriangleMesh([new(-3, -1, 5), new(-1, -1, 5), new(-2, 1, 5),
            new(1, -1, 5), new(3, -1, 5), new(2, 1, 5)], [0, 1, 2, 3, 4, 5]);
        physics.Register(Guid.NewGuid(), [shape]);
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 10, out _), Is.False, "Mesh gap must not hit an AABB or convex hull.");
        Assert.That(physics.Raycast(new(2, 0, 0), Vector3.UnitZ, 10, out var hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(5).Within(.001));
        Assert.That(physics.Raycast(new(2, 0, 10), -Vector3.UnitZ, 10, out _), Is.True, "Two-sided mesh queries.");
        Assert.That(physics.Raycast(new(2, 0, 10), Vector3.UnitZ, 10, out _), Is.False, "No negative ray distances.");
        Assert.That(physics.Raycast(new(2, -1, 0), Vector3.UnitZ, 10, out _), Is.True, "Triangle edges are closed.");
        Span<OverlapHit> hits = stackalloc OverlapHit[4];
        Assert.That(physics.OverlapSphere(new(0, 0, 5), .25f, hits).Total, Is.Zero);
        Assert.That(physics.OverlapSphere(new(2, 0, 5), .25f, hits).Total, Is.EqualTo(1));
        Assert.That(physics.SweepSphere(Vector3.Zero, .2f, Vector3.UnitZ, 10, out _), Is.False);
    }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void SweepsInitialOverlapAndBufferOverflow(PhysicsMode mode)
    {
        using var physics = new PhysicsScene(mode);
        Box(physics, new(0, 0, 5));
        Assert.That(physics.SweepSphere(Vector3.Zero, .5f, Vector3.UnitZ, 10, out var hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(4).Within(.003));
        Assert.That(hit.Point.Z, Is.EqualTo(4.5).Within(.003));
        Assert.That(hit.Normal.Z, Is.EqualTo(-1).Within(.003));
        Assert.That(physics.SweepCapsule(new(Vector3.Zero), .5f, 2, Vector3.UnitZ, 10, out hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(4).Within(.003));
        Assert.That(physics.SweepSphere(new(0, 0, 5), .5f, Vector3.UnitZ, 0, out hit), Is.True);
        Assert.That(hit.Distance, Is.Zero); Assert.That(hit.Normal, Is.EqualTo(Vector3.Zero));
        Assert.That(physics.Raycast(new(0, 0, 5), Vector3.UnitX, 0, out hit), Is.True);
        Assert.That(hit.Distance, Is.Zero);
        Box(physics, new(1, 0, 5));
        physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(.3f), ColliderShape.Sphere(.4f)], new PhysicsPose(new(0, 0, 5)));
        Span<OverlapHit> buffer = stackalloc OverlapHit[1];
        var overlaps = physics.OverlapSphere(new(0, 0, 5), 2, buffer);
        Assert.That(overlaps, Is.EqualTo(new OverlapResult(1, 3)));
        Assert.That(overlaps.Overflowed, Is.True);
        Assert.That(physics.OverlapSphere(new(0, 0, 5), 2, Span<OverlapHit>.Empty).Total, Is.EqualTo(3));
    }

    [TestCase(PhysicsMode.QueryOnly)]
    [TestCase(PhysicsMode.Simulation)]
    public void ParentRotationScaleAndMeshOffsetAgreeWithNjulfMatrix(PhysicsMode mode)
    {
        using var scene = new Scene(); using var physics = new PhysicsScene(mode, scene);
        var parent = new SceneNode { LocalMatrix = Matrix4x4.CreateScale(new(2)) * new Quaternion(Vector3.UnitY, .7f).ToMatrix4x4() * Matrix4x4.CreateTranslation(new(4, 0, 1)) };
        var node = new SceneNode { Position = new(1, 0, 0) }; node.SetParent(parent, false);
        var offset = Matrix4x4.CreateTranslation(new(2, 0, 0));
        var handle = physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(.5f).WithLocalTransform(offset)], node: node);
        Vector3 center = (offset * node.WorldMatrix).Translation;
        Assert.That(physics.Raycast(center - Vector3.UnitY * 5, Vector3.UnitY, 10, out var hit), Is.True);
        Assert.That(hit.Distance, Is.EqualTo(4).Within(.003));
        parent.Position += new Vector3(30, 0, 0);
        Assert.That(physics.Raycast(center - Vector3.UnitY * 5, Vector3.UnitY, 10, out _), Is.False);
        Assert.That(physics.TransformSynchronizations, Is.EqualTo(1));
        physics.Synchronize(); Assert.That(physics.TransformSynchronizations, Is.EqualTo(1));
        scene.Clear(); Assert.That(physics.ColliderCount, Is.Zero);
        Assert.Throws<ArgumentException>(() => physics.GetPose(handle));
        parent.Position += Vector3.One; physics.Synchronize();
    }

    [Test]
    public void BodiesFallRestReceiveImpulseAndPublishPivotWithoutFeedback()
    {
        using var scene = new Scene(); using var physics = new PhysicsScene(PhysicsMode.Simulation, scene);
        physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(20, 1, 20))], new PhysicsPose(new(0, -.5f, 0)));
        var parent = new SceneNode { Position = new(3, 0, 0) };
        var node = new SceneNode { Position = new(-3, 4, 0) }; node.SetParent(parent, false);
        var handle = physics.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One).WithLocalTransform(Matrix4x4.CreateTranslation(new(1, 0, 0)))],
            body: new BodySettings { Kind = BodyKind.Dynamic }, node: node);
        Steps(physics, 240);
        Assert.That(physics.GetPose(handle).Position.Y, Is.EqualTo(.5).Within(.04));
        Assert.That(node.WorldMatrix.Translation.Y, Is.EqualTo(.5).Within(.04));
        Assert.That(node.WorldMatrix.Translation.X, Is.EqualTo(0).Within(.04), "COM shift must not shift the visual pivot.");
        Assert.That(physics.TransformSynchronizations, Is.Zero);
        physics.ApplyImpulse(handle, new(0, 5, 0)); Steps(physics, 12);
        Assert.That(physics.GetPose(handle).Position.Y, Is.GreaterThan(1));
        physics.Teleport(handle, new(new(0, 3, 0)));
        Assert.That(physics.GetVelocity(handle).Length(), Is.LessThan(.001));
    }

    [Test]
    public void MovingKinematicPlatformCarriesDynamicBodyAndStops()
    {
        using var physics = new PhysicsScene(PhysicsMode.Simulation);
        var platform = physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(6, 1, 6))], new PhysicsPose(new(0, -.5f, 0)),
            new BodySettings { Kind = BodyKind.Kinematic, Friction = 1 });
        var box = Box(physics, new(0, 1, 0), BodyKind.Dynamic);
        Steps(physics, 120);
        for (int i = 1; i <= 120; i++) { physics.SetKinematicTarget(platform, new(new(i / 60f, -.5f, 0))); physics.Step(1f / 60); }
        Assert.That(physics.GetPose(box).Position.X, Is.GreaterThan(1.3));
        Assert.That(physics.GetPose(box).Position.Y, Is.EqualTo(.5).Within(.06));
        physics.Step(1f / 60);
        Assert.That(physics.GetVelocity(platform).Length(), Is.LessThan(.001));
    }

    [Test]
    public void MasksFilterSimulationAndContactsAllowRemovalAfterPublication()
    {
        using var physics = new PhysicsScene(PhysicsMode.Simulation);
        physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(10, 1, 10))], new PhysicsPose(new(0, -.5f, 0)), layer: 1, mask: 1);
        var excluded = Box(physics, new(3, 1, 0), BodyKind.Dynamic, layer: 2);
        var included = Box(physics, new(0, 1, 0), BodyKind.Dynamic);
        bool began = false, ended = false;
        physics.Contact += contact =>
        {
            if (contact.Began)
            {
                began = true;
                Assert.That(physics.GetPose(included).Position.Y, Is.LessThan(.6));
                physics.Remove(included);
            }
            else ended = true;
        };
        Steps(physics, 120);
        Assert.That(began && ended, Is.True);
        Assert.That(physics.GetPose(excluded).Position.Y, Is.LessThan(-3));
    }

    [Test]
    public void TimingClockOwnsPauseAndScale()
    {
        using var physics = new PhysicsScene(PhysicsMode.Simulation);
        physics.Gravity = Vector3.Zero;
        var body = Box(physics, Vector3.Zero, BodyKind.Dynamic); physics.SetVelocity(body, Vector3.UnitX);
        var clock = new GameClock(); var dt = TimeSpan.FromSeconds(.01);
        clock.Configure(TimeSpan.Zero, true, dt, 5, .5, false); clock.Start(TimeSpan.Zero);
        for (int i = 1; i <= 100; i++)
        {
            var now = TimeSpan.FromSeconds(i * .01); clock.Update(now);
            while (clock.TryFixedUpdate(now, out var time)) physics.Step((float)time.ElapsedGameTime.TotalSeconds);
        }
        Assert.That(physics.CompletedSteps, Is.EqualTo(50));
        var before = physics.GetPose(body);
        clock.Configure(TimeSpan.FromSeconds(1), true, dt, 5, .5, true); clock.Update(TimeSpan.FromSeconds(5));
        Assert.That(clock.TryFixedUpdate(TimeSpan.FromSeconds(5), out _), Is.False);
        Assert.That(physics.GetPose(body), Is.EqualTo(before));
    }

    [Test]
    public void LifetimeReplacementRemovalAndDisposalRejectStaleHandles()
    {
        using var scene = new Scene(); using var next = new Scene();
        using var physics = new PhysicsScene(PhysicsMode.QueryOnly, scene); using var replacement = new PhysicsScene(PhysicsMode.QueryOnly, next);
        var visual = new RenderObject(); scene.Add(visual);
        var handle = physics.Register(visual.Id, [ColliderShape.Sphere(1)], node: visual.Node, entity: visual);
        scene.Remove(visual); Assert.That(physics.ColliderCount, Is.Zero);
        Assert.Throws<ArgumentException>(() => replacement.GetPose(handle));
        Box(physics, Vector3.Zero); scene.Dispose();
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitX, 1, out _), Is.False);
        Box(replacement, Vector3.Zero); Assert.That(replacement.ColliderCount, Is.EqualTo(1));
        physics.Dispose(); physics.Dispose();
        Assert.Throws<ObjectDisposedException>(() => physics.Raycast(Vector3.Zero, Vector3.UnitX, 1, out _));
        Assert.That(PhysicsScene.Create(), Is.Null);
    }

    [Test]
    public void InvalidGeometryPoseThreadAndScaleAreRejected()
    {
        using var physics = new PhysicsScene(PhysicsMode.QueryOnly);
        Assert.Throws<ArgumentOutOfRangeException>(() => ColliderShape.Sphere(float.NaN));
        Assert.Throws<ArgumentException>(() => physics.Raycast(Vector3.Zero, Vector3.Zero, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => physics.Raycast(Vector3.Zero, Vector3.UnitX, float.PositiveInfinity, out _));
        Assert.Throws<ArgumentException>(() => physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(1)], new PhysicsPose(Vector3.Zero, default)));
        Assert.Throws<InvalidOperationException>(() => Box(physics, Vector3.Zero, BodyKind.Dynamic));
        var node = new SceneNode(); physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(1)], node: node);
        node.LocalMatrix = Matrix4x4.CreateScale(new(2));
        Assert.Throws<InvalidOperationException>(() => physics.Synchronize());
        var error = Task.Run(() => { try { physics.Synchronize(); return false; } catch (InvalidOperationException) { return true; } }).GetAwaiter().GetResult();
        Assert.That(error, Is.True);
    }

    [Test]
    public void RepeatedQueriesDoNotAllocateAfterWarmup()
    {
        using var physics = new PhysicsScene(PhysicsMode.QueryOnly);
        Box(physics, new(0, 0, 5));
        var results = new OverlapHit[4];
        void Query()
        {
            physics.Raycast(Vector3.Zero, Vector3.UnitZ, 10, out _);
            physics.SweepSphere(Vector3.Zero, .5f, Vector3.UnitZ, 10, out _);
            physics.OverlapSphere(new(0, 0, 5), 1, results);
        }
        for (int i = 0; i < 2000; i++) Query();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) Query();
        Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        Assert.That(physics.TransformSynchronizations, Is.Zero);
        Assert.That(physics.UnmanagedBytes, Is.Zero);
    }

    [Test]
    public void GameSceneExchangeKeepsPhysicsAndSceneOwnershipPaired()
    {
        using var game = new SceneExchangeGame();
        using var firstPhysics = new PhysicsScene(PhysicsMode.QueryOnly, game.ActiveScene);
        var handle = Box(firstPhysics, new(0, 0, 5));
        var next = new Scene(); using var nextPhysics = new PhysicsScene(PhysicsMode.QueryOnly, next);
        var previous = game.Switch(next);
        Assert.That(previous, Is.SameAs(firstPhysics.Scene));
        Assert.That(game.ActiveScene, Is.SameAs(nextPhysics.Scene));
        Assert.That(nextPhysics.Raycast(Vector3.Zero, Vector3.UnitZ, 10, out _), Is.False);
        Assert.That(firstPhysics.GetPose(handle).Position.Z, Is.EqualTo(5), "Retaining the previous pair preserves its state.");
        firstPhysics.Dispose(); previous.Dispose();
        Assert.That(nextPhysics.ColliderCount, Is.Zero);
    }
    private sealed class SceneExchangeGame : Game
    {
        internal Scene ActiveScene => Scene;
        internal Scene Switch(Scene next) => ExchangeScene(next);
    }

    [Test]
    public void ForcesRespectMassAndDynamicQueriesUseCompletedPose()
    {
        using var physics = new PhysicsScene(PhysicsMode.Simulation); physics.Gravity = Vector3.Zero;
        var body = physics.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One)], new PhysicsPose(new(0, 0, 5)),
            new BodySettings { Kind = BodyKind.Dynamic, Mass = 2 });
        physics.AddForce(body, new(120, 0, 0)); physics.Step(1f / 60);
        Assert.That(physics.GetVelocity(body).X, Is.EqualTo(1).Within(.02));
        physics.Step(1f / 60);
        Assert.That(physics.GetVelocity(body).X, Is.EqualTo(1).Within(.02), "The previous force must not apply a second time.");
        physics.Gravity = new(0, -10, 0); physics.Step(.01f);
        Assert.That(physics.GetVelocity(body).Y, Is.EqualTo(-.1).Within(.005), "Gravity and step size changes apply immediately.");
        physics.Gravity = Vector3.Zero;
        physics.SetVelocity(body, new(30, 0, 0)); Steps(physics, 30);
        Assert.That(physics.Raycast(Vector3.Zero, Vector3.UnitZ, 10, out _), Is.False);
        Vector3 current = physics.GetPose(body).Position;
        Assert.That(physics.Raycast(current - Vector3.UnitZ * 5, Vector3.UnitZ, 10, out var hit), Is.True);
        Assert.That(hit.Collider, Is.EqualTo(body));
    }

    [Test]
    public void PhysicsParentPublicationPreservesIndependentDynamicChildWorldPose()
    {
        using var physics = new PhysicsScene(PhysicsMode.Simulation); physics.Gravity = Vector3.Zero;
        var parent = new SceneNode(); var child = new SceneNode { Position = new(3, 0, 0) }; child.SetParent(parent, false);
        var settings = new BodySettings { Kind = BodyKind.Dynamic };
        var childBody = physics.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One)], body: settings, node: child);
        var parentBody = physics.Register(Guid.NewGuid(), [ColliderShape.Box(Vector3.One)], body: settings, node: parent);
        physics.SetVelocity(parentBody, Vector3.UnitX); Steps(physics, 10);
        Assert.That(child.WorldMatrix.Translation.X, Is.EqualTo(3).Within(.001));
        Assert.That(physics.GetPose(childBody).Position.X, Is.EqualTo(3).Within(.001));
        physics.Teleport(parentBody, new(new(0, 4, 0), new Quaternion(Vector3.UnitY, .7f)));
        Assert.That((child.WorldMatrix.Translation - new Vector3(3, 0, 0)).Length(), Is.LessThan(.001));
        physics.Synchronize(); Assert.That(physics.TransformSynchronizations, Is.Zero);
    }
}
