using System;
using System.Collections;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Tracks resources whose ownership is in flight while one imported material is
/// assembled. Texture references remain pending until material registration
/// succeeds; at that point the material owns them and becomes the rollback unit.
///
/// Rollback is durable and serialized. A release occurrence is removed only
/// after its callback returns successfully, so a later call retries exactly the
/// failed occurrences without double-releasing completed work.
/// </summary>
internal sealed class ModelUploadOwnershipLedger
{
    private readonly Action<MaterialHandle> _releaseMaterial;
    private readonly Action<TextureHandle> _releaseTexture;
    private readonly List<MaterialHandle> _materials;
    private readonly List<TextureHandle> _pendingTextures;
    private readonly PendingTextureCollection _pendingTextureCollection;
    private readonly object _lock = new();
    private bool _rollbackStarted;
    private bool _rollbackCompleted;
    private bool _rollbackInProgress;
    private int _rollbackThreadId;

    public ModelUploadOwnershipLedger(
        int materialCapacity,
        int pendingTextureCapacity,
        Action<MaterialHandle> releaseMaterial,
        Action<TextureHandle> releaseTexture)
    {
        if (materialCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(materialCapacity));
        if (pendingTextureCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(pendingTextureCapacity));

        _releaseMaterial =
            releaseMaterial ?? throw new ArgumentNullException(nameof(releaseMaterial));
        _releaseTexture =
            releaseTexture ?? throw new ArgumentNullException(nameof(releaseTexture));
        _materials = new List<MaterialHandle>(materialCapacity);
        _pendingTextures = new List<TextureHandle>(pendingTextureCapacity);
        _pendingTextureCollection = new PendingTextureCollection(this);
    }

    public ICollection<TextureHandle> PendingTextures =>
        _pendingTextureCollection;

    internal int PendingTextureCount
    {
        get
        {
            lock (_lock)
                return _pendingTextures.Count;
        }
    }

    internal int PendingMaterialCount
    {
        get
        {
            lock (_lock)
                return _materials.Count;
        }
    }

    internal int PendingResourceCount
    {
        get
        {
            lock (_lock)
            {
                return checked(
                    _pendingTextures.Count +
                    _materials.Count);
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

    public void CommitPendingTexturesTo(MaterialHandle material)
    {
        if (!material.IsValid)
        {
            throw new ArgumentException(
                "A committed material handle must be valid.",
                nameof(material));
        }

        lock (_lock)
        {
            ThrowIfRollbackStartedLocked();

            // Capacity is reserved before acquisition begins, so this commit
            // cannot allocate after the material has accepted texture
            // ownership.
            _materials.Add(material);
            _pendingTextures.Clear();
        }
    }

    public Exception? TryRollback()
    {
        int currentThreadId = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            while (_rollbackInProgress)
            {
                if (_rollbackThreadId == currentThreadId)
                {
                    return new InvalidOperationException(
                        "A model-upload ownership rollback cannot re-enter its active drain.");
                }

                Monitor.Wait(_lock);
            }

            if (_rollbackCompleted)
                return null;

            _rollbackStarted = true;
            _rollbackInProgress = true;
            _rollbackThreadId = currentThreadId;
        }

        try
        {
            List<Exception>? failures = null;
            RollbackPendingTextures(ref failures);
            RollbackMaterials(ref failures);

            lock (_lock)
            {
                _rollbackCompleted =
                    _pendingTextures.Count == 0 &&
                    _materials.Count == 0;
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

    private void RollbackPendingTextures(
        ref List<Exception>? failures)
    {
        int initialCount;
        lock (_lock)
            initialCount = _pendingTextures.Count;

        for (int index = initialCount - 1;
             index >= 0;
             index--)
        {
            TextureHandle texture;
            lock (_lock)
                texture = _pendingTextures[index];

            try
            {
                if (texture.IsValid)
                    _releaseTexture(texture);

                lock (_lock)
                {
                    if (_pendingTextures[index] != texture)
                    {
                        throw new InvalidOperationException(
                            "Pending model-upload texture ownership changed during rollback.");
                    }

                    _pendingTextures.RemoveAt(index);
                }
            }
            catch (Exception rollbackFailure)
            {
                (failures ??= new List<Exception>())
                    .Add(rollbackFailure);
            }
        }
    }

    private void RollbackMaterials(
        ref List<Exception>? failures)
    {
        int initialCount;
        lock (_lock)
            initialCount = _materials.Count;

        for (int index = initialCount - 1;
             index >= 0;
             index--)
        {
            MaterialHandle material;
            lock (_lock)
                material = _materials[index];

            try
            {
                if (material.IsValid)
                    _releaseMaterial(material);

                lock (_lock)
                {
                    if (_materials[index] != material)
                    {
                        throw new InvalidOperationException(
                            "Committed model-upload material ownership changed during rollback.");
                    }

                    _materials.RemoveAt(index);
                }
            }
            catch (Exception rollbackFailure)
            {
                (failures ??= new List<Exception>())
                    .Add(rollbackFailure);
            }
        }
    }

    private void AddPendingTexture(TextureHandle texture)
    {
        if (!texture.IsValid)
        {
            throw new ArgumentException(
                "Pending texture ownership requires a valid handle.",
                nameof(texture));
        }

        lock (_lock)
        {
            ThrowIfRollbackStartedLocked();
            _pendingTextures.Add(texture);
        }
    }

    private void ClearPendingTextures()
    {
        lock (_lock)
        {
            ThrowIfRollbackStartedLocked();
            _pendingTextures.Clear();
        }
    }

    private bool RemovePendingTexture(TextureHandle texture)
    {
        lock (_lock)
        {
            ThrowIfRollbackStartedLocked();
            return _pendingTextures.Remove(texture);
        }
    }

    private bool ContainsPendingTexture(TextureHandle texture)
    {
        lock (_lock)
            return _pendingTextures.Contains(texture);
    }

    private TextureHandle[] GetPendingTextureSnapshot()
    {
        lock (_lock)
            return _pendingTextures.ToArray();
    }

    private void ThrowIfRollbackStartedLocked()
    {
        if (_rollbackStarted)
        {
            throw new InvalidOperationException(
                "A model-upload ownership ledger cannot be mutated after rollback begins.");
        }
    }

    private sealed class PendingTextureCollection :
        ICollection<TextureHandle>
    {
        private readonly ModelUploadOwnershipLedger _owner;

        public PendingTextureCollection(
            ModelUploadOwnershipLedger owner)
        {
            _owner = owner;
        }

        public int Count => _owner.PendingTextureCount;

        public bool IsReadOnly => false;

        public void Add(TextureHandle item) =>
            _owner.AddPendingTexture(item);

        public void Clear() =>
            _owner.ClearPendingTextures();

        public bool Contains(TextureHandle item) =>
            _owner.ContainsPendingTexture(item);

        public void CopyTo(
            TextureHandle[] array,
            int arrayIndex) =>
            _owner.GetPendingTextureSnapshot()
                .CopyTo(array, arrayIndex);

        public bool Remove(TextureHandle item) =>
            _owner.RemovePendingTexture(item);

        public IEnumerator<TextureHandle> GetEnumerator() =>
            ((IEnumerable<TextureHandle>)_owner
                .GetPendingTextureSnapshot())
            .GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
