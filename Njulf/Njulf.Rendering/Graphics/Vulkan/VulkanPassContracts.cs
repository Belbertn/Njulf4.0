using Njulf.Core.Math;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;

namespace Njulf.Graphics.Vulkan;

public enum VulkanPassStage { BeforeScene, AfterScene, AfterPostProcessing }
public enum VulkanPassKind { Graphics, Compute }
public enum VulkanViewImage { SceneColor, SceneDepth, Backbuffer }

/// <summary>A named, declared use. Access masks and stages describe all commands in the callback.</summary>
public abstract record VulkanResourceUse(string Name, RenderGraphResourceAccess Access,
    PipelineStageFlags2 Stages, AccessFlags2 AccessMask);

/// <summary>Specify exactly one of Texture or ViewImage. Attachment/sampled feedback is forbidden.</summary>
public sealed record VulkanImageUse(string Name, RenderGraphResourceAccess Access,
    PipelineStageFlags2 Stages, AccessFlags2 AccessMask, ImageLayout Layout,
    ITexture? Texture = null, VulkanViewImage? ViewImage = null,
    ImageLayout FinalLayout = ImageLayout.Undefined) : VulkanResourceUse(Name, Access, Stages, AccessMask);

/// <summary>Storage/transfer access to a bounded byte range. Size zero means the remaining buffer.</summary>
public sealed record VulkanBufferUse(string Name, RenderGraphResourceAccess Access,
    PipelineStageFlags2 Stages, AccessFlags2 AccessMask, GraphicsBuffer Buffer,
    ulong Offset = 0, ulong Size = 0) : VulkanResourceUse(Name, Access, Stages, AccessMask);

public sealed record VulkanPassDescription(string Name, VulkanPassKind Kind, VulkanPassStage Stage,
    IReadOnlyList<VulkanResourceUse> Resources);

/// <summary>
/// The registration owns this pass after successful registration. All callbacks run on the
/// device thread. Recording starts/ends outside rendering scopes; no submit/present/reset is allowed.
/// Dispose destroys user-owned pipelines/descriptors after all submitted GPU use completes.
/// </summary>
public interface IVulkanRenderPass : IDisposable
{
    void Initialize(VulkanDeviceInfo device);
    void ResourcesChanged(VulkanPassContext context);
    void Record(VulkanPassContext context);
}

/// <summary>Borrowed image metadata, valid only inside the current callback.</summary>
public readonly record struct VulkanPassImage(Image Image, ImageView View, Format Format, Extent2D Extent);

/// <summary>
/// Stack-scoped recording access. Only declared resources can be queried. Do not retain raw
/// handles. The graph handles inter-pass barriers; the callback handles internal dependencies
/// and must leave each image in FinalLayout (or Layout when FinalLayout is unspecified).
/// </summary>
public readonly ref struct VulkanPassContext
{
    private readonly VulkanPassAdapter _adapter;
    public VulkanDeviceInfo Device { get; }
    public CommandBuffer CommandBuffer { get; }
    public int FrameIndex { get; }
    /// <summary>Number of reusable frame slots. The current slot's submitted work has completed.</summary>
    public int FrameSlotCount => Njulf.Rendering.RenderingConstants.FramesInFlight;
    public Matrix4x4 ViewMatrix { get; }
    public Matrix4x4 ProjectionMatrix { get; }
    internal VulkanPassContext(VulkanPassAdapter adapter, CommandBuffer commandBuffer,
        Njulf.Rendering.Data.SceneRenderingData scene, int frameIndex)
    {
        _adapter = adapter; Device = adapter.DeviceInfo; CommandBuffer = commandBuffer;
        FrameIndex = frameIndex; ViewMatrix = scene.ViewMatrix; ProjectionMatrix = scene.ProjectionMatrix;
    }
    public VulkanPassImage GetImage(string name) => _adapter.GetImage(name);
    public VulkanBufferSlice GetBuffer(string name) => _adapter.GetBuffer(name);
    /// <summary>Retires replaced user-owned native objects after outstanding submitted work completes.</summary>
    public void Retire(Action release) => _adapter.Retire(release);
}

/// <summary>Disposal queues removal at the next production boundary and retires native cleanup.</summary>
public sealed class VulkanPassRegistration : IDisposable
{
    private readonly VulkanPassRegistry _owner;
    internal VulkanPassAdapter Adapter { get; }
    public bool IsDisposed { get; private set; }
    internal VulkanPassRegistration(VulkanPassRegistry owner, VulkanPassAdapter adapter)
    { _owner = owner; Adapter = adapter; }
    /// <summary>Replaces declarations at the next boundary; resources are retained before returning.</summary>
    public void Rebind(IReadOnlyList<VulkanResourceUse> resources)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _owner.Rebind(this, resources);
    }
    public void Dispose() { if (IsDisposed) return; _owner.Remove(this); IsDisposed = true; }
}
