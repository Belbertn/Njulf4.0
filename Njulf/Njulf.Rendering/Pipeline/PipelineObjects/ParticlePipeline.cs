using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkPipeline = Silk.NET.Vulkan.Pipeline;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    public sealed unsafe class ParticlePipeline : IDisposable
    {
        private const string EntryPoint = "main";
        private const uint AlphaBlendFamily = 1u << 0;
        private const uint PremultipliedBlendFamily = 1u << 1;
        private const uint AdditiveBlendFamily = 1u << 2;
        private const uint SoftAdditiveBlendFamily = 1u << 3;
        private const uint AllBlendFamilies = AlphaBlendFamily |
                                                PremultipliedBlendFamily |
                                                AdditiveBlendFamily |
                                                SoftAdditiveBlendFamily;

        private readonly VulkanContext _context;
        private readonly BindlessHeap _bindlessHeap;
        private readonly bool _receiverFeedbackEnabled;
        private readonly GiPipelineCacheService? _pipelineCacheService;
        private readonly nint _entryPointName;
        private VkPipeline _alphaPipeline;
        private VkPipeline _premultipliedPipeline;
        private VkPipeline _additivePipeline;
        private VkPipeline _softAdditivePipeline;
        private VkPipeline _alphaReceiverFeedbackPipeline;
        private VkPipeline _premultipliedReceiverFeedbackPipeline;
        private VkPipeline _additiveReceiverFeedbackPipeline;
        private VkPipeline _softAdditiveReceiverFeedbackPipeline;
        private PipelineLayout _layout;
        private PipelineCache _pipelineCache;
        private Format _colorFormat;
        private Format _depthFormat;
        private uint _preparedBlendFamilies;
        private bool _receiverFeedbackCreationFailed;
        private bool _disposed;

        public ParticlePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat,
            bool receiverFeedbackEnabled = false)
            : this(
                context,
                bindlessHeap,
                colorFormat,
                depthFormat,
                receiverFeedbackEnabled,
                pipelineCacheService: null,
                createPipelines: true)
        {
        }

        internal ParticlePipeline(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            Format colorFormat,
            Format depthFormat,
            bool receiverFeedbackEnabled,
            GiPipelineCacheService? pipelineCacheService,
            bool createPipelines)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bindlessHeap = bindlessHeap ?? throw new ArgumentNullException(nameof(bindlessHeap));
            _receiverFeedbackEnabled = receiverFeedbackEnabled;
            _pipelineCacheService = pipelineCacheService;
            _entryPointName = SilkMarshal.StringToPtr(EntryPoint);
            _colorFormat = colorFormat;
            _depthFormat = depthFormat;

            ValidatePushConstantRange((uint)Marshal.SizeOf<GPUParticlePushConstants>());
            CreatePipelineCache();
            CreatePipelineLayout();
            if (createPipelines)
                PrepareAll();
        }

        public PipelineLayout Layout => _layout;

        public bool ReceiverFeedbackPipelinesAvailable =>
            _receiverFeedbackEnabled &&
            !_receiverFeedbackCreationFailed &&
            _preparedBlendFamilies != 0 &&
            HasRequiredReceiverFeedbackPipelines();
        public string ReceiverFeedbackPipelineFailureReason { get; private set; } =
            "receiver-feedback-pipelines-not-admitted-at-startup";

        public VkPipeline GetPipeline(
            ParticleBlendMode blendMode,
            bool receiverFeedback = false)
        {
            PrepareBlendMode(blendMode);
            if (receiverFeedback && !ReceiverFeedbackPipelinesAvailable)
            {
                throw new InvalidOperationException(
                    "Exact particle receiver-feedback pipelines are unavailable.");
            }

            return blendMode switch
            {
                ParticleBlendMode.AlphaBlend or ParticleBlendMode.AlphaClip =>
                    receiverFeedback
                        ? _alphaReceiverFeedbackPipeline
                        : _alphaPipeline,
                ParticleBlendMode.Additive => receiverFeedback
                    ? _additiveReceiverFeedbackPipeline
                    : _additivePipeline,
                ParticleBlendMode.SoftAdditive => receiverFeedback
                    ? _softAdditiveReceiverFeedbackPipeline
                    : _softAdditivePipeline,
                _ => receiverFeedback
                    ? _premultipliedReceiverFeedbackPipeline
                    : _premultipliedPipeline
            };
        }

        public void Recreate(Format colorFormat, Format depthFormat)
        {
            _colorFormat = colorFormat;
            _depthFormat = depthFormat;
            uint preparedBlendFamilies = _preparedBlendFamilies;
            DestroyPipelines();
            if (preparedBlendFamilies != 0)
                Prepare(preparedBlendFamilies);
        }

        internal bool IsPrepared => _preparedBlendFamilies != 0;

        internal bool RequiresPreparation(
            IEnumerable<ParticleBlendMode> blendModes)
        {
            ArgumentNullException.ThrowIfNull(blendModes);
            foreach (ParticleBlendMode blendMode in blendModes)
            {
                if ((_preparedBlendFamilies & GetBlendFamily(blendMode)) == 0)
                    return true;
            }
            return false;
        }

        internal void Prepare(IEnumerable<ParticleBlendMode> blendModes)
        {
            ArgumentNullException.ThrowIfNull(blendModes);
            uint requestedBlendFamilies = 0;
            foreach (ParticleBlendMode blendMode in blendModes)
                requestedBlendFamilies |= GetBlendFamily(blendMode);
            Prepare(requestedBlendFamilies);
        }

        internal void PrepareAll() => Prepare(AllBlendFamilies);

        private void PrepareBlendMode(ParticleBlendMode blendMode) =>
            Prepare(GetBlendFamily(blendMode));

        private void Prepare(uint requestedBlendFamilies)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            uint pendingBlendFamilies =
                requestedBlendFamilies & ~_preparedBlendFamilies;
            if (pendingBlendFamilies == 0)
                return;

            try
            {
                CreatePipelines(
                    _colorFormat,
                    _depthFormat,
                    pendingBlendFamilies);
                _preparedBlendFamilies |= pendingBlendFamilies;
            }
            catch
            {
                DestroyPipelines();
                throw;
            }
        }

        private void ValidatePushConstantRange(uint requiredSize)
        {
            var properties = new PhysicalDeviceProperties();
            _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
                throw new VulkanException($"Particle pass requires {requiredSize} bytes of push constants but GPU supports {properties.Limits.MaxPushConstantsSize}.");
        }

        private void CreatePipelineCache()
        {
            if (_pipelineCacheService != null)
            {
                _pipelineCache = _pipelineCacheService.Cache;
                return;
            }

            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = _context.Api.CreatePipelineCache(_context.Device, &cacheInfo, null, out _pipelineCache);
            if (result != Result.Success)
                throw new VulkanException("Failed to create particle pipeline cache", result);
            _context.SetDebugName(_pipelineCache.Handle, ObjectType.PipelineCache, "Particle Pipeline Cache");
        }

        private void CreatePipelineLayout()
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = _bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = _bindlessHeap.TextureSamplerSetLayout;

            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Offset = 0,
                Size = (uint)Marshal.SizeOf<GPUParticlePushConstants>()
            };

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = _context.Api.CreatePipelineLayout(_context.Device, &layoutInfo, null, out _layout);
            if (result != Result.Success)
                throw new VulkanException("Failed to create particle pipeline layout", result);
            _context.SetDebugName(_layout.Handle, ObjectType.PipelineLayout, "Particle Pipeline Layout");
        }

        private void CreatePipelines(
            Format colorFormat,
            Format depthFormat,
            uint blendFamilies)
        {
            ShaderModule vertexModule = default;
            ShaderModule receiverFeedbackVertexModule = default;
            ShaderModule fragmentModule = default;

            try
            {
                vertexModule = ShaderModuleLoader.Load(_context, "particle.vert.spv");
                fragmentModule = ShaderModuleLoader.Load(_context, "particle.frag.spv");
                _context.SetDebugName(vertexModule.Handle, ObjectType.ShaderModule, "particle.vert.spv");
                _context.SetDebugName(fragmentModule.Handle, ObjectType.ShaderModule, "particle.frag.spv");

                if ((blendFamilies & AlphaBlendFamily) != 0)
                    _alphaPipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, "Particle Alpha Pipeline");
                if ((blendFamilies & PremultipliedBlendFamily) != 0)
                    _premultipliedPipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, "Particle Premultiplied Pipeline");
                if ((blendFamilies & AdditiveBlendFamily) != 0)
                    _additivePipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.One, "Particle Additive Pipeline");
                if ((blendFamilies & SoftAdditiveBlendFamily) != 0)
                    _softAdditivePipeline = CreateGraphicsPipeline(vertexModule, fragmentModule, colorFormat, depthFormat, BlendFactor.One, BlendFactor.One, "Particle Soft Additive Pipeline");

                if (_receiverFeedbackEnabled)
                {
                    try
                    {
                        receiverFeedbackVertexModule = ShaderModuleLoader.Load(
                            _context,
                            "particle_b1.vert.spv");
                        _context.SetDebugName(
                            receiverFeedbackVertexModule.Handle,
                            ObjectType.ShaderModule,
                            "particle_b1.vert.spv");
                        if ((blendFamilies & AlphaBlendFamily) != 0)
                            _alphaReceiverFeedbackPipeline = CreateGraphicsPipeline(
                                receiverFeedbackVertexModule,
                                fragmentModule,
                                colorFormat,
                                depthFormat,
                                BlendFactor.SrcAlpha,
                                BlendFactor.OneMinusSrcAlpha,
                                "Particle Alpha B1 Receiver Feedback Pipeline");
                        if ((blendFamilies & PremultipliedBlendFamily) != 0)
                            _premultipliedReceiverFeedbackPipeline =
                                CreateGraphicsPipeline(
                                    receiverFeedbackVertexModule,
                                    fragmentModule,
                                    colorFormat,
                                    depthFormat,
                                    BlendFactor.One,
                                    BlendFactor.OneMinusSrcAlpha,
                                    "Particle Premultiplied B1 Receiver Feedback Pipeline");
                        if ((blendFamilies & AdditiveBlendFamily) != 0)
                            _additiveReceiverFeedbackPipeline = CreateGraphicsPipeline(
                                receiverFeedbackVertexModule,
                                fragmentModule,
                                colorFormat,
                                depthFormat,
                                BlendFactor.One,
                                BlendFactor.One,
                                "Particle Additive B1 Receiver Feedback Pipeline");
                        if ((blendFamilies & SoftAdditiveBlendFamily) != 0)
                            _softAdditiveReceiverFeedbackPipeline =
                                CreateGraphicsPipeline(
                                    receiverFeedbackVertexModule,
                                    fragmentModule,
                                    colorFormat,
                                    depthFormat,
                                    BlendFactor.One,
                                    BlendFactor.One,
                                    "Particle Soft Additive B1 Receiver Feedback Pipeline");
                        ReceiverFeedbackPipelineFailureReason =
                            "receiver-feedback-pipelines-ready";
                    }
                    catch (Exception exception) when (
                        exception is VulkanException or IOException or
                        ArgumentException or InvalidOperationException)
                    {
                        DestroyPipeline(ref _alphaReceiverFeedbackPipeline);
                        DestroyPipeline(
                            ref _premultipliedReceiverFeedbackPipeline);
                        DestroyPipeline(ref _additiveReceiverFeedbackPipeline);
                        DestroyPipeline(
                            ref _softAdditiveReceiverFeedbackPipeline);
                        _receiverFeedbackCreationFailed = true;
                        ReceiverFeedbackPipelineFailureReason =
                            "receiver-feedback-particle-pipeline-creation-failed:" +
                            exception.GetType().Name + ":" + exception.Message;
                        System.Diagnostics.Debug.WriteLine(
                            "B1 particle pipelines unavailable; ordinary " +
                            "particles retained. " +
                            ReceiverFeedbackPipelineFailureReason);
                    }
                }
            }
            finally
            {
                DestroyShaderModule(fragmentModule);
                DestroyShaderModule(receiverFeedbackVertexModule);
                DestroyShaderModule(vertexModule);
            }
        }

        private VkPipeline CreateGraphicsPipeline(
            ShaderModule vertexModule,
            ShaderModule fragmentModule,
            Format colorFormat,
            Format depthFormat,
            BlendFactor srcColor,
            BlendFactor dstColor,
            string debugName)
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = CreateShaderStageInfo(ShaderStageFlags.VertexBit, vertexModule);
            stages[1] = CreateShaderStageInfo(ShaderStageFlags.FragmentBit, fragmentModule);

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
                DepthWriteEnable = false,
                DepthCompareOp = CompareOp.GreaterOrEqual,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
                MinDepthBounds = 0.0f,
                MaxDepthBounds = 1.0f
            };

            var colorBlendAttachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = srcColor,
                DstColorBlendFactor = dstColor,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
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
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachment
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
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &renderingColorFormat,
                DepthAttachmentFormat = depthFormat,
                StencilAttachmentFormat = Format.Undefined
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = 2,
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

            Result result = _pipelineCacheService != null
                ? _pipelineCacheService.CreateGraphicsPipeline(
                    new PipelineArtifactId(debugName),
                    &pipelineInfo,
                    out VkPipeline pipeline)
                : _context.Api.CreateGraphicsPipelines(
                    _context.Device,
                    _pipelineCache,
                    1,
                    &pipelineInfo,
                    null,
                    out pipeline);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}", result);

            _context.SetDebugName(pipeline.Handle, ObjectType.Pipeline, debugName);
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
            DestroyPipeline(ref _alphaPipeline);
            DestroyPipeline(ref _premultipliedPipeline);
            DestroyPipeline(ref _additivePipeline);
            DestroyPipeline(ref _softAdditivePipeline);
            DestroyPipeline(ref _alphaReceiverFeedbackPipeline);
            DestroyPipeline(ref _premultipliedReceiverFeedbackPipeline);
            DestroyPipeline(ref _additiveReceiverFeedbackPipeline);
            DestroyPipeline(ref _softAdditiveReceiverFeedbackPipeline);
            _preparedBlendFamilies = 0;
            _receiverFeedbackCreationFailed = false;
            ReceiverFeedbackPipelineFailureReason =
                "receiver-feedback-pipelines-not-admitted-at-startup";
        }

        private bool HasRequiredReceiverFeedbackPipelines()
        {
            return ((_preparedBlendFamilies & AlphaBlendFamily) == 0 ||
                    _alphaReceiverFeedbackPipeline.Handle != 0) &&
                   ((_preparedBlendFamilies & PremultipliedBlendFamily) == 0 ||
                    _premultipliedReceiverFeedbackPipeline.Handle != 0) &&
                   ((_preparedBlendFamilies & AdditiveBlendFamily) == 0 ||
                    _additiveReceiverFeedbackPipeline.Handle != 0) &&
                   ((_preparedBlendFamilies & SoftAdditiveBlendFamily) == 0 ||
                    _softAdditiveReceiverFeedbackPipeline.Handle != 0);
        }

        private static uint GetBlendFamily(ParticleBlendMode blendMode)
        {
            return blendMode switch
            {
                ParticleBlendMode.AlphaBlend or ParticleBlendMode.AlphaClip =>
                    AlphaBlendFamily,
                ParticleBlendMode.Additive => AdditiveBlendFamily,
                ParticleBlendMode.SoftAdditive => SoftAdditiveBlendFamily,
                _ => PremultipliedBlendFamily
            };
        }

        private void DestroyPipeline(ref VkPipeline pipeline)
        {
            if (pipeline.Handle != 0)
            {
                _context.Api.DestroyPipeline(_context.Device, pipeline, null);
                pipeline = default;
            }
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
            if (_layout.Handle != 0)
                _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
            if (_pipelineCacheService == null && _pipelineCache.Handle != 0)
                _context.Api.DestroyPipelineCache(_context.Device, _pipelineCache, null);
            if (_entryPointName != 0)
                SilkMarshal.Free(_entryPointName);
            GC.SuppressFinalize(this);
        }
    }
}
