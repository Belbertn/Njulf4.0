using System.Runtime.CompilerServices;
using Njulf.Core.Foliage;
using Njulf.Core.Interfaces;

namespace Njulf.Core.Scene;

public partial class Scene
{
    private sealed class Membership(Scene owner)
    {
        public Scene Owner { get; } = owner;
        public int Roles { get; set; } = 1;
    }

    private static readonly ConditionalWeakTable<object, Membership> Memberships = new();
    private readonly HashSet<object> _members = new(ReferenceEqualityComparer.Instance);
    private bool _detaching;
    public SceneEnvironment? Environment { get; set; }
    private readonly List<SceneLight> _lights = new();
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<SceneLight> _readOnlyLights;
    public IReadOnlyList<SceneLight> Lights => _readOnlyLights;
    public ulong LightRevision { get; private set; }

    public void Add(SceneLight light)
    {
        EnsureCanAdd(light);
        _lights.Add(light);
        light.Changed += OnLightChanged;
        LightRevision++;
    }

    public void Remove(SceneLight light)
    {
        // Scene-owned controllers remove their imported lights during teardown.
        // The light collection remains live until all owned disposers finish.
        if (!_disposeInProgress) EnsureMutable();
        if (!_lights.Remove(light)) return;
        light.Changed -= OnLightChanged;
        ReleaseMembership(light);
        LightRevision++;
    }

    private void OnLightChanged(SceneLight light) => LightRevision++;

    private readonly Dictionary<RenderObject, ModelInstance>
        _instanceChildren = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ModelInstance, List<RenderObject>>
        _instanceTransformGroups = new(ReferenceEqualityComparer.Instance);

    private readonly List<ModelInstance> _modelInstances = new();
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<ModelInstance> _readOnlyModelInstances;
    public IReadOnlyList<ModelInstance> ModelInstances => _readOnlyModelInstances;

    /// <summary>Returns the owning placement for a borrowed child, or null for a standalone/nonmember object.</summary>
    public ModelInstance? FindOwningInstance(RenderObject child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return _instanceChildren.GetValueOrDefault(child);
    }

    /// <summary>Transfers ownership of an instance and exposes its children to the renderer.</summary>
    public void Add(ModelInstance instance)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(instance);
        ObjectDisposedException.ThrowIf(instance.IsDisposed, instance);
        if (instance.AttachedScene != null)
            throw new InvalidOperationException("The instance is already attached to a scene.");
        var ids = new HashSet<Guid>();
        foreach (RenderObject child in instance.RenderObjects)
        {
            if (child.Id == Guid.Empty || FindById(child.Id) != null || !ids.Add(child.Id))
                throw new InvalidOperationException("Instance children must have unique scene IDs.");
            lock (Memberships)
                if (Memberships.TryGetValue(child, out _))
                    throw new InvalidOperationException("An instance child is already attached.");
        }

        ClaimMembership(instance);
        instance.AttachedScene = this;
        _modelInstances.Add(instance);
        AddDisposableReference(instance);
        foreach (RenderObject child in instance.RenderObjects)
        {
            _instanceChildren.Add(child, instance);
            Add(child);
        }

