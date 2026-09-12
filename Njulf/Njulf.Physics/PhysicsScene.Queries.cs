using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;
using Njulf.Core.Math;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

public sealed partial class PhysicsScene
{
    private readonly DynamicTree.RayCastFilterPre _rayFilter;
    private readonly DynamicTree.SweepCastFilterPre _sweepFilter;
    private QueryFilter _filter;
    private readonly List<IDynamicTreeProxy> _candidates = [];
    private readonly HashSet<long> _overlapSeen = [];

    private bool Accept(IDynamicTreeProxy proxy) => _owners.TryGetValue(proxy, out var e) && e.Enabled &&
        (e.Layer & _filter.LayerMask) != 0 && e.Owner != _filter.IgnoreOwner;

    private JVector Prepare(Vector3 origin, Vector3 direction, float maxDistance, QueryFilter? filter)
    {
        Check(); Finite(origin); Finite(direction);
        if (!float.IsFinite(maxDistance) || maxDistance < 0) throw new ArgumentOutOfRangeException(nameof(maxDistance));
        // Normalize in double precision, including very large or very small finite inputs.
        double length = System.Math.Sqrt((double)direction.X * direction.X + (double)direction.Y * direction.Y + (double)direction.Z * direction.Z);
        if (length == 0) throw new ArgumentException("Direction must be nonzero.", nameof(direction));
        Synchronize(); _filter = filter ?? new QueryFilter(uint.MaxValue);
        return new((float)(direction.X / length), (float)(direction.Y / length), (float)(direction.Z / length));
    }

    /// <summary>Closest exact hit, with distance in world units. Initial overlap returns distance/normal zero.
    /// Invalid inputs throw; no hit returns false and a default hit.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit, QueryFilter? filter = null)
    {
        var d = Prepare(origin, direction, maxDistance, filter); var p = ToJ(origin);
        // 2.8.11's ray helper uses a strict '<' bound. Make our documented endpoint inclusive, including zero.
        if (_tree.RayCast(p, d, MathF.BitIncrement(maxDistance), _rayFilter, null, out var proxy, out var normal, out float distance) && distance <= maxDistance)
        {
            var e = _owners[proxy!]; hit = new(e.Handle, e.Owner, FromJ(p + d * distance), FromJ(normal), distance); return true;
        }
        hit = default; return false;
    }

    public bool SweepSphere(Vector3 center, float radius, Vector3 direction, float maxDistance, out RaycastHit hit, QueryFilter? filter = null)
    {
        Positive(radius);
        var shape = SupportPrimitives.CreateSphere(radius);
        return Sweep(shape, new PhysicsPose(center), direction, maxDistance, out hit, filter);
    }

    /// <summary>Y-axis capsule with straight-segment length. Initial overlap returns distance/normal zero and the query origin as point.</summary>
    public bool SweepCapsule(PhysicsPose pose, float radius, float length, Vector3 direction, float maxDistance, out RaycastHit hit, QueryFilter? filter = null)
    {
        Positive(radius);
        if (!float.IsFinite(length) || length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        var shape = SupportPrimitives.CreateCapsule(radius, length / 2);
        return Sweep(shape, Validate(pose), direction, maxDistance, out hit, filter);
    }
    private bool Sweep<T>(T shape, PhysicsPose pose, Vector3 direction, float maxDistance, out RaycastHit hit, QueryFilter? filter) where T : ISupportMappable
    {
        var d = Prepare(pose.Position, direction, maxDistance, filter);
        if (_tree.SweepCast(shape, ToJ(pose.Rotation), ToJ(pose.Position), d, maxDistance, _sweepFilter, null,
                out var proxy, out _, out var point, out var normal, out float distance))
        {
            var e = _owners[proxy!];
            // Jitter's sweep normal points from query to target; expose the target's outward normal like Raycast.
            hit = new(e.Handle, e.Owner, distance == 0 ? pose.Position : FromJ(point), FromJ(-normal), distance); return true;
        }
        hit = default; return false;
    }

    /// <summary>Exact overlaps after tree pruning. Counts all unique registrations even when the caller's buffer is full.</summary>
    public OverlapResult OverlapSphere(Vector3 center, float radius, Span<OverlapHit> results, QueryFilter? filter = null)
    {
        Check(); Finite(center); Positive(radius); Synchronize();
        _filter = filter ?? new QueryFilter(uint.MaxValue);
        var p = ToJ(center); var r = new JVector(radius);
        var sphere = SupportPrimitives.CreateSphere(radius);
        _candidates.Clear(); _overlapSeen.Clear();
        _tree.Query(_candidates, new JBoundingBox(p - r, p + r));
        int total = 0, written = 0;
        foreach (var proxy in _candidates)
        {
            if (!Accept(proxy)) continue;
            var e = _owners[proxy];
            if (_overlapSeen.Contains(e.Handle.Id)) continue;
            RigidBodyShape shape; JVector position; JQuaternion rotation;
            if (proxy is QueryProxy q) { shape = q.Shape; position = q.Position; rotation = q.Rotation; }
            else { shape = (RigidBodyShape)proxy; position = e.Body!.Position; rotation = e.Body.Orientation; }
            if (!NarrowPhase.Overlap(sphere, shape, JQuaternion.Identity, rotation, p, position)) continue;
            _overlapSeen.Add(e.Handle.Id); total++;
            if (written < results.Length) results[written++] = new(e.Handle, e.Owner);
        }
        _candidates.Clear();
        return new(written, total);
    }

    /// <summary>Broadphase bounds for optional debug visualization, one per shape. Returns required buffer length.</summary>
    public int GetDebugBounds(Span<BoundingBox> bounds)
    {
        Check(); Synchronize(); int count = 0;
        foreach (var proxy in _owners.Keys)
        {
            if (count < bounds.Length) bounds[count] = new(FromJ(proxy.WorldBoundingBox.Min), FromJ(proxy.WorldBoundingBox.Max));
            count++;
        }
        return count;
    }
}
