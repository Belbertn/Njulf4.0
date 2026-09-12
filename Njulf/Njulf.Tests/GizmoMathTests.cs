using Njulf.Core.Math;
using Njulf.Editor;
using Njulf.Core.Scene;
using NUnit.Framework;
using N3 = System.Numerics.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class GizmoMathTests
{
    [Test]
    public void CenterRotationPreservesPivotForPreviouslyRotatedScaledObject()
    {
        var original = new GizmoTransform(new Vector3(5, 3, -2),
            new Quaternion(Vector3.UnitX, 0.4f), new Vector3(2, 1, 3));
        var point = new BoundingBox(new Vector3(4, 2, 1), new Vector3(4, 2, 1));
        Matrix4x4 Compose(GizmoTransform value) => Matrix4x4.CreateScale(value.Scale) *
            value.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(value.Position);
        Vector3 pivot = BoundingBox.Transform(point, Compose(original)).Center;
        using var target = new RenderObject { WorldMatrix = Compose(original) };
        var result = GizmoMath.RotateAroundPivot(GizmoTransform.Read(target), new N3(pivot.X, pivot.Y, pivot.Z),
            System.Numerics.Quaternion.CreateFromAxisAngle(N3.UnitZ, 0.7f));
        Vector3 after = BoundingBox.Transform(point, Compose(result)).Center;
        Assert.That(after.X, Is.EqualTo(pivot.X).Within(0.0001));
        Assert.That(after.Y, Is.EqualTo(pivot.Y).Within(0.0001));
        Assert.That(after.Z, Is.EqualTo(pivot.Z).Within(0.0001));
    }

    [Test]
    public void SharedSceneOriginsDefaultToGeometryCenterAndRotateInPlace()
    {
        using var scene = new Scene();
        var parent = new RenderObject { IsTransformGroup = true, Position = new Vector3(5, 0, 0) };
        var first = new RenderObject { LocalMeshBounds = new BoundingBox(new Vector3(10, -1, -1), new Vector3(12, 1, 1)) };
        first.Node.SetParent(parent.Node, false);
        var primitive = new RenderObject { LocalMeshBounds = new BoundingBox(new Vector3(12, -1, -1), new Vector3(14, 1, 1)) };
        primitive.AttachNode(first.Node, Matrix4x4.Identity);
        var sibling = new RenderObject { LocalMeshBounds = new BoundingBox(new Vector3(30, -1, -1), new Vector3(32, 1, 1)) };
        sibling.Node.SetParent(parent.Node, false);
        scene.Add(parent); scene.Add(first); scene.Add(primitive); scene.Add(sibling);
        var gizmo = new EditorGizmoController();
        Vector3 pivot = gizmo.GetPivot(scene, first);
        Assert.That(pivot, Is.EqualTo(new Vector3(17, 0, 0)));
        Assert.That(gizmo.GetPivot(scene, sibling), Is.EqualTo(new Vector3(36, 0, 0)));
        Assert.That(gizmo.GetPivot(scene, parent), Is.EqualTo(new Vector3(26, 0, 0)));
        GizmoTransform rotated = GizmoMath.RotateAroundPivot(GizmoTransform.Read(first), new N3(pivot.X, pivot.Y, pivot.Z),
            System.Numerics.Quaternion.CreateFromAxisAngle(N3.UnitZ, MathF.PI / 2));
        first.Node.SetWorldMatrix(Matrix4x4.CreateScale(rotated.Scale) * rotated.Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(rotated.Position));
        Vector3 after = gizmo.GetPivot(scene, first);
        Assert.That(after.X, Is.EqualTo(pivot.X).Within(0.0001));
        Assert.That(after.Y, Is.EqualTo(pivot.Y).Within(0.0001));
        Assert.That(after.Z, Is.EqualTo(pivot.Z).Within(0.0001));
        Assert.That(gizmo.GetPivot(scene, sibling), Is.EqualTo(new Vector3(36, 0, 0)));
        gizmo.PivotMode = GizmoPivotMode.Origin;
        Assert.That(gizmo.GetPivot(scene, sibling), Is.EqualTo(new Vector3(5, 0, 0)));
    }

    [Test]
    public void AxisAndRingIntersectionsRejectParallelRays()
    {
        Assert.That(GizmoMath.TryAxisDistance(new(new(2, 0, 5), new(0, 0, -1)), N3.Zero, N3.UnitX, out float distance), Is.True);
        Assert.That(distance, Is.EqualTo(2));
        Assert.That(GizmoMath.TryAxisDistance(new(Vector3.Zero, new(1, 0, 0)), N3.Zero, N3.UnitX, out _), Is.False);
        Assert.That(GizmoMath.TryRingDirection(new(new(2, 0, 5), new(0, 0, -1)), N3.Zero, N3.UnitZ, out var direction), Is.True);
        Assert.That(direction, Is.EqualTo(N3.UnitX));
        Assert.That(GizmoMath.TryRingDirection(new(Vector3.Zero, new(1, 0, 0)), N3.Zero, N3.UnitZ, out _), Is.False);
    }
    [Test]
    public void AxisScalingPreservesOtherAxesAndNegativeSigns()
    {
        Vector3 scale = GizmoMath.Scale(new(-2, 3, 4), 0, MathF.Log(2));
        Assert.That(scale, Is.EqualTo(new Vector3(-4, 3, 4)));
        scale = GizmoMath.Scale(new(-2, 3, 4), 3, -1000);
        Assert.That(scale.X, Is.LessThan(0)); Assert.That(scale.Y, Is.GreaterThan(0)); Assert.That(scale.Z, Is.GreaterThan(0));
    }
}
