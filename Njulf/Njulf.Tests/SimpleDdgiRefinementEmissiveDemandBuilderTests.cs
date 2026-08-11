using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiRefinementEmissiveDemandBuilderTests
{
    [Test]
    public void Build_AdmitsBrightCompactTriangleAtWorldCentroid()
    {
        GPUDdgiEmissiveSource source = Triangle(
            new Vector3(3f, 2f, 1f),
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 1f, 0f),
            new Vector3(8f),
            area: 1f);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandDiagnostics diagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                [source],
                new(250f, 2f, 8),
                destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Has.Count.EqualTo(1));
            Assert.That(destination[0].Position.X, Is.EqualTo(3f + 2f / 3f).Within(1e-5f));
            Assert.That(destination[0].Position.Y, Is.EqualTo(2f + 1f / 3f).Within(1e-5f));
            Assert.That(destination[0].Position.Z, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(destination[0].Reason, Is.EqualTo(
                SimpleDdgiRefinementDemandReason.CompactEmissive));
            Assert.That(diagnostics.EligibleSourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.AdmittedDemandCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Build_RejectsLargeOrDimTrianglesIndependently()
    {
        GPUDdgiEmissiveSource large = Triangle(
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitY,
            new Vector3(10f),
            area: 8f);
        GPUDdgiEmissiveSource dim = Triangle(
            new Vector3(4f, 0f, 0f),
            Vector3.UnitX,
            Vector3.UnitY,
            new Vector3(1f),
            area: 0.5f);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandDiagnostics diagnostics =
            SimpleDdgiRefinementEmissiveDemandBuilder.Build(
                [large, dim],
                new(250f, 2f, 8),
                destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Is.Empty);
            Assert.That(diagnostics.RejectedLargeSourceCount, Is.EqualTo(1));
            Assert.That(diagnostics.RejectedDimSourceCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Build_UsesBoundedStableTopKWithoutDependingOnInputOrder()
    {
        GPUDdgiEmissiveSource[] ascending = Enumerable.Range(1, 12)
            .Select(index => Triangle(
                new Vector3(index * 3f, 0f, 0f),
                Vector3.UnitX,
                Vector3.UnitY,
                new Vector3(index),
                area: 0.5f))
            .ToArray();
        GPUDdgiEmissiveSource[] descending = ascending.Reverse().ToArray();
        var left = new List<SimpleDdgiRefinementDemand>();
        var right = new List<SimpleDdgiRefinementDemand>();
        var configuration = new SimpleDdgiRefinementEmissiveDemandConfiguration(
            50f,
            2f,
            4);

        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            ascending,
            configuration,
            left);
        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            descending,
            configuration,
            right);

        Assert.Multiple(() =>
        {
            Assert.That(left, Has.Count.EqualTo(4));
            Assert.That(
                left.Select(static demand => demand.StableSourceId),
                Is.EqualTo(right.Select(static demand => demand.StableSourceId)));
            Assert.That(left[0].Priority, Is.GreaterThanOrEqualTo(left[^1].Priority));
        });
    }

    [Test]
    public void Build_ConvertsIntegratedMacroPowerToAreaNormalizedBrightness()
    {
        var macro = new DdgiVfxMacroEmitter(
            77,
            1,
            DdgiVfxMacroShape.Sphere,
            new Vector3(5f, 6f, 7f),
            Vector3.UnitY,
            new Vector3(0.1f),
            new Vector3(10f),
            new BoundingBox(new Vector3(4.9f, 5.9f, 6.9f), new Vector3(5.1f, 6.1f, 7.1f)),
            new BoundingBox(new Vector3(4.9f, 5.9f, 6.9f), new Vector3(5.1f, 6.1f, 7.1f)),
            AuthoredPower: true);
        var destination = new List<SimpleDdgiRefinementDemand>();

        SimpleDdgiRefinementEmissiveDemandBuilder.Build(
            [DdgiVfxMacroEmitterReducer.PackSource(macro)],
            new(250f, 1f, 8),
            destination);

        Assert.Multiple(() =>
        {
            Assert.That(destination, Has.Count.EqualTo(1));
            Assert.That(destination[0].Position, Is.EqualTo(macro.Center));
        });
    }

    [TestCase(false, false, true, true, true)]
    [TestCase(true, false, true, true, false)]
    [TestCase(false, true, true, true, false)]
    [TestCase(false, false, false, true, false)]
    [TestCase(false, false, true, false, false)]
    public void PublicationGate_IsAllOrNothing(
        bool invalidation,
        bool topologyChanged,
        bool certificationEnabled,
        bool certificateCurrent,
        bool expected)
    {
        Assert.That(
            SimpleDdgiRefinementPublication.CanPublishReceiverAuthority(
                invalidation,
                topologyChanged,
                certificationEnabled,
                certificateCurrent),
            Is.EqualTo(expected));
    }

    private static GPUDdgiEmissiveSource Triangle(
        Vector3 origin,
        Vector3 edge1,
        Vector3 edge2,
        Vector3 radiance,
        float area)
    {
        uint packed = (uint)DdgiEmissiveSourceFlags.Triangle <<
                      DdgiEmissiveTriangleTable.FlagsShift;
        return new GPUDdgiEmissiveSource
        {
            Vertex0Area = new Vector4(origin, area),
            Edge1AliasProbability = new Vector4(edge1, 1f),
            Edge2AliasFlags = new Vector4(
                edge2.X,
                edge2.Y,
                edge2.Z,
                BitConverter.UInt32BitsToSingle(packed)),
            RadianceSelectionProbability = new Vector4(radiance, 1f)
        };
    }
}
