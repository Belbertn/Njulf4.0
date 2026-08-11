using System;
using System.IO;
using System.Numerics;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticVulkanRuntimeContractTests
{
    [Test]
    public void CheckedInQualificationAndTransportProducer_AreExplicitlyFailClosed()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        GiCausticGpuPipelineQualification qualification =
            GiCausticGpuPipelineQualification.CheckedInShadersFailClosed;
        GiCausticTaggedTransportProducerContract unavailable =
            GiCausticTaggedTransportProducerContract.Unavailable;

        Assert.Multiple(() =>
        {
            Assert.That(qualification.TryValidateBuild(out string qualificationReason), Is.False);
            Assert.That(qualificationReason,
                Is.EqualTo("caustic-tagged-first-diffuse-trace-shader-unqualified"));
            Assert.That(unavailable.TryValidateForLayout(layout, out string producerReason), Is.False);
            Assert.That(producerReason,
                Is.EqualTo("caustic-tagged-hero-transport-producer-unavailable"));
            Assert.That(GiCausticVulkanRuntimeDiagnostics.Disabled.CapabilityReason,
                Is.EqualTo(GiCausticVulkanRuntimeCapabilityReason.PipelineQualificationUnavailable));
            Assert.That(GiCausticVulkanRuntimeDiagnostics.Disabled.Resource.AllocatedBytes,
                Is.Zero);
        });
    }

    [Test]
    public void TaggedTransportContract_RequiresExactTaskAbiRevisionAndFirstDiffuseSemantics()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        GiCausticCacheRevision revision = CreateRevision(51UL);
        GiCausticTaggedTransportProducerContract valid = CreateValidProducer(layout, revision);
        GiCausticTaggedTransportProducerContract directOrMultiBounce = valid with
        {
            FirstDiffuseEndpointsOnly = false
        };
        GiCausticTaggedTransportProducerContract staleRevision = valid with
        {
            RevisionFingerprint = valid.RevisionFingerprint + 1UL
        };
        GiCausticTaggedTransportProducerContract wrongStride = valid with
        {
            TaskRecordStrideBytes = 16u
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidateForLayout(layout, out string layoutReason), Is.True,
                layoutReason);
            Assert.That(valid.TryValidateForBuild(layout, revision, out string buildReason), Is.True,
                buildReason);
            Assert.That(directOrMultiBounce.TryValidateForLayout(layout, out string semanticReason),
                Is.False);
            Assert.That(semanticReason,
                Is.EqualTo("caustic-tagged-hero-transport-producer-semantics-unqualified"));
            Assert.That(staleRevision.TryValidateForBuild(layout, revision, out string revisionReason),
                Is.False);
            Assert.That(revisionReason,
                Is.EqualTo("caustic-tagged-hero-transport-producer-content-revision-mismatch"));
            Assert.That(wrongStride.TryValidateForLayout(layout, out string strideReason), Is.False);
            Assert.That(strideReason,
                Is.EqualTo("caustic-tagged-hero-transport-producer-task-abi-invalid"));
        });
    }

    [Test]
    public void RecordingContract_RequiresExactTwoBanksFixedSlotsAndFencePublicationOrder()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        GiCausticGpuRecordingStage[] expectedBuildOrder =
        [
            GiCausticGpuRecordingStage.TaggedTaskUpload,
            GiCausticGpuRecordingStage.TaggedTaskUploadToTaskBarrier,
            GiCausticGpuRecordingStage.TaskReset,
            GiCausticGpuRecordingStage.TaskResetToMetadataValidationBarrier,
            GiCausticGpuRecordingStage.TaskMetadataValidation,
            GiCausticGpuRecordingStage.TaskMetadataToGenerationBarrier,
            GiCausticGpuRecordingStage.TaskGeneration,
            GiCausticGpuRecordingStage.TaskGenerationToValidationBarrier,
            GiCausticGpuRecordingStage.TaskValidation,
            GiCausticGpuRecordingStage.TaskToTraceBarrier,
            GiCausticGpuRecordingStage.Trace,
            GiCausticGpuRecordingStage.TraceToCacheBuildBarrier,
            GiCausticGpuRecordingStage.CacheBuildClear,
            GiCausticGpuRecordingStage.CacheBuildClearToRadixBarrier,
            GiCausticGpuRecordingStage.CacheBuildStableRadix,
            GiCausticGpuRecordingStage.CacheBuildRadixToCompactBarrier,
            GiCausticGpuRecordingStage.CacheBuildDeterministicBottomK,
            GiCausticGpuRecordingStage.CacheBuildCompactToHashBarrier,
            GiCausticGpuRecordingStage.CacheBuildDeterministicCellHash,
            GiCausticGpuRecordingStage.CacheBuildToHeaderReadbackBarrier,
            GiCausticGpuRecordingStage.HeaderReadbackCopy,
            GiCausticGpuRecordingStage.FenceValidatedPublication
        ];
        GiCausticGpuRecordingStage[] expectedResolveOrder =
        [
            GiCausticGpuRecordingStage.ResolveRequestUpload,
            GiCausticGpuRecordingStage.ResolveRequestToResolveBarrier,
            GiCausticGpuRecordingStage.Resolve,
            GiCausticGpuRecordingStage.ResolveToForwardCompositeBarrier,
            GiCausticGpuRecordingStage.ForwardCompositeHandoff
        ];

        Assert.Multiple(() =>
        {
            Assert.That(GiCausticGpuVulkanRuntimeContract.TryValidateRecordingLayout(
                    layout,
                    out string layoutReason),
                Is.True,
                layoutReason);
            Assert.That(layout.PhotonBankCount,
                Is.EqualTo(GiCausticGpuVulkanRuntimeContract.RequiredPhotonBankCount));
            Assert.That(layout.CacheBankCount,
                Is.EqualTo(GiCausticGpuVulkanRuntimeContract.RequiredCacheBankCount));
            Assert.That(GiCausticGpuVulkanRuntimeContract.TaskBufferBindlessSlot, Is.EqualTo(204));
            Assert.That(GiCausticGpuVulkanRuntimeContract.PhotonBufferBindlessSlot, Is.EqualTo(205));
            Assert.That(GiCausticGpuVulkanRuntimeContract.CacheBufferBindlessSlot, Is.EqualTo(206));
            Assert.That(GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot, Is.EqualTo(207));
            Assert.That(GiCausticGpuAbi.BindlessSlots.TaskBufferIndex, Is.EqualTo(204));
            Assert.That(GiCausticGpuAbi.BindlessSlots.PhotonBufferIndex, Is.EqualTo(205));
            Assert.That(GiCausticGpuAbi.BindlessSlots.CacheBufferIndex, Is.EqualTo(206));
            Assert.That(GiCausticGpuAbi.BindlessSlots.ScratchBufferIndex, Is.EqualTo(207));
            Assert.That(GiCausticGpuRecordingContract.BuildStages.ToArray(),
                Is.EqualTo(expectedBuildOrder));
            Assert.That(GiCausticGpuRecordingContract.ResolveStages.ToArray(),
                Is.EqualTo(expectedResolveOrder));
            Assert.That(GiCausticGpuRecordingContract.RequiresFenceValidatedHeaderPublication,
                Is.True);
            Assert.That(GiCausticGpuVulkanRuntimeContract.GetCacheHeaderOffsetBytes(layout, 0),
                Is.EqualTo(layout.CacheTableBytes + layout.CacheHistoryBytes));
            Assert.That(GiCausticGpuVulkanRuntimeContract.GetCacheHeaderOffsetBytes(layout, 1),
                Is.EqualTo(layout.CacheTableBytes + layout.CacheHistoryBytes +
                    GiCausticGpuAbi.CacheHeaderBytes));
        });
    }

    [Test]
    public void ForwardCompositeHandoff_RequiresIsolatedValidatedScratchConsumer()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        uint scratchWords = GiCausticGpuVulkanRuntimeContract.GetScratchWordCapacity(layout);
        GiCausticForwardCompositeConsumerContract valid = new(
            IsAvailable: true,
            C4GpuAbiVersion: GiCausticGpuAbi.Version,
            ScratchBufferBindlessIndex:
                (uint)GiCausticGpuVulkanRuntimeContract.ScratchBufferBindlessSlot,
            ResolveRequestWordOffset: 0u,
            ResolveRequestCount: 2u,
            RequestWriteStageMask: PipelineStageFlags2.ComputeShaderBit,
            RequestWriteAccessMask: AccessFlags2.ShaderStorageWriteBit,
            CompositeReadStageMask: PipelineStageFlags2.FragmentShaderBit,
            CompositeReadAccessMask: AccessFlags2.ShaderStorageReadBit,
            UsesOnlyValidatedC4ResolveResults: true,
            KeepsC4ScratchSeparateFromDdgi: true);
        GiCausticForwardCompositeConsumerContract aliasesDdgi = valid with
        {
            KeepsC4ScratchSeparateFromDdgi = false
        };
        GiCausticForwardCompositeHandoff disabled =
            GiCausticForwardCompositeHandoff.Disabled("no-forward-consumer");

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidate(scratchWords, out string validReason), Is.True,
                validReason);
            Assert.That(aliasesDdgi.TryValidate(scratchWords, out string aliasReason), Is.False);
            Assert.That(aliasReason,
                Is.EqualTo("caustic-forward-composite-consumer-semantics-or-visibility-invalid"));
            Assert.That(GiCausticForwardCompositeConsumerContract.Unavailable.TryValidate(
                    scratchWords,
                    out string unavailableReason),
                Is.False);
            Assert.That(unavailableReason,
                Is.EqualTo("caustic-forward-composite-consumer-unavailable"));
            Assert.That(disabled.IsAvailable, Is.False);
            Assert.That(disabled.ScratchBufferBindlessIndex, Is.EqualTo(207u));
            Assert.That(disabled.Reason, Is.EqualTo("no-forward-consumer"));
        });
    }

    [Test]
    public void RuntimeSources_RecordGpuTaskGenerationTaggedRayTraceAndParallelBuilder()
    {
        string runtime = ReadRepoText(
            "Njulf.Rendering", "Resources", "GiCausticVulkanRuntime.cs");
        string pass = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "GiCausticGpuPass.cs");
        string trace = ReadRepoText("Njulf.Shaders", "gi_caustic_trace.comp");
        string cacheBuild = ReadRepoText("Njulf.Shaders", "gi_caustic_cache_build.comp");

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("CheckedInShadersFailClosed"));
            Assert.That(runtime, Does.Contain("TryRecordBuild"));
            Assert.That(runtime, Does.Contain("TryReadCompletedFrame"));
            Assert.That(runtime, Does.Contain("TryRecordResolveForForwardComposite"));
            Assert.That(runtime, Does.Contain("TryBindFallback"));
            Assert.That(runtime, Does.Not.Contain("VulkanRenderer"));
            Assert.That(GiCausticGpuPassNames.TaskShader,
                Is.EqualTo("gi_caustic_tasks.comp.spv"));
            Assert.That(GiCausticGpuPassNames.TraceShader,
                Is.EqualTo("gi_caustic_trace.comp.spv"));
            Assert.That(GiCausticGpuPassNames.CacheBuildShader,
                Is.EqualTo("gi_caustic_cache_build.comp.spv"));
            Assert.That(GiCausticGpuPassNames.ResolveShader,
                Is.EqualTo("gi_caustic_resolve.comp.spv"));
            Assert.That(pass, Does.Contain("GiCausticGpuPassNames.TaskShader"));
            Assert.That(pass, Does.Contain("GiCausticGpuPassNames.TraceShader"));
            Assert.That(pass, Does.Contain("GiCausticGpuPassNames.CacheBuildShader"));
            Assert.That(pass, Does.Contain("GiCausticGpuPassNames.ResolveShader"));
            Assert.That(pass, Does.Contain("RecordTaskUploadToTaskBarrier"));
            Assert.That(pass, Does.Contain("RecordC4StorageBarrier"));
            Assert.That(pass, Does.Contain("RecordResolveToForwardCompositeBarrier"));
            Assert.That(trace, Does.Contain("#extension GL_EXT_ray_query : require"));
            Assert.That(trace, Does.Contain("GiCausticTraceNearest"));
            Assert.That(trace, Does.Contain("GiCausticHeroHitMatchesTask"));
            Assert.That(trace, Does.Contain("GiCausticDielectricReflectance"));
            Assert.That(trace, Does.Contain("exp(-absorption"));
            Assert.That(trace,
                Does.Not.Contain("GI_CAUSTIC_RAY_QUERY_TRANSPORT_BACKEND_INTEGRATED"));
            Assert.That(cacheBuild,
                Does.Contain("GI_CAUSTIC_BUILD_PHASE_RADIX_HISTOGRAM"));
            Assert.That(cacheBuild,
                Does.Contain("GiCausticStableScatter"));
            Assert.That(cacheBuild,
                Does.Contain("GiCausticCompactLocalScan"));
            Assert.That(cacheBuild,
                Does.Contain("float(runLength) / float(keepCount)"));
            Assert.That(cacheBuild,
                Does.Contain("GiCausticHashAndFinalize"));
            Assert.That(cacheBuild,
                Does.Not.Contain("GI_CAUSTIC_ENABLE_SERIAL_REFERENCE_BUILD"));
        });
    }

    [Test]
    public void DeterministicBuildScratch_IsExactBoundedAndMatchesGpuLayout()
    {
        Assert.That(GiCausticDeterministicBuildScratchLayout.TryCreate(
            16, out GiCausticDeterministicBuildScratchLayout scratch), Is.True);
        GiCausticGpuResourceLayout layout = CreateValidLayout();

        Assert.Multiple(() =>
        {
            Assert.That(scratch.WorkgroupCount, Is.EqualTo(1u));
            Assert.That(scratch.IndexBank0WordOffset, Is.EqualTo(16u));
            Assert.That(scratch.IndexBank1WordOffset, Is.EqualTo(32u));
            Assert.That(scratch.HistogramWordOffset, Is.EqualTo(48u));
            Assert.That(scratch.GroupPrefixWordOffset, Is.EqualTo(304u));
            Assert.That(scratch.BinBaseWordOffset, Is.EqualTo(560u));
            Assert.That(scratch.RequiredWordCount, Is.EqualTo(816u));
            Assert.That(scratch.RequiredBytes, Is.EqualTo(3_264UL));
            Assert.That(layout.ScratchBytes, Is.EqualTo(scratch.RequiredBytes));
            Assert.That(GiCausticDeterministicBuildScratchLayout.RadixPassCount,
                Is.EqualTo(28u));
            Assert.That(GiCausticGpuBuildPhases.DecodeOperation(
                    GiCausticGpuBuildPhases.EncodeRadix(
                        GiCausticGpuBuildPhases.RadixScatter, 6u, 3u)),
                Is.EqualTo(GiCausticGpuBuildPhases.RadixScatter));
        });
    }

    [Test]
    public void ResourceManager_ExposesOnlyCurrentAllocationAndCanAbortARecordingToken()
    {
        GiCausticGpuResourceLayout layout = CreateValidLayout();
        var allocator = new FakeAllocator();
        using var manager = new GiCausticGpuResourceManager();
        GiCausticGpuRuntimeSnapshot configured = manager.Reconcile(
            new GiCausticGpuRuntimeRequest(true, layout, FullySupported()), allocator);
        GiCausticGpuBuildBeginResult begin = manager.BeginBuild(
            CreateRevision(100UL),
            taskCount: 3,
            new Vector4(1.0f, 2.0f, 3.0f, 0.5f));
        bool aborted = manager.AbortBuild(begin.Token, "test-recording-aborted");
        bool visible = manager.TryGetActiveAllocation(
            out GiCausticGpuAllocation allocation,
            out GiCausticGpuResourceLayout activeLayout);

        Assert.Multiple(() =>
        {
            Assert.That(configured.IsEffectivelyEnabled, Is.True);
            Assert.That(begin.Started, Is.True, begin.Reason);
            Assert.That(aborted, Is.True);
            Assert.That(visible, Is.True);
            Assert.That(allocation.DescriptorCount, Is.EqualTo(GiCausticGpuAbi.DescriptorCount));
            Assert.That(activeLayout, Is.EqualTo(layout));
            Assert.That(manager.Snapshot.State, Is.EqualTo(GiCausticGpuResourceState.ReadyForBuild));
            Assert.That(manager.Snapshot.Reason, Is.EqualTo("test-recording-aborted"));
            Assert.That(manager.PublicationFailureCount, Is.EqualTo(1UL));
        });
    }

    private static GiCausticGpuResourceLayout CreateValidLayout()
    {
        GiCausticCacheLayout source = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 16,
            maximumPhotonsPerCell: 4,
            maximumOccupiedCells: 4,
            recordStride: GiCausticGpuAbi.PhotonRecordBytes,
            writeBankCount: 2,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0UL,
            budgetBytes: 1_000_000UL);
        GiCausticGpuResourceLayout layout = GiCausticGpuResourceLayoutCompiler.Compile(
            new(
                source,
                IndependentMemoryBudgetBytes: 1_000_000UL,
                ScreenResolveProfile: new(64, 64)));
        Assert.That(layout.IsValid, Is.True, layout.FailureReason);
        return layout;
    }

    private static GiCausticCacheRevision CreateRevision(ulong value) => new(
        TransportAbi: GiCausticGpuAbi.Version,
        HeroMaterialRevision: value,
        LightDistributionRevision: value + 1UL,
        CasterGeometryRevision: value + 2UL,
        CasterTransformRevision: value + 3UL,
        ReceiverGeometryRevision: value + 4UL,
        StableIdentityRevision: value + 5UL);

    private static GiCausticTaggedTransportProducerContract CreateValidProducer(
        in GiCausticGpuResourceLayout layout,
        in GiCausticCacheRevision revision) => new(
        IsAvailable: true,
        C4GpuAbiVersion: GiCausticGpuAbi.Version,
        TransportAbiVersion: revision.TransportAbi,
        TaskCount: 3,
        TaskRecordStrideBytes: GiCausticGpuAbi.TaskRecordBytes,
        TaskPayloadBytes: 3UL * GiCausticGpuAbi.TaskRecordBytes,
        EmitterCount: 2,
        HeroCount: 2,
        ProposalPairCount: 4,
        MetadataPayloadBytes:
            2UL * GiCausticGpuAbi.EmitterRecordBytes +
            2UL * GiCausticGpuAbi.HeroRecordBytes +
            4UL * GiCausticGpuAbi.ProposalPairRecordBytes,
        RevisionFingerprint: GiCausticGpuAbi.ComputeRevisionFingerprint(revision),
        TaggedLightDistributionAvailable: true,
        HeroCasterMetadataAvailable: true,
        CurrentPoseAccelerationStructureAvailable: true,
        FirstDiffuseEndpointsOnly: true,
        SupportsTransactionStamping: true,
        GpuTaskGeneration: true,
        ExactTwoLevelProposal: true,
        CanonicalEmissionSupport: true,
        ProducerWriteStageMask: PipelineStageFlags2.ComputeShaderBit,
        ProducerWriteAccessMask: AccessFlags2.ShaderStorageWriteBit);

    private static GiCausticGpuFeatureSupport FullySupported() => new(
        ComputeSupported: true,
        RayQuerySupported: true,
        CurrentPoseAccelerationStructuresAvailable: true,
        TaggedTransportBackendIntegrated: true,
        DeterministicParallelCacheBuildIntegrated: true,
        PublicationReadbackSupported: true,
        DedicatedBindlessSlotsAvailable: true,
        ScreenResolvePipelineIntegrated: true,
        ScreenResolveResourcesAvailable: true);

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

    private sealed class FakeAllocator : IGiCausticGpuResourceAllocator
    {
        private ulong _nextHandle = 1UL;

        public GiCausticGpuAllocation Allocate(in GiCausticGpuResourceLayout layout) => new(
            AllocationId: _nextHandle++,
            Tasks: Create(layout.TaskQueueBytes),
            Photons: Create(checked(layout.CandidateStagingBytes + layout.PublishedPhotonBytes)),
            Cache: Create(layout.CacheBytes),
            Scratch: Create(layout.ScratchBytes),
            DescriptorCount: GiCausticGpuAbi.DescriptorCount);

        public void Retire(GiCausticGpuAllocation allocation)
        {
        }

        private GiCausticGpuBuffer Create(ulong bytes) => new(_nextHandle++, bytes);
    }
}
