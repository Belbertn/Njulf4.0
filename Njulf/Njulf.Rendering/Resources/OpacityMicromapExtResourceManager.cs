using System.Collections.ObjectModel;
using Njulf.Assets.Cooked;

namespace Njulf.Rendering.Resources;

/// <summary>
/// A production policy for the optional EXT build path.  Scratch and query
/// allocations remain owned by the native build host; this policy bounds the
/// aggregate published OMM/BLAS cache and defines when a measured compacted
/// result is worth retaining.
/// </summary>
public readonly record struct OpacityMicromapExtBuildPolicy(
    bool EnableCompaction,
    bool RequireCompaction,
    ulong MaximumPublishedResidentBytes,
    ulong MinimumCompactionSavingsBytes,
    double MinimumCompactionSavingsFraction)
{
    public static OpacityMicromapExtBuildPolicy Default { get; } = new(
        EnableCompaction: true,
        RequireCompaction: false,
        MaximumPublishedResidentBytes: 256UL * 1024UL * 1024UL,
        MinimumCompactionSavingsBytes: 64UL * 1024UL,
        MinimumCompactionSavingsFraction: 0.10);

    public bool IsValid =>
        MaximumPublishedResidentBytes != 0UL &&
        double.IsFinite(MinimumCompactionSavingsFraction) &&
        MinimumCompactionSavingsFraction is >= 0.0 and <= 1.0 &&
        (!RequireCompaction || EnableCompaction);

    /// <summary>
    /// Compaction is admitted only when both the absolute and relative saving
    /// thresholds are met.  This avoids adding an extra copy/query dependency
    /// for negligible memory reduction.
    /// </summary>
    public bool ShouldCompact(
        ulong uncompactedBytes,
        ulong compactedBytes)
    {
        if (!EnableCompaction || uncompactedBytes == 0UL ||
            compactedBytes >= uncompactedBytes)
        {
            return false;
        }

        ulong saved = uncompactedBytes - compactedBytes;
        return saved >= MinimumCompactionSavingsBytes &&
            (double)saved / uncompactedBytes >= MinimumCompactionSavingsFraction;
    }
}

public enum OpacityMicromapExtNativeResourceKind : byte
{
    InputDataBuffer = 0,
    PerPrimitiveIndexBuffer,
    DescriptorBuffer,
    UsageCountBuffer,
    MicromapStorageBuffer,
    CompactedMicromapStorageBuffer,
    MicromapObject,
    BlasVariant,
    DescriptorVisibleState
}

/// <summary>
/// A renderer-owned opaque native object.  Handles are intentionally numeric:
/// destruction remains with the object-owning Vulkan subsystem and this shared
/// lifetime layer must never reinterpret an EXT handle as a KHR object.
/// </summary>
public readonly record struct OpacityMicromapExtNativeResource(
    OpacityMicromapExtNativeResourceKind Kind,
    ulong Handle,
    ulong AllocationHandle,
    ulong ResidentBytes)
{
    public bool IsValid => Handle != 0UL;
}

/// <summary>
/// Persistent objects that must be retired as one generation.  Build scratch,
/// compaction queries, and staging uploads are not part of this set because the
/// native host must retire them once the build completion has been observed,
/// before the matching BLAS is published.
/// </summary>
public sealed class OpacityMicromapExtPublishedArtifacts
{
    private readonly ReadOnlyCollection<OpacityMicromapExtNativeResource>
        _resources;

    public OpacityMicromapExtPublishedArtifacts(
        StaticBlasVariantKey variantKey,
        IReadOnlyList<OpacityMicromapExtNativeResource> resources)
    {
        if (!variantKey.IsValid || !variantKey.HasOpacityMicromap)
            throw new ArgumentOutOfRangeException(nameof(variantKey));
        ArgumentNullException.ThrowIfNull(resources);

        VariantKey = variantKey;
        _resources = Array.AsReadOnly(resources.ToArray());
    }

    public StaticBlasVariantKey VariantKey { get; }
    public IReadOnlyList<OpacityMicromapExtNativeResource> Resources => _resources;

    public bool TryGetResidentBytes(out ulong residentBytes)
    {
        residentBytes = 0UL;
        try
        {
            foreach (OpacityMicromapExtNativeResource resource in _resources)
                residentBytes = checked(residentBytes + resource.ResidentBytes);
            return true;
        }
        catch (OverflowException)
        {
            residentBytes = 0UL;
            return false;
        }
    }

