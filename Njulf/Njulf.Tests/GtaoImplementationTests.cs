using System;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GtaoImplementationTests
{
    [Test]
    public void DdgiHighDefaults_EnableHighGtaoAndKeepBentNormalLightingOff()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.AmbientOcclusion.Mode,
                Is.EqualTo(AmbientOcclusionMode.Gtao));
            Assert.That(settings.AmbientOcclusion.BentNormalMode,
                Is.EqualTo(AmbientOcclusionBentNormalMode.Off));
            Assert.That(settings.AmbientOcclusion.EffectiveBentNormalMode,
                Is.EqualTo(AmbientOcclusionBentNormalMode.Off));
            Assert.That(settings.AmbientOcclusion.GtaoQualityPreset,
                Is.EqualTo(GtaoQualityPreset.High));
            Assert.That(settings.AmbientOcclusion.EffectiveGtaoDirectionCount,
                Is.EqualTo(6));
            Assert.That(settings.AmbientOcclusion.EffectiveGtaoStepCount,
                Is.EqualTo(8));
        });
    }

    [Test]
    public void QualityPresets_SelectTheProductionFeatureMatrix()
    {
        var expected = new[]
        {
            (RenderQualityPreset.Low, AmbientOcclusionMode.Disabled,
                GtaoQualityPreset.Low,
                AmbientOcclusionBentNormalMode.Off,
                SimpleDdgiReceiverCacheMode.Exact, false),
            (RenderQualityPreset.Medium, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.Low,
                AmbientOcclusionBentNormalMode.Off,
                SimpleDdgiReceiverCacheMode.TemporalAdaptive, true),
            (RenderQualityPreset.High, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.Balanced,
                AmbientOcclusionBentNormalMode.EnvironmentOnly,
                SimpleDdgiReceiverCacheMode.TemporalAdaptive, true),
            (RenderQualityPreset.DdgiHigh, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.High,
                AmbientOcclusionBentNormalMode.Off,
                SimpleDdgiReceiverCacheMode.TemporalAdaptive, true),
            (RenderQualityPreset.Ultra, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.High,
                AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
                SimpleDdgiReceiverCacheMode.TemporalAdaptive, true)
        };

        foreach (var entry in expected)
        {
            var settings = new RenderSettings();
            settings.SceneSubmission.GpuCompactionEnabled = false;
            settings.SceneSubmission.IndirectMeshletDispatchEnabled = false;
            settings.SceneSubmission.GpuLodSelectionEnabled = false;
            settings.SceneSubmission.GpuLodSelectionMode =
                GpuLodSelectionMode.LegacyDistance;
            settings.SceneSubmission.GpuLodDitherTransitionsEnabled = false;
            settings.SceneSubmission.GpuLodTransitionFrameCount = 2;
            settings.SceneSubmission.GpuHierarchicalLodEnabled = false;
            settings.SceneSubmission.GpuMeshletStreamingEnabled = false;
            settings.SceneSubmission.GpuShadowCompactionEnabled = false;
            settings.Foliage.IndirectMeshletDispatchEnabled = false;
            settings.MeshletNormalConeCullingEnabled = false;
            settings.ApplyQualityPreset(entry.Item1);
            Assert.Multiple(() =>
            {
                Assert.That(settings.AmbientOcclusion.Mode,
                    Is.EqualTo(entry.Item2), entry.Item1.ToString());
                Assert.That(settings.AmbientOcclusion.GtaoQualityPreset,
                    Is.EqualTo(entry.Item3), entry.Item1.ToString());
                Assert.That(settings.AmbientOcclusion.BentNormalMode,
                    Is.EqualTo(entry.Item4), entry.Item1.ToString());
                Assert.That(settings.GlobalIllumination
                        .SimpleDdgiReceiverCacheMode,
                    Is.EqualTo(entry.Item5), entry.Item1.ToString());
                Assert.That(settings.GlobalIllumination
                        .SimpleDdgiNearFieldResidualLocalAdaptiveSchedulingEnabled,
                    Is.EqualTo(entry.Item6), entry.Item1.ToString());
                Assert.That(settings.MeshletNormalConeCullingEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission.GpuCompactionEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .IndirectMeshletDispatchEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission.GpuLodSelectionEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission.GpuLodSelectionMode,
                    Is.EqualTo(GpuLodSelectionMode.ScreenSpaceError),
                    entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .GpuLodDitherTransitionsEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .GpuLodTransitionFrameCount,
                    Is.EqualTo(SceneSubmissionSettings
                        .DefaultGpuLodTransitionFrameCount),
                    entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .GpuHierarchicalLodEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .GpuMeshletStreamingEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.SceneSubmission
                        .GpuShadowCompactionEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.Foliage.IndirectMeshletDispatchEnabled,
                    Is.True, entry.Item1.ToString());
                Assert.That(settings.Transparency.PipelinePartitioningEnabled,
                    Is.True, entry.Item1.ToString());
            });
        }
    }

    [Test]
    public void Settings_RoundTripDistinctGtaoContractAndClampDdgiGate()
    {
        string path = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            $"gtao-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.AmbientOcclusion.Mode = AmbientOcclusionMode.Gtao;
            settings.AmbientOcclusion.GtaoQualityPreset =
                GtaoQualityPreset.High;
            settings.AmbientOcclusion.GtaoThickness = 0.27f;
            settings.AmbientOcclusion.GtaoFalloff = 1.6f;
            settings.AmbientOcclusion.BentNormalMode =
                AmbientOcclusionBentNormalMode.EnvironmentAndDdgi;
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            Assert.Multiple(() =>
            {
                Assert.That(loaded.AmbientOcclusion.Mode,
                    Is.EqualTo(AmbientOcclusionMode.Gtao));
                Assert.That(loaded.AmbientOcclusion.GtaoQualityPreset,
                    Is.EqualTo(GtaoQualityPreset.High));
                Assert.That(loaded.AmbientOcclusion.GtaoThickness,
                    Is.EqualTo(0.27f).Within(0.0001f));
                Assert.That(loaded.AmbientOcclusion.GtaoFalloff,
                    Is.EqualTo(1.6f).Within(0.0001f));
                Assert.That(loaded.AmbientOcclusion.BentNormalMode,
                    Is.EqualTo(
                        AmbientOcclusionBentNormalMode.EnvironmentAndDdgi));
                Assert.That(loaded.AmbientOcclusion.EffectiveBentNormalMode,
                    Is.EqualTo(
                        AmbientOcclusionBentNormalMode.EnvironmentAndDdgi));
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void UnsupportedGtaoFormats_FallBackToSsaoWithoutChangingRequest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                AmbientOcclusionPass.ResolveEffectiveMode(
                    AmbientOcclusionMode.Gtao,
                    gtaoRuntimeSupported: false),
                Is.EqualTo(AmbientOcclusionMode.Ssao));
            Assert.That(
                AmbientOcclusionPass.ResolveEffectiveMode(
                    AmbientOcclusionMode.Gtao,
                    gtaoRuntimeSupported: true),
                Is.EqualTo(AmbientOcclusionMode.Gtao));
            Assert.That(
                AmbientOcclusionPass.ResolveEffectiveMode(
                    AmbientOcclusionMode.Disabled,
                    gtaoRuntimeSupported: false),
                Is.EqualTo(AmbientOcclusionMode.Disabled));
        });
    }

    [Test]
    public void ManagedAbi_HasExactShaderPushBlockSizesAndStableFixedIndices()
    {
        uint flags = GPUForwardPushConstants.PackDebugAndAoFlags(
            debugViewMode: 3u,
            ambientOcclusionEnabled: true,
            ambientOcclusionDebugView: 11u,
            ambientOcclusionBentNormalMode:
                (uint)AmbientOcclusionBentNormalMode.EnvironmentOnly);

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUGtaoPushConstants>(),
                Is.EqualTo(184));
            Assert.That(Marshal.SizeOf<GPUGtaoTemporalPushConstants>(),
                Is.EqualTo(112));
            Assert.That(Marshal.SizeOf<GPUGtaoSpatialPushConstants>(),
                Is.EqualTo(96));
            Assert.That((flags >> 16) & 0x3fu, Is.EqualTo(11u));
            Assert.That((flags >> 22) & 0x03u,
                Is.EqualTo((uint)
                    AmbientOcclusionBentNormalMode.EnvironmentOnly));
            Assert.That((flags >> 24) & 1u, Is.EqualTo(1u));
            Assert.That(BindlessIndex.GtaoFilteredTexture,
                Is.EqualTo(
                    BindlessIndex.OpaqueSceneColorSnapshotTexture + 1));
            Assert.That(BindlessIndex.FirstDynamicTextureIndex,
                Is.EqualTo(BindlessIndex.GtaoDebugTexture + 1));
        });
    }

    [Test]
    public void PipelineAndShaders_AreDistinctTemporalGtaoWithOneSpatialPass()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string renderingDirectory = FindRepoDirectory("Njulf.Rendering");
        string raw = File.ReadAllText(Path.Combine(shaderDirectory,
            "gtao.comp"));
        string temporal = File.ReadAllText(Path.Combine(shaderDirectory,
            "gtao_temporal.comp"));
        string spatial = File.ReadAllText(Path.Combine(shaderDirectory,
            "gtao_spatial.comp"));
        string forward = File.ReadAllText(Path.Combine(shaderDirectory,
            "forward.frag"));
        string passes = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "GtaoPasses.cs"));
        string ssao = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "AmbientOcclusionPass.cs"));
        string blur = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "AmbientOcclusionBlurPass.cs"));
        string graph = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "ProductionRenderPipelineDeclaration.cs"));

        Assert.Multiple(() =>
        {
            Assert.That(ssao, Does.Contain(
                "effectiveMode == AmbientOcclusionMode.Ssao"));
            Assert.That(blur, Does.Contain(
                "sceneData.AmbientOcclusionMode != AmbientOcclusionMode.Ssao"));
            Assert.That(passes, Does.Contain(
                "sceneData.AmbientOcclusionMode == AmbientOcclusionMode.Gtao"));
            Assert.That(raw, Does.Contain("SearchHorizon("));
            Assert.That(raw, Does.Contain("textureLod(HiZTexture"));
            Assert.That(raw, Does.Contain("IntegrateVisibleArc("));
            Assert.That(raw, Does.Contain("EncodeOctahedral(bentNormal)"));
            Assert.That(temporal, Does.Contain("vec2 previousUv = uv - motion;"));
            Assert.That(temporal, Does.Contain("NeighborhoodEnvelope("));
            Assert.That(temporal, Does.Contain(
                "GTAO_TEMPORAL_SHARED_STRIDE"));
            Assert.That(temporal, Does.Contain(
                "shared vec4 SharedViewPosition"));
            Assert.That(temporal, Does.Contain(
                "SharedGeometricNormal[sharedIndex]"));
            Assert.That(temporal, Does.Contain("barrier();"));
            Assert.That(temporal, Does.Contain(
                "previousAge >= pc.MaximumHistoryAge"));
            Assert.That(temporal, Does.Contain(
                "dot(previousNormal, normal) < pc.NormalThreshold"));
            Assert.That(spatial, Does.Contain(
                "shared vec4 SharedPayload[GTAO_SHARED_COUNT];"));
            Assert.That(spatial, Does.Contain("barrier();"));
            Assert.That(spatial, Does.Contain(
                "imageStore(ScalarAoOutput"));
            Assert.That(graph, Does.Contain("Pass(\"GtaoPass\""));
            Assert.That(graph, Does.Contain("Pass(\"GtaoTemporalPass\""));
            Assert.That(graph, Does.Contain("Pass(\"GtaoSpatialPass\""));
            Assert.That(forward, Does.Contain(
                "TryResolveIndirectDiffuseNormal("));
            Assert.That(forward, Does.Contain(
                "ForwardAmbientOcclusionBentNormalMode() == 2u"));
            Assert.That(forward, Does.Contain(
                "fragWorldPosition,\n            geometricNormal,\n            pc.Push"));
            Assert.That(forward, Does.Contain(
                "#if NJULF_GTAO_BENT_NORMAL_LIGHTING"));
            Assert.That(forward, Does.Contain(
                "EvaluateEnvironmentDiffuseIrradiance(\n        environment,\n        diffuseIndirectNormal)"));
            Assert.That(forward, Does.Not.Contain(
                "EvaluateDirectLight(diffuseIndirectNormal"));
            Assert.That(forward, Does.Not.Contain(
                "reflect(-viewDirection, diffuseIndirectNormal)"));
        });
    }

    [Test]
    public void OctahedralBentNormals_RoundTripFiniteUnitHemisphereVectors()
    {
        var random = new Random(1729);
        for (int i = 0; i < 512; i++)
        {
            float x = (float)(random.NextDouble() * 2.0 - 1.0);
            float y = (float)(random.NextDouble() * 2.0 - 1.0);
            float z = (float)random.NextDouble();
            Normalize(ref x, ref y, ref z);
            (float encodedX, float encodedY) = EncodeOctahedral(x, y, z);
            (float decodedX, float decodedY, float decodedZ) =
                DecodeOctahedral(encodedX, encodedY);
            float length = MathF.Sqrt(decodedX * decodedX +
                decodedY * decodedY + decodedZ * decodedZ);
            float agreement = x * decodedX + y * decodedY + z * decodedZ;
            Assert.Multiple(() =>
            {
                Assert.That(float.IsFinite(length), Is.True);
                Assert.That(length, Is.EqualTo(1.0f).Within(0.00001f));
                Assert.That(decodedZ, Is.GreaterThanOrEqualTo(-0.00001f));
                Assert.That(agreement, Is.GreaterThan(0.9999f));
            });
        }
    }

    private static (float X, float Y) EncodeOctahedral(
        float x, float y, float z)
    {
        float inverseL1 = 1.0f /
            MathF.Max(MathF.Abs(x) + MathF.Abs(y) + MathF.Abs(z), 0.000001f);
        x *= inverseL1;
        y *= inverseL1;
        z *= inverseL1;
        if (z < 0.0f)
        {
            float oldX = x;
            x = (1.0f - MathF.Abs(y)) * MathF.CopySign(1.0f, oldX);
            y = (1.0f - MathF.Abs(oldX)) * MathF.CopySign(1.0f, y);
        }
        return (Math.Clamp(x, -1.0f, 1.0f),
            Math.Clamp(y, -1.0f, 1.0f));
    }

    private static (float X, float Y, float Z) DecodeOctahedral(
        float x, float y)
    {
        float z = 1.0f - MathF.Abs(x) - MathF.Abs(y);
        if (z < 0.0f)
        {
            float oldX = x;
            x = (1.0f - MathF.Abs(y)) * MathF.CopySign(1.0f, oldX);
            y = (1.0f - MathF.Abs(oldX)) * MathF.CopySign(1.0f, y);
        }
        Normalize(ref x, ref y, ref z);
        return (x, y, z);
    }

    private static void Normalize(ref float x, ref float y, ref float z)
    {
        float inverseLength = 1.0f / MathF.Max(
            MathF.Sqrt(x * x + y * y + z * z), 0.000001f);
        x *= inverseLength;
        y *= inverseLength;
        z *= inverseLength;
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new AssertionException(
            $"Could not find repo directory '{name}'.");
    }
}
