using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiProbePagingShaderContractTests
{
    [Test]
    public void ArenaClear_IsVisibleToBootstrapCopiesBeforeShaderUse()
    {
        string cache = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiProbePageCache.cs");

        Assert.That(cache, Does.Contain(
            "PipelineStageFlags2.TransferBit |\n                PipelineStageFlags2.ComputeShaderBit |\n                PipelineStageFlags2.FragmentShaderBit,\n                AccessFlags2.TransferWriteBit |\n                AccessFlags2.ShaderStorageReadBit"));
    }

    [Test]
    public void DepthReadOnlyTransition_RepublishesUnchangedLayoutForAttachmentLoads()
    {
        string target = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "RenderTarget.cs");

        Assert.Multiple(() =>
        {
            Assert.That(target, Does.Contain(
                "bool republishUnchangedLayout ="));
            Assert.That(target, Does.Contain(
                "PipelineStageFlags2.EarlyFragmentTestsBit |\n                    PipelineStageFlags2.LateFragmentTestsBit"));
            Assert.That(target, Does.Contain(
                "force: republishUnchangedLayout"));
        });
    }

    [Test]
    public void PageDemand_DoesNotRepublishAProvenReadAfterReadDepthEdge()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageDemandPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(pass, Does.Contain(
                "synchronizeMatchingLayout: false"));
            Assert.That(pass, Does.Not.Contain(
                "InsertStorageBarrier(cmd)"));
        });
    }

    [Test]
    public void PrimaryGraphicsFrameBoundary_OrdersSharedResourcesAcrossFrames()
    {
        string renderer = ReadRepoText(
            "Njulf.Rendering",
            "VulkanRenderer.cs");

        Assert.Multiple(() =>
        {
            Assert.That(renderer, Does.Contain(
                "_currentCommandBuffer = _cmd.BeginPrimaryGraphicsCommand(_currentFrame);\n            InsertInterFrameSharedResourceDependency(_currentCommandBuffer);"));
            Assert.That(renderer, Does.Contain(
                "SrcStageMask = PipelineStageFlags2.AllCommandsBit"));
            Assert.That(renderer, Does.Contain(
                "SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit"));
            Assert.That(renderer, Does.Contain(
                "DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit"));
        });
    }

    [Test]
    public void FeedbackReadback_IsTransferVisibleToTheHostBeforeFenceLateConsumption()
    {
        string cache = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiProbePageCache.cs");

        Assert.Multiple(() =>
        {
            Assert.That(cache, Does.Contain(
                "PipelineStageFlags2.TransferBit,\n                AccessFlags2.TransferWriteBit,\n                PipelineStageFlags2.HostBit,\n                AccessFlags2.HostReadBit"));
            Assert.That(cache, Does.Contain(
                "completedFrameSerial <=\n                    _feedbackSubmittedFrameSerial[frameIndex]"));
        });
    }

    [Test]
    public void FeedbackConvergence_IgnoresPersistentVisibilityMetadata()
    {
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");
        string metadataAbi = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_scheduler_metadata_abi.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(metadataAbi, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR = 1u << 30u"));
            Assert.That(feedback, Does.Contain(
                "schedulerMetadata &\n                    SIMPLE_DDGI_SCHEDULER_PROBE_META_REPAIR"));
            Assert.That(feedback, Does.Not.Contain(
                "schedulerBase + 5u) == 0u"));
        });
    }

    [Test]
    public void FeedbackAuditsBothDirectionsOfPageOwnershipOnGpu()
    {
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");

        Assert.Multiple(() =>
        {
            Assert.That(feedback, Does.Contain(
                "shared uint duplicateVirtualOwnerCount;"));
            Assert.That(feedback, Does.Contain(
                "shared uint duplicatePhysicalOwnerCount;"));
            Assert.That(feedback, Does.Contain(
                "duplicatePhysicalScratch +\n                        (physicalPlusOne - 1u) * 2u"));
            Assert.That(feedback, Does.Contain(
                "duplicateVirtualScratch + ownerPlusOne - 1u"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_RESIDENCY_COUNTER_DUPLICATE_PHYSICAL"));
            Assert.That(feedback, Does.Contain(
                "duplicateVirtualOwnerCount != 0u"));
            Assert.That(feedback, Does.Contain(
                "duplicatePhysicalOwnerCount != 0u"));
        });
    }

    [Test]
    public void FeedbackPageAndReverseMapSummaries_UseTheWholeWorkgroup()
    {
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");

        Assert.Multiple(() =>
        {
            Assert.That(feedback, Does.Contain(
                "for (uint virtualPage = local;"));
            Assert.That(feedback, Does.Contain(
                "virtualPage += gl_WorkGroupSize.x"));
            Assert.That(feedback, Does.Contain(
                "for (uint physicalPage = local;"));
            Assert.That(feedback, Does.Contain(
                "physicalPage += gl_WorkGroupSize.x"));
            Assert.That(feedback, Does.Contain(
                "uint resident = feedbackResidentPageCount"));
        });
    }

    [Test]
    public void CertifiedStaticPaging_UsesBoundedFullAuditsAndLightweightIntervalCloseout()
    {
        string demandPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageDemandPass.cs");
        string residencyPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageResidencyPass.cs");
        string feedbackPass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageFeedbackPass.cs");
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");

        Assert.Multiple(() =>
        {
            Assert.That(demandPass, Does.Contain(
                "sceneData.SimpleDdgiPageFullManagementRequired != 0"));
            Assert.That(residencyPass, Does.Contain(
                "sceneData.SimpleDdgiPageFullManagementRequired != 0"));
            Assert.That(feedbackPass, Does.Contain(
                "sceneData.SimpleDdgiPageFullManagementRequired != 0"));
            Assert.That(feedbackPass, Does.Contain("? 5u\n                : 6u"));
            Assert.That(feedback, Does.Contain("if (pc.Stage == 6u)"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_RESIDENCY_COUNTER_RECEIVER_REQUESTS"));
            Assert.That(feedback, Does.Contain(
                "SIMPLE_DDGI_RESIDENCY_COUNTER_NONRESIDENT_GATHER_REJECTIONS"));
            Assert.That(feedback, Does.Contain("atomicExchange("));
        });
    }

    [Test]
    public void CameraCutVictims_DoNotPrecedeNaturallyExpiredVictims()
    {
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");

        Assert.Multiple(() =>
        {
            Assert.That(reconcile, Does.Contain(
                "bool candidateExpiresWhenIdle"));
            Assert.That(reconcile, Does.Contain(
                "if (candidateExpiresWhenIdle != bestExpiresWhenIdle)"));
            Assert.That(reconcile, Does.Contain(
                "SIMPLE_DDGI_PAGE_CLASS_EVICT_WHEN_IDLE"));
        });
    }

    [Test]
    public void ExpiredPages_AreCachedUntilAdmissionPressureNeedsCapacity()
    {
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");

        Assert.Multiple(() =>
        {
            Assert.That(reconcile, Does.Contain(
                "Merely expired pages stay as a zero-cost\n    // cache while free physical slots exist"));
            Assert.That(reconcile, Does.Contain(
                "(classFlags & SIMPLE_DDGI_PAGE_CLASS_SUPPRESSED) == 0u"));
        });
    }

    [Test]
    public void Reconciliation_SelectsDirectlyFromStableVirtualPageClasses()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageResidencyPass.cs");
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");

        Assert.Multiple(() =>
        {
            Assert.That(pass, Does.Not.Contain(
                "ddgi_simple_page_prefix.comp.spv"));
            Assert.That(reconcile, Does.Contain(
                "virtualPage < params.virtualPageCount"));
            Assert.That(reconcile, Does.Contain(
                "candidateVirtual < params.virtualPageCount"));
            Assert.That(reconcile, Does.Not.Contain(
                "candidateBase + candidateIndex"));
            Assert.That(reconcile, Does.Not.Contain(
                "victimBase + victimIndex"));
        });
    }

    [Test]
    public void ReceiverDemand_IsGatedByARepresentativeInvocation()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "#ifndef SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE"));
            Assert.That(shared, Does.Contain(
                "if (SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE &&"));
            Assert.That(shared, Does.Contain(
                "paging.residencyMode == SIMPLE_DDGI_RESIDENCY_MODE_SHADOW"));
            Assert.That(shared, Does.Contain(
                "SimpleDdgiStampOpaqueGatherDemand"));
            Assert.That(ReadRepoText("Njulf.Shaders", "forward.frag"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (((uint(gl_FragCoord.x) & 7u) == 0u)"));
            Assert.That(ReadRepoText("Njulf.Shaders", "fog.comp"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (gl_LocalInvocationIndex == 0u)"));
            Assert.That(ReadRepoText("Njulf.Shaders", "particle.vert"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE ((uint(gl_VertexIndex) % 6u) == 0u)"));
            Assert.That(ReadRepoText("Njulf.Shaders", "foliage_mesh.mesh"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (gl_LocalInvocationIndex == 0u)"));
            Assert.That(ReadRepoText("Njulf.Shaders", "foliage_grass.mesh"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_SAMPLE (gl_LocalInvocationIndex == 0u)"));
            Assert.That(shared, Does.Contain(
                "#if SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT != 0"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET"));
            Assert.That(ReadRepoText("Njulf.Shaders", "forward.frag"),
                Does.Contain("The generic forward artifact is the sorted-transparent pipeline."));
            Assert.That(ReadRepoText("Njulf.Shaders", "forward.frag"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 0u"));
            Assert.That(ReadRepoText("Njulf.Shaders", "forward.frag"),
                Does.Contain("SIMPLE_DDGI_OPAQUE_GATHER_ORACLE 1"));
            Assert.That(ReadRepoText("Njulf.Shaders", "forward.frag"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 0"));
            Assert.That(shared, Does.Contain(
                "A compact publication miss is the authoritative point"));
            Assert.That(ReadRepoText("Njulf.Shaders", "fog.comp"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1"));
            Assert.That(ReadRepoText("Njulf.Shaders", "fog.comp"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u"));
            Assert.That(ReadRepoText("Njulf.Shaders", "particle.vert"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1"));
            Assert.That(ReadRepoText("Njulf.Shaders", "particle.vert"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 1u"));
            Assert.That(ReadRepoText("Njulf.Shaders", "foliage_mesh.mesh"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1"));
            Assert.That(ReadRepoText("Njulf.Shaders", "foliage_mesh.mesh"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_DEMAND_FRAME_OFFSET 0u"));
            Assert.That(ReadRepoText("Njulf.Shaders", "foliage_grass.mesh"),
                Does.Contain("SIMPLE_DDGI_RECEIVER_TOUCHES_RESIDENT 1"));
        });
    }

    [Test]
    public void ReceiverDemand_EpochPublicationIsLockFreeForSimtExecution()
    {
        string paging = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_shared.glsl");

        int methodStart = paging.IndexOf(
            "void SimpleDdgiRecordReceiverPageDemand",
            StringComparison.Ordinal);
        int methodEnd = paging.IndexOf(
            "void SimpleDdgiRecordResidencyCounter",
            methodStart,
            StringComparison.Ordinal);
        Assert.That(methodStart, Is.GreaterThanOrEqualTo(0));
        Assert.That(methodEnd, Is.GreaterThan(methodStart));
        string method = paging[methodStart..methodEnd];

        Assert.Multiple(() =>
        {
            Assert.That(method, Does.Contain(
                "SIMPLE_DDGI_RESIDENCY_COUNTER_EPOCH"));
            Assert.That(method, Does.Contain("atomicExchange("));
            Assert.That(method, Does.Contain("atomicMax("));
            Assert.That(method, Does.Contain("atomicCompSwap("));
            Assert.That(method, Does.Not.Contain("for (;;)"));
            Assert.That(method, Does.Not.Contain("0x80000000u"));
        });
    }

    [Test]
    public void ProactiveDemand_DeduplicatesGlobalStampsWithoutDroppingOverflow()
    {
        string demand = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_demand.comp");

        Assert.Multiple(() =>
        {
            Assert.That(demand, Does.Contain(
                "shared uint workgroupPageKeys"));
            Assert.That(demand, Does.Contain(
                "atomicCompSwap(\n            workgroupPageKeys[slot]"));
            Assert.That(demand, Does.Contain(
                "atomicMin(workgroupPageDistance[slot], distanceBucket)"));
            Assert.That(demand, Does.Contain(
                "Never trade correctness for the optimization"));
            Assert.That(demand, Does.Contain(
                "memoryBarrierShared();\n    barrier();"));
            Assert.That(demand, Does.Contain(
                "SimpleDdgiFlattenPage(pageCoord, paging.pageGrid)"));
            Assert.That(demand, Does.Not.Contain(
                "ResolveSimpleDdgiProbeAddress("));
        });
    }

    [Test]
    public void ProactiveDemand_UsesTheProfiledTileCoverageInDispatchAndShader()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageDemandPass.cs");
        string demand = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_demand.comp");

        Assert.Multiple(() =>
        {
            Assert.That(pass, Does.Contain(
                "internal const uint ReceiverTileSize = 64u"));
            Assert.That(pass, Does.Contain(
                "internal const uint WorkgroupWidth = 4u"));
            Assert.That(pass, Does.Contain(
                "ReceiverTileSize * WorkgroupWidth"));
            Assert.That(demand, Does.Contain(
                "ivec2(gl_GlobalInvocationID.xy) * tileSize"));
            Assert.That(demand, Does.Contain(
                "ivec2 offsets[4]"));
            Assert.That(demand, Does.Contain(
                "shared SimpleDdgiParams workgroupParams"));
            Assert.That(demand, Does.Contain(
                "shared SimpleDdgiVolume workgroupVolumes"));
            Assert.That(demand, Does.Contain(
                "SimpleDdgiVolumePaging paging = workgroupPaging[volumeIndex]"));
            Assert.That(demand, Does.Contain(
                "int sampleStride = max(tileSize / 2, 1)"));
            Assert.That(demand, Does.Contain(
                "SimpleDdgiSampleBiasMagnitudes(params, spacing)"));
            Assert.That(demand, Does.Contain(
                "includes the exact biased gather"));
            Assert.That(demand, Does.Not.Contain(
                "ReconstructDemandNormal"));
        });
    }

    [Test]
    public void ResidencyExplicitlyAcquiresGraphicsAndComputeDemandWrites()
    {
        string residency = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageResidencyPass.cs");

        Assert.Multiple(() =>
        {
            Assert.That(residency, Does.Contain(
                "InsertDemandVisibilityBarrier(cmd);"));
            Assert.That(residency, Does.Contain(
                "PipelineStageFlags2.FragmentShaderBit"));
            Assert.That(residency, Does.Contain(
                "PipelineStageFlags2.MeshShaderBitExt"));
            Assert.That(residency, Does.Contain(
                "SrcAccessMask = AccessFlags2.ShaderStorageWriteBit"));
        });
    }

    [Test]
    public void SparseDemand_UsesDepthPredictionAndKeepsExactGatherAsShadowOracle()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiPageDemandPass.cs");
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_shared.glsl");
        string paging = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(pass, Does.Contain(
                "ProbeResidencyMode.CollectsDemand()"));
            Assert.That(shared, Does.Contain(
                "p.frameIndex + 1u"));
            Assert.That(shared, Does.Contain(
                "SIMPLE_DDGI_OPAQUE_GATHER_ORACLE == 0"));
            Assert.That(paging, Does.Contain(
                "p.residencyMode != SIMPLE_DDGI_RESIDENCY_MODE_SHADOW"));
        });
    }

    [Test]
    public void ReceiverGather_UsesCompactPublishedPhysicalAddressWhileUpdatesUseFullMapping()
    {
        string shared = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_shared.glsl");
        string paging = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_shared.glsl");

        Assert.Multiple(() =>
        {
            Assert.That(shared, Does.Contain(
                "ResolveSimpleDdgiReceiverProbeAddress("));
            Assert.That(shared, Does.Contain(
                "atlasProbeAddress = receiverProbe.atlasProbeAddress"));
            Assert.That(shared, Does.Contain(
                "#if SIMPLE_DDGI_GATHER_USES_COMPUTE_STATE\n            ResolveSimpleDdgiProbeAddress("));
            Assert.That(paging, Does.Contain(
                "Residency invalidates that record before owner reuse"));
        });
    }

    [Test]
    public void ShadowFeedback_ExportsPredictorFalseNegativeAndPositiveCounts()
    {
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");

        Assert.Multiple(() =>
        {
            Assert.That(feedback, Does.Contain(
                "opaqueGatherDemanded && !visibleDemanded"));
            Assert.That(feedback, Does.Contain(
                "visibleDemanded && !opaqueGatherDemanded"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 60u, predictorFalseNegatives"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 61u, predictorFalsePositives"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 62u, opaqueGatherDemand"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 63u, predictorTruePositives"));
        });
    }

    [Test]
    public void PublicationLatency_SeparatesBootstrapCutAndOrdinaryMotion()
    {
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");
        string feedback = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_feedback.comp");

        Assert.Multiple(() =>
        {
            Assert.That(reconcile, Does.Contain(
                "SIMPLE_DDGI_PHYSICAL_PAGE_ALLOCATION_CAMERA_CUT"));
            Assert.That(reconcile, Does.Contain(
                "SIMPLE_DDGI_PHYSICAL_PAGE_ALLOCATION_BOOTSTRAP"));
            Assert.That(reconcile, Does.Contain("pc.AllocationFlags"));
            Assert.That(feedback, Does.Contain(
                "ordinaryPublicationLatencyHistogram"));
            Assert.That(feedback, Does.Contain(
                "cutPublicationLatencyHistogram"));
            Assert.That(feedback, Does.Contain(
                "bool allocatedDuringBootstrap"));
            Assert.That(feedback, Does.Contain(
                "if (!allocatedDuringBootstrap)"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 82u"));
            Assert.That(feedback, Does.Contain(
                "feedbackBase + 87u"));
        });
    }

    [Test]
    public void VisiblePublicationCohort_IsProbeBoundedAndReceivesExactSourcePriority()
    {
        string pageClassify = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_classify.comp");
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");
        string schedulerClassify = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_classify.comp");
        string admit = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_schedule_admit.comp");

        Assert.Multiple(() =>
        {
            Assert.That(pageClassify, Does.Contain(
                "pageValidProbeCount"));
            Assert.That(reconcile, Does.Contain(
                "pc.VisiblePublicationProbeBudget *"));
            Assert.That(reconcile, Does.Contain(
                "visiblePartialProbeCount"));
            Assert.That(reconcile, Does.Contain(
                "visiblePublicationCandidateDeferred"));
            Assert.That(reconcile, Does.Contain(
                "SIMPLE_DDGI_PHYSICAL_PAGE_ALLOCATION_DEMAND_CLASS_SHIFT"));
            Assert.That(schedulerClassify, Does.Contain(
                "SchedulerSparseVisiblePagePublicationPending("));
            Assert.That(schedulerClassify, Does.Contain(
                "SIMPLE_DDGI_SCHEDULER_COUNTER_VISIBLE_PAGE_COHORT_BASE"));
            Assert.That(admit, Does.Contain(
                "visiblePagePendingByVolume"));
            Assert.That(admit, Does.Contain(
                "visiblePageReservationPending"));
            Assert.That(admit, Does.Contain(
                "Return every unreserved request"));
        });
    }

    [Test]
    public void ConfirmedEmptyPage_RetriesOnlyOnAuthoritativeInvalidation()
    {
        string classify = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_classify.comp");

        Assert.Multiple(() =>
        {
            Assert.That(classify, Does.Contain(
                "if (historyGeometry != currentGeometry)"));
            Assert.That(classify, Does.Not.Contain(
                "pc.CurrentFrame - lastRelevant >= ResidencyRead(params, 14u)"));
            Assert.That(classify, Does.Not.Contain("bool retryDue"));
        });
    }

    [Test]
    public void DevelopmentPin_IsAnExplicitCommandAndDebugViewsRemainReadOnly()
    {
        string classify = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_classify.comp");
        string reconcile = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_reconcile.comp");
        string initialize = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_initialize.comp");
        string renderer = ReadRepoText(
            "Njulf.Rendering",
            "VulkanRenderer.cs");

        Assert.Multiple(() =>
        {
            Assert.That(classify, Does.Contain(
                "SIMPLE_DDGI_RESIDENCY_DEVELOPMENT_CONTROL_VALID"));
            Assert.That(classify, Does.Contain(
                "SIMPLE_DDGI_PAGE_HISTORY_DEVELOPMENT_PIN"));
            Assert.That(classify, Does.Contain(
                "SIMPLE_DDGI_PAGE_DEMAND_CLASS_DEVELOPMENT_PIN"));
            Assert.That(reconcile, Does.Contain(
                "SIMPLE_DDGI_PHYSICAL_PAGE_PINNED"));
            Assert.That(initialize, Does.Contain(
                "ResidencyRead(params, reverseBase + 3u) &\n            SIMPLE_DDGI_PHYSICAL_PAGE_PINNED"));
            Assert.That(renderer, Does.Contain(
                "TrySetSimpleDdgiProbeResidencyDevelopmentPin"));
            Assert.That(renderer, Does.Contain(
                "SetSimpleDdgiProbeResidencyDevelopmentFreeze"));
            Assert.That(renderer, Does.Contain(
                "Settings.Debug.Enabled"));
        });
    }

    [Test]
    public void PageInitialization_UsesTheCompiledPerVolumeCacheRegion()
    {
        string initialize = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_simple_page_initialize.comp");

        Assert.Multiple(() =>
        {
            Assert.That(initialize, Does.Contain(
                "uint cacheBase = volume.cacheBaseWord +"));
            Assert.That(initialize, Does.Contain(
                "volume.cacheStrideWords;"));
            Assert.That(initialize, Does.Not.Contain(
                "SIMPLE_DDGI_TRANSPORT_RAY_CACHE_STRIDE_WORDS"));
            Assert.That(initialize, Does.Contain(
                "cacheBase + volume.cacheStrideWords - 1u"));
            Assert.That(initialize, Does.Contain(
                "CPU layout compilation has already checked the complete region"));
            Assert.That(initialize, Does.Contain(
                "// VALID is the final store."));
        });
    }

    [TestCase(SimpleDdgiProbeResidencyMode.Dense, true, false)]
    [TestCase(SimpleDdgiProbeResidencyMode.Shadow, true, true)]
    [TestCase(SimpleDdgiProbeResidencyMode.SparseNearRing, true, true)]
    [TestCase(SimpleDdgiProbeResidencyMode.SparseNearRing, false, false)]
    public void FeedbackAuthority_IsBoundToDemandCollectingModes(
        SimpleDdgiProbeResidencyMode mode,
        bool feedbackValid,
        bool expected)
    {
        Assert.That(
            SimpleDdgiProbeResidencyTelemetryFactory.IsFeedbackValidForMode(
                mode,
                feedbackValid),
            Is.EqualTo(expected));
    }

    [Test]
    public void ResidencyTransactionReplacement_InvalidatesPriorFeedback()
    {
        string manager = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiVolumeManager.cs");

        int replacement = manager.IndexOf(
            "if (residencyArenaReplaced)",
            StringComparison.Ordinal);
        int invalidation = manager.IndexOf(
            "_probeResidencyFeedbackValid = false;",
            replacement,
            StringComparison.Ordinal);
        Assert.Multiple(() =>
        {
            Assert.That(replacement, Is.GreaterThanOrEqualTo(0));
            Assert.That(invalidation, Is.GreaterThan(replacement));
        });
        Assert.That(manager, Does.Contain(
            "_lastProbeResidencyFeedback = default;"));
        Assert.That(manager, Does.Contain(
            "_probeResidencyFeedbackFrameSerial = 0UL;"));
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

        throw new FileNotFoundException(
            "Could not locate repository file.",
            Path.Combine(segments));
    }
}