    public bool TryValidate(
        bool compactionApplied,
        out string detail)
    {
        if (_resources.Count == 0)
        {
            detail = "published-omm-artifacts-empty";
            return false;
        }

        var seen = new HashSet<(OpacityMicromapExtNativeResourceKind, ulong)>();
        var kinds = new HashSet<OpacityMicromapExtNativeResourceKind>();
        foreach (OpacityMicromapExtNativeResource resource in _resources)
        {
            if (!resource.IsValid)
            {
                detail = "published-omm-artifact-handle-invalid";
                return false;
            }
            if (!seen.Add((resource.Kind, resource.Handle)))
            {
                detail = "published-omm-artifact-duplicate-handle";
                return false;
            }
            kinds.Add(resource.Kind);
        }

        if (!kinds.Contains(OpacityMicromapExtNativeResourceKind.MicromapObject) ||
            !kinds.Contains(OpacityMicromapExtNativeResourceKind.BlasVariant))
        {
            detail = "published-omm-artifacts-missing-micromap-or-matching-blas";
            return false;
        }
        bool hasFinalStorage = compactionApplied
            ? kinds.Contains(
                OpacityMicromapExtNativeResourceKind.CompactedMicromapStorageBuffer)
            : kinds.Contains(
                OpacityMicromapExtNativeResourceKind.MicromapStorageBuffer);
        if (!hasFinalStorage)
        {
            detail = compactionApplied
                ? "compacted-micromap-storage-not-published"
                : "micromap-storage-not-published";
            return false;
        }
        if (!TryGetResidentBytes(out _))
        {
            detail = "published-omm-resident-byte-overflow";
            return false;
        }

        detail = "published-omm-artifacts-valid";
        return true;
    }
}

/// <summary>
/// A specific OMM BLAS candidate and its canonical ordinary fallback.  The
/// fallback must already exist and retain shader candidate confirmation before
/// a native OMM build can be requested; no error path is allowed to leave a
/// mesh without an ordinary alpha-tested variant.
/// </summary>
public readonly record struct OpacityMicromapExtBuildPlan(
    StaticBlasVariantKey OpacityVariantKey,
    StaticBlasVariantKey PlainFallbackVariantKey,
    ulong PlainFallbackBlasHandle,
    ulong PlainFallbackResidentBytes)
{
    public bool IsWellFormedFor(in OpacityMicromapBackendBuildRequest request)
    {
        if (!OpacityVariantKey.IsValid || !OpacityVariantKey.HasOpacityMicromap ||
            !PlainFallbackVariantKey.IsValid ||
            !PlainFallbackVariantKey.IsPlainFallback ||
            PlainFallbackBlasHandle == 0UL ||
            PlainFallbackResidentBytes == 0UL ||
            OpacityVariantKey.OpacityMicromapContentKeyOrNull != request.ContentKey ||
            OpacityVariantKey.AccelerationStructureBuildAbi !=
                request.AccelerationStructureBuildAbi ||
            PlainFallbackVariantKey.AccelerationStructureBuildAbi !=
                request.AccelerationStructureBuildAbi ||
            OpacityVariantKey.MeshGeometryKey != PlainFallbackVariantKey.MeshGeometryKey ||
            OpacityVariantKey.RayGeometryPolicy !=
                PlainFallbackVariantKey.RayGeometryPolicy)
        {
            return false;
        }

        return PlainFallbackVariantKey.RayGeometryPolicy is
            StaticBlasRayGeometryPolicy.CandidateConfirmationRequired or
            StaticBlasRayGeometryPolicy.TwoSidedCandidateConfirmationRequired;
    }
}

