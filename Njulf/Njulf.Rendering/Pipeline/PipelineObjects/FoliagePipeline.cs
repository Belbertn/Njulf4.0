using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Utilities;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class FoliagePipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;
        private PipelineLayout _computeLayout;
        private PipelineLayout _graphicsLayout;
        private PipelineCache _pipelineCache;
        private VkPipeline _cullPipeline;
        private VkPipeline _depthPipeline;
        private VkPipeline _forwardPipeline;
        private VkPipeline _authoredDepthPipeline;
        private VkPipeline _authoredForwardPipeline;
        private bool _disposed;

        public FoliagePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Math.Max(
                Marshal.SizeOf<GPUFoliageCullPushConstants>(),
                Marshal.SizeOf<GPUFoliageDrawPushConstants>()));
            CreatePipelineCache();
            CreateLayouts();
            CreatePipelines(colorFormat, depthFormat);
        }

        public PipelineLayout ComputeLayout => _computeLayout;
        public PipelineLayout GraphicsLayout => _graphicsLayout;
        public VkPipeline CullPipeline => _cullPipeline;
        public VkPipeline DepthPipeline => _depthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline AuthoredDepthPipeline => _authoredDepthPipeline;
        public VkPipeline AuthoredForwardPipeline => _authoredForwardPipeline;

        public void Recreate(Format colorFormat, Format depthFormat)
        {
            DestroyPipelines();
            CreatePipelines(colorFormat, depthFormat);
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
            {
                throw new VulkanException(
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, but foliage requires {requiredSize} bytes.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Foliage Pipeline Cache");
        }

        private void CreateLayouts()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var computePushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.ComputeBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUFoliageCullPushConstants>()
            };
            var computeLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &computePushRange
            };
            Result result = _context.Api.CreatePipelineLayout(_context.Device, &computeLayoutInfo, null, out _computeLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage compute pipeline layout", result);
            _context.SetDebugName(_computeLayout.Handle, ObjectType.PipelineLayout, "Foliage Compute Pipeline Layout");

            var graphicsPushRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUFoliageDrawPushConstants>()
            };
            var graphicsLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &graphicsPushRange
            };
            result = _context.Api.CreatePipelineLayout(_context.Device, &graphicsLayoutInfo, null, out _graphicsLayout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create foliage graphics pipeline layout", result);
            _context.SetDebugName(_graphicsLayout.Handle, ObjectType.PipelineLayout, "Foliage Graphics Pipeline Layout");
        }

        private void CreatePipelines(Format colorFormat, Format depthFormat)
        {
            _cullPipeline = CreateComputePipeline("foliage_cull.comp.spv");
            _context.SetDebugName(_cullPipeline.Handle, ObjectType.Pipeline, "Foliage Cull Compute Pipeline");

            _depthPipeline = CreateGraphicsPipeline(
                "foliage_grass.task.spv",
                "foliage_grass.mesh.spv",
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true);
            _context.SetDebugName(_depthPipeline.Handle, ObjectType.Pipeline, "Foliage Grass Depth Pipeline");

            _forwardPipeline = CreateGraphicsPipeline(
                "foliage_grass.task.spv",
                "foliage_grass.mesh.spv",
                "foliage_forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false);
            _context.SetDebugName(_forwardPipeline.Handle, ObjectType.Pipeline, "Foliage Grass Forward Pipeline");

            _authoredDepthPipeline = CreateGraphicsPipeline(
                "foliage_mesh.task.spv",
                "foliage_mesh.mesh.spv",
                "foliage_depth.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true);
            _context.SetDebugName(_authoredDepthPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Depth Pipeline");

            _authoredForwardPipeline = CreateGraphicsPipeline(
                "foliage_mesh.task.spv",
                "foliage_mesh.mesh.spv",
                "foliage_forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false);
            _context.SetDebugName(_authoredForwardPipeline.Handle, ObjectType.Pipeline, "Foliage Authored Meshlet Forward Pipeline");
        }

        private VkPipeline CreateComputePipeline(string shaderName)
        {
            ShaderModule shaderModule = default;
            try
            {
                shaderModule = ShaderModuleLoader.Load(_context, shaderName);
                _context.SetDebugName(shaderModule.Handle, ObjectType.ShaderModule, shaderName);

                var shaderStageInfo = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = shaderModule,
                    PName = (byte*)_entryPointName
                };

                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = shaderStageInfo,
                    Layout = _computeLayout,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                Result result = _context.Api.CreateComputePipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create foliage compute pipeline", result);
                return pipeline;
            }
            finally
            {
                DestroyShaderModule(shaderModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            string taskShaderName,
            string meshShaderName,
            string fragmentShaderName,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable)
        {
            ShaderModule taskModule = default;
            ShaderModule meshModule = default;
            ShaderModule fragmentModule = default;
            try
            {
                taskModule = ShaderModuleLoader.Load(_context, taskShaderName);
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);
                _context.SetDebugName(taskModule.Handle, ObjectType.ShaderModule, taskShaderName);
                _context.SetDebugName(meshModule.Handle, ObjectType.ShaderModule, meshShaderName);
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, fragmentShaderName);

                var stages = stackalloc PipelineShaderStageCreateInfo[3];
                stages[0] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
                stages[1] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);
                stages[2] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

                var vertexInputInfo = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo
                };
                var inputAssemblyInfo = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
                };
                var viewportInfo = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };
                var rasterInfo = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = false,
                    RasterizerDiscardEnable = false,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = false,
                    LineWidth = 1.0f
                };
                var multisampleInfo = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                var depthStencilInfo = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = depthWriteEnable,
                    DepthCompareOp = CompareOp.GreaterOrEqual,
                    DepthBoundsTestEnable = false,
                    StencilTestEnable = false,
                    MinDepthBounds = 0.0f,
                    MaxDepthBounds = 1.0f
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    BlendEnable = false,
                    SrcColorBlendFactor = BlendFactor.One,
                    DstColorBlendFactor = BlendFactor.Zero,
                    ColorBlendOp = BlendOp.Add,
                    SrcAlphaBlendFactor = BlendFactor.One,
                    DstAlphaBlendFactor = BlendFactor.Zero,
                    AlphaBlendOp = BlendOp.Add,
                    ColorWriteMask = ColorComponentFlags.RBit |
                                     ColorComponentFlags.GBit |
                                     ColorComponentFlags.BBit |
                                     ColorComponentFlags.ABit
                };
                var colorBlendInfo = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = false,
                    AttachmentCount = hasColorAttachment ? 1u : 0u,
                    PAttachments = hasColorAttachment ? &colorBlendAttachment : null
                };
                var dynamicStates = stackalloc DynamicState[2];
                dynamicStates[0] = DynamicState.Viewport;
                dynamicStates[1] = DynamicState.Scissor;
                var dynamicInfo = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates
                };
                var renderingColorFormat = colorFormat;
                var renderingInfo = new PipelineRenderingCreateInfo
                {
                    SType = StructureType.PipelineRenderingCreateInfo,
                    ColorAttachmentCount = hasColorAttachment ? 1u : 0u,
                    PColorAttachmentFormats = hasColorAttachment ? &renderingColorFormat : null,
                    DepthAttachmentFormat = depthFormat,
                    StencilAttachmentFormat = Format.Undefined
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    PNext = &renderingInfo,
                    StageCount = 3,
                    PStages = stages,
                    PVertexInputState = &vertexInputInfo,
                    PInputAssemblyState = &inputAssemblyInfo,
                    PViewportState = &viewportInfo,
                    PRasterizationState = &rasterInfo,
                    PMultisampleState = &multisampleInfo,
                    PDepthStencilState = &depthStencilInfo,
                    PColorBlendState = &colorBlendInfo,
                    PDynamicState = &dynamicInfo,
                    Layout = _graphicsLayout,
                    RenderPass = default,
                    Subpass = 0,
                    BasePipelineHandle = default,
                    BasePipelineIndex = -1
                };

                Result result = _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out VkPipeline pipeline);
                if (result != Result.Success)
                    throw new VulkanException("Failed to create foliage graphics pipeline", result);
                return pipeline;
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(meshModule);
                DestroyShaderModule(taskModule);
            }
        }

        private PipelineShaderStageCreateInfo CreateShaderStageInfo(ShaderStageFlags stageFlags, ShaderModule module)
        {
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stageFlags,
                Module = module,
                PName = (byte*)_entryPointName
            };
        }

        private void DestroyPipelines()
        {
            DestroyPipeline(ref _cullPipeline);
            DestroyPipeline(ref _depthPipeline);
            DestroyPipeline(ref _forwardPipeline);
            DestroyPipeline(ref _authoredDepthPipeline);
            DestroyPipeline(ref _authoredForwardPipeline);
        }

        private void DestroyPipeline(ref VkPipeline pipeline)
        {
            if (pipeline.Handle == 0)
                return;

            _context.Api.DestroyPipeline(_context.Device, pipeline, null);
            pipeline = default;
        }

        private void DestroyShaderModule(ShaderModule module)
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            DestroyPipelines();
            if (_computeLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _computeLayout, null);
            if (_graphicsLayout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _graphicsLayout, null);
            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            SilkMarshal.Free(_entryPointName);
        }
    }
}
