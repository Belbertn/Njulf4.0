using System;
using System.IO;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class HybridGiCompositionTests
{
    [Test]
    public void CompositionFlags_RequireExplicitRolloutAuthority()
    {
        var gi = new GlobalIlluminationSettings
        {
            GiMaterialTransportV2 = true,
            GiEmissiveMeshSampling = true,
            GiFarFieldMaterialV2 = true,
            GiHybridCompositionV2 = true,
            FarFieldClipmapEnabled = true
        };

        SsgiCompositionFlags unauthenticated =
            SsgiCompositePass.ResolveCompositionFlags(gi);
        gi.EnableMaterialGiV2ForConformance();
        SsgiCompositionFlags conformance =
            SsgiCompositePass.ResolveCompositionFlags(gi);

        Assert.Multiple(() =>
        {
            Assert.That(
                unauthenticated & SsgiCompositionFlags.HybridV2,
                Is.EqualTo(SsgiCompositionFlags.None));
            Assert.That(
                unauthenticated &
                SsgiCompositionFlags.MaterialTransportV2,
                Is.EqualTo(SsgiCompositionFlags.None));
            Assert.That(
                unauthenticated &
                SsgiCompositionFlags.FarFieldTransport,
                Is.EqualTo(SsgiCompositionFlags.None));
            Assert.That(
                conformance & SsgiCompositionFlags.HybridV2,
                Is.EqualTo(SsgiCompositionFlags.HybridV2));
            Assert.That(
                conformance &
                SsgiCompositionFlags.MaterialTransportV2,
                Is.EqualTo(SsgiCompositionFlags.MaterialTransportV2));
            Assert.That(
                conformance &
                SsgiCompositionFlags.FarFieldTransport,
                Is.EqualTo(SsgiCompositionFlags.FarFieldTransport));
        });
    }

    [Test]
    public void IdenticalEstimators_NeverIncreaseEnergy()
    {
        Vector3 estimate = new(2.5f, 0.75f, 7f);

        for (int i = 0; i <= 100; i++)
        {
            HybridGiCompositionResult result = HybridGiComposition.Compose(
                estimate,
                estimate,
                i / 100f,
                depthConfidence: 0.81f,
                normalConfidence: 0.73f,
                distanceConfidence: 0.62f,
                temporalConfidence: 0.91f,
                ddgiOwnership: 0.65f,
                environmentFallbackShare: 0.35f);

            Assert.Multiple(() =>
            {
                Assert.That(result.Composed.X, Is.EqualTo(estimate.X).Within(1e-6f));
                Assert.That(result.Composed.Y, Is.EqualTo(estimate.Y).Within(1e-6f));
                Assert.That(result.Composed.Z, Is.EqualTo(estimate.Z).Within(1e-6f));
                Assert.That(result.SignedDelta, Is.EqualTo(Vector3.Zero));
            });
        }
    }

    [Test]
    public void Composition_IsBoundedConvexEstimate_ForConformanceSweep()
    {
        var random = new Random(0x51_47_49);
        for (int i = 0; i < 10_000; i++)
        {
            Vector3 baseline = RandomRadiance(random);
            Vector3 ssgi = RandomRadiance(random);
            HybridGiCompositionResult result = HybridGiComposition.Compose(
                baseline,
                ssgi,
                (float)(random.NextDouble() * 1.5 - 0.25),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                ddgiOwnership: (float)random.NextDouble(),
                environmentFallbackShare: (float)random.NextDouble());

            Assert.Multiple(() =>
            {
                Assert.That(result.SsgiWeight, Is.InRange(0f, 1f));
                Assert.That(HybridGiComposition.IsComponentwiseBounded(result), Is.True);
                Assert.That(result.Composed, Is.EqualTo(result.Baseline + result.SignedDelta));
            });
        }
    }

    [Test]
    public void UnsupportedSsgi_UsesExplicitBaselineWithoutBlackout()
    {
        Vector3 baseline = new(0.25f, 1.5f, 4f);
        HybridGiCompositionResult result = HybridGiComposition.Compose(
            baseline,
            new Vector3(30f, 20f, 10f),
            ssgiSupport: 0f,
            ddgiOwnership: 0.4f,
            environmentFallbackShare: 0.6f);

        Assert.Multiple(() =>
        {
            Assert.That(result.SsgiWeight, Is.Zero);
            Assert.That(result.SignedDelta, Is.EqualTo(Vector3.Zero));
            Assert.That(result.Composed, Is.EqualTo(baseline));
        });
    }

    [Test]
    public void ShaderAndPipeline_UseSignedDeltaAndSignedCapableAdditiveBlend()
    {
        string root = FindRepositoryRoot();
        string shader = File.ReadAllText(Path.Combine(root, "Njulf.Shaders", "ssgi_composite.frag"));
        string pipeline = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "PipelineObjects",
            "SsgiCompositePipeline.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain("replacementWeight * (ssgiIndirect - baselineIndirect)"));
            Assert.That(shader, Does.Contain("SSGI_COMPOSITION_FLAG_HYBRID_V2"));
            Assert.That(shader, Does.Contain("float ddgiOwnership = clamp(baseline.a"));
            Assert.That(shader, Does.Contain("SSGI_COMPOSITION_FLAG_ENVIRONMENT_FALLBACK"));
            Assert.That(shader, Does.Not.Contain("max(ssgiIndirect - baselineIndirect"));
            Assert.That(pipeline, Does.Contain("SrcColorBlendFactor = BlendFactor.One"));
            Assert.That(pipeline, Does.Contain("DstColorBlendFactor = BlendFactor.One"));
            Assert.That(pipeline, Does.Contain("BlendOp = BlendOp.Add"));
        });
    }

    [Test]
    public void CompactProfile_PacksMeanMetallicAndRoughnessInNamedIntegerWord()
    {
        CompiledMaterialTransport compiled = MaterialTransportCompiler.Compile(new MaterialDefinition
        {
            MetallicFactor = 0.3125f,
            RoughnessFactor = 0.6875f
        });

        uint packed = compiled.GpuMaterial.PackedMeanMetallicRoughness;
        float metallic = (float)BitConverter.UInt16BitsToHalf((ushort)(packed & 0xffffu));
        float roughness = (float)BitConverter.UInt16BitsToHalf((ushort)(packed >> 16));
        Assert.Multiple(() =>
        {
            Assert.That(metallic, Is.EqualTo(0.3125f).Within(1e-4f));
            Assert.That(roughness, Is.EqualTo(0.6875f).Within(1e-4f));
            Assert.That(compiled.GpuMaterial.DdgiAverageEmissive.W, Is.Zero);
            Assert.That(compiled.GpuMaterial.TransportProfileQuality, Is.EqualTo((uint)compiled.TransportProfile.Quality));
        });
    }

    [Test]
    public void IndependentRolloutFlags_RoundTripAndSurviveQualityChanges()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-flags-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination.GiMaterialTransportV2 = false;
            settings.GlobalIllumination.GiEmissiveMeshSampling = false;
            settings.GlobalIllumination.GiFarFieldMaterialV2 = false;
            settings.GlobalIllumination.GiHybridCompositionV2 = false;
            settings.GlobalIllumination.ApplyDdgiQualityTier(DdgiQualityTier.DdgiLow);
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.GlobalIllumination.GiMaterialTransportV2, Is.False);
                Assert.That(loaded.GlobalIllumination.GiEmissiveMeshSampling, Is.False);
                Assert.That(loaded.GlobalIllumination.GiFarFieldMaterialV2, Is.False);
                Assert.That(loaded.GlobalIllumination.GiHybridCompositionV2, Is.False);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void SsgiTemporalHistory_TracksMaterialAspectRevision()
    {
        string root = FindRepositoryRoot();
        string temporalPass = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Pipeline",
            "SsgiTemporalPass.cs"));
        string sceneBuilder = File.ReadAllText(Path.Combine(
            root,
            "Njulf.Rendering",
            "Data",
            "SceneDataBuilder.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(temporalPass, Does.Contain("_lastMaterialRevision != sceneData.SsgiMaterialRevision"));
            Assert.That(sceneBuilder, Does.Contain("SsgiMaterialRevision = _materialManager.SsgiInputRevision"));
        });
    }

    private static Vector3 RandomRadiance(Random random) => new(
        (float)random.NextDouble() * 64f,
        (float)random.NextDouble() * 64f,
        (float)random.NextDouble() * 64f);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Njulf.Shaders")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
