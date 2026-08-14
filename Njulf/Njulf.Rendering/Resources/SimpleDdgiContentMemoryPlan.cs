using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Authoritative byte plan for optional content-dependent DDGI resources. Core
/// diffuse coverage is admitted separately and can never be reduced to make
/// room for one of these regions.
/// </summary>
public readonly record struct SimpleDdgiContentMemoryPlan(
    int LocalLightCount,
    int LightTreeNodeCount,
    ulong LightTreeNodeBytes,
    ulong LightTreeLeafBytes,
    ulong LightTreeBoundsBytes,
    ulong LightTreeSortScratchBytes,
    ulong LightTreeBuildScratchBytes,
    ulong LightTreeIndirectBytes,
    ulong LightTreeStateBytes,
    ulong DirectionalRadianceCanonicalBytes,
    ulong DirectionalRadianceParityBytes,
    ulong DynamicBlasLiveBytes,
    ulong DynamicBlasScratchBytes,
    ulong DynamicBlasRetiredBytes,
    int FoliageProxyTriangleCapacity,
    ulong FoliageProxyVertexBytes,
    ulong FoliageProxyIndexBytes,
    ulong FoliageProxyPatchBytes,
    DdgiFeatureFallbackReason LightTreeFallback,
    DdgiFeatureFallbackReason DirectionalRadianceFallback,
    DdgiFeatureFallbackReason DynamicGeometryFallback,
    DdgiFeatureFallbackReason FoliageFallback)
{
    /// <summary>
    /// Central sidecar for independently admitted B1/C1/C3/C4/C5 resources.
    /// The ordinary content plan remains source-compatible while resource
    /// transitions can attach this immutable extension with a <c>with</c>
    /// expression.  Default(struct) is safely all-zero.
    /// </summary>
    public SimpleDdgiAdvancedExperimentMemoryPlan AdvancedExperimentMemory { get; init; } =
        SimpleDdgiAdvancedExperimentMemoryPlan.Empty;

    public const ulong LightTreeNodeStride = 64;
    public const ulong LightTreeLeafStride = 32;
    public const ulong LightBoundsStride = 32;
    public const ulong LightSortKeyStride = 16;
    public const ulong LightTreeParentStride = 4;
    public const ulong LightTreeIndirectRegionBytes = 64;
    public const ulong LightTreeStateRegionBytes = 128; // two 64-byte states
    public const ulong RadianceShL1ReferenceStride = 32;
    public const ulong RadianceShL2Stride = 64;
    public const ulong FoliageProxyVertexStride = 80;
    public const ulong FoliageProxyIndexStride = 4;
    public const ulong FoliageProxyPatchStride = 80;
    public const int FoliageProxyTrianglesPerCard = 4;
    public const int FoliageProxyVerticesPerCard = 8;
    public const int FoliageProxyIndicesPerCard = 12;

    public ulong LightTreePersistentBytes => checked(
        LightTreeNodeBytes + LightTreeLeafBytes + LightTreeStateBytes);

    public ulong LightTreeWorkBytes => checked(
        LightTreeBoundsBytes +
        LightTreeSortScratchBytes +
        LightTreeBuildScratchBytes +
        LightTreeIndirectBytes);

    public ulong DirectionalRadianceBytes => checked(
        DirectionalRadianceCanonicalBytes + DirectionalRadianceParityBytes);

    public ulong DynamicAccelerationStructureBytes => checked(
        DynamicBlasLiveBytes + DynamicBlasScratchBytes + DynamicBlasRetiredBytes);

    public ulong FoliageProxyBytes => checked(
        FoliageProxyVertexBytes +
        FoliageProxyIndexBytes +
        FoliageProxyPatchBytes);

    public ulong PersistentBytes => checked(
        LightTreePersistentBytes +
        DirectionalRadianceBytes +
        DynamicBlasLiveBytes +
        DynamicBlasRetiredBytes +
        FoliageProxyBytes +
        AdvancedExperimentMemory.PersistentAllocatedBytes);

    public ulong WorkBytes => checked(
        LightTreeWorkBytes +
        DynamicBlasScratchBytes +
        AdvancedExperimentMemory.ConservativeTransientPeakLiveBytes);

    /// <summary>
    /// Total memory that can be live at a transactional transition.  Retired
    /// optional generations remain included until their GPU ownership has
    /// actually drained; excluding them would make a replacement allocation
    /// appear to fit headroom when it does not.
    /// </summary>
    public ulong LiveBytes => checked(
        PersistentBytes +
        WorkBytes +
        AdvancedExperimentMemory.RetiredButLiveBytes);

    public bool HasLightTree => LightTreeNodeCount > 0;
    public bool HasDirectionalRadiance => DirectionalRadianceCanonicalBytes > 0;
    public bool HasDynamicGeometry => DynamicBlasLiveBytes > 0;
    public bool HasFoliageProxies => FoliageProxyTriangleCapacity > 0;

    public bool IsValid
    {
        get
        {
            if (LocalLightCount < 0 ||
                LightTreeNodeCount < 0 ||
                FoliageProxyTriangleCapacity < 0 ||
                !Enum.IsDefined(LightTreeFallback) ||
                !Enum.IsDefined(DirectionalRadianceFallback) ||
                !Enum.IsDefined(DynamicGeometryFallback) ||
                !Enum.IsDefined(FoliageFallback) ||
                !AdvancedExperimentMemory.NormalizeForPersistence()
                    .Equals(AdvancedExperimentMemory))
            {
                return false;
            }

            try
            {
                _ = PersistentBytes;
                _ = WorkBytes;
                _ = LiveBytes;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
    }

    public static SimpleDdgiContentMemoryPlan Empty { get; } = new()
    {
        AdvancedExperimentMemory = SimpleDdgiAdvancedExperimentMemoryPlan.Empty
    };

    public SimpleDdgiContentMemoryPlan NormalizeForPersistence()
    {
        SimpleDdgiContentMemoryPlan normalized = this with
        {
            AdvancedExperimentMemory =
                AdvancedExperimentMemory.NormalizeForPersistence()
        };
        return normalized.IsValid ? normalized : Empty;
    }

    /// <summary>
    /// Attaches the immutable C5 ownership plan to this central content plan.
    /// Rejected C5 plans preserve their zero-byte fallback categories, while an
    /// active plan is rejected if it tries to overlap another feature's domain.
    /// </summary>
    public SimpleDdgiContentMemoryPlan WithNearFieldResidual(
        in SimpleDdgiNearFieldResidualPlan nearFieldResidual)
    {
        if (!nearFieldResidual.Memory.HasOnlyNearFieldResidualCategories)
        {
            throw new ArgumentException(
                "The C5 memory plan contains a non-C5 ownership category.",
                nameof(nearFieldResidual));
        }
        if (nearFieldResidual.Active !=
            (nearFieldResidual.Memory.AllocatedBytes != 0UL))
        {
            throw new ArgumentException(
                "C5 active state and central-memory allocation disagree.",
                nameof(nearFieldResidual));
        }

        return this with
        {
            AdvancedExperimentMemory =
                SimpleDdgiAdvancedExperimentMemoryPlan.CombineDisjoint(
                    AdvancedExperimentMemory,
                    nearFieldResidual.Memory)
        };
    }

    public static SimpleDdgiContentMemoryPlan Compile(
        GlobalIlluminationSettings settings,
        int localLightCount,
        int physicalProbeCapacity,
        ulong dynamicBlasRequiredBytes = 0,
        ulong dynamicBlasScratchRequiredBytes = 0,
        ulong dynamicBlasRetiredRequiredBytes = 0,
        int foliageProxyTriangleCount = 0,
        int foliageProxyPatchCount = 0,
        int frameSlotCount = RenderingConstants.FramesInFlight,
        SimpleDdgiAdvancedExperimentMemoryPlan? advancedExperimentMemory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        int locals = Math.Clamp(
            localLightCount,
            0,
            GlobalIlluminationSettings.MaxSimpleDdgiExactLocalLightThreshold);
        int probes = Math.Clamp(
            physicalProbeCapacity,
            0,
            GlobalIlluminationSettings.MaxSimpleDdgiTotalProbeCount);
        int frameSlots = Math.Clamp(frameSlotCount, 1, 8);
        DdgiContentFeature active = settings.ActiveContentDependentFeatures;

        bool treeFeatureActive =
            (active & DdgiContentFeature.ManyLightSampling) != 0;
        bool treeModeRequested = settings.SimpleDdgiLocalLightSamplingMode is
            SimpleDdgiLocalLightSamplingMode.Auto or
            SimpleDdgiLocalLightSamplingMode.LightTree;
        bool treeRequired = treeFeatureActive &&
            treeModeRequested &&
            locals > settings.SimpleDdgiExactLocalLightThreshold;
        int paddedLeafCount = treeRequired ? NextPowerOfTwo(locals) : 0;
        int nodeCount = treeRequired ? checked(paddedLeafCount * 2 - 1) : 0;
        // Nodes and leaves are double-buffered so trace only observes a complete
        // published hierarchy.
        ulong nodeBytes = checked((ulong)nodeCount * LightTreeNodeStride * 2UL);
        ulong leafBytes = checked((ulong)paddedLeafCount *
            LightTreeLeafStride * 2UL);
        // The production scratch ABI stores one 12-word bounds/Morton record
        // and one sorted-index word per padded leaf. Its 16-word header is
        // accounted as build scratch; the final 16 words are indirect data.
        ulong boundsBytes = checked((ulong)paddedLeafCount * 12UL * sizeof(uint));
        ulong sortBytes = checked((ulong)paddedLeafCount * sizeof(uint));
        ulong buildBytes = treeRequired ? 16UL * sizeof(uint) : 0UL;
        ulong indirectBytes = treeRequired ? LightTreeIndirectRegionBytes : 0UL;
        ulong stateBytes = treeRequired ? LightTreeStateRegionBytes : 0UL;
        DdgiFeatureFallbackReason treeFallback = treeRequired || locals == 0 ||
            locals <= settings.SimpleDdgiExactLocalLightThreshold
                ? DdgiFeatureFallbackReason.None
                : !treeFeatureActive
                    ? DdgiFeatureFallbackReason.DeviceProfileNotQualified
                    : DdgiFeatureFallbackReason.DisabledByPreset;

        bool directionalActive =
            (active & DdgiContentFeature.DirectionalRadiance) != 0;
        ulong directionalStride = settings.EffectiveSimpleDdgiDirectionalRadianceMode switch
        {
            SimpleDdgiDirectionalRadianceMode.L1Reference => RadianceShL1ReferenceStride,
            SimpleDdgiDirectionalRadianceMode.L2 => RadianceShL2Stride,
            _ => 0UL
        };
        ulong directionalCanonicalRequired = checked((ulong)probes * directionalStride);
        bool parityRequired = settings.EffectiveSimpleDdgiGlossyTransportMode is
            SimpleDdgiGlossyTransportMode.OneBounce or
            SimpleDdgiGlossyTransportMode.RecursiveCertified;
        ulong directionalParityRequired = parityRequired
            ? directionalCanonicalRequired
            : 0UL;
        ulong directionalTotalRequired = checked(
            directionalCanonicalRequired + directionalParityRequired);
        bool directionalAdmitted = directionalActive &&
            probes > 0 &&
            directionalTotalRequired <=
                settings.SimpleDdgiDirectionalRadianceMemoryBudgetBytes;
        ulong directionalBytes = directionalAdmitted
            ? directionalCanonicalRequired
            : 0UL;
        ulong parityBytes = directionalAdmitted
            ? directionalParityRequired
            : 0UL;
        DdgiFeatureFallbackReason directionalFallback =
            settings.SimpleDdgiDirectionalRadianceMode ==
                SimpleDdgiDirectionalRadianceMode.Off || probes == 0
                ? DdgiFeatureFallbackReason.None
                : !directionalActive
                    ? DdgiFeatureFallbackReason.DeviceProfileNotQualified
                    : !directionalAdmitted
                        ? DdgiFeatureFallbackReason.MemoryBudgetExceeded
                        : DdgiFeatureFallbackReason.None;

        bool dynamicActive =
            (active & DdgiContentFeature.CurrentPoseGeometry) != 0 &&
            settings.DdgiSkinnedGeometryMode == DdgiSkinnedGeometryMode.CurrentPose;
        ulong dynamicLive = dynamicActive
            ? Math.Min(
                dynamicBlasRequiredBytes,
                settings.DdgiDynamicBlasMemoryBudgetBytes)
            : 0UL;
        ulong dynamicRetiredCapacity = dynamicActive &&
            settings.DdgiDynamicBlasMemoryBudgetBytes > dynamicLive
                ? settings.DdgiDynamicBlasMemoryBudgetBytes - dynamicLive
                : 0UL;
        ulong dynamicRetired = Math.Min(
            dynamicBlasRetiredRequiredBytes,
            dynamicRetiredCapacity);
        ulong dynamicScratch = dynamicActive
            ? Math.Min(
                dynamicBlasScratchRequiredBytes,
                settings.DdgiDynamicBlasScratchBudgetBytes)
            : 0UL;
        bool dynamicBudgetShortfall = dynamicActive &&
            (dynamicLive < dynamicBlasRequiredBytes ||
             dynamicScratch < dynamicBlasScratchRequiredBytes ||
             dynamicRetired < dynamicBlasRetiredRequiredBytes);
        DdgiFeatureFallbackReason dynamicFallback =
            dynamicBlasRequiredBytes == 0
                ? DdgiFeatureFallbackReason.None
                : !dynamicActive
                    ? DdgiFeatureFallbackReason.DeviceProfileNotQualified
                    : dynamicBudgetShortfall
                        ? DdgiFeatureFallbackReason.ConservativeProxy
                        : DdgiFeatureFallbackReason.None;

        bool foliageActive =
            (active & DdgiContentFeature.FoliageGeometry) != 0 &&
            settings.DdgiFoliageGeometryMode != DdgiFoliageGeometryMode.Excluded;
        int requestedFoliageTriangles = Math.Max(0, foliageProxyTriangleCount);
        int admittedFoliageTriangles = foliageActive
            ? Math.Min(
                requestedFoliageTriangles,
                settings.DdgiFoliageProxyTriangleBudget)
            : 0;
        int foliageCards = admittedFoliageTriangles /
            FoliageProxyTrianglesPerCard;
        int foliageTriangles = checked(
            foliageCards * FoliageProxyTrianglesPerCard);
        ulong foliageVertexBytes = checked(
            (ulong)foliageCards * FoliageProxyVerticesPerCard *
            FoliageProxyVertexStride *
            (ulong)frameSlots);
        ulong foliageIndexBytes = checked(
            (ulong)foliageCards * FoliageProxyIndicesPerCard *
            FoliageProxyIndexStride *
            (ulong)frameSlots);
        int foliagePatches = foliageCards == 0
            ? 0
            : Math.Min(
                foliageCards,
                Math.Max(1, foliageProxyPatchCount));
        ulong foliagePatchBytes = checked(
            (ulong)foliagePatches * FoliageProxyPatchStride *
            (ulong)frameSlots);
        DdgiFeatureFallbackReason foliageFallback =
            requestedFoliageTriangles == 0
                ? DdgiFeatureFallbackReason.None
                : !foliageActive
                    ? DdgiFeatureFallbackReason.DeviceProfileNotQualified
                    : foliageTriangles < requestedFoliageTriangles
                        ? DdgiFeatureFallbackReason.MemoryBudgetExceeded
                        : DdgiFeatureFallbackReason.None;

        return new SimpleDdgiContentMemoryPlan(
            locals,
            nodeCount,
            nodeBytes,
            leafBytes,
            boundsBytes,
            sortBytes,
            buildBytes,
            indirectBytes,
            stateBytes,
            directionalBytes,
            parityBytes,
            dynamicLive,
            dynamicScratch,
            dynamicRetired,
            foliageTriangles,
            foliageVertexBytes,
            foliageIndexBytes,
            foliagePatchBytes,
            treeFallback,
            directionalFallback,
            dynamicFallback,
            foliageFallback)
        {
            AdvancedExperimentMemory = advancedExperimentMemory ??
                SimpleDdgiAdvancedExperimentMemoryPlan.Empty
        };
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return Math.Max(value, 1);
        uint result = System.Numerics.BitOperations.RoundUpToPowerOf2(
            checked((uint)value));
        return checked((int)result);
    }
}
