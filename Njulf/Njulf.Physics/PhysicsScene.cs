using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

/// <summary>Game-owned physics state. All calls require the creating thread, outside a step.</summary>
public sealed partial class PhysicsScene : IDisposable
{
    private sealed class Entry
    {
        internal required ColliderHandle Handle;
        internal required Guid Owner;
        internal required RigidBodyShape[] Shapes;
        internal required BodySettings Settings;
        internal QueryProxy[] Proxies = [];
        internal RigidBody? Body;
        internal PhysicsPose Pose, PreviousKinematic;
        internal PhysicsPose? Target;
        internal Vector3 Center;
        internal SceneNode? Node;
        internal object? Entity;
        internal float Scale = 1;
        internal bool Enabled = true;
        internal uint Layer, Mask;
    }

    private readonly Guid _id = Guid.NewGuid();
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly World? _world;
    private readonly DynamicTree _tree;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly Dictionary<IDynamicTreeProxy, Entry> _owners = [];
    private readonly Dictionary<SceneNode, Entry> _bindings = [];
    private readonly HashSet<Entry> _dirty = [];
    private readonly List<Entry> _removals = [];
    private readonly HashSet<Arbiter> _arbiters = [];
    private readonly Dictionary<(long, long), int> _contacts = [];
    private readonly List<PhysicsContact> _notifications = [];
    private readonly List<PhysicsContact> _dispatch = [];
    private long _nextId;
    private bool _disposed, _stepping, _notifying;
    private SceneNode? _publishingNode;

    public PhysicsMode Mode { get; }
    public Scene? Scene { get; }
    public int ColliderCount { get { Check(); return _entries.Count; } }
    public bool HasDynamicsWorld => _world != null;
    public long UnmanagedBytes { get { Check(); return _world?.RawData.TotalBytesAllocated ?? 0; } }
    public long TransformSynchronizations { get; private set; }
    public long CompletedSteps { get; private set; }
    public event Action<PhysicsContact>? Contact;

    /// <summary>Disabled returns null without constructing a scene, tree, world or subscriptions.</summary>
    public static PhysicsScene? Create(PhysicsMode mode = PhysicsMode.Disabled, Scene? scene = null) =>
        mode == PhysicsMode.Disabled ? null : new PhysicsScene(mode, scene);

    public PhysicsScene(PhysicsMode mode, Scene? scene = null)
    {
        if (mode is not (PhysicsMode.QueryOnly or PhysicsMode.Simulation))
            throw new ArgumentException("Use Create for Disabled mode.", nameof(mode));
        Mode = mode; Scene = scene;
        if (mode == PhysicsMode.Simulation)
        {
            _world = new World();
            _world.BroadPhaseFilter = new PairFilter(this);
            _tree = _world.DynamicTree;
        }
        else _tree = new DynamicTree(static (_, _) => false);
        _rayFilter = Accept;
        _sweepFilter = Accept;
        if (scene != null) { scene.EntityRemoved += OnEntityRemoved; scene.Cleared += OnSceneCleared; }
    }

    public Vector3 Gravity
    {
        get { CheckSimulation(); return FromJ(_world!.Gravity); }
        set { CheckSimulation(); Finite(value); _world!.Gravity = ToJ(value); }
    }

