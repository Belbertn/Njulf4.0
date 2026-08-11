using Njulf.Assets.Cooked;
using System.Collections.ObjectModel;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Qualification identity for the complete C1 runtime contract (cooked four-state payload,
/// EXT micromap build/compaction, matching static-BLAS attachment, publication, and retirement).
/// This is deliberately distinct from the optional cooked-chunk schema: changing any runtime
/// ownership or traversal semantic invalidates previously captured promotion evidence even when
/// the asset bytes remain readable.
/// </summary>
public static class OpacityMicromapRuntimeAbi
{
    public const uint Version = 0xC101_0001u;
}

/// <summary>
/// Runtime backend identifiers.  <see cref="KhrReserved"/> is persisted intent
/// only; there is intentionally no KHR backend implementation in this code.
/// </summary>
public enum OpacityMicromapBackendKind : byte
{
    Null = 0,
    VulkanExtFourState = 1,
    KhrReserved = 2
}

public enum OpacityMicromapBackendFallbackReason : byte
{
    None = 0,
    Disabled,
    NullBackendSelected,
    ExtBackendNotRegistered,
    ExtCapabilityUnavailable,
    ExtPayloadUnsupported,
    ContentKeyMismatch,
    PayloadRejected,
    BuildUnavailable,
    BuildFailed,
    KhrBackendNotImplemented,
    Cancelled
}

public readonly record struct OpacityMicromapRuntimeCapabilities(
    bool ExtensionAvailable,
    bool FeatureEnabled,
    bool AccelerationStructureDependencyAvailable,
    bool CommandBufferBuildAvailable,
    bool FourStateFormatAvailable,
    uint MaximumFourStateSubdivisionLevel,
    bool CompactionAvailable)
{
    public bool SupportsExtFourState =>
        ExtensionAvailable &&
        FeatureEnabled &&
        AccelerationStructureDependencyAvailable &&
        CommandBufferBuildAvailable &&
        FourStateFormatAvailable &&
        MaximumFourStateSubdivisionLevel > 0;
}

/// <summary>
/// Generic build request deliberately free of Vulkan structs.  The EXT backend
/// owns the extension-specific build chains, barriers, query pools, leases, and
/// fence retirement; this neutral contract prevents an accidental KHR/EXT ABI
/// reinterpretation.
/// </summary>
public readonly record struct OpacityMicromapBackendBuildRequest(
    OpacityMicromapContentKey ContentKey,
    OpacityMicromapCookedPayload Payload,
    uint AccelerationStructureBuildAbi,
    ulong PublicationGeneration)
{
    public bool IsWellFormed =>
        !ContentKey.IsZero &&
        Payload is not null &&
        Payload.PayloadKind == OpacityMicromapPayloadKind.VulkanExtFourState &&
        Payload.Format == OpacityMicromapFormat.FourState &&
        AccelerationStructureBuildAbi != 0;
}

/// <summary>
/// An atomically publishable OMM/BLAS ownership lease.  A ready lease is never
/// exposed until its micromap and matching BLAS generation are both complete.
/// </summary>
public interface IOpacityMicromapLease : IDisposable
{
    OpacityMicromapContentKey ContentKey { get; }
    ulong PublicationGeneration { get; }
    bool IsReadyForTlasPublication { get; }
    ulong ResidentBytes { get; }
}

public readonly record struct OpacityMicromapBackendBuildResult(
    bool Succeeded,
    IOpacityMicromapLease? Lease,
    OpacityMicromapBackendFallbackReason FallbackReason,
    string Detail)
{
    public bool UsesOrdinaryCandidatePath => !Succeeded || Lease is null;

    public static OpacityMicromapBackendBuildResult Fallback(
        OpacityMicromapBackendFallbackReason reason,
        string detail) => new(false, null, reason, detail);
}

/// <summary>
/// Common runtime boundary.  Implementations must return a ready lease only
/// after all backend work (build, optional compaction, matching BLAS build, and
/// publication synchronization) has completed.  Until then callers retain the
/// ordinary candidate-tested BLAS.
/// </summary>
public interface IOpacityMicromapBackend
{
    OpacityMicromapBackendKind Kind { get; }
    OpacityMicromapRuntimeCapabilities Capabilities { get; }

    ValueTask<OpacityMicromapBackendBuildResult> BuildAsync(
        OpacityMicromapBackendBuildRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Marker for the only production implementation that may be plugged into this
/// slice.  It is an interface, not an emulated implementation: the concrete
/// backend must bind Silk.NET's EXT types and issue real EXT commands.
/// </summary>
public interface IExtOpacityMicromapBackend : IOpacityMicromapBackend
{
}

/// <summary>
/// Canonical fallback backend.  It owns no device resources, descriptors, or
/// passes and always preserves ordinary DDGI candidate confirmation.
/// </summary>
public sealed class NullOpacityMicromapBackend : IOpacityMicromapBackend
{
    public static NullOpacityMicromapBackend Instance { get; } = new();

    private NullOpacityMicromapBackend()
    {
    }

    public OpacityMicromapBackendKind Kind => OpacityMicromapBackendKind.Null;

    public OpacityMicromapRuntimeCapabilities Capabilities => default;

    public ValueTask<OpacityMicromapBackendBuildResult> BuildAsync(
        OpacityMicromapBackendBuildRequest request,
        CancellationToken cancellationToken)
    {
        OpacityMicromapBackendBuildResult result = cancellationToken.IsCancellationRequested
            ? OpacityMicromapBackendBuildResult.Fallback(
                OpacityMicromapBackendFallbackReason.Cancelled,
                "opacity-micromap-build-cancelled-before-null-fallback")
            : OpacityMicromapBackendBuildResult.Fallback(
                OpacityMicromapBackendFallbackReason.NullBackendSelected,
                "ordinary-alpha-candidate-path; null-opacity-micromap-backend");
        return ValueTask.FromResult(result);
    }
}

public readonly record struct OpacityMicromapBackendResolution(
    IOpacityMicromapBackend Backend,
    OpacityMicromapBackendFallbackReason FallbackReason,
    string Detail)
{
    public bool UsesExtBackend => Backend.Kind == OpacityMicromapBackendKind.VulkanExtFourState;
}

/// <summary>
/// Resolves a requested backend without creating an object or allocating a
/// resource.  The null path is a normal successful outcome, including for a
/// missing extension, invalid payload, or reserved KHR request.
/// </summary>
public sealed class OpacityMicromapBackendSelector
{
    private readonly IExtOpacityMicromapBackend? _extBackend;

    public OpacityMicromapBackendSelector(IExtOpacityMicromapBackend? extBackend = null)
    {
        if (extBackend is not null && extBackend.Kind != OpacityMicromapBackendKind.VulkanExtFourState)
        {
            throw new ArgumentException(
                "An EXT opacity-micromap backend must identify itself as VulkanExtFourState.",
                nameof(extBackend));
        }
        _extBackend = extBackend;
    }

    public OpacityMicromapBackendResolution Resolve(
        OpacityMicromapBackendKind requested,
        OpacityMicromapCookedPayload? payload,
        OpacityMicromapContentKey expectedContentKey)
    {
        if (requested == OpacityMicromapBackendKind.Null)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.Disabled,
                "opacity-micromap-mode-disabled");
        }
        if (requested == OpacityMicromapBackendKind.KhrReserved)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.KhrBackendNotImplemented,
                "khr-opacity-micromap-is-reserved-and-has-no-runtime-backend");
        }
        if (requested != OpacityMicromapBackendKind.VulkanExtFourState)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.PayloadRejected,
                "opacity-micromap-backend-kind-unknown");
        }
        if (payload is null || payload.PayloadKind != OpacityMicromapPayloadKind.VulkanExtFourState ||
            payload.Format != OpacityMicromapFormat.FourState)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ExtPayloadUnsupported,
                "vulkan-ext-four-state-payload-required");
        }
        if (expectedContentKey.IsZero || payload.SourceContentHash != expectedContentKey)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ContentKeyMismatch,
                "cooked-opacity-micromap-content-key-mismatch");
        }
        if (_extBackend is null)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ExtBackendNotRegistered,
                "vulkan-ext-opacity-micromap-backend-not-registered");
        }
        if (!_extBackend.Capabilities.SupportsExtFourState ||
            payload.MaximumSubdivisionLevel >
                _extBackend.Capabilities.MaximumFourStateSubdivisionLevel)
        {
            return Fallback(
                OpacityMicromapBackendFallbackReason.ExtCapabilityUnavailable,
                "vulkan-ext-four-state-capability-or-subdivision-unavailable");
        }

        return new OpacityMicromapBackendResolution(
            _extBackend,
            OpacityMicromapBackendFallbackReason.None,
            "vulkan-ext-four-state-backend-selected");
    }

    private static OpacityMicromapBackendResolution Fallback(
        OpacityMicromapBackendFallbackReason reason,
        string detail) => new(NullOpacityMicromapBackend.Instance, reason, detail);
}

