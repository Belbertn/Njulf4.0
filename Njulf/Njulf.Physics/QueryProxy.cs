using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace Njulf.Physics;

/// <summary>Standalone query geometry: no RigidBody and no World.</summary>
internal sealed class QueryProxy(RigidBodyShape shape) : IDynamicTreeProxy, IRayCastable, ISweepTestable, IUpdatableBoundingBox
{
    internal readonly RigidBodyShape Shape = shape;
    internal JVector Position;
    internal JQuaternion Rotation = JQuaternion.Identity;
    public int SetIndex { get; set; } = -1;
    public int NodePtr { get; set; } = -1;
    public JVector Velocity => JVector.Zero;
    public JBoundingBox WorldBoundingBox { get; private set; }
    public void UpdateWorldBoundingBox(float dt = 0)
    {
        Shape.CalculateBoundingBox(Rotation, Position, out var box);
        WorldBoundingBox = box;
    }
    public bool RayCast(in JVector origin, in JVector direction, out JVector normal, out float lambda)
    {
        bool hit = Shape.LocalRayCast(JVector.ConjugatedTransform(origin - Position, Rotation),
            JVector.ConjugatedTransform(direction, Rotation), out normal, out lambda);
        normal = JVector.Transform(normal, Rotation);
        return hit;
    }
    public bool Sweep<T>(in T support, in JQuaternion orientation, in JVector position, in JVector sweep,
        out JVector pointA, out JVector pointB, out JVector normal, out float lambda) where T : ISupportMappable =>
        NarrowPhase.Sweep(support, Shape, orientation, Rotation, position, Position, sweep, JVector.Zero,
            out pointA, out pointB, out normal, out lambda);
}
