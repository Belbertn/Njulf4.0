using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiSegmentSelectorTests
{
    [Test]
    public void SegmentAabbTest_RejectsOutsideAndBehindBounds()
    {
        Vector3 origin = Vector3.Zero;
        Vector3 direction = Vector3.UnitX;

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiSegmentSelector.IntersectsSegment(
                origin,
                direction,
                10.0f,
                new BoundingBox(
                    new Vector3(3.0f, -1.0f, -1.0f),
                    new Vector3(4.0f, 1.0f, 1.0f))), Is.True);
            Assert.That(SimpleDdgiSegmentSelector.IntersectsSegment(
                origin,
                direction,
                10.0f,
                new BoundingBox(
                    new Vector3(3.0f, 2.0f, -1.0f),
                    new Vector3(4.0f, 3.0f, 1.0f))), Is.False);
            Assert.That(SimpleDdgiSegmentSelector.IntersectsSegment(
                origin,
                direction,
                10.0f,
                new BoundingBox(
                    new Vector3(-4.0f, -1.0f, -1.0f),
                    new Vector3(-3.0f, 1.0f, 1.0f))), Is.False);
        });
    }

    [Test]
    public void SegmentAabbTest_UncertainInputIsConservativelyTouched()
    {
        Assert.That(SimpleDdgiSegmentSelector.IntersectsSegment(
            Vector3.Zero,
            new Vector3(float.NaN, 0.0f, 0.0f),
            10.0f,
            new BoundingBox(Vector3.Zero, Vector3.One)), Is.True);
    }

    [Test]
    public void MaterialEdit_RetracesOnlyIntersectingPrimarySegment()
    {
        DdgiDirtyRegion region = CreateRegion(DdgiDirtyReason.MaterialChanged);
        var regions = new List<DdgiDirtyRegion> { region };

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiSegmentSelector.RequiresRetrace(
                Vector3.Zero,
                Vector3.UnitX,
                10.0f,
                cachedSurfaceHit: true,
                probeSpacing: 1.0f,
                regions), Is.True);
            Assert.That(SimpleDdgiSegmentSelector.RequiresRetrace(
                Vector3.Zero,
                Vector3.UnitY,
                10.0f,
                cachedSurfaceHit: true,
                probeSpacing: 1.0f,
                regions), Is.False);
        });
    }

    [Test]
    public void StructuralEdit_RetracesSurfaceHitsForShadowSafety()
    {
        var regions = new List<DdgiDirtyRegion>
        {
            CreateRegion(DdgiDirtyReason.TransformChanged)
        };

        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiSegmentSelector.RequiresRetrace(
                Vector3.Zero,
                Vector3.UnitY,
                10.0f,
                cachedSurfaceHit: true,
                probeSpacing: 1.0f,
                regions), Is.True);
            Assert.That(SimpleDdgiSegmentSelector.RequiresRetrace(
                Vector3.Zero,
                Vector3.UnitY,
                10.0f,
                cachedSurfaceHit: false,
                probeSpacing: 1.0f,
                regions), Is.False);
        });
    }

    [Test]
    public void UnsupportedEmitterEdit_ForcesFullRetrace()
    {
        var regions = new List<DdgiDirtyRegion>
        {
            CreateRegion(DdgiDirtyReason.EmissiveChanged)
        };

        Assert.That(SimpleDdgiSegmentSelector.RequiresRetrace(
            Vector3.Zero,
            Vector3.UnitY,
            10.0f,
            cachedSurfaceHit: false,
            probeSpacing: 1.0f,
            regions), Is.True);
    }

    [Test]
    public void DirtyRegionGpuRecord_CarriesInfluenceAndSweptBounds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                Marshal.SizeOf<GPUSimpleDdgiSchedulerDirtyRegion>(),
                Is.EqualTo(80));
            Assert.That(
                Marshal.OffsetOf<GPUSimpleDdgiSchedulerDirtyRegion>(
                    nameof(GPUSimpleDdgiSchedulerDirtyRegion.SegmentMinimum))
                    .ToInt32(),
                Is.EqualTo(32));
            Assert.That(
                Marshal.OffsetOf<GPUSimpleDdgiSchedulerDirtyRegion>(
                    nameof(GPUSimpleDdgiSchedulerDirtyRegion.ReasonFlags))
                    .ToInt32(),
                Is.EqualTo(64));
            Assert.That(
                SimpleDdgiGpuSchedulerLayout.DirtyRegionStrideBytes,
                Is.EqualTo(80));
        });
    }

    private static DdgiDirtyRegion CreateRegion(DdgiDirtyReason reason)
    {
        BoundingBox swept = new(
            new Vector3(3.0f, -0.5f, -0.5f),
            new Vector3(4.0f, 0.5f, 0.5f));
        return new DdgiDirtyRegion(swept, reason)
        {
            OldWorldBounds = swept,
            NewWorldBounds = swept,
            InfluenceBounds = new BoundingBox(
                new Vector3(-1.0f, -1.0f, -1.0f),
                new Vector3(8.0f, 8.0f, 8.0f))
        };
    }
}