public enum StaticBlasRayGeometryPolicy : byte
{
    CandidateConfirmationRequired = 0,
    OpaqueOnly = 1,
    TwoSidedCandidateConfirmationRequired = 2
}

/// <summary>
/// Cache identity for a static BLAS variant.  The all-zero OMM key represents
/// the plain candidate-tested fallback variant; it is not a valid cooked key.
/// </summary>
public readonly record struct StaticBlasVariantKey(
    OpacityMicromapContentKey MeshGeometryKey,
    StaticBlasRayGeometryPolicy RayGeometryPolicy,
    OpacityMicromapContentKey OpacityMicromapContentKeyOrNull,
    uint AccelerationStructureBuildAbi)
{
    public bool HasOpacityMicromap => !OpacityMicromapContentKeyOrNull.IsZero;
    public bool IsPlainFallback => !HasOpacityMicromap;

    public static StaticBlasVariantKey Plain(
        OpacityMicromapContentKey meshGeometryKey,
        StaticBlasRayGeometryPolicy rayGeometryPolicy,
        uint accelerationStructureBuildAbi)
    {
        if (meshGeometryKey.IsZero || accelerationStructureBuildAbi == 0)
            throw new ArgumentOutOfRangeException(nameof(meshGeometryKey));
        return new StaticBlasVariantKey(
            meshGeometryKey,
            rayGeometryPolicy,
            OpacityMicromapContentKey.Zero,
            accelerationStructureBuildAbi);
    }

    public bool IsValid => !MeshGeometryKey.IsZero && AccelerationStructureBuildAbi != 0;
}

