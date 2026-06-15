using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.GpuScene;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Diagnostics
{
    public static class RenderGraphResourceInventoryBuilder
    {
        public static RenderGraphResourceInventorySnapshot BuildProductionFrame(
            Extent2D sceneExtent,
            Format depthFormat,
            RenderSettings settings,
            SceneRenderingData? sceneData = null,
            uint swapchainImageCount = 0,
            Format swapchainFormat = Format.Undefined,
            GpuSceneStats? gpuSceneStats = null,
            GpuSceneBufferSetStats? gpuSceneBufferStats = null)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (sceneExtent.Width == 0 || sceneExtent.Height == 0)
                throw new ArgumentOutOfRangeException(nameof(sceneExtent), "Scene extent must be non-zero.");

            var images = new List<RenderGraphImageResourceInventory>(32);
            var buffers = new List<RenderGraphBufferResourceInventory>(32);

            AddCoreImages(images, sceneExtent, depthFormat, settings, sceneData, swapchainImageCount, swapchainFormat);
            AddExternalImages(images, sceneExtent, settings, sceneData);
            AddFrameBuffers(buffers, settings, sceneData, gpuSceneStats, gpuSceneBufferStats);

            ulong imageBytes = 0;
            foreach (RenderGraphImageResourceInventory image in images)
                imageBytes = checked(imageBytes + image.EstimatedBytes);

            ulong bufferBytes = 0;
            foreach (RenderGraphBufferResourceInventory buffer in buffers)
                bufferBytes = checked(bufferBytes + buffer.ByteSize);

            return new RenderGraphResourceInventorySnapshot(
                PassOrder: ProductionRenderPipeline.PassOrder,
                Images: images,
                Buffers: buffers,
                EstimatedImageBytes: imageBytes,
                EstimatedBufferBytes: bufferBytes);
        }

        private static void AddCoreImages(
            List<RenderGraphImageResourceInventory> images,
            Extent2D sceneExtent,
            Format depthFormat,
            RenderSettings settings,
            SceneRenderingData? sceneData,
            uint swapchainImageCount,
            Format swapchainFormat)
        {
            AntiAliasingMode aaMode = settings.AntiAliasing.EffectiveMode;
            bool smaa = AntiAliasingSettings.IsSmaaMode(aaMode);
            bool taa = aaMode == AntiAliasingMode.Taa;
            bool needsAaTarget = aaMode != AntiAliasingMode.None;

            AddImage(images, "Swapchain Color", swapchainFormat, "swapchain", sceneExtent.Width, sceneExtent.Height, 1, Math.Max(1u, swapchainImageCount),
                "color-attachment|present", "imported", "external",
                ProducerForSwapchain(aaMode), ["Present", "ScreenshotCapture"]);
            AddImage(images, "HDR Scene Color", RenderTargetManager.SceneColorFormat, "scene", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "color-attachment|sampled", "transient", "frame",
                ["ForwardPlusPass", "SkyboxPass", "TransparentForwardPass", "WeightedOitCompositePass", "ParticlePass", "DebugDrawPass"],
                ["FogPass", "AutoExposurePass", "BloomPass", "ToneMapCompositePass"]);
            AddImage(images, "Scene Depth", depthFormat, "scene", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "depth-attachment|sampled", "transient", "frame",
                ["DepthPrePass"],
                ["MotionVectorPass", "HiZBuildPass", "AmbientOcclusionPass", "TiledLightCullingPass", "ForwardPlusPass", "TransparentForwardPass", "ParticlePass", "DebugDrawPass", "FogPass"]);
            AddImage(images, "Fogged HDR Scene Color", RenderTargetManager.FoggedSceneColorFormat, "scene", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "storage|sampled", settings.Fog.Enabled ? "transient" : "placeholder", "frame",
                ["FogPass"], ["ToneMapCompositePass"]);

            Extent2D aoExtent = RenderTargetManager.CalculateAmbientOcclusionExtent(sceneExtent, settings.AmbientOcclusion.ResolutionScale);
            AddImage(images, "Ambient Occlusion Raw", RenderTargetManager.AmbientOcclusionFormat, "ambient-occlusion", aoExtent.Width, aoExtent.Height, 1, 1,
                "storage|sampled", settings.AmbientOcclusion.Enabled ? "transient" : "placeholder", "frame",
                ["AmbientOcclusionPass"], ["AmbientOcclusionBlurPass"]);
            AddImage(images, "Ambient Occlusion Blurred", RenderTargetManager.AmbientOcclusionFormat, "ambient-occlusion", aoExtent.Width, aoExtent.Height, 1, 1,
                "storage|sampled", settings.AmbientOcclusion.Enabled ? "transient" : "placeholder", "frame",
                ["AmbientOcclusionBlurPass"], ["ForwardPlusPass", "TransparentForwardPass"]);
            AddImage(images, "Ambient Occlusion Scratch", RenderTargetManager.AmbientOcclusionFormat, "ambient-occlusion", aoExtent.Width, aoExtent.Height, 1, 1,
                "storage|sampled", settings.AmbientOcclusion.Enabled ? "transient" : "placeholder", "frame",
                ["AmbientOcclusionBlurPass"], ["AmbientOcclusionBlurPass"]);

            AddImage(images, "LDR Scene Color", RenderTargetManager.LdrSceneColorFormat, "swapchain", needsAaTarget ? sceneExtent.Width : 1, needsAaTarget ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", needsAaTarget ? "transient" : "placeholder", "frame",
                ["ToneMapCompositePass"], ["AntiAliasingPass"]);
            AddImage(images, "SMAA Edges", RenderTargetManager.SmaaEdgesFormat, "swapchain", smaa ? sceneExtent.Width : 1, smaa ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", smaa ? "transient" : "placeholder", "frame",
                ["AntiAliasingPass"], ["AntiAliasingPass"]);
            AddImage(images, "SMAA Blend Weights", RenderTargetManager.SmaaBlendWeightsFormat, "swapchain", smaa ? sceneExtent.Width : 1, smaa ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", smaa ? "transient" : "placeholder", "frame",
                ["AntiAliasingPass"], ["AntiAliasingPass"]);
            AddImage(images, "Motion Vectors", RenderTargetManager.MotionVectorFormat, "swapchain", taa ? sceneExtent.Width : 1, taa ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", taa ? "transient" : "placeholder", "frame",
                ["MotionVectorPass"], ["AntiAliasingPass"]);
            AddImage(images, "Weighted OIT Accumulation", RenderTargetManager.WeightedOitAccumulationFormat, "scene", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "color-attachment|sampled", "transient", "frame",
                ["TransparentForwardPass"], ["WeightedOitCompositePass"]);
            AddImage(images, "Weighted OIT Revealage", RenderTargetManager.WeightedOitRevealageFormat, "scene", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "color-attachment|sampled", "transient", "frame",
                ["TransparentForwardPass"], ["WeightedOitCompositePass"]);
            AddImage(images, "TAA History A", RenderTargetManager.LdrSceneColorFormat, "history-matched-scene", taa ? sceneExtent.Width : 1, taa ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", taa ? "history" : "placeholder", "persistent",
                ["AntiAliasingPass"], ["AntiAliasingPass"]);
            AddImage(images, "TAA History B", RenderTargetManager.LdrSceneColorFormat, "history-matched-scene", taa ? sceneExtent.Width : 1, taa ? sceneExtent.Height : 1, 1, 1,
                "color-attachment|sampled", taa ? "history" : "placeholder", "persistent",
                ["AntiAliasingPass"], ["AntiAliasingPass"]);

            IReadOnlyList<Extent2D> bloomExtents = RenderTargetManager.CalculateBloomMipExtents(sceneExtent, settings.Bloom.MipCount);
            for (int i = 0; i < bloomExtents.Count; i++)
            {
                Extent2D extent = bloomExtents[i];
                AddImage(images, i == 0 ? "Bloom Extract" : $"Bloom Mip {i}", RenderTargetManager.SceneColorFormat, "bloom-chain", extent.Width, extent.Height, 1, 1,
                    "storage|sampled", settings.Bloom.Enabled ? "transient" : "placeholder", "frame",
                    ["BloomPass"], i == bloomExtents.Count - 1 ? ["ToneMapCompositePass"] : ["BloomPass", "ToneMapCompositePass"]);
            }

            uint hizWidth = sceneData?.HiZWidth > 0 ? sceneData.HiZWidth : sceneExtent.Width;
            uint hizHeight = sceneData?.HiZHeight > 0 ? sceneData.HiZHeight : sceneExtent.Height;
            uint hizMips = sceneData?.HiZMipCount > 0 ? sceneData.HiZMipCount : CalculateMipLevels(hizWidth, hizHeight, HiZDepthPyramid.MinimumUsefulMipDimension);
            AddImage(images, "Hi-Z Depth Pyramid", Format.R32Sfloat, "scene", hizWidth, hizHeight, hizMips, 1,
                "storage|sampled", "transient", "frame",
                ["HiZBuildPass"], ["ForwardPlusPass", "DepthPrePass"]);
        }

        private static void AddExternalImages(
            List<RenderGraphImageResourceInventory> images,
            Extent2D sceneExtent,
            RenderSettings settings,
            SceneRenderingData? sceneData)
        {
            AddImage(images, "Directional Shadow Map Array", Format.D32Sfloat, "fixed", settings.Shadows.DirectionalShadowMapSize, settings.Shadows.DirectionalShadowMapSize, 1, (uint)Math.Max(1, settings.Shadows.DirectionalCascadeCount),
                "depth-attachment|sampled", settings.Shadows.DirectionalShadowsEnabled ? "external" : "disabled", "persistent",
                ["DirectionalShadowPass"], ["ForwardPlusPass", "TransparentForwardPass"]);
            AddImage(images, "Spot Shadow Atlas", Format.D32Sfloat, "fixed", settings.Shadows.SpotShadowsEnabled ? settings.Shadows.SpotShadowAtlasSize : 0, settings.Shadows.SpotShadowsEnabled ? settings.Shadows.SpotShadowAtlasSize : 0, 1, 1,
                "depth-attachment|sampled", settings.Shadows.SpotShadowsEnabled ? "external" : "disabled", "persistent",
                ["SpotShadowPass"], ["ForwardPlusPass", "TransparentForwardPass"]);
            AddImage(images, "Point Shadow Cubemap Array", Format.D32Sfloat, "fixed", settings.Shadows.PointShadowsEnabled ? settings.Shadows.PointShadowMapSize : 0, settings.Shadows.PointShadowsEnabled ? settings.Shadows.PointShadowMapSize : 0, 1, (uint)Math.Max(1, settings.Shadows.MaxShadowedPointLights * 6),
                "depth-attachment|sampled", settings.Shadows.PointShadowsEnabled ? "external" : "disabled", "persistent",
                ["PointShadowPass"], ["ForwardPlusPass", "TransparentForwardPass"]);

            Format environmentFormat = ResolveEnvironmentFormat(settings.Environment.TexturePrecision);
            uint prefilteredMipCount = CalculateMipLevels(settings.Environment.PrefilteredSize, settings.Environment.PrefilteredSize, 1);
            AddImage(images, "Environment Cubemap", environmentFormat, "fixed", settings.Environment.EnvironmentSize, settings.Environment.EnvironmentSize, 1, 6,
                "sampled", settings.Environment.Enabled ? "external" : "disabled", "persistent",
                ["EnvironmentManager"], ["SkyboxPass", "ForwardPlusPass"]);
            AddImage(images, "Irradiance Cubemap", environmentFormat, "fixed", settings.Environment.IrradianceSize, settings.Environment.IrradianceSize, 1, 6,
                "sampled", settings.Environment.Enabled ? "external" : "disabled", "persistent",
                ["EnvironmentManager"], ["ForwardPlusPass"]);
            AddImage(images, "Prefiltered Environment Cubemap", environmentFormat, "fixed", settings.Environment.PrefilteredSize, settings.Environment.PrefilteredSize, prefilteredMipCount, 6,
                "sampled", settings.Environment.Enabled ? "external" : "disabled", "persistent",
                ["EnvironmentManager"], ["ForwardPlusPass", "ReflectionProbeManager"]);
            AddImage(images, "BRDF LUT", environmentFormat, "fixed", settings.Environment.BrdfLutSize, settings.Environment.BrdfLutSize, 1, 1,
                "sampled", settings.Environment.Enabled ? "external" : "disabled", "persistent",
                ["EnvironmentManager"], ["ForwardPlusPass"]);

            uint probeMipCount = CalculateMipLevels(settings.Reflections.ProbeResolution, settings.Reflections.ProbeResolution, 1);
            uint probeLayers = (uint)Math.Max(1, settings.Reflections.MaxProbes * 6);
            AddImage(images, "Reflection Probe Cubemap Array", environmentFormat, "fixed", settings.Reflections.Enabled ? settings.Reflections.ProbeResolution : 0, settings.Reflections.Enabled ? settings.Reflections.ProbeResolution : 0, probeMipCount, probeLayers,
                "sampled|transfer-destination", settings.Reflections.Enabled ? "external" : "disabled", "persistent",
                ["ReflectionProbeManager"], ["ForwardPlusPass"]);
            AddImage(images, "Reflection Probe Capture Target", RenderTargetManager.SceneColorFormat, "fixed", sceneExtent.Width, sceneExtent.Height, 1, 1,
                "color-attachment|sampled", settings.Reflections.Enabled && settings.Reflections.CaptureOnLoad ? "external" : "disabled", "frame",
                ["ReflectionProbeManager"], ["ReflectionProbeManager"]);
        }

        private static void AddFrameBuffers(
            List<RenderGraphBufferResourceInventory> buffers,
            RenderSettings settings,
            SceneRenderingData? sceneData,
            GpuSceneStats? gpuSceneStats,
            GpuSceneBufferSetStats? gpuSceneBufferStats)
        {
            if (gpuSceneStats != null && gpuSceneBufferStats != null)
            {
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneObjectBufferName, (ulong)gpuSceneBufferStats.ObjectCapacity * (ulong)Marshal.SizeOf<GPUSceneObject>(), (uint)Marshal.SizeOf<GPUSceneObject>(), (uint)gpuSceneStats.ObjectHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "DepthPrePass", "ForwardPlusPass"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneInstanceBufferName, (ulong)gpuSceneBufferStats.InstanceCapacity * (ulong)Marshal.SizeOf<GPUSceneInstance>(), (uint)Marshal.SizeOf<GPUSceneInstance>(), (uint)gpuSceneStats.InstanceHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "DepthPrePass", "ForwardPlusPass"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneTransformBufferName, (ulong)gpuSceneBufferStats.InstanceCapacity * (ulong)Marshal.SizeOf<GPUTransform>(), (uint)Marshal.SizeOf<GPUTransform>(), (uint)gpuSceneStats.InstanceHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "MotionVectorPass", "ForwardPlusPass"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuScenePreviousTransformBufferName, (ulong)gpuSceneBufferStats.InstanceCapacity * (ulong)Marshal.SizeOf<GPUPreviousTransform>(), (uint)Marshal.SizeOf<GPUPreviousTransform>(), (uint)gpuSceneStats.InstanceHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["MotionVectorPass", "AntiAliasingPass"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneBoundsBufferName, (ulong)gpuSceneBufferStats.ObjectCapacity * (ulong)Marshal.SizeOf<GPUObjectBounds>(), (uint)Marshal.SizeOf<GPUObjectBounds>(), (uint)gpuSceneStats.ObjectHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "DebugDrawPass"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneVisibilityBufferName, (ulong)gpuSceneBufferStats.ObjectCapacity * (ulong)Marshal.SizeOf<GPUVisibilityState>(), (uint)Marshal.SizeOf<GPUVisibilityState>(), (uint)gpuSceneStats.ObjectHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "Diagnostics"]);
                AddBuffer(buffers, ProductionRenderGraphResources.GpuSceneCompactedIndexBufferName, (ulong)gpuSceneBufferStats.InstanceCapacity * sizeof(uint), sizeof(uint), (uint)gpuSceneStats.InstanceHighWaterMark, "storage|transfer-destination", "external", "persistent", ["GpuSceneManager"], ["Visibility", "RenderPasses"]);
            }

            bool gpuDrivenVisibility = sceneData?.GpuDrivenVisibilityEnabled == true;
            IReadOnlyList<string> drawBufferProducers = gpuDrivenVisibility ? ["GpuVisibilityPass"] : ["SceneDataBuilder"];
            uint opaqueDrawHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, sceneData?.OpaqueMeshletCount ?? 0) : (uint)Math.Max(0, sceneData?.MeshletDrawCommands.Count ?? 0);
            uint solidDepthDrawHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, sceneData?.SolidMeshletCount ?? 0) : (uint)Math.Max(0, sceneData?.SolidDepthMeshletDrawCommands.Count ?? 0);
            uint maskedDepthDrawHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, sceneData?.MaskedMeshletCount ?? 0) : (uint)Math.Max(0, sceneData?.MaskedDepthMeshletDrawCommands.Count ?? 0);
            uint transparentDrawHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, sceneData?.TransparentMeshletCount ?? 0) : (uint)Math.Max(0, sceneData?.TransparentMeshletDrawCommands.Count ?? 0);
            uint directionalShadowHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, SumDirectionalShadowMeshlets(sceneData)) : 0u;
            uint localShadowHighWater = gpuDrivenVisibility ? (uint)Math.Max(0, sceneData?.LocalShadowMeshletCount ?? 0) : 0u;

            AddBuffer(buffers, "Material Data Buffer", sceneData?.MaterialBufferSize ?? 0, 0, 0, "storage|transfer-destination", "external", "persistent", ["MaterialManager"], ["DepthPrePass", "ForwardPlusPass"]);
            AddBuffer(buffers, "Material Extension Data Buffer", sceneData?.MaterialExtensionBufferSize ?? 0, 0, 0, "storage|transfer-destination", "external", "persistent", ["MaterialManager"], ["ForwardPlusPass"]);
            AddBuffer(buffers, "Meshlet Draw Buffer", sceneData?.MeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), opaqueDrawHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["ForwardPlusPass"]);
            AddBuffer(buffers, "Solid Depth Meshlet Draw Buffer", sceneData?.SolidDepthMeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), solidDepthDrawHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["DepthPrePass"]);
            AddBuffer(buffers, "Masked Depth Meshlet Draw Buffer", sceneData?.MaskedDepthMeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), maskedDepthDrawHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["DepthPrePass"]);
            AddBuffer(buffers, "Transparent Meshlet Draw Buffer", sceneData?.TransparentMeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), transparentDrawHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["TransparentForwardPass"]);
            AddBuffer(buffers, "Directional Shadow Meshlet Draw Buffer", sceneData?.DirectionalShadowMeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), directionalShadowHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["DirectionalShadowPass"]);
            AddBuffer(buffers, "Local Shadow Meshlet Draw Buffer", sceneData?.LocalShadowMeshletDrawBufferSize ?? 0, (uint)Marshal.SizeOf<GPUMeshletDrawCommand>(), localShadowHighWater, "storage|transfer-destination|indirect", "external", "frame", drawBufferProducers, ["SpotShadowPass", "PointShadowPass"]);
            AddBuffer(buffers, "Light Buffer", sceneData?.LightUploadBytes ?? 0, 0, 0, "storage|transfer-destination", "external", "persistent", ["LightManager"], ["TiledLightCullingPass", "ForwardPlusPass"]);
            AddBuffer(buffers, "Tiled Light Header Buffer", sceneData?.TiledLightHeaderBufferSize ?? 0, 0, 0, "storage", "external", "frame", ["TiledLightCullingPass"], ["ForwardPlusPass"]);
            AddBuffer(buffers, "Tiled Light Index Buffer", sceneData?.TiledLightIndexBufferSize ?? 0, 0, 0, "storage", "external", "frame", ["TiledLightCullingPass"], ["ForwardPlusPass"]);
            AddBuffer(buffers, "Particle Instance Buffer", sceneData?.ParticleInstanceBufferSize ?? 0, 0, 0, "storage|transfer-destination", settings.Particles.Enabled ? "external" : "disabled", "frame", ["ParticleSystemManager"], ["ParticlePass"]);
            AddBuffer(buffers, "Particle Batch Buffer", sceneData?.ParticleBatchBufferSize ?? 0, 0, 0, "storage|transfer-destination", settings.Particles.Enabled ? "external" : "disabled", "frame", ["ParticleSystemManager"], ["ParticlePass"]);
            AddBuffer(buffers, "Skin Matrix Buffer", sceneData?.SkinMatrixBufferSize ?? 0, 0, 0, "storage|transfer-destination", settings.Animation.Enabled ? "external" : "disabled", "frame", ["SkinningManager"], ["SkinningPass"]);
            AddBuffer(buffers, "Skinned Vertex Buffer", sceneData?.SkinnedVertexBufferSize ?? 0, 0, 0, "storage", settings.Animation.Enabled ? "external" : "disabled", "frame", ["SkinningPass"], ["DepthPrePass", "ForwardPlusPass"]);
            AddBuffer(buffers, "Renderer Diagnostics Buffer", RendererDiagnosticsBuffer.CounterBufferSizePerFrame, sizeof(uint), RendererDiagnosticsBuffer.CounterCount, "storage|transfer-source", "external", "frame", ["RenderGraphPasses"], ["VulkanRenderer"]);
            AddBuffer(buffers, "Auto Exposure Histogram Buffer", AutoExposureManager.HistogramBufferSize, sizeof(uint), AutoExposureManager.HistogramBinCount, "storage|transfer-destination", settings.AutoExposure.Enabled ? "external" : "disabled", "frame", ["AutoExposurePass"], ["AutoExposurePass"]);
            AddBuffer(buffers, "Auto Exposure State Buffer", AutoExposureManager.StateBufferSize, sizeof(uint), AutoExposureManager.StateWordCount, "storage|transfer-destination", settings.AutoExposure.Enabled ? "external" : "disabled", "persistent", ["AutoExposurePass"], ["ToneMapCompositePass"]);
            AddBuffer(buffers, "Staging Upload Ring", sceneData?.UploadedBytes ?? 0, 0, 0, "transfer-source|host-visible", "external", "frame", ["UploadSystems"], ["BufferManager", "TextureManager"]);
        }

        private static IReadOnlyList<string> ProducerForSwapchain(AntiAliasingMode mode)
        {
            return mode == AntiAliasingMode.None ? ["ToneMapCompositePass"] : ["AntiAliasingPass"];
        }

        private static int SumDirectionalShadowMeshlets(SceneRenderingData? sceneData)
        {
            if (sceneData == null)
                return 0;

            int total = 0;
            for (int i = 0; i < sceneData.DirectionalShadowMeshletCounts.Length; i++)
                total = checked(total + sceneData.DirectionalShadowMeshletCounts[i]);
            return total;
        }

        private static void AddImage(
            List<RenderGraphImageResourceInventory> images,
            string name,
            Format format,
            string resolutionClass,
            uint width,
            uint height,
            uint mipCount,
            uint arrayLayers,
            string usage,
            string persistence,
            string lifetime,
            IReadOnlyList<string> producers,
            IReadOnlyList<string> consumers)
        {
            images.Add(new RenderGraphImageResourceInventory(
                name,
                format.ToString(),
                resolutionClass,
                width,
                height,
                mipCount,
                arrayLayers,
                usage,
                persistence,
                lifetime,
                producers,
                consumers,
                EstimateImageBytes(format, width, height, mipCount, arrayLayers)));
        }

        private static void AddBuffer(
            List<RenderGraphBufferResourceInventory> buffers,
            string name,
            ulong byteSize,
            uint stride,
            uint count,
            string usage,
            string persistence,
            string lifetime,
            IReadOnlyList<string> producers,
            IReadOnlyList<string> consumers)
        {
            buffers.Add(new RenderGraphBufferResourceInventory(
                name,
                byteSize,
                stride,
                count,
                usage,
                persistence,
                lifetime,
                producers,
                consumers));
        }

        private static ulong EstimateImageBytes(Format format, uint width, uint height, uint mipCount, uint arrayLayers)
        {
            if (format == Format.Undefined || width == 0 || height == 0 || mipCount == 0 || arrayLayers == 0)
                return 0;

            return ImageByteEstimator.EstimateBytes(
                format,
                new Extent3D { Width = width, Height = height, Depth = 1 },
                mipCount,
                arrayLayers);
        }

        private static uint CalculateMipLevels(uint width, uint height, uint minimumDimension)
        {
            uint levels = 1;
            uint dimension = Math.Max(width, height);
            while (dimension > minimumDimension)
            {
                dimension /= 2;
                levels++;
            }

            return levels;
        }

        private static Format ResolveEnvironmentFormat(EnvironmentTexturePrecision precision)
        {
            return precision == EnvironmentTexturePrecision.Float32
                ? Format.R32G32B32A32Sfloat
                : Format.R16G16B16A16Sfloat;
        }
    }
}