    /// <summary>Registers one body/compound. A bound node supplies the initial pose and uniform scale.
    /// Entity is the scene member whose removal removes this registration (for a model use its instance).
    /// A node can have only one registration; put material submeshes into the same compound.</summary>
    public ColliderHandle Register(Guid ownerId, IReadOnlyList<ColliderShape> shapes, PhysicsPose? pose = null,
        BodySettings? body = null, uint layer = 1, uint mask = uint.MaxValue,
        SceneNode? node = null, object? entity = null)
    {
        Check(); ArgumentNullException.ThrowIfNull(shapes);
        if (shapes.Count == 0) throw new ArgumentException("At least one shape is required.");
        var settings = body ?? new BodySettings();
        if (!Enum.IsDefined(settings.Kind)) throw new ArgumentOutOfRangeException(nameof(body));
        Positive(settings.Mass);
        if (!float.IsFinite(settings.Friction) || settings.Friction < 0 || !float.IsFinite(settings.Restitution) || settings.Restitution is < 0 or > 1)
            throw new ArgumentException("Friction must be nonnegative and restitution between zero and one.");
        if (_world == null && settings.Kind != BodyKind.Static) throw new InvalidOperationException("Moving bodies require Simulation mode.");
        if (node != null && _bindings.ContainsKey(node)) throw new ArgumentException("This node already has a body; use compound shapes.");
        if (entity != null && (Scene == null || node == null)) throw new ArgumentException("Entity lifetime binding requires a scene and node.");
        PhysicsPose initial = Validate(pose ?? PhysicsPose.Identity);
        float scale = 1;
        if (node != null)
        {
            if (pose != null) throw new ArgumentException("A bound node supplies the pose.");
            var trs = Decompose(node.WorldMatrix); initial = trs.Pose; scale = UniformScale(trs.Scale);
        }
        var built = new List<RigidBodyShape>();
        foreach (var shape in shapes)
        {
            ArgumentNullException.ThrowIfNull(shape);
            if (shape.IsMesh && settings.Kind != BodyKind.Static) throw new ArgumentException("Triangle meshes must be static.");
            built.AddRange(shape.Create(scale));
        }
        var entry = new Entry { Handle = new(_id, ++_nextId), Owner = ownerId, Shapes = built.ToArray(),
            Settings = settings, Pose = initial, PreviousKinematic = initial, Node = node, Entity = entity,
            Scale = scale, Layer = layer, Mask = mask };
        // Keep the visual pivot independent from Jitter's center-of-mass origin.
        if (settings.Kind != BodyKind.Static)
        {
            JVector center = JVector.Zero; float total = 0;
            foreach (var shape in entry.Shapes)
            {
                shape.CalculateMassInertia(out _, out var com, out float mass);
                center += com * mass; total += mass;
            }
            Positive(total); center *= 1 / total; entry.Center = FromJ(center);
            for (int i = 0; i < entry.Shapes.Length; i++)
                entry.Shapes[i] = new TransformedShape(entry.Shapes[i], -center);
        }
        if (_world != null)
        {
            entry.Body = _world.CreateRigidBody();
            entry.Body.Tag = entry;
            entry.Body.Friction = settings.Friction; entry.Body.Restitution = settings.Restitution;
            entry.Body.AffectedByGravity = settings.AffectedByGravity;
            SetBodyPose(entry, initial);
            entry.Body.MotionType = settings.Kind switch { BodyKind.Static => MotionType.Static, BodyKind.Kinematic => MotionType.Kinematic, _ => MotionType.Dynamic };
            entry.Body.BeginCollide += OnBegin; entry.Body.EndCollide += OnEnd;
        }
        else entry.Proxies = entry.Shapes.Select(s => new QueryProxy(s)).ToArray();
        _entries.Add(entry.Handle.Id, entry);
        try
        {
            Attach(entry);
            if (node != null) { _bindings.Add(node, entry); node.WorldChanged += OnNodeChanged; }
            if (settings.Kind != BodyKind.Static && Scene != null)
                foreach (var visual in Scene.RenderObjects)
                    if (ReferenceEquals(visual.Node, node)) visual.IsStatic = false;
        }
        catch { Remove(entry.Handle); throw; }
        return entry.Handle;
    }

