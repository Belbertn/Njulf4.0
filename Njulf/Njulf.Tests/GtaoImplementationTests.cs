using System;
using System.Collections.Generic;
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
                SimpleDdgiReceiverCacheMode.Exact, true),
            (RenderQualityPreset.High, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.Balanced,
                AmbientOcclusionBentNormalMode.EnvironmentOnly,
                SimpleDdgiReceiverCacheMode.Exact, true),
            (RenderQualityPreset.DdgiHigh, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.High,
                AmbientOcclusionBentNormalMode.Off,
                SimpleDdgiReceiverCacheMode.Exact, true),
            (RenderQualityPreset.Ultra, AmbientOcclusionMode.Gtao,
                GtaoQualityPreset.High,
                AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
                SimpleDdgiReceiverCacheMode.Exact, true)
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
                Is.EqualTo(180));
            Assert.That(Marshal.SizeOf<GPUGtaoTemporalPushConstants>(),
                Is.EqualTo(48));
            Assert.That(Marshal.SizeOf<GPUGtaoSpatialPushConstants>(),
                Is.EqualTo(32));
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
            "forward.frag")).ReplaceLineEndings("\n");
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
            Assert.That(raw, Does.Contain("SearchHorizonCos("));
            Assert.That(raw, Does.Contain("IntegrateGtaoArc("));
            Assert.That(raw, Does.Contain("ApproximateHorizonAngle("));
            Assert.That(raw, Does.Contain(
                "ApproximateAcos(float sineMagnitude, float cosine)"));
            Assert.That(raw, Does.Contain("ReconstructViewZw("));
            Assert.That(raw, Does.Contain(
                "ivec2 centerPixel = ResolveDepthPixel(uv, sourceExtent);"));
            Assert.That(raw, Does.Contain(
                "vec2 centerUv = DepthPixelUv(centerPixel, sourceExtent);"));
            Assert.That(raw, Does.Contain(
                "vec2 resolvedSampleUv = DepthPixelUv(samplePixel, sourceExtent);"));
            Assert.That(raw, Does.Contain(
                "resolvedSampleUv, sampleDepth);"));
            Assert.That(raw, Does.Not.Contain(
                "ReconstructViewPosition(sampleUv, sampleDepth)"));
            Assert.That(raw, Does.Contain(
                "GtaoCurrentGeometryOutput"));
            Assert.That(raw, Does.Not.Contain("HiZTexture"));
            Assert.That(temporal, Does.Contain(
                "GtaoCurrentGeometryInput"));
            Assert.That(temporal, Does.Not.Contain("DepthTexture"));
            Assert.That(temporal, Does.Not.Contain(
                "ReconstructViewPosition("));
            Assert.That(raw, Does.Not.Contain("uv + vec2(invSource"));
            Assert.That(temporal, Does.Not.Contain("uv + vec2(texel"));
            Assert.That(raw, Does.Not.Contain(
                "textureLod(HiZTexture, sampleUv"));
            Assert.That(raw, Does.Contain(
                "float planeDistance = dot(delta, surfaceNormal);"));
            Assert.That(raw, Does.Contain(
                "if (planeDistance <= pc.PlaneBias)"));
            Assert.That(raw, Does.Not.Contain(
                "sourcePixelsPerDestinationPixel"));
            Assert.That(CountOccurrences(raw,
                "textureSize(DepthTexture, 0)"), Is.EqualTo(1));
            Assert.That(raw, Does.Not.Contain("float angle = atan("));
            Assert.That(raw, Does.Not.Contain("float normalAngle = atan("));
            Assert.That(raw, Does.Contain("EncodeOctahedral(bentNormal)"));
            Assert.That(temporal, Does.Contain("vec2 previousUv = uv - motion;"));
            Assert.That(temporal, Does.Contain("NeighborhoodEnvelope("));
            Assert.That(temporal, Does.Contain(
                "GTAO_TEMPORAL_SHARED_STRIDE"));
            Assert.That(temporal, Does.Contain(
                "shared float SharedViewDepth"));
            Assert.That(temporal, Does.Contain(
                "SharedGeometricNormal[sharedIndex]"));
            Assert.That(temporal, Does.Contain("barrier();"));
            Assert.That(temporal, Does.Contain(
                "previousAge >= pc.MaximumHistoryAge"));
            Assert.That(temporal, Does.Contain(
                "dot(previousNormal, normal) < pc.NormalThreshold"));
            Assert.That(spatial, Does.Contain(
                "shared vec4 SharedPayload[GTAO_SHARED_COUNT];"));
            Assert.That(spatial, Does.Contain(
                "CombinedAxisGaussianWeight("));
            Assert.That(spatial, Does.Contain(
                "uint uniqueSourceCount ="));
            Assert.That(spatial, Does.Not.Contain("DepthTexture"));
            Assert.That(spatial, Does.Contain("barrier();"));
            Assert.That(spatial, Does.Contain(
                "imageStore(ScalarAoOutput"));
            Assert.That(graph, Does.Contain("Pass(\"GtaoPass\""));
            Assert.That(graph, Does.Contain("Pass(\"GtaoTemporalPass\""));
            Assert.That(graph, Does.Contain("Pass(\"GtaoSpatialPass\""));
            Assert.That(graph, Does.Contain(
                "WriteComputeStorage(RenderGraphResourceId.GtaoCurrentGeometry"));
            Assert.That(graph, Does.Contain(
                "ReadComputeSampled(RenderGraphResourceId.GtaoCurrentGeometry)"));
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
    public void CollapsedSpatialKernel_PreservesExactGaussianCoefficients()
    {
        var extents = new[]
        {
            (OutputWidth: 13, OutputHeight: 9, SourceWidth: 13, SourceHeight: 9),
            (OutputWidth: 13, OutputHeight: 9, SourceWidth: 7, SourceHeight: 5),
            (OutputWidth: 13, OutputHeight: 9, SourceWidth: 4, SourceHeight: 3)
        };

        foreach (var extent in extents)
        {
            var centers = new[]
            {
                (X: 0, Y: 0),
                (X: extent.OutputWidth - 1, Y: extent.OutputHeight - 1),
                (X: extent.OutputWidth / 2, Y: extent.OutputHeight / 2),
                (X: Math.Min(7, extent.OutputWidth - 1),
                    Y: Math.Min(7, extent.OutputHeight - 1))
            };
            foreach (var center in centers)
            foreach (int radius in new[] { 0, 1, 2 })
            {
                double sigma = Math.Max(radius, 1);
                var brute = new Dictionary<(int X, int Y), double>();
                for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                {
                    var source = (
                        ResolveSourceCoordinate(center.X + x,
                            extent.OutputWidth, extent.SourceWidth),
                        ResolveSourceCoordinate(center.Y + y,
                            extent.OutputHeight, extent.SourceHeight));
                    double coefficient = Math.Exp(
                        -0.5 * (x * x + y * y) / (sigma * sigma));
                    brute[source] = brute.GetValueOrDefault(source) +
                        coefficient;
                }

                var collapsed = new Dictionary<(int X, int Y), double>();
                int minimumSourceX = ResolveSourceCoordinate(
                    center.X - radius, extent.OutputWidth, extent.SourceWidth);
                int maximumSourceX = ResolveSourceCoordinate(
                    center.X + radius, extent.OutputWidth, extent.SourceWidth);
                int minimumSourceY = ResolveSourceCoordinate(
                    center.Y - radius, extent.OutputHeight, extent.SourceHeight);
                int maximumSourceY = ResolveSourceCoordinate(
                    center.Y + radius, extent.OutputHeight, extent.SourceHeight);
                for (int sourceY = minimumSourceY;
                     sourceY <= maximumSourceY;
                     sourceY++)
                for (int sourceX = minimumSourceX;
                     sourceX <= maximumSourceX;
                     sourceX++)
                {
                    double xWeight = CombinedAxisWeight(sourceX, center.X,
                        extent.SourceWidth, extent.OutputWidth, radius, sigma);
                    double yWeight = CombinedAxisWeight(sourceY, center.Y,
                        extent.SourceHeight, extent.OutputHeight, radius, sigma);
                    collapsed[(sourceX, sourceY)] = xWeight * yWeight;
                }

                Assert.That(collapsed.Keys, Is.EquivalentTo(brute.Keys));
                foreach (var sample in brute)
                {
                    Assert.That(collapsed[sample.Key],
                        Is.EqualTo(sample.Value).Within(1.0e-12),
                        $"scale={extent.SourceWidth}x{extent.SourceHeight}/" +
                        $"{extent.OutputWidth}x{extent.OutputHeight}, " +
                        $"center={center}, radius={radius}, source={sample.Key}");
                }
            }
        }
    }

    [Test]
    public void GtaoApproximations_StayWithinNumericalErrorBudget()
    {
        const int functionSampleCount = 65_536;
        float maximumAngleError = 0.0f;
        float maximumAcosError = 0.0f;
        for (int i = 0; i <= functionSampleCount; i++)
        {
            float ratio = (float)i / functionSampleCount;
            maximumAngleError = MathF.Max(maximumAngleError, MathF.Max(
                MathF.Abs(ApproximateHorizonAngle(ratio, 1.0f) -
                    MathF.Atan(ratio)),
                MathF.Abs(ApproximateHorizonAngle(1.0f, ratio) -
                    MathF.Atan2(1.0f, ratio))));
            float value = -1.0f + 2.0f * ratio;
            float sineMagnitude = MathF.Sqrt(MathF.Max(
                1.0f - value * value, 0.0f));
            maximumAcosError = MathF.Max(maximumAcosError,
                MathF.Abs(ApproximateAcos(sineMagnitude, value) -
                    MathF.Acos(value)));
        }

        const int arcSampleCount = 256;
        float maximumArcError = 0.0f;
        bool allArcValuesFinite = true;
        for (int horizonIndex = 0; horizonIndex <= arcSampleCount * 2;
             horizonIndex++)
        {
            float exactHorizon = -MathF.PI +
                MathF.PI * horizonIndex / arcSampleCount;
            float horizonCosine = MathF.Cos(exactHorizon);
            float horizonSineMagnitude = MathF.Abs(MathF.Sin(exactHorizon));
            float approximateHorizon = exactHorizon < 0.0f
                ? -ApproximateAcos(horizonSineMagnitude, horizonCosine)
                : ApproximateAcos(horizonSineMagnitude, horizonCosine);
            for (int normalIndex = 0; normalIndex <= arcSampleCount;
                 normalIndex++)
            {
                float normalAngle = -MathF.PI * 0.5f +
                    MathF.PI * normalIndex / arcSampleCount;
                float normalSine = MathF.Sin(normalAngle);
                float normalCosine = MathF.Cos(normalAngle);
                float exact = 0.25f * (normalCosine +
                    2.0f * exactHorizon * normalSine -
                    MathF.Cos(2.0f * exactHorizon - normalAngle));
                float approximate = IntegrateGtaoArc(
                    approximateHorizon,
                    MathF.Sin(exactHorizon),
                    horizonCosine,
                    normalSine,
                    normalCosine);
                allArcValuesFinite &= float.IsFinite(approximate);
                maximumArcError = MathF.Max(maximumArcError,
                    MathF.Abs(approximate - exact));
            }
        }

        const int directionCount = 4_096;
        float maximumUnoccludedError = 0.0f;
        for (int tiltIndex = 0; tiltIndex <= 16; tiltIndex++)
        {
            float tilt = 1.4f * tiltIndex / 16.0f;
            float normalHorizontal = MathF.Sin(tilt);
            float normalView = MathF.Cos(tilt);
            double visibility = 0.0;
            for (int directionIndex = 0; directionIndex < directionCount;
                 directionIndex++)
            {
                float directionAngle = MathF.PI *
                    (directionIndex + 0.5f) / directionCount;
                float normalTangent = normalHorizontal *
                    MathF.Cos(directionAngle);
                float projectedLength = MathF.Sqrt(
                    normalTangent * normalTangent + normalView * normalView);
                float normalSine = normalTangent / projectedLength;
                float normalCosine = normalView / projectedLength;
                float h0Sine = -MathF.Sqrt(MathF.Max(
                    1.0f - normalSine * normalSine, 0.0f));
                float h1Sine = -h0Sine;
                float h0 = -ApproximateAcos(-h0Sine, normalSine);
                float h1 = ApproximateAcos(h1Sine, -normalSine);
                visibility += projectedLength * (
                    IntegrateGtaoArc(h0, h0Sine, normalSine,
                        normalSine, normalCosine) +
                    IntegrateGtaoArc(h1, h1Sine, -normalSine,
                        normalSine, normalCosine));
            }
            maximumUnoccludedError = MathF.Max(maximumUnoccludedError,
                MathF.Abs((float)(visibility / directionCount) - 1.0f));
        }

        Assert.Multiple(() =>
        {
            Assert.That(allArcValuesFinite, Is.True);
            Assert.That(maximumAngleError, Is.LessThanOrEqualTo(0.000005f));
            Assert.That(maximumAcosError, Is.LessThanOrEqualTo(0.000005f));
            Assert.That(maximumArcError, Is.LessThanOrEqualTo(0.00001f));
            Assert.That(maximumUnoccludedError,
                Is.LessThanOrEqualTo(0.00001f));
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

    private static float ApproximateHorizonAngle(float y, float x)
    {
        const float halfPi = MathF.PI * 0.5f;
        float absoluteY = MathF.Abs(y);
        float maximumComponent = MathF.Max(x, absoluteY);
        float minimumComponent = MathF.Min(x, absoluteY);
        float ratio = minimumComponent /
            MathF.Max(maximumComponent, 1.0e-20f);
        float ratioSquared = ratio * ratio;
        float polynomial = -0.013480470f;
        polynomial = polynomial * ratioSquared + 0.057477314f;
        polynomial = polynomial * ratioSquared - 0.121239071f;
        polynomial = polynomial * ratioSquared + 0.195635925f;
        polynomial = polynomial * ratioSquared - 0.332994597f;
        polynomial = polynomial * ratioSquared + 0.999995630f;
        float angle = polynomial * ratio;
        if (absoluteY > x)
            angle = halfPi - angle;
        return y < 0.0f ? -angle : angle;
    }

    private static float ApproximateAcos(
        float sineMagnitude,
        float cosine)
    {
        float boundedCosine = Math.Clamp(cosine, -1.0f, 1.0f);
        float absoluteCosine = MathF.Abs(boundedCosine);
        float acuteAngle = ApproximateHorizonAngle(
            sineMagnitude, MathF.Max(absoluteCosine, 1.0e-20f));
        return boundedCosine < 0.0f ? MathF.PI - acuteAngle : acuteAngle;
    }

    private static float IntegrateGtaoArc(
        float horizon,
        float horizonSine,
        float horizonCosine,
        float normalSine,
        float normalCosine)
    {
        float cosineDoubleHorizon = horizonCosine * horizonCosine -
            horizonSine * horizonSine;
        float sineDoubleHorizon = 2.0f * horizonSine * horizonCosine;
        float cosineDoubleHorizonMinusNormal =
            cosineDoubleHorizon * normalCosine +
            sineDoubleHorizon * normalSine;
        return 0.25f * (normalCosine +
            2.0f * horizon * normalSine -
            cosineDoubleHorizonMinusNormal);
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

    private static int ResolveSourceCoordinate(
        int outputCoordinate,
        int outputExtent,
        int sourceExtent)
    {
        int clampedOutput = Math.Clamp(
            outputCoordinate, 0, outputExtent - 1);
        return Math.Clamp((int)Math.Floor(
            (clampedOutput + 0.5) * sourceExtent / outputExtent),
            0,
            sourceExtent - 1);
    }

    private static double CombinedAxisWeight(
        int sourceCoordinate,
        int outputCoordinate,
        int sourceExtent,
        int outputExtent,
        int radius,
        double sigma)
    {
        double weight = 0.0;
        for (int offset = -2; offset <= 2; offset++)
        {
            if (Math.Abs(offset) > radius ||
                ResolveSourceCoordinate(
                    outputCoordinate + offset,
                    outputExtent,
                    sourceExtent) != sourceCoordinate)
            {
                continue;
            }
            weight += Math.Exp(-0.5 * offset * offset / (sigma * sigma));
        }
        return weight;
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
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
