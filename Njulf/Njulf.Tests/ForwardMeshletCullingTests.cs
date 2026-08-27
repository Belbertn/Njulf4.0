using Njulf.Core.Math;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ForwardMeshletCullingTests
{
    [Test]
    public void NormalConeEligibility_AcceptsRigidUniformTransforms()
    {
        Matrix4x4 transform =
            Matrix4x4.CreateScale(new Vector3(2.0f)) *
            Matrix4x4.CreateRotationY(0.6f) *
            Matrix4x4.CreateTranslation(new Vector3(3.0f, 4.0f, 5.0f));

        Assert.That(
            SceneDataBuilder.IsNormalConePreservingTransform(transform),
            Is.True);
    }

    [Test]
    public void NormalConeEligibility_RejectsAnisotropicAndMirroredTransforms()
    {
        Matrix4x4 anisotropic = Matrix4x4.CreateScale(
            new Vector3(2.0f, 1.0f, 1.0f));
        Matrix4x4 mirrored = Matrix4x4.CreateScale(
            new Vector3(-1.0f, 1.0f, 1.0f));

        Assert.Multiple(() =>
        {
            Assert.That(
                SceneDataBuilder.IsNormalConePreservingTransform(anisotropic),
                Is.False);
            Assert.That(
                SceneDataBuilder.IsNormalConePreservingTransform(mirrored),
                Is.False);
        });
    }

    [Test]
    public void PerspectiveConeDecision_RejectsOnlyBackFacingViews()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MeshletNormalConeCulling.IsPerspectiveCulled(
                    Vector3.UnitZ,
                    1.0f,
                    Vector3.Zero,
                    1.0f,
                    new Vector3(0.0f, 0.0f, -5.0f)),
                Is.True);
            Assert.That(
                MeshletNormalConeCulling.IsPerspectiveCulled(
                    Vector3.UnitZ,
                    1.0f,
                    Vector3.Zero,
                    1.0f,
                    new Vector3(0.0f, 0.0f, 5.0f)),
                Is.False);
            Assert.That(
                MeshletNormalConeCulling.IsPerspectiveCulled(
                    Vector3.UnitZ,
                    1.0f,
                    Vector3.Zero,
                    1.0f,
                    new Vector3(0.0f, 0.0f, -0.5f)),
                Is.False,
                "A camera inside the bounds must remain visible.");
            Assert.That(
                MeshletNormalConeCulling.IsPerspectiveCulled(
                    Vector3.Zero,
                    -1.0f,
                    Vector3.Zero,
                    1.0f,
                    new Vector3(0.0f, 0.0f, -5.0f)),
                Is.False,
                "The disabled sentinel must remain visible.");
        });
    }

    [Test]
    public void OrthographicConeDecision_UsesConstantSurfaceToCameraDirection()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MeshletNormalConeCulling.IsOrthographicCulled(
                    Vector3.UnitZ,
                    1.0f,
                    -Vector3.UnitZ),
                Is.True);
            Assert.That(
                MeshletNormalConeCulling.IsOrthographicCulled(
                    Vector3.UnitZ,
                    1.0f,
                    Vector3.UnitX),
                Is.False);
        });
    }

    [Test]
    public void PerspectiveConeDecision_HasNoSampledFalsePositiveRejections()
    {
        const float radius = 1.0f;
        var random = new System.Random(0x4E4A554C);
        for (int scenario = 0; scenario < 200; scenario++)
        {
            float coneAngle = 0.02f + (float)random.NextDouble() * 1.0f;
            float cutoff = MathF.Cos(coneAngle);
            Vector3 cameraDirection = RandomUnitVector(random);
            float cameraDistance = 1.01f + (float)random.NextDouble() * 30.0f;
            Vector3 camera = cameraDirection * cameraDistance;
            if (!MeshletNormalConeCulling.IsPerspectiveCulled(
                    Vector3.UnitZ,
                    cutoff,
                    Vector3.Zero,
                    radius,
                    camera))
            {
                continue;
            }

            for (int sample = 0; sample < 96; sample++)
            {
                float angle = (float)random.NextDouble() * coneAngle;
                float azimuth = (float)random.NextDouble() * MathF.Tau;
                Vector3 normal = new(
                    MathF.Sin(angle) * MathF.Cos(azimuth),
                    MathF.Sin(angle) * MathF.Sin(azimuth),
                    MathF.Cos(angle));
                Vector3 point = RandomUnitVector(random) *
                    (float)random.NextDouble() * radius;
                Assert.That(
                    Vector3.Dot(normal, camera - point),
                    Is.LessThan(0.0f),
                    "A rejected cone must contain no sampled front-facing triangle/view pair.");
            }
        }
    }

    private static Vector3 RandomUnitVector(System.Random random)
    {
        float z = (float)random.NextDouble() * 2.0f - 1.0f;
        float azimuth = (float)random.NextDouble() * MathF.Tau;
        float radial = MathF.Sqrt(MathF.Max(1.0f - z * z, 0.0f));
        return new Vector3(
            radial * MathF.Cos(azimuth),
            radial * MathF.Sin(azimuth),
            z);
    }
}