    private void Attach(Entry e)
    {
        if (e.Body != null)
        {
            foreach (var shape in e.Shapes) _owners.Add(shape, e);
            e.Body.AddShapes(e.Shapes, e.Settings.Kind == BodyKind.Static ? MassInertiaUpdateMode.Preserve : MassInertiaUpdateMode.Update);
            if (e.Settings.Kind != BodyKind.Static) e.Body.SetMassInertia(e.Settings.Mass);
        }
        else
            foreach (var proxy in e.Proxies)
            {
                proxy.Position = ToJ(e.Pose.Position); proxy.Rotation = ToJ(e.Pose.Rotation); proxy.UpdateWorldBoundingBox();
                _owners.Add(proxy, e); _tree.AddProxy(proxy, false);
            }
    }
    private void Detach(Entry e)
    {
        if (e.Body != null)
        {
            // Jitter's explicit removal does not raise EndCollide; close our pair state before it releases arbiters.
            foreach (var arbiter in e.Body.Contacts) OnEnd(arbiter);
            e.Body.ClearShapes(MassInertiaUpdateMode.Preserve);
            foreach (var shape in e.Shapes) _owners.Remove(shape);
        }
        else foreach (var proxy in e.Proxies) { _tree.RemoveProxy(proxy); _owners.Remove(proxy); }
    }
    public void SetEnabled(ColliderHandle handle, bool enabled)
    {
        var e = Get(handle);
        if (e.Enabled == enabled) return;
        if (enabled) { Attach(e); e.Enabled = true; }
        else { Detach(e); e.Enabled = false; }
    }
    public void SetFilter(ColliderHandle handle, uint layer, uint mask)
    {
        var e = Get(handle);
        if (e.Layer == layer && e.Mask == mask) return;
        if (e.Enabled) Detach(e);
        e.Layer = layer; e.Mask = mask;
        if (e.Enabled) Attach(e);
    }
    public void Remove(ColliderHandle handle)
    {
        var e = Get(handle);
        if (e.Node != null) { e.Node.WorldChanged -= OnNodeChanged; _bindings.Remove(e.Node); }
        _dirty.Remove(e);
        if (e.Enabled) Detach(e);
        if (e.Body != null)
        {
            _world!.Remove(e.Body);
            e.Body.BeginCollide -= OnBegin; e.Body.EndCollide -= OnEnd; e.Body.Tag = null;
        }
        _entries.Remove(handle.Id);
    }
    public PhysicsPose GetPose(ColliderHandle handle) { var e = Get(handle); Synchronize(); return e.Pose; }
    /// <summary>Game-owned static pose update. Dynamic bodies use Teleport; kinematics use SetKinematicTarget.</summary>
    public void SetPose(ColliderHandle handle, PhysicsPose pose)
    {
        var e = Get(handle);
        if (e.Settings.Kind != BodyKind.Static) throw new InvalidOperationException("Use body controls for moving bodies.");
        Teleport(handle, pose);
    }
    public void Teleport(ColliderHandle handle, PhysicsPose pose, bool clearVelocity = true)
    {
        var e = Get(handle); pose = Validate(pose);
        _dirty.Remove(e); e.Target = null; e.PreviousKinematic = pose;
        ApplyPose(e, pose);
        if (clearVelocity && e.Body != null && e.Settings.Kind != BodyKind.Static) { e.Body.Velocity = JVector.Zero; e.Body.AngularVelocity = JVector.Zero; }
        Publish(e);
    }
    public void SetKinematicTarget(ColliderHandle handle, PhysicsPose pose)
    {
        var e = Get(handle);
        if (e.Settings.Kind != BodyKind.Kinematic) throw new InvalidOperationException("A kinematic body is required.");
        pose = Validate(pose); _dirty.Remove(e); e.Target = pose; ApplyPose(e, pose); Publish(e);
    }
    public Vector3 GetVelocity(ColliderHandle handle) => FromJ(GetBody(handle).Velocity);
    public Vector3 GetAngularVelocity(ColliderHandle handle) => FromJ(GetBody(handle).AngularVelocity);
    public void SetVelocity(ColliderHandle handle, Vector3 velocity, Vector3 angularVelocity = default)
    {
        var e = Get(handle); RequireDynamic(e); Finite(velocity); Finite(angularVelocity);
        e.Body!.Velocity = ToJ(velocity); e.Body.AngularVelocity = ToJ(angularVelocity); e.Body.SetActivationState(true);
    }
    public void AddForce(ColliderHandle handle, Vector3 force)
    {
        var e = Get(handle); RequireDynamic(e); Finite(force); e.Body!.AddForce(ToJ(force));
    }
    public void ApplyImpulse(ColliderHandle handle, Vector3 impulse)
    {
        var e = Get(handle); RequireDynamic(e); Finite(impulse); e.Body!.ApplyImpulse(ToJ(impulse));
    }
    private static void RequireDynamic(Entry e)
    {
        if (e.Settings.Kind != BodyKind.Dynamic) throw new InvalidOperationException("A dynamic body is required.");
    }
    private RigidBody GetBody(ColliderHandle handle) => Get(handle).Body ?? throw new InvalidOperationException("Simulation mode is required.");

