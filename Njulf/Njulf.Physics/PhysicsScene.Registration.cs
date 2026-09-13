using Njulf.Core.Scene;

namespace Njulf.Physics;

public sealed partial class PhysicsScene
{
    /// <summary>Registers shapes in the visual's node space and removes them when the visual leaves the scene.</summary>
    /// <remarks>Use WithLocalTransform(visual.MeshToNode) for mesh-space geometry. A node accepts one compound registration.</remarks>
    public ColliderHandle Register(RenderObject visual, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All)
    {
        ArgumentNullException.ThrowIfNull(visual);
        return Register(visual.Id, shapes, body: body, layer: layer, mask: mask, node: visual.Node, entity: visual);
    }

    /// <summary>Registers placement-space shapes, using PlacementRoot.Id as owner and the instance for lifetime ownership.</summary>
    public ColliderHandle Register(ModelInstance instance, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Register(instance.PlacementRoot.Id, shapes, body: body, layer: layer, mask: mask, node: instance.PlacementRoot, entity: instance);
    }

    /// <summary>Registers a single node-space shape and binds its lifetime to scene removal; requires positive uniform node scale.</summary>
    public ColliderHandle Register(RenderObject visual, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(visual, [shape], body, layer, mask);
    /// <summary>Registers a single placement-space shape, borrowing the instance and following its scene lifetime.</summary>
    public ColliderHandle Register(ModelInstance instance, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(instance, [shape], body, layer, mask);

    /// <summary>Registers scene-owned visual geometry as a static collider. Node scale must be positive and uniform; scene removal unregisters it.</summary>
    public ColliderHandle RegisterStatic(RenderObject visual, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterStatic(visual, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a static collider. Node scale must be positive and uniform; scene removal unregisters it.</summary>
    public ColliderHandle RegisterStatic(ModelInstance instance, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterStatic(instance, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a static collider. Node scale must be positive and uniform; scene removal unregisters it.</summary>
    public ColliderHandle RegisterStatic(RenderObject visual, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(visual, shapes, Kind(body, BodyKind.Static), layer, mask);
    /// <summary>Registers scene-owned visual geometry as a static collider. Node scale must be positive and uniform; scene removal unregisters it.</summary>
    public ColliderHandle RegisterStatic(ModelInstance instance, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(instance, shapes, Kind(body, BodyKind.Static), layer, mask);

    /// <summary>Registers scene-owned visual geometry as a dynamic body in a simulation world. Overrides BodySettings.Kind; requires positive uniform node scale and non-mesh shapes.</summary>
    public ColliderHandle RegisterDynamic(RenderObject visual, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterDynamic(visual, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a dynamic body in a simulation world. Overrides BodySettings.Kind; requires positive uniform node scale and non-mesh shapes.</summary>
    public ColliderHandle RegisterDynamic(ModelInstance instance, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterDynamic(instance, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a dynamic body in a simulation world. Overrides BodySettings.Kind; requires positive uniform node scale and non-mesh shapes.</summary>
    public ColliderHandle RegisterDynamic(RenderObject visual, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(visual, shapes, Kind(body, BodyKind.Dynamic), layer, mask);
    /// <summary>Registers scene-owned visual geometry as a dynamic body in a simulation world. Overrides BodySettings.Kind; requires positive uniform node scale and non-mesh shapes.</summary>
    public ColliderHandle RegisterDynamic(ModelInstance instance, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(instance, shapes, Kind(body, BodyKind.Dynamic), layer, mask);

    /// <summary>Registers scene-owned visual geometry as a game-driven body in a simulation world. Overrides BodySettings.Kind; move it with SetKinematicTarget.</summary>
    public ColliderHandle RegisterKinematic(RenderObject visual, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterKinematic(visual, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a game-driven body in a simulation world. Overrides BodySettings.Kind; move it with SetKinematicTarget.</summary>
    public ColliderHandle RegisterKinematic(ModelInstance instance, ColliderShape shape, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => RegisterKinematic(instance, [shape], body, layer, mask);
    /// <summary>Registers scene-owned visual geometry as a game-driven body in a simulation world. Overrides BodySettings.Kind; move it with SetKinematicTarget.</summary>
    public ColliderHandle RegisterKinematic(RenderObject visual, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(visual, shapes, Kind(body, BodyKind.Kinematic), layer, mask);
    /// <summary>Registers scene-owned visual geometry as a game-driven body in a simulation world. Overrides BodySettings.Kind; move it with SetKinematicTarget.</summary>
    public ColliderHandle RegisterKinematic(ModelInstance instance, IReadOnlyList<ColliderShape> shapes, BodySettings? body = null,
        uint layer = CollisionLayers.Default, uint mask = CollisionLayers.All) => Register(instance, shapes, Kind(body, BodyKind.Kinematic), layer, mask);

    private static BodySettings Kind(BodySettings? settings, BodyKind kind) => (settings ?? new BodySettings()) with { Kind = kind };
}
