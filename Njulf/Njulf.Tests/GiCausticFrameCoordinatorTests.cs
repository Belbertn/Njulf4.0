using System;
using System.Linq;
using System.Reflection;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticFrameCoordinatorTests
{
    [Test]
    public void Renderer_OwnsOnlyTheCoordinatorBoundaryForC4State()
    {
        FieldInfo[] rendererFields = typeof(VulkanRenderer).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Multiple(() =>
        {
            Assert.That(
                rendererFields.Count(field =>
                    field.FieldType == typeof(GiCausticFrameCoordinator)),
                Is.EqualTo(1));
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType == typeof(GiCausticVulkanRuntime)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(GiCausticTaggedTransportGpuProducer)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType == typeof(GiTaggedCausticCachePlan)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(GiExperimentModeState<GiCausticMode>)),
                Is.False);
        });
    }

    [Test]
    public void FrameRequest_ContainsFactsWithoutRendererOrSceneDataOwnership()
    {
        Type[] fieldTypes = typeof(GiCausticFrameRequest)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                       BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(fieldTypes, Does.Not.Contain(typeof(VulkanRenderer)));
            Assert.That(fieldTypes,
                Does.Not.Contain(typeof(SceneRenderingData)));
            Assert.That(fieldTypes,
                Does.Not.Contain(typeof(AccelerationStructureManager)));
            Assert.That(fieldTypes,
                Does.Contain(typeof(GiCausticHeroSourceSnapshot)));
            Assert.That(fieldTypes,
                Does.Contain(typeof(DdgiEmissiveTransportSnapshot)));
        });
    }

    [Test]
    public void DefaultGraphAndExtentSnapshots_AreFailClosed()
    {
        GiCausticGraphResourceSnapshot graph = default;
        GiCausticExtentTransition transition =
            GiCausticExtentTransition.Unchanged;

        Assert.Multiple(() =>
        {
            Assert.That(graph.IsComplete, Is.False);
            Assert.That(graph.Runtime, Is.Null);
            Assert.That(transition.Changed, Is.False);
            Assert.That(transition.Reason, Is.Empty);
            Assert.That(GiCausticFrameCoordinator.ExperimentBudgetBytes,
                Is.EqualTo(96UL * 1024UL * 1024UL));
        });
    }
}