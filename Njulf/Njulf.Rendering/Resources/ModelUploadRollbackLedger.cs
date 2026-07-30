using Njulf.Core.Scene;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Owns every resource category acquired after imported materials have been
/// registered but before a model upload commits. Model-attached resources stay
/// in the model's durable disposal lists; unattached mesh and material
/// occurrences are copied into preallocated, exact-once release ledgers.
/// </summary>
internal sealed class ModelUploadRollbackLedger
{
    private readonly Model _model;
    private readonly Action<MeshHandle> _releaseMesh;
    private readonly Action<MaterialHandle> _releaseMaterial;
    private readonly Action<TextureHandle> _releaseTexture;
    private readonly List<MeshHandle> _trackedMeshes;
    private readonly List<MaterialHandle> _trackedMaterials;
    private readonly List<TextureHandle> _pendingPrimitiveTextures;
    private readonly List<MeshHandle> _pendingMeshes;
    private readonly List<MaterialHandle> _pendingMaterials;
    private readonly object _lock = new();
    private int _baseMaterialCount;
    private int _attachedRenderObjectCount;
    private int _expectedPrimitiveTextureCount;
    private bool _primitiveMaterialAcquisitionActive;
    private bool _baseMaterialsTransferredToModel;
    private bool _directOwnershipInitialized;
    private bool _modelDisposalCompleted;
    private bool _committed;
    private bool _rollbackStarted;
    private bool _rollbackInProgress;
    private bool _rollbackCompleted;
    private int _rollbackThreadId;

    public ModelUploadRollbackLedger(
        Model model,
        int materialCapacity,
        int meshCapacity,
        Action<MeshHandle> releaseMesh,
        Action<MaterialHandle> releaseMaterial,
        Action<TextureHandle> releaseTexture)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (materialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(materialCapacity));
        if (meshCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(meshCapacity));

