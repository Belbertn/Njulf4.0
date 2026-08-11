using System.Linq;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class RenderFeatureIsolationPolicyTests
    {
        [Test]
        public void FullFrame_AllowsProductionRenderPasses()
        {
            Assert.That(
                VulkanRenderer.ProductionRenderPassOrder.All(passName =>
                    RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.FullFrame, passName)),
                Is.True);
        }

        [Test]
        public void Geometry_SkipsFeaturePassesButKeepsPresentationPath()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "DirectionalShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SpotShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "PointShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "AmbientOcclusionPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "AmbientOcclusionBlurPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "FarFieldClipmapBakePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiTracePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiRelocateClassifyPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiDirectionalRadiancePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiTransportPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiBlendPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "SimpleDdgiPublishPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "GpuParticleResetPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "GpuParticleSimulatePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "GpuParticleSortPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "ParticlePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "FogPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "AutoExposurePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "BloomPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "ForwardPlusPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "ToneMapCompositePass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "AntiAliasingPass"), Is.True);
            });
        }

        [Test]
        public void PostProcessing_AllowsPostPassesAndSkipsUnrelatedFeaturePasses()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "AmbientOcclusionPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "AmbientOcclusionBlurPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "FarFieldClipmapBakePass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiTracePass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiRelocateClassifyPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiDirectionalRadiancePass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiTransportPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiBlendPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SimpleDdgiPublishPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "FogPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "AutoExposurePass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "BloomPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "DirectionalShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "SpotShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "PointShadowPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "GpuParticleResetPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "GpuParticleSimulatePass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "GpuParticleSortPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "ParticlePass"), Is.False);
            });
        }

        [Test]
        public void FeatureHelpers_AreModeScoped()
        {
            Assert.Multiple(() =>
            {
                Assert.That(RenderFeatureIsolationPolicy.AllowsShadows(RenderFeatureIsolationMode.Shadows), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.AllowsPostProcessing(RenderFeatureIsolationMode.PostProcessing), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.AllowsReflections(RenderFeatureIsolationMode.Reflections), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.AllowsAnimation(RenderFeatureIsolationMode.Animation), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.AllowsParticles(RenderFeatureIsolationMode.Particles), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.AllowsParticles(RenderFeatureIsolationMode.Geometry), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.AllowsAnimation(RenderFeatureIsolationMode.Geometry), Is.False);
            });
        }

        [Test]
        public void RenderSettings_QualityPresetPreservesFeatureIsolation()
        {
            var settings = new RenderSettings
            {
                FeatureIsolation = RenderFeatureIsolationMode.Particles
            };

            settings.ApplyQualityPreset(RenderQualityPreset.Low);

            Assert.That(settings.FeatureIsolation, Is.EqualTo(RenderFeatureIsolationMode.Particles));
        }

        [Test]
        public void SceneRenderingData_ClearResetsIsolationDiagnostics()
        {
            var sceneData = new SceneRenderingData
            {
                ActiveFeatureIsolation = RenderFeatureIsolationMode.Shadows,
                SkippedRenderPassCount = 4
            };

            sceneData.Clear();

            Assert.Multiple(() =>
            {
                Assert.That(sceneData.ActiveFeatureIsolation, Is.EqualTo(RenderFeatureIsolationMode.FullFrame));
                Assert.That(sceneData.SkippedRenderPassCount, Is.Zero);
            });
        }
    }
}
