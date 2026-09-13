# Math and geometry

Positions and lengths use game-defined world units. Use one consistent scale for graphics,
physics, and audio (one unit per meter is a useful convention). Animation and physics time
arguments are seconds; rotation angles and field-of-view arguments are radians. World axes
are +X right, +Y up, and -Z forward. Mouse coordinates instead use pixels with +Y down.

Engine transforms use row vectors: `point * matrix`. Translation lives in `M41..M43`;
`scale * rotation * translation` applies those operations in that order. A child world matrix
is `local * parentWorld`. The separate `matrix * point` operator uses column-vector semantics;
do not use it for an ordinary engine transform. Projection factories include the Vulkan Y
flip and reverse-Z depth mapping (near = 1, far = 0).

```csharp
var world = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(3, 0, -5);
var nonuniform = Matrix4x4.CreateScale(2, 3, 4);
Vector3 position = Vector3.One * world;
```

`Vector2`, `Vector3`, and `Vector4` use exact component-wise floating-point equality for
`==`, `!=`, and `Equals`. There is no tolerance: NaN is unequal to itself, equal infinities
compare equal, and positive/negative zero compare equal. Use an explicit tolerance when
comparing the results of numerical calculations. Uniform-scale overloads can make old
target-typed calls such as `CreateScale(new(2))` ambiguous; use `CreateScale(2)` or an
explicit `new Vector3(...)`.

## Geometry queries

Box/sphere intersection uses the closest point on the box. Touching a face, edge, or corner
counts as intersection. Both `box.Intersects(sphere)` and `sphere.Intersects(box)` agree.
Bounds must describe valid geometry (`Min <= Max`, nonnegative sphere radius).

A `Ray` may store any finite nonzero direction, including a non-unit direction. Each query
normalizes it without changing the stored fields; returned distances and `GetPointAt(distance)`
are in world units. `GetPointAt` requires a finite nonnegative distance. Nonfinite origins,
zero directions, and nonfinite directions throw `ArgumentException` at use, including for a
default-constructed ray. Intersections starting inside or on the surface return distance zero;
misses return `false` and distance zero. Axis-parallel rays include slab boundaries. These
rules match physics query distances and initial-overlap behavior.

## Primitive meshes

`Njulf.Assets.PrimitiveMesh` returns ordinary CPU `ModelMesh` data:

```csharp
ModelMesh box = PrimitiveMesh.Box(new Vector3(2, 4, 2), tangents: true);
ModelMesh sphere = PrimitiveMesh.Sphere(radius: 1);
ModelMesh ground = PrimitiveMesh.Plane(width: 20, depth: 20);
ModelMesh capsule = PrimitiveMesh.Capsule(radius: .5f, length: 1);
ProcessedMeshAsset processed = new ProcessedMeshAssetBuilder().Build(box);
```

All primitives are centered at the origin. Boxes use full size, spheres use radius, and
capsules run along Y with total height `length + 2 * radius`, matching `ColliderShape`.
Planes lie in XZ, face +Y, and use full width/depth; use their triangles with
`ColliderShape.TriangleMesh` for static collision. Curved meshes default to 32 radial and
16 latitude subdivisions (minimum 3 and 2). Capsule latitude subdivisions are split between
the hemispheres; zero length produces a sphere.

Factories include outward-facing triangles, normals, [0,1] UVs, bounds, and a default material.
Boxes have separate face vertices and UV squares; curved meshes duplicate the longitude seam
and poles, with V increasing bottom-to-top. Capsule V follows surface arc length. Tangent and
bitangent arrays are empty unless `tangents: true`; provided frames follow increasing U/V,
including their handedness. For packed tangent W, use the sign of
`Dot(Cross(normal, tangent), bitangent)`. No GPU resources are allocated by these factories.
