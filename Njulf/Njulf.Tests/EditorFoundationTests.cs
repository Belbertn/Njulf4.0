using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class EditorFoundationTests
{
    [Test]
    public void RenderObject_Trs_ComposesAndDecomposesIncludingNegativeScale()
    {
        Quaternion rotation = new Quaternion(new Vector3(0.2f, -0.6f, 0.35f));
        Matrix4x4 expected = Matrix4x4.CreateScale(new Vector3(-2f, 3f, 4f)) *
                             rotation.ToMatrix4x4() *
                             Matrix4x4.CreateTranslation(new Vector3(5f, 6f, 7f));
        var renderObject = new RenderObject { WorldMatrix = expected };

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.HasNonTrsMatrix, Is.False);
            AssertMatrix(renderObject.WorldMatrix, expected);
            Assert.That(renderObject.Position, Is.EqualTo(new Vector3(5f, 6f, 7f)));
        });
    }

    [Test]
    public void RenderObject_NonTrsMatrix_PreservesRawMatrixUntilTransformIsEdited()
    {
        Matrix4x4 sheared = Matrix4x4.Identity;
        sheared.M12 = 0.5f;
        var renderObject = new RenderObject { WorldMatrix = sheared };

        Assert.Multiple(() =>
        {
            Assert.That(renderObject.HasNonTrsMatrix, Is.True);
            AssertMatrix(renderObject.WorldMatrix, sheared);
        });

        renderObject.Position = new Vector3(1f, 2f, 3f);
        Assert.Multiple(() =>
        {
            Assert.That(renderObject.HasNonTrsMatrix, Is.False);
            Assert.That(renderObject.WorldMatrix.Translation, Is.EqualTo(new Vector3(1f, 2f, 3f)));
        });
    }

    [Test]
    public void Scene_FindById_FindsEverySupportedEntity()
    {
        var scene = new Scene();
        var renderObject = new RenderObject();
        var probe = new ReflectionProbe();
        var prototype = new Njulf.Core.Foliage.FoliagePrototype();
        var patch = new Njulf.Core.Foliage.FoliagePatch(prototype, new BoundingBox(Vector3.Zero, Vector3.One));

        scene.Add(renderObject);
        scene.Add(probe);
        scene.Add(patch);

        Assert.Multiple(() =>
        {
            Assert.That(scene.FindById(renderObject.Id), Is.SameAs(renderObject));
            Assert.That(scene.FindById(probe.Id), Is.SameAs(probe));
            Assert.That(scene.FindById(prototype.Id), Is.SameAs(prototype));
            Assert.That(scene.FindById(patch.Id), Is.SameAs(patch));
            Assert.That(scene.FindById(System.Guid.NewGuid()), Is.Null);
        });
    }

    [Test]
    public void ScreenPointToRay_CenterPointsCameraForward_AndPickerChoosesNearestObject()
    {
        var camera = new FirstPersonCamera(Vector3.Zero) { AspectRatio = 1f, FieldOfView = System.MathF.PI / 2f };
        Ray ray = camera.ScreenPointToRay(new Vector2(50f, 50f), new Vector2(100f, 100f));
        var scene = new Scene();
        var far = new RenderObject
        {
            LocalMeshBounds = new BoundingBox(new Vector3(-1f), new Vector3(1f)),
            Position = new Vector3(0f, 0f, -10f)
        };
        var near = new RenderObject
        {
            LocalMeshBounds = new BoundingBox(new Vector3(-1f), new Vector3(1f)),
            Position = new Vector3(0f, 0f, -4f)
        };
        scene.Add(far);
        scene.Add(near);

        bool hit = ScenePicker.TryPickRenderObject(scene, ray, out RenderObject? picked, out float distance);

        Assert.Multiple(() =>
        {
            Assert.That(Vector3.Dot(ray.Direction, camera.Forward), Is.GreaterThan(0.999f));
            Assert.That(hit, Is.True);
            Assert.That(picked, Is.SameAs(near));
            Assert.That(distance, Is.GreaterThan(0f));
        });
    }

    [Test]
    public void ScreenPointToRay_CornersFollowTheCameraFrustum()
    {
        var camera = new FirstPersonCamera(Vector3.Zero) { AspectRatio = 1f, FieldOfView = System.MathF.PI / 2f };

        Ray topLeft = camera.ScreenPointToRay(Vector2.Zero, new Vector2(100f, 100f));
        Ray bottomRight = camera.ScreenPointToRay(new Vector2(100f, 100f), new Vector2(100f, 100f));

        Assert.Multiple(() =>
        {
            Assert.That(topLeft.Direction.X, Is.LessThan(0f));
            Assert.That(topLeft.Direction.Y, Is.GreaterThan(0f));
            Assert.That(topLeft.Direction.Z, Is.LessThan(0f));
            Assert.That(bottomRight.Direction.X, Is.GreaterThan(0f));
            Assert.That(bottomRight.Direction.Y, Is.LessThan(0f));
            Assert.That(bottomRight.Direction.Z, Is.LessThan(0f));
        });
    }

    private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
    {
        for (int row = 0; row < 4; row++)
        for (int column = 0; column < 4; column++)
            Assert.That(actual[row, column], Is.EqualTo(expected[row, column]).Within(0.0001f));
    }
}
