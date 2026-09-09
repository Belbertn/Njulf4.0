using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SecondarySceneSnapshotTests
{
    private static readonly MeshHandle Mesh = new(0, 1);
    private static readonly MaterialHandle Material = new(0, 1);

    private sealed class Fixture : IDisposable
    {
        internal readonly Scene Scene = new();
        internal readonly SecondarySceneSnapshot Snapshot;
        internal readonly Dictionary<MaterialHandle, (SecondarySceneSnapshot.MaterialData Data, uint Revision)> Materials = [];
        internal int MeshReads;
        internal int MaterialReads;
        internal uint MaterialRevision = 1;

        internal Fixture()
        {
            Materials.Add(Material, (new(0, new(), MaterialForwardClass.SimpleOpaque), 1));
            Snapshot = new(Material, mesh =>
            {
                MeshReads++;
                return new MeshInfo
                {
                    BoundingBoxMin = new(-1), BoundingBoxMax = new(1),
                    MeshletOffset = checked((uint)mesh.Index), MeshletCount = 1
                };
            }, handle => { MaterialReads++; return Materials[handle].Data; }, handle => Materials[handle].Revision);
        }

        internal IReadOnlyList<SecondarySceneSnapshot.Instance> Prepare()
        {
            Snapshot.BeginSubmission(Scene, MaterialRevision);
            return Snapshot.Prepare();
        }

        public void Dispose() { Snapshot.Dispose(); Scene.Dispose(); }
    }

    [Test]
    public void PreparationIsLazyAndSharedAcrossViewsAndUnchangedSubmissions()
    {
        using var f = new Fixture();
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        f.Scene.Add(new StaticInstanceBatch([Matrix4x4.Identity, Matrix4x4.Identity]) { Mesh = Mesh, Material = Material });
        f.Snapshot.BeginSubmission(f.Scene, 1);
        Assert.That(f.MeshReads + f.MaterialReads, Is.Zero);

        var instances = f.Snapshot.Prepare();
        Assert.Multiple(() =>
        {
            Assert.That(instances.Select(i => i.InstanceId), Is.EqualTo(new uint[] { 0, 1, 2, 3 }));
            Assert.That(f.MeshReads, Is.EqualTo(3));
            Assert.That(f.MaterialReads, Is.EqualTo(1));
            Assert.That(instances[0].Material, Is.SameAs(instances[3].Material));
        });
        Assert.That(f.Snapshot.Prepare(), Is.SameAs(instances));
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int submission = 0; submission < 100; submission++) f.Prepare();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Multiple(() =>
        {
            Assert.That(f.MeshReads, Is.EqualTo(3));
            Assert.That(f.MaterialReads, Is.EqualTo(1));
            Assert.That(allocated, Is.Zero, "An unchanged snapshot must not allocate each submission.");
        });
    }

    [Test]
    public void TransformChangesRefreshOnlyTheChangedProducerAtTheNextSubmission()
    {
        using var f = new Fixture();
        var moving = new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material));
        f.Scene.Add(moving);
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        var entries = f.Prepare();
        moving.WorldMatrix = Matrix4x4.CreateScale(new Vector3(-2, 3, 4)) *
                             Matrix4x4.CreateTranslation(new Vector3(10, 20, 30));
        Assert.That(f.Snapshot.Prepare()[0].Bounds.Center, Is.EqualTo(Vector3.Zero), "The current submission stays frozen.");
        f.Prepare();
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Bounds.Min, Is.EqualTo(new Vector3(8, 17, 26)));
            Assert.That(entries[0].Bounds.Max, Is.EqualTo(new Vector3(12, 23, 34)));
            Assert.That(entries[1].Bounds.Center, Is.EqualTo(Vector3.Zero));
            Assert.That(f.MeshReads, Is.EqualTo(3));
            Assert.That(f.MaterialReads, Is.EqualTo(1));
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public void InPlaceMaterialEditsRefreshSharedClassificationWithoutRebuildingBounds(bool notify)
    {
        using var f = new Fixture();
        MaterialHandle other = new(1, 1);
        f.Materials.Add(other, (new(1, new(), MaterialForwardClass.SimpleOpaque), 1));
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(other)));
        var entries = f.Prepare();
        var metadata = new MaterialRenderMetadata { BlendMode = MaterialBlendMode.AlphaBlend, DecalLayer = 7 };
        f.Materials[Material] = (new(0, metadata, MaterialForwardClass.Transparent), 2);
        if (notify)
            f.Snapshot.OnMaterialChanged(new(Material, MaterialChangeMask.All, MaterialAspectRevisions.Initial));
        else
            f.MaterialRevision++; // A revision-only change must also invalidate a cached record.
        Assert.That(f.Snapshot.Prepare()[0].Material.Data.Family, Is.EqualTo(MaterialForwardClass.SimpleOpaque));
        f.Prepare();
        Assert.Multiple(() =>
        {
            Assert.That(entries[0].Material.Data.Metadata, Is.EqualTo(metadata));
            Assert.That(entries[1].Material.Data.Family, Is.EqualTo(MaterialForwardClass.Transparent));
            Assert.That(entries[2].Material.Data.Family, Is.EqualTo(MaterialForwardClass.SimpleOpaque));
            Assert.That(f.MeshReads, Is.EqualTo(3));
            Assert.That(f.MaterialReads, Is.EqualTo(3), "Only the changed material is resolved again.");
        });
    }

    [Test]
    public void MembershipVisibilityMeshAndBatchChangesPreserveSubmissionOrder()
    {
        using var f = new Fixture();
        var first = new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material));
        var second = new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material));
        var batch = new StaticInstanceBatch([Matrix4x4.Identity, Matrix4x4.Identity]) { Mesh = Mesh, Material = Material };
        f.Scene.Add(first); f.Scene.Add(second); f.Scene.Add(batch);
        f.Prepare();
        first.Visible = false;
        second.Mesh = TestGraphicsResources.Mesh(new MeshHandle(3, 1));
        batch.ReplaceWorldMatrices([Matrix4x4.CreateTranslation(new Vector3(4, 0, 0))]);
        var entries = f.Prepare();
        Assert.Multiple(() =>
        {
            Assert.That(entries.Select(i => i.InstanceId), Is.EqualTo(new uint[] { 0, 1 }));
            Assert.That(entries[0].Mesh, Is.EqualTo(new MeshHandle(3, 1)));
            Assert.That(entries[1].Bounds.Center, Is.EqualTo(new Vector3(4, 0, 0)));
            Assert.That(entries[1].LodKey, Is.EqualTo(new SecondaryLodInstanceKey(batch.Id, 0, Mesh)));
        });
        first.Visible = true;
        second.Mesh = null;
        batch.ReplaceWorldMatrices([Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity]);
        Assert.That(f.Prepare().Select(i => i.InstanceId), Is.EqualTo(new uint[] { 0, 1, 2, 3 }));
        f.Scene.Remove(first);
        f.Scene.Remove(batch);
        Assert.That(f.Prepare(), Is.Empty);
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        Assert.That(f.Prepare().Single().InstanceId, Is.Zero);
    }

    [Test]
    public void MaterialReplacementEvictsUnusedHandlesIncludingReusedIndices()
    {
        using var f = new Fixture();
        var obj = new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material));
        f.Scene.Add(obj);
        f.Prepare();
        MaterialHandle replacement = new(0, 2);
        f.Materials.Remove(Material);
        f.Materials.Add(replacement, (new(0, new(), MaterialForwardClass.FullOpaque), 2));
        obj.Material = TestGraphicsResources.Material(replacement);
        f.MaterialRevision++;
        Assert.That(f.Prepare().Single().Material.Data.Family, Is.EqualTo(MaterialForwardClass.FullOpaque));
        f.MaterialRevision++;
        Assert.DoesNotThrow(() => f.Prepare(), "The retired generation must no longer be queried.");
    }

    [Test]
    public void SkinningStateAndBindTransformsAreInvalidated()
    {
        using var f = new Fixture();
        var obj = new SkinnedRenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)) { SkinningEnabled = true };
        f.Scene.Add(obj);
        var entry = f.Prepare().Single();
        Assert.That(entry.Deforming, Is.True);
        obj.SkinningBindTransform = Matrix4x4.CreateTranslation(new Vector3(10, 0, 0));
        obj.SkinningEnabled = false;
        f.Prepare();
        Assert.Multiple(() =>
        {
            Assert.That(entry.Deforming, Is.False);
            Assert.That(entry.Bounds.Center, Is.EqualTo(new Vector3(10, 0, 0)));
        });
    }

    [Test]
    public void SnapshotRetainsGeometryOutsideTheFirstViewsFrustum()
    {
        using var f = new Fixture();
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)));
        f.Scene.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)) { Position = new Vector3(10, 0, 0) });
        var entries = f.Prepare();
        Frustum first = SceneDataBuilder.ExtractFrustum(Matrix4x4.Identity);
        Frustum second = SceneDataBuilder.ExtractFrustum(Matrix4x4.CreateTranslation(new Vector3(-10, 0, 0)));
        Assert.Multiple(() =>
        {
            Assert.That(SecondaryViewVisibility.IsVisible(entries[0].Bounds, first, default), Is.True);
            Assert.That(SecondaryViewVisibility.IsVisible(entries[1].Bounds, first, default), Is.False);
            Assert.That(SecondaryViewVisibility.IsVisible(entries[0].Bounds, second, default), Is.False);
            Assert.That(SecondaryViewVisibility.IsVisible(entries[1].Bounds, second, default), Is.True);
            Assert.That(entries.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void SceneReplacementAndDisposalDetachPreviousSources()
    {
        using var f = new Fixture();
        var old = new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material));
        f.Scene.Add(old);
        f.Prepare();
        using var replacement = new Scene();
        replacement.Add(new RenderObject(TestGraphicsResources.Mesh(Mesh), TestGraphicsResources.Material(Material)) { Position = new Vector3(20, 0, 0) });
        f.Snapshot.BeginSubmission(replacement, 1);
        Assert.That(f.Snapshot.Prepare().Single().Bounds.Center, Is.EqualTo(new Vector3(20, 0, 0)));
        old.Position = new Vector3(50, 0, 0);
        f.Snapshot.BeginSubmission(replacement, 1);
        f.Snapshot.Prepare();
        Assert.That(f.MeshReads, Is.EqualTo(2));
        f.Snapshot.Dispose();
        old.Position = Vector3.Zero;
        Assert.Throws<InvalidOperationException>(() => f.Snapshot.Prepare());
    }
}
