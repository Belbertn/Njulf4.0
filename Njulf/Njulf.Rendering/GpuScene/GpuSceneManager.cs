using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.GpuScene;

public sealed class GpuSceneManager
{
    private const int InitialObjectCapacity = 1024;
    private const int InitialInstanceCapacity = 2048;

    private readonly object _lock = new();
    private readonly List<ObjectSlot> _objects = new();
    private readonly List<InstanceSlot> _instances = new();
    private readonly Stack<int> _freeObjectSlots = new();
    private readonly Stack<int> _freeInstanceSlots = new();
    private readonly Dictionary<RenderObject, GpuObjectId> _renderObjectMap = new();
    private readonly Dictionary<StaticInstanceBatch, GpuSceneStaticBatchRegistration> _staticBatchMap = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyObjects = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyInstances = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyTransforms = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyPreviousTransforms = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyBounds = new();
    private readonly GpuSceneDirtyRangeTracker _dirtyVisibility = new();

    private int _objectCapacity = InitialObjectCapacity;
    private int _instanceCapacity = InitialInstanceCapacity;
    private int _objectHighWaterMark;
    private int _instanceHighWaterMark;
    private int _objectResizeCount;
    private int _instanceResizeCount;
    private ulong _lastUploadBytes;
    private ulong _lastTransformUploadBytes;
    private ulong _lastMaterialUploadBytes;
    private ulong _lastMeshUploadBytes;
    private ulong _lastBoundsUploadBytes;
    private ulong _lastVisibilityUploadBytes;
    private ulong _totalUploadBytes;
    private bool _updatesFrozen;

