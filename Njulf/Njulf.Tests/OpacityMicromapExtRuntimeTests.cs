using System.Security.Cryptography;
using Njulf.Assets.Cooked;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class OpacityMicromapExtRuntimeTests
{
    [Test]
    public void CapabilityInspector_RequiresEnabledFeatureChainAndBlasAttachment()
    {
        VulkanExtOpacityMicromapFeatureSnapshot disabledExtension = Snapshot() with
        {
            ExtensionEnabled = false
        };
        VulkanExtOpacityMicromapFeatureSnapshot noAttachment = Snapshot() with
        {
            BlasOpacityAttachmentEnabled = false
        };
        VulkanExtOpacityMicromapFeatureSnapshot noCompactionQuery = Snapshot() with
        {
            CompactedSizeQueryEnabled = false
        };

        OpacityMicromapExtCapabilityReport disabled =
            VulkanExtOpacityMicromapCapabilityInspector.Evaluate(disabledExtension);
        OpacityMicromapExtCapabilityReport attachment =
            VulkanExtOpacityMicromapCapabilityInspector.Evaluate(noAttachment);
        OpacityMicromapExtCapabilityReport compaction =
            VulkanExtOpacityMicromapCapabilityInspector.Evaluate(
                noCompactionQuery,
                requireCompaction: true);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.SupportsPublication, Is.False);
            Assert.That(disabled.Capabilities.ExtensionAvailable, Is.False);
            Assert.That(disabled.Failure,
                Is.EqualTo(OpacityMicromapExtCapabilityFailure.ExtensionNotEnabled));
            Assert.That(attachment.SupportsPublication, Is.False);
            Assert.That(attachment.Failure,
                Is.EqualTo(OpacityMicromapExtCapabilityFailure.BlasAttachmentNotIntegrated));
            Assert.That(compaction.SupportsPublication, Is.False);
            Assert.That(compaction.Failure,
                Is.EqualTo(OpacityMicromapExtCapabilityFailure.CompactionRequiredButUnavailable));
        });
    }

    [Test]
    public async Task Backend_PublishesOnlyCompleteLifecycle_ReusesContent_AndRetiresAtLatestUse()
    {
        var host = new FakeNativeLifecycleHost();
        var backend = new VulkanExtOpacityMicromapBackend(host);
        OpacityMicromapBackendBuildRequest request = Request();

        OpacityMicromapBackendBuildResult first = await backend.BuildAsync(
            request,
            CancellationToken.None);
        OpacityMicromapBackendBuildResult second = await backend.BuildAsync(
            request with { PublicationGeneration = 2UL },
            CancellationToken.None);

        var firstLease = (IOpacityMicromapRetirementLease)first.Lease!;
        var secondLease = (IOpacityMicromapRetirementLease)second.Lease!;
        Assert.Multiple(() =>
        {
            Assert.That(first.Succeeded, Is.True, first.Detail);
            Assert.That(second.Succeeded, Is.True, second.Detail);
            Assert.That(first.Lease!.IsReadyForTlasPublication, Is.True);
            Assert.That(first.Lease.PublicationGeneration, Is.EqualTo(1UL));
            Assert.That(second.Lease!.PublicationGeneration, Is.EqualTo(1UL),
                "Content reuse retains the resource generation rather than claiming a new native build.");
            Assert.That(host.BuildCalls, Is.EqualTo(1));
            Assert.That(host.DisposeUnpublishedCalls, Is.EqualTo(0));
        });
        firstLease.RecordLastUse(GpuCompletionToken.ForFrameFence(10UL));
        secondLease.RecordLastUse(GpuCompletionToken.ForFrameFence(11UL));
        firstLease.Dispose();
        secondLease.Dispose();

        OpacityMicromapExtRuntimeSnapshot snapshot = backend.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(host.RetireCalls, Is.EqualTo(1));
            Assert.That(host.LastRetirementToken, Is.EqualTo(GpuCompletionToken.ForFrameFence(11UL)));
            Assert.That(snapshot.PublishedVariantCount, Is.EqualTo(0));
            Assert.That(snapshot.ActiveLeaseCount, Is.EqualTo(0));
            Assert.That(snapshot.PublishedResidentBytes, Is.EqualTo(0UL));
            Assert.That(snapshot.SuccessfulNativeBuildCount, Is.EqualTo(1UL));
        });
    }

    [Test]
    public async Task Backend_RejectsIncompleteReceipt_AndReturnsOrdinaryCandidatePath()
    {
        var host = new FakeNativeLifecycleHost
        {
            ReceiptMutation = static receipt => receipt with
            {
                Lifecycle = receipt.Lifecycle with
                {
                    MicromapWritesVisibleToBlasBuild = false
                }
            }
        };
        var backend = new VulkanExtOpacityMicromapBackend(host);

        OpacityMicromapBackendBuildResult result = await backend.BuildAsync(
            Request(),
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Lease, Is.Null);
            Assert.That(result.UsesOrdinaryCandidatePath, Is.True);
            Assert.That(result.FallbackReason,
                Is.EqualTo(OpacityMicromapBackendFallbackReason.BuildFailed));
            Assert.That(host.BuildCalls, Is.EqualTo(1));
            Assert.That(host.DisposeUnpublishedCalls, Is.EqualTo(1));
            Assert.That(host.RetireCalls, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task Backend_RequiresExistingCandidateTestedFallback_AndRejectsCrossDomainRetirement()
    {
        var unavailableHost = new FakeNativeLifecycleHost
        {
            PlanAvailable = false
        };
        var unavailableBackend = new VulkanExtOpacityMicromapBackend(unavailableHost);
        OpacityMicromapBackendBuildResult unavailable = await unavailableBackend.BuildAsync(
            Request(),
            CancellationToken.None);

        var host = new FakeNativeLifecycleHost();
        var backend = new VulkanExtOpacityMicromapBackend(host);
        OpacityMicromapBackendBuildResult active = await backend.BuildAsync(
            Request(),
            CancellationToken.None);
        var lease = (IOpacityMicromapRetirementLease)active.Lease!;

        Assert.Multiple(() =>
        {
            Assert.That(unavailable.Succeeded, Is.False);
            Assert.That(unavailable.FallbackReason,
                Is.EqualTo(OpacityMicromapBackendFallbackReason.BuildUnavailable));
            Assert.That(unavailableHost.BuildCalls, Is.EqualTo(0));
            Assert.That(active.Succeeded, Is.True, active.Detail);
            Assert.Throws<ArgumentException>(() => lease.RecordLastUse(
                GpuCompletionToken.ForTimeline(1UL, 2UL)));
        });

        lease.Dispose();
        Assert.That(host.LastRetirementToken,
            Is.EqualTo(GpuCompletionToken.ForFrameFence(1UL)),
            "A rejected cross-domain signal cannot shorten or alter the safe build completion.");
    }

    [Test]
    public async Task Backend_EnforcesAggregatePublishedCacheBudget()
    {
        var host = new FakeNativeLifecycleHost();
        var backend = new VulkanExtOpacityMicromapBackend(
            host,
            new OpacityMicromapExtBuildPolicy(
                EnableCompaction: true,
                RequireCompaction: false,
                MaximumPublishedResidentBytes: 3_000_000UL,
                MinimumCompactionSavingsBytes: 64UL * 1024UL,
                MinimumCompactionSavingsFraction: 0.10));

        OpacityMicromapBackendBuildResult first = await backend.BuildAsync(
            Request(contentKeyByte: 1),
            CancellationToken.None);
        OpacityMicromapBackendBuildResult second = await backend.BuildAsync(
            Request(contentKeyByte: 9),
            CancellationToken.None);

        OpacityMicromapExtRuntimeSnapshot active = backend.GetSnapshot();
        Assert.Multiple(() =>
        {
            Assert.That(first.Succeeded, Is.True, first.Detail);
            Assert.That(second.Succeeded, Is.False);
            Assert.That(second.FallbackReason,
                Is.EqualTo(OpacityMicromapBackendFallbackReason.BuildFailed));
            Assert.That(second.Detail,
                Is.EqualTo("published-omm-cache-resident-byte-budget-exceeded"));
            Assert.That(host.BuildCalls, Is.EqualTo(2));
            Assert.That(host.DisposeUnpublishedCalls, Is.EqualTo(1));
            Assert.That(active.PublishedVariantCount, Is.EqualTo(1));
            Assert.That(active.PublishedResidentBytes, Is.EqualTo(2_600_000UL));
        });

        first.Lease!.Dispose();
        Assert.That(host.RetireCalls, Is.EqualTo(1));
    }

    [Test]
    public void BuildPolicy_CompactsOnlyWhenBothMeasuredThresholdsAreMet()
    {
        var policy = new OpacityMicromapExtBuildPolicy(
            EnableCompaction: true,
            RequireCompaction: false,
            MaximumPublishedResidentBytes: 1024UL * 1024UL,
            MinimumCompactionSavingsBytes: 100UL,
            MinimumCompactionSavingsFraction: 0.20);

        Assert.Multiple(() =>
        {
            Assert.That(policy.IsValid, Is.True);
            Assert.That(policy.ShouldCompact(1_000UL, 800UL), Is.True);
            Assert.That(policy.ShouldCompact(1_000UL, 950UL), Is.False,
                "The absolute threshold alone is insufficient.");
            Assert.That(policy.ShouldCompact(1_000UL, 850UL), Is.False,
                "The relative threshold alone is insufficient.");
            Assert.That(policy.ShouldCompact(1_000UL, 1_000UL), Is.False);
        });
    }

    [Test]
    public void CompactionQueryMiss_IsFatalOnlyWhenCompactionIsRequired()
    {
        OpacityMicromapExtBuildPolicy optional =
            OpacityMicromapExtBuildPolicy.Default;
        OpacityMicromapExtBuildPolicy required = optional with
        {
            RequireCompaction = true
        };
        OpacityMicromapExtBuildPolicy disabled = required with
        {
            EnableCompaction = false
        };

        Assert.Multiple(() =>
        {
            Assert.That(AccelerationStructureManager
                    .IsRequiredCompactionQueryFailure(optional),
                Is.False,
                "A valid uncompacted result remains authoritative under the default policy.");
            Assert.That(AccelerationStructureManager
                    .IsRequiredCompactionQueryFailure(required),
                Is.True);
            Assert.That(AccelerationStructureManager
                    .IsRequiredCompactionQueryFailure(disabled),
                Is.False);
        });
    }

    private static VulkanExtOpacityMicromapFeatureSnapshot Snapshot() => new(
        ExtensionAdvertised: true,
        ExtensionEnabled: true,
        MicromapFeatureEnabled: true,
        AccelerationStructureExtensionEnabled: true,
        BufferDeviceAddressEnabled: true,
        DeferredHostOperationsExtensionEnabled: true,
        NativeDispatchLoaded: true,
        CommandBufferBuildEnabled: true,
        CompactedSizeQueryEnabled: true,
        BlasOpacityAttachmentEnabled: true,
        MaximumFourStateSubdivisionLevel: 4U);

    private static OpacityMicromapBackendBuildRequest Request(
        byte contentKeyByte = 1,
        ulong publicationGeneration = 1UL) => new(
        ContentKey: Key(contentKeyByte),
        Payload: Payload(contentKeyByte),
        AccelerationStructureBuildAbi: 5U,
        PublicationGeneration: publicationGeneration);

    private static OpacityMicromapCookedPayload Payload(byte contentKeyByte = 1) =>
        OpacityMicromapCookedPayload.Create(
            cookAbi: 7U,
            sourceContentHash: Key(contentKeyByte),
            sdkProvenanceHash: Key(2),
            maximumSubdivisionLevel: 4U,
            primitiveCount: 1U,
            descriptorCount: 1U,
            materialContracts: new[] { MaterialContract() },
            usageHistogram: new[]
            {
                new OpacityMicromapUsage(OpacityMicromapFormat.FourState, 1U, 1UL)
            },
            ommData: new byte[] { 1, 2, 3, 4, 5 },
            indexData: new byte[] { 6, 7, 8, 9 },
            descriptorData: new byte[] { 10, 11 });

    private static OpacityMicromapMaterialContract MaterialContract() => new(
        MaterialSlot: 3U,
        FirstPrimitive: 0U,
        PrimitiveCount: 1U,
        TexCoordSet: 0,
        UvTransform: OpacityMicromapUvTransformBits.Identity,
        TextureContentHash: Key(3),
        TextureFormatAndMipHash: Key(4),
        Sampler: OpacityMicromapEligibilityInput.ExactStaticMask.Sampler,
        MaterialAlphaBits: Bits(1.0f),
        UniformVertexAlphaBits: Bits(1.0f),
        AlphaCutoffBits: Bits(0.5f),
        FixedLodBits: Bits(0.0f),
        AlphaContractRevision: 8U,
        ShaderAbiRevision: 9U);

    private static uint Bits(float value) =>
        unchecked((uint)BitConverter.SingleToInt32Bits(value));

    private static OpacityMicromapContentKey Key(byte value) =>
        OpacityMicromapContentKey.FromSha256(
            SHA256.HashData(new[] { value }));

    private sealed class FakeNativeLifecycleHost :
        IOpacityMicromapExtNativeLifecycleHost
    {
        public bool PlanAvailable { get; init; } = true;
        public Func<OpacityMicromapExtBuildReceipt, OpacityMicromapExtBuildReceipt>?
            ReceiptMutation { get; init; }
        public int BuildCalls { get; private set; }
        public int DisposeUnpublishedCalls { get; private set; }
        public int RetireCalls { get; private set; }
        public GpuCompletionToken LastRetirementToken { get; private set; }

        public OpacityMicromapExtCapabilityReport CapabilityReport =>
            VulkanExtOpacityMicromapCapabilityInspector.Evaluate(Snapshot());

        public bool TryCreateBuildPlan(
            OpacityMicromapBackendBuildRequest request,
            OpacityMicromapExtBuildPolicy policy,
            out OpacityMicromapExtBuildPlan plan,
            out string detail)
        {
            if (!PlanAvailable)
            {
                plan = default;
                detail = "ordinary-candidate-tested-fallback-not-resident";
                return false;
            }

            StaticBlasVariantKey plain = StaticBlasVariantKey.Plain(
                Key(50),
                StaticBlasRayGeometryPolicy.CandidateConfirmationRequired,
                request.AccelerationStructureBuildAbi);
            plan = new OpacityMicromapExtBuildPlan(
                plain with
                {
                    OpacityMicromapContentKeyOrNull = request.ContentKey
                },
                plain,
                PlainFallbackBlasHandle: 500UL,
                PlainFallbackResidentBytes: 4_096UL);
            detail = "candidate-tested-plain-fallback-ready";
            return true;
        }

        public ValueTask<OpacityMicromapExtBuildReceipt>
            BuildAndWaitForPublicationAsync(
                OpacityMicromapBackendBuildRequest request,
                OpacityMicromapExtBuildPlan plan,
                OpacityMicromapExtBuildPolicy policy,
                CancellationToken cancellationToken)
        {
            BuildCalls++;
            var artifacts = new OpacityMicromapExtPublishedArtifacts(
                plan.OpacityVariantKey,
                new[]
                {
                    new OpacityMicromapExtNativeResource(
                        OpacityMicromapExtNativeResourceKind.CompactedMicromapStorageBuffer,
                        101UL,
                        201UL,
                        600_000UL),
                    new OpacityMicromapExtNativeResource(
                        OpacityMicromapExtNativeResourceKind.MicromapObject,
                        102UL,
                        0UL,
                        0UL),
                    new OpacityMicromapExtNativeResource(
                        OpacityMicromapExtNativeResourceKind.BlasVariant,
                        103UL,
                        203UL,
                        2_000_000UL),
                    new OpacityMicromapExtNativeResource(
                        OpacityMicromapExtNativeResourceKind.DescriptorVisibleState,
                        104UL,
                        0UL,
                        0UL)
                });
            var lifecycle = new OpacityMicromapExtLifecycleEvidence(
                DeviceBuildSizesQueried: true,
                DeviceAddressableInputsUploaded: true,
                TransferWritesVisibleToMicromapBuild: true,
                MicromapObjectCreated: true,
                MicromapBuildRecorded: true,
                MicromapWritesVisibleToBlasBuild: true,
                CompactionQueryRecorded: true,
                CompactionCopyRecorded: true,
                CompactionCopyCompleted: true,
                BuildScratchRetiredBeforePublication: true,
                MatchingBlasBuiltAgainstFinalMicromap: true,
                BlasCompactionPerformed: true,
                BlasCompactionAfterFinalMicromap: true,
                GpuCompletionObserved: true);
            var receipt = new OpacityMicromapExtBuildReceipt(
                Succeeded: true,
                CompactionApplied: true,
                UncompactedMicromapBytes: 1_000_000UL,
                FinalMicromapBytes: 600_000UL,
                RetirementToken: GpuCompletionToken.ForFrameFence(1UL),
                Lifecycle: lifecycle,
                PublishedArtifacts: artifacts,
                Detail: "fake-native-ext-build-complete");
            if (ReceiptMutation is not null)
                receipt = ReceiptMutation(receipt);
            return ValueTask.FromResult(receipt);
        }

        public void DisposeUnpublished(OpacityMicromapExtPublishedArtifacts artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifacts);
            DisposeUnpublishedCalls++;
        }

        public void RetirePublished(
            OpacityMicromapExtPublishedArtifacts artifacts,
            GpuCompletionToken completion)
        {
            ArgumentNullException.ThrowIfNull(artifacts);
            RetireCalls++;
            LastRetirementToken = completion;
        }
    }
}