    /// <summary>Call once from Game.FixedUpdate with its elapsed simulation time. No accumulator or time scaling here.</summary>
    public void Step(float seconds)
    {
        CheckSimulation(); Positive(seconds);
        if (_notifying) throw new InvalidOperationException("Cannot recursively step from a contact handler.");
        Synchronize();
        _stepping = true;
        try
        {
            foreach (var e in _entries.Values)
            {
                if (e.Settings.Kind == BodyKind.Dynamic)
                {
                    // 2.8.11 prepares force/gravity deltas at the END of its step. Refresh the next
                    // integration delta here so game-thread AddForce, gravity and dt apply to THIS step.
                    var b = e.Body!;
                    b.Data.DeltaVelocity = (b.Force * (1 / b.Mass) + (b.AffectedByGravity ? _world!.Gravity : JVector.Zero)) * (seconds / _world!.SubstepCount);
                    b.Force = JVector.Zero;
                }
                if (e.Settings.Kind != BodyKind.Kinematic) continue;
                var target = e.Target ?? e.PreviousKinematic;
                SetBodyPose(e, e.PreviousKinematic);
                var rotation = ToJ(target.Rotation);
                var position = ToJ(target.Position) + JVector.Transform(ToJ(e.Center), rotation);
                e.Body!.Velocity = (position - e.Body.Position) * (1 / seconds);
                var delta = rotation * JQuaternion.Conjugate(e.Body.Orientation);
                if (delta.W < 0) delta = new(-delta.X, -delta.Y, -delta.Z, -delta.W);
                var axis = new JVector(delta.X, delta.Y, delta.Z);
                float length = axis.Length();
                e.Body.AngularVelocity = length < 1e-7f ? JVector.Zero : axis * (2 * MathF.Atan2(length, delta.W) / (length * seconds));
            }
            _world!.Step(seconds, false); CompletedSteps++;
            foreach (var e in _entries.Values)
            {
                if (e.Settings.Kind == BodyKind.Dynamic)
                {
                    var b = e.Body!;
                    e.Pose = new(FromJ(b.Position - JVector.Transform(ToJ(e.Center), b.Orientation)), FromJ(b.Orientation));
                    Publish(e);
                }
                else if (e.Settings.Kind == BodyKind.Kinematic)
                {
                    e.PreviousKinematic = e.Target ?? e.PreviousKinematic; e.Target = null;
                    ApplyPose(e, e.PreviousKinematic); Publish(e);
                }
            }
        }
        finally { _stepping = false; }
        DispatchContacts();
    }

