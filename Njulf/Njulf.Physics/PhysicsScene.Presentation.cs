using Njulf.Core.Math;
using Njulf.Core.Scene;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

public sealed partial class PhysicsScene
{
    private sealed record Presentation(Entry Body, SceneNode Node, Vector3 Scale);
    private readonly Dictionary<long, Presentation> _presentations = [];

    /// <summary>Interpolates only the two latest completed poses; alpha must be finite and between zero and one.</summary>
    public PhysicsPose GetInterpolatedPose(ColliderHandle handle, float alpha)
    {
        var e = Get(handle); CheckAlpha(alpha);
        if (e.Settings.Kind == BodyKind.Static) { Synchronize(); return e.Pose; }
        return Interpolate(e, alpha);
    }

    /// <summary>Binds a separate visual node, preserving its world scale. Null removes the binding.</summary>
    /// <remarks>The node must not contain physics nodes or overlap another presentation binding's hierarchy.</remarks>
    public void BindPresentation(ColliderHandle handle, SceneNode? node)
    {
        var e = Get(handle);
        if (node == null) { _presentations.Remove(handle.Id); return; }
        if (e.Settings.Kind == BodyKind.Static) throw new ArgumentException("Presentation interpolation requires a moving body.");
        CheckPresentationNode(node, handle.Id);
        var binding = new Presentation(e, node, Decompose(node.WorldMatrix).Scale);
        _presentations[handle.Id] = binding;
        if (Scene != null)
            foreach (var visual in Scene.RenderObjects)
                if (IsAncestor(node, visual.Node)) visual.IsStatic = false;
        PublishPresentation(binding, e.CurrentCompleted);
    }

    /// <summary>Call after all fixed steps using Game.InterpolationAlpha and Game.IsSimulationPaused.</summary>
    /// <remarks>Pause collapses history to the latest completed pose. Queries and authoritative nodes remain untouched.</remarks>
    public void UpdatePresentation(float alpha, bool isPaused = false)
    {
        Check(); CheckAlpha(alpha); Synchronize();
        // Reparenting is allowed by SceneNode; detect unsafe new ancestry before writing any visual transforms.
        foreach (var binding in _presentations.Values) CheckPresentationNode(binding.Node, binding.Body.Handle.Id);
        if (isPaused)
            foreach (var e in _entries.Values) e.PreviousCompleted = e.CurrentCompleted;
        foreach (var binding in _presentations.Values)
            PublishPresentation(binding, Interpolate(binding.Body, isPaused ? 1 : alpha));
    }

    private static PhysicsPose Interpolate(Entry e, float alpha) => new(
        e.PreviousCompleted.Position + (e.CurrentCompleted.Position - e.PreviousCompleted.Position) * alpha,
        Quaternion.Slerp(e.PreviousCompleted.Rotation, e.CurrentCompleted.Rotation, alpha));

    private void PublishPresentation(Presentation binding, PhysicsPose pose)
    {
        CheckPresentationNode(binding.Node, binding.Body.Handle.Id);
        binding.Node.SetWorldMatrix(Matrix4x4.CreateScale(binding.Scale) * pose.ToMatrix());
    }

    private static void CheckAlpha(float alpha)
    {
        if (!float.IsFinite(alpha) || alpha < 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
    }

    private static bool IsAncestor(SceneNode ancestor, SceneNode node)
    {
        for (SceneNode? current = node; current != null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void CheckPresentationNode(SceneNode node, long id)
    {
        foreach (var physicsNode in _bindings.Keys)
            if (IsAncestor(node, physicsNode)) throw new ArgumentException("A presentation node cannot contain registered physics nodes.");
        foreach (var binding in _presentations.Values)
            if (binding.Body.Handle.Id != id && (IsAncestor(node, binding.Node) || IsAncestor(binding.Node, node)))
                throw new ArgumentException("Presentation bindings must have independent visual hierarchies.");
    }

    private void CheckPhysicsNodePresentation(SceneNode node)
    {
        foreach (var binding in _presentations.Values)
            if (IsAncestor(binding.Node, node)) throw new ArgumentException("Physics nodes cannot be registered under an interpolated visual.");
    }
}
