using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
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
        "SceneSurfacePass",
        "AmbientOcclusionPass",
        "AmbientOcclusionBlurPass",
        "TiledLightCullingPass",
        "ForwardPlusPass",
        "SsgiTracePass",
        "SsgiTemporalPass",
        "SsgiDenoisePass",
        "SsgiCompositePass",
        "DdgiUpdatePass",
        "DdgiRecursiveSnapshotPass",
        "SkyboxPass",
        "TransparentForwardPass",
        "WeightedTransparentPass",
        "WeightedOitCompositePass",
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
            Assert.That(graph.ResourceInventory, Has.Count.EqualTo(41));
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
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneNormal)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SceneNormalFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneMaterial)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SceneMaterialFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiTraceSource)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiTraceSourceFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiRaw)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiHitDistance)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiHitDistanceFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiFiltered)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiHistory)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.ImageChain)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiDepthHistory)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.ImageChain)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiDepthHistoryFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiNormalHistory)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.ImageChain)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiNormalHistoryFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiMoments)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.ImageChain)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiMomentsFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SsgiHistoryLength)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.ImageChain)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.SsgiHistoryLengthFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.GiFinalDiffuse)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.GiFinalDiffuseFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.DdgiProbeResources)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.BufferSet));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.WeightedOitAccumulation)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.WeightedOitAccumulationFormat));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.WeightedOitRevealage)
                    .And.Property(nameof(RenderGraphResourceDescriptor.Format)).EqualTo(RenderTargetManager.WeightedOitRevealageFormat));
        });
    }

    [Test]
    public void RegisterResourcesAndPasses_DdgiOnlyOmitsSsgiGraphResourcesAndPasses()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        var graph = new RenderGraph();
        var passInstances = declaration.PassOrder.ToDictionary(
            passName => passName,
            CreateUninitializedPass,
            StringComparer.Ordinal);

        declaration.RegisterResources(graph, Format.D32Sfloat, Format.B8G8R8A8Unorm, includeSsgi: false);
        declaration.DeclarePassResources(graph, includeSsgi: false);
        declaration.RegisterPasses(graph, passInstances, includeSsgi: false);

        string[] ssgiOnlyPasses =
        [
            "SceneSurfacePass",
            "SsgiTracePass",
            "SsgiTemporalPass",
            "SsgiDenoisePass",
            "SsgiCompositePass"
        ];
        RenderGraphResourceId[] ssgiOnlyResources =
        [
            RenderGraphResourceId.SceneNormal,
            RenderGraphResourceId.SceneMaterial,
            RenderGraphResourceId.SsgiTraceSource,
            RenderGraphResourceId.SsgiRaw,
            RenderGraphResourceId.SsgiHitDistance,
            RenderGraphResourceId.SsgiFiltered,
            RenderGraphResourceId.SsgiHistory,
            RenderGraphResourceId.SsgiDepthHistory,
            RenderGraphResourceId.SsgiNormalHistory,
            RenderGraphResourceId.SsgiMoments,
            RenderGraphResourceId.SsgiHistoryLength,
            RenderGraphResourceId.GiFinalDiffuse
        ];

        Assert.Multiple(() =>
        {
            Assert.That(graph.PassNames, Is.EqualTo(declaration.GetPassOrder(includeSsgi: false)));
            Assert.DoesNotThrow(() => declaration.ValidatePassOrder(graph.PassNames, includeSsgi: false));
            Assert.DoesNotThrow(graph.ValidateResourceDeclarations);
            Assert.That(graph.ResourceInventory, Has.Count.EqualTo(29));
            foreach (string passName in ssgiOnlyPasses)
                Assert.That(graph.PassNames, Does.Not.Contain(passName), passName);
            foreach (RenderGraphResourceId resource in ssgiOnlyResources)
            {
                Assert.That(
                    graph.ResourceInventory.Select(descriptor => descriptor.Id),
                    Does.Not.Contain(resource),
                    resource.ToString());
            }
            Assert.That(
                graph.PassResourceUsages["ForwardPlusPass"].Select(usage => usage.Resource),
                Does.Not.Contain(RenderGraphResourceId.SsgiTraceSource));
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.DdgiProbeResources));
        });
    }

    [Test]
    public void GraphDiagnostics_ReportAsyncComputeCandidatesAndQueueTransitions()
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

        RenderGraphDiagnostics diagnostics = graph.CreateDiagnostics(
            RenderFeatureIsolationMode.FullFrame,
            asyncComputeEnabled: false);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.AsyncComputeCandidatePassCount, Is.EqualTo(5));
            Assert.That(diagnostics.AsyncComputeEnabledPassCount, Is.EqualTo(0));
            Assert.That(
                diagnostics.Passes.Where(pass => pass.AsyncComputeCandidate).Select(pass => pass.Name),
                Is.EquivalentTo(new[] { "HiZBuildPass", "AmbientOcclusionBlurPass", "DdgiUpdatePass", "FogPass", "BloomPass" }));
            Assert.That(
                diagnostics.Passes.Single(pass => pass.Name == "BloomPass").QueueIntent,
                Is.EqualTo(RenderGraphQueueIntent.Compute.ToString()));
        });
    }

    [Test]
    public void SceneColorDynamicRenderingWriters_DeclareColorAttachmentLayout()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        string[] sceneColorAttachmentWriters =
        [
            "ForwardPlusPass",
            "SsgiCompositePass",
            "SkyboxPass",
            "TransparentForwardPass",
            "WeightedOitCompositePass",
            "ParticlePass",
            "DebugDrawPass"
        ];

        Assert.Multiple(() =>
        {
            foreach (string passName in sceneColorAttachmentWriters)
            {
                RenderGraphResourceUsage usage = declaration.PassResourceDeclarations
                    .Single(pass => pass.PassName == passName)
                    .Usages
                    .Single(usage => usage.Resource == RenderGraphResourceId.SceneColor);

                Assert.That(usage.ImageLayout, Is.EqualTo(ImageLayout.ColorAttachmentOptimal), passName);
                Assert.That(usage.StageMask, Is.EqualTo(PipelineStageFlags2.ColorAttachmentOutputBit), passName);
                Assert.That(
                    usage.AccessMask,
                    Is.EqualTo(AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.ColorAttachmentReadBit),
                    passName);
            }
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

        string[] geometryPasses = declaration.GetActivePasses(
            RenderFeatureIsolationMode.Geometry,
            TransparencyMode.SortedAlphaBlend).ToArray();
        string[] expectedGeometryPasses = declaration.PassOrder
            .Where(passName => RenderFeatureIsolationPolicy.ShouldExecutePass(RenderFeatureIsolationMode.Geometry, passName))
            .Where(passName => passName is not "WeightedTransparentPass" and not "WeightedOitCompositePass")
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(geometryPasses, Is.EqualTo(expectedGeometryPasses));
            Assert.That(geometryPasses, Does.Not.Contain("DirectionalShadowPass"));
            Assert.That(geometryPasses, Does.Not.Contain("AmbientOcclusionPass"));
            Assert.That(geometryPasses, Does.Not.Contain("SsgiTracePass"));
            Assert.That(geometryPasses, Does.Not.Contain("SsgiTemporalPass"));
            Assert.That(geometryPasses, Does.Not.Contain("SsgiDenoisePass"));
            Assert.That(geometryPasses, Does.Not.Contain("SsgiCompositePass"));
            Assert.That(geometryPasses, Does.Not.Contain("DdgiUpdatePass"));
            Assert.That(geometryPasses, Does.Not.Contain("ParticlePass"));
            Assert.That(geometryPasses, Does.Not.Contain("WeightedTransparentPass"));
            Assert.That(geometryPasses, Does.Not.Contain("WeightedOitCompositePass"));
            Assert.That(geometryPasses, Does.Contain("ForwardPlusPass"));
            Assert.That(geometryPasses, Does.Contain("TransparentForwardPass"));
            Assert.That(geometryPasses, Does.Contain("ToneMapCompositePass"));
            Assert.That(geometryPasses, Does.Contain("AntiAliasingPass"));
            Assert.That(
                declaration.GetActivePasses(RenderFeatureIsolationMode.FullFrame, TransparencyMode.SortedAlphaBlend),
                Is.EqualTo(declaration.PassOrder.Where(passName => passName is not "WeightedTransparentPass" and not "WeightedOitCompositePass")));
            Assert.That(
                declaration.GetActivePasses(RenderFeatureIsolationMode.FullFrame, TransparencyMode.WeightedBlendedOit),
                Is.EqualTo(declaration.PassOrder.Where(passName => passName != "TransparentForwardPass")));
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

        public override RenderGraphQueueIntent QueueIntent => SupportsAsyncCompute
            ? RenderGraphQueueIntent.Compute
            : RenderGraphQueueIntent.Graphics;

        public override bool SupportsAsyncCompute => Name is
            "HiZBuildPass" or
            "AmbientOcclusionBlurPass" or
            "DdgiUpdatePass" or
            "FogPass" or
            "BloomPass";

        public override string AsyncComputeReason => SupportsAsyncCompute
            ? "Test pass is marked as an async compute candidate."
            : base.AsyncComputeReason;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
        }
    }
}
