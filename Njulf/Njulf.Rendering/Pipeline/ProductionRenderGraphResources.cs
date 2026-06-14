using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    internal static class ProductionRenderGraphResources
    {
        public const string HdrSceneColorName = "HDR Scene Color";
        public const string SceneDepthName = "Scene Depth";
        public const string FoggedSceneColorName = "Fogged HDR Scene Color";
        public const string HiZDepthPyramidName = "Hi-Z Depth Pyramid";
        public const string AmbientOcclusionRawName = "Ambient Occlusion Raw";
        public const string AmbientOcclusionBlurredName = "Ambient Occlusion Blurred";
        public const string AmbientOcclusionScratchName = "Ambient Occlusion Scratch";
        public const string LdrSceneColorName = "LDR Scene Color";
        public const string SwapchainColorName = "Swapchain Color";
        public const string SmaaEdgesName = "SMAA Edges";
        public const string SmaaBlendWeightsName = "SMAA Blend Weights";
        public const string SmaaAreaTextureName = "SMAA Area Texture";
        public const string SmaaSearchTextureName = "SMAA Search Texture";
        public const string MotionVectorsName = "Motion Vectors";
        public const string WeightedOitAccumulationName = "Weighted OIT Accumulation";
        public const string WeightedOitRevealageName = "Weighted OIT Revealage";
        public const string TaaHistoryAName = "TAA History A";
        public const string TaaHistoryBName = "TAA History B";
        public const string BloomExtractName = "Bloom Extract";
        public const string DirectionalShadowMapArrayName = "Directional Shadow Map Array";
        public const string SpotShadowAtlasName = "Spot Shadow Atlas";
        public const string PointShadowCubemapArrayName = "Point Shadow Cubemap Array";
        public const string EnvironmentCubemapName = "Environment Cubemap";
        public const string DebugDrawVertexBufferName = "Debug Draw Vertex Buffer";
        public const string OpaqueMeshletDrawBufferName = "Opaque Meshlet Draw Buffer";
        public const string SolidDepthMeshletDrawBufferName = "Solid Depth Meshlet Draw Buffer";
        public const string MaskedDepthMeshletDrawBufferName = "Masked Depth Meshlet Draw Buffer";
        public const string DirectionalShadowMeshletDrawBufferName = "Directional Shadow Meshlet Draw Buffer";
        public const string LocalShadowMeshletDrawBufferName = "Local Shadow Meshlet Draw Buffer";
        public const string TransparentMeshletDrawBufferName = "Transparent Meshlet Draw Buffer";
        public const string LightBufferName = "Light Buffer";
        public const string TiledLightHeaderBufferName = "Tiled Light Header Buffer";
        public const string TiledLightIndexBufferName = "Tiled Light Index Buffer";
        public const string ParticleInstanceBufferName = "Particle Instance Buffer";
        public const string ParticleBatchBufferName = "Particle Batch Buffer";
        public const string AutoExposureHistogramBufferName = "Auto Exposure Histogram Buffer";
        public const string AutoExposureStateBufferName = "Auto Exposure State Buffer";
        public const string GpuSceneObjectBufferName = "GPU Scene Object Buffer";
        public const string GpuSceneInstanceBufferName = "GPU Scene Instance Buffer";
        public const string GpuSceneTransformBufferName = "GPU Scene Transform Buffer";
        public const string GpuScenePreviousTransformBufferName = "GPU Scene Previous Transform Buffer";
        public const string GpuSceneBoundsBufferName = "GPU Scene Bounds Buffer";
        public const string GpuSceneVisibilityBufferName = "GPU Scene Visibility Buffer";
        public const string GpuSceneCompactedIndexBufferName = "GPU Scene Compacted Index Buffer";
        public const string SkinMatrixBufferName = "Skin Matrix Buffer";
        public const string SkinningDispatchBufferName = "Skinning Dispatch Buffer";
        public const string SkinnedVertexBufferName = "Skinned Vertex Buffer";

        public static RenderGraphResourceHandle HdrSceneColor(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                HdrSceneColorName,
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                AllowDriverCompression = true
            });
        }

        public static RenderGraphResourceHandle SceneDepth(RenderGraphResourceRegistry resources, Format depthFormat)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                SceneDepthName,
                depthFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient));
        }

        public static RenderGraphResourceHandle FoggedSceneColor(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                FoggedSceneColorName,
                RenderTargetManager.FoggedSceneColorFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                AllowDriverCompression = true
            });
        }

        public static RenderGraphResourceHandle HiZDepthPyramid(RenderGraphResourceRegistry resources, uint mipCount)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                HiZDepthPyramidName,
                Format.R32Sfloat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                MipCount = Math.Max(1u, mipCount),
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle HiZDepthPyramid(RenderGraphResourceRegistry resources)
        {
            RenderGraphResourceHandle existing = resources.FindImage(HiZDepthPyramidName);
            if (existing.IsValid)
                return existing;

            return HiZDepthPyramid(resources, 1);
        }

        public static RenderGraphResourceHandle AmbientOcclusionRaw(RenderGraphResourceRegistry resources, float resolutionScale)
        {
            return AmbientOcclusionTarget(resources, AmbientOcclusionRawName, resolutionScale);
        }

        public static RenderGraphResourceHandle AmbientOcclusionBlurred(RenderGraphResourceRegistry resources, float resolutionScale)
        {
            return AmbientOcclusionTarget(resources, AmbientOcclusionBlurredName, resolutionScale);
        }

        public static RenderGraphResourceHandle AmbientOcclusionScratch(RenderGraphResourceRegistry resources, float resolutionScale)
        {
            return AmbientOcclusionTarget(resources, AmbientOcclusionScratchName, resolutionScale);
        }

        public static RenderGraphResourceHandle MotionVectors(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                MotionVectorsName,
                RenderTargetManager.MotionVectorFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient));
        }

        public static RenderGraphResourceHandle WeightedOitAccumulation(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                WeightedOitAccumulationName,
                RenderTargetManager.WeightedOitAccumulationFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle WeightedOitRevealage(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                WeightedOitRevealageName,
                RenderTargetManager.WeightedOitRevealageFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle LdrSceneColor(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                LdrSceneColorName,
                RenderTargetManager.LdrSceneColorFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                AllowDriverCompression = true
            });
        }

        public static RenderGraphResourceHandle SwapchainColor(RenderGraphResourceRegistry resources, Format swapchainFormat)
        {
            Format format = swapchainFormat == Format.Undefined ? Format.B8G8R8A8Unorm : swapchainFormat;
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                SwapchainColorName,
                format,
                RenderGraphResolutionClass.Swapchain,
                RenderGraphResourcePersistence.Imported));
        }

        public static RenderGraphResourceHandle SmaaEdges(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                SmaaEdgesName,
                RenderTargetManager.SmaaEdgesFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle SmaaBlendWeights(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                SmaaBlendWeightsName,
                RenderTargetManager.SmaaBlendWeightsFormat,
                RenderGraphResolutionClass.Scene,
                RenderGraphResourcePersistence.Transient)
            {
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle TaaHistoryA(RenderGraphResourceRegistry resources)
        {
            return TaaHistory(resources, TaaHistoryAName);
        }

        public static RenderGraphResourceHandle TaaHistoryB(RenderGraphResourceRegistry resources)
        {
            return TaaHistory(resources, TaaHistoryBName);
        }

        public static RenderGraphResourceHandle BloomMip(RenderGraphResourceRegistry resources, int mip)
        {
            string name = mip <= 0 ? BloomExtractName : $"Bloom Mip {mip}";
            RenderGraphResourceHandle existing = resources.FindImage(name);
            if (existing.IsValid)
                return existing;

            float scale = 1.0f / (1 << Math.Clamp(mip + 1, 1, 16));
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                name,
                RenderTargetManager.SceneColorFormat,
                RenderGraphResolutionClass.CustomScale,
                RenderGraphResourcePersistence.Transient)
            {
                CustomResolutionScale = scale,
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        public static RenderGraphResourceHandle SmaaAreaTexture(RenderGraphResourceRegistry resources)
        {
            return SmaaLookupTexture(resources, SmaaAreaTextureName);
        }

        public static RenderGraphResourceHandle SmaaSearchTexture(RenderGraphResourceRegistry resources)
        {
            return SmaaLookupTexture(resources, SmaaSearchTextureName);
        }

        public static RenderGraphResourceHandle DirectionalShadowMapArray(
            RenderGraphResourceRegistry resources,
            DirectionalShadowResources shadows)
        {
            if (shadows == null)
                throw new ArgumentNullException(nameof(shadows));

            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                DirectionalShadowMapArrayName,
                shadows.Format,
                RenderGraphResolutionClass.Fixed,
                RenderGraphResourcePersistence.External)
            {
                Width = Math.Max(1u, shadows.MapSize),
                Height = Math.Max(1u, shadows.MapSize),
                ArrayLayers = (uint)Math.Max(1, shadows.CascadeCount)
            });
        }

        public static RenderGraphResourceHandle SpotShadowAtlas(
            RenderGraphResourceRegistry resources,
            SpotShadowAtlas atlas)
        {
            if (atlas == null)
                throw new ArgumentNullException(nameof(atlas));

            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                SpotShadowAtlasName,
                atlas.Format,
                RenderGraphResolutionClass.Fixed,
                RenderGraphResourcePersistence.External)
            {
                Width = Math.Max(1u, atlas.AtlasSize),
                Height = Math.Max(1u, atlas.AtlasSize)
            });
        }

        public static RenderGraphResourceHandle PointShadowCubemapArray(
            RenderGraphResourceRegistry resources,
            PointShadowCubemapArray cubemapArray)
        {
            if (cubemapArray == null)
                throw new ArgumentNullException(nameof(cubemapArray));

            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                PointShadowCubemapArrayName,
                cubemapArray.Format,
                RenderGraphResolutionClass.Fixed,
                RenderGraphResourcePersistence.External)
            {
                Width = Math.Max(1u, cubemapArray.MapSize),
                Height = Math.Max(1u, cubemapArray.MapSize),
                ArrayLayers = (uint)Math.Max(1, cubemapArray.LayerCount)
            });
        }

        public static RenderGraphResourceHandle EnvironmentCubemap(
            RenderGraphResourceRegistry resources,
            RenderSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                EnvironmentCubemapName,
                ResolveEnvironmentFormat(settings.Environment.TexturePrecision),
                RenderGraphResolutionClass.Fixed,
                RenderGraphResourcePersistence.External)
            {
                Width = Math.Max(1u, settings.Environment.EnvironmentSize),
                Height = Math.Max(1u, settings.Environment.EnvironmentSize),
                ArrayLayers = 6
            });
        }

        public static RenderGraphResourceHandle DebugDrawVertexBuffer(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateBuffer(new RenderGraphBufferDesc(
                DebugDrawVertexBufferName,
                RenderGraphResourcePersistence.External)
            {
                Stride = (uint)Marshal.SizeOf<GPUDebugLineVertex>(),
                Count = 4096,
                Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                CpuUploadPolicy = RenderGraphCpuUploadPolicy.PerFrameUpload
            });
        }

        public static RenderGraphResourceHandle OpaqueMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, OpaqueMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle SolidDepthMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, SolidDepthMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle MaskedDepthMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, MaskedDepthMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle DirectionalShadowMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, DirectionalShadowMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle LocalShadowMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, LocalShadowMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle TransparentMeshletDrawBuffer(RenderGraphResourceRegistry resources)
        {
            return MeshletDrawBuffer(resources, TransparentMeshletDrawBufferName);
        }

        public static RenderGraphResourceHandle LightBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, LightBufferName);
        }

        public static RenderGraphResourceHandle TiledLightHeaderBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, TiledLightHeaderBufferName);
        }

        public static RenderGraphResourceHandle TiledLightIndexBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, TiledLightIndexBufferName);
        }

        public static RenderGraphResourceHandle ParticleInstanceBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, ParticleInstanceBufferName);
        }

        public static RenderGraphResourceHandle ParticleBatchBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, ParticleBatchBufferName);
        }

        public static RenderGraphResourceHandle AutoExposureHistogramBuffer(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateBuffer(new RenderGraphBufferDesc(
                AutoExposureHistogramBufferName,
                RenderGraphResourcePersistence.External)
            {
                ByteSize = AutoExposureManager.HistogramBufferSize,
                Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit
            });
        }

        public static RenderGraphResourceHandle AutoExposureStateBuffer(RenderGraphResourceRegistry resources)
        {
            return resources.GetOrCreateBuffer(new RenderGraphBufferDesc(
                AutoExposureStateBufferName,
                RenderGraphResourcePersistence.External)
            {
                ByteSize = AutoExposureManager.StateBufferSize,
                Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit
            });
        }

        public static RenderGraphResourceHandle SkinMatrixBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, SkinMatrixBufferName, (uint)Marshal.SizeOf<Matrix4x4>());
        }

        public static RenderGraphResourceHandle SkinningDispatchBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, SkinningDispatchBufferName, (uint)Marshal.SizeOf<GPUSkinningDispatch>());
        }

        public static RenderGraphResourceHandle SkinnedVertexBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, SkinnedVertexBufferName, (uint)Marshal.SizeOf<GPUVertex>());
        }

        public static GpuSceneGraphBuffers GpuSceneBuffers(RenderGraphResourceRegistry resources)
        {
            if (resources == null)
                throw new ArgumentNullException(nameof(resources));

            return new GpuSceneGraphBuffers(
                GpuSceneObjectBuffer(resources),
                GpuSceneInstanceBuffer(resources),
                GpuSceneTransformBuffer(resources),
                GpuScenePreviousTransformBuffer(resources),
                GpuSceneBoundsBuffer(resources),
                GpuSceneVisibilityBuffer(resources),
                GpuSceneCompactedIndexBuffer(resources));
        }

        public static RenderGraphResourceHandle GpuSceneObjectBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneObjectBufferName, (uint)Marshal.SizeOf<GPUSceneObject>());
        }

        public static RenderGraphResourceHandle GpuSceneInstanceBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneInstanceBufferName, (uint)Marshal.SizeOf<GPUSceneInstance>());
        }

        public static RenderGraphResourceHandle GpuSceneTransformBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneTransformBufferName, (uint)Marshal.SizeOf<GPUTransform>());
        }

        public static RenderGraphResourceHandle GpuScenePreviousTransformBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuScenePreviousTransformBufferName, (uint)Marshal.SizeOf<GPUPreviousTransform>());
        }

        public static RenderGraphResourceHandle GpuSceneBoundsBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneBoundsBufferName, (uint)Marshal.SizeOf<GPUObjectBounds>());
        }

        public static RenderGraphResourceHandle GpuSceneVisibilityBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneVisibilityBufferName, (uint)Marshal.SizeOf<GPUVisibilityState>());
        }

        public static RenderGraphResourceHandle GpuSceneCompactedIndexBuffer(RenderGraphResourceRegistry resources)
        {
            return StorageBuffer(resources, GpuSceneCompactedIndexBufferName, sizeof(uint));
        }

        private static RenderGraphResourceHandle AmbientOcclusionTarget(
            RenderGraphResourceRegistry resources,
            string name,
            float resolutionScale)
        {
            RenderGraphResourceHandle existing = resources.FindImage(name);
            if (existing.IsValid)
                return existing;

            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                name,
                RenderTargetManager.AmbientOcclusionFormat,
                RenderGraphResolutionClass.CustomScale,
                RenderGraphResourcePersistence.Transient)
            {
                CustomResolutionScale = NormalizeResolutionScale(resolutionScale),
                UsageHint = ImageUsageFlags.SampledBit
            });
        }

        private static RenderGraphResourceHandle MeshletDrawBuffer(RenderGraphResourceRegistry resources, string name)
        {
            return StorageBuffer(resources, name);
        }

        private static RenderGraphResourceHandle StorageBuffer(RenderGraphResourceRegistry resources, string name, uint stride = 0, uint count = 0)
        {
            return resources.GetOrCreateBuffer(new RenderGraphBufferDesc(
                name,
                RenderGraphResourcePersistence.External)
            {
                ByteSize = stride == 0 ? 1u : 0u,
                Stride = stride,
                Count = count == 0 && stride != 0 ? 1u : count,
                Usage = BufferUsageFlags.StorageBufferBit
            });
        }

        private static RenderGraphResourceHandle TaaHistory(RenderGraphResourceRegistry resources, string name)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                name,
                RenderTargetManager.LdrSceneColorFormat,
                RenderGraphResolutionClass.HistoryMatchedScene,
                RenderGraphResourcePersistence.History)
            {
                HistoryInvalidationRule = "invalidate-on-resolution-change"
            });
        }

        private static RenderGraphResourceHandle SmaaLookupTexture(RenderGraphResourceRegistry resources, string name)
        {
            return resources.GetOrCreateImage(new RenderGraphImageDesc(
                name,
                Format.R8G8B8A8Unorm,
                RenderGraphResolutionClass.Fixed,
                RenderGraphResourcePersistence.External)
            {
                Width = 1,
                Height = 1
            });
        }

        private static float NormalizeResolutionScale(float resolutionScale)
        {
            if (resolutionScale <= 0.0f)
                return 0.5f;

            return resolutionScale <= 0.375f ? 0.25f : resolutionScale <= 0.75f ? 0.5f : 1.0f;
        }

        private static Format ResolveEnvironmentFormat(EnvironmentTexturePrecision precision)
        {
            return precision == EnvironmentTexturePrecision.Float32
                ? Format.R32G32B32A32Sfloat
                : Format.R16G16B16A16Sfloat;
        }
    }

    internal readonly record struct GpuSceneGraphBuffers(
        RenderGraphResourceHandle Objects,
        RenderGraphResourceHandle Instances,
        RenderGraphResourceHandle Transforms,
        RenderGraphResourceHandle PreviousTransforms,
        RenderGraphResourceHandle Bounds,
        RenderGraphResourceHandle Visibility,
        RenderGraphResourceHandle CompactedIndices);
}
