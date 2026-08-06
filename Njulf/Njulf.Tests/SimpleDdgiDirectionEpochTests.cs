using System.Numerics;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiDirectionEpochTests
{
    [Test]
    public void CheckedInCodebook_IsNormalizedAndWrapsAtFiveBits()
    {
        for (uint epoch = 0; epoch < SimpleDdgiDirectionCodebook.RotationCount; epoch++)
        {
            Quaternion rotation = SimpleDdgiDirectionCodebook.GetRotation(epoch);
            Assert.That(rotation.Length(), Is.EqualTo(1.0f).Within(2.0e-6f), $"epoch {epoch}");
            Assert.That(SimpleDdgiDirectionCodebook.GetRotation(epoch + 32u), Is.EqualTo(rotation));
        }
    }

    [Test]
    public void MaintenanceSubset_MapsToExactFullSequenceIndices()
    {
        uint[] indices = Enumerable.Range(0, 16)
            .Select(local => SimpleDdgiDirectionCodebook.ResolveDirectionRayIndex(
                checked((uint)local), 16, 64, 128))
            .ToArray();

        Assert.That(indices, Is.EqualTo(Enumerable.Range(0, 16).Select(index => checked((uint)(index * 8)))));
        Assert.That(indices.Distinct().Count(), Is.EqualTo(indices.Length));
    }

    [Test]
    public void Reconstruction_IsEpochPersistentAndMatchesOctahedralPrecision()
    {
        Vector3 first = SimpleDdgiDirectionCodebook.ReconstructDirection(73, 91, 192, 7);
        Vector3 repeated = SimpleDdgiDirectionCodebook.ReconstructDirection(73, 91, 192, 7 + 32);
        Vector3 nextEpoch = SimpleDdgiDirectionCodebook.ReconstructDirection(73, 91, 192, 8);
        uint packed = SimpleDdgiTransportCachePacking.PackOctahedralSnorm16(first);
        Vector3 decoded = SimpleDdgiTransportCachePacking.UnpackOctahedralSnorm16(packed);

        Assert.Multiple(() =>
        {
            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(Vector3.Dot(first, decoded), Is.GreaterThan(0.999999f));
            Assert.That(Vector3.Dot(first, nextEpoch), Is.LessThan(0.999f));
        });
    }

    [Test]
    public void Reconstruction_AllEpochsAndSupportedRayCountsRemainFiniteAndUnitLength()
    {
        int[] rayCounts = [1, 8, 24, 32, 48, 64, 96, 128, 192, 256];
        uint[] probes = [0u, 73u, 32_767u];
        foreach (int rayCount in rayCounts)
        foreach (uint probe in probes)
        foreach (uint epoch in Enumerable.Range(0, 32).Select(value => (uint)value))
        {
            uint[] rays = [0u, (uint)(rayCount / 2), (uint)(rayCount - 1)];
            foreach (uint ray in rays.Distinct())
            {
                Vector3 direction = SimpleDdgiDirectionCodebook.ReconstructDirection(
                    probe,
                    ray,
                    checked((uint)rayCount),
                    epoch);
                Assert.That(float.IsFinite(direction.X) &&
                    float.IsFinite(direction.Y) &&
                    float.IsFinite(direction.Z), Is.True,
                    $"probe={probe}, rays={rayCount}, ray={ray}, epoch={epoch}");
                Assert.That(direction.LengthSquared(), Is.EqualTo(1.0f).Within(3.0e-6f));
            }
        }
    }

    [Test]
    public void MaintenanceSubset_RejectsInvalidSequenceContracts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(() => SimpleDdgiDirectionCodebook.ResolveDirectionRayIndex(
                1, 1, 8, 8), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiDirectionCodebook.ResolveDirectionRayIndex(
                0, 9, 8, 8), Throws.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(() => SimpleDdgiDirectionCodebook.ResolveDirectionRayIndex(
                0, 8, 9, 8), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    [Test]
    public void DirectionHistogram_OverflowBucketHasFinitePhysicalUpperBound()
    {
        SimpleDdgiStorageValidationCounters counters =
            SimpleDdgiStorageValidationCounters.Empty with
            {
                ReadbackValid = 1,
                DirectionComparisonSampleCount = 100,
                DirectionMaximumAngularErrorRadians = 0.001f,
                DirectionAngularErrorHistogram = [0, 0, 0, 0, 0, 0, 0, 100]
            };

        Assert.Multiple(() =>
        {
            Assert.That(float.IsFinite(counters.DirectionAngularErrorP99UpperBoundRadians), Is.True);
            Assert.That(counters.DirectionAngularErrorP99UpperBoundRadians,
                Is.EqualTo(0.0010005f).Within(1.0e-8f));
        });
    }
}
