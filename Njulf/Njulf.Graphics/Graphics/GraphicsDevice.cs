using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Graphics;

/// <summary>Creates resources on the game's existing graphics device.</summary>
/// <remarks>The renderer owns this device. Call resource operations on the device thread after
/// initialization and before shutdown. Resource creation consumes supplied spans synchronously.
/// Scene objects and materials retain independent references; releasing a wrapper never destroys
/// storage still referenced by another owner or outstanding GPU work.</remarks>
public abstract partial class GraphicsDevice
{
    internal GraphicsDevice() { }
    /// <summary>Common settings and frame-boundary change receipts.</summary>
    public abstract GraphicsSettingsController Settings { get; }
    /// <summary>Hardware support, enabled features and latest actual execution.</summary>
    public abstract GraphicsCapabilities Capabilities { get; }
    /// <summary>Creates indexed triangles from scene-unit positions and zero-based indices.</summary>
    /// <param name="positions">Positions copied before return; normals and meshlets are generated.</param>
    /// <param name="indices">Triangle indices into <paramref name="positions"/>.</param>
    /// <returns>An independently owned geometry reference.</returns>
    public abstract Mesh CreateMesh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<uint> indices);
    /// <summary>Creates an owned material using the authored definition and no texture assignments.</summary>
    public abstract Material CreateMaterial(MaterialDefinition definition);
    /// <summary>Creates a material retaining its assigned textures. Callers retain ownership of input wrappers.</summary>
    public abstract Material CreateMaterial(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures);
    /// <summary>Creates one-mip RGBA8 storage from exactly width × height × 4 bytes.</summary>
    /// <remarks>Dimensions must be positive. Only Linear and Srgb are accepted; HDR input requires content loading.</remarks>
    public abstract Texture CreateTexture2D(int width, int height, ReadOnlySpan<byte> rgba8, TextureColorSpace colorSpace);
    /// <summary>Creates one-mip, one-sample linear RGBA16F storage with undefined initial contents.</summary>
    /// <remarks>Supports sampling, storage, color attachment and transfer use. Replace the target to resize it.
    /// Check capabilities first; unsupported devices throw NotSupportedException.</remarks>
    public abstract RenderTarget2D CreateRenderTarget2D(int width, int height);
    /// <summary>Creates an unmapped storage/transfer buffer of a positive size in bytes.</summary>
    public abstract GraphicsBuffer CreateBuffer(ulong sizeInBytes);
    /// <summary>Creates an independently owned object retaining same-device mesh and material references.</summary>
    /// <remarks>Attach it to a scene to transfer ownership. Disposing the input wrappers does not invalidate it.</remarks>
    public abstract RenderObject CreateRenderObject(IMesh mesh, IMaterial material);
}

/// <summary>An owned geometry reference. Scene objects retain independent references.</summary>
public abstract class Mesh : IMesh, IDisposable
{
    internal virtual event Action? ContentChanged { add { } remove { } }
    /// <summary>Whether fixed-topology vertex updates are supported.</summary>
    public virtual MeshUsage Usage => MeshUsage.Static;
    internal Mesh() { }
    /// <summary>Local-space bounds in scene units.</summary>
    public abstract BoundingBox Bounds { get; }
    /// <summary>Whether this wrapper has released its reference.</summary>
    public abstract bool IsDisposed { get; protected set; }
    /// <summary>Releases this reference on the device thread; outstanding GPU use delays reclamation.</summary>
    public abstract void Dispose();
}

