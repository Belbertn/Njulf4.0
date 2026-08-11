using Njulf.Core.Math;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiRefinementBrickPoolTests
{
    private static readonly SimpleDdgiRefinementBrickConfiguration Configuration =
        new(
            Enabled: true,
            Capacity: 2,
            CountX: 6,
            CountY: 4,
            CountZ: 6,
            Spacing: 0.5f,
            RetentionFrames: 3);

    [Test]
    public void Update_DeduplicatesWorldCellAndUsesBoundedProbePool()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        SimpleDdgiRefinementDemand[] demands =
        [
            new(new Vector3(0.2f, 0.1f, 0.2f), 100f,
                SimpleDdgiRefinementDemandReason.VisibleReceiver),
            new(new Vector3(0.8f, 0.3f, 0.7f), 200f,
                SimpleDdgiRefinementDemandReason.CompactEmissive),
            new(new Vector3(20f, 0f, 0f), 150f,
                SimpleDdgiRefinementDemandReason.DynamicGeometry)
        ];

        IReadOnlyList<SimpleDdgiRefinementBrick> bricks =
            pool.Update(1, Configuration, demands);

        Assert.Multiple(() =>
        {
            Assert.That(bricks, Has.Count.EqualTo(2));
            Assert.That(bricks.Sum(static brick => brick.ProbeCount), Is.EqualTo(288));
            Assert.That(pool.Diagnostics.UniqueCandidateCount, Is.EqualTo(2));
            Assert.That(pool.Diagnostics.TopologyChanged, Is.True);
            Assert.That(bricks[0].Reasons.HasFlag(
                SimpleDdgiRefinementDemandReason.VisibleReceiver), Is.True);
            Assert.That(bricks[0].Reasons.HasFlag(
                SimpleDdgiRefinementDemandReason.CompactEmissive), Is.True);
        });
    }

    [Test]
    public void Hysteresis_RetainsBrickAcrossBoundaryAndExpiresAfterRetention()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        pool.Update(
            1,
            Configuration,
            [new(new Vector3(2.4f, 0.2f, 0.2f), 100f,
                SimpleDdgiRefinementDemandReason.VisibleReceiver)]);
        SimpleDdgiRefinementBrick initial = pool.ActiveBricks.Single();

        pool.Update(
            2,
            Configuration,
            [new(new Vector3(2.7f, 0.2f, 0.2f), 100f,
                SimpleDdgiRefinementDemandReason.VisibleReceiver)]);

        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks.Single().Key, Is.EqualTo(initial.Key));
            Assert.That(pool.Diagnostics.TopologyChanged, Is.False);
        });

        pool.Update(3, Configuration, Array.Empty<SimpleDdgiRefinementDemand>());
        pool.Update(4, Configuration, Array.Empty<SimpleDdgiRefinementDemand>());
        pool.Update(5, Configuration, Array.Empty<SimpleDdgiRefinementDemand>());
        Assert.That(pool.ActiveBricks, Has.Count.EqualTo(1));
        pool.Update(6, Configuration, Array.Empty<SimpleDdgiRefinementDemand>());
        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks, Is.Empty);
            Assert.That(pool.Diagnostics.EvictedBrickCount, Is.EqualTo(1));
            Assert.That(pool.Diagnostics.TopologyChanged, Is.True);
        });
    }

    [Test]
    public void Replacement_RequiresPriorityMarginButAllowsMateriallyStrongerDemand()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        pool.Update(
            1,
            Configuration with { Capacity = 1 },
            [new(new Vector3(0f), 100f,
                SimpleDdgiRefinementDemandReason.VisibleReceiver)]);
        SimpleDdgiRefinementBrickKey initial = pool.ActiveBricks.Single().Key;

        pool.Update(
            2,
            Configuration with { Capacity = 1 },
            [
                new(new Vector3(0f), 100f,
                    SimpleDdgiRefinementDemandReason.VisibleReceiver),
                new(new Vector3(20f, 0f, 0f), 110f,
                    SimpleDdgiRefinementDemandReason.CompactEmissive)
            ]);

        Assert.That(pool.ActiveBricks.Single().Key, Is.EqualTo(initial));

        pool.Update(
            3,
            Configuration with { Capacity = 1 },
            [
                new(new Vector3(0f), 100f,
                    SimpleDdgiRefinementDemandReason.VisibleReceiver),
                new(new Vector3(20f, 0f, 0f), 1_000f,
                    SimpleDdgiRefinementDemandReason.CompactEmissive)
            ]);
        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks.Single().Key, Is.Not.EqualTo(initial));
            Assert.That(pool.Diagnostics.EvictedBrickCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void DisabledConfiguration_ReleasesEverySlotWithoutAllocating()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        pool.Update(
            1,
            Configuration,
            [new(new Vector3(0f), 1f,
                SimpleDdgiRefinementDemandReason.AuthoredHero)]);

        IReadOnlyList<SimpleDdgiRefinementBrick> disabled = pool.Update(
            2,
            Configuration with { Enabled = false },
            Array.Empty<SimpleDdgiRefinementDemand>());

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.Empty);
            Assert.That(pool.Diagnostics.ProbeCount, Is.Zero);
            Assert.That(pool.Diagnostics.TopologyChanged, Is.True);
        });
    }
}
