using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Graphics;
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
    public void DdgiHighDefaults_EnableHighGtaoAndBentNormalLighting()
    {
        var settings = new RenderSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.AmbientOcclusion.Mode,
                Is.EqualTo(AmbientOcclusionMode.Gtao));
            Assert.That(settings.AmbientOcclusion.BentNormalMode,
                Is.EqualTo(AmbientOcclusionBentNormalMode.EnvironmentAndDdgi));
            Assert.That(settings.AmbientOcclusion.EffectiveBentNormalMode,
                Is.EqualTo(AmbientOcclusionBentNormalMode.EnvironmentAndDdgi));
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
                AmbientOcclusionBentNormalMode.EnvironmentAndDdgi,
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
                Is.EqualTo(96));
            Assert.That((flags >> 16) & 0x3fu, Is.EqualTo(11u));
            Assert.That((flags >> 22) & 0x03u,
                Is.EqualTo((uint)
                    AmbientOcclusionBentNormalMode.EnvironmentOnly));
            Assert.That((flags >> 24) & 1u, Is.EqualTo(1u));
            Assert.That(BindlessIndex.GtaoFilteredTexture,
                Is.EqualTo(
                    BindlessIndex.OpaqueSceneColorSnapshotTexture + 1));
            Assert.That(BindlessIndex.GtaoReferenceNormalTexture,
                Is.EqualTo(
                    BindlessIndex.OpaqueSceneColorSnapshotTextureB + 1));
            Assert.That(BindlessIndex.FirstDynamicTextureIndex,
                Is.EqualTo(BindlessIndex.GtaoReferenceNormalTexture + 1));
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
        string forward = ForwardShaderSource.Read().ReplaceLineEndings("\n");
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
                "dot(tapNormal, normal) < relaxedNormalThreshold"));
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
    public void SpatialReconstruction_ConsumesFullResolutionDepthWithNeutralFallbacks()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string renderingDirectory = FindRepoDirectory("Njulf.Rendering");
        string spatial = File.ReadAllText(Path.Combine(shaderDirectory,
            "gtao_spatial.comp")).ReplaceLineEndings("\n");
        string temporal = File.ReadAllText(Path.Combine(shaderDirectory,
            "gtao_temporal.comp")).ReplaceLineEndings("\n");
        string forward = ForwardShaderSource.Read().ReplaceLineEndings("\n");
        string passes = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "GtaoPasses.cs"));
        string graph = File.ReadAllText(Path.Combine(renderingDirectory,
            "Pipeline", "ProductionRenderPipelineDeclaration.cs"));

        int spatialPassStart = graph.IndexOf(
            "Pass(\"GtaoSpatialPass\"", StringComparison.Ordinal);
        int spatialPassEnd = graph.IndexOf(
            "Pass(\"TiledLightCullingPass\"", StringComparison.Ordinal);
        Assert.That(spatialPassStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(spatialPassEnd, Is.GreaterThan(spatialPassStart));
        string spatialPassDeclaration = graph[
            spatialPassStart..spatialPassEnd];

        Assert.Multiple(() =>
        {
            // A1: the output pixel's full-resolution depth, not the nearest
            // packed texel, drives the reconstruction.
            Assert.That(spatial, Does.Contain(
                "uniform sampler2D SceneDepthInput;"));
            Assert.That(spatial, Does.Contain("ReconstructViewDepth("));
            Assert.That(spatial, Does.Contain("ResolveFootprint("));
            Assert.That(spatial, Does.Contain(
                "vec4 centerGeometry = vec4(footprint.referenceNormal, outputViewDepth);"));
            // A1: the footprint carries the center tap's Gaussian weight so
            // the kernel must not count it twice; radius 0 must still
            // reconstruct per pixel rather than pass a nearest texel
            // through.
            Assert.That(spatial, Does.Contain("float weightSum = 1.0;"));
            Assert.That(spatial, Does.Contain("if (x == 0 && y == 0)"));
            Assert.That(spatial, Does.Contain(
                "max(spatialWeight - 1.0, 0.0)"));
            // A2: rejected pixels emit neutral AO with zero bent-normal
            // confidence instead of a neighboring surface's payload.
            Assert.That(spatial, Does.Contain("EmitNeutralAo(pixel);"));
            // A3: temporal history is validated across its bilinear
            // footprint instead of a single geometry texel.
            Assert.That(temporal, Does.Contain(
                "PreviousGeometryHistory, tapPixel, 0).xy"));
            Assert.That(temporal, Does.Not.Contain(
                "textureLod(PreviousHistory"));
            // Phase B: the spatial pass publishes the filtered bent normal in
            // absolute view space together with the reference normal it
            // measured against, and the forward pass transfers that bounded
            // delta as a shortest-arc rotation of the material shading
            // normal, never a replacement, so normal-map detail survives the
            // indirect diffuse term and no re-derived azimuth enters the
            // lobe.
            Assert.That(forward, Does.Contain(
                "const float GTAO_BENT_NORMAL_MAX_BEND_ANGLE = 0.7854;"));
            Assert.That(forward, Does.Not.Contain(
                "ResolveGtaoReferenceFrame"));
            Assert.That(forward, Does.Contain(
                "GTAO_REFERENCE_NORMAL_TEXTURE_INDEX"));
            Assert.That(forward, Does.Contain(
                "vec3 bendAxis = cross(referenceNormal, bentNormal);"));
            Assert.That(forward, Does.Contain(
                "TryResolveIndirectDiffuseNormal(\n" +
                "        normal,\n" +
                "        diffuseIndirectNormal);"));
            Assert.That(spatial, Does.Contain(
                "EncodeOctahedral(bentNormal)"));
            Assert.That(spatial, Does.Contain(
                "EncodeOctahedral(footprint.referenceNormal)"));
            Assert.That(spatial, Does.Not.Contain(
                "ResolveReferenceFrame"));
            Assert.That(spatial, Does.Contain(
                "GtaoReferenceNormalOutput"));
            // C2: the spatial radius follows the shared blur-radius setting,
            // capped at the kernel's shared-memory halo.
            Assert.That(passes, Does.Contain("GtaoMaxSpatialRadius"));
            Assert.That(passes, Does.Contain(
                "_settings.AmbientOcclusion.BlurRadius,"));
            Assert.That(spatialPassDeclaration, Does.Contain(
                "ReadComputeDepth(RenderGraphResourceId.SceneDepth)"));
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
    public void CollapsedSpatialKernel_ExcludesReconstructionCenterTapExactly()
    {
        // Mirrors gtao_spatial.comp: the reconstruction already carries the
        // center offset's Gaussian(0) weight, so the collapsed kernel minus
        // one at the center source texel must equal the brute-force kernel
        // evaluated without the (0, 0) offset.
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
                (X: extent.OutputWidth / 2, Y: extent.OutputHeight / 2)
            };
            foreach (var center in centers)
            foreach (int radius in new[] { 0, 1, 2 })
            {
                double sigma = Math.Max(radius, 1);
                int centerSourceX = ResolveSourceCoordinate(
                    center.X, extent.OutputWidth, extent.SourceWidth);
                int centerSourceY = ResolveSourceCoordinate(
                    center.Y, extent.OutputHeight, extent.SourceHeight);

                var brute = new Dictionary<(int X, int Y), double>();
                for (int y = -radius; y <= radius; y++)
                for (int x = -radius; x <= radius; x++)
                {
                    if (x == 0 && y == 0)
                        continue;
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
                    double spatialWeight = xWeight * yWeight;
                    if (sourceX == centerSourceX && sourceY == centerSourceY)
                        spatialWeight = Math.Max(spatialWeight - 1.0, 0.0);
                    if (spatialWeight > 0.0)
                        collapsed[(sourceX, sourceY)] = spatialWeight;
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

    [Test]
    public void GtaoDither_IsStratifiedSpatiotemporalAndGuardsTheConstantDitherBug()
    {
        // A C# mirror of gtao.comp's dither index arithmetic, asserting what
        // the review of a1468e0 had to measure by hand. Two failed dither
        // attempts justify the fixture: the guards must fail against the
        // superseded IGN scheme and pass against the stratified one.
        string raw = File.ReadAllText(Path.Combine(
            FindRepoDirectory("Njulf.Shaders"), "gtao.comp"));

        const int blockSize = 64;
        const int temporalWindow = 24;

        Assert.Multiple(() =>
        {
            // The shader must carry the stratified scheme the mirror
            // reflects; the superseded gradient-noise dither must be gone.
            Assert.That(raw, Does.Contain(
                "(((pixel.x + pixel.y) & 3) << 2) + (pixel.x & 3)"));
            Assert.That(raw, Does.Contain(
                "(pixel.y - pixel.x) & 3"));
            Assert.That(raw, Does.Contain(
                "uint rotationIndex = pc.FrameIndex % 6u;"));
            Assert.That(raw, Does.Contain(
                "uint offsetIndex = (pc.FrameIndex / 6u) % 4u;"));
            Assert.That(raw, Does.Not.Contain(
                "InterleavedGradientNoise"));
            Assert.That(raw, Does.Not.Contain("pc.FrameIndex & 63u"));

            // (b) Every 4x4 tile is a complete direction stratum, so any
            // 4x4 neighbourhood the spatial filter covers sees all 16
            // direction indices.
            Assert.That(EveryTileHasSixteenDirectionIndices(blockSize),
                Is.True);

            // (a) The direction and offset channels are decorrelated: low
            // rank correlation, and the circular channel difference takes
            // several distinct values per frame instead of one locked
            // constant, with the full window of temporal rotations and
            // offset shifts spreading it across the unit interval.
            Assert.That(MaxAbsoluteRankCorrelation(
                    blockSize, temporalWindow, SampleStratifiedGtaoDither),
                Is.LessThan(0.5));
            Assert.That(MinimumDistinctChannelDifferences(
                    blockSize, temporalWindow, SampleStratifiedGtaoDither),
                Is.GreaterThanOrEqualTo(4));
            Assert.That(PooledDistinctChannelDifferences(
                    blockSize, temporalWindow, SampleStratifiedGtaoDither),
                Is.GreaterThanOrEqualTo(12));

            // (c) Consecutive frames never advance both channels by the
            // same delta for every pixel: the rotation and offset
            // sequences are independent, so the joint pattern is not a
            // rigid translation of a single number.
            Assert.That(CountLockedTemporalTransitions(
                    blockSize, temporalWindow, SampleStratifiedGtaoDither),
                Is.EqualTo(0));
        });

        // The same guards have teeth: the superseded IGN dither locks the
        // offset channel to a near-constant of the direction channel (guard
        // a, two distinct differences across pixels and the whole window)
        // and advances both channels identically every frame (guard c),
        // exactly the two defects the stratified scheme exists to remove.
        Assert.Multiple(() =>
        {
            Assert.That(MinimumDistinctChannelDifferences(
                    blockSize, temporalWindow,
                    SampleInterleavedGradientGtaoDither),
                Is.LessThanOrEqualTo(2));
            Assert.That(PooledDistinctChannelDifferences(
                    blockSize, temporalWindow,
                    SampleInterleavedGradientGtaoDither),
                Is.LessThanOrEqualTo(2));
            Assert.That(CountLockedTemporalTransitions(
                    blockSize, temporalWindow,
                    SampleInterleavedGradientGtaoDither),
                Is.EqualTo(temporalWindow - 1));
        });
    }

    private delegate (float Direction, float Offset) DitherSampler(
        int pixelX, int pixelY, uint temporalIndex);

    private static (float Direction, float Offset) SampleStratifiedGtaoDither(
        int pixelX,
        int pixelY,
        uint temporalIndex)
    {
        float directionNoise = (1.0f / 16.0f) *
            ((((pixelX + pixelY) & 3) << 2) + (pixelX & 3));
        float offsetNoise = (1.0f / 4.0f) * ((pixelY - pixelX) & 3);
        uint rotationIndex = temporalIndex % 6u;
        uint offsetIndex = (temporalIndex / 6u) % 4u;
        directionNoise = Fract(directionNoise +
            rotationIndex * (1.0f / 6.0f));
        offsetNoise = Fract(offsetNoise + offsetIndex * 0.25f);
        return (directionNoise, offsetNoise);
    }

    private static (float Direction, float Offset)
        SampleInterleavedGradientGtaoDither(
            int pixelX,
            int pixelY,
            uint temporalIndex)
    {
        // The dither a1468e0 shipped: two IGN evaluations over one
        // position. The temporal phase and the spatial shift both reduce
        // to constants, so the second channel is a locked function of the
        // first and every frame advances both identically.
        float x = pixelX + temporalIndex * 0.61803398875f;
        float y = pixelY + temporalIndex * 0.30901699437f;
        return (
            InterleavedGradientNoise(x, y),
            InterleavedGradientNoise(x + 5.588238f, y + 5.588238f));
    }

    private static float InterleavedGradientNoise(float x, float y) =>
        Fract(52.9829189f * Fract(x * 0.06711056f + y * 0.00583715f));

    private static float Fract(float value) =>
        value - MathF.Floor(value);

    private static bool EveryTileHasSixteenDirectionIndices(int blockSize)
    {
        for (int tileY = 0; tileY < blockSize; tileY += 4)
        {
            for (int tileX = 0; tileX < blockSize; tileX += 4)
            {
                var distinct = new HashSet<int>();
                for (int y = tileY; y < tileY + 4; y++)
                {
                    for (int x = tileX; x < tileX + 4; x++)
                        distinct.Add((((x + y) & 3) << 2) + (x & 3));
                }
                if (distinct.Count != 16)
                    return false;
            }
        }
        return true;
    }

    private static float MaxAbsoluteRankCorrelation(
        int blockSize,
        int temporalWindow,
        DitherSampler sample)
    {
        float maximum = 0.0f;
        for (uint temporalIndex = 0;
             temporalIndex < temporalWindow;
             temporalIndex++)
        {
            int pixelCount = blockSize * blockSize;
            var direction = new float[pixelCount];
            var offset = new float[pixelCount];
            int index = 0;
            for (int y = 0; y < blockSize; y++)
            {
                for (int x = 0; x < blockSize; x++)
                {
                    (direction[index], offset[index]) =
                        sample(x, y, temporalIndex);
                    index++;
                }
            }
            maximum = MathF.Max(maximum, (float)Math.Abs(
                SpearmanRankCorrelation(direction, offset)));
        }
        return maximum;
    }

    private static double SpearmanRankCorrelation(float[] a, float[] b)
    {
        double[] ranksA = AverageRanks(a);
        double[] ranksB = AverageRanks(b);
        double meanA = 0.0;
        double meanB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            meanA += ranksA[i];
            meanB += ranksB[i];
        }
        meanA /= a.Length;
        meanB /= b.Length;
        double covariance = 0.0;
        double varianceA = 0.0;
        double varianceB = 0.0;
        for (int i = 0; i < a.Length; i++)
        {
            double deltaA = ranksA[i] - meanA;
            double deltaB = ranksB[i] - meanB;
            covariance += deltaA * deltaB;
            varianceA += deltaA * deltaA;
            varianceB += deltaB * deltaB;
        }
        return covariance / Math.Sqrt(varianceA * varianceB);
    }

    private static double[] AverageRanks(float[] values)
    {
        var order = new int[values.Length];
        for (int i = 0; i < order.Length; i++)
            order[i] = i;
        Array.Sort(order, (x, y) => values[x].CompareTo(values[y]));
        var ranks = new double[values.Length];
        int start = 0;
        while (start < order.Length)
        {
            int end = start;
            while (end + 1 < order.Length &&
                values[order[end + 1]] == values[order[start]])
                end++;
            double averageRank = (start + end) * 0.5 + 1.0;
            for (int i = start; i <= end; i++)
                ranks[order[i]] = averageRank;
            start = end + 1;
        }
        return ranks;
    }

    private static int MinimumDistinctChannelDifferences(
        int blockSize,
        int temporalWindow,
        DitherSampler sample)
    {
        int minimum = int.MaxValue;
        for (uint temporalIndex = 0;
             temporalIndex < temporalWindow;
             temporalIndex++)
        {
            var differences = new float[blockSize * blockSize];
            int index = 0;
            for (int y = 0; y < blockSize; y++)
            {
                for (int x = 0; x < blockSize; x++)
                {
                    var (direction, offset) =
                        sample(x, y, temporalIndex);
                    differences[index++] = Fract(offset - direction + 1.0f);
                }
            }
            Array.Sort(differences);
            int distinct = 1;
            for (int i = 1; i < differences.Length; i++)
            {
                if (differences[i] - differences[i - 1] > 2.0e-3f)
                    distinct++;
            }
            minimum = Math.Min(minimum, distinct);
        }
        return minimum;
    }

    private static int PooledDistinctChannelDifferences(
        int blockSize,
        int temporalWindow,
        DitherSampler sample)
    {
        var differences = new float[blockSize * blockSize * temporalWindow];
        int index = 0;
        for (uint temporalIndex = 0;
             temporalIndex < temporalWindow;
             temporalIndex++)
        {
            for (int y = 0; y < blockSize; y++)
            {
                for (int x = 0; x < blockSize; x++)
                {
                    var (direction, offset) =
                        sample(x, y, temporalIndex);
                    differences[index++] =
                        Fract(offset - direction + 1.0f);
                }
            }
        }
        Array.Sort(differences);
        int distinct = 1;
        for (int i = 1; i < differences.Length; i++)
        {
            if (differences[i] - differences[i - 1] > 2.0e-3f)
                distinct++;
        }
        return distinct;
    }

    private static int CountLockedTemporalTransitions(
        int blockSize,
        int temporalWindow,
        DitherSampler sample)
    {
        const float lockTolerance = 0.005f;
        const float lockFraction = 0.8f;
        int lockedTransitions = 0;
        for (uint temporalIndex = 0;
             temporalIndex + 1 < temporalWindow;
             temporalIndex++)
        {
            int lockedPixels = 0;
            for (int y = 0; y < blockSize; y++)
            {
                for (int x = 0; x < blockSize; x++)
                {
                    var (direction0, offset0) =
                        sample(x, y, temporalIndex);
                    var (direction1, offset1) =
                        sample(x, y, temporalIndex + 1);
                    float directionDelta =
                        CircularDelta(direction1, direction0);
                    float offsetDelta = CircularDelta(offset1, offset0);
                    float channelDifference = MathF.Abs(
                        CircularDelta(directionDelta, offsetDelta));
                    if (MathF.Min(channelDifference,
                            1.0f - channelDifference) < lockTolerance)
                        lockedPixels++;
                }
            }
            if (lockedPixels >=
                (int)(lockFraction * blockSize * blockSize))
                lockedTransitions++;
        }
        return lockedTransitions;
    }

    private static float CircularDelta(float next, float current) =>
        Fract(next - current + 0.5f) - 0.5f;

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
