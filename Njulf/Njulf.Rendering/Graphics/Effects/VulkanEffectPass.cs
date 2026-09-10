using Njulf.Graphics.Vulkan;
using Njulf.Rendering;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Graphics;

/// <summary>Pass-local pipelines and frame-slot descriptors; the registry owns all bound resources.</summary>
internal sealed unsafe class VulkanEffectPass(VulkanGraphicsDevice graphics, ShaderEffectAsset asset, byte[] parameters) : IVulkanRenderPass
{
    private VulkanDeviceInfo _device;
    private DescriptorSetLayout _setLayout;
    private PipelineLayout _layout;
    private DescriptorPool _pool;
    private DescriptorSet[] _sets = [];
    private Sampler _sampler;
    private VkPipeline _pipeline;
    private Format _format;
    internal EffectDispatchSize DispatchSize;
    private bool Compute => asset.Kind == ShaderEffectKind.Compute;
    private ShaderStageFlags Stage => Compute ? ShaderStageFlags.ComputeBit : ShaderStageFlags.FragmentBit;
    private static DescriptorType Descriptor(EffectResourceKind kind) => kind switch
    {
        EffectResourceKind.SampledTexture2D => DescriptorType.CombinedImageSampler,
        EffectResourceKind.StorageImage2D => DescriptorType.StorageImage,
        _ => DescriptorType.StorageBuffer
    };
    public void Initialize(VulkanDeviceInfo device)
    {
        _device = device;
        var bindings = new DescriptorSetLayoutBinding[asset.Resources.Count];
        for (int i = 0; i < bindings.Length; i++)
            bindings[i] = new(asset.Resources[i].Binding, Descriptor(asset.Resources[i].Kind), 1, Stage);
        fixed (DescriptorSetLayoutBinding* pointer = bindings)
        {
            var info = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length, PBindings = pointer };
            Check(device.Api.CreateDescriptorSetLayout(device.Device, &info, null, out _setLayout));
        }
        var setLayout = _setLayout;
        var push = new PushConstantRange(Stage, 0, (uint)parameters.Length);
        var layout = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1, PSetLayouts = &setLayout, PushConstantRangeCount = parameters.Length == 0 ? 0u : 1u,
            PPushConstantRanges = parameters.Length == 0 ? null : &push };
        Check(device.Api.CreatePipelineLayout(device.Device, &layout, null, out _layout));
        var sizes = asset.Resources.GroupBy(r => Descriptor(r.Kind))
            .Select(g => new DescriptorPoolSize(g.Key, (uint)(g.Count() * RenderingConstants.FramesInFlight))).ToArray();
        fixed (DescriptorPoolSize* pointer = sizes)
        {
            var pool = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = RenderingConstants.FramesInFlight, PoolSizeCount = (uint)sizes.Length, PPoolSizes = pointer };
            Check(device.Api.CreateDescriptorPool(device.Device, &pool, null, out _pool));
        }
        _sets = new DescriptorSet[RenderingConstants.FramesInFlight];
        for (int i = 0; i < _sets.Length; i++)
        {
            var allocation = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            Check(device.Api.AllocateDescriptorSets(device.Device, &allocation, out _sets[i]));
        }
        if (asset.Resources.Any(r => r.Kind == EffectResourceKind.SampledTexture2D))
        {
            var sampler = new SamplerCreateInfo { SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear,
                MinFilter = Filter.Linear, AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge, MaxLod = 0 };
            Check(device.Api.CreateSampler(device.Device, &sampler, null, out _sampler));
        }
    }
    public void ResourcesChanged(VulkanPassContext context)
    {
        Format format = Compute ? Format.Undefined : context.GetImage("$destination").Format;
        if (_pipeline.Handle != 0 && _format == format) return;
        var replacement = CreatePipeline(format);
        var previous = _pipeline;
        _pipeline = replacement; _format = format;
        if (previous.Handle != 0)
        {
            var device = _device;
            context.Retire(() => device.Api.DestroyPipeline(device.Device, previous, null));
        }
    }
    public void Record(VulkanPassContext context)
    {
        var set = _sets[context.FrameIndex];
        // Update only the fence-completed slot. Stack storage stays valid until UpdateDescriptorSets returns.
        foreach (var resource in asset.Resources)
        {
            DescriptorImageInfo image = default;
            DescriptorBufferInfo buffer = default;
            if (resource.Kind == EffectResourceKind.StorageBuffer)
            {
                var bound = context.GetBuffer(resource.Name);
                buffer = new(bound.Buffer, bound.Offset, bound.Length);
            }
            else
            {
                var bound = context.GetImage(resource.Name);
                image = new(resource.Kind == EffectResourceKind.SampledTexture2D ? _sampler : default, bound.View,
                    resource.Kind == EffectResourceKind.SampledTexture2D ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.General);
                // Depth uses the graph's read-only depth layout.
                if (bound.Format is Format.D32Sfloat or Format.D32SfloatS8Uint or Format.D24UnormS8Uint or Format.D16Unorm)
                    image.ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal;
            }
            var write = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set,
                DstBinding = resource.Binding, DescriptorCount = 1, DescriptorType = Descriptor(resource.Kind),
                PImageInfo = &image, PBufferInfo = &buffer };
            _device.Api.UpdateDescriptorSets(_device.Device, 1, &write, 0, null);
        }
        var point = Compute ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics;
        _device.Api.CmdBindPipeline(context.CommandBuffer, point, _pipeline);
        _device.Api.CmdBindDescriptorSets(context.CommandBuffer, point, _layout, 0, 1, &set, 0, null);
        if (parameters.Length != 0)
            fixed (byte* pointer = parameters)
                _device.Api.CmdPushConstants(context.CommandBuffer, _layout, Stage, 0, (uint)parameters.Length, pointer);
        if (Compute)
        {
            var local = asset.WorkgroupSize;
            _device.Api.CmdDispatch(context.CommandBuffer, Groups(DispatchSize.X, local.X), Groups(DispatchSize.Y, local.Y), Groups(DispatchSize.Z, local.Z));
            return;
        }
        var destination = context.GetImage("$destination");
        var attachment = new RenderingAttachmentInfo { SType = StructureType.RenderingAttachmentInfo, ImageView = destination.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal, LoadOp = AttachmentLoadOp.DontCare, StoreOp = AttachmentStoreOp.Store };
        var rendering = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = new(default, destination.Extent),
            LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &attachment };
        var viewport = new Viewport(0, 0, destination.Extent.Width, destination.Extent.Height, 0, 1);
        var scissor = new Rect2D(default, destination.Extent);
        _device.Api.CmdSetViewport(context.CommandBuffer, 0, 1, &viewport);
        _device.Api.CmdSetScissor(context.CommandBuffer, 0, 1, &scissor);
        _device.Api.CmdBeginRendering(context.CommandBuffer, &rendering);
        _device.Api.CmdDraw(context.CommandBuffer, 3, 1, 0, 0);
        _device.Api.CmdEndRendering(context.CommandBuffer);
    }
    internal static uint Groups(uint threads, uint local) => 1 + (threads - 1) / local;
    private VkPipeline CreatePipeline(Format format)
    {
        ShaderModule shader = default, vertexModule = default;
        try
        {
            shader = ShaderModuleLoader.LoadEffect(graphics.Context, asset);
            byte* entry = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
            var stage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = Stage, Module = shader, PName = entry };
            VkPipeline pipeline;
            if (Compute)
            {
                var info = new ComputePipelineCreateInfo { SType = StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = _layout };
                Result result = _device.Api.CreateComputePipelines(_device.Device, default, 1, &info, null, out pipeline);
                if (result != Result.Success && pipeline.Handle != 0) _device.Api.DestroyPipeline(_device.Device, pipeline, null);
                Check(result);
                return pipeline;
            }
            vertexModule = ShaderModuleLoader.Load(graphics.Context, "composite.vert.spv");
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexModule, PName = entry };
            stages[1] = stage;
            var vertex = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var input = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var viewport = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var raster = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1 };
            var multisample = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            var color = new PipelineColorBlendAttachmentState { ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &color };
            var dynamics = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamic = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamics };
            var rendering = new PipelineRenderingCreateInfo { SType = StructureType.PipelineRenderingCreateInfo, ColorAttachmentCount = 1, PColorAttachmentFormats = &format };
            var graphicsInfo = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, PNext = &rendering,
                StageCount = 2, PStages = stages, PVertexInputState = &vertex, PInputAssemblyState = &input, PViewportState = &viewport,
                PRasterizationState = &raster, PMultisampleState = &multisample, PColorBlendState = &blend, PDynamicState = &dynamic, Layout = _layout };
            Result graphicsResult = _device.Api.CreateGraphicsPipelines(_device.Device, default, 1, &graphicsInfo, null, out pipeline);
            if (graphicsResult != Result.Success && pipeline.Handle != 0) _device.Api.DestroyPipeline(_device.Device, pipeline, null);
            Check(graphicsResult);
            return pipeline;
        }
        finally
        {
            if (shader.Handle != 0) _device.Api.DestroyShaderModule(_device.Device, shader, null);
            if (vertexModule.Handle != 0) _device.Api.DestroyShaderModule(_device.Device, vertexModule, null);
        }
    }
    private void Check(Result result)
    {
        if (result != Result.Success) throw new InvalidOperationException($"Effect '{asset.Name}' Vulkan setup failed: {result}.");
    }
    public void Dispose()
    {
        if (_pipeline.Handle != 0) { _device.Api.DestroyPipeline(_device.Device, _pipeline, null); _pipeline = default; }
        if (_pool.Handle != 0) { _device.Api.DestroyDescriptorPool(_device.Device, _pool, null); _pool = default; }
        if (_sampler.Handle != 0) { _device.Api.DestroySampler(_device.Device, _sampler, null); _sampler = default; }
        if (_layout.Handle != 0) { _device.Api.DestroyPipelineLayout(_device.Device, _layout, null); _layout = default; }
        if (_setLayout.Handle != 0) { _device.Api.DestroyDescriptorSetLayout(_device.Device, _setLayout, null); _setLayout = default; }
    }
}