    public GpuSceneStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new GpuSceneStats(
                    CountActiveObjectsLocked(),
                    CountActiveInstancesLocked(),
                    _objectCapacity,
                    _instanceCapacity,
                    _objectHighWaterMark,
                    _instanceHighWaterMark,
                    _objectResizeCount,
                    _instanceResizeCount,
                    _lastUploadBytes,
                    _lastTransformUploadBytes,
                    _lastMaterialUploadBytes,
                    _lastMeshUploadBytes,
                    _lastBoundsUploadBytes,
                    _lastVisibilityUploadBytes,
                    _totalUploadBytes);
            }
        }
    }

    public GpuObjectId RegisterObject(RenderObject renderObject, GpuSceneObjectDesc desc)
    {
        if (renderObject == null)
            throw new ArgumentNullException(nameof(renderObject));

        lock (_lock)
        {
            EnsureMutableLocked();
            if (_renderObjectMap.ContainsKey(renderObject))
                throw new InvalidOperationException($"Render object '{renderObject.Name}' is already registered in the GPU scene.");

            GpuObjectId objectId = RegisterObjectLocked(desc);
            _renderObjectMap.Add(renderObject, objectId);
            return objectId;
        }
    }

    public GpuObjectId RegisterObject(GpuSceneObjectDesc desc)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            return RegisterObjectLocked(desc);
        }
    }

    public GpuSceneStaticBatchRegistration RegisterStaticBatch(StaticInstanceBatch batch, GpuSceneStaticBatchDesc desc)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));

        lock (_lock)
        {
            EnsureMutableLocked();
            if (_staticBatchMap.ContainsKey(batch))
                throw new InvalidOperationException($"Static batch '{batch.Name}' is already registered in the GPU scene.");

            ValidateStaticBatchDesc(desc);
            var objectIds = new GpuObjectId[desc.WorldMatrices.Count];
            var instanceIds = new GpuInstanceId[desc.WorldMatrices.Count];
            for (int i = 0; i < desc.WorldMatrices.Count; i++)
            {
                var objectDesc = new GpuSceneObjectDesc(
                    desc.Mesh,
                    desc.Material,
                    desc.WorldMatrices[i],
                    desc.LocalBounds,
                    desc.LocalBoundingSphere,
                    desc.Flags | GpuSceneObjectFlags.Static,
                    desc.VisibilityMask);
                objectIds[i] = RegisterObjectLocked(objectDesc);
                instanceIds[i] = _objects[objectIds[i].Index].PrimaryInstance;
            }

            var registration = new GpuSceneStaticBatchRegistration(objectIds, instanceIds, batch.Revision);
            _staticBatchMap.Add(batch, registration);
            return registration;
        }
    }

    public void UpdateStaticInstanceRange(StaticInstanceBatch batch, int start, IReadOnlyList<Matrix4x4> worldMatrices)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));
        if (worldMatrices == null)
            throw new ArgumentNullException(nameof(worldMatrices));
        if (start < 0)
            throw new ArgumentOutOfRangeException(nameof(start));

        lock (_lock)
        {
            EnsureMutableLocked();
            if (!_staticBatchMap.TryGetValue(batch, out GpuSceneStaticBatchRegistration? registration))
                throw new InvalidOperationException($"Static batch '{batch.Name}' is not registered in the GPU scene.");
            if (start + worldMatrices.Count > registration.ObjectIds.Count)
                throw new ArgumentOutOfRangeException(nameof(worldMatrices), "Static batch update range exceeds the registered instance range.");

            for (int i = 0; i < worldMatrices.Count; i++)
                UpdateObjectTransformLocked(registration.ObjectIds[start + i], worldMatrices[i], resetHistory: false);
        }
    }

    public void RemoveStaticBatch(StaticInstanceBatch batch)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));

        lock (_lock)
        {
            EnsureMutableLocked();
            if (!_staticBatchMap.Remove(batch, out GpuSceneStaticBatchRegistration? registration))
                return;

            foreach (GpuObjectId objectId in registration.ObjectIds)
                RemoveObjectLocked(objectId);
        }
    }

    public bool TryGetGpuObjectId(RenderObject renderObject, out GpuObjectId objectId)
    {
        if (renderObject == null)
            throw new ArgumentNullException(nameof(renderObject));

        lock (_lock)
            return _renderObjectMap.TryGetValue(renderObject, out objectId);
    }

    public GpuSceneObjectSnapshot GetObjectSnapshot(GpuObjectId objectId)
    {
        lock (_lock)
        {
            ObjectSlot slot = GetObjectSlotLocked(objectId);
            InstanceSlot instance = GetInstanceSlotLocked(slot.PrimaryInstance);
            return new GpuSceneObjectSnapshot(objectId, slot.PrimaryInstance, slot.Object, instance.Transform, instance.PreviousTransform, slot.Bounds, slot.Visibility);
        }
    }

    public Matrix4x4 ResolvePreviousWorldMatrix(RenderObject renderObject, Matrix4x4 currentWorldMatrix)
    {
        if (renderObject == null)
            throw new ArgumentNullException(nameof(renderObject));

        lock (_lock)
        {
            if (!_renderObjectMap.TryGetValue(renderObject, out GpuObjectId objectId))
                throw new InvalidOperationException($"Render object '{renderObject.Name}' is not registered in the GPU scene.");

            ObjectSlot slot = GetObjectSlotLocked(objectId);
            InstanceSlot instance = GetInstanceSlotLocked(slot.PrimaryInstance);
            return instance.PreviousTransform.WorldMatrix;
        }
    }

    public Matrix4x4 ResolvePreviousWorldMatrix(StaticInstanceBatch batch, int instanceIndex, Matrix4x4 currentWorldMatrix)
    {
        if (batch == null)
            throw new ArgumentNullException(nameof(batch));
        if (instanceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(instanceIndex));

        lock (_lock)
        {
            if (!_staticBatchMap.TryGetValue(batch, out GpuSceneStaticBatchRegistration? registration))
                throw new InvalidOperationException($"Static batch '{batch.Name}' is not registered in the GPU scene.");
            if (instanceIndex >= registration.ObjectIds.Count)
                throw new ArgumentOutOfRangeException(nameof(instanceIndex), "Static batch previous-transform lookup exceeds the registered instance range.");

            ObjectSlot slot = GetObjectSlotLocked(registration.ObjectIds[instanceIndex]);
            InstanceSlot instance = GetInstanceSlotLocked(slot.PrimaryInstance);
            return instance.PreviousTransform.WorldMatrix;
        }
    }

    public GPUSceneObject[] GetObjectDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUSceneObject[_objects.Count];
            for (int i = 0; i < _objects.Count; i++)
                snapshot[i] = _objects[i].Active ? _objects[i].Object : default;
            return snapshot;
        }
    }

    public GPUSceneInstance[] GetInstanceDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUSceneInstance[_instances.Count];
            for (int i = 0; i < _instances.Count; i++)
                snapshot[i] = _instances[i].Active ? _instances[i].Instance : default;
            return snapshot;
        }
    }

    public GPUTransform[] GetTransformDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUTransform[_instances.Count];
            for (int i = 0; i < _instances.Count; i++)
                snapshot[i] = _instances[i].Active ? _instances[i].Transform : default;
            return snapshot;
        }
    }

    public GPUPreviousTransform[] GetPreviousTransformDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUPreviousTransform[_instances.Count];
            for (int i = 0; i < _instances.Count; i++)
                snapshot[i] = _instances[i].Active ? _instances[i].PreviousTransform : default;
            return snapshot;
        }
    }

    public GPUObjectBounds[] GetBoundsDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUObjectBounds[_objects.Count];
            for (int i = 0; i < _objects.Count; i++)
                snapshot[i] = _objects[i].Active ? _objects[i].Bounds : default;
            return snapshot;
        }
    }

    public GPUVisibilityState[] GetVisibilityDataSnapshot()
    {
        lock (_lock)
        {
            var snapshot = new GPUVisibilityState[_objects.Count];
            for (int i = 0; i < _objects.Count; i++)
                snapshot[i] = _objects[i].Active ? _objects[i].Visibility : default;
            return snapshot;
        }
    }

    public void UpdateObjectTransform(GpuObjectId objectId, Matrix4x4 worldMatrix, bool resetHistory = false)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            UpdateObjectTransformLocked(objectId, worldMatrix, resetHistory);
        }
    }

    public void UpdateObjectMaterial(GpuObjectId objectId, MaterialHandle material)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            ValidateMaterial(material);
            ObjectSlot slot = GetObjectSlotLocked(objectId);
            if (slot.Object.MaterialIndex == (uint)material.Index)
                return;

            slot.Object.MaterialIndex = (uint)material.Index;
            slot.DirtyFlags |= GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Material;
            _objects[objectId.Index] = slot;
            MarkObjectDirty(objectId.Index, GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Material);
        }
    }

    public void UpdateObjectMesh(GpuObjectId objectId, MeshHandle mesh)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            ValidateMesh(mesh);
            ObjectSlot slot = GetObjectSlotLocked(objectId);
            if (slot.Object.MeshIndex == (uint)mesh.Index)
                return;

            slot.Object.MeshIndex = (uint)mesh.Index;
            slot.DirtyFlags |= GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Mesh;
            _objects[objectId.Index] = slot;
            MarkObjectDirty(objectId.Index, GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Mesh);
        }
    }

    public void UpdateObjectFlags(GpuObjectId objectId, GpuSceneObjectFlags flags, uint visibilityMask = uint.MaxValue)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            ObjectSlot slot = GetObjectSlotLocked(objectId);
            if (slot.Object.Flags == (uint)flags && slot.Object.VisibilityMask == visibilityMask)
                return;

            slot.Object.Flags = (uint)flags;
            slot.Object.VisibilityMask = visibilityMask;
            slot.Visibility.VisibilityMask = visibilityMask;
            slot.Visibility.Flags = (uint)flags;
            slot.DirtyFlags |= GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Visibility;
            _objects[objectId.Index] = slot;
            MarkObjectDirty(objectId.Index, GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Visibility);
        }
    }

    public void RemoveObject(RenderObject renderObject)
    {
        if (renderObject == null)
            throw new ArgumentNullException(nameof(renderObject));

        lock (_lock)
        {
            EnsureMutableLocked();
            if (!_renderObjectMap.Remove(renderObject, out GpuObjectId objectId))
                return;

            RemoveObjectLocked(objectId);
        }
    }

    public void RemoveObject(GpuObjectId objectId)
    {
        lock (_lock)
        {
            EnsureMutableLocked();
            RemoveObjectLocked(objectId);
        }
    }

    public void FreezeForFrame()
    {
        lock (_lock)
            _updatesFrozen = true;
    }

    public void UnfreezeAfterFrame()
    {
        lock (_lock)
            _updatesFrozen = false;
    }

    public void CompleteSuccessfulFrame()
    {
        lock (_lock)
        {
            for (int i = 0; i < _instances.Count; i++)
            {
                InstanceSlot slot = _instances[i];
                if (!slot.Active)
                    continue;

                slot.PreviousTransform = new GPUPreviousTransform { WorldMatrix = slot.Transform.WorldMatrix };
                _instances[i] = slot;
            }
        }
    }

    public GpuSceneUploadPlan BuildUploadPlanAndClearDirty()
    {
        lock (_lock)
        {
            IReadOnlyList<GpuSceneUploadRange> objectRanges = _dirtyObjects.BuildRangesAndClear();
            IReadOnlyList<GpuSceneUploadRange> instanceRanges = _dirtyInstances.BuildRangesAndClear();
            IReadOnlyList<GpuSceneUploadRange> transformRanges = _dirtyTransforms.BuildRangesAndClear();
            IReadOnlyList<GpuSceneUploadRange> previousTransformRanges = _dirtyPreviousTransforms.BuildRangesAndClear();
            IReadOnlyList<GpuSceneUploadRange> boundsRanges = _dirtyBounds.BuildRangesAndClear();
            IReadOnlyList<GpuSceneUploadRange> visibilityRanges = _dirtyVisibility.BuildRangesAndClear();

            ulong objectBytes = CountBytes<GPUSceneObject>(objectRanges);
            ulong instanceBytes = CountBytes<GPUSceneInstance>(instanceRanges);
            ulong transformBytes = CountBytes<GPUTransform>(transformRanges);
            ulong previousTransformBytes = CountBytes<GPUPreviousTransform>(previousTransformRanges);
            ulong boundsBytes = CountBytes<GPUObjectBounds>(boundsRanges);
            ulong visibilityBytes = CountBytes<GPUVisibilityState>(visibilityRanges);

            _lastTransformUploadBytes = transformBytes + previousTransformBytes;
            _lastBoundsUploadBytes = boundsBytes;
            _lastVisibilityUploadBytes = visibilityBytes;
            _lastUploadBytes = objectBytes + instanceBytes + transformBytes + previousTransformBytes + boundsBytes + visibilityBytes;
            _totalUploadBytes = checked(_totalUploadBytes + _lastUploadBytes);

            ClearSlotDirtyFlags();
            return new GpuSceneUploadPlan(
                objectRanges,
                instanceRanges,
                transformRanges,
                previousTransformRanges,
                boundsRanges,
                visibilityRanges,
                objectBytes,
                instanceBytes,
                transformBytes,
                previousTransformBytes,
                boundsBytes,
                visibilityBytes);
        }
    }

    private GpuObjectId RegisterObjectLocked(GpuSceneObjectDesc desc)
    {
        ValidateObjectDesc(desc);
        int objectIndex = AllocateObjectSlot();
        int instanceIndex = AllocateInstanceSlot();
        uint objectGeneration = AllocateObjectGeneration(objectIndex);
        uint instanceGeneration = AllocateInstanceGeneration(instanceIndex);
        var objectId = new GpuObjectId(objectIndex, objectGeneration);
        var instanceId = new GpuInstanceId(instanceIndex, instanceGeneration);

        GPUSceneObject gpuObject = CreateGpuObject(desc, instanceIndex);
        GPUTransform transform = new() { WorldMatrix = desc.WorldMatrix };
        GPUPreviousTransform previousTransform = new() { WorldMatrix = desc.WorldMatrix };
        GPUObjectBounds bounds = CreateBounds(desc);
        GPUVisibilityState visibility = new()
        {
            VisibilityMask = desc.VisibilityMask,
            Flags = (uint)desc.Flags,
            LastVisibleFrame = 0,
            LastTestedFrame = 0
        };

        var objectSlot = new ObjectSlot
        {
            Active = true,
            Generation = objectGeneration,
            Object = gpuObject,
            Bounds = bounds,
            Visibility = visibility,
            PrimaryInstance = instanceId,
            DirtyFlags = GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Bounds | GpuSceneDirtyFlags.Visibility
        };
        var instanceSlot = new InstanceSlot
        {
            Active = true,
            Generation = instanceGeneration,
            Instance = new GPUSceneInstance
            {
                ObjectIndex = (uint)objectIndex,
                TransformIndex = (uint)instanceIndex,
                PreviousTransformIndex = (uint)instanceIndex,
                VisibilityIndex = (uint)objectIndex
            },
            Transform = transform,
            PreviousTransform = previousTransform,
            DirtyFlags = GpuSceneDirtyFlags.Instance | GpuSceneDirtyFlags.Transform | GpuSceneDirtyFlags.PreviousTransform
        };

        SetObjectSlot(objectIndex, objectSlot);
        SetInstanceSlot(instanceIndex, instanceSlot);
        MarkObjectDirty(objectIndex, objectSlot.DirtyFlags);
        MarkInstanceDirty(instanceIndex, instanceSlot.DirtyFlags);
        _objectHighWaterMark = Math.Max(_objectHighWaterMark, objectIndex + 1);
        _instanceHighWaterMark = Math.Max(_instanceHighWaterMark, instanceIndex + 1);
        return objectId;
    }

    private void UpdateObjectTransformLocked(GpuObjectId objectId, Matrix4x4 worldMatrix, bool resetHistory)
    {
        ValidateFinite(worldMatrix, nameof(worldMatrix));
        ObjectSlot objectSlot = GetObjectSlotLocked(objectId);
        InstanceSlot instanceSlot = GetInstanceSlotLocked(objectSlot.PrimaryInstance);
        if (!resetHistory && instanceSlot.Transform.WorldMatrix.Equals(worldMatrix))
            return;

        instanceSlot.Transform = new GPUTransform { WorldMatrix = worldMatrix };
        instanceSlot.DirtyFlags |= GpuSceneDirtyFlags.Transform;
        if (resetHistory)
        {
            instanceSlot.PreviousTransform = new GPUPreviousTransform { WorldMatrix = worldMatrix };
            instanceSlot.DirtyFlags |= GpuSceneDirtyFlags.PreviousTransform;
            objectSlot.Object.Flags |= (uint)GpuSceneObjectFlags.TeleportHistoryReset;
            objectSlot.DirtyFlags |= GpuSceneDirtyFlags.Object;
            _objects[objectId.Index] = objectSlot;
            MarkObjectDirty(objectId.Index, GpuSceneDirtyFlags.Object);
            _dirtyPreviousTransforms.MarkDirty(objectSlot.PrimaryInstance.Index);
        }

        _instances[objectSlot.PrimaryInstance.Index] = instanceSlot;
        _dirtyTransforms.MarkDirty(objectSlot.PrimaryInstance.Index);
    }

    private void RemoveObjectLocked(GpuObjectId objectId)
    {
        ObjectSlot slot = GetObjectSlotLocked(objectId);
        ReleaseInstanceSlot(slot.PrimaryInstance);
        slot.Active = false;
        slot.Generation = NextGeneration(slot.Generation);
        slot.DirtyFlags = GpuSceneDirtyFlags.None;
        _objects[objectId.Index] = slot;
        _freeObjectSlots.Push(objectId.Index);
    }

    private ObjectSlot GetObjectSlotLocked(GpuObjectId objectId)
    {
        if (!objectId.IsValid || objectId.Index >= _objects.Count)
            throw new InvalidOperationException($"Invalid GPU object ID {objectId}.");

        ObjectSlot slot = _objects[objectId.Index];
        if (!slot.Active)
            throw new InvalidOperationException($"GPU object ID {objectId} references a removed slot.");
        if (slot.Generation != objectId.Generation)
            throw new InvalidOperationException($"GPU object ID {objectId} generation mismatch. Current generation is {slot.Generation}.");

        return slot;
    }

    private InstanceSlot GetInstanceSlotLocked(GpuInstanceId instanceId)
    {
        if (!instanceId.IsValid || instanceId.Index >= _instances.Count)
            throw new InvalidOperationException($"Invalid GPU instance ID {instanceId}.");

        InstanceSlot slot = _instances[instanceId.Index];
        if (!slot.Active)
            throw new InvalidOperationException($"GPU instance ID {instanceId} references a removed slot.");
        if (slot.Generation != instanceId.Generation)
            throw new InvalidOperationException($"GPU instance ID {instanceId} generation mismatch. Current generation is {slot.Generation}.");

        return slot;
    }

    private int AllocateObjectSlot() => _freeObjectSlots.Count > 0 ? _freeObjectSlots.Pop() : _objects.Count;

    private int AllocateInstanceSlot() => _freeInstanceSlots.Count > 0 ? _freeInstanceSlots.Pop() : _instances.Count;

    private uint AllocateObjectGeneration(int index) => index < _objects.Count ? NextGeneration(_objects[index].Generation) : 1;

    private uint AllocateInstanceGeneration(int index) => index < _instances.Count ? NextGeneration(_instances[index].Generation) : 1;

    private void SetObjectSlot(int index, ObjectSlot slot)
    {
        if (index == _objects.Count)
            _objects.Add(slot);
        else
            _objects[index] = slot;

        EnsureObjectCapacity(index + 1);
    }

    private void SetInstanceSlot(int index, InstanceSlot slot)
    {
        if (index == _instances.Count)
            _instances.Add(slot);
        else
            _instances[index] = slot;

        EnsureInstanceCapacity(index + 1);
    }

    private void ReleaseInstanceSlot(GpuInstanceId instanceId)
    {
        InstanceSlot instance = GetInstanceSlotLocked(instanceId);
        instance.Active = false;
        instance.Generation = NextGeneration(instance.Generation);
        instance.DirtyFlags = GpuSceneDirtyFlags.None;
        _instances[instanceId.Index] = instance;
        _freeInstanceSlots.Push(instanceId.Index);
    }

    private void EnsureObjectCapacity(int requiredCount)
    {
        if (requiredCount <= _objectCapacity)
            return;

        while (_objectCapacity < requiredCount)
            _objectCapacity = checked(_objectCapacity * 2);
        _objectResizeCount++;
        _dirtyObjects.MarkRangeDirty(0, _objects.Count);
        _dirtyBounds.MarkRangeDirty(0, _objects.Count);
        _dirtyVisibility.MarkRangeDirty(0, _objects.Count);
    }

    private void EnsureInstanceCapacity(int requiredCount)
    {
        if (requiredCount <= _instanceCapacity)
            return;

        while (_instanceCapacity < requiredCount)
            _instanceCapacity = checked(_instanceCapacity * 2);
        _instanceResizeCount++;
        _dirtyInstances.MarkRangeDirty(0, _instances.Count);
        _dirtyTransforms.MarkRangeDirty(0, _instances.Count);
        _dirtyPreviousTransforms.MarkRangeDirty(0, _instances.Count);
    }

    private void MarkObjectDirty(int objectIndex, GpuSceneDirtyFlags flags)
    {
        if ((flags & (GpuSceneDirtyFlags.Object | GpuSceneDirtyFlags.Material | GpuSceneDirtyFlags.Mesh)) != 0)
            _dirtyObjects.MarkDirty(objectIndex);
        if ((flags & GpuSceneDirtyFlags.Bounds) != 0)
            _dirtyBounds.MarkDirty(objectIndex);
        if ((flags & GpuSceneDirtyFlags.Visibility) != 0)
            _dirtyVisibility.MarkDirty(objectIndex);
        if ((flags & GpuSceneDirtyFlags.Material) != 0)
            _lastMaterialUploadBytes = (ulong)Marshal.SizeOf<GPUSceneObject>();
        if ((flags & GpuSceneDirtyFlags.Mesh) != 0)
            _lastMeshUploadBytes = (ulong)Marshal.SizeOf<GPUSceneObject>();
    }

    private void MarkInstanceDirty(int instanceIndex, GpuSceneDirtyFlags flags)
    {
        if ((flags & GpuSceneDirtyFlags.Instance) != 0)
            _dirtyInstances.MarkDirty(instanceIndex);
        if ((flags & GpuSceneDirtyFlags.Transform) != 0)
            _dirtyTransforms.MarkDirty(instanceIndex);
        if ((flags & GpuSceneDirtyFlags.PreviousTransform) != 0)
            _dirtyPreviousTransforms.MarkDirty(instanceIndex);
    }

    private void ClearSlotDirtyFlags()
    {
        for (int i = 0; i < _objects.Count; i++)
        {
            ObjectSlot slot = _objects[i];
            slot.DirtyFlags = GpuSceneDirtyFlags.None;
            _objects[i] = slot;
        }

        for (int i = 0; i < _instances.Count; i++)
        {
            InstanceSlot slot = _instances[i];
            slot.DirtyFlags = GpuSceneDirtyFlags.None;
            _instances[i] = slot;
        }
    }

    private int CountActiveObjectsLocked()
    {
        int count = 0;
        foreach (ObjectSlot slot in _objects)
        {
            if (slot.Active)
                count++;
        }

        return count;
    }

    private int CountActiveInstancesLocked()
    {
        int count = 0;
        foreach (InstanceSlot slot in _instances)
        {
            if (slot.Active)
                count++;
        }

        return count;
    }

    private void EnsureMutableLocked()
    {
        if (_updatesFrozen)
            throw new InvalidOperationException("GPU scene mutation is frozen for the current frame. Queue changes before FreezeForFrame or after UnfreezeAfterFrame.");
    }

    private static GPUSceneObject CreateGpuObject(GpuSceneObjectDesc desc, int primaryInstanceIndex)
    {
        return new GPUSceneObject
        {
            MeshIndex = (uint)desc.Mesh.Index,
            MaterialIndex = (uint)desc.Material.Index,
            FirstInstance = (uint)primaryInstanceIndex,
            InstanceCount = 1,
            BoundsIndex = (uint)primaryInstanceIndex,
            VisibilityIndex = (uint)primaryInstanceIndex,
            Flags = (uint)desc.Flags,
            VisibilityMask = desc.VisibilityMask,
            SkinningDataOffset = desc.SkinningDataOffset,
            LightReferenceOffset = uint.MaxValue,
            LightReferenceCount = 0,
            DecalReferenceOffset = uint.MaxValue,
            DecalReferenceCount = 0
        };
    }

    private static GPUObjectBounds CreateBounds(GpuSceneObjectDesc desc)
    {
        return new GPUObjectBounds
        {
            BoundingSphere = new Vector4(
                desc.LocalBoundingSphere.Center.X,
                desc.LocalBoundingSphere.Center.Y,
                desc.LocalBoundingSphere.Center.Z,
                desc.LocalBoundingSphere.Radius),
            BoundingBoxMin = new Vector4(desc.LocalBounds.Min.X, desc.LocalBounds.Min.Y, desc.LocalBounds.Min.Z, 0f),
            BoundingBoxMax = new Vector4(desc.LocalBounds.Max.X, desc.LocalBounds.Max.Y, desc.LocalBounds.Max.Z, 0f)
        };
    }

    private static void ValidateObjectDesc(GpuSceneObjectDesc desc)
    {
        ValidateMesh(desc.Mesh);
        ValidateMaterial(desc.Material);
        ValidateFinite(desc.WorldMatrix, nameof(desc.WorldMatrix));
        ValidateFinite(desc.LocalBounds, nameof(desc.LocalBounds));
        ValidateFinite(desc.LocalBoundingSphere, nameof(desc.LocalBoundingSphere));
        if (desc.LocalBoundingSphere.Radius < 0f)
            throw new ArgumentException("Bounding sphere radius cannot be negative.", nameof(desc));
    }

    private static void ValidateStaticBatchDesc(GpuSceneStaticBatchDesc desc)
    {
        ValidateMesh(desc.Mesh);
        ValidateMaterial(desc.Material);
        if (desc.WorldMatrices == null)
            throw new ArgumentNullException(nameof(desc.WorldMatrices));
        foreach (Matrix4x4 matrix in desc.WorldMatrices)
            ValidateFinite(matrix, nameof(desc.WorldMatrices));
        ValidateFinite(desc.LocalBounds, nameof(desc.LocalBounds));
        ValidateFinite(desc.LocalBoundingSphere, nameof(desc.LocalBoundingSphere));
    }

    private static void ValidateMesh(MeshHandle mesh)
    {
        if (!mesh.IsValid)
            throw new ArgumentException("A valid mesh handle is required.", nameof(mesh));
    }

    private static void ValidateMaterial(MaterialHandle material)
    {
        if (!material.IsValid)
            throw new ArgumentException("A valid material handle is required.", nameof(material));
    }

    private static void ValidateFinite(Matrix4x4 matrix, string name)
    {
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                if (!float.IsFinite(matrix[row, column]))
                    throw new ArgumentException($"{name} contains a non-finite value at [{row}, {column}].", name);
            }
        }
    }

    private static void ValidateFinite(BoundingBox bounds, string name)
    {
        ValidateFinite(bounds.Min, $"{name}.Min");
        ValidateFinite(bounds.Max, $"{name}.Max");
    }

    private static void ValidateFinite(BoundingSphere bounds, string name)
    {
        ValidateFinite(bounds.Center, $"{name}.Center");
        if (!float.IsFinite(bounds.Radius))
            throw new ArgumentException($"{name}.Radius contains a non-finite value.", name);
    }

    private static void ValidateFinite(Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new ArgumentException($"{name} contains a non-finite value.", name);
    }

    private static uint NextGeneration(uint generation)
    {
        generation++;
        return generation == 0 ? 1 : generation;
    }

    private static ulong CountBytes<T>(IReadOnlyList<GpuSceneUploadRange> ranges)
        where T : struct
    {
        ulong stride = (ulong)Unsafe.SizeOf<T>();
        ulong total = 0;
        foreach (GpuSceneUploadRange range in ranges)
            total = checked(total + (ulong)range.Count * stride);
        return total;
    }

    private struct ObjectSlot
    {
        public bool Active;
        public uint Generation;
        public GPUSceneObject Object;
        public GPUObjectBounds Bounds;
        public GPUVisibilityState Visibility;
        public GpuInstanceId PrimaryInstance;
        public GpuSceneDirtyFlags DirtyFlags;
    }

    private struct InstanceSlot
    {
        public bool Active;
        public uint Generation;
        public GPUSceneInstance Instance;
        public GPUTransform Transform;
        public GPUPreviousTransform PreviousTransform;
        public GpuSceneDirtyFlags DirtyFlags;
    }
}

public sealed record GpuSceneStaticBatchRegistration(
    IReadOnlyList<GpuObjectId> ObjectIds,
    IReadOnlyList<GpuInstanceId> InstanceIds,
    uint SourceRevision);

public sealed record GpuSceneObjectSnapshot(
    GpuObjectId ObjectId,
    GpuInstanceId InstanceId,
    GPUSceneObject Object,
    GPUTransform Transform,
    GPUPreviousTransform PreviousTransform,
    GPUObjectBounds Bounds,
    GPUVisibilityState Visibility);
