using System;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class MeshPipeline : IDisposable
    {
        private const string EntryPoint = "main";

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly nint _entryPointName;

        private VkPipeline _depthPipeline;
        private VkPipeline _forwardPipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private bool _disposed;

        public MeshPipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);

            ValidatePushConstantRange((uint)Math.Max(
                Marshal.SizeOf<GPUDepthPushConstants>(),
                Marshal.SizeOf<GPUForwardPushConstants>()));
            CreatePipelineCache();
            CreatePipelineLayout();
            CreatePipelines(colorFormat, depthFormat);
        }

        public VkPipeline DepthPipeline => _depthPipeline;
        public VkPipeline ForwardPipeline => _forwardPipeline;
        public VkPipeline Pipeline => _forwardPipeline;
        public PipelineLayout Layout => _layout;

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
                    $"GPU supports {properties.Limits.MaxPushConstantsSize} bytes of push constants, " +
                    $"but mesh rendering requires {requiredSize} bytes.");
            }
        }

        private void CreatePipelineCache()
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(
                _context.Device,
                &cacheInfo,
                null,
                out _pipelineCache);

            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh pipeline cache", result);
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.TaskBitExt | ShaderStageFlags.MeshBitExt | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Math.Max(
                    Marshal.SizeOf<GPUDepthPushConstants>(),
                    Marshal.SizeOf<GPUForwardPushConstants>())
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(
                _context.Device,
                &layoutInfo,
                null,
                out _layout);

            if (result != Result.Success)
                throw new VulkanException("Failed to create mesh pipeline layout", result);
        }

        private void CreatePipelines(Format colorFormat, Format depthFormat)
        {
            _depthPipeline = CreateGraphicsPipeline(
                "depth.task.spv",
                "depth.mesh.spv",
                fragmentShaderName: null,
                colorFormat,
                depthFormat,
                hasColorAttachment: false,
                depthWriteEnable: true);

            _forwardPipeline = CreateGraphicsPipeline(
                "forward.task.spv",
                "forward.mesh.spv",
                "forward.frag.spv",
                colorFormat,
                depthFormat,
                hasColorAttachment: true,
                depthWriteEnable: false);
        }

        private VkPipeline CreateGraphicsPipeline(
            string taskShaderName,
            string meshShaderName,
            string? fragmentShaderName,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable)
        {
            ShaderModule taskModule = new ShaderModule();
            ShaderModule meshModule = new ShaderModule();
            ShaderModule fragmentModule = new ShaderModule();

            try
            {
                taskModule = ShaderModuleLoader.Load(_context, taskShaderName);
                meshModule = ShaderModuleLoader.Load(_context, meshShaderName);
                if (fragmentShaderName != null)
                    fragmentModule = ShaderModuleLoader.Load(_context, fragmentShaderName);

                return CreateGraphicsPipeline(
                    taskModule,
                    meshModule,
                    fragmentModule,
                    colorFormat,
                    depthFormat,
                    hasColorAttachment,
                    depthWriteEnable);
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(meshModule);
                DestroyShaderModule(taskModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            ShaderModule taskModule,
            ShaderModule meshModule,
            ShaderModule fragmentModule,
            Format colorFormat,
            Format depthFormat,
            bool hasColorAttachment,
            bool depthWriteEnable)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[3];
            stages[0] = CreateShaderStageInfo(ShaderStageFlags.TaskBitExt, taskModule);
            stages[1] = CreateShaderStageInfo(ShaderStageFlags.MeshBitExt, meshModule);

            uint stageCount = 2;
            if (fragmentModule.Handle != 0)
            {
                stages[2] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);
                stageCount = 3;
            }

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
                CullMode = CullModeFlags.BackBit,
                // Njulf's Vulkan backend uses a positive-height viewport, so front-facing
                // triangles are clockwise in framebuffer space. Keep glTF indices as-is;
                // asset importers should only flip winding for explicit negative-determinant
                // conversions or nonstandard source formats.
                FrontFace = FrontFace.Clockwise,
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
                StageCount = stageCount,
                PStages = stages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssemblyInfo,
                PViewportState = &viewportInfo,
                PRasterizationState = &rasterInfo,
                PMultisampleState = &multisampleInfo,
                PDepthStencilState = &depthStencilInfo,
                PColorBlendState = &colorBlendInfo,
                PDynamicState = &dynamicInfo,
                Layout = _layout,
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
                throw new VulkanException("Failed to create mesh graphics pipeline", result);

            return pipeline;
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
            if (_depthPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _depthPipeline, null);
                _depthPipeline = default;
            }

            if (_forwardPipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, _forwardPipeline, null);
                _forwardPipeline = default;
            }
        }

        private void DestroyShaderModule(ShaderModule module)
        {
            if (module.Handle != 0)
                _context.Api.DestroyShaderModule(_context.Device, module, null);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            DestroyPipelines();

            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);

            if (_pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);

            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);

            Console.WriteLine("Mesh pipelines disposed.");
        }

        ~MeshPipeline()
        {
            Dispose(false);
        }
    }
}
