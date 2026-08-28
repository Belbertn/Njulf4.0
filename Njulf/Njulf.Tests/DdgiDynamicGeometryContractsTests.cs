using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiDynamicGeometryContractsTests
{
    private static readonly DdgiDynamicGeometryFrameContext Context =
        new(17UL, 3UL, 1);

    [Test]
    public void ValidSubmissionIsAccepted()
    {
        Assert.That(
            DdgiDynamicGeometrySubmissionValidator.Validate(
                ValidSubmission(),
                Context),
            Is.EqualTo(DdgiDynamicGeometrySubmissionDisposition.Accepted));
    }

    [Test]
    public void FrameAndGenerationMustMatchCollectionToken()
    {
        DdgiDynamicGeometrySubmission submission = ValidSubmission() with
        {
            FrameSerial = Context.FrameSerial + 1UL
        };

        Assert.That(
            DdgiDynamicGeometrySubmissionValidator.Validate(
                submission,
                Context),
            Is.EqualTo(
                DdgiDynamicGeometrySubmissionDisposition.FrameIdentityMismatch));
    }

    [TestCase(2u)]
    [TestCase(4u)]
    public void NonTriangleIndexCountsFailClosed(uint indexCount)
    {
        DdgiDynamicGeometrySubmission submission = ValidSubmission() with
        {
            IndexCount = indexCount
        };

        Assert.That(
            DdgiDynamicGeometrySubmissionValidator.Validate(
                submission,
                Context),
            Is.EqualTo(
                DdgiDynamicGeometrySubmissionDisposition.InvalidTopology));
    }

    [Test]
    public void InfluenceBoundsMustContainPreviousAndCurrentGeometry()
    {
        DdgiDynamicGeometrySubmission submission = ValidSubmission() with
        {
            InfluenceBounds = new BoundingBox(
                new Vector3(-0.5f),
                new Vector3(0.5f))
        };

        Assert.That(
            DdgiDynamicGeometrySubmissionValidator.Validate(
                submission,
                Context),
            Is.EqualTo(
                DdgiDynamicGeometrySubmissionDisposition.InvalidBoundsOrTransform));
    }

    [Test]
    public void PositionLayoutMustFitTheDeclaredStride()
    {
        DdgiDynamicGeometrySubmission submission = ValidSubmission() with
        {
            VertexStride = 16U,
            PositionOffset = 8U
        };

        Assert.That(
            DdgiDynamicGeometrySubmissionValidator.Validate(
                submission,
                Context),
            Is.EqualTo(
                DdgiDynamicGeometrySubmissionDisposition.InvalidVertexLayout));
    }

    [Test]
    public void DynamicDescriptorArenaIsDisjointAcrossFrameSlots()
    {
        int frame0Last = BindlessIndex.GetDdgiDynamicGeometryIndexBufferIndex(
            0,
            BindlessIndex.DdgiDynamicGeometryMaximumSubmissionsPerFrame - 1);
        int frame1First =
            BindlessIndex.GetDdgiDynamicGeometryVertexBufferIndex(1, 0);

        Assert.That(frame1First, Is.EqualTo(frame0Last + 1));
        Assert.That(
            BindlessIndex.GetDdgiDynamicGeometryIndexBufferIndex(
                RenderingConstants.FramesInFlight - 1,
                BindlessIndex.DdgiDynamicGeometryMaximumSubmissionsPerFrame - 1),
            Is.LessThan(BindlessIndex.StaticBufferCount));
    }

    [Test]
    public void ProductionFairnessKeepsFoliageAtQuarterShare()
    {
        DdgiDynamicGeometryClassBudget foliage =
            DdgiDynamicGeometryBudgetPolicy.Production.For(
                DdgiDynamicGeometryContentClass.Foliage);
        DdgiDynamicGeometryClassBudget skinned =
            DdgiDynamicGeometryBudgetPolicy.Production.For(
                DdgiDynamicGeometryContentClass.Skinned);

        Assert.Multiple(() =>
        {
            Assert.That(foliage.Weight, Is.EqualTo(1));
            Assert.That(foliage.MaximumMixedShare, Is.EqualTo(0.25));
            Assert.That(skinned.Weight, Is.EqualTo(4));
            Assert.That(skinned.MaximumMixedShare, Is.EqualTo(1.0));
        });
    }

    [Test]
    public void GpuBudgetGovernorBootstrapsThenUsesConservativeFenceFeedback()
    {
        var governor = new DdgiDynamicGeometryGpuBudgetGovernor();

        Assert.That(
            governor.ResolveMaximumBuilds(12, 750),
            Is.EqualTo(12));
        Assert.That(governor.Observe(600, 4), Is.True);
        Assert.That(
            governor.ResolveMaximumBuilds(12, 750),
            Is.EqualTo(12),
            "One delayed sample must not abruptly throttle startup.");
        Assert.That(governor.Observe(800, 4), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(governor.SampleCount, Is.EqualTo(2));
            Assert.That(
                governor.ConservativeMicrosecondsPerBuild,
                Is.GreaterThan(governor.EstimatedMicrosecondsPerBuild));
            Assert.That(
                governor.ResolveMaximumBuilds(12, 750),
                Is.InRange(2, 4));
        });
    }

    [Test]
    public void GpuBudgetGovernorIgnoresInvalidSamplesAndNeverStarvesProgress()
    {
        var governor = new DdgiDynamicGeometryGpuBudgetGovernor();

        Assert.That(governor.Observe(0, 2), Is.False);
        Assert.That(governor.Observe(400, 0), Is.False);
        Assert.That(governor.Observe(4_000, 1), Is.True);
        Assert.That(governor.Observe(4_000, 1), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(governor.SampleCount, Is.EqualTo(2));
            Assert.That(governor.ResolveMaximumBuilds(8, 750), Is.EqualTo(1));
            Assert.That(governor.ResolveMaximumBuilds(0, 750), Is.Zero);
        });
    }

    private static DdgiDynamicGeometrySubmission ValidSubmission() => new()
    {
        StableSourceId = 0x1234UL,
        GeometryPartId = 7U,
        ContentClass = DdgiDynamicGeometryContentClass.TopologyChanging,
        FrameSerial = Context.FrameSerial,
        ResourceGeneration = Context.RaySceneResourceGeneration,
        VertexBuffer = new BufferHandle(11, 2U),
        IndexBuffer = new BufferHandle(12, 2U),
        VertexOffset = 4U,
        VertexCount = 24U,
        VertexStride = 80U,
        PositionOffset = 0U,
        NormalOffset = 16U,
        TangentOffset = 48U,
        TexCoord0Offset = 32U,
        TexCoord1Offset = 40U,
        ColorOffset = 64U,
        IndexOffset = 3U,
        IndexCount = 36U,
        VertexFormat = DdgiRayVertexFormat.InterleavedGpuVertex,
        Material = new MaterialHandle(2, 1U),
        WorldMatrix = Matrix4x4.Identity,
        LocalBounds = new BoundingBox(new Vector3(-1.0f), new Vector3(1.0f)),
        PreviousWorldBounds = new BoundingBox(
            new Vector3(-1.0f),
            new Vector3(1.0f)),
        CurrentWorldBounds = new BoundingBox(
            new Vector3(-0.75f),
            new Vector3(1.25f)),
        InfluenceBounds = new BoundingBox(
            new Vector3(-1.0f),
            new Vector3(1.25f)),
        TransformRevision = 4UL,
        TopologyRevision = 5UL,
        DeformationRevision = 6UL,
        BuildPreference = DdgiDynamicGeometryBuildPreference.RebuildRequired
    };
}