/// <summary>
/// Runtime retention facts for one published opacity-micromap BLAS variant.
/// Keeping this as a value-only contract makes pressure decisions deterministic
/// and independently testable without exposing native Vulkan resources.
/// </summary>
internal readonly record struct OpacityMicromapVariantRetentionCandidate(
    StaticBlasVariantKey Key,
    ulong ReuseCount,
    ulong LastUsedFrameSerial,
    bool Active,
    bool Published,
    bool HasCandidateBlas)
{
    public bool IsEvictable =>
        Key.IsValid &&
        Key.HasOpacityMicromap &&
        Published &&
        HasCandidateBlas &&
        !Active;
}

/// <summary>
/// Stable ordering and LRU/reuse retention policy shared by the cap planner
/// and the live cache.  Lower reuse is evicted first, then older use, then a
/// complete immutable key tie-breaker.  Active, incomplete, plain-fallback,
/// or otherwise invalid entries are never selected.
/// </summary>
internal static class OpacityMicromapVariantRetentionPolicy
{
    public static bool TrySelectEvictionCandidate(
        IReadOnlyList<OpacityMicromapVariantRetentionCandidate> candidates,
        in OpacityMicromapContentKey geometryKey,
        bool restrictToGeometry,
        out StaticBlasVariantKey selectedKey)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        selectedKey = default;
        if (restrictToGeometry && geometryKey.IsZero)
            return false;

        bool found = false;
        OpacityMicromapVariantRetentionCandidate selected = default;
        foreach (OpacityMicromapVariantRetentionCandidate candidate in candidates)
        {
            if (!candidate.IsEvictable ||
                (restrictToGeometry &&
                 candidate.Key.MeshGeometryKey != geometryKey))
            {
                continue;
            }

            if (!found || IsLowerRetentionPriority(candidate, selected))
            {
                found = true;
                selected = candidate;
            }
        }

