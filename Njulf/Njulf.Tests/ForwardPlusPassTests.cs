using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests
{
    public sealed class ForwardPlusPassTests
    {
        [Test]
        public void ResolveOpaqueVariantSelection_UsesSimpleGlobalIblWhenNoLocalProbesAreActive()
        {
            var sceneData = new SceneRenderingData
            {
                SimpleOpaqueMeshletCount = 20,
                SimpleNormalOpaqueMeshletCount = 7,
                FullOpaqueMeshletCount = 5,
                ReflectionsEnabled = true,
                ReflectionMode = ReflectionMode.StaticProbes,
                ReflectionProbeCount = 0
            };

            var selection = ForwardPlusPass.ResolveOpaqueVariantSelection(sceneData);

            Assert.Multiple(() =>
            {
                Assert.That(selection.UseSimpleGlobalIblPipeline, Is.True);
                Assert.That(selection.SimpleMeshletCount, Is.EqualTo(27));
                Assert.That(selection.FullMaterialMeshletCount, Is.EqualTo(5));
                Assert.That(selection.LocalProbeMeshletCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void ResolveOpaqueVariantSelection_ForcesFullMaterialWhenLocalProbesCanInfluenceOpaquePixels()
        {
            var sceneData = new SceneRenderingData
            {
                SimpleOpaqueMeshletCount = 20,
                SimpleNormalOpaqueMeshletCount = 7,
                FullOpaqueMeshletCount = 5,
                ReflectionsEnabled = true,
                ReflectionMode = ReflectionMode.StaticProbes,
                ReflectionProbeCount = 2
            };

            var selection = ForwardPlusPass.ResolveOpaqueVariantSelection(sceneData);

            Assert.Multiple(() =>
            {
                Assert.That(selection.UseSimpleGlobalIblPipeline, Is.False);
                Assert.That(selection.SimpleMeshletCount, Is.EqualTo(0));
                Assert.That(selection.FullMaterialMeshletCount, Is.EqualTo(32));
                Assert.That(selection.LocalProbeMeshletCount, Is.EqualTo(32));
            });
        }

        [Test]
        public void ResolveOpaqueVariantSelection_ForcesFullMaterialForReflectionDebugViews()
        {
            var sceneData = new SceneRenderingData
            {
                SimpleOpaqueMeshletCount = 20,
                SimpleNormalOpaqueMeshletCount = 7,
                FullOpaqueMeshletCount = 5,
                ReflectionsEnabled = true,
                ReflectionMode = ReflectionMode.GlobalEnvironmentOnly,
                ReflectionProbeCount = 0,
                ReflectionDebugView = ReflectionDebugView.ProbeInfluence
            };

            var selection = ForwardPlusPass.ResolveOpaqueVariantSelection(sceneData);

            Assert.Multiple(() =>
            {
                Assert.That(selection.UseSimpleGlobalIblPipeline, Is.False);
                Assert.That(selection.SimpleMeshletCount, Is.EqualTo(0));
                Assert.That(selection.FullMaterialMeshletCount, Is.EqualTo(32));
                Assert.That(selection.LocalProbeMeshletCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void ShouldApplyGlobalIllumination_AllowsDdgiWithoutDepthPrePassWhenConfigured()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiAllowForwardWithoutDepthPrePass = true
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: false, ddgiProbeCount: 16);

            Assert.Multiple(() =>
            {
                Assert.That(ForwardPlusPass.ShouldApplyDdgi(sceneData, settings), Is.True);
                Assert.That(ForwardPlusPass.ShouldApplySsgi(sceneData, settings), Is.False);
                Assert.That(ForwardPlusPass.ShouldApplyGlobalIllumination(sceneData, settings), Is.True);
            });
        }

        [Test]
        public void ShouldApplyGlobalIllumination_BlocksDdgiWithoutDepthPrePassWhenConfigured()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiAllowForwardWithoutDepthPrePass = false
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: false, ddgiProbeCount: 16);

            Assert.Multiple(() =>
            {
                Assert.That(ForwardPlusPass.ShouldApplyDdgi(sceneData, settings), Is.False);
                Assert.That(ForwardPlusPass.ShouldApplyGlobalIllumination(sceneData, settings), Is.False);
            });
        }

        [Test]
        public void ShouldApplyGlobalIllumination_KeepsSsgiDepthPrePassRequirement()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ssgi,
                UseSsgi = true
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: false, ddgiProbeCount: 0);

            Assert.Multiple(() =>
            {
                Assert.That(ForwardPlusPass.ShouldApplySsgi(sceneData, settings), Is.False);
                Assert.That(ForwardPlusPass.ShouldApplyGlobalIllumination(sceneData, settings), Is.False);
            });
        }

        [Test]
        public void ShouldApplyGlobalIllumination_BlocksGiDuringAnimationDebugView()
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiAllowForwardWithoutDepthPrePass = true
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: false, ddgiProbeCount: 16);
            sceneData.AnimationDebugView = AnimationDebugView.SkinnedObjects;

            Assert.That(ForwardPlusPass.ShouldApplyGlobalIllumination(sceneData, settings), Is.False);
        }

        private static SceneRenderingData CreateGiScene(bool depthPrePassEnabled, int ddgiProbeCount)
        {
            return new SceneRenderingData
            {
                DepthPrePassEnabled = depthPrePassEnabled,
                DdgiProbeCount = ddgiProbeCount,
                ActiveFeatureIsolation = RenderFeatureIsolationMode.FullFrame
            };
        }
    }
}
