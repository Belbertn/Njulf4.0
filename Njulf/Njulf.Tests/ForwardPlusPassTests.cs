using System;
using System.Runtime.CompilerServices;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;

namespace Njulf.Tests
{
    public sealed class ForwardPlusPassTests
    {
        [Test]
        public void EnableDepthPrePass_RejectsTheUnsupportedDisabledConfiguration()
        {
            var renderer = (VulkanRenderer)RuntimeHelpers.GetUninitializedObject(typeof(VulkanRenderer));

#pragma warning disable CS0618 // Verifies the intentional compatibility migration contract.
            Assert.Multiple(() =>
            {
                Assert.That(renderer.EnableDepthPrePass, Is.True);
                Assert.That(
                    () => renderer.EnableDepthPrePass = false,
                    Throws.TypeOf<NotSupportedException>());
            });
#pragma warning restore CS0618
        }

        [Test]
        public void DepthPrePassProvenance_RequiresCompletionFromTheCurrentFrame()
        {
            var sceneData = new SceneRenderingData
            {
                DdgiFrameSerial = 42,
                DepthPrePassCompleted = true,
                DepthPrePassFrameSerial = 41
            };

            Assert.That(sceneData.HasCurrentDepthPrePass, Is.False);

            sceneData.DepthPrePassFrameSerial = sceneData.DdgiFrameSerial;

            Assert.That(sceneData.HasCurrentDepthPrePass, Is.True);
        }

        [Test]
        public void TiledLightCullingProvenance_RequiresCompletionFromTheCurrentFrame()
        {
            var sceneData = new SceneRenderingData
            {
                DdgiFrameSerial = 42,
                TiledLightCullingCompleted = true,
                TiledLightCullingFrameSerial = 41
            };

            Assert.That(sceneData.HasCurrentTiledLightCulling, Is.False);

            sceneData.TiledLightCullingFrameSerial = sceneData.DdgiFrameSerial;

            Assert.That(sceneData.HasCurrentTiledLightCulling, Is.True);
        }

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

        [TestCase(true)]
        [TestCase(false)]
        public void ShouldApplyGlobalIllumination_RequiresDepthPrePassRegardlessOfLegacyOverride(
            bool legacyOverride)
        {
            var settings = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DdgiAllowForwardWithoutDepthPrePass = legacyOverride
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: false, ddgiProbeCount: 16);

            Assert.Multiple(() =>
            {
                Assert.That(ForwardPlusPass.ShouldApplyDdgi(sceneData, settings), Is.False);
                Assert.That(ForwardPlusPass.ShouldApplySsgi(sceneData, settings), Is.False);
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
                UseDdgi = true
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: true, ddgiProbeCount: 16);
            sceneData.AnimationDebugView = AnimationDebugView.SkinnedObjects;

            Assert.That(ForwardPlusPass.ShouldApplyGlobalIllumination(sceneData, settings), Is.False);
        }

        [Test]
        public void ShouldCollectDdgiForwardEstimateCounters_DoesNotEnableForDebugViewAlone()
        {
            var gi = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DebugView = GlobalIlluminationDebugView.DdgiEffectiveWeight
            };
            var diagnostics = new RenderDiagnosticsSettings
            {
                DdgiForwardEstimateCountersEnabled = false
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: true, ddgiProbeCount: 64);

            bool collect = ForwardPlusPass.ShouldCollectDdgiForwardEstimateCounters(sceneData, gi, diagnostics);

            Assert.That(collect, Is.False);
        }

        [Test]
        public void ShouldCollectDdgiClipmapCoverageCounters_EnablesForGatherDebugView()
        {
            var gi = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DebugView = GlobalIlluminationDebugView.DdgiGatherBlendWeight
            };
            var diagnostics = new RenderDiagnosticsSettings
            {
                DdgiForwardEstimateCountersEnabled = false
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: true, ddgiProbeCount: 64);

            bool collect = ForwardPlusPass.ShouldCollectDdgiClipmapCoverageCounters(sceneData, gi, diagnostics);

            Assert.That(collect, Is.True);
        }

        [Test]
        public void ShouldCollectDdgiClipmapCoverageCounters_DoesNotEnableForNonGatherDebugView()
        {
            var gi = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DebugView = GlobalIlluminationDebugView.DdgiEffectiveWeight
            };
            var diagnostics = new RenderDiagnosticsSettings
            {
                DdgiForwardEstimateCountersEnabled = false
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: true, ddgiProbeCount: 64);

            bool collect = ForwardPlusPass.ShouldCollectDdgiClipmapCoverageCounters(sceneData, gi, diagnostics);

            Assert.That(collect, Is.False);
        }

        [Test]
        public void ShouldCollectDdgiForwardEstimateCounters_UsesExplicitDiagnosticsToggle()
        {
            var gi = new GlobalIlluminationSettings
            {
                Enabled = true,
                Mode = GlobalIlluminationMode.Ddgi,
                UseDdgi = true,
                DebugView = GlobalIlluminationDebugView.None
            };
            var diagnostics = new RenderDiagnosticsSettings
            {
                DdgiForwardEstimateCountersEnabled = true
            };
            var sceneData = CreateGiScene(depthPrePassEnabled: true, ddgiProbeCount: 64);

            bool collect = ForwardPlusPass.ShouldCollectDdgiForwardEstimateCounters(sceneData, gi, diagnostics);

            Assert.That(collect, Is.True);
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
