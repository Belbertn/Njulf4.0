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

    public event Action<SceneNode, Matrix4x4, Matrix4x4>? WorldChanged;

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
