using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

public sealed class StaticInstanceBatch :
    IIdentifiedSceneEntity,
    IDisposable
{
    private readonly List<Matrix4x4> _worldMatrices;
    private readonly ReadOnlyCollection<Matrix4x4> _readOnlyWorldMatrices;
    private uint _revision = 1;
    private RenderObject? _resourceOwner;

    public StaticInstanceBatch(IEnumerable<Matrix4x4> worldMatrices)
    {
        if (worldMatrices == null)
            throw new ArgumentNullException(nameof(worldMatrices));

        _worldMatrices = new List<Matrix4x4>(worldMatrices);
        _readOnlyWorldMatrices = _worldMatrices.AsReadOnly();
    }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "StaticInstanceBatch";
    /// <summary>Source model identity used by scene serialization.</summary>
    public SceneAssetReference? AssetReference { get; set; }
    public object? Mesh { get; set; }
    public object? Material { get; set; }
    public bool Visible { get; set; } = true;
    public IReadOnlyList<Matrix4x4> WorldMatrices => _readOnlyWorldMatrices;
    public uint Revision => _revision;

    /// <summary>
    /// Transfers a render object's mesh/material leases to this batch. The
    /// source object is retained solely as the retryable lifetime owner and is
    /// disposed with the batch.
    /// </summary>
    public void AdoptResourceOwner(RenderObject source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_resourceOwner != null)
        {
            throw new InvalidOperationException(
                "Static-instance resource ownership is already attached.");
        }
        if (!Equals(Mesh, source.Mesh) ||
            !Equals(Material, source.Material))
        {
            throw new InvalidOperationException(
                "Static-instance handles must match the transferred resource owner.");
        }

        _resourceOwner = source;
    }

    public void ReplaceWorldMatrices(IEnumerable<Matrix4x4> worldMatrices)
    {
        if (worldMatrices == null)
            throw new ArgumentNullException(nameof(worldMatrices));

        _worldMatrices.Clear();
        _worldMatrices.AddRange(worldMatrices);
        _revision++;
        if (_revision == 0)
            _revision = 1;
    }

    public void Dispose()
    {
        if (_resourceOwner == null)
            return;

        _resourceOwner.Dispose();
        _resourceOwner = null;
        Mesh = null;
        Material = null;
    }
}
