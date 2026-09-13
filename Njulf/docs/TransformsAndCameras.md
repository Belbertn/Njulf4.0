# Transforms, cameras, and model placement

`SceneNode` is the editable transform shared by render objects and model placement roots.
Use `object.LocalPosition` or `instance.PlacementRoot.LocalPosition` to move relative to
the parent; use `WorldPosition` for a scene-space position. Both also expose
`LocalRotation`, `LocalScale`, `WorldRotation`, `WorldScale`, `SetLocalTransform(position,
rotation, scale)`, and `SetWorldTransform(position, rotation, scale)`. Rotations are
quaternions. Matrices use row vectors: scale * rotation * translation, then parent world.

`RenderObject.WorldMatrix` includes baked `MeshToNode` compensation. Its new world
properties edit `Node.WorldMatrix`, excluding that compensation, so imported pivots
and placement roots behave alike.

`SetParent(parent)` preserves the world matrix. `SetParent(parent, keepWorld: false)`
preserves the local matrix. Passing null detaches the node. Cycles and world operations
requiring inversion of a singular parent fail before mutation.

Explicit position edits preserve shear. Explicit rotation/scale component access rejects
non-decomposable matrices (including shear or zero scale); whole-TRS setters intentionally
replace them. Nonuniformly scaled parents can produce local or world shear. Existing
`Position`, `Rotation`, and `Scale` names still describe local components and preserve
their legacy behavior: `RenderObject` edits replace non-TRS components, while
`SceneNode.Position` preserves the rest of its matrix.

Physics registrations keep their existing ownership rules. Dynamic bodies own their world
pose; use `PhysicsScene.Teleport` rather than editing their nodes or moving them indirectly
through authored parenting. Kinematic motion uses `SetKinematicTarget`; static/query pose
edits are synchronized through the existing physics update. Collider scale changes require
registration recreation. These conveniences still publish normal node-change notifications.

## Camera switching and controllers

```csharp
ICamera gameplay = SetActiveCamera(menuCamera);
CameraController = new OrbitCameraController(instance.PlacementRoot)
{
    Distance = 5,
    Pitch = .2f
};

// Restore gameplay. Every switch clears the previous controller.
SetActiveCamera(gameplay);
CameraController = new FollowCameraController(instance.PlacementRoot)
{
    Offset = new Vector3(0, 2, 5)
};
```

Call `Game.SetActiveCamera` on the game thread after initialization, outside drawing.
It returns the previous borrowed camera and applies the current window aspect ratio.
`Game.Camera` is the active-camera lookup; the DI `ICamera` registration remains the
startup camera. Rendering, resizing, and spatial audio use the active camera. Replacing
the camera triggers temporal-history invalidation even when its pose is unchanged.

Controllers borrow a `SceneNode` target, so either `renderObject.Node` or
`instance.PlacementRoot` works. Follow offsets and look-target offsets are world-space.
Orbit yaw/pitch use radians; zero yaw/pitch places the camera on +Z, positive yaw moves
toward +X, and positive pitch raises it. Distance must be positive; pitch is clamped
away from the poles. Controllers apply placement directly without smoothing or input
bindings. Detach a controller with `CameraController = null` for scripted camera motion.

The host calls `ICameraController.Update(ICamera, GameTime)` after physics presentation
and before spatial audio, including while paused. Custom controllers choose scaled or
unscaled time. Built-in cameras retain `LookAt` orientation across updates and projection
changes; use `Camera.Position` and `Camera.LookAt(target, up)` for scripted control.

## Load and attach a model

```csharp
ModelInstance instance = await Content.LoadModelInstanceAsync(
    Scene, "Assets/character.glb", cancellationToken: cancellationToken);
instance.PlacementRoot.LocalPosition = new Vector3(2, 0, 0);
```

The helper lives in Assets and is available through `IContentManager`, including content
scopes and level content. Optional `ContentLoadOptions` use the ordinary loading rules.
The host must pump uploads; without a dispatcher, call on the device/scene thread.

The content lifetime owns the cached template; the scene owns the returned instance.
Use `Scene.Remove(instance)` to dispose it, or `Scene.Detach(instance)` to take ownership.
Instances retain their resources independently and survive content unload. Failed attachment
cleans up the instance and partial scene membership while retaining a successfully loaded
template in its content lifetime. Cancellation before attachment prevents scene addition;
successful attachment completes the operation. If rollback also fails, both errors are
reported and cleanup remains owned for retry by the scene or content manager.

Run `Njulf.ApiExamples --example camera --frames 150 --validation` to exercise gameplay,
paused menu orbit, scripted cutscene, and restoration. Finite runs also resize the window
and verify camera cuts, aspect ratio, and spatial-listener propagation. `--example model`
shows the basic load-and-instantiate call.
