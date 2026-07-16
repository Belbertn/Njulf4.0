using System;
using Njulf.Rendering.Core;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline.PipelineObjects
{
    internal static unsafe class GraphicsPipelineFactory
    {
        public static void ValidatePushConstantRange(VulkanContext context, uint requiredSize, string ownerName)
        {
            var properties = new PhysicalDeviceProperties();
            context.Api.GetPhysicalDeviceProperties(context.PhysicalDevice, &properties);
            if (requiredSize > properties.Limits.MaxPushConstantsSize)
                throw new VulkanException($"{ownerName} requires {requiredSize} bytes of push constants but GPU supports {properties.Limits.MaxPushConstantsSize}.");
        }

        public static PipelineCache CreatePipelineCache(VulkanContext context, string debugName)
        {
            var cacheInfo = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo
            };

            Result result = context.Api.CreatePipelineCache(context.Device, &cacheInfo, null, out PipelineCache cache);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}", result);
            context.SetDebugName(cache.Handle, ObjectType.PipelineCache, debugName);
            return cache;
        }

        public static PipelineLayout CreateBindlessPipelineLayout(
            VulkanContext context,
            BindlessHeap bindlessHeap,
            PushConstantRange pushConstantRange,
            string debugName)
        {
            var setLayouts = stackalloc DescriptorSetLayout[2];
            setLayouts[0] = bindlessHeap.StorageBufferSetLayout;
            setLayouts[1] = bindlessHeap.TextureSamplerSetLayout;

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 2,
                PSetLayouts = setLayouts,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            Result result = context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out PipelineLayout layout);
            if (result != Result.Success)
                throw new VulkanException($"Failed to create {debugName}", result);
            context.SetDebugName(layout.Handle, ObjectType.PipelineLayout, debugName);
            return layout;
        }

        public static PipelineShaderStageCreateInfo ShaderStage(ShaderStageFlags stageFlags, ShaderModule module, nint entryPointName)
        {
            return new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stageFlags,
                Module = module,
                PName = (byte*)entryPointName
            };
        }

        public static PipelineVertexInputStateCreateInfo EmptyVertexInput()
        {
            return new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo
            };
        }

        public static PipelineInputAssemblyStateCreateInfo TriangleListInputAssembly()
        {
            return new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };
        }

        public static PipelineViewportStateCreateInfo DynamicViewportScissorState()
        {
            return new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1
            };
        }

        public static PipelineRasterizationStateCreateInfo FillNoCullRasterization()
        {
            return new PipelineRasterizationStateCreateInfo
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
        }

        public static PipelineMultisampleStateCreateInfo SingleSample()
        {
            return new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };
        }

        public static PipelineDynamicStateCreateInfo DynamicViewportScissor(DynamicState* states)
        {
            states[0] = DynamicState.Viewport;
            states[1] = DynamicState.Scissor;
            return new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = states
            };
        }
    }
}