/// <summary>
/// Audit facts required before a native receipt may be published.  The fields
/// map to the required EXT sequence, rather than treating a successful API
/// return as proof that data dependencies or BLAS attachment were correct.
/// </summary>
public readonly record struct OpacityMicromapExtLifecycleEvidence(
    bool DeviceBuildSizesQueried,
    bool DeviceAddressableInputsUploaded,
    bool TransferWritesVisibleToMicromapBuild,
    bool MicromapObjectCreated,
    bool MicromapBuildRecorded,
    bool MicromapWritesVisibleToBlasBuild,
    bool CompactionQueryRecorded,
    bool CompactionCopyRecorded,
    bool CompactionCopyCompleted,
    bool BuildScratchRetiredBeforePublication,
    bool MatchingBlasBuiltAgainstFinalMicromap,
    bool BlasCompactionPerformed,
    bool BlasCompactionAfterFinalMicromap,
    bool GpuCompletionObserved)
{
    public bool HasRequiredCoreSteps =>
        DeviceBuildSizesQueried &&
        DeviceAddressableInputsUploaded &&
        TransferWritesVisibleToMicromapBuild &&
        MicromapObjectCreated &&
        MicromapBuildRecorded &&
        MicromapWritesVisibleToBlasBuild &&
        BuildScratchRetiredBeforePublication &&
        MatchingBlasBuiltAgainstFinalMicromap &&
        GpuCompletionObserved;

    public bool HasRequiredCompactionSteps =>
        CompactionQueryRecorded &&
        CompactionCopyRecorded &&
        CompactionCopyCompleted;

    public bool IsBlasCompactionOrderingValid =>
        !BlasCompactionPerformed || BlasCompactionAfterFinalMicromap;
}

/// <summary>
/// Result returned by the native lifecycle host.  A successful receipt is not
/// accepted unless GPU completion has already been observed: an
/// <see cref="IOpacityMicromapLease"/> must never advertise an object that is
/// still under construction to TLAS publication.
/// </summary>
public readonly record struct OpacityMicromapExtBuildReceipt(
    bool Succeeded,
    bool CompactionApplied,
    ulong UncompactedMicromapBytes,
    ulong FinalMicromapBytes,
    GpuCompletionToken RetirementToken,
    OpacityMicromapExtLifecycleEvidence Lifecycle,
    OpacityMicromapExtPublishedArtifacts? PublishedArtifacts,
    string Detail)
{
    public static OpacityMicromapExtBuildReceipt Failed(
        string detail,
        OpacityMicromapExtPublishedArtifacts? artifacts = null) => new(
            Succeeded: false,
            CompactionApplied: false,
            UncompactedMicromapBytes: 0UL,
            FinalMicromapBytes: 0UL,
            RetirementToken: default,
            Lifecycle: default,
            PublishedArtifacts: artifacts,
            Detail: detail);
}

/// <summary>
/// The only place that may touch native EXT objects.  Implementations own the
/// exact <c>MicromapBuildInfoEXT</c> buffers, query pool slots, synchronization
/// barriers, <c>AccelerationStructureTrianglesOpacityMicromapEXT</c> chain,
/// and matching static-BLAS cache.  The existing renderer does not yet supply
/// this integration host, so no default implementation claims hardware
/// resolution.
/// </summary>
public interface IOpacityMicromapExtNativeLifecycleHost
{
    OpacityMicromapExtCapabilityReport CapabilityReport { get; }

    /// <summary>
    /// Resolves the static mesh/BLAS domain and proves that its ordinary
    /// candidate-tested fallback is resident.  This method must allocate no
    /// EXT resource and must return false for content that cannot attach to the
    /// current static BLAS cache.
    /// </summary>
    bool TryCreateBuildPlan(
        OpacityMicromapBackendBuildRequest request,
        OpacityMicromapExtBuildPolicy policy,
        out OpacityMicromapExtBuildPlan plan,
        out string detail);

    /// <summary>
    /// Records all EXT work, submits it on the AS build queue, and awaits the
    /// specific completion primitive before returning a successful receipt.
    /// It must retain and retire scratch/query/source resources until that
    /// completion, build the matching BLAS against the final micromap object,
    /// and leave the ordinary fallback untouched on every failure.
    /// </summary>
    ValueTask<OpacityMicromapExtBuildReceipt> BuildAndWaitForPublicationAsync(
        OpacityMicromapBackendBuildRequest request,
        OpacityMicromapExtBuildPlan plan,
        OpacityMicromapExtBuildPolicy policy,
        CancellationToken cancellationToken);

    /// <summary>
    /// Takes ownership of artifacts that did not become a cached publication.
    /// The host must destroy them only after their build completion is safe.
    /// </summary>
    void DisposeUnpublished(OpacityMicromapExtPublishedArtifacts artifacts);

    /// <summary>
    /// Enqueues a published generation for deferred destruction.  The caller
    /// supplies the latest known frame-fence or timeline completion token from
    /// all uses of the shared BLAS/micromap generation.
    /// </summary>
    void RetirePublished(
        OpacityMicromapExtPublishedArtifacts artifacts,
        GpuCompletionToken completion);
}

