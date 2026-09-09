using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Graphics;
using Njulf.Graphics.Vulkan;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.ApiExamples;

internal sealed class CustomRenderingExample(ExampleOptions options) : ExampleGame(options)
{
    private RenderTarget2D _target = null!;
    private GraphicsBuffer _buffer = null!;
    private VulkanPassRegistration _compute = null!, _composite = null!, _overlay = null!;
    private int _updates;
    private Task<GraphicsSettingsResult>? _scaleChange;
    protected override void Load()
    {
        base.Load();
        _target = Graphics.CreateRenderTarget2D(128, 128);
        _buffer = Graphics.CreateBuffer(16);
        _compute = Graphics.AddVulkanPass(new("Custom.Gradient", VulkanPassKind.Compute, VulkanPassStage.BeforeScene, ComputeResources()), new ExampleNativePass(true));
        _composite = Graphics.AddVulkanPass(new("Custom.Composite", VulkanPassKind.Graphics, VulkanPassStage.AfterScene, CompositeResources()), new ExampleNativePass(false));
        _overlay = Graphics.AddVulkanPass(new("Custom.Overlay", VulkanPassKind.Graphics, VulkanPassStage.AfterPostProcessing,
            [new VulkanImageUse("destination", RenderGraphResourceAccess.Write, PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentWriteBit, ImageLayout.ColorAttachmentOptimal, ViewImage: VulkanViewImage.Backbuffer)]), new ExampleOverlayPass());
        using var mesh = Graphics.CreateMesh(
            [new Vector3(0, 1, 0), new Vector3(-1, -1, 1), new Vector3(1, -1, 1)], [0u, 1u, 2u]);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default,
            [new(MaterialTextureSlot.BaseColor, _target)]);
        Scene.Add(Graphics.CreateRenderObject(mesh, material));
    }
    private VulkanResourceUse[] ComputeResources() =>
    [
        new VulkanImageUse("destination", RenderGraphResourceAccess.Write, PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit, ImageLayout.General, Texture: _target),
        new VulkanBufferUse("data", RenderGraphResourceAccess.Write, PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit, _buffer, Size: 4)
    ];
    private VulkanResourceUse[] CompositeResources() =>
    [
        new VulkanImageUse("source", RenderGraphResourceAccess.Read, PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderSampledReadBit, ImageLayout.ShaderReadOnlyOptimal, Texture: _target),
        new VulkanImageUse("destination", RenderGraphResourceAccess.ReadWrite, PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit, ImageLayout.ColorAttachmentOptimal, ViewImage: VulkanViewImage.SceneColor),
        new VulkanBufferUse("data", RenderGraphResourceAccess.Read, PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageReadBit, _buffer, Size: 4)
    ];
    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (QualityFrames >= 240 && _scaleChange == null)
            _scaleChange = Graphics.Settings.ApplyAsync(new() { ResolutionScale = .75f });
        if (_scaleChange?.IsCompletedSuccessfully == true && _scaleChange.Result.Outcome != GraphicsSettingsOutcome.Rebuilt)
            throw new InvalidOperationException($"Scene resolution change failed: {_scaleChange.Result}");
        if (QualityFrames < 180 || _updates++ != 0) return;
        // Replace targets and rebind while earlier frames are in flight. The scene material
        // retains the original target; pass registrations retain the replacement.
        var previous = _target;
        _target = Graphics.CreateRenderTarget2D(192, 96);
        _compute.Rebind(ComputeResources());
        _composite.Rebind(CompositeResources());
        previous.Dispose();
    }
    protected override void Unload()
    {
        _overlay?.Dispose(); _composite?.Dispose(); _compute?.Dispose();
        _target?.Dispose(); _buffer?.Dispose();
        base.Unload();
    }
}

