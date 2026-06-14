using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public class GpuSceneManagerTests
{
    [Test]
    public void RegisterUpdateRemove_RejectsStaleObjectIds()
    {
        var scene = new GpuSceneManager();
        GpuObjectId first = scene.RegisterObject(CreateDesc());
        scene.RemoveObject(first);
        GpuObjectId second = scene.RegisterObject(CreateDesc());

        Assert.That(second.Index, Is.EqualTo(first.Index));
        Assert.That(second.Generation, Is.Not.EqualTo(first.Generation));
        Assert.Throws<InvalidOperationException>(() => scene.UpdateObjectTransform(first, Matrix4x4.Identity));
    }

    [Test]
    public void MovingOneObject_UploadsOnlyOneTransformRange()
    {
        var scene = new GpuSceneManager();
        GpuObjectId first = scene.RegisterObject(CreateDesc());
        scene.RegisterObject(CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        scene.UpdateObjectTransform(first, Matrix4x4.CreateTranslation(new Vector3(10f, 0f, 0f)));
        GpuSceneUploadPlan uploadPlan = scene.BuildUploadPlanAndClearDirty();

        Assert.That(uploadPlan.TransformRanges, Has.Count.EqualTo(1));
        Assert.That(uploadPlan.TransformRanges[0], Is.EqualTo(new GpuSceneUploadRange(0, 1)));
        Assert.That(uploadPlan.TransformBytes, Is.EqualTo(64));
        Assert.That(uploadPlan.ObjectBytes, Is.EqualTo(0));
    }

    [Test]
    public void MaterialOnlyUpdate_DoesNotUploadTransformData()
    {
        var scene = new GpuSceneManager();
        GpuObjectId id = scene.RegisterObject(CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        scene.UpdateObjectMaterial(id, new MaterialHandle(3, 1));
        GpuSceneUploadPlan uploadPlan = scene.BuildUploadPlanAndClearDirty();

        Assert.That(uploadPlan.ObjectRanges, Has.Count.EqualTo(1));
        Assert.That(uploadPlan.TransformRanges, Is.Empty);
        Assert.That(uploadPlan.ObjectBytes, Is.EqualTo(80));
        Assert.That(uploadPlan.TransformBytes, Is.EqualTo(0));
    }

    [Test]
    public void UnchangedMeshMaterialFlagsAndTransform_DoNotUpload()
    {
        var scene = new GpuSceneManager();
        GpuObjectId id = scene.RegisterObject(CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        scene.UpdateObjectMesh(id, new MeshHandle(1, 1));
        scene.UpdateObjectMaterial(id, new MaterialHandle(1, 1));
        scene.UpdateObjectFlags(
            id,
            GpuSceneObjectFlags.Visible | GpuSceneObjectFlags.CastsShadows | GpuSceneObjectFlags.ReceivesShadows);
        scene.UpdateObjectTransform(id, Matrix4x4.Identity);
        GpuSceneUploadPlan uploadPlan = scene.BuildUploadPlanAndClearDirty();

        Assert.Multiple(() =>
        {
            Assert.That(uploadPlan.TotalBytes, Is.EqualTo(0));
            Assert.That(uploadPlan.ObjectRanges, Is.Empty);
            Assert.That(uploadPlan.TransformRanges, Is.Empty);
            Assert.That(uploadPlan.PreviousTransformRanges, Is.Empty);
        });
    }

    [Test]
    public void PreviousTransform_AdvancesOnlyAfterSuccessfulFrame()
    {
        var scene = new GpuSceneManager();
        GpuObjectId id = scene.RegisterObject(CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        Matrix4x4 moved = Matrix4x4.CreateTranslation(new Vector3(4f, 0f, 0f));
        scene.UpdateObjectTransform(id, moved);

        GpuSceneObjectSnapshot beforeFrameComplete = scene.GetObjectSnapshot(id);
        Assert.That(beforeFrameComplete.Transform.WorldMatrix, Is.EqualTo(moved));
        Assert.That(beforeFrameComplete.PreviousTransform.WorldMatrix, Is.EqualTo(Matrix4x4.Identity));

        scene.CompleteSuccessfulFrame();
        GpuSceneObjectSnapshot afterFrameComplete = scene.GetObjectSnapshot(id);
        Assert.That(afterFrameComplete.PreviousTransform.WorldMatrix, Is.EqualTo(moved));
    }

    [Test]
    public void ResolvePreviousWorldMatrix_UsesGpuSceneHistoryForRenderObjects()
    {
        var scene = new GpuSceneManager();
        var renderObject = new RenderObject { Name = "TrackedObject" };
        scene.RegisterObject(renderObject, CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        Matrix4x4 moved = Matrix4x4.CreateTranslation(new Vector3(2f, 0f, 0f));
        Assert.That(scene.TryGetGpuObjectId(renderObject, out GpuObjectId id), Is.True);
        scene.UpdateObjectTransform(id, moved);

        Assert.That(scene.ResolvePreviousWorldMatrix(renderObject, moved), Is.EqualTo(Matrix4x4.Identity));

        scene.CompleteSuccessfulFrame();

        Assert.That(scene.ResolvePreviousWorldMatrix(renderObject, moved), Is.EqualTo(moved));
    }

    [Test]
    public void ResolvePreviousWorldMatrix_RejectsUnregisteredRenderObject()
    {
        var scene = new GpuSceneManager();
        var renderObject = new RenderObject { Name = "MissingObject" };

        Assert.Throws<InvalidOperationException>(() => scene.ResolvePreviousWorldMatrix(renderObject, Matrix4x4.Identity));
    }

    [Test]
    public void Teleport_ResetsPreviousTransformImmediately()
    {
        var scene = new GpuSceneManager();
        GpuObjectId id = scene.RegisterObject(CreateDesc());
        scene.BuildUploadPlanAndClearDirty();

        Matrix4x4 teleported = Matrix4x4.CreateTranslation(new Vector3(100f, 0f, 0f));
        scene.UpdateObjectTransform(id, teleported, resetHistory: true);

        GpuSceneObjectSnapshot snapshot = scene.GetObjectSnapshot(id);
        Assert.That(snapshot.Transform.WorldMatrix, Is.EqualTo(teleported));
        Assert.That(snapshot.PreviousTransform.WorldMatrix, Is.EqualTo(teleported));
        Assert.That(((GpuSceneObjectFlags)snapshot.Object.Flags & GpuSceneObjectFlags.TeleportHistoryReset) != 0, Is.True);

        GpuSceneUploadPlan uploadPlan = scene.BuildUploadPlanAndClearDirty();
        Assert.Multiple(() =>
        {
            Assert.That(uploadPlan.ObjectBytes, Is.EqualTo(80));
            Assert.That(uploadPlan.TransformBytes, Is.EqualTo(64));
            Assert.That(uploadPlan.PreviousTransformBytes, Is.EqualTo(64));
        });
    }

    [Test]
    public void DirtyRangeTracker_CoalescesAdjacentSlots()
    {
        var tracker = new GpuSceneDirtyRangeTracker();
        tracker.MarkDirty(4);
        tracker.MarkDirty(2);
        tracker.MarkDirty(3);
        tracker.MarkDirty(8);

        var ranges = tracker.BuildRangesAndClear();

        Assert.That(ranges, Is.EqualTo(new[]
        {
            new GpuSceneUploadRange(2, 3),
            new GpuSceneUploadRange(8, 1)
        }));
        Assert.That(tracker.DirtySlotCount, Is.EqualTo(0));
    }

    [Test]
    public void StaticBatchRegistration_CreatesDeterministicObjectAndInstanceMappings()
    {
        var scene = new GpuSceneManager();
        var batch = new StaticInstanceBatch(new[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(new Vector3(1f, 2f, 3f))
        });

        GpuSceneStaticBatchRegistration registration = scene.RegisterStaticBatch(batch, new GpuSceneStaticBatchDesc(
            new MeshHandle(1, 1),
            new MaterialHandle(1, 1),
            batch.WorldMatrices,
            UnitBox(),
            UnitSphere(),
            GpuSceneObjectFlags.Visible));

        Assert.That(registration.ObjectIds, Has.Count.EqualTo(2));
        Assert.That(registration.InstanceIds, Has.Count.EqualTo(2));
        Assert.That(registration.SourceRevision, Is.EqualTo(batch.Revision));
        Assert.That(scene.Stats.ObjectCount, Is.EqualTo(2));
        Assert.That(scene.Stats.InstanceCount, Is.EqualTo(2));
    }

    [Test]
    public void ResolvePreviousWorldMatrix_UsesGpuSceneHistoryForStaticBatches()
    {
        var scene = new GpuSceneManager();
        var batch = new StaticInstanceBatch(new[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(new Vector3(1f, 0f, 0f))
        });
        scene.RegisterStaticBatch(batch, new GpuSceneStaticBatchDesc(
            new MeshHandle(1, 1),
            new MaterialHandle(1, 1),
            batch.WorldMatrices,
            UnitBox(),
            UnitSphere(),
            GpuSceneObjectFlags.Visible));
        scene.BuildUploadPlanAndClearDirty();

        Matrix4x4 moved = Matrix4x4.CreateTranslation(new Vector3(9f, 0f, 0f));
        scene.UpdateStaticInstanceRange(batch, 1, new[] { moved });

        Assert.That(scene.ResolvePreviousWorldMatrix(batch, 1, moved), Is.EqualTo(Matrix4x4.CreateTranslation(new Vector3(1f, 0f, 0f))));

        scene.CompleteSuccessfulFrame();

        Assert.That(scene.ResolvePreviousWorldMatrix(batch, 1, moved), Is.EqualTo(moved));
    }

    [Test]
    public void FreezeForFrame_RejectsMutationUntilUnfrozen()
    {
        var scene = new GpuSceneManager();
        scene.FreezeForFrame();

        Assert.Throws<InvalidOperationException>(() => scene.RegisterObject(CreateDesc()));

        scene.UnfreezeAfterFrame();
        Assert.DoesNotThrow(() => scene.RegisterObject(CreateDesc()));
    }

    [Test]
    public void BufferSet_GrowsWithHeadroomAndAccountsDirtyUploads()
    {
        var scene = new GpuSceneManager();
        var buffers = new GpuSceneBufferSet(initialObjectCapacity: 1, initialInstanceCapacity: 1);
        GpuObjectId first = scene.RegisterObject(CreateDesc());
        scene.RegisterObject(CreateDesc());

        GpuSceneUploadPlan firstUpload = scene.BuildUploadPlanAndClearDirty();
        GpuSceneBufferUploadResult firstResult = buffers.ApplyUploadPlan(scene, firstUpload);

        Assert.That(firstResult.UploadedBytes, Is.EqualTo(firstUpload.TotalBytes));
        Assert.That(buffers.ObjectCapacity, Is.EqualTo(2));
        Assert.That(buffers.InstanceCapacity, Is.EqualTo(2));
        Assert.That(buffers.ObjectResizeCount, Is.EqualTo(1));
        Assert.That(buffers.InstanceResizeCount, Is.EqualTo(1));

        scene.UpdateObjectTransform(first, Matrix4x4.CreateTranslation(new Vector3(7f, 0f, 0f)));
        GpuSceneUploadPlan transformOnlyUpload = scene.BuildUploadPlanAndClearDirty();
        GpuSceneBufferUploadResult transformOnlyResult = buffers.ApplyUploadPlan(scene, transformOnlyUpload);

        Assert.That(transformOnlyResult.UploadedBytes, Is.EqualTo(64));
        Assert.That(buffers.ObjectResizeCount, Is.EqualTo(1));
        Assert.That(buffers.InstanceResizeCount, Is.EqualTo(1));
    }

    private static GpuSceneObjectDesc CreateDesc()
    {
        return new GpuSceneObjectDesc(
            new MeshHandle(1, 1),
            new MaterialHandle(1, 1),
            Matrix4x4.Identity,
            UnitBox(),
            UnitSphere(),
            GpuSceneObjectFlags.Visible | GpuSceneObjectFlags.CastsShadows | GpuSceneObjectFlags.ReceivesShadows);
    }

    private static BoundingBox UnitBox() => new(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));

    private static BoundingSphere UnitSphere() => new(Vector3.Zero, 1f);
}
