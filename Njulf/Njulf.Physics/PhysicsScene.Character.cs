using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;
using Njulf.Core.Math;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

public sealed partial class PhysicsScene
{
    private Entry? _characterQuery;
    private ColliderHandle _characterIgnore;
    private DynamicTree.SweepCastFilterPre? _characterSweepFilter;
    private readonly List<IDynamicTreeProxy> _characterCandidates = [];

    private bool AcceptCharacter(IDynamicTreeProxy proxy) => _owners.TryGetValue(proxy, out var e) &&
        e.Enabled && !e.Settings.IsTrigger && !ReferenceEquals(e, _characterQuery) && e.Handle != _characterIgnore &&
        (e.Layer & _characterQuery!.Mask) != 0 && (e.Mask & _characterQuery.Layer) != 0;

    internal bool SweepCharacter(ColliderHandle character, Vector3 position, float radius, float length,
        Vector3 direction, float distance, ColliderHandle ignore, out RaycastHit hit)
    {
        _characterQuery = Get(character); _characterIgnore = ignore; Synchronize();
        var capsule = SupportPrimitives.CreateCapsule(radius, length / 2);
        if (_tree.SweepCast(capsule, JQuaternion.Identity, ToJ(position), ToJ(direction), distance,
                _characterSweepFilter ??= AcceptCharacter, null, out var proxy, out _, out var point, out var normal, out float travelled))
        {
            var e = _owners[proxy!];
            hit = new(e.Handle, e.Owner, FromJ(point), FromJ(-normal), travelled);
            return true;
        }
        hit = default; return false;
    }

    // Return the deepest penetration. The controller re-queries after each bounded correction.
    internal bool CharacterPenetration(ColliderHandle character, Vector3 position, float radius, float length,
        out Vector3 normal, out float depth)
    {
        _characterQuery = Get(character); _characterIgnore = default; Synchronize();
        var capsule = SupportPrimitives.CreateCapsule(radius, length / 2);
        var p = ToJ(position); var extent = new JVector(radius, radius + length / 2, radius);
        _characterCandidates.Clear(); _tree.Query(_characterCandidates, new JBoundingBox(p - extent, p + extent));
        depth = 0; normal = default;
        foreach (var proxy in _characterCandidates)
        {
            if (!AcceptCharacter(proxy)) continue;
            var e = _owners[proxy]; var shape = (RigidBodyShape)proxy;
            if (NarrowPhase.MprEpa(capsule, shape, JQuaternion.Identity, e.Body!.Orientation, p, e.Body.Position,
                    out _, out _, out var direction, out float penetration) && penetration > depth)
            { depth = penetration; normal = FromJ(-direction); }
        }
        _characterCandidates.Clear();
        return depth > 1e-4f;
    }

    internal bool TryGetSupport(ColliderHandle handle, out PhysicsPose pose, out long teleportVersion)
    {
        Check(); pose = default; teleportVersion = 0;
        if (!IsValid(handle)) return false;
        var e = _entries[handle.Id];
        if (!e.Enabled || e.Settings.IsTrigger) return false;
        Synchronize(); pose = e.Pose; teleportVersion = e.TeleportVersion; return true;
    }

    // A capsule touching a tread's edge has a rounded sweep normal. Sample the actual supporting surface
    // just inside that edge so a flat stair is not mistaken for a steep slope.
    internal Vector3 SupportSurfaceNormal(RaycastHit hit, Vector3 capsulePosition, float skin)
    {
        var e = Get(hit.Collider); var body = e.Body!;
        var inward = new Vector3(hit.Point.X - capsulePosition.X, 0, hit.Point.Z - capsulePosition.Z).Normalized();
        var origin = ToJ(hit.Point + inward * (skin * .1f) + Vector3.UnitY * (skin * 2));
        var localOrigin = JVector.ConjugatedTransform(origin - body.Position, body.Orientation);
        var localDirection = JVector.ConjugatedTransform(new JVector(0, -1, 0), body.Orientation);
        float closest = skin * 4; Vector3 result = hit.Normal;
        foreach (var shape in e.Shapes)
            if (shape.LocalRayCast(localOrigin, localDirection, out var normal, out float distance) && distance >= 0 && distance < closest)
            { closest = distance; result = FromJ(JVector.Transform(normal, body.Orientation)); }
        return result;
    }
}
