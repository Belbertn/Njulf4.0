using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

public sealed class StaticInstanceBatch : IIdentifiedSceneEntity
{
    private readonly List<Matrix4x4> _worldMatrices;
    private readonly ReadOnlyCollection<Matrix4x4> _readOnlyWorldMatrices;
    private uint _revision = 1;

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
}
