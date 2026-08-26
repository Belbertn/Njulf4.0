using System.Linq;
using System.Reflection;
using Njulf.Rendering;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Pipeline.PipelineObjects;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldResidualCoordinatorTests
{
    [Test]
    public void Renderer_OwnsOnlyTheCoordinatorBoundaryForC5State()
    {
        FieldInfo[] rendererFields = typeof(VulkanRenderer).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Multiple(() =>
        {
            Assert.That(
                rendererFields.Count(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiNearFieldResidualCoordinator)),
                Is.EqualTo(1));
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiNearFieldResidualVulkanRuntime)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType ==
                    typeof(SimpleDdgiNearFieldResidualPlan)),
                Is.False);
            Assert.That(
                rendererFields.Any(field =>
                    field.FieldType.IsGenericType &&
                    field.FieldType.GetGenericTypeDefinition() ==
                    typeof(SimpleDdgiNearFieldResidualGenerationTransaction<>)),
                Is.False);
        });
    }

    [Test]
    public void InitializationRequest_ContainsFactsWithoutRendererEffects()
    {
        Type[] fieldTypes = typeof(NearFieldResidualInitializationRequest)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic |
                       BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(fieldTypes, Does.Not.Contain(typeof(VulkanRenderer)));
            Assert.That(fieldTypes,
                Does.Not.Contain(typeof(RenderTargetManager)));
            Assert.That(fieldTypes, Does.Not.Contain(typeof(MeshPipeline)));
            Assert.That(fieldTypes, Does.Not.Contain(typeof(RenderGraph)));
        });
    }

    [Test]
    public void DefaultPublicationAndGraphSnapshot_AreFailClosed()
    {
        NearFieldResidualPublication publication = default;
        NearFieldResidualGraphResourceSnapshot graph = default;

        Assert.Multiple(() =>
        {
            Assert.That(publication.Changed, Is.False);
            Assert.That(publication.Executable, Is.False);
            Assert.That(publication.DisableFeature, Is.False);
            Assert.That(graph.IsComplete, Is.False);
            Assert.That(graph.Runtime, Is.Null);
            Assert.That(
                SimpleDdgiNearFieldResidualCoordinator.ExperimentBudgetBytes,
                Is.EqualTo(96UL * 1024UL * 1024UL));
            Assert.That(
                SimpleDdgiNearFieldResidualCoordinator.HotSwapBudgetBytes,
                Is.EqualTo(192UL * 1024UL * 1024UL));
        });
    }
}