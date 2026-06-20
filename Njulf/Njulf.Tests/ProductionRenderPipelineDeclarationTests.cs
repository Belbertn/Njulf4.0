using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Pipeline;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProductionRenderPipelineDeclarationTests
{
    private static readonly string[] ExpectedProductionPassOrder =
    [
        "SceneOpaqueCompactionPass",
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

    [Test]
    public void PassOrder_MatchesCurrentRendererCompatibilityOrder()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;

        Assert.Multiple(() =>
        {
            Assert.That(declaration.Name, Is.EqualTo("Production"));
            Assert.That(declaration.PassOrder, Is.EqualTo(ExpectedProductionPassOrder));
            Assert.That(VulkanRenderer.ProductionRenderPassOrder, Is.EqualTo(ExpectedProductionPassOrder));
            Assert.That(VulkanRenderer.PhaseOneRenderPassOrder, Is.EqualTo(ExpectedProductionPassOrder));
        });
    }

    [Test]
    public void PassResourceDeclarations_CoverDeclaredPassesInOrder()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;

        Assert.Multiple(() =>
        {
            Assert.That(
                declaration.PassResourceDeclarations.Select(pass => pass.PassName),
                Is.EqualTo(declaration.PassOrder));
            Assert.That(
                declaration.PassResourceDeclarations,
                Has.All.Property(nameof(RenderGraphPassResourceDeclaration.Usages)).Not.Empty);
        });
    }

    [Test]
    public void RegisterResourcesAndPasses_ProducesValidGraphContract()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        var graph = new RenderGraph();
        var passInstances = declaration.PassOrder.ToDictionary(
            passName => passName,
            CreateUninitializedPass,
            StringComparer.Ordinal);

        declaration.RegisterResources(graph, Format.D32Sfloat, Format.B8G8R8A8Unorm);
        declaration.DeclarePassResources(graph);
        declaration.RegisterPasses(graph, passInstances);

        Assert.Multiple(() =>
        {
            Assert.That(graph.PassNames, Is.EqualTo(declaration.PassOrder));
            Assert.DoesNotThrow(() => declaration.ValidatePassOrder(graph.PassNames));
            Assert.DoesNotThrow(graph.ValidateResourceDeclarations);
            Assert.That(graph.ResourceInventory, Has.Count.EqualTo(26));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneSubmissionBuffers));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SwapchainColor)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(Format.B8G8R8A8Unorm));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneDepth)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(Format.D32Sfloat));
        });
    }

    [Test]
    public void RegisterPasses_RejectsMissingProductionPass()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        var graph = new RenderGraph();
        var passInstances = declaration.PassOrder
            .Where(passName => !string.Equals(passName, "BloomPass", StringComparison.Ordinal))
            .ToDictionary(passName => passName, CreateUninitializedPass, StringComparer.Ordinal);

        Assert.That(
            () => declaration.RegisterPasses(graph, passInstances),
            Throws.InvalidOperationException.With.Message.Contains("BloomPass"));
    }

    [Test]
    public void ValidatePassOrder_RejectsReorderedOrMissingPasses()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        string[] reordered = declaration.PassOrder.ToArray();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => declaration.ValidatePassOrder(reordered),
                Throws.InvalidOperationException.With.Message.Contains("pass order changed"));
            Assert.That(
                () => declaration.ValidatePassOrder(declaration.PassOrder.Skip(1).ToArray()),
                Throws.InvalidOperationException.With.Message.Contains("pass count changed"));
        });
    }

    [Test]
    public void ActivePasses_ApplyFeatureIsolationWithoutChangingRelativeOrder()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;

        string[] geometryPasses = declaration.GetActivePasses(RenderFeatureIsolationMode.Geometry).ToArray();
        string[] expectedGeometryPasses = declaration.PassOrder
            .Where(passName => RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, passName))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(geometryPasses, Is.EqualTo(expectedGeometryPasses));
            Assert.That(geometryPasses, Does.Not.Contain("DirectionalShadowPass"));
            Assert.That(geometryPasses, Does.Not.Contain("AmbientOcclusionPass"));
            Assert.That(geometryPasses, Does.Not.Contain("ParticlePass"));
            Assert.That(geometryPasses, Does.Contain("ForwardPlusPass"));
            Assert.That(geometryPasses, Does.Contain("ToneMapCompositePass"));
            Assert.That(geometryPasses, Does.Contain("AntiAliasingPass"));
            Assert.That(
                declaration.GetActivePasses(RenderFeatureIsolationMode.FullFrame),
                Is.EqualTo(declaration.PassOrder));
        });
    }

    private static RenderPassBase CreateUninitializedPass(string name)
    {
        var pass = (NamedTestPass)RuntimeHelpers.GetUninitializedObject(typeof(NamedTestPass));
        FieldInfo field = typeof(RenderPassBase).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RenderPassBase.Name backing field was not found.");
        field.SetValue(pass, name);
        return pass;
    }

    private sealed class NamedTestPass : RenderPassBase
    {
        private NamedTestPass()
            : base("unused", null!, null!, null!)
        {
        }

        public override void Initialize()
        {
        }

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
        }
    }
}
