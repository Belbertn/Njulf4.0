using System;
using System.Linq;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests
{
    [TestFixture]
    public class RenderGraphResourceInventoryTests
    {
        [Test]
        public void ProductionPassOrder_RemainsStableForArchitectureMigration()
        {
            string[] expected =
            [
                "DirectionalShadowPass",
                "SpotShadowPass",
                "PointShadowPass",
                "DepthPrePass",
                "MotionVectorPass",
                "HiZBuildPass",
                "AmbientOcclusionPass",
                "AmbientOcclusionBlurPass",
                "TiledLightCullingPass",
                "ForwardPlusPass",
                "SkyboxPass",
                "TransparentForwardPass",
                "ParticlePass",
                "DebugDrawPass",
                "FogPass",
                "AutoExposurePass",
                "BloomPass",
                "ToneMapCompositePass",
                "AntiAliasingPass"
            ];

            Assert.That(ProductionRenderPipeline.PassOrder, Is.EqualTo(expected));
            Assert.DoesNotThrow(() => ProductionRenderPipeline.ValidatePassOrder(expected));
        }

        [Test]
        public void ProductionPassOrderValidation_ReportsPassNamesOnMismatch()
        {
            string[] invalid = ProductionRenderPipeline.PassOrder.ToArray();
            (invalid[3], invalid[4]) = (invalid[4], invalid[3]);

            var ex = Assert.Throws<InvalidOperationException>(() => ProductionRenderPipeline.ValidatePassOrder(invalid));

            Assert.That(ex!.Message, Does.Contain("DepthPrePass"));
            Assert.That(ex.Message, Does.Contain("MotionVectorPass"));
        }

        [Test]
        public void ProductionFrameInventory_ListsCurrentImageResources()
        {
            var settings = new RenderSettings();
            settings.AntiAliasing.Mode = AntiAliasingMode.Taa;
            settings.Shadows.SpotShadowsEnabled = true;
            settings.Shadows.MaxShadowedSpotLights = 2;
            settings.Shadows.PointShadowsEnabled = true;
            settings.Shadows.MaxShadowedPointLights = 1;

            var snapshot = RenderGraphResourceInventoryBuilder.BuildProductionFrame(
                new Extent2D { Width = 1920, Height = 1080 },
                Format.D32Sfloat,
                settings,
                swapchainImageCount: 3,
                swapchainFormat: Format.B8G8R8A8Srgb);

            string[] requiredImages =
            [
                "Swapchain Color",
                "HDR Scene Color",
                "Scene Depth",
                "Fogged HDR Scene Color",
                "Ambient Occlusion Raw",
                "Ambient Occlusion Blurred",
                "Ambient Occlusion Scratch",
                "LDR Scene Color",
                "SMAA Edges",
                "SMAA Blend Weights",
                "Motion Vectors",
                "TAA History A",
                "TAA History B",
                "Bloom Extract",
                "Directional Shadow Map Array",
                "Spot Shadow Atlas",
                "Point Shadow Cubemap Array",
                "Environment Cubemap",
                "Irradiance Cubemap",
                "Prefiltered Environment Cubemap",
                "BRDF LUT",
                "Reflection Probe Cubemap Array",
                "Hi-Z Depth Pyramid"
            ];

            Assert.Multiple(() =>
            {
                foreach (string imageName in requiredImages)
                    Assert.That(snapshot.Images.Select(image => image.Name), Does.Contain(imageName));

                Assert.That(snapshot.PassOrder, Is.EqualTo(ProductionRenderPipeline.PassOrder));
                Assert.That(snapshot.EstimatedImageBytes, Is.GreaterThan(0));
                Assert.That(snapshot.Images.Single(image => image.Name == "HDR Scene Color").Producers, Does.Contain("ForwardPlusPass"));
                Assert.That(snapshot.Images.Single(image => image.Name == "Scene Depth").Consumers, Does.Contain("HiZBuildPass"));
                Assert.That(snapshot.Images.Single(image => image.Name == "TAA History A").Persistence, Is.EqualTo("history"));
            });
        }

        [Test]
        public void ProductionFrameInventory_ListsCrossPassBuffers()
        {
            var sceneData = new SceneRenderingData
            {
                ObjectBufferSize = 64 * 1024,
                MaterialBufferSize = 128 * 1024,
                MeshletDrawBufferSize = 256 * 1024,
                TiledLightHeaderBufferSize = 8 * 1024,
                TiledLightIndexBufferSize = 32 * 1024,
                ParticleInstanceBufferSize = 16 * 1024,
                SkinMatrixBufferSize = 4 * 1024
            };

            var snapshot = RenderGraphResourceInventoryBuilder.BuildProductionFrame(
                new Extent2D { Width = 1280, Height = 720 },
                Format.D32Sfloat,
                new RenderSettings(),
                sceneData);

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Buffers.Select(buffer => buffer.Name), Does.Contain("Object Data Buffer"));
                Assert.That(snapshot.Buffers.Select(buffer => buffer.Name), Does.Contain("Tiled Light Index Buffer"));
                Assert.That(snapshot.Buffers.Select(buffer => buffer.Name), Does.Contain("Renderer Diagnostics Buffer"));
                Assert.That(snapshot.Buffers.Single(buffer => buffer.Name == "Object Data Buffer").Consumers, Does.Contain("ForwardPlusPass"));
                Assert.That(snapshot.Buffers.Single(buffer => buffer.Name == "Tiled Light Header Buffer").Producers, Does.Contain("TiledLightCullingPass"));
                Assert.That(snapshot.EstimatedBufferBytes, Is.GreaterThan(0));
            });
        }
    }
}
