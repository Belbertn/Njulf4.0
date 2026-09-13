using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

public sealed partial class PhysicsScene
{
    private readonly Dictionary<(long, long), ContactDetails> _contactDetails = [];
    private readonly Dictionary<(long, long), PhysicsTrigger> _triggerPairs = [];
    private readonly HashSet<(long, long)> _triggerSeen = [];
    private readonly List<(long, long)> _triggerEnded = [];
    private readonly List<IDynamicTreeProxy> _triggerCandidates = [];
    private readonly List<PhysicsTrigger> _triggerNotifications = [], _triggerDispatch = [];
    private int _eventGeneration;

    public event Action<PhysicsTrigger>? Trigger;

    private sealed class ContactFilter(PhysicsScene scene) : INarrowPhaseFilter
    {
        public bool Filter(RigidBodyShape shapeA, RigidBodyShape shapeB, ref JVector pointA,
            ref JVector pointB, ref JVector normal, ref float penetration)
        {
            var a = scene._owners[shapeA]; var b = scene._owners[shapeB];
            var ba = a.Body!; var bb = b.Body!;
            var va = ba.Velocity + JVector.Cross(ba.AngularVelocity, pointA - ba.Position);
            var vb = bb.Velocity + JVector.Cross(bb.AngularVelocity, pointB - bb.Position);
            // 2.8.11's filter callback supplies A -> B (its XML comment says the opposite).
            var direction = normal;
            float speed = MathF.Max(0, JVector.Dot(va - vb, direction));
            if (a.Handle.Id > b.Handle.Id) { (a, b) = (b, a); direction = -direction; }
            var key = (a.Handle.Id, b.Handle.Id);
            if (!scene._contactDetails.TryGetValue(key, out var old) || speed > old.ClosingSpeed)
                scene._contactDetails[key] = new(FromJ((pointA + pointB) * .5f), FromJ(direction), speed);
            return true;
        }
    }

    private void UpdateTriggers()
    {
        _triggerSeen.Clear();
        foreach (var a in _entries.Values)
        {
            if (!a.Enabled || !a.Settings.IsTrigger) continue;
            foreach (var shapeA in a.Shapes)
            {
                shapeA.CalculateBoundingBox(a.Body!.Orientation, a.Body.Position, out var bounds);
                _triggerCandidates.Clear(); _tree.Query(_triggerCandidates, bounds);
                foreach (var proxy in _triggerCandidates)
                {
                    var b = _owners[proxy];
                    if (ReferenceEquals(a, b) || !b.Enabled || (a.Layer & b.Mask) == 0 || (b.Layer & a.Mask) == 0) continue;
                    var key = a.Handle.Id < b.Handle.Id ? (a.Handle.Id, b.Handle.Id) : (b.Handle.Id, a.Handle.Id);
                    if (_triggerSeen.Contains(key)) continue;
                    var shapeB = (RigidBodyShape)proxy;
                    if (!NarrowPhase.Overlap(shapeA, shapeB, a.Body.Orientation, b.Body!.Orientation, a.Body.Position, b.Body.Position)) continue;
                    _triggerSeen.Add(key);
                    if (_triggerPairs.ContainsKey(key)) continue;
                    var first = a.Handle.Id < b.Handle.Id ? a : b;
                    var second = ReferenceEquals(first, a) ? b : a;
                    var notification = new PhysicsTrigger(first.Handle, first.Owner, second.Handle, second.Owner, true);
                    _triggerPairs.Add(key, notification); _triggerNotifications.Add(notification);
                }
            }
        }
        _triggerEnded.Clear();
        foreach (var pair in _triggerPairs)
            if (!_triggerSeen.Contains(pair.Key)) _triggerEnded.Add(pair.Key);
        FlushTriggerEnds();
        _triggerCandidates.Clear();
    }

    private void EndTriggers(Entry entry)
    {
        _triggerEnded.Clear();
        foreach (var pair in _triggerPairs)
            if (pair.Key.Item1 == entry.Handle.Id || pair.Key.Item2 == entry.Handle.Id) _triggerEnded.Add(pair.Key);
        FlushTriggerEnds();
    }

    private void FlushTriggerEnds()
    {
        foreach (var key in _triggerEnded)
        {
            _triggerNotifications.Add(_triggerPairs[key] with { Entered = false });
            _triggerPairs.Remove(key);
        }
        _triggerEnded.Clear();
    }
}
