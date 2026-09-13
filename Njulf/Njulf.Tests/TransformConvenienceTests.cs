using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class TransformConvenienceTests
{
    [Test]
    public void PlacementAndObjectShareNodeTransformsWithoutMeshCompensation()
    {
        using var template = new Model();
        template.Add(new RenderObject());
        using var instance = template.CreateInstance();
        var root = instance.PlacementRoot;
        root.SetLocalTransform(new(10, 0, 0), Quaternion.FromMatrix4x4(Matrix4x4.CreateRotationY(MathF.PI / 2)), new(2));
        var item = instance.RenderObjects[0];
        item.AttachNode(item.Node, Matrix4x4.CreateTranslation(0, 0, 3));
        item.LocalPosition = new(0, 0, 1);
        AssertVector(item.WorldPosition, new(8, 0, 0));
        AssertVector(item.WorldMatrix.Translation, new(2, 0, 0));
        ulong revision = item.Revision;
        item.WorldPosition = new(6, 0, 0);
        AssertVector(item.LocalPosition, new(0, 0, 2));
        Assert.That(item.Revision, Is.GreaterThan(revision));
        item.WorldRotation = Quaternion.Identity;
        item.WorldScale = new(3);
        AssertMatrix(item.Node.WorldMatrix, Matrix4x4.CreateScale(3) * Matrix4x4.CreateTranslation(6, 0, 0));
        AssertVector(root.WorldPosition, new(10, 0, 0));
    }

    [Test]
    public void ParentingPreservesRequestedSpaceAndSingularParentFailsBeforeMutation()
    {
        var node = new SceneNode { LocalPosition = new(2, 0, 0) };
        var parent = new SceneNode { LocalPosition = new(5, 0, 0), LocalScale = new(2) };
        node.SetParent(parent);
        AssertVector(node.WorldPosition, new(2, 0, 0));
        AssertVector(node.LocalPosition, new(-1.5f, 0, 0));
        node.SetParent(null, keepWorld: false);
        AssertVector(node.WorldPosition, new(-1.5f, 0, 0));
        var singular = new SceneNode();
        singular.SetLocalTransform(Vector3.Zero, Quaternion.Identity, Vector3.Zero);
        Matrix4x4 before = node.LocalMatrix;
        Assert.Throws<InvalidOperationException>(() => node.SetParent(singular));
        Assert.That(node.Parent, Is.Null);
        AssertMatrix(node.LocalMatrix, before);
        node.SetParent(singular, keepWorld: false);
        Assert.Throws<InvalidOperationException>(() => node.WorldPosition = Vector3.One);
        AssertMatrix(node.LocalMatrix, before);
    }

    [Test]
    public void ExplicitComponentEditsPreserveShearOrRejectItWithoutMutation()
    {
        Matrix4x4 shear = Matrix4x4.Identity;
        shear.M12 = .5f;
        var node = new SceneNode { LocalMatrix = shear };
        node.LocalPosition = Vector3.One;
        node.WorldPosition = new(2);
        Assert.That(node.LocalMatrix.M12, Is.EqualTo(.5f));
        Matrix4x4 before = node.LocalMatrix;
        Assert.Throws<InvalidOperationException>(() => node.LocalRotation = Quaternion.Identity);
        Assert.Throws<InvalidOperationException>(() => node.WorldScale = Vector3.One);
        AssertMatrix(node.LocalMatrix, before);
        node.SetLocalTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
        AssertMatrix(node.LocalMatrix, Matrix4x4.Identity);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0001f));
    private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
    {
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                Assert.That(actual[row, col], Is.EqualTo(expected[row, col]).Within(.0001f));
    }
}
