using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleSponzaNearFieldResidualTestSphereTests
{
    [Test]
    public void FixtureIsAnOpaqueWarmGiEmitterPlacedJustAboveTheCourtyardFloor()
    {
        var emissionTexture = new TextureHandle(7, 1);
        MaterialDefinition material =
            SampleSponzaNearFieldResidualTestSphere.CreateMaterialDefinition(
                emissionTexture);

        Assert.Multiple(() =>
        {
            Assert.That(material.Name,
                Is.EqualTo(SampleSponzaNearFieldResidualTestSphere.MaterialName));
            Assert.That(material.AlphaMode, Is.EqualTo(MaterialAlphaMode.Opaque));
            Assert.That(material.EmissiveFactor,
                Is.EqualTo(SampleSponzaNearFieldResidualTestSphere.EmissiveColor));
            Assert.That(material.EmissiveStrength,
                Is.EqualTo(SampleSponzaNearFieldResidualTestSphere.EmissiveStrength));
            Assert.That(material.Emissive.Texture, Is.EqualTo(emissionTexture));
            Assert.That(material.Emissive.TexCoordSet, Is.Zero);
            Assert.That(material.EmitsIntoGi, Is.True);
            Assert.That(
                SampleSponzaNearFieldResidualTestSphere.Position.Y -
                SampleSponzaNearFieldResidualTestSphere.Radius,
                Is.EqualTo(0.13f).Within(1.0e-5f));
        });
    }

    [Test]
    public void EmissionCheckerContainsEqualBrightAndDimOpaqueTexels()
    {
        byte[] pixels = SampleSponzaNearFieldResidualTestSphere.CreateEmissionPattern();
        int texelCount =
            SampleSponzaNearFieldResidualTestSphere.EmissionTextureWidth *
            SampleSponzaNearFieldResidualTestSphere.EmissionTextureHeight;

        Assert.Multiple(() =>
        {
            Assert.That(pixels, Has.Length.EqualTo(texelCount * 4));
            Assert.That(Enumerable.Range(0, texelCount)
                    .Count(index => pixels[index * 4] == 255),
                Is.EqualTo(texelCount / 2));
            Assert.That(Enumerable.Range(0, texelCount)
                    .Count(index => pixels[index * 4] == 24),
                Is.EqualTo(texelCount / 2));
            Assert.That(Enumerable.Range(0, texelCount)
                    .All(index => pixels[index * 4 + 3] == 255),
                Is.True);
        });
    }

    [Test]
    public void SharedSphereMeshIsClosedAndUsesOnlyValidUnitSphereVertices()
    {
        GPUVertex[] vertices = SampleUvSphereMesh.CreateVertices();
        uint[] indices = SampleUvSphereMesh.CreateIndices();

        Assert.Multiple(() =>
        {
            Assert.That(vertices, Has.Length.EqualTo(1_106));
            Assert.That(indices, Has.Length.EqualTo(6_624));
            Assert.That(indices.Length % 3, Is.Zero);
            Assert.That(indices.Max(), Is.LessThan((uint)vertices.Length));
            Assert.That(vertices.All(vertex =>
                    System.MathF.Abs(vertex.Position.LengthSquared() - 1.0f) <
                    1.0e-4f),
                Is.True);
        });
    }
}
