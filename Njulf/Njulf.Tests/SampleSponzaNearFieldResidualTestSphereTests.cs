using System.Linq;
using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

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

    [Test]
    public void ProductionSphereSources_CoalesceIntoOneAlignedEligibleDemand()
    {
        GPUVertex[] vertices = SampleUvSphereMesh.CreateVertices();
        uint[] indices = SampleUvSphereMesh.CreateIndices();
        byte[] emission =
            SampleSponzaNearFieldResidualTestSphere.CreateEmissionPattern();
        TextureTransportStatistics statistics = TextureTransportImage.FromRgba8(
            emission,
            SampleSponzaNearFieldResidualTestSphere.EmissionTextureWidth,
            SampleSponzaNearFieldResidualTestSphere.EmissionTextureHeight,
            TextureColorSpace.Srgb,
            TextureSemantic.Color,
            CookedHash.Bytes(emission),
            SampleSponzaNearFieldResidualTestSphere.EmissionTextureSchema).Statistics;
        float meanEmission = (float)statistics.LinearChannelMean.X;
        CoreVector3 radiance =
            SampleSponzaNearFieldResidualTestSphere.EmissiveColor *
            (SampleSponzaNearFieldResidualTestSphere.EmissiveStrength *
             meanEmission);
        var candidates = new DdgiEmissiveTriangleCandidate[indices.Length / 3];
        for (int triangle = 0; triangle < candidates.Length; triangle++)
        {
            CoreVector3 World(uint vertexIndex) =>
                vertices[vertexIndex].Position *
                SampleSponzaNearFieldResidualTestSphere.Radius +
                SampleSponzaNearFieldResidualTestSphere.Position;
            candidates[triangle] = new DdgiEmissiveTriangleCandidate(
                World(indices[triangle * 3]),
                World(indices[triangle * 3 + 1]),
                World(indices[triangle * 3 + 2]),
                radiance,
                DdgiEmissiveSourceFlags.Triangle,
                checked((ulong)triangle + 1UL));
        }

        var sources = new GPUDdgiEmissiveSource[candidates.Length];
        DdgiEmissiveTriangleTable.Build(candidates, sources);
        var demands = new List<SimpleDdgiRefinementDemand>();
        SimpleDdgiRefinementEmissiveDemandDiagnostics diagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                sources,
                new SimpleDdgiRefinementEmissiveDemandConfiguration(
                    MinimumLuminanceNits: 200f,
                    MaximumEmitterAreaSquareMeters: 4f,
                    MaximumDemandCount: 32),
                demands);

        var pool = new SimpleDdgiRefinementBrickPool();
        pool.Update(
            1,
            new SimpleDdgiRefinementBrickConfiguration(
                Enabled: true,
                Capacity: 1,
                CountX: 6,
                CountY: 6,
                CountZ: 6,
                Spacing: 0.59375f,
                RetentionFrames: 90),
            demands);

        SimpleDdgiRefinementDemand demand = demands.Single();
        SimpleDdgiRefinementBrick brick = pool.ActiveBricks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(
                EmissivePhotometry.SceneLinearLuminanceToNits(
                    EmissivePhotometry.Luminance(radiance)),
                Is.EqualTo(234.40327f).Within(0.001f));
            Assert.That(diagnostics.EligibleSourceCount, Is.EqualTo(2_208));
            Assert.That(diagnostics.AdmittedDemandCount, Is.EqualTo(1));
            Assert.That(demand.Position.X, Is.EqualTo(1.25f).Within(1e-5f));
            Assert.That(demand.Position.Y, Is.EqualTo(0.58f).Within(1e-5f));
            Assert.That(demand.Position.Z, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(demand.SourceBounds!.Value.Min,
                Is.EqualTo(new CoreVector3(0.8f, 0.13f, 1.55f)));
            Assert.That(demand.SourceBounds.Value.Max,
                Is.EqualTo(new CoreVector3(1.7f, 1.03f, 2.45f)));
            Assert.That(brick.Origin.X, Is.EqualTo(-0.296875f).Within(1e-6f));
            Assert.That(brick.Origin.Y, Is.EqualTo(0.1484375f).Within(1e-6f));
            Assert.That(brick.Origin.Z, Is.EqualTo(0.4453125f).Within(1e-6f));
        });
    }
}
