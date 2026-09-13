using System.Collections.ObjectModel;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

/// <summary>An editable transform in a scene hierarchy. Matrices use Njulf's row-vector convention.</summary>
public sealed class SceneNode : IIdentifiedSceneEntity
{
    private readonly List<SceneNode> _children = [];
    private readonly ReadOnlyCollection<SceneNode> _readOnlyChildren;
    private SceneNode? _parent;
    private Matrix4x4 _localMatrix = Matrix4x4.Identity;

    public SceneNode() => _readOnlyChildren = _children.AsReadOnly();

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Node";
    public SceneNode? Parent => _parent;
    public IReadOnlyList<SceneNode> Children => _readOnlyChildren;
    public Matrix4x4 LocalMatrix
    {
        get => _localMatrix;
        set
        {
            if (_localMatrix.Equals(value)) return;
            Matrix4x4 oldWorld = WorldMatrix;
            _localMatrix = value;
            PublishWorldChanged(oldWorld);
        }
    }

    public Matrix4x4 WorldMatrix => _parent == null ? _localMatrix : _localMatrix * _parent.WorldMatrix;
    public Vector3 Position
    {
        get => _localMatrix.Translation;
        set
        {
            Matrix4x4 next = _localMatrix;
            next.M41 = value.X; next.M42 = value.Y; next.M43 = value.Z;
            LocalMatrix = next;
        }
    }

    /// <summary>Local translation relative to Parent. Editing translation preserves shear.</summary>
    public Vector3 LocalPosition { get => Position; set => Position = value; }
    public Quaternion LocalRotation
    {
        get => Decompose(LocalMatrix).Rotation;
        set { var trs = Decompose(LocalMatrix); SetLocalTransform(trs.Position, value, trs.Scale); }
    }
    public Vector3 LocalScale
    {
        get => Decompose(LocalMatrix).Scale;
        set { var trs = Decompose(LocalMatrix); SetLocalTransform(trs.Position, trs.Rotation, value); }
    }

    /// <summary>Local rotation convenience; like RenderObject.Rotation, editing replaces non-TRS components.</summary>
    public Quaternion Rotation
    {
        get => LegacyLocalTransform().Rotation;
        set { var trs = LegacyLocalTransform(); SetLocalTransform(trs.Position, value, trs.Scale); }
    }
    /// <summary>Local scale convenience; like RenderObject.Scale, editing replaces non-TRS components.</summary>
    public Vector3 Scale
    {
        get => LegacyLocalTransform().Scale;
        set { var trs = LegacyLocalTransform(); SetLocalTransform(trs.Position, trs.Rotation, value); }
    }

    public Vector3 WorldPosition
    {
        get => WorldMatrix.Translation;
        set
        {
            Matrix4x4 world = WorldMatrix;
            world.M41 = value.X; world.M42 = value.Y; world.M43 = value.Z;
            SetWorldMatrix(world);
        }
    }
    public Quaternion WorldRotation
    {
        get => Decompose(WorldMatrix).Rotation;
        set { var trs = Decompose(WorldMatrix); SetWorldTransform(trs.Position, value, trs.Scale); }
    }
    public Vector3 WorldScale
    {
        get => Decompose(WorldMatrix).Scale;
        set { var trs = Decompose(WorldMatrix); SetWorldTransform(trs.Position, trs.Rotation, value); }
    }

    /// <summary>Replaces the local matrix with scale * rotation * translation.</summary>
    /// <remarks>Physics-owned dynamic poses must use PhysicsScene.Teleport. Kinematic motion uses
    /// SetKinematicTarget; changing collider scale requires recreating its registration.</remarks>
    public void SetLocalTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        LocalMatrix = Compose(position, rotation, scale);

    /// <summary>Replaces the world transform; requires an invertible parent. May produce local shear.</summary>
    public void SetWorldTransform(Vector3 position, Quaternion rotation, Vector3 scale) =>
        SetWorldMatrix(Compose(position, rotation, scale));

    private static Matrix4x4 Compose(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * RenderObject.NormalizeRotation(rotation).ToMatrix4x4() * Matrix4x4.CreateTranslation(position);

    private static (Vector3 Position, Quaternion Rotation, Vector3 Scale) Decompose(Matrix4x4 matrix)
    {
        if (RenderObject.TryDecompose(matrix, out var position, out var rotation, out var scale))
            return (position, rotation, scale);
        throw new InvalidOperationException("Rotation/scale components require a non-degenerate TRS matrix. Use an explicit matrix or whole-TRS setter to replace shear.");
    }

    private (Vector3 Position, Quaternion Rotation, Vector3 Scale) LegacyLocalTransform() =>
        RenderObject.TryDecompose(LocalMatrix, out var position, out var rotation, out var scale)
            ? (position, rotation, scale) : (LocalMatrix.Translation, Quaternion.Identity, LocalMatrix.Scale);

    public event Action<SceneNode, Matrix4x4, Matrix4x4>? WorldChanged;

    /// <summary>Reparents this node, preserving world placement by default; false preserves the local matrix.</summary>
    public void SetParent(SceneNode? parent, bool keepWorld = true)
    {
        if (ReferenceEquals(_parent, parent)) return;
        for (SceneNode? ancestor = parent; ancestor != null; ancestor = ancestor._parent)
            if (ReferenceEquals(ancestor, this)) throw new InvalidOperationException("A scene-node hierarchy cannot contain a cycle.");

        Matrix4x4 oldWorld = WorldMatrix;
        Matrix4x4 nextLocal = _localMatrix;
        if (keepWorld && parent != null)
            nextLocal = oldWorld * parent.WorldMatrix.Invert();
        else if (keepWorld)
            nextLocal = oldWorld;

        _parent?._children.Remove(this);
        _parent = parent;
        parent?._children.Add(this);
        _localMatrix = nextLocal;
        PublishWorldChanged(oldWorld);
    }

    public void SetWorldMatrix(Matrix4x4 world) =>
        LocalMatrix = _parent == null ? world : world * _parent.WorldMatrix.Invert();

    private void PublishWorldChanged(Matrix4x4 oldWorld)
    {
        Matrix4x4 newWorld = WorldMatrix;
        WorldChanged?.Invoke(this, oldWorld, newWorld);
        foreach (SceneNode child in _children)
            child.PublishAncestorWorldChanged(oldWorld, newWorld);
    }

    private void PublishAncestorWorldChanged(Matrix4x4 oldParentWorld, Matrix4x4 newParentWorld)
    {
        Matrix4x4 oldWorld = _localMatrix * oldParentWorld;
        Matrix4x4 newWorld = _localMatrix * newParentWorld;
        WorldChanged?.Invoke(this, oldWorld, newWorld);
        foreach (SceneNode child in _children)
            child.PublishAncestorWorldChanged(oldWorld, newWorld);
    }
}
