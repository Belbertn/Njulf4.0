# Graphics resource updates

Use `GraphicsDevice` on the device thread after initialization. Existing position-only mesh,
RGBA8 texture, and uninitialized-buffer overloads retain their original behavior.

## Meshes

`VertexPositionNormalTexture` supplies authored position, normal and UV0.
`VertexPositionNormalTextureTangent` also supplies a tangent with handedness in W (+1 or -1).
Both use `Njulf.Core.Math` vectors. Missing secondary UVs are zero, color is white,
and the non-tangent overload uses the existing `(1,0,0,1)` tangent default.

```csharp
VertexPositionNormalTexture[] vertices =
[
    new(new(-1, 0, 0), Vector3.UnitZ, new(0, 0)),
    new(new(1, 0, 0), Vector3.UnitZ, new(1, 0)),
    new(new(0, 1, 0), Vector3.UnitZ, new(.5f, 1))
];
using Mesh mesh = Graphics.CreateMesh(vertices, [0u, 1u, 2u], MeshUsage.Dynamic);
vertices[2] = vertices[2] with { Position = new(0, 2, 0) };
Graphics.UpdateMeshVertices(mesh, vertices);
```

Updates replace the complete vertex stream with unchanged counts and indices. Create and
assign another mesh to change topology. All retained references observe new bounds and
scene invalidation. Dynamic meshes retain LOD0 geometry; meshlets use conservative
whole-mesh bounds and disabled normal-cone rejection. Ray tracing uses the existing dynamic
BLAS budget and refresh path. Budget-deferred geometry is excluded rather than represented
by an obsolete static BLAS.

## Textures

`Texture2DDescription` specifies width, height, format and mip count (default one).
Portable formats: R8/RG8 UNORM, RGBA8/BGRA8 UNORM and sRGB, RGBA16F, and R32F.
Unsupported device operations throw `NotSupportedException`. Dimensions and format remain
fixed. `Format` and `MipLevels` describe views; other loaded formats report `Unknown`
and do not support portable transfers.

Pass one tightly packed `ReadOnlyMemory<byte>` per mip, largest first, or only mip zero
with explicit generation:

```csharp
using Texture texture = Graphics.CreateTexture2D(
    new(4, 4, TextureFormat.Rgba8Srgb, 3),
    new ReadOnlyMemory<byte>[] { rgbaPixels }, generateMipmaps: true);
Graphics.UpdateTexture2D(texture, 0, new(1, 1, 1, 1), new byte[] { 255, 0, 0, 255 });
Graphics.GenerateMipmaps(texture);
```

Rectangle coordinates are relative to the selected mip. Supply exactly width × height ×
bytes-per-pixel, without row padding. Other pixels/mips remain intact; regeneration is explicit.
Sampler aliases share updates. Publicly created textures retain base pixels for transport
statistics. For loaded images without retained pixels, partial updates invalidate old statistics;
a full base update restores exact statistics. Content reload invalidates that pixel snapshot.

## Buffers, readback and scheduling

`CreateBuffer(size, initialData)` initializes a nonempty prefix; remaining bytes are undefined.
`UpdateBuffer(buffer, byteOffset, bytes)` replaces a nonempty range without resizing.

```csharp
using GraphicsBuffer buffer = Graphics.CreateBuffer(16, new byte[16]);
Graphics.UpdateBuffer(buffer, 4, new byte[] { 1, 2, 3, 4 });
Task<byte[]> pendingBytes = Graphics.ReadBufferAsync(buffer, 4, 4);
Task<TextureReadback> pendingImage = Graphics.ReadTexture2DAsync(texture, mipLevel: 0);
// Keep pumping frames; inspect completed tasks from a later Update callback.
```

Readback accepts buffers, textures, and initialized `RenderTarget2D` images. Texture results
contain dimensions, format, row pitch and owned native-format bytes. HDR values are preserved;
no image encoding or gamma conversion occurs.

Queue updates and readbacks outside frame recording, normally in `Load` or `Update`.
Input is copied before return. Updates execute in order before the next frame's consumers;
readbacks capture after that frame's producers, including later updates queued before that frame.
Bootstrap frames also process transfers, but an unwritten target cannot be read.
Completion follows the submission fence and requires continued frame pumping. Never block
the device thread waiting for readback. At most eight requests may be outstanding.

Disposal releases the caller's reference without invalidating pending transfers. Cancellation
settles the task without reclaiming storage still used by the GPU. Shutdown and submission
failure fault outstanding requests. Invalid ranges, data sizes, foreign/disposed resources,
and wrong-thread calls fail before recording GPU work.

The procedural example animates shared geometry and patches a mipmapped texture. The custom
example verifies uploads, compute-written buffer bytes, and RGBA16F target readback.