/// <summary>An owned compiled material reference; scene properties expose borrowed IMaterial views.</summary>
public abstract class Material : IMaterial, IDisposable
{
    internal Material() { }
    /// <inheritdoc />
    public abstract MaterialDefinition Definition { get; }
    /// <inheritdoc />
    public abstract Texture? RetainTexture(MaterialTextureSlot slot);
    /// <inheritdoc />
    public abstract void UpdateShared(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures = default);
    internal abstract void UpdateForObject(RenderObject target, MaterialDefinition definition,
        ReadOnlySpan<MaterialTextureAssignment> textures);
    /// <summary>The current authored material name.</summary>
    public abstract string Name { get; }
    /// <summary>Whether this wrapper has released its reference.</summary>
    public abstract bool IsDisposed { get; protected set; }
    /// <summary>Releases this reference on the device thread, preserving other owners and pending GPU work.</summary>
    public abstract void Dispose();
}

/// <summary>An owned sampled texture reference. Materials retain independent dependencies.</summary>
public abstract class Texture : ITexture, IDisposable
{
    public virtual TextureFormat Format => TextureFormat.Unknown;
    public virtual int MipLevels => 1;
    internal Texture() { }
    /// <summary>Width in pixels.</summary>
    public abstract int Width { get; }
    /// <summary>Height in pixels.</summary>
    public abstract int Height { get; }
    /// <summary>Interpretation of stored color channels.</summary>
    public abstract TextureColorSpace ColorSpace { get; }
    /// <summary>Whether this wrapper has released its reference.</summary>
    public abstract bool IsDisposed { get; protected set; }
    /// <summary>Releases this reference on the device thread after outstanding GPU use.</summary>
    public abstract void Dispose();
}

/// <summary>An owned linear RGBA16F target. Materials and native passes retain independent references.</summary>
public abstract class RenderTarget2D : ITexture, IDisposable
{
    public virtual TextureFormat Format => TextureFormat.Rgba16Float;
    public virtual int MipLevels => 1;
    internal RenderTarget2D() { }
    /// <summary>Width in pixels, fixed at creation.</summary>
    public abstract int Width { get; }
    /// <summary>Height in pixels, fixed at creation.</summary>
    public abstract int Height { get; }
    /// <summary>Linear scene color.</summary>
    public abstract TextureColorSpace ColorSpace { get; }
    /// <summary>Whether this wrapper has released its reference.</summary>
    public abstract bool IsDisposed { get; protected set; }
    /// <summary>Releases the reference on the device thread; retained uses and GPU completion delay reclamation.</summary>
    public abstract void Dispose();
}

/// <summary>An owned storage/transfer buffer retained independently by registered native passes.</summary>
public abstract class GraphicsBuffer : IDisposable
{
    internal GraphicsBuffer() { }
    /// <summary>Allocation size in bytes, fixed at creation.</summary>
    public abstract ulong SizeInBytes { get; }
    /// <summary>Whether this wrapper has released its reference.</summary>
    public abstract bool IsDisposed { get; protected set; }
    /// <summary>Releases this reference on the device thread after outstanding GPU use.</summary>
    public abstract void Dispose();
}

/// <summary>Device-thread controller for common settings. One change may be pending at a time.</summary>
public abstract class GraphicsSettingsController
{
    internal GraphicsSettingsController() { }
    /// <summary>Currently applied settings, distinct from active hardware features.</summary>
    public abstract GraphicsSettingsSnapshot Current { get; }
    /// <summary>The pending request's values, or Current when no request is pending.</summary>
    public abstract GraphicsSettingsSnapshot Requested { get; }
    /// <summary>Whether an application request is awaiting completion.</summary>
    public abstract bool IsPending { get; }
    /// <summary>Classifies a change without applying it. Presets precede explicit field overrides.</summary>
    public abstract GraphicsSettingsResult Preview(GraphicsSettingsChange change);
    /// <summary>Queues a change and completes after its frame-boundary resource preparation.</summary>
    /// <remarks>Cancellation is honored until processing begins. A busy controller rejects another request.
    /// Unsupported and restart-required changes report a result without partially applying the request.</remarks>
    public abstract Task<GraphicsSettingsResult> ApplyAsync(GraphicsSettingsChange change, CancellationToken cancellationToken = default);
}
