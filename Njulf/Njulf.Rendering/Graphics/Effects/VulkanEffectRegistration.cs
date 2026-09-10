using Njulf.Graphics.Vulkan;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Silk.NET.Vulkan;

namespace Njulf.Graphics;

internal sealed class VulkanEffectRegistration : EffectRegistration
{
    private readonly VulkanGraphicsDevice _graphics;
    private readonly EffectStage _stage;
    private readonly bool _post;
    private EffectImage? _destination;
    private readonly byte[] _parameters;
    private readonly VulkanEffectPass _pass;
    private readonly VulkanPassRegistration _native;
    private VulkanPassRegistration? _copy;
    private RenderTarget2D? _scratch;
    private EffectResourceBinding[] _bindings;
    private bool _enabled = true, _disposed;
    public override ShaderEffectAsset Asset { get; }
    public override bool IsDisposed => _disposed;
    internal bool IsPost => _post;
    public override bool Enabled
    {
        get => _enabled;
        set
        {
            Check();
            if (_enabled == value) return;
            _native.SetEnabled(value); _copy?.SetEnabled(value);
            _enabled = value; Changed();
        }
    }

    internal VulkanEffectRegistration(VulkanGraphicsDevice graphics, string name, ShaderEffectAsset asset,
        EffectStage stage, IReadOnlyList<EffectResourceBinding> bindings, EffectImage? destination,
        EffectDispatchSize dispatch, bool post)
    {
        _graphics = graphics; Asset = asset; _stage = stage; _post = post; _destination = destination;
        _bindings = bindings.ToArray(); _parameters = asset.DefaultBytes.ToArray();
        _pass = new(graphics, asset, _parameters) { DispatchSize = dispatch };
        VulkanPassRegistration? added = null;
        try
        {
            if (post)
            {
                ValidatePostAsset(asset);
                var extent = PostExtent();
                _scratch = graphics.CreateRenderTarget2D((int)extent.Width, (int)extent.Height);
                _pass.DispatchSize = new(extent.Width, extent.Height);
            }
            var uses = BuildUses(_bindings, _scratch);
            added = graphics.CustomPasses.Add(new(name,
                asset.Kind == ShaderEffectKind.Compute ? VulkanPassKind.Compute : VulkanPassKind.Graphics,
                (VulkanPassStage)stage, uses), _pass);
            _native = added;
            if (post)
            {
                var copyAsset = new ShaderEffectAsset("Njulf.Effects.Copy", ShaderEffectKind.Fullscreen,
                    ShaderModuleLoader.LoadBytes("effect_copy.frag.spv"), resources:
                    [new("SourceColor", 0, EffectResourceKind.SampledTexture2D)]);
                _copy = graphics.CustomPasses.Add(new(name + ".$copy", VulkanPassKind.Graphics, VulkanPassStage.AfterToneMapping,
                    CopyUses(_scratch!)), new VulkanEffectPass(graphics, copyAsset, []));
            }
            Changed();
        }
        catch
        {
            added?.Dispose();
            _scratch?.Dispose();
            throw;
        }
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _graphics.EnsureUsable();
    }
    private void Changed()
    {
        if (_stage == EffectStage.AfterToneMapping) _graphics.PostEffectRevision++;
    }
    public override void SetParameter<T>(string name, T value)
    {
        Check();
        int index = Asset.ParameterIndex(name);
        Span<byte> next = stackalloc byte[16];
        ShaderEffectAsset.WriteParameter(Asset.Parameters[index], value, next);
        var current = _parameters.AsSpan(index * 16, 16);
        if (next.SequenceEqual(current)) return;
        next.CopyTo(current);
        if (_enabled) Changed();
    }
    public override void Rebind(IReadOnlyList<EffectResourceBinding> bindings)
        => RebindCore(bindings, null, null);
    public override void Rebind(IReadOnlyList<EffectResourceBinding> bindings, EffectImage destination)
    {
        Check();
        ArgumentNullException.ThrowIfNull(destination);
        if (_post || Asset.Kind != ShaderEffectKind.Fullscreen) throw new NotSupportedException("Only ordinary fullscreen effects accept a destination override.");
        if (destination.Texture is { IsDisposed: true } texture) throw new ObjectDisposedException(texture.GetType().Name);
        RebindCore(bindings, destination, null);
    }
    public override void Rebind(IReadOnlyList<EffectResourceBinding> bindings, EffectDispatchSize dispatchSize)
    {
        Check();
        if (_post || Asset.Kind != ShaderEffectKind.Compute) throw new NotSupportedException("Only ordinary compute effects accept a dispatch override.");
        RebindCore(bindings, null, dispatchSize);
    }
    private void RebindCore(IReadOnlyList<EffectResourceBinding> bindings, EffectImage? destination, EffectDispatchSize? dispatchSize)
    {
        Check();
        ArgumentNullException.ThrowIfNull(bindings);
        var replacement = bindings.ToArray();
        var uses = BuildUses(replacement, _scratch, destination, dispatchSize);
        _native.RebindKeepingOutput(uses); // Acquire/validation succeeds before changing public state.
        _bindings = replacement;
        if (destination is not null) _destination = destination;
        if (dispatchSize is { } dispatch) _pass.DispatchSize = dispatch;
        if (_enabled) Changed();
    }
    internal void Prepare()
    {
        if (!_post || _disposed || !_enabled) return;
        var extent = PostExtent();
        if (_scratch!.Width == extent.Width && _scratch.Height == extent.Height) return;
        var replacement = _graphics.CreateRenderTarget2D((int)extent.Width, (int)extent.Height);
        try
        {
            var uses = BuildUses(_bindings, replacement);
            _native.RebindRetained(uses);
            _copy!.Rebind(CopyUses(replacement));
        }
        catch { replacement.Dispose(); throw; }
        var previous = _scratch;
        _scratch = replacement;
        _pass.DispatchSize = new(extent.Width, extent.Height);
        previous.Dispose();
        Changed();
    }
    private Extent2D PostExtent() => _graphics.CustomPasses.Graph.GetLayoutTrackedRenderTargets(RenderGraphResourceId.LdrSceneColor).Single().Extent;
    private static void ValidatePostAsset(ShaderEffectAsset asset)
    {
        if (!asset.Resources.Any(r => r.Name == "SourceColor" && r.Kind == EffectResourceKind.SampledTexture2D))
            throw new ArgumentException("Post effects require a sampled SourceColor binding.");
        if (asset.Kind == ShaderEffectKind.Compute && !asset.Resources.Any(r => r.Name == "Destination" &&
                r.Kind == EffectResourceKind.StorageImage2D && r.Access == EffectResourceAccess.Write))
            throw new ArgumentException("Compute post effects require a write-only rgba16f Destination binding.");
    }
    private static VulkanResourceUse[] CopyUses(RenderTarget2D scratch) =>
    [
        new VulkanImageUse("SourceColor", RenderGraphResourceAccess.Read, PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit, ImageLayout.ShaderReadOnlyOptimal, Texture: scratch),
        new VulkanImageUse("$destination", RenderGraphResourceAccess.Write, PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit, ImageLayout.ColorAttachmentOptimal, ViewImage: VulkanViewImage.PostProcessColor)
    ];
    private unsafe VulkanResourceUse[] BuildUses(EffectResourceBinding[] supplied, RenderTarget2D? scratch,
        EffectImage? destination = null, EffectDispatchSize? dispatchSize = null)
    {
        if (!Enum.IsDefined(_stage)) throw new ArgumentException("Unknown effect stage.");
        _graphics.Context.Api.GetPhysicalDeviceProperties(_graphics.Context.PhysicalDevice, out var properties);
        var limits = properties.Limits;
        bool compute = Asset.Kind == ShaderEffectKind.Compute;
        if (compute)
        {
            var local = Asset.WorkgroupSize;
            var dispatch = scratch is null ? dispatchSize ?? _pass.DispatchSize : new EffectDispatchSize((uint)scratch.Width, (uint)scratch.Height);
            if (local.X > limits.MaxComputeWorkGroupSize[0] || local.Y > limits.MaxComputeWorkGroupSize[1] ||
                local.Z > limits.MaxComputeWorkGroupSize[2] || (ulong)local.X * local.Y * local.Z > limits.MaxComputeWorkGroupInvocations)
                throw new NotSupportedException("Effect workgroup size exceeds device limits.");
            if (dispatch.X == 0 || dispatch.Y == 0 || dispatch.Z == 0 ||
                VulkanEffectPass.Groups(dispatch.X, local.X) > limits.MaxComputeWorkGroupCount[0] ||
                VulkanEffectPass.Groups(dispatch.Y, local.Y) > limits.MaxComputeWorkGroupCount[1] ||
                VulkanEffectPass.Groups(dispatch.Z, local.Z) > limits.MaxComputeWorkGroupCount[2])
                throw new ArgumentOutOfRangeException(nameof(supplied), "Effect dispatch exceeds device limits.");
        }
        var map = new Dictionary<string, EffectResourceBinding>(StringComparer.Ordinal);
        foreach (var binding in supplied)
        {
            ArgumentNullException.ThrowIfNull(binding);
            if (string.IsNullOrWhiteSpace(binding.Name) || !map.TryAdd(binding.Name, binding)) throw new ArgumentException("Binding names must be unique and nonempty.");
        }
        if (_post)
        {
            if (map.ContainsKey("SourceColor") || compute && map.ContainsKey("Destination"))
                throw new ArgumentException("Post-effect input/output bindings are automatic.");
            map.Add("SourceColor", new("SourceColor", ViewImage: EffectViewImage.PostProcessColor));
            if (compute) map.Add("Destination", new("Destination", Texture: scratch));
        }
        if (map.Count != Asset.Resources.Count) throw new ArgumentException("Bindings must match the asset's declared resources exactly.");
        var result = new List<VulkanResourceUse>();
        foreach (var resource in Asset.Resources)
        {
            if (!map.TryGetValue(resource.Name, out var binding)) throw new ArgumentException($"Missing effect binding '{resource.Name}'.");
            if ((binding.Texture is null ? 0 : 1) + (binding.ViewImage is null ? 0 : 1) + (binding.Buffer is null ? 0 : 1) != 1)
                throw new ArgumentException($"Binding '{resource.Name}' needs exactly one resource.");
            var access = resource.Access switch { EffectResourceAccess.Read => RenderGraphResourceAccess.Read,
                EffectResourceAccess.Write => RenderGraphResourceAccess.Write, _ => RenderGraphResourceAccess.ReadWrite };
            var stages = compute ? PipelineStageFlags2.ComputeShaderBit : PipelineStageFlags2.FragmentShaderBit;
            var mask = resource.Access == EffectResourceAccess.Read ? AccessFlags2.ShaderStorageReadBit :
                resource.Access == EffectResourceAccess.Write ? AccessFlags2.ShaderStorageWriteBit : AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit;
            if (resource.Kind == EffectResourceKind.StorageBuffer)
            {
                if (binding.Buffer is null) throw new ArgumentException($"'{resource.Name}' requires a storage buffer.");
                ulong length = binding.Size == 0 && binding.Offset < binding.Buffer.SizeInBytes ? binding.Buffer.SizeInBytes - binding.Offset : binding.Size;
                if (binding.Offset % limits.MinStorageBufferOffsetAlignment != 0 || length > limits.MaxStorageBufferRange)
                    throw new ArgumentException($"'{resource.Name}' violates storage-buffer device limits.");
                result.Add(new VulkanBufferUse(resource.Name, access, stages, mask, binding.Buffer, binding.Offset, binding.Size));
            }
            else
            {
                if (binding.Buffer is not null) throw new ArgumentException($"'{resource.Name}' requires an image.");
                if (binding.Offset != 0 || binding.Size != 0) throw new ArgumentException("Image bindings cannot specify buffer ranges.");
                bool sampled = resource.Kind == EffectResourceKind.SampledTexture2D;
                if (!sampled && binding.Texture is not RenderTarget2D { Format: TextureFormat.Rgba16Float })
                    throw new ArgumentException("Storage images require an RGBA16F render target.");
                var layout = sampled ? binding.ViewImage == EffectViewImage.SceneDepth ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.ShaderReadOnlyOptimal : ImageLayout.General;
                result.Add(new VulkanImageUse(resource.Name, access, stages, sampled ? AccessFlags2.ShaderSampledReadBit : mask,
                    layout, binding.Texture, binding.ViewImage is { } view ? (VulkanViewImage)view : null));
            }
        }
        if (!compute)
        {
            var output = _post ? new EffectImage(Texture: scratch) : destination ?? _destination!;
            if (output is null || (output.Texture is null) == (output.ViewImage is null)) throw new ArgumentException("Fullscreen output requires exactly one image.");
            result.Add(new VulkanImageUse("$destination", RenderGraphResourceAccess.Write, PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit, ImageLayout.ColorAttachmentOptimal, output.Texture,
                output.ViewImage is { } view ? (VulkanViewImage)view : null));
        }
        // Validate declared image capability before staging any changes. Native Acquire checks
        // ownership, physical aliases and retention; this also catches view-layout mistakes early.
        foreach (var use in result)
        {
            VulkanPassRegistry.ValidateAccess(use, compute ? VulkanPassKind.Compute : VulkanPassKind.Graphics);
            if (use is not VulkanImageUse image) continue;
            VulkanPassRegistry.ValidateImage((VulkanPassStage)_stage, image);
            if (image.ViewImage is VulkanViewImage.SceneColor or VulkanViewImage.SceneDepth or VulkanViewImage.PostProcessColor)
            {
                var id = image.ViewImage == VulkanViewImage.SceneColor ? RenderGraphResourceId.SceneColor :
                    image.ViewImage == VulkanViewImage.SceneDepth ? RenderGraphResourceId.SceneDepth : RenderGraphResourceId.LdrSceneColor;
                var target = _graphics.CustomPasses.Graph.GetLayoutTrackedRenderTargets(id).Single();
                var required = image.Layout == ImageLayout.ColorAttachmentOptimal ? ImageUsageFlags.ColorAttachmentBit :
                    image.Layout == ImageLayout.General ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit;
                if ((target.Usage & required) == 0) throw new ArgumentException($"Effect image '{image.Name}' does not support its declared usage.");
            }
        }
        return result.ToArray();
    }
    public override void Dispose()
    {
        if (_disposed) return;
        _graphics.EnsureReleaseAllowed();
        _native.Dispose(); _copy?.Dispose(); _scratch?.Dispose();
        _disposed = true;
        if (_enabled) Changed();
        _graphics.RemoveEffect(this);
    }
    internal void ShutdownAfterDeviceIdle()
    {
        if (_disposed) return;
        if (_scratch is VulkanRenderTarget2D target) _graphics.TextureResources.ReleaseTexture(target.Handle);
        _scratch = null;
        _disposed = true;
    }
}