        if (!found)
            return false;

        selectedKey = selected.Key;
        return true;
    }

    public static int CompareKeys(
        in StaticBlasVariantKey left,
        in StaticBlasVariantKey right)
    {
        int comparison = left.MeshGeometryKey.CompareTo(right.MeshGeometryKey);
        if (comparison != 0)
            return comparison;
        comparison = left.OpacityMicromapContentKeyOrNull.CompareTo(
            right.OpacityMicromapContentKeyOrNull);
        if (comparison != 0)
            return comparison;
        comparison = left.AccelerationStructureBuildAbi.CompareTo(
            right.AccelerationStructureBuildAbi);
        return comparison != 0
            ? comparison
            : left.RayGeometryPolicy.CompareTo(right.RayGeometryPolicy);
    }

    private static bool IsLowerRetentionPriority(
        in OpacityMicromapVariantRetentionCandidate candidate,
        in OpacityMicromapVariantRetentionCandidate selected)
    {
        if (candidate.ReuseCount != selected.ReuseCount)
            return candidate.ReuseCount < selected.ReuseCount;
        if (candidate.LastUsedFrameSerial != selected.LastUsedFrameSerial)
        {
            return candidate.LastUsedFrameSerial <
                selected.LastUsedFrameSerial;
        }
        return CompareKeys(candidate.Key, selected.Key) < 0;
    }
}

public readonly record struct OpacityMicromapBlasVariantCandidate(
    StaticBlasVariantKey Key,
    ulong BlasResidentBytes,
    ulong OpacityMicromapResidentBytes,
    uint ReuseCount,
    double QualifiedBenefitScore,
    bool Qualified);

public readonly record struct OpacityMicromapBlasVariantCapPolicy(
    bool Enabled,
    int MaximumOpacityVariantsPerMesh,
    int MaximumOpacityVariantsGlobally,
    ulong MaximumOpacityVariantResidentBytes)
{
    public static OpacityMicromapBlasVariantCapPolicy Disabled { get; } = new(
        Enabled: false,
        MaximumOpacityVariantsPerMesh: 0,
        MaximumOpacityVariantsGlobally: 0,
        MaximumOpacityVariantResidentBytes: 0);

    public bool IsValid =>
        MaximumOpacityVariantsPerMesh >= 0 &&
        MaximumOpacityVariantsGlobally >= 0;
}

public enum OpacityMicromapBlasVariantDecisionReason : byte
{
    SelectedPlainFallback = 0,
    SelectedOpacityMicromapVariant = 1,
    InvalidCandidate,
    DuplicateSuperseded,
    PlainFallbackMissing,
    PolicyDisabled,
    QualificationMissing,
    PerMeshCapReached,
    GlobalCapReached,
    ResidentByteCapReached
}

public readonly record struct OpacityMicromapBlasVariantDecision(
    StaticBlasVariantKey Key,
    bool Selected,
    OpacityMicromapBlasVariantDecisionReason Reason,
    string Detail);

public sealed class OpacityMicromapBlasVariantPlan
{
    private readonly ReadOnlyCollection<OpacityMicromapBlasVariantDecision> _decisions;

    public OpacityMicromapBlasVariantPlan(
        IReadOnlyList<OpacityMicromapBlasVariantDecision> decisions,
        int selectedOpacityVariantCount,
        ulong selectedOpacityVariantResidentBytes)
    {
        _decisions = Array.AsReadOnly(decisions.ToArray());
        SelectedOpacityVariantCount = selectedOpacityVariantCount;
        SelectedOpacityVariantResidentBytes = selectedOpacityVariantResidentBytes;
    }

    public IReadOnlyList<OpacityMicromapBlasVariantDecision> Decisions => _decisions;
    public int SelectedOpacityVariantCount { get; }
    public ulong SelectedOpacityVariantResidentBytes { get; }
}

