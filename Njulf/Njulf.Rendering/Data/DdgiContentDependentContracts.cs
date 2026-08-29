using System;
using System.Threading;

namespace Njulf.Rendering.Data;

/// <summary>
/// Selects the local-light estimator used while shading a Simple-DDGI ray hit.
/// Directional lights are evaluated exactly and never consume this budget.
/// </summary>
public enum SimpleDdgiLocalLightSamplingMode : uint
{
    Auto = 0,
    Exact = 1,
    LightTree = 2,
    LegacyTopKReference = 3
}

/// <summary>Directional incident-radiance representation stored beside a probe.</summary>
public enum SimpleDdgiDirectionalRadianceMode : uint
{
    Off = 0,
    L1Reference = 1,
    L2 = 2
}

/// <summary>Controls consumers of the directional-radiance sidecar.</summary>
public enum SimpleDdgiGlossyTransportMode : uint
{
    Off = 0,
    ReceiverOnly = 1,
    OneBounce = 2,
    /// <summary>
    /// Coupled diffuse and rough-glossy Jacobi transport. Admission requires
    /// the recursive-glossy release feature in addition to the ordinary
    /// directional-radiance and one-bounce feature gates.
    /// </summary>
    RecursiveCertified = 3,
    /// <summary>Schema compatibility name for pre-certification settings.</summary>
    [Obsolete("Use RecursiveCertified. Legacy requests are migrated on load.")]
    RecursiveExperimental = RecursiveCertified
}

public enum DdgiSkinnedGeometryMode : uint
{
    Excluded = 0,
    ConservativeProxy = 1,
    CurrentPose = 2
}

public enum DdgiTransparentGeometryMode : uint
{
    MaskOnly = 0,
    MaskAndThin = 1,
    StochasticBlend = 2
}

public enum DdgiFoliageGeometryMode : uint
{
    Excluded = 0,
    AuthoredMeshOnly = 1,
    AuthoredAndProceduralProxy = 2
}

/// <summary>
/// Independently qualified content-dependent DDGI capabilities. The mask is
/// deliberately runtime-only: copying a settings file must not promote an
/// unqualified feature on a different device.
/// </summary>
[Flags]
public enum DdgiContentFeature : uint
{
    None = 0,
    ManyLightSampling = 1u << 0,
    CurrentPoseGeometry = 1u << 1,
    TransparentGeometry = 1u << 2,
    FoliageGeometry = 1u << 3,
    DirectionalRadiance = 1u << 4,
    OneBounceGlossyTransport = 1u << 5,
    RecursiveGlossyTransport = 1u << 6,
    All = ManyLightSampling |
        CurrentPoseGeometry |
        TransparentGeometry |
        FoliageGeometry |
        DirectionalRadiance |
        OneBounceGlossyTransport |
        RecursiveGlossyTransport
}

public enum DdgiFeatureFallbackReason : uint
{
    None = 0,
    DisabledByPreset = 1,
    DeviceProfileNotQualified = 2,
    ReferenceModeRequiresValidationAuthorization = 3,
    MemoryBudgetExceeded = 4,
    BuildBudgetExceeded = 5,
    ResourceAllocationFailed = 6,
    InvalidPublishedData = 7,
    UnsupportedMaterial = 8,
    PreviousCompleteGeneration = 9,
    ConservativeProxy = 10
}

/// <summary>
/// Non-persisted authorization for content-dependent DDGI additions. A release
/// profile can approve each feature independently. Validation authorization is
/// separate because reference modes are intentionally too expensive to ship.
/// </summary>
public sealed class DdgiContentRolloutPolicy
{
    /// <summary>
    /// Features requested by the ordinary production profile. Device support,
    /// memory admission, source ABI, resource completeness, and transport
    /// convergence remain runtime facts; external qualification artifacts are
    /// optional evidence and are not activation authority.
    /// </summary>
    public const DdgiContentFeature ProductionBaseline =
        DdgiContentFeature.ManyLightSampling |
        DdgiContentFeature.CurrentPoseGeometry |
        DdgiContentFeature.TransparentGeometry |
        DdgiContentFeature.FoliageGeometry |
        DdgiContentFeature.DirectionalRadiance |
        DdgiContentFeature.OneBounceGlossyTransport |
        DdgiContentFeature.RecursiveGlossyTransport;

    private int _approvedFeatures = unchecked((int)(uint)ProductionBaseline);
    private int _validationReferenceModesAuthorized;

