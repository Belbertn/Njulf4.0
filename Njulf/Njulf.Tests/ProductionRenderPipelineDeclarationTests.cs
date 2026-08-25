using System.IO;
using System.Linq;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Data;
using Silk.NET.Vulkan;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ProductionRenderPipelineDeclarationTests
{
    [Test]
    public void ProductionDeclaration_ContainsSimpleDdgi()
    {
        string source = ReadRepoText("Njulf.Rendering", "Pipeline", "ProductionRenderPipelineDeclaration.cs");

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("SimpleDdgi"));
        });
    }

    [Test]
    public void SparseResidencyTransaction_HasRequiredOrderAndGraphicsQueueClassification()
    {
        var order = ProductionRenderPipelineDeclaration.Instance.PassOrder.ToList();
        int forward = order.IndexOf("ForwardPlusPass");
        int demand = order.IndexOf("SimpleDdgiPageDemandPass");
        int residency = order.IndexOf("SimpleDdgiPageResidencyPass");
        int schedule = order.IndexOf("SimpleDdgiSchedulePass");
        int commit = order.IndexOf("SimpleDdgiSchedulerCommitPass");
        int feedback = order.IndexOf("SimpleDdgiPageFeedbackPass");
        int transparent = order.IndexOf("TransparentForwardPass");

        Assert.Multiple(() =>
        {
            Assert.That(demand, Is.GreaterThan(forward));
            Assert.That(residency, Is.GreaterThan(demand));
            Assert.That(schedule, Is.GreaterThan(residency));
            Assert.That(commit, Is.GreaterThan(schedule));
            Assert.That(feedback, Is.GreaterThan(commit));
            Assert.That(transparent, Is.GreaterThan(feedback));
            Assert.That(
                AsyncComputePassCatalog.GetClassification("SimpleDdgiPageDemandPass"),
                Is.EqualTo(AsyncComputePassClassification.GraphicsQueueComputeByDesign));
            Assert.That(
                AsyncComputePassCatalog.GetClassification("SimpleDdgiPageResidencyPass"),
                Is.EqualTo(AsyncComputePassClassification.GraphicsQueueComputeByDesign));
            Assert.That(
                AsyncComputePassCatalog.GetClassification("SimpleDdgiPageFeedbackPass"),
                Is.EqualTo(AsyncComputePassClassification.GraphicsQueueComputeByDesign));
        });
    }

    [Test]
    public void UrgentRelightLane_IsImmediatelyPreForwardAndHasNoRaySceneDependency()
    {
        var order = ProductionRenderPipelineDeclaration.Instance.PassOrder.ToList();
        int environmentPrefilter = order.IndexOf("EnvironmentPrefilterPass");
        int urgent = order.IndexOf("SimpleDdgiUrgentRelightPass");
        int forward = order.IndexOf("ForwardPlusPass");
        int ordinarySchedule = order.IndexOf("SimpleDdgiSchedulePass");
        RenderGraphPassResourceDeclaration declaration =
            ProductionRenderPipelineDeclaration.Instance
                .CreatePassResourceDeclarations()
                .Single(candidate =>
                    candidate.PassName == "SimpleDdgiUrgentRelightPass");
        RenderGraphResourceId[] forbiddenRaySceneResources =
        [
            RenderGraphResourceId.TlasStorage,
            RenderGraphResourceId.RayQueryInstanceMetadata,
            RenderGraphResourceId.MeshGeometryBuffers,
            RenderGraphResourceId.MaterialBuffers,
            RenderGraphResourceId.MaterialTextures,
            RenderGraphResourceId.FarFieldParameters,
            RenderGraphResourceId.FarFieldVoxels,
            RenderGraphResourceId.FarFieldInstances,
            RenderGraphResourceId.FarFieldJumpFlood,
            RenderGraphResourceId.FarFieldPageTable
        ];

        Assert.Multiple(() =>
        {
            Assert.That(urgent, Is.EqualTo(environmentPrefilter + 1));
            Assert.That(forward, Is.EqualTo(urgent + 1));
            Assert.That(ordinarySchedule, Is.GreaterThan(forward));
            Assert.That(
                declaration.Usages.Where(usage =>
                    forbiddenRaySceneResources.Contains(usage.Resource)),
                Is.Empty);
        });
    }

    [Test]
    public void DirectionalRayShadowMask_IsAfterDepthAndBeforeForwardWithExplicitDependencies()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var order = production.PassOrder.ToList();
        var declarations = production.CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        int depth = order.IndexOf("DepthPrePass");
        int ray = order.IndexOf("DirectionalRayShadowPass");
        int forward = order.IndexOf("ForwardPlusPass");
        RenderGraphPassResourceDeclaration rayDeclaration =
            declarations["DirectionalRayShadowPass"];
        RenderGraphResourceUsage maskWrite = rayDeclaration.Usages.Single(
            usage => usage.Resource ==
                RenderGraphResourceId.DirectionalRayShadowMask);
        RenderGraphResourceUsage maskRead = declarations["ForwardPlusPass"]
            .Usages.Single(usage => usage.Resource ==
                RenderGraphResourceId.DirectionalRayShadowMask);

        Assert.Multiple(() =>
        {
            Assert.That(ray, Is.GreaterThan(depth));
            Assert.That(ray, Is.LessThan(forward));
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId.SceneDepth), Is.True);
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId.TlasStorage), Is.True);
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId
                    .RayQueryInstanceMetadata), Is.True);
            Assert.That(maskWrite.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(maskWrite.StageMask &
                PipelineStageFlags2.ComputeShaderBit, Is.Not.Zero);
            Assert.That(maskWrite.StageMask &
                PipelineStageFlags2.TransferBit, Is.Not.Zero);
            Assert.That(maskWrite.AccessMask &
                AccessFlags2.ShaderStorageWriteBit, Is.Not.Zero);
            Assert.That(maskWrite.AccessMask &
                AccessFlags2.TransferWriteBit, Is.Not.Zero);
            Assert.That(maskRead.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(maskRead.StageMask &
                PipelineStageFlags2.FragmentShaderBit, Is.Not.Zero);
            Assert.That(AsyncComputePassCatalog.GetClassification(
                    "DirectionalRayShadowPass"),
                Is.EqualTo(
                    AsyncComputePassClassification.GraphicsQueueComputeByDesign));
        });
    }

    [Test]
    public void AreaRayShadowMask_IsAfterDepthAndBeforeForwardWithExplicitDependencies()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var order = production.PassOrder.ToList();
        var declarations = production.CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        int depth = order.IndexOf("DepthPrePass");
        int ray = order.IndexOf("AreaRayShadowPass");
        int forward = order.IndexOf("ForwardPlusPass");
        RenderGraphPassResourceDeclaration rayDeclaration =
            declarations["AreaRayShadowPass"];
        RenderGraphResourceUsage maskWrite = rayDeclaration.Usages.Single(
            usage => usage.Resource == RenderGraphResourceId.AreaRayShadowMask);
        RenderGraphResourceUsage maskRead = declarations["ForwardPlusPass"]
            .Usages.Single(usage =>
                usage.Resource == RenderGraphResourceId.AreaRayShadowMask);

        Assert.Multiple(() =>
        {
            Assert.That(ray, Is.GreaterThan(depth));
            Assert.That(ray, Is.LessThan(forward));
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId.SceneDepth), Is.True);
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId.TlasStorage), Is.True);
            Assert.That(rayDeclaration.Usages.Any(usage =>
                usage.Resource == RenderGraphResourceId.LightBuffers), Is.True);
            Assert.That(maskWrite.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(maskWrite.StageMask & PipelineStageFlags2.ComputeShaderBit,
                Is.Not.Zero);
            Assert.That(maskWrite.StageMask & PipelineStageFlags2.TransferBit,
                Is.Not.Zero);
            Assert.That(maskRead.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(maskRead.StageMask & PipelineStageFlags2.FragmentShaderBit,
                Is.Not.Zero);
            Assert.That(AsyncComputePassCatalog.GetClassification(
                    "AreaRayShadowPass"),
                Is.EqualTo(
                    AsyncComputePassClassification.GraphicsQueueComputeByDesign));
        });
    }

    [Test]
    public void ContentDependentPrelude_DeclaresComputeToAsAndTreeToTraceEdges()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var prelude = production.ExternallyRecordedPassResourceDeclarations
            .ToList();
        var declarations = production.CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        var order = production.PassOrder.ToList();

        RenderGraphPassResourceDeclaration foliage = prelude.Single(
            declaration =>
                declaration.PassName == "DdgiFoliageProxyGenerationPass");
        RenderGraphPassResourceDeclaration blas = prelude.Single(
            declaration =>
                declaration.PassName == "AccelerationStructureBlasPass");
        RenderGraphResourceUsage generatedGeometry = foliage.Usages.Single(
            usage => usage.Resource ==
                RenderGraphResourceId.DdgiFoliageProxyGeometry);
        RenderGraphResourceUsage blasInput = blas.Usages.Single(
            usage => usage.Resource ==
                RenderGraphResourceId.DdgiFoliageProxyGeometry);
        RenderGraphResourceUsage treeWrite =
            declarations["SimpleDdgiLightTreePass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.SimpleDdgiLightTree);
        RenderGraphResourceUsage traceRead =
            declarations["SimpleDdgiTracePass"].Usages.Single(
                usage => usage.Resource ==
                    RenderGraphResourceId.SimpleDdgiLightTree);

        Assert.Multiple(() =>
        {
            Assert.That(prelude.FindIndex(candidate =>
                    candidate.PassName == "SkinningPass"),
                Is.LessThan(prelude.FindIndex(candidate =>
                    candidate.PassName == "DdgiFoliageProxyGenerationPass")));
            Assert.That(prelude.FindIndex(candidate =>
                    candidate.PassName == "DdgiFoliageProxyGenerationPass"),
                Is.LessThan(prelude.FindIndex(candidate =>
                    candidate.PassName == "AccelerationStructureBlasPass")));
            Assert.That(generatedGeometry.Access,
                Is.EqualTo(RenderGraphResourceAccess.Write));
            Assert.That(generatedGeometry.StageMask &
                PipelineStageFlags2.ComputeShaderBit, Is.Not.Zero);
            Assert.That(blasInput.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(blasInput.StageMask &
                PipelineStageFlags2.AccelerationStructureBuildBitKhr,
                Is.Not.Zero);
            Assert.That(blasInput.AccessMask &
                AccessFlags2.AccelerationStructureReadBitKhr,
                Is.Not.Zero);
            Assert.That(treeWrite.Access,
                Is.EqualTo(RenderGraphResourceAccess.ReadWrite));
            Assert.That(traceRead.Access,
                Is.EqualTo(RenderGraphResourceAccess.Read));
            Assert.That(order.IndexOf("SimpleDdgiLightTreePass"),
                Is.LessThan(order.IndexOf("SimpleDdgiTracePass")));
            Assert.That(AsyncComputePassCatalog.GetClassification(
                    "SimpleDdgiLightTreePass"),
                Is.EqualTo(
                    AsyncComputePassClassification.GraphicsQueueComputeByDesign));
            Assert.That(AsyncComputePassCatalog.GetClassification(
                    "DdgiFoliageProxyGenerationPass"),
                Is.EqualTo(
                    AsyncComputePassClassification.GraphicsQueueComputeByDesign));
        });
    }

    [Test]
    public void SparseResidencyTransaction_DeclaresAllMutationAndConsumerEdges()
    {
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);

        foreach (string mutator in new[]
                 {
                     "SimpleDdgiPageDemandPass",
                     "SimpleDdgiPageResidencyPass",
                     "SimpleDdgiSchedulePass",
                     "SimpleDdgiSchedulerCommitPass",
                     "SimpleDdgiPageFeedbackPass"
                 })
        {
            RenderGraphResourceUsage usage = declarations[mutator].Usages.Single(
                candidate => candidate.Resource == RenderGraphResourceId.SimpleDdgiResidency);
            Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.ReadWrite), mutator);
        }

        foreach (string consumer in new[]
                 {
                     "SimpleDdgiTracePass",
                     "SimpleDdgiRelocateClassifyPass",
                     "SimpleDdgiDirectionalRadiancePass",
                     "SimpleDdgiAcceleratedSolvePass",
                     "SimpleDdgiTransportPass",
                     "SimpleDdgiBlendPass",
                     "SimpleDdgiPublishPass"
                 })
        {
            RenderGraphResourceUsage usage = declarations[consumer].Usages.Single(
                candidate => candidate.Resource == RenderGraphResourceId.SimpleDdgiResidency);
            Assert.That(
                usage.Access is RenderGraphResourceAccess.Read or RenderGraphResourceAccess.ReadWrite,
                Is.True,
                consumer);
        }
    }

    [Test]
    public void SimpleDdgiIndirectConsumersDeclareComputeAndDrawIndirectSchedulerAccess()
    {
        string[] consumerNames =
        [
            "SimpleDdgiUrgentRelightPass",
            "SimpleDdgiTracePass",
            "SimpleDdgiRelocateClassifyPass",
            "SimpleDdgiDirectionalRadiancePass",
            "SimpleDdgiTransportPass",
            "SimpleDdgiBlendPass",
            "SimpleDdgiPublishPass",
            "SimpleDdgiSchedulerCommitPass"
        ];
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        PipelineStageFlags2 requiredStages =
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.DrawIndirectBit;
        AccessFlags2 requiredAccess =
            AccessFlags2.ShaderStorageReadBit |
            AccessFlags2.ShaderStorageWriteBit |
            AccessFlags2.IndirectCommandReadBit;

        foreach (string name in consumerNames)
        {
            RenderGraphResourceUsage usage = declarations[name].Usages.Single(
                usage => usage.Resource == RenderGraphResourceId.SimpleDdgiScheduler);
            Assert.Multiple(() =>
            {
                Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.ReadWrite), name);
                Assert.That(usage.StageMask & requiredStages, Is.EqualTo(requiredStages), name);
                Assert.That(usage.AccessMask & requiredAccess, Is.EqualTo(requiredAccess), name);
            });
        }
    }

    [Test]
    public void DirectionalRadianceProjection_IsPostBlendTransactionalScratchConsumer()
    {
        var production = ProductionRenderPipelineDeclaration.Instance;
        var order = production.PassOrder.ToList();
        var declarations = production.CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);
        int blend = order.IndexOf("SimpleDdgiBlendPass");
        int directional = order.IndexOf("SimpleDdgiDirectionalRadiancePass");
        int publish = order.IndexOf("SimpleDdgiPublishPass");
        RenderGraphPassResourceDeclaration directionalDeclaration =
            declarations["SimpleDdgiDirectionalRadiancePass"];
        RenderGraphResourceUsage scratch =
            directionalDeclaration.Usages.Single(usage =>
                usage.Resource == RenderGraphResourceId.SimpleDdgiRayScratch);
        RenderGraphResourceUsage sidecar =
            directionalDeclaration.Usages.Single(usage =>
                usage.Resource ==
                    RenderGraphResourceId.SimpleDdgiDirectionalRadiance);

        Assert.Multiple(() =>
        {
            Assert.That(directional, Is.GreaterThan(blend));
            Assert.That(directional, Is.LessThan(publish));
            Assert.That(
                scratch.Access,
                Is.EqualTo(RenderGraphResourceAccess.ReadWrite));
            Assert.That(
                sidecar.Access,
                Is.EqualTo(RenderGraphResourceAccess.ReadWrite));
            Assert.That(
                declarations["SimpleDdgiBlendPass"].Usages.Any(usage =>
                    usage.Resource ==
                        RenderGraphResourceId.SimpleDdgiDirectionalRadiance),
                Is.False);
        });
    }

    [Test]
    public void ScheduleAndCommitUseIndirectCommandReadBarriers()
    {
        string schedule = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiSchedulePass.cs");
        string commit = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "SimpleDdgiSchedulerCommitPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(schedule, Does.Contain("BufferMemoryBarrier2"));
            Assert.That(schedule, Does.Contain("DrawIndirectBit"));
            Assert.That(schedule, Does.Contain("IndirectCommandReadBit"));
            Assert.That(schedule, Does.Contain("GetArenaVkBuffer()"));
            Assert.That(schedule, Does.Contain("GetProbeUpdateQueueVkBuffer()"));
            Assert.That(schedule, Does.Contain("ProbeUpdateQueueBytes"));
            Assert.That(schedule, Does.Contain("Vk.QueueFamilyIgnored"));
            Assert.That(commit, Does.Contain("SupportsSecondaryCommandBuffer => true"));
            Assert.That(commit, Does.Contain("DispatchIndirect"));
            Assert.That(commit, Does.Contain("BufferMemoryBarrier2"));
            Assert.That(commit, Does.Contain("DrawIndirectBit"));
            Assert.That(commit, Does.Contain("IndirectCommandReadBit"));
            Assert.That(commit, Does.Contain("CmdDispatchIndirect"));
            Assert.That(commit, Does.Contain("GetArenaVkBuffer()"));
        });
    }

    [Test]
    public void CompactReceiverPublication_HasCompleteProducerAndConsumerGraphEdges()
    {
        var declarations = ProductionRenderPipelineDeclaration.Instance
            .CreatePassResourceDeclarations()
            .ToDictionary(declaration => declaration.PassName);

        string[] graphicsConsumers =
        [
            "ForwardPlusPass",
            "TransparentForwardPass",
            "WeightedTransparentPass",
            "ParticlePass"
        ];
        foreach (string passName in graphicsConsumers)
        {
#if DEBUG || NJULF_DETAILED_INVESTIGATION
            bool expectsForwardDiagnosticResources = passName != "ParticlePass";
#else
            const bool expectsForwardDiagnosticResources = false;
#endif
            RenderGraphResourceUsage usage = declarations[passName].Usages.Single(
                usage => usage.Resource == RenderGraphResourceId.SimpleDdgiReceiverProbes);
            Assert.Multiple(() =>
            {
                Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.Read), passName);
                Assert.That(
                    usage.StageMask & (
                        PipelineStageFlags2.VertexShaderBit |
                        PipelineStageFlags2.TaskShaderBitExt |
                        PipelineStageFlags2.MeshShaderBitExt |
                        PipelineStageFlags2.FragmentShaderBit),
                    Is.Not.Zero,
                    passName);
                Assert.That(
                    declarations[passName].Usages.Any(
                        candidate => candidate.Resource == RenderGraphResourceId.SimpleDdgiProbeState),
                    Is.EqualTo(expectsForwardDiagnosticResources),
                    passName);
                Assert.That(
                    declarations[passName].Usages.Any(
                        candidate => candidate.Resource == RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                    Is.False,
                    passName);
            });
        }

        RenderGraphResourceUsage fog = declarations["FogPass"].Usages.Single(
            usage => usage.Resource == RenderGraphResourceId.SimpleDdgiReceiverProbes);
        Assert.Multiple(() =>
        {
            Assert.That(fog.StageMask & PipelineStageFlags2.ComputeShaderBit, Is.Not.Zero);
            Assert.That(
                declarations["FogPass"].Usages.Any(
                    usage => usage.Resource == RenderGraphResourceId.SimpleDdgiProbeState ||
                        usage.Resource == RenderGraphResourceId.SimpleDdgiTransportSourceCache),
                Is.False);
        });

        foreach (string producer in new[]
                 {
                     "SimpleDdgiUrgentRelightPass",
                     "SimpleDdgiPublishPass",
                     "SimpleDdgiSchedulerCommitPass"
                 })
        {
            RenderGraphResourceUsage usage = declarations[producer].Usages.Single(
                candidate => candidate.Resource == RenderGraphResourceId.SimpleDdgiReceiverProbes);
            Assert.Multiple(() =>
            {
                Assert.That(usage.Access, Is.EqualTo(RenderGraphResourceAccess.Write), producer);
                Assert.That(usage.StageMask & PipelineStageFlags2.ComputeShaderBit, Is.Not.Zero, producer);
                Assert.That(usage.AccessMask & AccessFlags2.ShaderStorageWriteBit, Is.Not.Zero, producer);
                Assert.That(
                    (ulong)(usage.AccessMask & AccessFlags2.ShaderStorageReadBit),
                    Is.Zero,
                    producer);
                Assert.That(
                    (ulong)(usage.StageMask & PipelineStageFlags2.TransferBit),
                    Is.Zero,
                    producer);
            });
        }
    }

    private static string ReadRepoText(params string[] segments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