/// <summary>
/// Deterministically retains only qualified OMM variants under per-mesh,
/// global, and byte caps.  A plain variant is selected independently of OMM
/// pressure and an OMM candidate is never selected when its mesh has no plain
/// fallback candidate.
/// </summary>
public static class OpacityMicromapBlasVariantCapPlanner
{
    public static OpacityMicromapBlasVariantPlan Plan(
        IReadOnlyList<OpacityMicromapBlasVariantCandidate> candidates,
        in OpacityMicromapBlasVariantCapPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var decisions = new List<OpacityMicromapBlasVariantDecision>(candidates.Count);
        if (!policy.IsValid)
        {
            foreach (OpacityMicromapBlasVariantCandidate candidate in candidates)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.InvalidCandidate,
                    "opacity-micromap-variant-cap-policy-invalid"));
            }
            return new OpacityMicromapBlasVariantPlan(decisions, 0, 0);
        }

        var unique = new Dictionary<StaticBlasVariantKey, OpacityMicromapBlasVariantCandidate>();
        foreach (OpacityMicromapBlasVariantCandidate candidate in candidates)
        {
            if (!IsCandidateShapeValid(candidate))
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.InvalidCandidate,
                    "opacity-micromap-variant-candidate-invalid"));
                continue;
            }
            if (!unique.TryGetValue(candidate.Key, out OpacityMicromapBlasVariantCandidate existing))
            {
                unique.Add(candidate.Key, candidate);
                continue;
            }

            if (CompareCandidatePriority(candidate, existing) < 0)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    existing.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.DuplicateSuperseded,
                    "duplicate-variant-superseded-by-higher-priority-candidate"));
                unique[candidate.Key] = candidate;
            }
            else
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.DuplicateSuperseded,
                    "duplicate-variant-superseded-by-higher-priority-candidate"));
            }
        }

        List<OpacityMicromapBlasVariantCandidate> orderedUnique = unique.Values.ToList();
        orderedUnique.Sort(static (left, right) =>
            OpacityMicromapVariantRetentionPolicy.CompareKeys(
                left.Key,
                right.Key));

        var plainFallbackDomains = new HashSet<StaticBlasFallbackDomain>();
        foreach (OpacityMicromapBlasVariantCandidate candidate in orderedUnique)
        {
            if (!candidate.Key.IsPlainFallback)
                continue;
            plainFallbackDomains.Add(StaticBlasFallbackDomain.From(candidate.Key));
            decisions.Add(new OpacityMicromapBlasVariantDecision(
                candidate.Key,
                true,
                OpacityMicromapBlasVariantDecisionReason.SelectedPlainFallback,
                "plain-candidate-tested-blas-retained"));
        }

        var perMeshEligible = new Dictionary<OpacityMicromapContentKey, List<OpacityMicromapBlasVariantCandidate>>();
        foreach (OpacityMicromapBlasVariantCandidate candidate in orderedUnique)
        {
            if (candidate.Key.IsPlainFallback)
                continue;
            if (!plainFallbackDomains.Contains(StaticBlasFallbackDomain.From(candidate.Key)))
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.PlainFallbackMissing,
                    "plain-candidate-tested-blas-required-before-omm-variant"));
                continue;
            }
            if (!policy.Enabled)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.PolicyDisabled,
                    "opacity-micromap-variant-policy-disabled"));
                continue;
            }
            if (!candidate.Qualified)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.QualificationMissing,
                    "opacity-micromap-variant-not-qualified"));
                continue;
            }
            if (!perMeshEligible.TryGetValue(candidate.Key.MeshGeometryKey, out List<OpacityMicromapBlasVariantCandidate>? entries))
            {
                entries = new List<OpacityMicromapBlasVariantCandidate>();
                perMeshEligible.Add(candidate.Key.MeshGeometryKey, entries);
            }
            entries.Add(candidate);
        }

        var globalCandidates = new List<OpacityMicromapBlasVariantCandidate>();
        List<OpacityMicromapContentKey> orderedMeshKeys = perMeshEligible.Keys.ToList();
        orderedMeshKeys.Sort();
        foreach (OpacityMicromapContentKey meshKey in orderedMeshKeys)
        {
            List<OpacityMicromapBlasVariantCandidate> entries = perMeshEligible[meshKey];
            entries.Sort(CompareCandidatePriority);
            int selectedFromMesh = Math.Min(entries.Count, policy.MaximumOpacityVariantsPerMesh);
            for (int index = 0; index < entries.Count; index++)
            {
                OpacityMicromapBlasVariantCandidate candidate = entries[index];
                if (index < selectedFromMesh)
                {
                    globalCandidates.Add(candidate);
                }
                else
                {
                    decisions.Add(new OpacityMicromapBlasVariantDecision(
                        candidate.Key,
                        false,
                        OpacityMicromapBlasVariantDecisionReason.PerMeshCapReached,
                        "per-mesh-opacity-micromap-variant-cap-reached"));
                }
            }
        }

        globalCandidates.Sort(CompareCandidatePriority);
        int selectedCount = 0;
        ulong selectedBytes = 0;
        foreach (OpacityMicromapBlasVariantCandidate candidate in globalCandidates)
        {
            if (selectedCount >= policy.MaximumOpacityVariantsGlobally)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.GlobalCapReached,
                    "global-opacity-micromap-variant-cap-reached"));
                continue;
            }
            if (!TryGetResidentBytes(candidate, out ulong residentBytes))
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.InvalidCandidate,
                    "opacity-micromap-variant-resident-byte-overflow"));
                continue;
            }
            if (residentBytes > policy.MaximumOpacityVariantResidentBytes ||
                selectedBytes > policy.MaximumOpacityVariantResidentBytes - residentBytes)
            {
                decisions.Add(new OpacityMicromapBlasVariantDecision(
                    candidate.Key,
                    false,
                    OpacityMicromapBlasVariantDecisionReason.ResidentByteCapReached,
                    "opacity-micromap-variant-resident-byte-cap-reached"));
                continue;
            }

            selectedCount++;
            selectedBytes += residentBytes;
            decisions.Add(new OpacityMicromapBlasVariantDecision(
                candidate.Key,
                true,
                OpacityMicromapBlasVariantDecisionReason.SelectedOpacityMicromapVariant,
                "qualified-opacity-micromap-variant-selected"));
        }

        return new OpacityMicromapBlasVariantPlan(decisions, selectedCount, selectedBytes);
    }

    private static bool IsCandidateShapeValid(OpacityMicromapBlasVariantCandidate candidate)
    {
        if (!candidate.Key.IsValid || !double.IsFinite(candidate.QualifiedBenefitScore) ||
            candidate.QualifiedBenefitScore < 0.0)
        {
            return false;
        }
        if (candidate.Key.IsPlainFallback)
            return candidate.OpacityMicromapResidentBytes == 0;
        return candidate.OpacityMicromapResidentBytes > 0 && candidate.ReuseCount > 0 &&
            TryGetResidentBytes(candidate, out _);
    }

    private static bool TryGetResidentBytes(
        OpacityMicromapBlasVariantCandidate candidate,
        out ulong residentBytes)
    {
        try
        {
            residentBytes = checked(candidate.BlasResidentBytes + candidate.OpacityMicromapResidentBytes);
            return true;
        }
        catch (OverflowException)
        {
            residentBytes = 0;
            return false;
        }
    }

    private readonly record struct StaticBlasFallbackDomain(
        OpacityMicromapContentKey MeshGeometryKey,
        StaticBlasRayGeometryPolicy RayGeometryPolicy,
        uint AccelerationStructureBuildAbi)
    {
        public static StaticBlasFallbackDomain From(StaticBlasVariantKey key) => new(
            key.MeshGeometryKey,
            key.RayGeometryPolicy,
            key.AccelerationStructureBuildAbi);
    }

    /// <summary>Sorts highest benefit/reuse first, then a full stable key.</summary>
    private static int CompareCandidatePriority(
        OpacityMicromapBlasVariantCandidate left,
        OpacityMicromapBlasVariantCandidate right)
    {
        int comparison = right.ReuseCount.CompareTo(left.ReuseCount);
        if (comparison != 0)
            return comparison;
        comparison = right.QualifiedBenefitScore.CompareTo(left.QualifiedBenefitScore);
        if (comparison != 0)
            return comparison;
        comparison = OpacityMicromapVariantRetentionPolicy.CompareKeys(
            left.Key,
            right.Key);
        return comparison;
    }
}