        AddMissingTransformGroups(instance);
    }

    private void AddMissingTransformGroups(ModelInstance instance)
    {
        var represented = new HashSet<SceneNode>(
            instance.RenderObjects.Select(child => child.Node),
            ReferenceEqualityComparer.Instance);
        var missing = new HashSet<SceneNode>(
            instance.Nodes.Where(node => !represented.Contains(node)),
            ReferenceEqualityComparer.Instance);
        if (instance.Nodes.Count > 0) missing.Add(instance.PlacementRoot);

        if (missing.Count == 0)
            return;

        var groups = new List<RenderObject>(missing.Count);
        foreach (SceneNode node in missing.OrderBy(GetNodeDepth).ThenBy(node => node.Name, StringComparer.Ordinal))
        {
            var group = new RenderObject
            {
                Name = node.Name,
                IsTransformGroup = true,
                PlacementRoot = instance.PlacementRoot,
                IsStatic = false
            };
            group.AttachNode(node, Njulf.Core.Math.Matrix4x4.Identity);
            Add(group);
            _instanceChildren.Add(group, instance);
            groups.Add(group);
        }
        _instanceTransformGroups.Add(instance, groups);
    }

    private static int GetNodeDepth(SceneNode node)
    {
        int depth = 0;
        for (SceneNode? current = node.Parent; current != null; current = current.Parent)
            depth++;
        return depth;
    }

    public void Remove(ModelInstance instance)
    {
        EnsureMutable();
        if (!_modelInstances.Contains(instance)) return;
        RenderObject[] children = _instanceChildren.Where(pair => ReferenceEquals(pair.Value, instance))
            .Select(pair => pair.Key).ToArray();
        DisposeInstance(instance); // Retain membership and borrowed children if release needs a retry.
        _ownedDisposableReferences.Remove(instance);
        foreach (RenderObject child in children)
        {
            _instanceChildren.Remove(child);
            Detach(child);
            if (child.IsTransformGroup && !instance.RenderObjects.Contains(child))
                child.Dispose();
        }

        _instanceTransformGroups.Remove(instance);
        _modelInstances.Remove(instance);
        ReleaseAllMemberships(instance);
    }

    public void Detach(ModelInstance instance)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(instance);
        if (!_modelInstances.Contains(instance)) return;
        RenderObject[] children = _instanceChildren.Where(pair => ReferenceEquals(pair.Value, instance))
            .Select(pair => pair.Key).ToArray();
        foreach (RenderObject child in children)
        {
            _instanceChildren.Remove(child);
            Detach(child);
            if (child.IsTransformGroup && !instance.RenderObjects.Contains(child))
                child.Dispose();
        }

        _instanceTransformGroups.Remove(instance);
        _ownedDisposableReferences.Remove(instance);
        _modelInstances.Remove(instance);
        instance.AttachedScene = null;
        ReleaseAllMemberships(instance);
    }

    private void DisposeInstance(ModelInstance instance)
    {
        instance.AttachedScene = null;
        try
        {
            instance.Dispose();
        }
        catch
        {
            instance.AttachedScene = this;
            throw;
        }
    }

    private void EnsureIndividualRemoval(object entity)
    {
        if (entity is RenderObject child && _instanceChildren.ContainsKey(child))
            throw new InvalidOperationException("Remove or detach the owning model instance as a group.");
    }

    private void ClaimMembership(object entity)
    {
        lock (Memberships)
        {
            if (Memberships.TryGetValue(entity, out var membership))
            {
                if (!ReferenceEquals(membership.Owner, this))
                    throw new InvalidOperationException(
                        "The entity belongs to another scene. Detach it before transferring it.");
                membership.Roles++;
            }
            else Memberships.Add(entity, new Membership(this));

            _members.Add(entity);
        }
    }

    private void ReleaseMembership(object entity)
    {
        lock (Memberships)
        {
            if (Memberships.TryGetValue(entity, out var membership) && ReferenceEquals(membership.Owner, this) &&
                --membership.Roles == 0)
            {
                Memberships.Remove(entity);
                _members.Remove(entity);
            }
        }
    }

    private void ReleaseAllMemberships(object entity)
    {
        lock (Memberships)
        {
            if (Memberships.TryGetValue(entity, out var membership) && ReferenceEquals(membership.Owner, this))
                Memberships.Remove(entity);
            _members.Remove(entity);
        }
    }

    private void ReleaseClearedMemberships()
    {
        foreach (object entity in _members.ToArray())
        {
            // Failed releases retain ownership and prevent attachment to another scene until retry.
            if (entity is IDisposable disposable && _ownedDisposableReferences.ContainsKey(disposable)) continue;
            if (entity is RenderObject child && _instanceChildren.TryGetValue(child, out var instance) &&
                _ownedDisposableReferences.ContainsKey(instance)) continue;
            ReleaseAllMemberships(entity);
        }

        foreach (var child in _instanceChildren.Where(pair => !_ownedDisposableReferences.ContainsKey(pair.Value))
                     .Select(pair => pair.Key).ToArray())
            _instanceChildren.Remove(child);
        _modelInstances.RemoveAll(instance => !_ownedDisposableReferences.ContainsKey(instance));
        foreach (ModelInstance instance in _instanceTransformGroups.Keys
                     .Where(instance => !_ownedDisposableReferences.ContainsKey(instance)).ToArray())
            _instanceTransformGroups.Remove(instance);
    }

    /// <summary>Removes rendering and updating roles without disposal, returning ownership to the caller.</summary>
    public void Detach(RenderObject entity) => Detach((object)entity);

    public void Detach(IUpdateable entity) => Detach((object)entity);
    public void Detach(ReflectionProbe entity) => Detach((object)entity);
    public void Detach(GlobalIlluminationProbeVolume entity) => Detach((object)entity);
    public void Detach(VolumetricDensityVolume entity) => Detach((object)entity);
    public void Detach(ParticleEffectInstance entity) => Detach((object)entity);
    public void Detach(StaticInstanceBatch entity) => Detach((object)entity);
    public void Detach(FoliagePrototype entity) => Detach((object)entity);
    public void Detach(FoliagePatch entity) => Detach((object)entity);
    public void Detach(SceneLight entity) => Detach((object)entity);

    private void Detach(object entity)
    {
        EnsureMutable();
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is ModelInstance instance)
        {
            Detach(instance);
            return;
        }

        EnsureIndividualRemoval(entity);
        if (!_members.Contains(entity)) return;
        _detaching = true;
        try
        {
            if (entity is IUpdateable updateable) Remove(updateable);
            switch (entity)
            {
                case RenderObject item: Remove(item); break;
                case ReflectionProbe item: Remove(item); break;
                case GlobalIlluminationProbeVolume item: Remove(item); break;
                case VolumetricDensityVolume item: Remove(item); break;
                case ParticleEffectInstance item: Remove(item); break;
                case StaticInstanceBatch item: Remove(item); break;
                case FoliagePrototype item: Remove(item); break;
                case FoliagePatch item: Remove(item); break;
                case SceneLight item: Remove(item); break;
            }
        }
        finally
        {
            _detaching = false;
        }
    }
}