        _model = model;
        _releaseMesh =
            releaseMesh ??
            throw new ArgumentNullException(nameof(releaseMesh));
        _releaseMaterial =
            releaseMaterial ??
            throw new ArgumentNullException(nameof(releaseMaterial));
        _releaseTexture =
            releaseTexture ??
            throw new ArgumentNullException(nameof(releaseTexture));
        _trackedMeshes =
            new List<MeshHandle>(meshCapacity);
        _trackedMaterials =
            new List<MaterialHandle>(materialCapacity);
        _pendingPrimitiveTextures =
            new List<TextureHandle>();
        _pendingMeshes =
            new List<MeshHandle>(meshCapacity);
        _pendingMaterials =
            new List<MaterialHandle>(materialCapacity);
    }

    internal int TrackedMaterialCount
    {
        get
        {
            lock (_lock)
                return _trackedMaterials.Count;
        }
    }

    internal int PendingResourceCount
    {
        get
        {
            lock (_lock)
            {
                if (_rollbackCompleted || _committed)
                    return 0;
                if (_directOwnershipInitialized)
                {
                    return checked(
                        (_modelDisposalCompleted ? 0 : 1) +
                        _pendingPrimitiveTextures.Count +
                        _pendingMeshes.Count +
                        _pendingMaterials.Count);
                }

                int primitiveCount =
                    _trackedMaterials.Count -
                    _baseMaterialCount;
                int directBaseCount =
                    _baseMaterialsTransferredToModel
                        ? 0
                        : _baseMaterialCount;
                return checked(
                    1 +
                    _pendingPrimitiveTextures.Count +
                    Math.Max(
                        _trackedMeshes.Count -
                        _attachedRenderObjectCount,
                        0) +
                    Math.Max(
                        primitiveCount -
                        _attachedRenderObjectCount,
                        0) +
                    directBaseCount);
            }
        }
    }

    internal bool RollbackCompleted
    {
        get
        {
            lock (_lock)
                return _rollbackCompleted;
        }
    }

    internal void TrackBaseMaterials(
        IReadOnlyList<MaterialHandle> materials)
    {
        ArgumentNullException.ThrowIfNull(materials);
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (_baseMaterialCount != 0 ||
                _trackedMaterials.Count != 0)
            {
                throw new InvalidOperationException(
                    "Base model materials can be tracked only once and before primitive references.");
            }
            if (materials.Count >
                _trackedMaterials.Capacity)
            {
                throw new InvalidOperationException(
                    "The model rollback ledger did not reserve enough material ownership capacity.");
            }

            foreach (MaterialHandle material in materials)
            {
                if (!material.IsValid)
                {
                    throw new ArgumentException(
                        "Tracked base material ownership requires valid handles.",
                        nameof(materials));
                }

                _trackedMaterials.Add(material);
            }

            _baseMaterialCount = materials.Count;
        }
    }

    /// <summary>
    /// Preallocates every collection slot needed to acquire one primitive
    /// material. This must run before retaining any backend resource so the
    /// subsequent ownership records cannot allocate.
    /// </summary>
    internal void BeginPrimitiveMaterialAcquisition(
        int expectedTextureCount)
    {
        if (expectedTextureCount < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedTextureCount));

        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (_primitiveMaterialAcquisitionActive)
            {
                throw new InvalidOperationException(
                    "A primitive material acquisition is already active.");
            }
            if (_pendingPrimitiveTextures.Count != 0)
            {
                throw new InvalidOperationException(
                    "A previous primitive material acquisition still owns uncommitted texture references.");
            }
            if (_trackedMaterials.Count ==
                _trackedMaterials.Capacity)
            {
                throw new InvalidOperationException(
                    "The model rollback ledger exhausted its reserved material ownership capacity.");
            }

            _pendingPrimitiveTextures.EnsureCapacity(
                expectedTextureCount);
            _expectedPrimitiveTextureCount =
                expectedTextureCount;
            _primitiveMaterialAcquisitionActive =
                true;
        }
    }

    /// <summary>
    /// Records a successfully retained texture occurrence. Capacity was
    /// reserved by <see cref="BeginPrimitiveMaterialAcquisition"/>, so this
    /// operation cannot allocate after ownership has been acquired.
    /// </summary>
    internal void TrackRetainedPrimitiveTexture(
        TextureHandle texture)
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (!_primitiveMaterialAcquisitionActive)
            {
                throw new InvalidOperationException(
                    "No primitive material acquisition is active.");
            }
            if (_pendingPrimitiveTextures.Count >=
                _expectedPrimitiveTextureCount)
            {
                throw new InvalidOperationException(
                    "The primitive material acquisition exceeded its preallocated texture ownership count.");
            }

            _pendingPrimitiveTextures.Add(texture);
        }
    }

    /// <summary>
    /// Atomically replaces the direct texture occurrences with the material
    /// occurrence that accepted their ownership.
    /// </summary>
    internal void CommitPrimitiveMaterialAcquisition(
        MaterialHandle material)
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (!_primitiveMaterialAcquisitionActive)
            {
                throw new InvalidOperationException(
                    "No primitive material acquisition is active.");
            }
            if (_pendingPrimitiveTextures.Count !=
                _expectedPrimitiveTextureCount)
            {
                throw new InvalidOperationException(
                    "The primitive material acquisition did not record every expected texture occurrence.");
            }

            // The material slot and texture capacity were both reserved before
            // backend acquisition began. Neither mutation allocates.
            _trackedMaterials.Add(material);
            _pendingPrimitiveTextures.Clear();
            _expectedPrimitiveTextureCount = 0;
            _primitiveMaterialAcquisitionActive = false;
        }
    }

    /// <summary>
    /// Ends a failed acquisition while preserving every retained texture
    /// occurrence for durable rollback.
    /// </summary>
    internal void AbortPrimitiveMaterialAcquisition()
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (!_primitiveMaterialAcquisitionActive)
                return;

            _expectedPrimitiveTextureCount = 0;
            _primitiveMaterialAcquisitionActive = false;
        }
    }

    internal void TrackMeshes(
        IReadOnlyList<MeshHandle> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (_trackedMeshes.Count != 0)
            {
                throw new InvalidOperationException(
                    "Model mesh ownership can be tracked only once.");
            }
            if (meshes.Count >
                _trackedMeshes.Capacity)
            {
                throw new InvalidOperationException(
                    "The model rollback ledger did not reserve enough mesh ownership capacity.");
            }

            foreach (MeshHandle mesh in meshes)
            {
                if (!mesh.IsValid)
                {
                    throw new ArgumentException(
                        "Tracked model mesh ownership requires valid handles.",
                        nameof(meshes));
                }

                _trackedMeshes.Add(mesh);
            }
        }
    }

    internal void MarkRenderObjectAttached()
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            int primitiveCount =
                _trackedMaterials.Count -
                _baseMaterialCount;
            if (_attachedRenderObjectCount >=
                    _trackedMeshes.Count ||
                _attachedRenderObjectCount >=
                    primitiveCount)
            {
                throw new InvalidOperationException(
                    "A render object cannot acquire ownership beyond the tracked mesh/material pairs.");
            }

            _attachedRenderObjectCount++;
        }
    }

    internal void TransferBaseMaterialsToModel()
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (_baseMaterialsTransferredToModel)
            {
                throw new InvalidOperationException(
                    "Base material ownership was already transferred to the model.");
            }

            _baseMaterialsTransferredToModel = true;
        }
    }

    internal void Commit()
    {
        lock (_lock)
        {
            ThrowIfOwnershipClosedLocked();
            if (!_baseMaterialsTransferredToModel)
            {
                throw new InvalidOperationException(
                    "A model upload cannot commit before base material ownership is transferred.");
            }
            if (_primitiveMaterialAcquisitionActive ||
                _pendingPrimitiveTextures.Count != 0)
            {
                throw new InvalidOperationException(
                    "A model upload cannot commit while primitive texture ownership remains uncommitted.");
            }
            if (_attachedRenderObjectCount !=
                    _trackedMeshes.Count ||
                _attachedRenderObjectCount !=
                    _trackedMaterials.Count -
                    _baseMaterialCount)
            {
                throw new InvalidOperationException(
                    "A model upload cannot commit while tracked mesh or primitive material ownership remains unattached.");
            }

            _committed = true;
            _trackedMeshes.Clear();
            _trackedMaterials.Clear();
        }
    }

    internal Exception? TryRollback()
    {
        int currentThreadId =
            Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            while (_rollbackInProgress)
            {
                if (_rollbackThreadId ==
                    currentThreadId)
                {
                    return new InvalidOperationException(
                        "A model-upload rollback cannot re-enter its active drain.");
                }

                Monitor.Wait(_lock);
            }

            if (_committed)
            {
                return new InvalidOperationException(
                    "Committed model ownership cannot be rolled back.");
            }
            if (_rollbackCompleted)
                return null;

            _rollbackStarted = true;
            try
            {
                InitializeDirectOwnershipLocked();
            }
            catch (Exception initializationFailure)
            {
                return initializationFailure;
            }

            _rollbackInProgress = true;
            _rollbackThreadId =
                currentThreadId;
        }

        try
        {
            List<Exception>? failures = null;
            TryDrainPrimitiveTextures(ref failures);
            TryDisposeModel(ref failures);
            TryDrainMeshes(ref failures);
            TryDrainMaterials(ref failures);

            lock (_lock)
            {
                _rollbackCompleted =
                    _pendingPrimitiveTextures.Count == 0 &&
                    _modelDisposalCompleted &&
                    _pendingMeshes.Count == 0 &&
                    _pendingMaterials.Count == 0;
            }

            return failures == null
                ? null
                : new AggregateException(
                    "One or more model-upload resources could not be rolled back.",
                    failures);
        }
        finally
        {
            lock (_lock)
            {
                _rollbackInProgress = false;
                _rollbackThreadId = 0;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void InitializeDirectOwnershipLocked()
    {
        if (_directOwnershipInitialized)
            return;

        _primitiveMaterialAcquisitionActive = false;
        _expectedPrimitiveTextureCount = 0;
        int primitiveCount =
            _trackedMaterials.Count -
            _baseMaterialCount;
        if (_baseMaterialCount < 0 ||
            primitiveCount < 0 ||
            _attachedRenderObjectCount < 0 ||
            _attachedRenderObjectCount >
                _trackedMeshes.Count ||
            _attachedRenderObjectCount >
                primitiveCount)
        {
            throw new InvalidOperationException(
                "Tracked model ownership counts are inconsistent.");
        }

        for (int index =
                 _trackedMeshes.Count - 1;
             index >=
                 _attachedRenderObjectCount;
             index--)
        {
            _pendingMeshes.Add(
                _trackedMeshes[index]);
        }

        int firstUnattachedPrimitive =
            checked(
                _baseMaterialCount +
                _attachedRenderObjectCount);
        for (int index =
                 _trackedMaterials.Count - 1;
             index >=
                 firstUnattachedPrimitive;
             index--)
        {
            _pendingMaterials.Add(
                _trackedMaterials[index]);
        }

        if (!_baseMaterialsTransferredToModel)
        {
            for (int index =
                     _baseMaterialCount - 1;
                 index >= 0;
                 index--)
            {
                _pendingMaterials.Add(
                    _trackedMaterials[index]);
            }
        }

        _directOwnershipInitialized = true;
        _trackedMeshes.Clear();
        _trackedMaterials.Clear();
    }

    private void TryDisposeModel(
        ref List<Exception>? failures)
    {
        lock (_lock)
        {
            if (_modelDisposalCompleted)
                return;
        }

        try
        {
            _model.Dispose();
            lock (_lock)
                _modelDisposalCompleted = true;
        }
        catch (Exception disposeFailure)
        {
            (failures ??= new List<Exception>())
                .Add(disposeFailure);
        }
    }

    private void TryDrainPrimitiveTextures(
        ref List<Exception>? failures)
    {
        int remainingAttempts;
        lock (_lock)
            remainingAttempts = _pendingPrimitiveTextures.Count;

        int index = remainingAttempts - 1;
        while (remainingAttempts > 0)
        {
            TextureHandle occurrence;
            lock (_lock)
                occurrence = _pendingPrimitiveTextures[index];

            try
            {
                _releaseTexture(occurrence);
                lock (_lock)
                    _pendingPrimitiveTextures.RemoveAt(index);
            }
            catch (Exception releaseFailure)
            {
                (failures ??= new List<Exception>())
                    .Add(releaseFailure);
            }

            index--;
            remainingAttempts--;
        }
    }

    private void TryDrainMeshes(
        ref List<Exception>? failures)
    {
        TryDrainOccurrences(
            _pendingMeshes,
            _releaseMesh,
            ref failures);
    }

    private void TryDrainMaterials(
        ref List<Exception>? failures)
    {
        TryDrainOccurrences(
            _pendingMaterials,
            _releaseMaterial,
            ref failures);
    }

    private void TryDrainOccurrences<T>(
        List<T> pending,
        Action<T> release,
        ref List<Exception>? failures)
    {
        int remainingAttempts;
        lock (_lock)
            remainingAttempts = pending.Count;

        int index = 0;
        while (remainingAttempts > 0)
        {
            T occurrence;
            lock (_lock)
                occurrence = pending[index];

            try
            {
                release(occurrence);
                lock (_lock)
                    pending.RemoveAt(index);
            }
            catch (Exception releaseFailure)
            {
                (failures ??= new List<Exception>())
                    .Add(releaseFailure);
                index++;
            }

            remainingAttempts--;
        }
    }

    private void ThrowIfOwnershipClosedLocked()
    {
        if (_committed)
        {
            throw new InvalidOperationException(
                "Committed model ownership cannot be mutated.");
        }
        if (_rollbackStarted)
        {
            throw new InvalidOperationException(
                "Model ownership cannot be mutated after rollback starts.");
        }
    }
}
