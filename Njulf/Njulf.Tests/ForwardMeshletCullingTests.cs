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
}