/// <summary>
/// Extra lease operation required by a renderer that binds a cached OMM
/// variant.  Every submission using the lease must call
/// <see cref="RecordLastUse"/> with the same completion domain before releasing
/// its reference.  This makes stale descriptor/BLAS retirement fail closed.
/// </summary>
public interface IOpacityMicromapRetirementLease : IOpacityMicromapLease
{
    void RecordLastUse(GpuCompletionToken completion);
}

public readonly record struct OpacityMicromapExtRuntimeSnapshot(
    int PublishedVariantCount,
    int ActiveLeaseCount,
    ulong PublishedResidentBytes,
    ulong SuccessfulNativeBuildCount,
    ulong FallbackCount,
    ulong InvalidReceiptCount,
    ulong RetirementFailureCount,
    string? LastRetirementFailure);

/// <summary>
/// Content-keyed C1 runtime manager.  It is deliberately a native lifecycle
/// coordinator, not an emulation: without a real host that owns the static
/// BLAS builder it always returns the canonical ordinary candidate path.
/// Reused content returns reference-counted leases and retires its OMM object,
/// matching BLAS, buffers, and descriptor-visible state as one generation.
/// </summary>
public sealed class VulkanExtOpacityMicromapBackend : IExtOpacityMicromapBackend
{
    private readonly IOpacityMicromapExtNativeLifecycleHost _nativeHost;
    private readonly OpacityMicromapExtBuildPolicy _policy;
    private readonly object _sync = new();
    private readonly Dictionary<OpacityMicromapExtCacheKey, SharedEntry> _cache = new();
    private int _activeLeaseCount;
    private ulong _publishedResidentBytes;
    private ulong _successfulNativeBuildCount;
    private ulong _fallbackCount;
    private ulong _invalidReceiptCount;
    private ulong _retirementFailureCount;
    private string? _lastRetirementFailure;

    public VulkanExtOpacityMicromapBackend(
        IOpacityMicromapExtNativeLifecycleHost nativeHost,
        OpacityMicromapExtBuildPolicy policy = default)
    {
        _nativeHost = nativeHost ?? throw new ArgumentNullException(nameof(nativeHost));
        _policy = policy == default ? OpacityMicromapExtBuildPolicy.Default : policy;
        if (!_policy.IsValid)
            throw new ArgumentOutOfRangeException(nameof(policy));
    }

    public OpacityMicromapBackendKind Kind =>
        OpacityMicromapBackendKind.VulkanExtFourState;

    public OpacityMicromapRuntimeCapabilities Capabilities =>
        _nativeHost.CapabilityReport.Capabilities;

    public OpacityMicromapExtRuntimeSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new OpacityMicromapExtRuntimeSnapshot(
                _cache.Count,
                _activeLeaseCount,
                _publishedResidentBytes,
                _successfulNativeBuildCount,
                _fallbackCount,
                _invalidReceiptCount,
                _retirementFailureCount,
                _lastRetirementFailure);
        }
    }

    public async ValueTask<OpacityMicromapBackendBuildResult> BuildAsync(
        OpacityMicromapBackendBuildRequest request,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Fallback(
                OpacityMicromapBackendFallbackReason.Cancelled,
                "opacity-micromap-build-cancelled-before-native-work");

        if (!request.IsWellFormed || request.PublicationGeneration == 0UL)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.PayloadRejected,
                "opacity-micromap-build-request-malformed");
        }
        if (!TryValidatePayload(request, out string payloadDetail))
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.PayloadRejected,
                payloadDetail);
        }

        OpacityMicromapExtCapabilityReport capability =
            _nativeHost.CapabilityReport;
        if (!capability.SupportsPublication ||
            !capability.Capabilities.SupportsExtFourState ||
            request.Payload.MaximumSubdivisionLevel >
                capability.Capabilities.MaximumFourStateSubdivisionLevel)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ExtCapabilityUnavailable,
                capability.Detail);
        }
        if (_policy.RequireCompaction &&
            !capability.Capabilities.CompactionAvailable)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ExtCapabilityUnavailable,
                "required-omm-compaction-unavailable");
        }

        if (!_nativeHost.TryCreateBuildPlan(
                request,
                _policy,
                out OpacityMicromapExtBuildPlan plan,
                out string planDetail) ||
            !plan.IsWellFormedFor(request))
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.BuildUnavailable,
                string.IsNullOrWhiteSpace(planDetail)
                    ? "matching-candidate-tested-fallback-or-static-blas-domain-unavailable"
                    : planDetail);
        }

        var cacheKey = new OpacityMicromapExtCacheKey(
            request.ContentKey,
            plan.OpacityVariantKey);
        if (TryAcquireCached(cacheKey, out IOpacityMicromapLease? cachedLease))
        {
            return new OpacityMicromapBackendBuildResult(
                true,
                cachedLease,
                OpacityMicromapBackendFallbackReason.None,
                "reused-published-vulkan-ext-opacity-micromap-variant");
        }

        OpacityMicromapExtBuildReceipt receipt;
        try
        {
            receipt = await _nativeHost.BuildAndWaitForPublicationAsync(
                request,
                plan,
                _policy,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.Cancelled,
                "opacity-micromap-native-build-cancelled");
        }
        catch (Exception exception)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.BuildFailed,
                $"opacity-micromap-native-build-threw-{exception.GetType().Name}");
        }

        if (!TryValidateReceipt(request, plan, receipt, out string receiptDetail))
        {
            DisposeUnpublishedQuietly(receipt.PublishedArtifacts);
            lock (_sync)
                _invalidReceiptCount++;
            return Fallback(
                OpacityMicromapBackendFallbackReason.BuildFailed,
                receiptDetail);
        }

        OpacityMicromapExtPublishedArtifacts artifacts = receipt.PublishedArtifacts!;
        if (!artifacts.TryGetResidentBytes(out ulong residentBytes) ||
            residentBytes > _policy.MaximumPublishedResidentBytes)
        {
            DisposeUnpublishedQuietly(artifacts);
            return Fallback(
                OpacityMicromapBackendFallbackReason.BuildFailed,
                "published-omm-variant-exceeds-resident-byte-policy");
        }

        SharedEntry? loser = null;
        bool residentBudgetRejected = false;
        IOpacityMicromapLease? lease = null;
        lock (_sync)
        {
            if (_cache.TryGetValue(cacheKey, out SharedEntry? existing))
            {
                existing.ReferenceCount++;
                _activeLeaseCount++;
                loser = new SharedEntry(
                    cacheKey,
                    artifacts,
                    receipt.RetirementToken,
                    request.PublicationGeneration,
                    residentBytes);
                lease = new PublishedLease(this, existing);
            }
            else if (_publishedResidentBytes >
                _policy.MaximumPublishedResidentBytes ||
                residentBytes > _policy.MaximumPublishedResidentBytes -
                    _publishedResidentBytes)
            {
                residentBudgetRejected = true;
            }
            else
            {
                var entry = new SharedEntry(
                    cacheKey,
                    artifacts,
                    receipt.RetirementToken,
                    request.PublicationGeneration,
                    residentBytes);
                _cache.Add(cacheKey, entry);
                _activeLeaseCount++;
                _publishedResidentBytes = checked(_publishedResidentBytes + residentBytes);
                _successfulNativeBuildCount++;
                lease = new PublishedLease(this, entry);
            }
        }

        if (residentBudgetRejected)
        {
            DisposeUnpublishedQuietly(artifacts);
            return Fallback(
                OpacityMicromapBackendFallbackReason.BuildFailed,
                "published-omm-cache-resident-byte-budget-exceeded");
        }

        // A concurrent build may have published the same immutable content.
        // Its build completion has been observed, so this duplicate can be
        // returned to the native host as an unpublished generation.
        if (loser is not null)
            DisposeUnpublishedQuietly(loser.Artifacts);

        return new OpacityMicromapBackendBuildResult(
            true,
            lease!,
            OpacityMicromapBackendFallbackReason.None,
            "published-vulkan-ext-opacity-micromap-and-matching-blas-variant");
    }

    private bool TryAcquireCached(
        in OpacityMicromapExtCacheKey key,
        out IOpacityMicromapLease? lease)
    {
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out SharedEntry? entry))
            {
                entry.ReferenceCount++;
                _activeLeaseCount++;
                lease = new PublishedLease(this, entry);
                return true;
            }
        }

        lease = null;
        return false;
    }

    private OpacityMicromapBackendBuildResult Fallback(
        OpacityMicromapBackendFallbackReason reason,
        string detail)
    {
        lock (_sync)
            _fallbackCount++;
        return OpacityMicromapBackendBuildResult.Fallback(reason, detail);
    }

    private bool TryValidatePayload(
        in OpacityMicromapBackendBuildRequest request,
        out string detail)
    {
        OpacityMicromapCookedPayload payload = request.Payload;
        if (payload.SourceContentHash != request.ContentKey ||
            payload.PayloadKind != OpacityMicromapPayloadKind.VulkanExtFourState ||
            payload.Format != OpacityMicromapFormat.FourState ||
            payload.CookAbi == 0U || payload.PrimitiveCount == 0U ||
            payload.DescriptorCount == 0U || payload.OmmData.IsEmpty ||
            payload.IndexData.IsEmpty || payload.DescriptorData.IsEmpty ||
            payload.DescriptorCount > payload.DescriptorData.Length ||
            payload.MaterialContracts.Count == 0 ||
            payload.UsageHistogram.Count == 0)
        {
            detail = "cooked-vulkan-ext-four-state-payload-malformed";
            return false;
        }
        if (payload.MaximumSubdivisionLevel == 0U ||
            payload.MaximumSubdivisionLevel >
                OpacityMicromapSubdivisionPolicy.AbsoluteMaximumSubdivisionLevel)
        {
            detail = "cooked-vulkan-ext-subdivision-level-invalid";
            return false;
        }

        foreach (OpacityMicromapMaterialContract contract in payload.MaterialContracts)
        {
            if (!contract.IsExactStaticMaskContract)
            {
                detail = "cooked-vulkan-ext-material-contract-not-exact-static-mask";
                return false;
            }
        }
        foreach (OpacityMicromapUsage usage in payload.UsageHistogram)
        {
            if (usage.Format != OpacityMicromapFormat.FourState ||
                usage.Count == 0UL ||
                usage.SubdivisionLevel > payload.MaximumSubdivisionLevel)
            {
                detail = "cooked-vulkan-ext-usage-histogram-invalid";
                return false;
            }
        }

        detail = "cooked-vulkan-ext-four-state-payload-valid";
        return true;
    }

    private bool TryValidateReceipt(
        in OpacityMicromapBackendBuildRequest request,
        in OpacityMicromapExtBuildPlan plan,
        in OpacityMicromapExtBuildReceipt receipt,
        out string detail)
    {
        if (!receipt.Succeeded)
        {
            detail = string.IsNullOrWhiteSpace(receipt.Detail)
                ? "native-omm-build-failed"
                : receipt.Detail;
            return false;
        }
        if (!receipt.RetirementToken.IsValid ||
            !receipt.Lifecycle.HasRequiredCoreSteps ||
            !receipt.Lifecycle.IsBlasCompactionOrderingValid ||
            receipt.PublishedArtifacts is null)
        {
            detail = "native-omm-build-receipt-missing-core-lifecycle-evidence";
            return false;
        }
        if (receipt.UncompactedMicromapBytes == 0UL ||
            receipt.FinalMicromapBytes == 0UL ||
            receipt.FinalMicromapBytes > receipt.UncompactedMicromapBytes)
        {
            detail = "native-omm-build-receipt-micromap-size-invalid";
            return false;
        }
        if (receipt.CompactionApplied)
        {
            if (!receipt.Lifecycle.HasRequiredCompactionSteps ||
                !_policy.ShouldCompact(
                    receipt.UncompactedMicromapBytes,
                    receipt.FinalMicromapBytes))
            {
                detail = "native-omm-build-receipt-compaction-policy-or-lifecycle-invalid";
                return false;
            }
        }
        else if (_policy.RequireCompaction)
        {
            detail = "native-omm-build-receipt-required-compaction-not-applied";
            return false;
        }

        OpacityMicromapExtPublishedArtifacts artifacts =
            receipt.PublishedArtifacts;
        if (artifacts.VariantKey != plan.OpacityVariantKey ||
            artifacts.VariantKey.OpacityMicromapContentKeyOrNull !=
                request.ContentKey)
        {
            detail = "native-omm-build-receipt-artifact-variant-key-invalid";
            return false;
        }
        if (!artifacts.TryValidate(receipt.CompactionApplied, out string artifactDetail))
        {
            detail = "native-omm-build-receipt-artifacts-invalid-" + artifactDetail;
            return false;
        }

        detail = "native-omm-build-receipt-valid";
        return true;
    }

    private void RecordLastUse(SharedEntry entry, in GpuCompletionToken completion)
    {
        if (!completion.IsValid)
            throw new ArgumentOutOfRangeException(nameof(completion));

        lock (_sync)
        {
            if (!_cache.TryGetValue(entry.Key, out SharedEntry? current) ||
                !ReferenceEquals(current, entry))
            {
                throw new ObjectDisposedException(nameof(IOpacityMicromapLease));
            }
            if (!SameCompletionDomain(entry.LastUse, completion))
            {
                throw new ArgumentException(
                    "OMM lease use completion must stay in the build completion domain. " +
                    "Normalize cross-queue work to the renderer frame fence before recording it.",
                    nameof(completion));
            }
            if (completion.Value > entry.LastUse.Value)
                entry.LastUse = completion;
        }
    }

    private void Release(SharedEntry entry)
    {
        OpacityMicromapExtPublishedArtifacts? artifacts = null;
        GpuCompletionToken completion = default;
        lock (_sync)
        {
            if (!_cache.TryGetValue(entry.Key, out SharedEntry? current) ||
                !ReferenceEquals(current, entry) || entry.ReferenceCount <= 0)
            {
                return;
            }

            entry.ReferenceCount--;
            _activeLeaseCount--;
            if (entry.ReferenceCount != 0)
                return;

            _cache.Remove(entry.Key);
            _publishedResidentBytes -= entry.ResidentBytes;
            artifacts = entry.Artifacts;
            completion = entry.LastUse;
        }

        try
        {
            _nativeHost.RetirePublished(artifacts, completion);
        }
        catch (Exception exception)
        {
            // Do not retry a possibly partially accepted native retirement from
            // an IDisposable call.  The failure is surfaced in diagnostics and
            // the host remains responsible for retaining the generation rather
            // than risking early destruction.
            lock (_sync)
            {
                _retirementFailureCount++;
                _lastRetirementFailure =
                    $"{exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private void DisposeUnpublishedQuietly(
        OpacityMicromapExtPublishedArtifacts? artifacts)
    {
        if (artifacts is null)
            return;

        try
        {
            _nativeHost.DisposeUnpublished(artifacts);
        }
        catch (Exception exception)
        {
            lock (_sync)
            {
                _retirementFailureCount++;
                _lastRetirementFailure =
                    $"unpublished-{exception.GetType().Name}: {exception.Message}";
            }
        }
    }

    private static bool SameCompletionDomain(
        in GpuCompletionToken left,
        in GpuCompletionToken right) =>
        left.Kind == right.Kind && left.Identity == right.Identity;

    private readonly record struct OpacityMicromapExtCacheKey(
        OpacityMicromapContentKey ContentKey,
        StaticBlasVariantKey VariantKey);

    private sealed class SharedEntry
    {
        public SharedEntry(
            OpacityMicromapExtCacheKey key,
            OpacityMicromapExtPublishedArtifacts artifacts,
            GpuCompletionToken lastUse,
            ulong publicationGeneration,
            ulong residentBytes)
        {
            Key = key;
            Artifacts = artifacts;
            LastUse = lastUse;
            PublicationGeneration = publicationGeneration;
            ResidentBytes = residentBytes;
            ReferenceCount = 1;
        }

        public OpacityMicromapExtCacheKey Key { get; }
        public OpacityMicromapExtPublishedArtifacts Artifacts { get; }
        public ulong PublicationGeneration { get; }
        public ulong ResidentBytes { get; }
        public GpuCompletionToken LastUse { get; set; }
        public int ReferenceCount { get; set; }
    }

    private sealed class PublishedLease : IOpacityMicromapRetirementLease
    {
        private readonly VulkanExtOpacityMicromapBackend _owner;
        private readonly SharedEntry _entry;
        private int _disposed;

        public PublishedLease(
            VulkanExtOpacityMicromapBackend owner,
            SharedEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public OpacityMicromapContentKey ContentKey => _entry.Key.ContentKey;
        public ulong PublicationGeneration => _entry.PublicationGeneration;
        public bool IsReadyForTlasPublication => Volatile.Read(ref _disposed) == 0;
        public ulong ResidentBytes => _entry.ResidentBytes;

        public void RecordLastUse(GpuCompletionToken completion)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PublishedLease));
            _owner.RecordLastUse(_entry, completion);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.Release(_entry);
        }
    }
}
