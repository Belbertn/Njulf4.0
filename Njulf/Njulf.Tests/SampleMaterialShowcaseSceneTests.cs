using Njulf.Assets.Validation;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NjulfHelloGame;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class SampleMaterialShowcaseSceneTests
{
    [Test]
    public void RenderProfile_EnablesPhysicalTransportAndGeneralizedCaustics()
    {
        var settings = new RenderSettings();

        SampleMaterialShowcaseScene.ConfigureRenderSettings(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.Enabled, Is.True);
            Assert.That(settings.GlobalIllumination.Mode,
                Is.EqualTo(GlobalIlluminationMode.Ddgi));
            Assert.That(settings.GlobalIllumination.UseDdgi, Is.True);
            Assert.That(settings.GlobalIllumination.UseRayQueryBackend, Is.True);
            Assert.That(settings.GlobalIllumination.SimpleDdgiRingCount,
                Is.EqualTo(1));
            Assert.That(settings.GlobalIllumination.SimpleDdgiAuthoredVolumes,
                Has.Count.EqualTo(1));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiAuthoredVolumes[0].Spacing,
                Is.EqualTo(0.65f));
            Assert.That(
                settings.GlobalIllumination.SimpleDdgiThinSurfaceTransmissionEnabled,
                Is.True);
            Assert.That(settings.GlobalIllumination.DdgiTransparentGeometryMode,
                Is.EqualTo(DdgiTransparentGeometryMode.StochasticBlend));
            Assert.That(settings.GlobalIllumination.SimpleDdgiRoughSpecularEnabled,
                Is.True);
            Assert.That(settings.GlobalIllumination.GiCausticMode,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(settings.Transparency.ThickTransmissionMode,
                Is.EqualTo(ThickTransmissionMode.RayQuery));
            Assert.That(settings.Transparency.DispersionMode,
                Is.EqualTo(DispersionMode.RgbTriplet));
            Assert.That(settings.Reflections.Mode,
                Is.EqualTo(ReflectionMode.HybridRayQuery));
        });
    }

    [Test]
    public void RenderProfile_RequiredDdgiTopologyFitsTierProbeBudget()
    {
        var settings = new RenderSettings();
        SampleMaterialShowcaseScene.ConfigureRenderSettings(settings);

        SimpleDdgiReceiverCoverageReport coverage =
            SimpleDdgiReceiverCoverageValidator.Validate(
                settings.GlobalIllumination,
                new BoundingBox(
                    new CoreVector3(-4.8f, -0.15f, -1.0f),
                    new CoreVector3(4.8f, 3.0f, 5.35f)),
                [],
                []);
        SimpleDdgiLayoutBudget budget =
            SimpleDdgiLayoutBudget.Resolve(settings.GlobalIllumination);

        Assert.Multiple(() =>
        {
            Assert.That(coverage.Layout.HasRequiredRejection,
                Is.False, coverage.Layout.Summary);
            Assert.That(coverage.Layout.RequestedProbeCount,
                Is.LessThanOrEqualTo(budget.ProbeBudget));
        });
    }

    [Test]
    public void SharedSphereMesh_HasAuthenticatedClosedCausticTopology()
    {
        GPUVertex[] vertices = SampleUvSphereMesh.CreateVertices();
        uint[] indices = SampleUvSphereMesh.CreateIndices();
        var positions = new CoreVector3[vertices.Length];
        for (int index = 0; index < vertices.Length; index++)
            positions[index] = vertices[index].Position;

        bool analyzed = ModelGiCausticHeroTopologyAnalyzer.TryAnalyze(
            positions,
            indices,
            isSkinned: false,
            out ModelGiCausticHeroTopologyEvidence evidence,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(analyzed, Is.True, reason);
            Assert.That(evidence.IsStructurallyValid, Is.True);
            Assert.That(evidence.Facts.IsClosedManifold, Is.True);
            Assert.That(evidence.Facts.HasConsistentWinding, Is.True);
            Assert.That(evidence.Facts.HasGeometricNormals, Is.True);
            Assert.That(evidence.Facts.HasUnsupportedNestedMedium, Is.False);
        });
    }

    [Test]
    public void Showcase_ContainsExplicitThinGlassReflectionTarget()
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(
                   root,
                   "NjulfHelloGame",
                   "SampleMaterialShowcaseScene.cs")))
        {
            root = Directory.GetParent(root)?.FullName ??
                throw new DirectoryNotFoundException(
                    "Could not locate SampleMaterialShowcaseScene.cs.");
        }

        string source = File.ReadAllText(Path.Combine(
            root,
            "NjulfHelloGame",
            "SampleMaterialShowcaseScene.cs"));
        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("ReflectionTest.ThinGlassPane"));
            Assert.That(source, Does.Contain(
                "shadingModel: MaterialShadingModel.ThinGlass"));
            Assert.That(source, Does.Contain(
                "blendMode: MaterialBlendMode.AlphaBlend"));
            Assert.That(source, Does.Contain("TransmissionFactor = 0.96f"));
            Assert.That(source, Does.Contain("roughness: 0.05f"));
        });
    }
}
