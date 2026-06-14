using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class ProductionRenderGraphResourcesTests
    {
        [Test]
        public void SceneTargets_RequestCompatibleDescriptorsAcrossPasses()
        {
            var registry = new RenderGraphResourceRegistry();

            RenderGraphResourceHandle firstColor = ProductionRenderGraphResources.HdrSceneColor(registry);
            RenderGraphResourceHandle secondColor = ProductionRenderGraphResources.HdrSceneColor(registry);
            RenderGraphResourceHandle firstDepth = ProductionRenderGraphResources.SceneDepth(registry, Format.D32Sfloat);
            RenderGraphResourceHandle secondDepth = ProductionRenderGraphResources.SceneDepth(registry, Format.D32Sfloat);

            Assert.Multiple(() =>
            {
                Assert.That(secondColor, Is.EqualTo(firstColor));
                Assert.That(secondDepth, Is.EqualTo(firstDepth));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.HdrSceneColorName).Format, Is.EqualTo(RenderTargetManager.SceneColorFormat));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.SceneDepthName).Format, Is.EqualTo(Format.D32Sfloat));
            });
        }

        [Test]
        public void AmbientOcclusionTargets_NormalizeResolutionScaleAndShareDescriptorShape()
        {
            var registry = new RenderGraphResourceRegistry();

            RenderGraphResourceHandle raw = ProductionRenderGraphResources.AmbientOcclusionRaw(registry, 0.51f);
            RenderGraphResourceHandle scratch = ProductionRenderGraphResources.AmbientOcclusionScratch(registry, 0.51f);
            RenderGraphResourceHandle blurred = ProductionRenderGraphResources.AmbientOcclusionBlurred(registry, 0.51f);

            Assert.Multiple(() =>
            {
                Assert.That(raw.IsValid, Is.True);
                Assert.That(scratch.IsValid, Is.True);
                Assert.That(blurred.IsValid, Is.True);
                Assert.That(registry.Images.Where(image => image.Name.StartsWith("Ambient Occlusion")).Select(image => image.CustomResolutionScale), Is.All.EqualTo(0.5f));
                Assert.That(registry.Images.Where(image => image.Name.StartsWith("Ambient Occlusion")).Select(image => image.ResolutionClass), Is.All.EqualTo(RenderGraphResolutionClass.CustomScale));
                Assert.That(registry.Images.Where(image => image.Name.StartsWith("Ambient Occlusion")).Select(image => image.Persistence), Is.All.EqualTo(RenderGraphResourcePersistence.Transient));
            });
        }

        [Test]
        public void HiZDepthPyramid_RecordsMipCountForPerMipBarrierPlanning()
        {
            var registry = new RenderGraphResourceRegistry();

            RenderGraphResourceHandle hiz = ProductionRenderGraphResources.HiZDepthPyramid(registry, 7);
            RenderGraphResourceHandle reused = ProductionRenderGraphResources.HiZDepthPyramid(registry);
            RenderGraphImageDesc desc = registry.Images.Single(image => image.Name == ProductionRenderGraphResources.HiZDepthPyramidName);

            Assert.Multiple(() =>
            {
                Assert.That(hiz.IsValid, Is.True);
                Assert.That(reused, Is.EqualTo(hiz));
                Assert.That(desc.Format, Is.EqualTo(Format.R32Sfloat));
                Assert.That(desc.MipCount, Is.EqualTo(7));
                Assert.That(desc.Persistence, Is.EqualTo(RenderGraphResourcePersistence.Transient));
            });
        }

        [Test]
        public void ExternalFrameBuffers_UseStorageBufferDescriptors()
        {
            var registry = new RenderGraphResourceRegistry();

            ProductionRenderGraphResources.LightBuffer(registry);
            ProductionRenderGraphResources.TiledLightHeaderBuffer(registry);
            ProductionRenderGraphResources.TiledLightIndexBuffer(registry);
            ProductionRenderGraphResources.TransparentMeshletDrawBuffer(registry);
            ProductionRenderGraphResources.ParticleInstanceBuffer(registry);
            ProductionRenderGraphResources.ParticleBatchBuffer(registry);

            Assert.That(registry.Buffers, Has.Count.EqualTo(6));
            Assert.That(registry.Buffers.Select(buffer => buffer.Persistence), Is.All.EqualTo(RenderGraphResourcePersistence.External));
            Assert.That(registry.Buffers.Select(buffer => buffer.Usage), Is.All.EqualTo(BufferUsageFlags.StorageBufferBit));
        }

        [Test]
        public void GpuSceneBuffers_AreExternalStorageBuffersWithStableStrides()
        {
            var registry = new RenderGraphResourceRegistry();

            GpuSceneGraphBuffers handles = ProductionRenderGraphResources.GpuSceneBuffers(registry);

            Assert.Multiple(() =>
            {
                Assert.That(handles.Objects.IsValid, Is.True);
                Assert.That(handles.Instances.IsValid, Is.True);
                Assert.That(handles.Transforms.IsValid, Is.True);
                Assert.That(handles.PreviousTransforms.IsValid, Is.True);
                Assert.That(handles.Bounds.IsValid, Is.True);
                Assert.That(handles.Visibility.IsValid, Is.True);
                Assert.That(handles.CompactedIndices.IsValid, Is.True);
                Assert.That(registry.Buffers, Has.Count.EqualTo(7));
                Assert.That(registry.Buffers.Select(buffer => buffer.Persistence), Is.All.EqualTo(RenderGraphResourcePersistence.External));
                Assert.That(registry.Buffers.Select(buffer => buffer.Usage), Is.All.EqualTo(BufferUsageFlags.StorageBufferBit));
                Assert.That(registry.Buffers.Single(buffer => buffer.Name == ProductionRenderGraphResources.GpuSceneObjectBufferName).Stride, Is.EqualTo(Marshal.SizeOf<GPUSceneObject>()));
                Assert.That(registry.Buffers.Single(buffer => buffer.Name == ProductionRenderGraphResources.GpuSceneTransformBufferName).Stride, Is.EqualTo(Marshal.SizeOf<GPUTransform>()));
                Assert.That(registry.Buffers.Single(buffer => buffer.Name == ProductionRenderGraphResources.GpuSceneCompactedIndexBufferName).Stride, Is.EqualTo(sizeof(uint)));
            });
        }

        [Test]
        public void EnvironmentCubemap_UsesFixedExternalCubemapDescriptor()
        {
            var registry = new RenderGraphResourceRegistry();
            var settings = new Njulf.Rendering.Data.RenderSettings();
            settings.Environment.EnvironmentSize = 256;

            RenderGraphResourceHandle environment = ProductionRenderGraphResources.EnvironmentCubemap(registry, settings);
            RenderGraphImageDesc desc = registry.Images.Single(image => image.Name == ProductionRenderGraphResources.EnvironmentCubemapName);

            Assert.Multiple(() =>
            {
                Assert.That(environment.IsValid, Is.True);
                Assert.That(desc.ResolutionClass, Is.EqualTo(RenderGraphResolutionClass.Fixed));
                Assert.That(desc.Persistence, Is.EqualTo(RenderGraphResourcePersistence.External));
                Assert.That(desc.Width, Is.EqualTo(256));
                Assert.That(desc.Height, Is.EqualTo(256));
                Assert.That(desc.ArrayLayers, Is.EqualTo(6));
            });
        }

        [Test]
        public void PostProcessTargets_UseExpectedPersistenceAndResolutionClasses()
        {
            var registry = new RenderGraphResourceRegistry();

            ProductionRenderGraphResources.FoggedSceneColor(registry);
            ProductionRenderGraphResources.LdrSceneColor(registry);
            ProductionRenderGraphResources.SwapchainColor(registry, Format.B8G8R8A8Unorm);
            ProductionRenderGraphResources.SmaaEdges(registry);
            ProductionRenderGraphResources.SmaaBlendWeights(registry);
            ProductionRenderGraphResources.WeightedOitAccumulation(registry);
            ProductionRenderGraphResources.WeightedOitRevealage(registry);
            ProductionRenderGraphResources.BloomMip(registry, 0);
            ProductionRenderGraphResources.BloomMip(registry, 3);

            Assert.Multiple(() =>
            {
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.FoggedSceneColorName).Persistence, Is.EqualTo(RenderGraphResourcePersistence.Transient));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.LdrSceneColorName).Format, Is.EqualTo(RenderTargetManager.LdrSceneColorFormat));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.SwapchainColorName).ResolutionClass, Is.EqualTo(RenderGraphResolutionClass.Swapchain));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.SmaaEdgesName).Format, Is.EqualTo(RenderTargetManager.SmaaEdgesFormat));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.WeightedOitAccumulationName).Format, Is.EqualTo(RenderTargetManager.WeightedOitAccumulationFormat));
                Assert.That(registry.Images.Single(image => image.Name == ProductionRenderGraphResources.WeightedOitRevealageName).Format, Is.EqualTo(RenderTargetManager.WeightedOitRevealageFormat));
                Assert.That(registry.Images.Single(image => image.Name == "Bloom Mip 3").CustomResolutionScale, Is.EqualTo(0.0625f));
            });
        }

        [Test]
        public void HistoryAndAutoExposureResources_RecordCrossFrameIntent()
        {
            var registry = new RenderGraphResourceRegistry();

            ProductionRenderGraphResources.TaaHistoryA(registry);
            ProductionRenderGraphResources.TaaHistoryB(registry);
            ProductionRenderGraphResources.AutoExposureHistogramBuffer(registry);
            ProductionRenderGraphResources.AutoExposureStateBuffer(registry);

            Assert.Multiple(() =>
            {
                Assert.That(registry.Images.Where(image => image.Name.StartsWith("TAA History")).Select(image => image.Persistence), Is.All.EqualTo(RenderGraphResourcePersistence.History));
                Assert.That(registry.Images.Where(image => image.Name.StartsWith("TAA History")).Select(image => image.HistoryInvalidationRule), Is.All.EqualTo("invalidate-on-resolution-change"));
                Assert.That(registry.Buffers.Single(buffer => buffer.Name == ProductionRenderGraphResources.AutoExposureHistogramBufferName).ByteSize, Is.EqualTo(AutoExposureManager.HistogramBufferSize));
                Assert.That(registry.Buffers.Single(buffer => buffer.Name == ProductionRenderGraphResources.AutoExposureStateBufferName).ByteSize, Is.EqualTo(AutoExposureManager.StateBufferSize));
            });
        }

        [Test]
        public void ProductionPasses_AllOverrideResourceDeclarations()
        {
            foreach (string passName in ProductionRenderPipeline.PassOrder)
            {
                string typeName = passName switch
                {
                    "GpuOcclusionCompactionPass" => "GpuVisibilityPass",
                    "SkinningPass" => "SkinningGraphPass",
                    _ => passName
                };
                Type passType = typeof(RenderPassBase).Assembly
                    .GetTypes()
                    .Single(type => type.Name == typeName);
                MethodInfo declaration = passType.GetMethod(nameof(RenderPassBase.DeclareResources))!;

                Assert.That(declaration.DeclaringType, Is.EqualTo(passType), $"{passName} must override DeclareResources.");
            }
        }
    }
}
