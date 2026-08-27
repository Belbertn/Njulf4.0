using System;
using System.IO;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline.PipelineObjects;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class TransparentPipelinePartitioningTests
{
    [Test]
    public void Classifier_MapsDecalOrdinaryThinAndVolumeClasses()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                TransparentMaterialClassifier.Classify(
                    MaterialForwardClass.Transparent,
                    isGeometryDecal: true,
                    GiTransmissionPolicy.None),
                Is.EqualTo(TransparentMaterialClass.GeometryDecal));
            Assert.That(
                TransparentMaterialClassifier.Classify(
                    MaterialForwardClass.Transparent,
                    isGeometryDecal: false,
                    GiTransmissionPolicy.None),
                Is.EqualTo(TransparentMaterialClass.OrdinaryBlend));
            Assert.That(
                TransparentMaterialClassifier.Classify(
                    MaterialForwardClass.ThinGlass,
                    isGeometryDecal: false,
                    GiTransmissionPolicy.ThinSurface),
                Is.EqualTo(TransparentMaterialClass.OrdinaryBlend));
            Assert.That(
                TransparentMaterialClassifier.Classify(
                    MaterialForwardClass.ThickTransmission,
                    isGeometryDecal: false,
                    GiTransmissionPolicy.Volume),
                Is.EqualTo(TransparentMaterialClass.ThickTransmission));
        });
    }

    [Test]
    public void PackedDrawRange_RoundTripsBoundaryValuesWithoutClamping()
    {
        Assert.That(
            GPUForwardPushConstants.TryPackTransparentDrawRange(
                GPUForwardPushConstants.MaximumTransparentDrawBufferIndex,
                GPUForwardPushConstants.MaximumTransparentFirstDraw,
                out uint packed),
            Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(
                GPUForwardPushConstants
                    .UnpackTransparentDrawBufferBaseIndex(packed),
                Is.EqualTo(
                    GPUForwardPushConstants
                        .MaximumTransparentDrawBufferIndex));
            Assert.That(
                GPUForwardPushConstants.UnpackTransparentFirstDraw(packed),
                Is.EqualTo(
                    GPUForwardPushConstants.MaximumTransparentFirstDraw));
            Assert.That(
                GPUForwardPushConstants.TryPackTransparentDrawRange(
                    GPUForwardPushConstants
                        .MaximumTransparentDrawBufferIndex + 1u,
                    0u,
                    out _),
                Is.False);
            Assert.That(
                GPUForwardPushConstants.TryPackTransparentDrawRange(
                    BindlessIndex.TransparentMeshletDrawBufferBase,
                    GPUForwardPushConstants.MaximumTransparentFirstDraw +
                    1u,
                    out _),
                Is.False);
        });
    }

    [Test]
    public void Planner_CoversContiguousInputExactlyAndMergesEqualKeys()
    {
        var materialRuns = new[]
        {
            Run(0, 8, TransparentMaterialClass.OrdinaryBlend),
            Run(8, 8, TransparentMaterialClass.OrdinaryBlend),
            Run(16, 12, TransparentMaterialClass.ThickTransmission)
        };
        var planned = new TransparentDrawRun[
            TransparentDrawRunPlanner.DefaultMaximumRunCount];

        bool success = TransparentDrawRunPlanner.TryBuildRuns(
            materialRuns,
            28,
            DefaultOptions(),
            planned,
            out int count,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True, reason);
            Assert.That(count, Is.EqualTo(2));
            Assert.That(planned[0].FirstDraw, Is.Zero);
            Assert.That(planned[0].DrawCount, Is.EqualTo(16));
            Assert.That(planned[1].FirstDraw, Is.EqualTo(16));
            Assert.That(planned[1].DrawCount, Is.EqualTo(12));
            Assert.That(
                planned[0].DrawCount + planned[1].DrawCount,
                Is.EqualTo(28));
        });
    }

    [Test]
    public void Planner_ThickRayModeLeavesOrdinaryAndDecalRunsNonRay()
    {
        var materialRuns = new[]
        {
            Run(0, 8, TransparentMaterialClass.GeometryDecal),
            Run(8, 8, TransparentMaterialClass.OrdinaryBlend),
            Run(16, 8, TransparentMaterialClass.ThickTransmission)
        };
        var planned = new TransparentDrawRun[
            TransparentDrawRunPlanner.DefaultMaximumRunCount];

        bool success = TransparentDrawRunPlanner.TryBuildRuns(
            materialRuns,
            24,
            DefaultOptions(thickRay: true),
            planned,
            out int count,
            out string reason);

        Assert.Multiple(() =>
        {
            Assert.That(success, Is.True, reason);
            Assert.That(count, Is.EqualTo(3));
            Assert.That(planned[0].PipelineKey.RaySceneRequired, Is.False);
            Assert.That(planned[1].PipelineKey.RaySceneRequired, Is.False);
            Assert.That(planned[2].PipelineKey.RaySceneRequired, Is.True);
        });
    }

    [Test]
    public void Planner_LayeredPoliciesSelectOnlyTheirReceiverClasses()
    {
        var ordinary = new TransparentDrawClassification(
            TransparentMaterialClass.OrdinaryBlend,
            ReceivesSceneReflections: false);
        var decal = new TransparentDrawClassification(
            TransparentMaterialClass.GeometryDecal,
            ReceivesSceneReflections: false);
        var transparentPolicy = DefaultOptions(
            transparentLayeredRay: true);
        var decalPolicy = DefaultOptions(decalLayeredRay: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                TransparentDrawRunPlanner.CreatePipelineKey(
                    ordinary,
                    transparentPolicy).RaySceneRequired,
                Is.True);
            Assert.That(
                TransparentDrawRunPlanner.CreatePipelineKey(
                    decal,
                    transparentPolicy).RaySceneRequired,
                Is.False);
            Assert.That(
                TransparentDrawRunPlanner.CreatePipelineKey(
                    ordinary,
                    decalPolicy).RaySceneRequired,
                Is.False);
            Assert.That(
                TransparentDrawRunPlanner.CreatePipelineKey(
                    decal,
                    decalPolicy).RaySceneRequired,
                Is.True);
        });
    }

    [Test]
    public void Planner_FallsBackForTinyAlternatingRunsAndWeightedFeedback()
    {
        var tinyRuns = new[]
        {
            Run(0, 4, TransparentMaterialClass.OrdinaryBlend),
            Run(4, 4, TransparentMaterialClass.ThickTransmission)
        };
        var planned = new TransparentDrawRun[
            TransparentDrawRunPlanner.DefaultMaximumRunCount];

        bool tinySuccess = TransparentDrawRunPlanner.TryBuildRuns(
            tinyRuns,
            8,
            DefaultOptions(thickRay: true),
            planned,
            out _,
            out string tinyReason);
        bool feedbackSuccess = TransparentDrawRunPlanner.TryBuildRuns(
            new[]
            {
                Run(0, 8, TransparentMaterialClass.OrdinaryBlend)
            },
            8,
            DefaultOptions(
                mode: TransparencyMode.WeightedBlendedOit,
                exactFeedback: true),
            planned,
            out _,
            out string feedbackReason);

        Assert.Multiple(() =>
        {
            Assert.That(tinySuccess, Is.False);
            Assert.That(
                tinyReason,
                Is.EqualTo(
                    "transparent-run-minimum-length-governor"));
            Assert.That(feedbackSuccess, Is.False);
            Assert.That(
                feedbackReason,
                Is.EqualTo(
                    "transparent-run-exact-feedback-requires-canonical-order"));
        });
    }

    [Test]
    public void PipelineLookup_UsesBoundedRoleArtifactNames()
    {
        var ordinaryRay = new TransparentPipelineKey(
            TransparentMaterialClass.OrdinaryBlend,
            TransparencyMode.SortedAlphaBlend,
            RaySceneRequired: true,
            ExactReceiverFeedbackRequired: false,
            DecalReceiverCacheRequired: false);
        var weightedDecalCache = new TransparentPipelineKey(
            TransparentMaterialClass.GeometryDecal,
            TransparencyMode.WeightedBlendedOit,
            RaySceneRequired: false,
            ExactReceiverFeedbackRequired: false,
            DecalReceiverCacheRequired: true);

        Assert.Multiple(() =>
        {
            Assert.That(
                MeshPipeline.ResolveTransparentPartitionFragmentShader(
                    ordinaryRay),
                Is.EqualTo(
                    "forward_transparent_ordinary_ray.frag.spv"));
            Assert.That(
                MeshPipeline.ResolveTransparentPartitionFragmentShader(
                    weightedDecalCache),
                Is.EqualTo(
                    "forward_weighted_oit_decal_cache_required.frag.spv"));
            Assert.That(ordinaryRay.CacheIndex,
                Is.InRange(0,
                    TransparentPipelineKey.CacheEntryCount - 1));
        });
    }

    [Test]
    public void ShaderContracts_UnpackRangesAndDeclareEveryRoleVariant()
    {
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string task = ReadRepoText("Njulf.Shaders", "forward.task");
        string mesh = ReadRepoText("Njulf.Shaders", "forward.mesh");
        string fragment = ReadRepoText("Njulf.Shaders", "forward.frag");
        string project = ReadRepoText(
            "Njulf.Shaders",
            "Njulf.Shaders.csproj");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain(
                "FORWARD_TRANSPARENT_DRAW_BUFFER_INDEX_BITS = 14u"));
            Assert.That(task, Does.Contain(
                "ForwardTransparentFirstDraw("));
            Assert.That(mesh, Does.Contain(
                "ForwardDrawBufferBaseIndex("));
            Assert.That(fragment, Does.Contain(
                "FORWARD_TRANSPARENT_ROLE_ORDINARY"));
            Assert.That(fragment, Does.Contain(
                "FORWARD_TRANSPARENT_ROLE_DECAL"));
            Assert.That(fragment, Does.Contain(
                "FORWARD_TRANSPARENT_ROLE_THICK"));
            Assert.That(project, Does.Contain(
                "forward_transparent_ordinary_ray.frag"));
            Assert.That(project, Does.Contain(
                "forward_weighted_oit_decal_cache_required.frag"));
        });
    }

    private static TransparentMaterialRun Run(
        int firstDraw,
        int drawCount,
        TransparentMaterialClass materialClass) =>
        new(
            firstDraw,
            drawCount,
            new TransparentDrawClassification(
                materialClass,
                ReceivesSceneReflections: false));

    private static TransparentRunPlanningOptions DefaultOptions(
        TransparencyMode mode = TransparencyMode.SortedAlphaBlend,
        bool transparentLayeredRay = false,
        bool decalLayeredRay = false,
        bool thickRay = false,
        bool reflectionRay = false,
        bool exactFeedback = false,
        bool decalCache = false) =>
        new(
            mode,
            transparentLayeredRay,
            decalLayeredRay,
            thickRay,
            reflectionRay,
            exactFeedback,
            decalCache);

    private static string ReadRepoText(params string[] pathParts)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                new[] { directory.FullName }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate repository source file.");
    }
}