    /// <summary>Flushes only changed registered node transforms, including while paused.</summary>
    public void Synchronize()
    {
        Check();
        foreach (var e in _dirty)
        {
            if (e.Settings.Kind == BodyKind.Dynamic) throw new InvalidOperationException("Dynamic node poses are physics-owned. Use Teleport.");
            var trs = Decompose(e.Node!.WorldMatrix);
            if (MathF.Abs(UniformScale(trs.Scale) - e.Scale) > 1e-5f * e.Scale) throw new InvalidOperationException("Collider scale changed; recreate the registration.");
            ApplyPose(e, trs.Pose);
            if (e.Settings.Kind == BodyKind.Kinematic) e.Target = trs.Pose;
            TransformSynchronizations++;
        }
        _dirty.Clear();
    }
    private void ApplyPose(Entry e, PhysicsPose pose)
    {
        e.Pose = pose;
        if (e.Body != null) SetBodyPose(e, pose);
        else foreach (var proxy in e.Proxies)
        {
            proxy.Position = ToJ(pose.Position); proxy.Rotation = ToJ(pose.Rotation);
            if (e.Enabled) _tree.Update(proxy);
        }
    }
    private static void SetBodyPose(Entry e, PhysicsPose pose)
    {
        var rotation = ToJ(pose.Rotation);
        var position = ToJ(pose.Position) + JVector.Transform(ToJ(e.Center), rotation);
        if (!e.Body!.Orientation.Equals(rotation)) e.Body.Orientation = rotation;
        if (!e.Body.Position.Equals(position)) e.Body.Position = position;
    }
    private void Publish(Entry e)
    {
        if (e.Node == null) return;
        if (e.Settings.Kind == BodyKind.Dynamic)
        {
            var b = e.Body!;
            e.Pose = new(FromJ(b.Position - JVector.Transform(ToJ(e.Center), b.Orientation)), FromJ(b.Orientation));
        }
        _publishingNode = e.Node;
        try { e.Node.SetWorldMatrix(Matrix4x4.CreateScale(new Vector3(e.Scale)) * e.Pose.ToMatrix()); }
        finally { _publishingNode = null; }
        // A physics-owned child keeps its own world pose when a parent is published, even while paused.
        PublishDynamicDescendants(e.Node);
    }
    private void PublishDynamicDescendants(SceneNode parent)
    {
        for (int i = 0; i < parent.Children.Count; i++)
        {
            var child = parent.Children[i];
            if (_bindings.TryGetValue(child, out var e) && e.Settings.Kind == BodyKind.Dynamic) Publish(e);
            else PublishDynamicDescendants(child);
        }
    }
    private void OnNodeChanged(SceneNode node, Matrix4x4 before, Matrix4x4 after)
    {
        if (ReferenceEquals(node, _publishingNode)) return;
        var e = _bindings[node];
        if (_publishingNode != null && e.Settings.Kind == BodyKind.Dynamic)
            for (var parent = node.Parent; parent != null; parent = parent.Parent)
                if (ReferenceEquals(parent, _publishingNode)) return;
        _dirty.Add(e);
    }
    private void OnEntityRemoved(object entity)
    {
        _removals.Clear();
        foreach (var e in _entries.Values)
            if (ReferenceEquals(e.Entity, entity)) _removals.Add(e);
        foreach (var e in _removals) Remove(e.Handle);
        _removals.Clear();
    }
    private void OnSceneCleared() => Clear();
    public void Clear()
    {
        Check();
        while (_entries.Count > 0) Remove(_entries.First().Value.Handle);
        _notifications.Clear(); _contacts.Clear(); _arbiters.Clear(); _candidates.Clear(); _overlapSeen.Clear();
    }
    public void Dispose()
    {
        if (_disposed) return;
        Check(); Clear();
        if (Scene != null) { Scene.EntityRemoved -= OnEntityRemoved; Scene.Cleared -= OnSceneCleared; }
        _world?.Dispose(); Contact = null; _disposed = true;
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Physics calls require the creating game thread.");
        if (_stepping) throw new InvalidOperationException("Physics cannot be accessed during a step.");
    }
    private void CheckSimulation() { Check(); if (_world == null) throw new InvalidOperationException("Simulation mode is required."); }
    private Entry Get(ColliderHandle handle)
    {
        Check();
        if (handle.SceneId != _id || !_entries.TryGetValue(handle.Id, out var e)) throw new ArgumentException("Foreign or removed collider handle.", nameof(handle));
        return e;
    }
    private sealed class PairFilter(PhysicsScene scene) : IBroadPhaseFilter
    {
        public bool Filter(IDynamicTreeProxy a, IDynamicTreeProxy b) =>
            scene._owners.TryGetValue(a, out var x) && scene._owners.TryGetValue(b, out var y) &&
            x.Enabled && y.Enabled && (x.Layer & y.Mask) != 0 && (y.Layer & x.Mask) != 0;
    }
    private void OnBegin(Arbiter arbiter)
    {
        if (!_arbiters.Add(arbiter)) return;
        BufferContact(arbiter, true);
    }
    private void OnEnd(Arbiter arbiter)
    {
        if (!_arbiters.Remove(arbiter)) return;
        BufferContact(arbiter, false);
    }
    private void BufferContact(Arbiter arbiter, bool begin)
    {
        var a = (Entry)arbiter.Body1.Tag!; var b = (Entry)arbiter.Body2.Tag!;
        if (a.Handle.Id > b.Handle.Id) (a, b) = (b, a);
        var key = (a.Handle.Id, b.Handle.Id);
        _contacts.TryGetValue(key, out int count);
        int next = count + (begin ? 1 : -1);
        if (next > 0) _contacts[key] = next; else _contacts.Remove(key);
        if (begin ? count == 0 : next == 0) _notifications.Add(new(a.Handle, a.Owner, b.Handle, b.Owner, begin));
    }
    private void DispatchContacts()
    {
        _dispatch.AddRange(_notifications); _notifications.Clear(); _notifying = true;
        try { foreach (var contact in _dispatch) { if (_disposed) break; Contact?.Invoke(contact); } }
        finally { _dispatch.Clear(); _notifying = false; }
    }
}