// Example-local Vulkan setup. Descriptor sets are separated by frame slot so updating
// the current slot never overwrites descriptors referenced by submitted work.
internal sealed unsafe class ExampleNativePass(bool compute) : IVulkanRenderPass
{
    private VulkanDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private DescriptorPool _pool;
    private DescriptorSet[] _sets = [];
    private Sampler _sampler;
    private VkPipeline _pipeline;
    public void Initialize(VulkanDeviceInfo device)
    {
        _device = device;
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new(0, compute ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler, 1,
            compute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.FragmentBit);
        bindings[1] = new(1, DescriptorType.StorageBuffer, 1, compute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.FragmentBit);
        var setInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        Check(device.Api.CreateDescriptorSetLayout(device.Device, &setInfo, null, out _setLayout));
        var setLayout = _setLayout;
        var layoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        Check(device.Api.CreatePipelineLayout(device.Device, &layoutInfo, null, out _layout));
        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new(compute ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler, 32);
        sizes[1] = new(DescriptorType.StorageBuffer, 32);
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 32, PoolSizeCount = 2, PPoolSizes = sizes };
        Check(device.Api.CreateDescriptorPool(device.Device, &poolInfo, null, out _pool));
        if (!compute)
        {
            var samplerInfo = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear,
                MinFilter = Filter.Linear, AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge, MaxLod = 0 };
            Check(device.Api.CreateSampler(device.Device, &samplerInfo, null, out _sampler));
        }
    }
    public void ResourcesChanged(VulkanPassContext context)
    {
        if (_sets.Length == 0)
        {
            _sets = new DescriptorSet[context.FrameSlotCount];
            for (int i = 0; i < _sets.Length; i++)
            {
                var layout = _setLayout;
                var allocation = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &layout };
                Check(_device.Api.AllocateDescriptorSets(_device.Device, &allocation, out _sets[i]));
            }
        }
        if (_pipeline.Handle == 0) CreatePipeline(context.GetImage("destination").Format);
    }
    public void Record(VulkanPassContext context)
    {
        var api = _device.Api;
        var source = context.GetImage(compute ? "destination" : "source");
        var buffer = context.GetBuffer("data");
        var imageInfo = new DescriptorImageInfo(_sampler, source.View, compute ? ImageLayout.General : ImageLayout.ShaderReadOnlyOptimal);
        var bufferInfo = new DescriptorBufferInfo(buffer.Buffer, buffer.Offset, buffer.Length);
        var set = _sets[context.FrameIndex];
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorCount = 1,
            DescriptorType = compute ? DescriptorType.StorageImage : DescriptorType.CombinedImageSampler, PImageInfo = &imageInfo };
        writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &bufferInfo };
        api.UpdateDescriptorSets(_device.Device, 2, writes, 0, null);
        var point = compute ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics;
        api.CmdBindPipeline(context.CommandBuffer, point, _pipeline);
        api.CmdBindDescriptorSets(context.CommandBuffer, point, _layout, 0, 1, &set, 0, null);
        if (compute) api.CmdDispatch(context.CommandBuffer, (source.Extent.Width + 7) / 8, (source.Extent.Height + 7) / 8, 1);
        else
        {
            var destination = context.GetImage("destination");
            BeginRendering(context, destination);
            api.CmdDraw(context.CommandBuffer, 3, 1, 0, 0);
            api.CmdEndRendering(context.CommandBuffer);
        }
    }
    internal static void BeginRendering(VulkanPassContext context, VulkanPassImage destination)
    {
        var color = new RenderingAttachmentInfo { SType = StructureType.RenderingAttachmentInfo, ImageView = destination.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal, LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store };
        var info = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = new(default, destination.Extent),
            LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &color };
        context.Device.Api.CmdBeginRendering(context.CommandBuffer, &info);
        var viewport = new Viewport(0, 0, destination.Extent.Width, destination.Extent.Height, 0, 1);
        var scissor = new Rect2D(default, destination.Extent);
        context.Device.Api.CmdSetViewport(context.CommandBuffer, 0, 1, &viewport);
        context.Device.Api.CmdSetScissor(context.CommandBuffer, 0, 1, &scissor);
    }
    private void CreatePipeline(Format format)
    {
        ShaderModule first = default, fragment = default;
        try
        {
            first = LoadShader(compute ? "api_example.comp" : "api_example.vert");
            byte* entry = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
            var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = compute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.VertexBit, Module = first, PName = entry };
            if (compute)
            {
                var info = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _layout };
                Check(_device.Api.CreateComputePipelines(_device.Device, default, 1, &info, null, out _pipeline));
                return;
            }
            fragment = LoadShader("api_example.frag");
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = stage;
            stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = entry };
            var vertex = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var input = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var attachment = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &attachment };
            var dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamics };
            var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &format };
            var graphics = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, PNext = &rendering,
                StageCount = 2, PStages = stages, PVertexInputState = &vertex, PInputAssemblyState = &input, PViewportState = &viewport,
                PRasterizationState = &raster, PMultisampleState = &multisample, PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout };
            Check(_device.Api.CreateGraphicsPipelines(_device.Device, default, 1, &graphics, null, out _pipeline));
        }
        finally
        {
            if (first.Handle != 0) _device.Api.DestroyShaderModule(_device.Device, first, null);
            if (fragment.Handle != 0) _device.Api.DestroyShaderModule(_device.Device, fragment, null);
        }
    }
    private ShaderModule LoadShader(string name)
    {
        using var stream = typeof(Njulf.Shaders.ShaderLibrary).Assembly.GetManifestResourceStream("Njulf.Shaders." + name)
            ?? throw new FileNotFoundException(name);
        var code = new byte[stream.Length];
        stream.ReadExactly(code);
        fixed (byte* bytes = code)
        {
            var info = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)bytes };
            Check(_device.Api.CreateShaderModule(_device.Device, &info, null, out var module));
            return module;
        }
    }
    private static void Check(Result result) { if (result != Result.Success) throw new InvalidOperationException($"Example Vulkan operation failed: {result}"); }
    public void Dispose()
    {
        if (_pipeline.Handle != 0) { _device.Api.DestroyPipeline(_device.Device, _pipeline, null); _pipeline = default; }
        if (_pool.Handle != 0) { _device.Api.DestroyDescriptorPool(_device.Device, _pool, null); _pool = default; }
        if (_sampler.Handle != 0) { _device.Api.DestroySampler(_device.Device, _sampler, null); _sampler = default; }
        if (_layout.Handle != 0) { _device.Api.DestroyPipelineLayout(_device.Device, _layout, null); _layout = default; }
        if (_setLayout.Handle != 0) { _device.Api.DestroyDescriptorSetLayout(_device.Device, _setLayout, null); _setLayout = default; }
    }
}

internal sealed unsafe class ExampleOverlayPass : IVulkanRenderPass
{
    public void Initialize(VulkanDeviceInfo device) { }
    public void ResourcesChanged(VulkanPassContext context) { }
    public void Record(VulkanPassContext context)
    {
        var image = context.GetImage("destination");
        ExampleNativePass.BeginRendering(context, image);
        var clear = new ClearAttachment { AspectMask = ImageAspectFlags.ColorBit, ColorAttachment = 0,
            ClearValue = new ClearValue { Color = new ClearColorValue(0, 0.8f, 0.2f, 1) } };
        var rect = new ClearRect { Rect = new(new(12, 12), new(120, 12)), LayerCount = 1 };
        context.Device.Api.CmdClearAttachments(context.CommandBuffer, 1, &clear, 1, &rect);
        context.Device.Api.CmdEndRendering(context.CommandBuffer);
    }
    public void Dispose() { }
}