    public DdgiContentFeature ApprovedFeatures =>
        (DdgiContentFeature)(uint)Volatile.Read(ref _approvedFeatures);

    public bool ValidationReferenceModesAuthorized =>
        Volatile.Read(ref _validationReferenceModesAuthorized) != 0;

    public DdgiContentFeature Resolve(DdgiContentFeature requested) =>
        requested & ApprovedFeatures;

    public void UseQualifiedLegacyBaseline()
    {
        Volatile.Write(
            ref _approvedFeatures,
            unchecked((int)(uint)ProductionBaseline));
        Volatile.Write(ref _validationReferenceModesAuthorized, 0);
    }

    /// <summary>
    /// Enables selected features for a conformance process. This is not release
    /// evidence and reference modes remain opt-in through the second argument.
    /// </summary>
    public void EnableForConformance(
        DdgiContentFeature features,
        bool authorizeReferenceModes = false)
    {
        ValidateFeatureMask(features);
        Volatile.Write(ref _approvedFeatures, unchecked((int)(uint)features));
        Volatile.Write(ref _validationReferenceModesAuthorized, authorizeReferenceModes ? 1 : 0);
    }

    /// <summary>Applies an externally reviewed per-device release qualification.</summary>
    public void ApplyReleaseQualification(DdgiContentFeature features)
    {
        ValidateFeatureMask(features);
        Volatile.Write(ref _approvedFeatures, unchecked((int)(uint)features));
        Volatile.Write(ref _validationReferenceModesAuthorized, 0);
    }

    private static void ValidateFeatureMask(DdgiContentFeature features)
    {
        if ((features & ~DdgiContentFeature.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(features));
    }
}

/// <summary>Stable domains prevent unrelated stochastic decisions from correlating.</summary>
public enum DdgiStochasticDecisionDomain : uint
{
    LocalLightTreeTraversal = 0x11u,
    AlphaCoverage = 0x23u,
    FoliageProxyGeneration = 0x37u,
    TransparentLayerSelection = 0x41u,
    DecalCandidateOrdering = 0x53u,
    ReceiverContributionFeedback = 0x67u,
    DirectionalGuiding = 0x71u,
    TaggedCaustic = 0x83u,
    NearFieldResidual = 0x97u,
    AreaLightSurface = 0xA7u
}

/// <summary>
/// Frozen stochastic identity used for persistent DDGI source samples. Frame
/// number is intentionally absent. The matching GLSL implementation lives in
/// <c>ddgi_content_stochastic.glsl</c>.
/// </summary>
public readonly record struct DdgiStochasticIdentity(
    ulong WorldProbeStableKey,
    uint DirectionRayOrdinal,
    uint SourceLightingEpoch,
    uint SamplingSequenceEpoch,
    DdgiStochasticDecisionDomain DecisionDomain,
    uint InstanceIdentity = 0,
    uint PrimitiveIdentity = 0)
{
    public const uint HashAbiVersion = 1;
    public const uint DefaultSamplingSequenceEpoch = 1;

    public uint Hash32()
    {
        uint state = 0xD1B54A35u ^ HashAbiVersion;
        state = Mix(state, (uint)WorldProbeStableKey);
        state = Mix(state, (uint)(WorldProbeStableKey >> 32));
        state = Mix(state, DirectionRayOrdinal);
        state = Mix(state, SourceLightingEpoch);
        state = Mix(state, SamplingSequenceEpoch);
        state = Mix(state, (uint)DecisionDomain);
        state = Mix(state, InstanceIdentity);
        state = Mix(state, PrimitiveIdentity);
        return Avalanche(state);
    }

    public ulong Hash64()
    {
        uint low = Hash32();
        uint high = Mix(low ^ 0xA511E9B3u, 0x63D83595u);
        return ((ulong)Avalanche(high) << 32) | low;
    }

    /// <summary>Returns a deterministic value strictly inside [0,1).</summary>
    public float UnitFloat()
    {
        // The high 24 bits are exactly representable by binary32. Half a ULP
        // keeps zero out of reciprocal-PDF and logarithm calculations.
        return ((Hash32() >> 8) + 0.5f) * (1.0f / 16_777_216.0f);
    }

    public DdgiStochasticIdentity WithDomain(DdgiStochasticDecisionDomain domain) =>
        this with { DecisionDomain = domain };

    private static uint Mix(uint state, uint value)
    {
        uint x = unchecked(state ^ (value + 0x9E3779B9u + (state << 6) + (state >> 2)));
        return Avalanche(x);
    }

    private static uint Avalanche(uint value)
    {
        value ^= value >> 16;
        value = unchecked(value * 0x7FEB352Du);
        value ^= value >> 15;
        value = unchecked(value * 0x846CA68Bu);
        value ^= value >> 16;
        return value;
    }
}

/// <summary>
/// Independent resource generations and content epochs used by DDGI additions.
/// A content edit must not masquerade as resource recreation or force a global
/// probe-atlas reset.
/// </summary>
public readonly record struct DdgiContentRevisions(
    ulong LightBufferRevision,
    ulong LightTreeTopologyRevision,
    ulong LightTreeContentRevision,
    ulong RaySceneResourceGeneration,
    ulong RaySceneContentEpoch,
    uint DirectionalRadianceAbiVersion,
    uint SourceLightingEpoch,
    uint DdgiSamplingSequenceEpoch)
{
    public static DdgiContentRevisions Initial { get; } = new(
        LightBufferRevision: 0,
        LightTreeTopologyRevision: 0,
        LightTreeContentRevision: 0,
        RaySceneResourceGeneration: 1,
        RaySceneContentEpoch: 1,
        DirectionalRadianceAbiVersion: DdgiDirectionalRadianceAbi.Off,
        SourceLightingEpoch: 1,
        DdgiSamplingSequenceEpoch: DdgiStochasticIdentity.DefaultSamplingSequenceEpoch);
}

public static class DdgiDirectionalRadianceAbi
{
    public const uint Off = 0;
    public const uint L1Reference = 0x4C31_0001u;
    public const uint L2 = 0x4C32_0001u;

