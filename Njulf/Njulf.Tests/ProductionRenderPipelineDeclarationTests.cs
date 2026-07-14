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
        "ForwardVisibilityCompactionPass",
        "SceneSurfacePass",
        "AmbientOcclusionPass",
        "AmbientOcclusionBlurPass",
        "TiledLightCullingPass",
        "ForwardPlusPass",
        "SsgiTracePass",
        "SsgiTemporalPass",
        "SsgiDenoisePass",
        "SsgiCompositePass",
        "FarFieldClipmapBakePass",
        "SimpleDdgiTracePass",
        "SimpleDdgiRelocateClassifyPass",
        "SimpleDdgiBlendPass",
        "DdgiSchedulePass",
        "DdgiTracePass",
        "DdgiBlendPass",
        "DdgiRelocateClassifyPass",
        "DdgiPublishPass",
        "SkyboxPass",
        "TransparentForwardPass",
        "WeightedTransparentPass",
        "WeightedOitCompositePass",
        "GpuParticleResetPass",
        "GpuParticleSimulatePass",
        "GpuParticleSortPass",
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
        string[] passOrder = declaration.PassOrder.ToArray();
        int forwardIndex = Array.IndexOf(passOrder, "ForwardPlusPass");
        int ddgiScheduleIndex = Array.IndexOf(passOrder, "DdgiSchedulePass");
        int ddgiPublishIndex = Array.IndexOf(passOrder, "DdgiPublishPass");
        int skyboxIndex = Array.IndexOf(passOrder, "SkyboxPass");

        Assert.Multiple(() =>
        {
            Assert.That(declaration.Name, Is.EqualTo("Production"));
            Assert.That(declaration.PassOrder, Is.EqualTo(ExpectedProductionPassOrder));
            Assert.That(VulkanRenderer.ProductionRenderPassOrder, Is.EqualTo(ExpectedProductionPassOrder));
            Assert.That(forwardIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(ddgiScheduleIndex, Is.GreaterThan(forwardIndex));
            Assert.That(ddgiPublishIndex, Is.GreaterThan(ddgiScheduleIndex));
            Assert.That(skyboxIndex, Is.GreaterThan(ddgiPublishIndex));
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
        RenderGraphResourceId[] explicitDdgiResources =
        [
            RenderGraphResourceId.SimpleDdgiParameters,
            RenderGraphResourceId.SimpleDdgiIrradianceAtlas,
            RenderGraphResourceId.SimpleDdgiVisibilityAtlas,
            RenderGraphResourceId.SimpleDdgiRayScratch,
            RenderGraphResourceId.SimpleDdgiProbeState,
            RenderGraphResourceId.SimpleDdgiUpdateQueue,
            RenderGraphResourceId.SimpleDdgiRelocationData,
            RenderGraphResourceId.FullDdgiScheduler,
            RenderGraphResourceId.FullDdgiRayResources,
            RenderGraphResourceId.FullDdgiAtlases,
            RenderGraphResourceId.FullDdgiState,
            RenderGraphResourceId.FullDdgiPublishResources
        ];
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
            Assert.That(graph.ResourceInventory, Has.Count.EqualTo(77));
            foreach (RenderGraphResourceId resource in explicitDdgiResources)
            {
                Assert.That(
                    graph.ResourceInventory,
                    Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(resource)
                        .And.Property(nameof(RenderGraphResourceDescriptor.Kind)).EqualTo(RenderGraphResourceKind.BufferSet),
                    resource.ToString());
            }
            Assert.That(
                graph.ResourceInventory,
                Has.Some.Property(nameof(RenderGraphResourceDescriptor.Id)).EqualTo(RenderGraphResourceId.SceneSubmissionBuffers));
            Assert.That(
                graph.PassResourceUsages["SceneOpaqueCompactionPass"],
                Has.Some.Property(nameof(RenderGraphResourceUsage.Resource)).EqualTo(RenderGraphResourceId.HiZPyramid)
                    .And.Property(nameof(RenderGraphResourceUsage.Access)).EqualTo(RenderGraphResourceAccess.Read)
                    .And.Property(nameof(RenderGraphResourceUsage.ImageLayout)).EqualTo(ImageLayout.ShaderReadOnlyOptimal));
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
            Assert.That(graph.ResourceInventory, Has.Count.EqualTo(65));
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
            Assert.That(diagnostics.AsyncComputeCandidatePassCount, Is.EqualTo(19));
            Assert.That(diagnostics.AsyncComputeEnabledPassCount, Is.EqualTo(0));
            Assert.That(
                diagnostics.Passes.Where(pass => pass.AsyncComputeCandidate).Select(pass => pass.Name),
                Is.EquivalentTo(new[]
                {
                    "HiZBuildPass",
                    "AmbientOcclusionBlurPass",
                    "FarFieldClipmapBakePass",
                    "SimpleDdgiTracePass",
                    "SimpleDdgiRelocateClassifyPass",
                    "SimpleDdgiBlendPass",
                    "DdgiSchedulePass",
                    "DdgiTracePass",
                    "DdgiBlendPass",
                    "DdgiRelocateClassifyPass",
                    "DdgiPublishPass",
                    "SsgiTracePass",
                    "SsgiTemporalPass",
                    "SsgiDenoisePass",
                    "FogPass",
                    "BloomPass",
                    "GpuParticleResetPass",
                    "GpuParticleSimulatePass",
                    "GpuParticleSortPass"
                }));
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
    public void AmbientOcclusionBlur_DeclaresItsComputeDepthSample()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        RenderGraphResourceUsage usage = declaration.PassResourceDeclarations
            .Single(pass => pass.PassName == "AmbientOcclusionBlurPass")
            .Usages
            .Single(candidate => candidate.Resource == RenderGraphResourceId.SceneDepth);

        Assert.Multiple(() =>
        {
            Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(usage.QueueIntent, Is.EqualTo(RenderGraphQueueIntent.Compute));
            Assert.That(usage.StageMask, Is.EqualTo(PipelineStageFlags2.ComputeShaderBit));
            Assert.That(usage.AccessMask, Is.EqualTo(AccessFlags2.ShaderSampledReadBit));
            Assert.That(usage.ImageLayout, Is.EqualTo(ImageLayout.DepthStencilReadOnlyOptimal));
        });
    }

    [Test]
    public void ForwardPlus_DeclaresTheConditionalDepthAttachmentContract()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        RenderGraphResourceUsage usage = declaration.PassResourceDeclarations
            .Single(pass => pass.PassName == "ForwardPlusPass")
            .Usages
            .Single(candidate => candidate.Resource == RenderGraphResourceId.SceneDepth);

        Assert.Multiple(() =>
        {
            Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.ReadWrite));
            Assert.That(usage.QueueIntent, Is.EqualTo(RenderGraphQueueIntent.Graphics));
            Assert.That(
                usage.StageMask,
                Is.EqualTo(
                    PipelineStageFlags2.EarlyFragmentTestsBit |
                    PipelineStageFlags2.LateFragmentTestsBit |
                    PipelineStageFlags2.FragmentShaderBit));
            Assert.That(
                usage.AccessMask,
                Is.EqualTo(
                    AccessFlags2.DepthStencilAttachmentReadBit |
                    AccessFlags2.DepthStencilAttachmentWriteBit |
                    AccessFlags2.ShaderSampledReadBit));
            Assert.That(usage.ImageLayout, Is.EqualTo(ImageLayout.Undefined));
        });
    }

    [Test]
    public void AsyncDdgiAndParticles_DeclareConcreteResourceContracts()
    {
        ProductionRenderPipelineDeclaration declaration = ProductionRenderPipelineDeclaration.Instance;
        RenderGraphResourceUsage[] simpleTrace = declaration.PassResourceDeclarations
            .Single(pass => pass.PassName == "SimpleDdgiTracePass")
            .Usages;
        RenderGraphResourceUsage[] particleSimulation = declaration.PassResourceDeclarations
            .Single(pass => pass.PassName == "GpuParticleSimulatePass")
            .Usages;
        RenderGraphResourceUsage[] particleSort = declaration.PassResourceDeclarations
            .Single(pass => pass.PassName == "GpuParticleSortPass")
            .Usages;
        string[] passOrder = declaration.PassOrder.ToArray();
        int resetIndex = Array.IndexOf(passOrder, "GpuParticleResetPass");

        Assert.Multiple(() =>
        {
            Assert.That(
                simpleTrace,
                Has.Some.Property(nameof(RenderGraphResourceUsage.Resource)).EqualTo(RenderGraphResourceId.TlasStorage)
                    .And.Property(nameof(RenderGraphResourceUsage.StageMask)).EqualTo(PipelineStageFlags2.ComputeShaderBit)
                    .And.Property(nameof(RenderGraphResourceUsage.AccessMask)).EqualTo(AccessFlags2.AccelerationStructureReadBitKhr));
            Assert.That(simpleTrace.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.MaterialTextures));
            Assert.That(simpleTrace.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.EnvironmentData));
            Assert.That(simpleTrace.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.RendererDiagnosticsBuffer));
            Assert.That(particleSimulation.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.ParticleBuffers));
            Assert.That(particleSimulation.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.GpuParticleEmitterData));
            Assert.That(particleSort.Select(usage => usage.Resource), Does.Contain(RenderGraphResourceId.GpuParticleCounterReadback));
            Assert.That(passOrder.Skip(resetIndex).Take(4), Is.EqualTo(new[]
            {
                "GpuParticleResetPass",
                "GpuParticleSimulatePass",
                "GpuParticleSortPass",
                "ParticlePass"
            }));
        });
    }

    [Test]
    public void ComputePassCatalog_ExplicitlyClassifiesProductionAndGraphicsQueueWork()
    {
        IReadOnlyList<AsyncComputePassAuditEntry> audit = AsyncComputePassCatalog.All;

        Assert.Multiple(() =>
        {
            Assert.That(audit.Select(entry => entry.PassName), Is.Unique);
            Assert.That(audit, Has.All.Property(nameof(AsyncComputePassAuditEntry.Producers)).Not.Empty);
            Assert.That(audit, Has.All.Property(nameof(AsyncComputePassAuditEntry.Consumers)).Not.Empty);
            Assert.That(audit, Has.All.Property(nameof(AsyncComputePassAuditEntry.Rationale)).Not.Empty);
            Assert.That(
                AsyncComputePassCatalog.ProductionCandidatePasses,
                Is.EquivalentTo(new[]
                {
                    "AmbientOcclusionBlurPass", "HiZBuildPass",
                    "SsgiTracePass", "SsgiTemporalPass", "SsgiDenoisePass",
                    "FarFieldClipmapBakePass",
                    "SimpleDdgiTracePass", "SimpleDdgiRelocateClassifyPass", "SimpleDdgiBlendPass",
                    "DdgiSchedulePass", "DdgiTracePass", "DdgiBlendPass", "DdgiRelocateClassifyPass", "DdgiPublishPass",
                    "FogPass", "BloomPass",
                    "GpuParticleResetPass", "GpuParticleSimulatePass", "GpuParticleSortPass"
                }));
            Assert.That(
                AsyncComputePassCatalog.GetClassification("TiledLightCullingPass"),
                Is.EqualTo(AsyncComputePassClassification.GraphicsQueueComputeByDesign));
            Assert.That(
                () => AsyncComputePassCatalog.GetClassification("UnknownComputePass"),
                Throws.InvalidOperationException);
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
            Assert.That(geometryPasses, Does.Not.Contain("DdgiSchedulePass"));
            Assert.That(geometryPasses, Does.Not.Contain("DdgiTracePass"));
            Assert.That(geometryPasses, Does.Not.Contain("DdgiBlendPass"));
            Assert.That(geometryPasses, Does.Not.Contain("DdgiRelocateClassifyPass"));
            Assert.That(geometryPasses, Does.Not.Contain("DdgiPublishPass"));
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
            "FarFieldClipmapBakePass" or
            "SimpleDdgiTracePass" or
            "SimpleDdgiRelocateClassifyPass" or
            "SimpleDdgiBlendPass" or
            "DdgiSchedulePass" or
            "DdgiTracePass" or
            "DdgiBlendPass" or
            "DdgiRelocateClassifyPass" or
            "DdgiPublishPass" or
            "SsgiTracePass" or
            "SsgiTemporalPass" or
            "SsgiDenoisePass" or
            "FogPass" or
            "BloomPass" or
            "GpuParticleResetPass" or
            "GpuParticleSimulatePass" or
            "GpuParticleSortPass";

        public override string AsyncComputeReason => SupportsAsyncCompute
            ? "Test pass is marked as an async compute candidate."
            : base.AsyncComputeReason;

        public override void Execute(CommandBuffer cmd, int frameIndex, SceneRenderingData sceneData)
        {
        }
    }
}
