using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Graphics.Vulkan;

internal sealed class VulkanPassRegistry
{
    internal readonly VulkanGraphicsDevice Graphics;
    internal readonly RenderGraph Graph;
    internal readonly SwapchainManager Swapchain;
    private readonly BindlessHeap _heap;
    private readonly List<VulkanPassAdapter> _passes = new();
    private readonly Queue<Action> _changes = new();
    private int _nextId = 0x10000;
    private bool _stopped;
    internal bool RequiresPostProcessColor => _passes.Any(p => p.Enabled && !p.RemovalRequested &&
        (p.RequestedBindings ?? p.Bindings).Any(b => b.Use is VulkanImageUse { ViewImage: VulkanViewImage.PostProcessColor }));
    internal VulkanPassRegistry(VulkanGraphicsDevice graphics, RenderGraph graph, SwapchainManager swapchain, BindlessHeap heap)
    { Graphics = graphics; Graph = graph; Swapchain = swapchain; _heap = heap; }
    internal VulkanPassRegistration Add(VulkanPassDescription description, IVulkanRenderPass pass)
    {
        Graphics.EnsureUsable();
        ObjectDisposedException.ThrowIf(_stopped, this);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(pass);
        if (string.IsNullOrWhiteSpace(description.Name))
            throw new ArgumentException("A unique pass name is required.", nameof(description));
        if (!Enum.IsDefined(description.Stage) || !Enum.IsDefined(description.Kind)) throw new ArgumentException("Unknown pass stage or kind.");
        if (_passes.Any(p => p.Name == description.Name)) throw new ArgumentException("Pass name is already registered.");
        if (_passes.Any(p => p.Owns(pass))) throw new ArgumentException("A pass instance can have only one registration.");
        var bindings = Acquire(description, description.Resources);
        var adapter = new VulkanPassAdapter(this, description, pass, bindings, _heap);
        var registration = new VulkanPassRegistration(this, adapter);
        _passes.Add(adapter);
        _changes.Enqueue(() =>
        {
            if (_stopped) { adapter.Cleanup(); return; }
            if (adapter.RemovalRequested) return;
            ValidateViewResources(bindings);
            adapter.Initialize();
            Publish(bindings);
            adapter.Attached = true;
            RefreshStage(description.Stage);
        });
        return registration;
    }
    internal static string Anchor(VulkanPassStage stage) => stage switch
    {
        VulkanPassStage.BeforeScene => "SceneOpaqueCompactionPass",
        VulkanPassStage.AfterScene => "FogPass",
        VulkanPassStage.AfterPostProcessing => "ImGuiRenderPass",
        VulkanPassStage.AfterToneMapping => "AntiAliasingPass",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
    internal void SetEnabled(VulkanPassRegistration registration, bool enabled)
    {
        Graphics.EnsureUsable();
        ObjectDisposedException.ThrowIf(registration.IsDisposed, registration);
        registration.Adapter.Enabled = enabled;
        _changes.Enqueue(() => RefreshStage(registration.Adapter.Description.Stage));
    }
    private void RefreshStage(VulkanPassStage stage)
    {
        foreach (var pass in _passes.Where(p => p.Description.Stage == stage && p.GraphAttached))
        {
            Graph.DetachCustomPass(pass);
            pass.GraphAttached = false;
        }
        foreach (var pass in _passes.Where(p => p.Description.Stage == stage && p.Attached && p.Enabled && !p.RemovalRequested))
        {
            Graph.InsertCustomPass(pass, Anchor(stage), pass.Bindings.Select(b => b.Usage).ToArray());
            pass.GraphAttached = true;
        }
    }
    internal void Rebind(VulkanPassRegistration registration, IReadOnlyList<VulkanResourceUse> resources)
        => Rebind(registration, resources, null);
    internal void Rebind(VulkanPassRegistration registration, IReadOnlyList<VulkanResourceUse> resources, VulkanPassBinding[]? retained)
    {
        Graphics.EnsureUsable();
        ObjectDisposedException.ThrowIf(_stopped, this);
        var adapter = registration.Adapter;
        var replacement = Acquire(adapter.Description, resources, retained);
        adapter.RequestedBindings = replacement;
        _changes.Enqueue(() =>
        {
            if (_stopped) { Release(replacement); return; }
            if (adapter.RemovalRequested) { Release(replacement); return; }
            try { ValidateViewResources(replacement); }
            catch { Release(replacement); throw; }
            var previous = adapter.Bindings;
            Publish(replacement);
            if (adapter.GraphAttached) Graph.ReplaceCustomPassUsages(adapter, replacement.Select(b => b.Usage).ToArray());
            adapter.Bindings = replacement;
            adapter.ResourcesDirty = true;
            Unpublish(previous);
            Graphics.QueueRelease(() => Release(previous));
        });
    }
    internal void Remove(VulkanPassRegistration registration)
    {
        if (_stopped) return;
        Graphics.EnsureReleaseAllowed();
        var adapter = registration.Adapter;
        adapter.RemovalRequested = true;
        _changes.Enqueue(() =>
        {
            if (_stopped) { adapter.Cleanup(); return; }
            Graph.DetachCustomPass(adapter);
            adapter.Attached = false;
            adapter.GraphAttached = false;
            Unpublish(adapter.Bindings);
            _passes.Remove(adapter);
            Graphics.QueueRelease(adapter.Cleanup);
        });
    }
    internal void ApplyChanges()
    {
        while (_changes.TryDequeue(out var change)) change();
    }
    internal void ShutdownAfterDeviceIdle()
    {
        _stopped = true;
        // Pending operations own retained references too; consume in original order at idle.
        ApplyChanges();
        foreach (var pass in _passes) pass.Cleanup();
        _passes.Clear();
    }
    internal void AddBindings(List<RenderGraphConcreteResourceBinding> output, IReadOnlyList<uint> families, uint graphicsFamily)
    {
        if (_passes.Count == 0) return;
        foreach (var pass in _passes)
        {
            if (!pass.GraphAttached) continue;
            foreach (var binding in pass.Bindings)
            {
                if (binding.Texture.IsValid)
                {
                    var current = Graphics.TextureResources.GetGraphImage(binding.Texture);
                    if (!ReferenceEquals(current, binding.Image))
                    {
                        binding.Image = current;
                        Graph.ReplaceCustomImageTarget(binding.Id, current);
                        pass.ResourcesDirty = true;
                    }
                }
                if (binding.Image is { } image)
                    output.Add(RenderGraphConcreteResourceBinding.ForImage(binding.Id, binding.Use.Name,
                        image.Image.Image, image.Range, image.Layout, families, graphicsFamily,
                        allocationGeneration: image.AllocationGeneration,
                        layoutTracker: image.LayoutTracker, layoutProvider: image.LayoutProvider));
            }
        }
        // Partition overlapping byte ranges once per plan generation. Exact segments then share
        // an allocation identity in the existing inter-frame and queue-handoff planners.
        foreach (var group in _passes.Where(p => p.GraphAttached).SelectMany(p => p.Bindings)
            .Where(b => b.Use is VulkanBufferUse).GroupBy(b => b.Buffer!.Handle))
        {
            var boundaries = group.SelectMany(b => new[] { ((VulkanBufferUse)b.Use).Offset,
                ((VulkanBufferUse)b.Use).Offset + b.BufferSize }).Distinct().Order().ToArray();
            foreach (var binding in group)
            {
                var use = (VulkanBufferUse)binding.Use;
                for (int i = 0; i + 1 < boundaries.Length; i++)
                {
                    ulong start = boundaries[i], end = boundaries[i + 1];
                    if (start < use.Offset || end > use.Offset + binding.BufferSize) continue;
                    output.Add(RenderGraphConcreteResourceBinding.ForBuffer(binding.Id, use.Name,
                        binding.BufferSlice.Buffer, end - start, families, graphicsFamily,
                        byteOffset: start, allocationSize: use.Buffer.SizeInBytes,
                        allocationGeneration: binding.Buffer!.Handle.Generation));
                }
            }
        }
    }
    private VulkanPassBinding[] Acquire(VulkanPassDescription description, IReadOnlyList<VulkanResourceUse> resources)
        => Acquire(description, resources, null);
    private VulkanPassBinding[] Acquire(VulkanPassDescription description, IReadOnlyList<VulkanResourceUse> resources, VulkanPassBinding[]? retained)
    {
        ArgumentNullException.ThrowIfNull(resources);
        if (resources.Count == 0) throw new ArgumentException("At least one resource must be declared.", nameof(resources));
        var result = new List<VulkanPassBinding>(resources.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var images = new HashSet<ulong>();
        var views = new HashSet<VulkanViewImage>();
        try
        {
            foreach (var use in resources)
            {
                ArgumentNullException.ThrowIfNull(use);
                if (string.IsNullOrWhiteSpace(use.Name) || !names.Add(use.Name)) throw new ArgumentException("Resource names must be unique and nonempty.");
                if (!Enum.IsDefined(use.Access) || use.Stages == PipelineStageFlags2.None || use.AccessMask == AccessFlags2.None)
                    throw new ArgumentException("Every resource needs access, stage and access masks.");
                ValidateAccess(use, description.Kind);
                var binding = new VulkanPassBinding(use);
                var retainedBinding = retained?.FirstOrDefault(b => b.Use == use);
                if (use is VulkanImageUse image)
                {
                    ValidateImage(description.Stage, image);
                    if (image.Texture is { } texture)
                    {
                        var handle = retainedBinding?.Texture.IsValid == true ? retainedBinding.Texture : Graphics.ValidateTexture(texture);
                        var state = Graphics.TextureResources.GetGraphImage(handle);
                        if (!images.Add(state.Image.Image.Handle)) throw new ArgumentException("Declare each physical image once; use explicit storage ReadWrite instead of feedback aliases.");
                        if (!state.Writable && (image.Access != RenderGraphResourceAccess.Read || image.Layout != ImageLayout.ShaderReadOnlyOptimal ||
                            image.FinalLayout is not (ImageLayout.Undefined or ImageLayout.ShaderReadOnlyOptimal)))
                            throw new ArgumentException("Loaded textures permit sampled reads only.");
                        binding.Id = (RenderGraphResourceId)checked(_nextId++);
                        Graphics.TextureResources.RetainTexture(handle);
                        binding.Texture = handle;
                        binding.Image = state;
                    }
                    else
                    {
                        if (!views.Add(image.ViewImage!.Value)) throw new ArgumentException("Declare each view image once.");
                        binding.Id = image.ViewImage switch
                        {
                            VulkanViewImage.SceneColor => RenderGraphResourceId.SceneColor,
                            VulkanViewImage.SceneDepth => RenderGraphResourceId.SceneDepth,
                            VulkanViewImage.PostProcessColor => RenderGraphResourceId.LdrSceneColor,
                            _ => RenderGraphResourceId.SwapchainColor
                        };
                    }
                }
                else if (use is VulkanBufferUse buffer)
                {
                    ArgumentNullException.ThrowIfNull(buffer.Buffer);
                    if (retainedBinding is null) ObjectDisposedException.ThrowIf(buffer.Buffer.IsDisposed, buffer.Buffer);
                    if (buffer.Buffer is not VulkanGraphicsBuffer nativeBuffer || !ReferenceEquals(nativeBuffer.Owner, Graphics)) throw new ArgumentException("Buffer belongs to another device.");
                    ulong size = buffer.Size == 0 && buffer.Offset < buffer.Buffer.SizeInBytes ? buffer.Buffer.SizeInBytes - buffer.Offset : buffer.Size;
                    if (size == 0 || buffer.Offset >= buffer.Buffer.SizeInBytes || size > buffer.Buffer.SizeInBytes - buffer.Offset)
                        throw new ArgumentOutOfRangeException(nameof(resources), "Buffer range exceeds its allocation.");
                    binding.BufferSlice = new(Graphics.Buffers.GetBuffer(nativeBuffer.Handle), buffer.Offset, size, 0);
                    binding.Id = (RenderGraphResourceId)checked(_nextId++);
                    nativeBuffer.Retain();
                    binding.Buffer = nativeBuffer;
                    binding.BufferSize = size;
                }
                else throw new ArgumentException("Unknown resource declaration.");
                result.Add(binding);
            }
            return result.ToArray();
        }
        catch { Release(result); throw; }
    }
    internal static void ValidateImage(VulkanPassStage stage, VulkanImageUse image)
    {
        if ((image.Texture == null) == (image.ViewImage == null)) throw new ArgumentException("Specify exactly one texture or view image.");
        if (image.Layout is not (ImageLayout.ShaderReadOnlyOptimal or ImageLayout.General or ImageLayout.ColorAttachmentOptimal or ImageLayout.DepthStencilReadOnlyOptimal or ImageLayout.TransferSrcOptimal or ImageLayout.TransferDstOptimal))
            throw new ArgumentException("Unsupported image layout.");
        if (image.FinalLayout is not (ImageLayout.Undefined or ImageLayout.ShaderReadOnlyOptimal or ImageLayout.General or ImageLayout.ColorAttachmentOptimal or ImageLayout.DepthStencilReadOnlyOptimal))
            throw new ArgumentException("Unsupported final image layout.");
        if (image.Layout == ImageLayout.ColorAttachmentOptimal && (image.AccessMask & (AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit)) != 0)
            throw new ArgumentException("Sampling/storage access while attached is unsupported.");
        if (image.ViewImage is { } view)
        {
            if (!Enum.IsDefined(view)) throw new ArgumentException("Unknown view image.");
            if (view == VulkanViewImage.Backbuffer)
            {
                if (stage != VulkanPassStage.AfterPostProcessing || image.Layout != ImageLayout.ColorAttachmentOptimal ||
                    image.FinalLayout is not (ImageLayout.Undefined or ImageLayout.ColorAttachmentOptimal))
                    throw new ArgumentException("Backbuffer is a color attachment available only after post processing.");
            }
            else if (view == VulkanViewImage.PostProcessColor)
            {
                if (stage != VulkanPassStage.AfterToneMapping) throw new ArgumentException("Post-process color is available only after tone mapping.");
            }
            else if (stage != VulkanPassStage.AfterScene) throw new ArgumentException("Scene color/depth are available only after scene rendering.");
            if (view == VulkanViewImage.SceneDepth && (image.Access != RenderGraphResourceAccess.Read || image.Layout != ImageLayout.DepthStencilReadOnlyOptimal ||
                image.FinalLayout is not (ImageLayout.Undefined or ImageLayout.DepthStencilReadOnlyOptimal)))
                throw new ArgumentException("Scene depth is read-only.");
        }
        else if (image.Layout == ImageLayout.DepthStencilReadOnlyOptimal || image.FinalLayout == ImageLayout.DepthStencilReadOnlyOptimal)
            throw new ArgumentException("Color textures cannot use a depth layout.");
    }

    internal void ValidateViewResources(IEnumerable<VulkanPassBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            if (binding.Use is not VulkanImageUse { ViewImage: VulkanViewImage.SceneColor or VulkanViewImage.SceneDepth or VulkanViewImage.PostProcessColor } use) continue;
            var targets = Graph.GetLayoutTrackedRenderTargets(binding.Id);
            if (targets.Count != 1) throw new InvalidOperationException($"View resource '{use.Name}' is unavailable.");
            ImageUsageFlags required = use.Layout switch
            {
                ImageLayout.General => ImageUsageFlags.StorageBit,
                ImageLayout.ColorAttachmentOptimal => ImageUsageFlags.ColorAttachmentBit,
                ImageLayout.TransferSrcOptimal => ImageUsageFlags.TransferSrcBit,
                ImageLayout.TransferDstOptimal => ImageUsageFlags.TransferDstBit,
                _ => ImageUsageFlags.SampledBit
            };
            if ((targets[0].Usage & required) == 0) throw new ArgumentException($"View resource '{use.Name}' does not support {use.Layout}.");
            binding.ViewTarget = targets[0];
        }
    }

    internal static void ValidateAccess(VulkanResourceUse use, VulkanPassKind kind)
    {
        const AccessFlags2 writes = AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderWriteBit |
            AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit |
            AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit;
        const AccessFlags2 reads = AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderReadBit | AccessFlags2.ColorAttachmentReadBit | AccessFlags2.DepthStencilAttachmentReadBit |
            AccessFlags2.TransferReadBit | AccessFlags2.MemoryReadBit;
        if ((use.AccessMask & ~(reads | writes)) != 0 ||
            (use.Access == RenderGraphResourceAccess.Read && (use.AccessMask & writes) != 0) ||
            (use.Access != RenderGraphResourceAccess.Read && (use.AccessMask & writes) == 0))
            throw new ArgumentException("Access masks do not match the declared resource access.");
        if (kind == VulkanPassKind.Compute && (use.Stages &
            (PipelineStageFlags2.AllGraphicsBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
             PipelineStageFlags2.ColorAttachmentOutputBit | PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit)) != 0)
            throw new ArgumentException("Compute passes cannot declare graphics stages.");
        if (use is VulkanBufferUse && (use.AccessMask & (AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit |
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.ShaderSampledReadBit)) != 0)
            throw new ArgumentException("Buffers support storage and transfer access only.");
        if (use is VulkanImageUse image)
        {
            AccessFlags2 allowed = image.Layout switch
            {
                ImageLayout.ColorAttachmentOptimal => AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                ImageLayout.ShaderReadOnlyOptimal => AccessFlags2.ShaderReadBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.MemoryReadBit,
                ImageLayout.DepthStencilReadOnlyOptimal => AccessFlags2.ShaderReadBit | AccessFlags2.ShaderSampledReadBit | AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.MemoryReadBit,
                ImageLayout.TransferSrcOptimal => AccessFlags2.TransferReadBit | AccessFlags2.MemoryReadBit,
                ImageLayout.TransferDstOptimal => AccessFlags2.TransferWriteBit | AccessFlags2.MemoryWriteBit,
                ImageLayout.General => AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderSampledReadBit |
                    AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit |
                    AccessFlags2.TransferWriteBit | AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                _ => AccessFlags2.None
            };
            if ((image.AccessMask & ~allowed) != 0) throw new ArgumentException("Image access masks are incompatible with its layout.");
            if (image.Layout is ImageLayout.ShaderReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal && (image.AccessMask & writes) != 0)
                throw new ArgumentException("Read-only layouts cannot declare writes.");
            if (image.Layout == ImageLayout.General && (image.AccessMask & (AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit)) != 0)
                throw new ArgumentException("General layout is reserved for storage/transfer access in this API.");
        }
    }
    private void Publish(IEnumerable<VulkanPassBinding> bindings)
    {
        foreach (var b in bindings)
        {
            if ((int)b.Id < 0x10000) continue;
            Graph.RegisterResource(new(b.Id, b.Use.Name, b.Image != null ? RenderGraphResourceKind.Image : RenderGraphResourceKind.Buffer,
                b.Image?.Image.Format, RenderGraphResourceSizePolicy.Fixed, RenderGraphResourceLifetime.Imported, true));
            if (b.Image != null) Graph.RegisterImportedImageTarget(b.Id, b.Image);
        }
    }
    private void Unpublish(IEnumerable<VulkanPassBinding> bindings)
    { foreach (var b in bindings) if ((int)b.Id >= 0x10000) Graph.RemoveCustomResource(b.Id); }
    internal void Release(IEnumerable<VulkanPassBinding> bindings)
    {
        foreach (var b in bindings)
        {
            if (b.Released) continue;
            if (b.Texture.IsValid) Graphics.TextureResources.ReleaseTexture(b.Texture);
            if (b.Buffer is { } retainedBuffer)
            {
                if (retainedBuffer.References == 1) Graphics.Buffers.DestroyBuffer(retainedBuffer.Handle);
                retainedBuffer.References--;
            }
            b.Released = true;
        }
    }
}

internal sealed class VulkanPassBinding(VulkanResourceUse use)
{
    internal readonly VulkanResourceUse Use = use;
    internal RenderGraphResourceId Id;
    internal TextureHandle Texture;
    internal TextureGraphImage? Image;
    internal ulong BufferSize;
    internal VulkanGraphicsBuffer? Buffer;
    internal bool Released;
    internal VulkanPassImage CurrentImage;
    internal VulkanBufferSlice BufferSlice;
    internal RenderTarget? ViewTarget;
    internal RenderGraphResourceUsage Usage => Use is VulkanImageUse image
        ? new(Id, image.Access, image.Stages, image.AccessMask, image.Layout, FinalImageLayout: image.FinalLayout)
        : new(Id, Use.Access, Use.Stages, Use.AccessMask);
}

internal sealed class VulkanPassAdapter : RenderPassBase
{
    private readonly VulkanPassRegistry _registry;
    private readonly IVulkanRenderPass _pass;
    private bool _cleaned;
    private bool _nativeDisposed;
    internal readonly VulkanPassDescription Description;
    internal VulkanPassBinding[] Bindings;
    internal VulkanPassBinding[]? RequestedBindings;
    internal bool Attached;
    internal bool GraphAttached;
    internal bool Enabled = true;
    internal bool RemovalRequested;
    internal bool ResourcesDirty = true;
    internal VulkanDeviceInfo DeviceInfo => new(_context.Api, _context.Instance, _context.PhysicalDevice, _context.Device);
    internal bool Owns(IVulkanRenderPass pass) => ReferenceEquals(_pass, pass);
    internal void Retire(Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        _registry.Graphics.QueueRelease(release);
    }
    internal VulkanPassAdapter(VulkanPassRegistry registry, VulkanPassDescription description, IVulkanRenderPass pass,
        VulkanPassBinding[] bindings, BindlessHeap heap) : base(description.Name, registry.Graphics.Context, registry.Swapchain, heap)
    { _registry = registry; Description = description; _pass = pass; Bindings = bindings; }
    public override void Initialize() => _pass.Initialize(DeviceInfo);
    public override void OnSwapchainRecreated()
    {
        _registry.ValidateViewResources(Bindings);
        ResourcesDirty = true;
    }
    public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Use is not VulkanImageUse image) continue;
            VulkanPassImage current;
            if (binding.Image is { } own) current = own.Image;
            else if (image.ViewImage == VulkanViewImage.Backbuffer)
                current = new(_swapchain.Images[sceneData.ImageIndex], _swapchain.ImageViews[sceneData.ImageIndex], _swapchain.SurfaceFormat, _swapchain.Extent);
            else
            {
                var target = binding.ViewTarget ?? throw new InvalidOperationException($"View resource '{image.Name}' is unavailable.");
                current = new(target.Image, target.View, target.Format, target.Extent);
            }
            // Swapchain rotation changes the image, not the pipeline format/extent contract.
            if (binding.CurrentImage.Format != current.Format || !binding.CurrentImage.Extent.Equals(current.Extent) ||
                image.ViewImage != VulkanViewImage.Backbuffer && binding.CurrentImage.Image.Handle != current.Image.Handle)
                ResourcesDirty = true;
            binding.CurrentImage = current;
        }
        var context = new VulkanPassContext(this, cmd, sceneData, frameIndex);
        if (ResourcesDirty) { _pass.ResourcesChanged(context); ResourcesDirty = false; }
        _pass.Record(context);
        _registry.Graph.CommitCustomPassLayouts(Bindings);
    }
    internal VulkanPassImage GetImage(string name)
    {
        foreach (var b in Bindings) if (b.Use.Name == name && b.Use is VulkanImageUse) return b.CurrentImage;
        throw new ArgumentException("Image was not declared by this pass.", nameof(name));
    }
    internal VulkanBufferSlice GetBuffer(string name)
    {
        foreach (var b in Bindings) if (b.Use.Name == name && b.Use is VulkanBufferUse) return b.BufferSlice;
        throw new ArgumentException("Buffer was not declared by this pass.", nameof(name));
    }
    public override void Cleanup()
    {
        if (_cleaned) return;
        if (!_nativeDisposed) { _pass.Dispose(); _nativeDisposed = true; }
        _registry.Release(Bindings);
        _cleaned = true;
    }
}
