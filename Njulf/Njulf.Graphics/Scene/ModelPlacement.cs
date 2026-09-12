using Njulf.Core.Math;

namespace Njulf.Core.Scene;

/// <summary>One placement's transform hierarchy, shared by incrementally cloned primitives.</summary>
public sealed class ModelPlacement
{
    private readonly Model _source;
    private readonly Dictionary<SceneNode, SceneNode> _nodes = new(ReferenceEqualityComparer.Instance);
    public SceneNode Root { get; } = new();
    public IReadOnlyCollection<SceneNode> Nodes => _nodes.Values;

    public ModelPlacement(Model source)
    {
        _source = source;
        Root.Name = source.Name;
        foreach (SceneNode node in source.Nodes) CloneNode(node);
    }

    private SceneNode CloneNode(SceneNode source)
    {
        if (_nodes.TryGetValue(source, out SceneNode? existing)) return existing;
        var clone = new SceneNode { Name = source.Name, LocalMatrix = source.LocalMatrix };
        _nodes.Add(source, clone);
        clone.SetParent(source.Parent is { } parent ? CloneNode(parent) : Root, keepWorld: false);
        return clone;
    }

    public IEnumerable<RenderObject> CreateTransformGroups()
    {
        var represented = _source.RenderObjects.Select(item => item.Node).ToHashSet();
        foreach (SceneNode node in _nodes.Where(pair => !represented.Contains(pair.Key))
                     .Select(pair => pair.Value).Prepend(Root))
        {
            var group = new RenderObject { Name = node.Name, IsTransformGroup = true, PlacementRoot = Root };
            group.AttachNode(node, Matrix4x4.Identity);
            yield return group;
        }
    }

    public RenderObject CreateRenderObjectInstance(int index)
    {
        RenderObject source = _source.RenderObjects[index];
        RenderObject clone = _source.CreateRenderObjectInstance(index);
        try
        {
            clone.AttachNode(CloneNode(source.Node), source.MeshToNode);
            clone.PlacementRoot = Root;
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }
}
