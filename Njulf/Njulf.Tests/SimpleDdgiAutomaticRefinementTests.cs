using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiAutomaticRefinementTests
{
    [TestCase(64.0f, 0.0f, 0.0f, 0.0f,
        SimpleDdgiRefinementDemandReason.HighReceiverDensity)]
    [TestCase(0.0f, 1.0f, 0.0f, 0.0f,
        SimpleDdgiRefinementDemandReason.GeometricComplexity)]
    [TestCase(0.0f, 0.0f, 1.0f, 0.0f,
        SimpleDdgiRefinementDemandReason.LightingVariance)]
    [TestCase(0.0f, 0.0f, 0.0f, 8.0f,
        SimpleDdgiRefinementDemandReason.ObservedError)]
    public void StrongIndependentWitness_AdmitsAndRecordsItsReason(
        float receiverDensity,
        float geometry,
        float lighting,
        float error,
        SimpleDdgiRefinementDemandReason expectedReason)
    {
        bool admitted = SimpleDdgiAutomaticRefinementDemandBuilder.TryBuild(
            new Vector3(1.0f, 2.0f, 3.0f),
            new SimpleDdgiAutomaticRefinementMetrics(
                receiverDensity,
                geometry,
                lighting,
                error),
            17UL,
            out SimpleDdgiRefinementDemand demand);

        Assert.Multiple(() =>
        {
            Assert.That(admitted, Is.True);
            Assert.That(demand.Reason.HasFlag(expectedReason), Is.True);
            Assert.That(demand.Priority, Is.GreaterThan(144.0f));
            Assert.That(demand.StableSourceId, Is.EqualTo(17UL));
        });
    }

    [Test]
    public void ModerateCombinedWitnesses_AdmitWhileWeakNoiseDoesNot()
    {
        bool combined = SimpleDdgiAutomaticRefinementDemandBuilder.TryBuild(
            Vector3.Zero,
            new SimpleDdgiAutomaticRefinementMetrics(
                ReceiverDensity: 8.0f,
                GeometricComplexity: 0.5f,
                LightingVariance: 0.5f,
                ObservedError: 1.0f),
            0UL,
            out SimpleDdgiRefinementDemand demand);
        bool weak = SimpleDdgiAutomaticRefinementDemandBuilder.TryBuild(
            Vector3.Zero,
            new SimpleDdgiAutomaticRefinementMetrics(0.1f, 0.1f, 0.1f, 0.1f),
            0UL,
            out _);

        Assert.Multiple(() =>
        {
            Assert.That(combined, Is.True);
            Assert.That(demand.Reason, Is.Not.EqualTo(
                SimpleDdgiRefinementDemandReason.None));
            Assert.That(weak, Is.False);
        });
    }

    [Test]
    public void InvalidMetricsFailClosed()
    {
        Assert.That(
            SimpleDdgiAutomaticRefinementDemandBuilder.TryBuild(
                Vector3.Zero,
                new SimpleDdgiAutomaticRefinementMetrics(
                    float.NaN,
                    1.0f,
                    1.0f,
                    1.0f),
                0UL,
                out _),
            Is.False);
    }

    [Test]
    public void FrameEvidence_LocalizesGeometryAndNormalizesTailError()
    {
        DdgiDirtyRegion[] regions =
        [
            new DdgiDirtyRegion(
                new BoundingBox(
                    new Vector3(-0.5f, -0.01f, -0.5f),
                    new Vector3(0.5f, 0.01f, 0.5f)),
                DdgiDirtyReason.TransformChanged)
            {
                InfluenceBounds = new BoundingBox(
                    new Vector3(-0.5f, -0.01f, -0.5f),
                    new Vector3(0.5f, 0.01f, 0.5f))
            }
        ];
        var tail = new SimpleDdgiTransportTailSummary
        {
            Tolerance = 0.025f,
            RelativeTailBound = 0.05f
        };

        SimpleDdgiAutomaticRefinementMetrics metrics =
            SimpleDdgiFrameCoordinator.ResolveAutomaticRefinementMetrics(
                receiverDensity: 16.0f,
                regions,
                Vector3.Zero,
                nearRingSpacing: 1.0f,
                architecturalThickness: 0.08f,
                DdgiSceneInvalidationCoordinator.SimpleDdgiDirtyReasonLight,
                new Vector3(1.0f, 0.5f, 1.0f),
                tail);

        Assert.Multiple(() =>
        {
            Assert.That(metrics.ReceiverDensity, Is.EqualTo(16.0f));
            Assert.That(metrics.GeometricComplexity, Is.GreaterThan(0.7f));
            Assert.That(metrics.LightingVariance, Is.GreaterThanOrEqualTo(0.85f));
            Assert.That(metrics.ObservedError, Is.EqualTo(2.0f));
        });
    }
}
