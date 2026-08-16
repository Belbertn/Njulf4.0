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
    public void OriginDemand_ProducesSymmetricWorldKeyedBrick()
    {
        var pool = new SimpleDdgiRefinementBrickPool();

        pool.Update(
            1,
            Configuration,
            [new(new Vector3(0f), 100f,
                SimpleDdgiRefinementDemandReason.VisibleReceiver)]);

        SimpleDdgiRefinementBrick brick = pool.ActiveBricks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(brick.Key, Is.EqualTo(
                new SimpleDdgiRefinementBrickKey(0, 0, 0)));
            Assert.That(brick.Origin, Is.EqualTo(
                new Vector3(-1.25f, -0.75f, -1.25f)));
            Assert.That(brick.Origin + brick.LatticeSize * 0.5f,
                Is.EqualTo(Vector3.Zero));
        });
    }

    [Test]
    public void CompactEmitterBounds_PlaceEveryProbeRowAboveTheFloor()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        SimpleDdgiRefinementBrickConfiguration configuration = Configuration with
        {
            Capacity = 1,
            CountY = 6,
            Spacing = 0.59375f
        };
        var sourceBounds = new BoundingBox(
            new Vector3(0.8f, 0.13f, 1.55f),
            new Vector3(1.7f, 1.03f, 2.45f));
        var demand = new SimpleDdgiRefinementDemand(
            new Vector3(1.25f, 0.58f, 2f),
            200f,
            SimpleDdgiRefinementDemandReason.CompactEmissive,
            42UL)
        {
            SourceBounds = sourceBounds
        };

        pool.Update(1, configuration, [demand]);

        SimpleDdgiRefinementBrick brick = pool.ActiveBricks.Single();
        Assert.Multiple(() =>
        {
            Assert.That(brick.Key.PlacementClass, Is.EqualTo(1));
            Assert.That(brick.Origin.X, Is.EqualTo(-0.296875f).Within(1e-6f));
            Assert.That(brick.Origin.Y, Is.EqualTo(0.1484375f).Within(1e-6f));
            Assert.That(brick.Origin.Z, Is.EqualTo(0.4453125f).Within(1e-6f));
            Assert.That(brick.Origin.Y, Is.GreaterThan(0f));
            Assert.That(brick.Origin.Y, Is.GreaterThanOrEqualTo(sourceBounds.Min.Y));
        });
    }

    [Test]
    public void PlacementClasses_DoNotAliasAtTheSameWorldPosition()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        Vector3 position = new(1.25f, 0.58f, 2f);
        var compact = new SimpleDdgiRefinementDemand(
            position,
            200f,
            SimpleDdgiRefinementDemandReason.CompactEmissive,
            2UL)
        {
            SourceBounds = new BoundingBox(
                new Vector3(0.8f, 0.13f, 1.55f),
                new Vector3(1.7f, 1.03f, 2.45f))
        };

        pool.Update(
            1,
            Configuration,
            [
                new SimpleDdgiRefinementDemand(
                    position,
                    100f,
                    SimpleDdgiRefinementDemandReason.VisibleReceiver,
                    1UL),
                compact
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks, Has.Count.EqualTo(2));
            Assert.That(
                pool.ActiveBricks.Select(static brick => brick.Key.PlacementClass),
                Is.EquivalentTo(new[] { 0, 1 }));
            Assert.That(pool.Diagnostics.UniqueCandidateCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CompactEmitterPlacement_IsStableUntilItsQuantizedOriginChanges()
    {
        var pool = new SimpleDdgiRefinementBrickPool();
        var demand = new SimpleDdgiRefinementDemand(
            new Vector3(1.25f, 0.58f, 2f),
            200f,
            SimpleDdgiRefinementDemandReason.CompactEmissive,
            42UL)
        {
            SourceBounds = new BoundingBox(
                new Vector3(0.8f, 0.13f, 1.55f),
                new Vector3(1.7f, 1.03f, 2.45f))
        };

        pool.Update(1, Configuration with { Capacity = 1 }, [demand]);
        SimpleDdgiRefinementBrickKey initial = pool.ActiveBricks.Single().Key;
        pool.Update(2, Configuration with { Capacity = 1 }, [demand]);

        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks.Single().Key, Is.EqualTo(initial));
            Assert.That(pool.Diagnostics.TopologyChanged, Is.False);
        });

        var moved = demand with
        {
            Position = demand.Position + new Vector3(0.2f, 0f, 0f),
            SourceBounds = new BoundingBox(
                demand.SourceBounds!.Value.Min + new Vector3(0.2f, 0f, 0f),
                demand.SourceBounds.Value.Max + new Vector3(0.2f, 0f, 0f))
        };
        pool.Update(3, Configuration with { Capacity = 1 }, [moved]);

        Assert.Multiple(() =>
        {
            Assert.That(pool.ActiveBricks.Single().Key, Is.Not.EqualTo(initial));
            Assert.That(pool.Diagnostics.TopologyChanged, Is.True);
            Assert.That(pool.Diagnostics.EvictedBrickCount, Is.EqualTo(1));
        });
    }

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