    public static uint ForMode(SimpleDdgiDirectionalRadianceMode mode) => mode switch
    {
        SimpleDdgiDirectionalRadianceMode.Off => Off,
        SimpleDdgiDirectionalRadianceMode.L1Reference => L1Reference,
        SimpleDdgiDirectionalRadianceMode.L2 => L2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

/// <summary>Thread-safe owner for the revision taxonomy.</summary>
public sealed class DdgiContentRevisionTracker
{
    private readonly object _sync = new();
    private DdgiContentRevisions _current = DdgiContentRevisions.Initial;

    public DdgiContentRevisions Snapshot
    {
        get
        {
            lock (_sync)
                return _current;
        }
    }

    public DdgiContentRevisions RecordLightChange(bool topologyChanged)
    {
        lock (_sync)
        {
            _current = _current with
            {
                LightBufferRevision = Next(_current.LightBufferRevision),
                LightTreeContentRevision = Next(_current.LightTreeContentRevision),
                LightTreeTopologyRevision = topologyChanged
                    ? Next(_current.LightTreeTopologyRevision)
                    : _current.LightTreeTopologyRevision,
                SourceLightingEpoch = Next(_current.SourceLightingEpoch)
            };
            return _current;
        }
    }

    public DdgiContentRevisions RecordSourceLightingChange()
    {
        lock (_sync)
        {
            _current = _current with
            {
                SourceLightingEpoch = Next(_current.SourceLightingEpoch)
            };
            return _current;
        }
    }

    public DdgiContentRevisions RecordRaySceneContentChange()
    {
        lock (_sync)
        {
            _current = _current with
            {
                RaySceneContentEpoch = Next(_current.RaySceneContentEpoch)
            };
            return _current;
        }
    }

    public DdgiContentRevisions RecordRaySceneResourceChange()
    {
        lock (_sync)
        {
            _current = _current with
            {
                RaySceneResourceGeneration = Next(_current.RaySceneResourceGeneration),
                RaySceneContentEpoch = Next(_current.RaySceneContentEpoch)
            };
            return _current;
        }
    }

    public DdgiContentRevisions SetDirectionalRadianceMode(
        SimpleDdgiDirectionalRadianceMode mode)
    {
        uint abi = DdgiDirectionalRadianceAbi.ForMode(mode);
        lock (_sync)
        {
            if (_current.DirectionalRadianceAbiVersion == abi)
                return _current;

            _current = _current with { DirectionalRadianceAbiVersion = abi };
            return _current;
        }
    }

    public DdgiContentRevisions AdvanceSamplingSequenceEpoch()
    {
        lock (_sync)
        {
            _current = _current with
            {
                DdgiSamplingSequenceEpoch = Next(_current.DdgiSamplingSequenceEpoch)
            };
            return _current;
        }
    }

    private static ulong Next(ulong value) => value == ulong.MaxValue ? 1 : value + 1;
    private static uint Next(uint value) => value == uint.MaxValue ? 1 : value + 1;
}
