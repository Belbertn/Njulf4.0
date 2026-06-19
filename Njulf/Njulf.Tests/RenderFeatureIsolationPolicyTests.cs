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
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "AmbientOcclusionPass"), Is.False);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, "ParticlePass"), Is.False);
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
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "FogPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "BloomPass"), Is.True);
                Assert.That(RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.PostProcessing, "DirectionalShadowPass"), Is.False);
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
