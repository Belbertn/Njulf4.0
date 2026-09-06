using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SecondaryViewVisibilityTests
{
    [Test]
    public void MeshletBoundsRemainConservativeUnderShearAndNegativeScale()
    {
        Matrix4x4 world = Matrix4x4.Identity;
        world.M11 = -2; world.M21 = 3; world.M31 = -4;
        BoundingBox bounds = SecondaryViewVisibility.TransformSphere(Vector3.Zero, 1, world);
        Vector3 extreme = new Vector3(world.M11, world.M21, world.M31).Normalized() * world;
        Assert.That(bounds.Max.X, Is.EqualTo(extreme.X).Within(1e-5f));
        Assert.That(bounds.Min.X, Is.EqualTo(-extreme.X).Within(1e-5f));
    }

    [Test]
    public void ReflectedFrustumKeepsGeometryBehindMainCameraAndHonorsClipTolerance()
    {
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 100);
        Frustum main = SceneDataBuilder.ExtractFrustum(projection);
        Frustum reflected = SceneDataBuilder.ExtractFrustum(
            Matrix4x4.CreateLookAt(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY) * projection);
        var bounds = new BoundingBox(new(-0.5f, -0.5f, 2), new(0.5f, 0.5f, 3));
        Assert.Multiple(() =>
        {
            Assert.That(SecondaryViewVisibility.IsVisible(bounds, main, default), Is.False);
            Assert.That(SecondaryViewVisibility.IsVisible(bounds, reflected, default), Is.True);
            Assert.That(SecondaryViewVisibility.IsVisible(bounds, reflected, new(0, 0, -1, 1)), Is.False);
            Assert.That(SecondaryViewVisibility.IsVisible(bounds, reflected, new(0, 0, -1, 1.9995f), 0.001f), Is.True);
        });
    }

    [Test]
    public void FootprintIncludesFilteringAndClipsToVisibleReflector()
    {
        AutomaticPlanarCandidate candidate = new()
        {
            WorldOrigin = new(0, 0, 0.5f), WorldTangent = Vector3.UnitX,
            WorldBitangent = Vector3.UnitY, ProjectedBoundsMin = new(-0.2f),
            ProjectedBoundsMax = new(0.2f), MaximumSamplingRoughness = 0f
        };
        AutomaticPlanarCluster Cluster(AutomaticPlanarCandidate c) => new()
            { Representative = c, Members = [c], ReceiverIdentities = new HashSet<uint>() };
        SecondaryViewRegion sharp = SecondaryViewFootprint.Compute(Cluster(candidate),
            Matrix4x4.Identity, Matrix4x4.Identity, 1024, 1024, 10);
        SecondaryViewRegion rough = SecondaryViewFootprint.Compute(
            Cluster(candidate with { MaximumSamplingRoughness = 1 }),
            Matrix4x4.Identity, Matrix4x4.Identity, 1024, 1024, 10).Resolve(1024, 1024);
        Assert.Multiple(() =>
        {
            Assert.That(sharp.X, Is.LessThan(409));
            Assert.That(sharp.X + sharp.Width, Is.GreaterThan(615));
            Assert.That(sharp.Width, Is.LessThan(512));
            Assert.That(rough, Is.EqualTo(new SecondaryViewRegion(0, 0, 1024, 1024)));
        });
        Frustum crop = SceneDataBuilder.ExtractFrustum(sharp.Crop(Matrix4x4.Identity, 1024, 1024));
        Assert.That(SecondaryViewVisibility.IsVisible(new BoundingBox(new(-0.1f, -0.1f, 0.4f),
            new(0.1f, 0.1f, 0.6f)), crop, default), Is.True);
        Assert.That(SecondaryViewVisibility.IsVisible(new BoundingBox(new(0.8f, -0.1f, 0.4f),
            new(0.9f, 0.1f, 0.6f)), crop, default), Is.False);
    }

    [Test]
    public void TransparencyPreservesReflectedDistanceOrderAndIndependentStorage()
    {
        var first = new SecondaryViewDrawLists();
        var second = new SecondaryViewDrawLists();
        first.Transparent.Add(new(new GPUMeshletDrawCommand { InstanceId = 1 }, 4, 0));
        first.Transparent.Add(new(new GPUMeshletDrawCommand { InstanceId = 2 }, 16, 0));
        first.SortTransparency();
        second.Clear();
        Assert.That(first.TransparentCommands.Select(c => c.InstanceId), Is.EqualTo(new uint[] { 2, 1 }));
        Assert.That(second.TransparentCommands, Is.Empty);
    }
}
