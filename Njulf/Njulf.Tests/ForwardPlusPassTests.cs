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
    }
}
