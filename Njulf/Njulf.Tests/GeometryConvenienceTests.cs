using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class GeometryConvenienceTests
{
    [TestCase(5, 0, 0, 1, true)] // Face contact, far from the box center.
    [TestCase(5, 3, 0, 1, false)] // Near a corner but outside the radius.
    [TestCase(4, 2, 0, 0, true)]
    [TestCase(0, 0, 0, .1f, true)]
    public void BoxSphereUsesClosestPointInBothDirections(float x, float y, float z, float radius, bool hit)
    {
        var box = new BoundingBox(new(-4, -2, -1), new(4, 2, 1));
        var sphere = new BoundingSphere(new(x, y, z), radius);
        Assert.That(box.Intersects(sphere), Is.EqualTo(hit));
        Assert.That(sphere.Intersects(box), Is.EqualTo(hit));
    }

    [TestCase(1f)]
    [TestCase(10f)]
    [TestCase(1e-30f)]
    [TestCase(float.MaxValue)]
    public void RaysReturnWorldDistanceIndependentOfDirectionLength(float scale)
    {
        var ray = new Ray(new(-3, 0, 0), new(scale, 0, 0));
        Assert.That(ray.Intersects(new BoundingBox(new(-1), new(1)), out float boxDistance), Is.True);
        Assert.That(ray.Intersects(new BoundingSphere(Vector3.Zero, 1), out float sphereDistance), Is.True);
        Assert.That(boxDistance, Is.EqualTo(2).Within(1e-5));
        Assert.That(sphereDistance, Is.EqualTo(2).Within(1e-5));
        Assert.That(ray.GetPointAt(sphereDistance), Is.EqualTo(new Vector3(-1, 0, 0)));
    }

    [TestCase(0f)]
    [TestCase(1f)]
    public void RaysStartingInsideOrOnSurfaceReturnZero(float x)
    {
        var ray = new Ray(new(x, 0, 0), Vector3.UnitX);
        Assert.That(ray.Intersects(new BoundingBox(new(-1), new(1)), out float a), Is.True);
        Assert.That(ray.Intersects(new BoundingSphere(Vector3.Zero, 1), out float b), Is.True);
        Assert.That(a, Is.Zero); Assert.That(b, Is.Zero);
    }

    [Test]
    public void ParallelSlabsTangenciesAndMissesHaveDefinedResults()
    {
        var box = new BoundingBox(new(-1), new(1));
        var sphere = new BoundingSphere(Vector3.Zero, 1);
        var tangent = new Ray(new(-3, 1, 0), Vector3.UnitX);
        Assert.That(tangent.Intersects(box, out float a), Is.True); Assert.That(a, Is.EqualTo(2));
        Assert.That(tangent.Intersects(sphere, out float b), Is.True); Assert.That(b, Is.EqualTo(3));
        foreach (Ray miss in new[] { new Ray(new(-3, 2, 0), Vector3.UnitX), new Ray(new(-3, 0, 0), -Vector3.UnitX) })
        {
            Assert.That(miss.Intersects(box, out a), Is.False); Assert.That(a, Is.Zero);
            Assert.That(miss.Intersects(sphere, out b), Is.False); Assert.That(b, Is.Zero);
        }
        foreach (Vector3 direction in new[] { Vector3.Zero, new Vector3(float.NaN), new Vector3(float.PositiveInfinity) })
        {
            var invalid = new Ray(Vector3.Zero, direction);
            Assert.Throws<ArgumentException>(() => invalid.Intersects(box, out _));
            Assert.Throws<ArgumentException>(() => invalid.Intersects(sphere, out _));
            Assert.Throws<ArgumentException>(() => invalid.GetPointAt(0));
        }
    }

    [Test]
    public void EqualityAndMatrixOverloadsMatchExistingSemantics()
    {
        foreach (float value in new[] { 0f, -0f, 2f, float.PositiveInfinity, float.NaN })
        {
            var a = new Vector2(value, 1); var b = new Vector2(value, 1);
            var c = new Vector4(value, 1, 2, 3); var d = new Vector4(value, 1, 2, 3);
            Assert.That(a == b, Is.EqualTo(a.Equals(b))); Assert.That(a != b, Is.EqualTo(!a.Equals(b)));
            Assert.That(c == d, Is.EqualTo(c.Equals(d))); Assert.That(c != d, Is.EqualTo(!c.Equals(d)));
            Assert.That(a == b, Is.EqualTo(!float.IsNaN(value)));
            Assert.That(c == d, Is.EqualTo(!float.IsNaN(value)));
        }
        Assert.That(new Vector2(1, 2) != new Vector2(1, 3), Is.True);
        Assert.That(new Vector4(1, 2, 3, 4) != new Vector4(1, 2, 3, 5), Is.True);
        Assert.That(Matrix4x4.CreateScale(2), Is.EqualTo(Matrix4x4.CreateScale(new Vector3(2))));
        Assert.That(Matrix4x4.CreateScale(2, 3, 4), Is.EqualTo(Matrix4x4.CreateScale(new Vector3(2, 3, 4))));
        Assert.That(new Vector3(1, 1, 1) * Matrix4x4.CreateTranslation(2, 3, 4), Is.EqualTo(new Vector3(3, 4, 5)));
    }
}
