using System;
using System.IO;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiGuidingVulkanRuntimeTests
{
    [Test]
    public void SourceCacheHandshake_RequiresFixedOwnerAndVariableGenerationTimePdfConsumer()
    {
        SimpleDdgiGuidingSourceCacheHandshake valid = CreateValidHandshake();
        SimpleDdgiGuidingSourceCacheHandshake wrongSlot = valid with
        {
            DirectionPdfSidecarBindlessSlot =
                (uint)SimpleDdgiGuidingBindlessSlots.TrainingScratch
        };
        SimpleDdgiGuidingSourceCacheHandshake stalePdfConsumer = valid with
        {
            ConsumerAcceptsGenerationTimePdf = false
        };
        SimpleDdgiGuidingSourceCacheHandshake fixedPdfOnlyConsumer = valid with
        {
            ConsumerSupportsVariablePdfProjection = false
        };
        SimpleDdgiGuidingSourceCacheHandshake missingOwnerPriorAccess = valid with
        {
            SourceCachePriorAccessStageMask = 0,
            SourceCachePriorAccessMask = 0
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidate(out SimpleDdgiGuidingGpuCapabilityReason validCapability,
                out string validReason), Is.True, validReason);
            Assert.That(validCapability, Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason.None));

            Assert.That(wrongSlot.TryValidate(out SimpleDdgiGuidingGpuCapabilityReason slotCapability,
                out string slotReason), Is.False);
            Assert.That(slotCapability,
                Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarOwnershipInvalid));
            Assert.That(slotReason, Does.Contain("slot-203"));

            Assert.That(stalePdfConsumer.TryValidate(
                out SimpleDdgiGuidingGpuCapabilityReason consumerCapability,
                out string consumerReason), Is.False);
            Assert.That(consumerCapability,
                Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason
                    .SourceCacheDirectionPdfConsumerUnavailable));
            Assert.That(consumerReason, Does.Contain("generation-time-pdf"));

            Assert.That(fixedPdfOnlyConsumer.TryValidate(
                out SimpleDdgiGuidingGpuCapabilityReason variableCapability,
                out string variableReason), Is.False);
            Assert.That(variableCapability,
                Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason.VariablePdfProjectionUnavailable));
            Assert.That(variableReason, Does.Contain("variable-pdf"));

            Assert.That(missingOwnerPriorAccess.TryValidate(
                out SimpleDdgiGuidingGpuCapabilityReason ownerAccessCapability,
                out string ownerAccessReason), Is.False);
            Assert.That(ownerAccessCapability,
                Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason.SourceCacheHandshakeInvalid));
            Assert.That(ownerAccessReason, Does.Contain("prior-access"));
        });
    }

    [Test]
    public void BuildWorkload_RequiresFrozenStridesAndStrictCpuOwnedHeaderIdentities()
    {
        SimpleDdgiGuidingLayout layout = CreateLayout();
        SimpleDdgiGuidingBuildWorkload valid = CreateValidBuildWorkload();
        SimpleDdgiGuidingBuildWorkload legacyTrainingStride = valid with
        {
            TrainingRecords = valid.TrainingRecords with { ElementStrideBytes = 16u }
        };
        SimpleDdgiGuidingBuildWorkload missingTraceSource = valid with
        {
            TraceTrainingSource = default
        };
        SimpleDdgiGuidingBuildWorkload duplicatePhysicalProbe = valid with
        {
            ExpectedHeaders =
            new[]
            {
                new SimpleDdgiGuidingExpectedProbeHeader(1u, 10u, 4u),
                new SimpleDdgiGuidingExpectedProbeHeader(1u, 11u, 4u)
            }
        };

        Assert.Multiple(() =>
        {
            Assert.That(valid.TryValidate(layout, out string validReason), Is.True, validReason);
            Assert.That(legacyTrainingStride.TryValidate(layout, out string strideReason), Is.False);
            Assert.That(strideReason, Does.Contain("training-records"));
            Assert.That(duplicatePhysicalProbe.TryValidate(layout, out string duplicateReason), Is.False);
            Assert.That(duplicateReason, Does.Contain("strictly-ascending"));
            Assert.That(missingTraceSource.TryValidate(layout, out string sourceReason), Is.False);
            Assert.That(sourceReason, Does.Contain("trace-training-source"));
        });
    }

    [Test]
    public void DisabledRuntimeDiagnostics_ExposeNoC3Resources()
    {
        SimpleDdgiGuidingGpuRuntimeDiagnostics disabled =
            SimpleDdgiGuidingGpuRuntimeDiagnostics.Disabled;

        Assert.Multiple(() =>
        {
            Assert.That(disabled.CapabilityReason,
                Is.EqualTo(SimpleDdgiGuidingGpuCapabilityReason.SourceCacheSidecarUnavailable));
            Assert.That(disabled.Resource.IsEffectivelyEnabled, Is.False);
            Assert.That(disabled.Resource.AllocatedBytes, Is.Zero);
            Assert.That(disabled.Resource.DescriptorCount, Is.Zero);
        });
    }

    [Test]
    public void C3BindlessSlots_AreImmutableAndKeepSidecarOutsideGuidingOwnership()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SimpleDdgiGuidingBindlessSlots.DistributionBank0, Is.EqualTo(200));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DistributionBank1, Is.EqualTo(201));
            Assert.That(SimpleDdgiGuidingBindlessSlots.TrainingScratch, Is.EqualTo(202));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar, Is.EqualTo(203));
            Assert.That(SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
                Is.Not.EqualTo(SimpleDdgiGuidingBindlessSlots.TrainingScratch));
        });
    }

    [Test]
    public void VulkanRuntimeSources_KeepSlot203SourceCacheOwnedAndSequenceAllC3Stages()
    {
        string runtime = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiGuidingVulkanRuntime.cs");
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiGuidingGpuPass.cs");
        string passContract = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiGuidingPasses.cs");
        string extractor = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_extract.comp");

        Assert.Multiple(() =>
        {
            Assert.That(runtime, Does.Contain("VariablePdfProjectionUnavailable"));
            Assert.That(runtime, Does.Contain("TryReadCompletedFrame"));
            Assert.That(runtime, Does.Contain("CompleteBuild"));
            Assert.That(runtime, Does.Contain("SourceCachePriorAccessStageMask"));
            Assert.That(runtime, Does.Contain("ValidateFixedBindlessSlots"));
            Assert.That(runtime,
                Does.Contain("C3 may publish only valid fixed slots 200, 201, and 202."));
            Assert.That(runtime, Does.Contain("TryClaimBuildDescriptorSetsNoLock"));
            Assert.That(runtime, Does.Contain("ClearFrameDescriptorClaimsNoLock"));
            Assert.That(runtime, Does.Not.Contain(
                "Register(SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar"));
            Assert.That(runtime, Does.Not.Contain(
                "RegisterStorageBuffer(\n                SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar"));
            Assert.That(pass, Does.Contain("SimpleDdgiGuidingGpuPassNames.TrainShader"));
            Assert.That(pass, Does.Contain("SimpleDdgiGuidingGpuPassNames.BuildShader"));
            Assert.That(pass, Does.Contain("SimpleDdgiGuidingGpuPassNames.ValidateShader"));
            Assert.That(pass, Does.Contain("SimpleDdgiGuidingGpuPassNames.SampleShader"));
            Assert.That(pass, Does.Contain("ResolveExtractShader"));
            Assert.That(pass, Does.Contain("RecordExtractInputBarriers"));
            Assert.That(passContract,
                Does.Contain("ddgi_guiding_extract_legacy.comp.spv"));
            Assert.That(passContract,
                Does.Contain("ddgi_guiding_extract_validate.comp.spv"));
            Assert.That(passContract,
                Does.Contain("ddgi_guiding_extract_packed.comp.spv"));
            Assert.That(passContract, Does.Contain("ddgi_guiding_train.comp.spv"));
            Assert.That(passContract, Does.Contain("ddgi_guiding_build.comp.spv"));
            Assert.That(passContract, Does.Contain("ddgi_guiding_validate.comp.spv"));
            Assert.That(passContract, Does.Contain("ddgi_guiding_sample.comp.spv"));
            Assert.That(pass, Does.Contain("RecordTransferToComputeBarrier"));
            Assert.That(pass, Does.Contain("RecordSidecarReuseBarrier"));
            Assert.That(pass, Does.Contain("RecordSidecarConsumerBarrier"));
            Assert.That(pass, Does.Contain("RenderingConstants.FramesInFlight * PassKindCount"));
            Assert.That(extractor,
                Does.Contain("SimpleDdgiUpdateMatchesLiveAddress"));
            Assert.That(extractor,
                Does.Contain("workItem.sourceLightingGeneration"));
            Assert.That(extractor,
                Does.Contain("SimpleDdgiGuidingTrainingPdf(payload)"));
            Assert.That(extractor,
                Does.Contain("SIMPLE_DDGI_SOURCE_MODE_FULL_TRACE"));
            Assert.That(extractor,
                Does.Contain("layout(std430, set = 2, binding = 0)"));
        });
    }

    [Test]
    public void C3NativeInterfaces_UseFrozenWordAddressingAndDivisionFreeOverflowChecks()
    {
        string pass = ReadRepoText(
            "Njulf.Rendering",
            "Pipeline",
            "SimpleDdgiGuidingGpuPass.cs");
        string runtime = ReadRepoText(
            "Njulf.Rendering",
            "Resources",
            "SimpleDdgiGuidingVulkanRuntime.cs");
        string arithmetic = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_arithmetic.glsl");
        string build = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_build.comp");
        string sample = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_sample.comp");
        string validate = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_validate.comp");
        string prepare = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_prepare.comp");
        string extract = ReadRepoText(
            "Njulf.Shaders",
            "ddgi_guiding_extract.comp");

        Assert.Multiple(() =>
        {
            Assert.That(arithmetic, Does.Contain("umulExtended"));
            Assert.That(build, Does.Contain("guidingBuildWorkItems.words"));
            Assert.That(sample, Does.Contain("guidingSampleRequests.words"));
            Assert.That(sample, Does.Contain("guidingSamplePayloads.words"));
            Assert.That(validate, Does.Contain("guidingValidatePublication.words"));
            Assert.That(prepare, Does.Contain("guidingPrepareBuildWork.words"));
            Assert.That(prepare, Does.Contain("readBankIndex == 0xffffffffu"));
            Assert.That(prepare, Does.Contain(
                "guidingPreparePc.physicalProbeCapacity > params.physicalProbeCapacity"));
            Assert.That(prepare, Does.Not.Contain(
                "params.physicalProbeCapacity != guidingPreparePc.physicalProbeCapacity"));
            Assert.That(extract, Does.Contain(
                "guidingExtractPc.physicalProbeCapacity <=\n            params.physicalProbeCapacity"));
            Assert.That(extract, Does.Not.Contain(
                "params.physicalProbeCapacity ==\n            guidingExtractPc.physicalProbeCapacity"));
            Assert.That(runtime, Does.Contain(
                "guiding-gpu-preparation-produced-no-eligible-work"));
            Assert.That(runtime, Does.Contain(
                "SimpleDdgiGuidingPublicationFailure.EmptyPublication"));
            Assert.That(pass, Does.Not.Contain("private PipelineCache _pipelineCache"));
            Assert.That(pass, Does.Not.Contain("CreatePipelineCache();"));
        });
    }

    private static SimpleDdgiGuidingSourceCacheHandshake CreateValidHandshake() =>
        new(
            IsAvailable: true,
            GuidingAbiVersion: SimpleDdgiGuidingGpuAbi.Version,
            SourceCacheOwnsDirectionPdfSidecar: true,
            DirectionPdfSidecarBindlessSlot:
                (uint)SimpleDdgiGuidingBindlessSlots.DirectionPdfSidecar,
            DirectionPdfSidecar: new BufferHandle(index: 9, generation: 1u),
            DirectionPdfSidecarOffsetBytes: 0UL,
            DirectionPdfSidecarBytes:
                4UL * SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount,
            DirectionPdfSidecarCapacity: 4u,
            DirectionPdfSidecarStrideBytes:
                SimpleDdgiGuidingGpuAbi.SamplePayloadByteCount,
            SourceCachePriorAccessStageMask: PipelineStageFlags2.ComputeShaderBit,
            SourceCachePriorAccessMask: AccessFlags2.ShaderStorageReadBit,
            ConsumerAcceptsGenerationTimePdf: true,
            ConsumerSupportsVariablePdfProjection: true,
            ConsumerReadStageMask: PipelineStageFlags2.ComputeShaderBit,
            ConsumerReadAccessMask: AccessFlags2.ShaderStorageReadBit);

    private static SimpleDdgiGuidingBuildWorkload CreateValidBuildWorkload() =>
        new SimpleDdgiGuidingBuildWorkload(
            TargetProposalEpoch: 3u,
            TrainingRecords: CreateBuffer(
                index: 1,
                count: SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
                stride: SimpleDdgiGuidingGpuAbi.TrainingRecordByteCount),
            TrainingWorkItems: CreateBuffer(
                index: 2,
                count: 2u,
                stride: SimpleDdgiGuidingGpuAbi.TrainingWorkItemByteCount),
            BuildWorkItems: CreateBuffer(
                index: 3,
                count: 2u,
                stride: SimpleDdgiGuidingGpuAbi.BuildWorkItemByteCount),
            ValidationCounters: CreateBuffer(
                index: 4,
                count: SimpleDdgiGuidingGpuAbi.ValidationCounterWordCount,
                stride: sizeof(uint)),
            ExpectedHeaders:
            new[]
            {
                new SimpleDdgiGuidingExpectedProbeHeader(1u, 10u, 4u),
                new SimpleDdgiGuidingExpectedProbeHeader(2u, 11u, 4u)
            })
        {
            TraceTrainingSource = new SimpleDdgiGuidingTraceTrainingSource(
                IsAvailable: true,
                TraceDispatchCompleted: true,
                StoragePackingMode: SimpleDdgiStoragePackingMode.Legacy,
                ParamsBufferIndex: (uint)BindlessIndex.SimpleDdgiParamsBuffer,
                RayResultScratchBufferIndex:
                    (uint)BindlessIndex.SimpleDdgiRayResultScratchBuffer,
                ProbeUpdateQueueBufferIndex:
                    (uint)BindlessIndex.SimpleDdgiProbeUpdateQueueBuffer,
                Params: CreateBuffer(index: 5, count: 64u, stride: sizeof(uint)),
                RayResultScratch: CreateBuffer(index: 6, count: 128u, stride: 32u),
                ProbeUpdateQueue: CreateBuffer(
                    index: 7,
                    count: 2u,
                    stride: checked((uint)SimpleDdgiMemoryPlan.ProbeUpdateBytes)))
            {
                GuidingTracePayloadScratch = CreateBuffer(
                    index: 6,
                    count: 128u,
                    stride: checked((uint)SimpleDdgiMemoryPlan
                        .GuidingTraceDirectionRecordBytes))
            }
        };

    private static SimpleDdgiGuidingExternalBuffer CreateBuffer(
        int index,
        uint count,
        uint stride) =>
        new(
            new BufferHandle(index, 1u),
            OffsetBytes: 0UL,
            RangeBytes: checked((ulong)count * stride),
            ElementCount: count,
            ElementStrideBytes: stride,
            LastWriterStageMask: PipelineStageFlags2.ComputeShaderBit,
            LastWriterAccessMask: AccessFlags2.ShaderStorageWriteBit);

    private static SimpleDdgiGuidingLayout CreateLayout() =>
        SimpleDdgiGuidingLayoutCompiler.Compile(
            new SimpleDdgiGuidingLayoutRequest(
                SimpleDdgiGuidingDistributionConfiguration.EightByEight,
                PhysicalProbeCapacity: 8,
                ScheduledGuidedProbeCapacity: 2,
                StorageAlignmentBytes: 16UL,
                AllocateValidationReferenceBank: false));

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
