using Njulf.Core.Math;
using Njulf.Editor;
using NUnit.Framework;
using N3 = System.Numerics.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class GizmoMathTests
{
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
