using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DdgiMutationJournalTests
{
    [Test]
    public void UnchangedScene_DrainsNoEventsAndPerformsNoAdditionalScan()
    {
        using var scene = new Scene();
        var renderObject = new RenderObject
        {
            LocalMeshBounds = UnitBounds
        };
        scene.Add(renderObject);
        var materialSource = new TestMaterialMutationSource();
        var lightSource = new TestLightMutationSource();
        using var journal = new DdgiMutationJournal(
            materialSource,
            lightSource);

        journal.AttachScene(scene);
        IReadOnlyList<DdgiDirtyRegion> bootstrap = journal.Drain(SceneBounds);
        int bootstrapRegionCount = bootstrap.Count;
        DdgiMutationJournalTelemetry afterBootstrap = journal.Telemetry;
        IReadOnlyList<DdgiDirtyRegion> unchanged = journal.Drain(SceneBounds);
        DdgiMutationJournalTelemetry afterUnchanged = journal.Telemetry;

        Assert.Multiple(() =>
        {
            Assert.That(bootstrapRegionCount, Is.EqualTo(1));
            Assert.That(unchanged, Is.Empty);
            Assert.That(afterBootstrap.SceneAttachScanCount, Is.EqualTo(1));
            Assert.That(afterBootstrap.SceneAttachObjectCount, Is.EqualTo(1));
            Assert.That(afterUnchanged.SceneAttachScanCount,
                Is.EqualTo(afterBootstrap.SceneAttachScanCount));
            Assert.That(afterUnchanged.SceneAttachObjectCount,
                Is.EqualTo(afterBootstrap.SceneAttachObjectCount));
            Assert.That(afterUnchanged.PendingEventCount, Is.Zero);
        });
    }

    [Test]
    public void RenderObjectTransform_RecordsExactOldAndNewBoundsOnce()
    {
        using var scene = new Scene();
        var renderObject = new RenderObject
        {
            LocalMeshBounds = UnitBounds
        };
        scene.Add(renderObject);
        using var journal = CreateJournal();
        journal.AttachScene(scene);
        journal.Drain(SceneBounds);

        renderObject.Position = new Vector3(10f, 0f, 0f);
        renderObject.Position = new Vector3(10f, 0f, 0f);
        IReadOnlyList<DdgiDirtyRegion> regions = journal.Drain(SceneBounds);

        Assert.That(regions, Has.Count.EqualTo(1));
        DdgiDirtyRegion region = regions[0];
        Assert.Multiple(() =>
        {
            Assert.That(region.Reason, Is.EqualTo(DdgiDirtyReason.TransformChanged));
            Assert.That(region.OldWorldBounds.Min, Is.EqualTo(new Vector3(-1f)));
            Assert.That(region.NewWorldBounds.Min, Is.EqualTo(new Vector3(9f, -1f, -1f)));
            Assert.That(region.Bounds.Min, Is.EqualTo(new Vector3(-1f)));
            Assert.That(region.Bounds.Max, Is.EqualTo(new Vector3(11f, 1f, 1f)));
        });
    }

    [Test]
    public void BoundsFreeProducer_UsesLastPublishedBoundsForSweptMove()
    {
        using var scene = new Scene();
        var batch = new StaticInstanceBatch(new[] { Matrix4x4.Identity });
        scene.Add(batch);
        using var journal = CreateJournal();
        journal.AttachScene(scene);

        static DdgiMutationResolution Resolve(SceneMutation mutation)
        {
            if (mutation.Producer is not StaticInstanceBatch batch)
                return default;
            Vector3 center = batch.WorldMatrices[0].Translation;
            var bounds = new BoundingBox(center - Vector3.One, center + Vector3.One);
            return new DdgiMutationResolution(
                null,
                bounds,
                null,
                DdgiDirtyReason.Unknown,
                IgnoreWhenUntracked: true);
        }

        journal.Drain(SceneBounds, Resolve);
        batch.ReplaceWorldMatrices(new[]
        {
            Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f))
        });
        IReadOnlyList<DdgiDirtyRegion> regions =
            journal.Drain(SceneBounds, Resolve);

        Assert.That(regions, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(regions[0].OldWorldBounds.Min,
                Is.EqualTo(new Vector3(-1f)));
            Assert.That(regions[0].NewWorldBounds.Min,
                Is.EqualTo(new Vector3(9f, -1f, -1f)));
            Assert.That(regions[0].InfluenceBounds.Min,
                Is.EqualTo(new Vector3(-2f)));
            Assert.That(regions[0].InfluenceBounds.Max,
                Is.EqualTo(new Vector3(12f, 2f, 2f)));
        });
    }

    [Test]
    public void EventOverflow_ProducesOneDiagnosedGlobalFallback()
    {
        using var scene = new Scene();
        for (int index = 0; index < 3; index++)
        {
            scene.Add(new RenderObject
            {
                LocalMeshBounds = UnitBounds,
                Position = new Vector3(index * 4f, 0f, 0f)
            });
        }
        var materialSource = new TestMaterialMutationSource();
        var lightSource = new TestLightMutationSource();
        using var journal = new DdgiMutationJournal(
            materialSource,
            lightSource,
            eventCapacity: 2);

        journal.AttachScene(scene);
        IReadOnlyList<DdgiDirtyRegion> regions = journal.Drain(SceneBounds);
        DdgiMutationJournalTelemetry telemetry = journal.Telemetry;

        Assert.Multiple(() =>
        {
            Assert.That(regions, Has.Count.EqualTo(1));
            Assert.That(regions[0].Reason, Is.EqualTo(DdgiDirtyReason.Teleport));
            Assert.That(regions[0].InfluenceBounds, Is.EqualTo(SceneBounds));
            Assert.That(telemetry.OverflowCount, Is.EqualTo(1));
            Assert.That(telemetry.ConservativeFallbackCount, Is.EqualTo(1));
            Assert.That(telemetry.OverflowedThisFrame, Is.True);
        });
    }

    [Test]
    public void EmissiveMaterialEdit_FansOutToTrackedMaterialUsers()
    {
        var handle = new MaterialHandle(7, 2);
        using var scene = new Scene();
        var first = new RenderObject
        {
            Material = handle,
            LocalMeshBounds = UnitBounds
        };
        var second = new RenderObject
        {
            Material = handle,
            LocalMeshBounds = UnitBounds,
            Position = new Vector3(16f, 0f, 0f)
        };
        scene.Add(first);
        scene.Add(second);
        var materialSource = new TestMaterialMutationSource();
        var lightSource = new TestLightMutationSource();
        using var journal = new DdgiMutationJournal(
            materialSource,
            lightSource,
            coalescingBrickSize: 4f);
        journal.AttachScene(scene);
        journal.Drain(SceneBounds);

        materialSource.Publish(new MaterialChangedEvent(
            handle,
            MaterialChangeMask.Emission,
            new MaterialAspectRevisions(2, 2, 9, 2, 2, 2, 2)));
        IReadOnlyList<DdgiDirtyRegion> regions = journal.Drain(SceneBounds);

        Assert.Multiple(() =>
        {
            Assert.That(regions, Has.Count.EqualTo(2));
            Assert.That(regions, Has.All.Property(nameof(DdgiDirtyRegion.Reason))
                .EqualTo(DdgiDirtyReason.EmissiveChanged));
            Assert.That(regions, Has.All.Property(nameof(DdgiDirtyRegion.SourceRevision))
                .EqualTo(9));
        });
    }

    [Test]
    public void LocalAndDirectionalLightChanges_UseRegionalThenGlobalInvalidation()
    {
        using var scene = new Scene();
        var materialSource = new TestMaterialMutationSource();
        var lightSource = new TestLightMutationSource();
        using var journal = new DdgiMutationJournal(
            materialSource,
            lightSource);
        journal.AttachScene(scene);
        journal.Drain(SceneBounds);

        Guid localId = Guid.NewGuid();
        var local = new Light
        {
            Type = LightType.Point,
            Position = new System.Numerics.Vector3(5f, 0f, 0f),
            Range = 3f
        };
        lightSource.Publish(new LightMutation(
            2,
            LightMutationKind.Added,
            default,
            localId,
            null,
            local));
        DdgiDirtyRegion localRegion = journal.Drain(SceneBounds)[0];

        var directional = new Light { Type = LightType.Directional };
        lightSource.Publish(new LightMutation(
            3,
            LightMutationKind.Added,
            default,
            Guid.NewGuid(),
            null,
            directional));
        IReadOnlyList<DdgiDirtyRegion> directionalRegions = journal.Drain(SceneBounds);

        Assert.Multiple(() =>
        {
            Assert.That(localRegion.Reason,
                Is.EqualTo(DdgiDirtyReason.LocalLightChanged));
            Assert.That(localRegion.InfluenceBounds.Min,
                Is.EqualTo(new Vector3(1f, -4f, -4f)));
            Assert.That(localRegion.InfluenceBounds.Max,
                Is.EqualTo(new Vector3(9f, 4f, 4f)));
            Assert.That(directionalRegions, Has.Count.EqualTo(1));
            Assert.That(directionalRegions[0].Reason,
                Is.EqualTo(DdgiDirtyReason.Teleport));
            Assert.That(directionalRegions[0].InfluenceBounds,
                Is.EqualTo(SceneBounds));
        });
    }

    [Test]
    public void SceneClear_PublishesOneConservativeGlobalMutation()
    {
        using var scene = new Scene();
        scene.Add(new RenderObject { LocalMeshBounds = UnitBounds });
        using var journal = CreateJournal();
        journal.AttachScene(scene);
        journal.Drain(SceneBounds);

        scene.Clear();
        IReadOnlyList<DdgiDirtyRegion> regions = journal.Drain(SceneBounds);

        Assert.That(regions, Has.Count.EqualTo(1));
        Assert.That(regions[0].Reason, Is.EqualTo(DdgiDirtyReason.Teleport));
    }

    private static DdgiMutationJournal CreateJournal() =>
        new(new TestMaterialMutationSource(), new TestLightMutationSource());

    private static BoundingBox UnitBounds { get; } =
        new(new Vector3(-1f), new Vector3(1f));

    private static BoundingBox SceneBounds { get; } =
        new(new Vector3(-100f), new Vector3(100f));

    private sealed class TestMaterialMutationSource : IDdgiMaterialMutationSource
    {
        public event Action<MaterialChangedEvent>? MaterialChanged;
        public void Publish(MaterialChangedEvent changed) =>
            MaterialChanged?.Invoke(changed);
    }

    private sealed class TestLightMutationSource : IDdgiLightMutationSource
    {
        public event Action<LightMutation>? Changed;
        public void Publish(LightMutation changed) => Changed?.Invoke(changed);
    }
}
