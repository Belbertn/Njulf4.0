using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources;

public readonly record struct GiCausticHeroSourceRejection(
    uint StableHeroId,
    uint MaterialIndex,
    GiCausticHeroRejectionReason Reason,
    string Detail);

public readonly record struct GiCausticHeroExtractionProfile(
    float InitialConeRadius,
    float ConeSpread,
    float MaximumPathDistance,
    float ProposalWeight,
    int MaximumHeroCount)
{
    public static GiCausticHeroExtractionProfile Reference { get; } = new(
        InitialConeRadius: 0.01f,
        ConeSpread: 0.001f,
        MaximumPathDistance: 1_000.0f,
        ProposalWeight: 1.0f,
        MaximumHeroCount: 64);

    public bool IsValid =>
        float.IsFinite(InitialConeRadius) && InitialConeRadius > 0.0f &&
        float.IsFinite(ConeSpread) && ConeSpread >= 0.0f &&
        float.IsFinite(MaximumPathDistance) && MaximumPathDistance > 0.0f &&
        float.IsFinite(ProposalWeight) && ProposalWeight > 0.0f &&
        MaximumHeroCount is > 0 and <=
            GiCausticGpuTaskGenerationFlags.MaximumHeroCount;
}

/// <summary>
/// Immutable C4 view of the exact scene instances published into the current
/// TLAS. It is rebuilt only with a coherent TLAS publication and therefore
/// cannot mix current material/topology facts with stale acceleration data.
/// </summary>
public sealed class GiCausticHeroSourceSnapshot
{
    private readonly GiCausticHeroSource[] _heroes;
    private readonly GiCausticHeroSourceRejection[] _rejections;

    public GiCausticHeroSourceSnapshot(
        GiCausticHeroSource[] heroes,
        GiCausticHeroSourceRejection[] rejections,
        ulong sceneContentRevision,
        ulong raySceneContentEpoch,
        ulong topLevelInstanceSignature)
    {
        _heroes = heroes ?? throw new ArgumentNullException(nameof(heroes));
        _rejections = rejections ??
            throw new ArgumentNullException(nameof(rejections));
        if (sceneContentRevision == 0UL || raySceneContentEpoch == 0UL ||
            topLevelInstanceSignature == 0UL)
        {
            throw new ArgumentException(
                "A C4 hero snapshot requires non-zero scene/TLAS revision identity.");
        }
        SceneContentRevision = sceneContentRevision;
        RaySceneContentEpoch = raySceneContentEpoch;
        TopLevelInstanceSignature = topLevelInstanceSignature;
    }

    public ReadOnlyMemory<GiCausticHeroSource> Heroes => _heroes;
    public ReadOnlyMemory<GiCausticHeroSourceRejection> Rejections => _rejections;
    public ulong SceneContentRevision { get; }
    public ulong RaySceneContentEpoch { get; }
    /// <summary>
    /// Stable semantic identity for the instance set represented by the TLAS.
    /// Physical frame-slot and backing-buffer rotation is intentionally absent.
    /// </summary>
    public ulong TopLevelInstanceSignature { get; }
    public bool HasEligibleHeroes => _heroes.Length > 0;
}

/// <summary>
/// Deterministic semantic identity for the exact authored heroes represented
/// by a current-pose TLAS snapshot. Qualification tools and the live renderer
/// use this same contract, avoiding ad-hoc revision substitutions.
/// </summary>
public readonly record struct GiCausticHeroRevisionIdentity(
    ulong AggregateSourceRevision,
    ulong MaterialRevision,
    ulong GeometryRevision,
    ulong TransformRevision,
    ulong StableIdentityRevision,
    int HeroCount)
{
    public bool IsValid => AggregateSourceRevision != 0UL &&
        MaterialRevision != 0UL && GeometryRevision != 0UL &&
        TransformRevision != 0UL && StableIdentityRevision != 0UL &&
        HeroCount > 0;
}

/// <summary>
/// Typed CPU source for a C4 emitter. <see cref="RadiometricValue"/> is radiant
/// intensity for point/spot sources, irradiance for a directional disk, and
/// scene-linear radiance for triangle sources. This distinction is frozen by
/// <see cref="Type"/> and mirrored by the task shader.
/// </summary>
public readonly record struct GiCausticEmitterSource(
    GiCausticGpuEmitterType Type,
    uint StableSourceId,
    Vector3 Position,
    Vector3 Direction,
    Vector3 RadiometricValue,
    float RangeOrDirectionalDiskRadius,
    Vector3 TriangleEdge1,
    Vector3 TriangleEdge2,
    float OuterConeCosine,
    float InnerConeCosine,
    bool TwoSided,
    float SelectionWeight,
    float TargetingMixtureProbability,
    ulong ContentRevision)
{
    public bool TryCompile(out GPUCausticEmitterV1 emitter, out string reason)
    {
        emitter = default;
        if (StableSourceId == 0u || ContentRevision == 0UL ||
            !Enum.IsDefined(Type) || !Finite(Position) || !Finite(Direction) ||
            !Finite(RadiometricValue) || RadiometricValue.X < 0.0f ||
            RadiometricValue.Y < 0.0f || RadiometricValue.Z < 0.0f ||
            RadiometricValue == Vector3.Zero ||
            !float.IsFinite(RangeOrDirectionalDiskRadius) ||
            RangeOrDirectionalDiskRadius <= 0.0f ||
            !float.IsFinite(SelectionWeight) || SelectionWeight <= 0.0f ||
            !float.IsFinite(TargetingMixtureProbability) ||
            TargetingMixtureProbability is < 0.0f or > 0.95f)
        {
            reason = "caustic-emitter-source-common-contract-invalid";
            return false;
        }

        GiCausticGpuEmitterFlags flags = GiCausticGpuEmitterFlags.Valid |
            GiCausticGpuEmitterFlags.SceneLinearRadiometry;
        Vector3 normal = Vector3.Zero;
        float area = 0.0f;
        switch (Type)
        {
            case GiCausticGpuEmitterType.Point:
                flags |= GiCausticGpuEmitterFlags.DeltaPosition;
                break;

            case GiCausticGpuEmitterType.Spot:
                if (!Unit(Direction) || !float.IsFinite(OuterConeCosine) ||
                    !float.IsFinite(InnerConeCosine) ||
                    OuterConeCosine is < -1.0f or >= 1.0f ||
                    InnerConeCosine < OuterConeCosine || InnerConeCosine > 1.0f)
                {
                    reason = "caustic-spot-emitter-cone-invalid";
                    return false;
                }
                flags |= GiCausticGpuEmitterFlags.DeltaPosition;
                break;

            case GiCausticGpuEmitterType.DirectionalDisk:
                if (!Unit(Direction))
                {
                    reason = "caustic-directional-emitter-direction-invalid";
                    return false;
                }
                flags |= GiCausticGpuEmitterFlags.DeltaDirection;
                break;

            case GiCausticGpuEmitterType.AreaTriangle:
            case GiCausticGpuEmitterType.EmissiveTriangle:
                if (!Finite(TriangleEdge1) || !Finite(TriangleEdge2))
                {
                    reason = "caustic-triangle-emitter-edges-invalid";
                    return false;
                }
                Vector3 cross = Vector3.Cross(TriangleEdge1, TriangleEdge2);
                float twiceArea = cross.Length();
                if (!float.IsFinite(twiceArea) || twiceArea <= 1.0e-10f)
                {
                    reason = "caustic-triangle-emitter-area-invalid";
                    return false;
                }
                normal = cross / twiceArea;
                area = 0.5f * twiceArea;
                if (TwoSided)
                    flags |= GiCausticGpuEmitterFlags.TwoSided;
                break;

            default:
                reason = "caustic-emitter-type-unsupported";
                return false;
        }

        emitter = new GPUCausticEmitterV1
        {
            AbiVersion = GiCausticGpuAbi.Version,
            StableSourceId = StableSourceId,
            Type = Type,
            Flags = flags,
            PositionAndRange = new Vector4(Position, RangeOrDirectionalDiskRadius),
            DirectionAndCosOuter = new Vector4(Direction, OuterConeCosine),
            RadiometricValueAndSelectionWeight = new Vector4(
                RadiometricValue, SelectionWeight),
            Edge1AndArea = new Vector4(TriangleEdge1, area),
            Edge2AndCosInner = new Vector4(TriangleEdge2, InnerConeCosine),
            NormalAndTargetingMix = new Vector4(normal,
                Type == GiCausticGpuEmitterType.DirectionalDisk
                    ? 0.0f
                    : TargetingMixtureProbability),
            ContentRevisionLow = GiCausticGpuAbi.Low32(ContentRevision),
            ContentRevisionHigh = GiCausticGpuAbi.High32(ContentRevision)
        };
        if (!emitter.IsValid)
        {
            emitter = default;
            reason = "caustic-emitter-gpu-contract-invalid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool Unit(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared is > 0.999f and < 1.001f;
    }
}

/// <summary>Authoring, topology, current-pose, bounds, and revision proof for one hero.</summary>
public readonly record struct GiCausticHeroSource(
    uint StableHeroId,
    uint MaterialRevision,
    Vector3 BoundsMinimum,
    Vector3 BoundsMaximum,
    GiCausticMaterialContract Material,
    GiCausticHeroGeometryFacts Geometry,
    float InitialConeRadius,
    float ConeSpread,
    float MaximumPathDistance,
    float ProposalWeight,
    ulong GeometryRevision,
    ulong TransformRevision)
{
    public bool TryCompile(out GPUCausticHeroV1 hero, out string reason)
    {
        hero = default;
        GiCausticHeroValidation validation =
            GiCausticHeroContractValidator.Validate(Material, Geometry);
        if (!validation.IsEligible)
        {
            reason = validation.Detail;
            return false;
        }
        if (StableHeroId == 0u || MaterialRevision == 0u ||
            GeometryRevision == 0UL || TransformRevision == 0UL ||
            !Finite(BoundsMinimum) || !Finite(BoundsMaximum) ||
            BoundsMinimum.X > BoundsMaximum.X ||
            BoundsMinimum.Y > BoundsMaximum.Y ||
            BoundsMinimum.Z > BoundsMaximum.Z ||
            !float.IsFinite(InitialConeRadius) || InitialConeRadius <= 0.0f ||
            !float.IsFinite(ConeSpread) || ConeSpread < 0.0f ||
            !float.IsFinite(MaximumPathDistance) || MaximumPathDistance <= 0.0f ||
            !float.IsFinite(ProposalWeight) || ProposalWeight <= 0.0f)
        {
            reason = "caustic-hero-source-bounds-or-revision-invalid";
            return false;
        }

        Vector3 center = 0.5f * (BoundsMinimum + BoundsMaximum);
        float radius = 0.5f * (BoundsMaximum - BoundsMinimum).Length();
        if (!float.IsFinite(radius) || radius <= 0.0f)
        {
            reason = "caustic-hero-source-bound-radius-invalid";
            return false;
        }
        GiCausticGpuTaskFlags mode = Material.EffectiveCasterPolicy switch
        {
            GiCausticCasterPolicy.Mirror =>
                GiCausticGpuTaskFlags.MirrorHero,
            GiCausticCasterPolicy.DielectricPriority =>
                GiCausticGpuTaskFlags.ClosedDielectricHero,
            GiCausticCasterPolicy.RoughSpecular =>
                GiCausticGpuTaskFlags.RoughSpecularReference,
            _ => GiCausticGpuTaskFlags.None
        };
        hero = new GPUCausticHeroV1
        {
            AbiVersion = GiCausticGpuAbi.Version,
            StableHeroId = StableHeroId,
            MaterialRevision = MaterialRevision,
            Flags = GiCausticGpuTaskFlags.AuthoredHero | mode,
            BoundsCenterAndRadius = new Vector4(center, radius),
            BoundsMinimumAndConeRadius = new Vector4(BoundsMinimum,
                InitialConeRadius),
            BoundsMaximumAndConeSpread = new Vector4(BoundsMaximum, ConeSpread),
            HeroOptics = new Vector4(Material.Ior, Material.Roughness, 0.0f, 0.0f),
            AbsorptionAndMaximumDistance = new Vector4(
                Material.AbsorptionCoefficient, MaximumPathDistance),
            ProposalWeightAndReserved = new Vector4(ProposalWeight, 0.0f, 0.0f, 0.0f),
            GeometryRevisionLow = GiCausticGpuAbi.Low32(GeometryRevision),
            GeometryRevisionHigh = GiCausticGpuAbi.High32(GeometryRevision),
            TransformRevisionLow = GiCausticGpuAbi.Low32(TransformRevision),
            TransformRevisionHigh = GiCausticGpuAbi.High32(TransformRevision)
        };
        if (!hero.IsValid)
        {
            hero = default;
            reason = "caustic-hero-gpu-contract-invalid";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>Immutable metadata and exact proposal distribution for one cache generation.</summary>
public sealed class GiCausticTaskGenerationBatch
{
    private readonly GPUCausticEmitterV1[] _emitters;
    private readonly GPUCausticHeroV1[] _heroes;
    private readonly GPUCausticProposalPairV1[] _pairs;

    private GiCausticTaskGenerationBatch(
        GPUCausticEmitterV1[] emitters,
        GPUCausticHeroV1[] heroes,
        GPUCausticProposalPairV1[] pairs,
        int taskCount,
        GiCausticCacheRevision revision)
    {
        _emitters = emitters;
        _heroes = heroes;
        _pairs = pairs;
        TaskCount = taskCount;
        Revision = revision;
        RevisionFingerprint = GiCausticGpuAbi.ComputeRevisionFingerprint(revision);
    }

    public ReadOnlyMemory<GPUCausticEmitterV1> Emitters => _emitters;
    public ReadOnlyMemory<GPUCausticHeroV1> Heroes => _heroes;
    public ReadOnlyMemory<GPUCausticProposalPairV1> ProposalPairs => _pairs;
    public int TaskCount { get; }
    public GiCausticCacheRevision Revision { get; }
    public ulong RevisionFingerprint { get; }
    public ulong MetadataPayloadBytes => checked(
        (ulong)_emitters.Length * GiCausticGpuAbi.EmitterRecordBytes +
        (ulong)_heroes.Length * GiCausticGpuAbi.HeroRecordBytes +
        (ulong)_pairs.Length * GiCausticGpuAbi.ProposalPairRecordBytes);

    public static bool TryCreate(
        ReadOnlySpan<GPUCausticEmitterV1> emitters,
        ReadOnlySpan<GPUCausticHeroV1> heroes,
        ReadOnlySpan<GPUCausticProposalPairV1> pairs,
        int taskCount,
        in GiCausticCacheRevision revision,
        out GiCausticTaskGenerationBatch? batch,
        out string reason)
    {
        batch = null;
        if (!revision.IsValid || revision.TransportAbi != GiCausticGpuAbi.Version ||
            taskCount <= 0 || emitters.IsEmpty || heroes.IsEmpty || pairs.IsEmpty ||
            emitters.Length > GiCausticGpuTaskGenerationFlags.MaximumEmitterCount ||
            heroes.Length > GiCausticGpuTaskGenerationFlags.MaximumHeroCount ||
            pairs.Length > GiCausticGpuTaskGenerationFlags.MaximumProposalPairCount ||
            pairs.Length > emitters.Length * heroes.Length)
        {
            reason = "caustic-task-generation-batch-shape-invalid";
            return false;
        }

        var emitterIds = new HashSet<uint>();
        for (int index = 0; index < emitters.Length; index++)
        {
            if (!emitters[index].IsValid ||
                !emitterIds.Add(emitters[index].StableSourceId))
            {
                reason = "caustic-task-generation-emitter-invalid-or-duplicate";
                return false;
            }
        }
        var heroIds = new HashSet<uint>();
        for (int index = 0; index < heroes.Length; index++)
        {
            if (!heroes[index].IsValid || !heroIds.Add(heroes[index].StableHeroId))
            {
                reason = "caustic-task-generation-hero-invalid-or-duplicate";
                return false;
            }
        }

        var pairIds = new HashSet<uint>();
        var emitterMarginals = new Dictionary<uint, double>();
        var casterSums = new Dictionary<uint, double>();
        double previousCdf = 0.0;
        for (int index = 0; index < pairs.Length; index++)
        {
            ref readonly GPUCausticProposalPairV1 pair = ref pairs[index];
            if (!pair.IsValidFor(emitters.Length, heroes.Length) ||
                !pairIds.Add(pair.StablePairId) || pair.CdfUpper <= previousCdf)
            {
                reason = "caustic-task-generation-proposal-entry-invalid";
                return false;
            }
            double mass = (double)pair.EmitterPdf * pair.CasterGivenEmitterPdf;
            double actualMass = pair.CdfUpper - previousCdf;
            double tolerance = Math.Max(2.0e-6, Math.Abs(mass) * 2.0e-4);
            if (!double.IsFinite(mass) || Math.Abs(actualMass - mass) > tolerance)
            {
                reason = "caustic-task-generation-proposal-cdf-mass-mismatch";
                return false;
            }
            if (emitterMarginals.TryGetValue(pair.EmitterIndex, out double marginal) &&
                Math.Abs(marginal - pair.EmitterPdf) > 2.0e-6)
            {
                reason = "caustic-task-generation-emitter-marginal-inconsistent";
                return false;
            }
            emitterMarginals[pair.EmitterIndex] = pair.EmitterPdf;
            casterSums[pair.EmitterIndex] =
                casterSums.GetValueOrDefault(pair.EmitterIndex) +
                pair.CasterGivenEmitterPdf;
            previousCdf = pair.CdfUpper;
        }
        if (Math.Abs(previousCdf - 1.0) > 2.0e-6 ||
            emitterMarginals.Count != emitters.Length)
        {
            reason = "caustic-task-generation-proposal-cdf-incomplete";
            return false;
        }
        double emitterSum = 0.0;
        foreach ((uint emitterIndex, double marginal) in emitterMarginals)
        {
            emitterSum += marginal;
            if (!casterSums.TryGetValue(emitterIndex, out double casterSum) ||
                Math.Abs(casterSum - 1.0) > 2.0e-5)
            {
                reason = "caustic-task-generation-conditional-caster-pdf-incomplete";
                return false;
            }
        }
        if (Math.Abs(emitterSum - 1.0) > 2.0e-5)
        {
            reason = "caustic-task-generation-emitter-pdf-incomplete";
            return false;
        }

        batch = new GiCausticTaskGenerationBatch(
            emitters.ToArray(), heroes.ToArray(), pairs.ToArray(), taskCount, revision);
        reason = string.Empty;
        return true;
    }
}

/// <summary>Deterministic compiler for emitted-power × hero-potential proposals.</summary>
public static class GiCausticTaskGenerationCompiler
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public static GiCausticHeroRevisionIdentity ComputeHeroRevisionIdentity(
        GiCausticHeroSourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.HasEligibleHeroes)
            return default;

        ulong material = FnvOffset;
        ulong geometry = FnvOffset;
        ulong transform = FnvOffset;
        ulong stable = FnvOffset;
        ReadOnlySpan<GiCausticHeroSource> heroes = snapshot.Heroes.Span;
        for (int index = 0; index < heroes.Length; index++)
        {
            ref readonly GiCausticHeroSource hero = ref heroes[index];
            material = Hash(material, hero.StableHeroId);
            material = Hash(material, hero.MaterialRevision);
            material = Hash(material, (uint)hero.Material.Participation);
            material = Hash(material,
                BitConverter.SingleToUInt32Bits(hero.Material.Roughness));
            material = Hash(material,
                BitConverter.SingleToUInt32Bits(hero.Material.Ior));
            material = Hash(material,
                BitConverter.SingleToUInt32Bits(
                    hero.Material.AbsorptionCoefficient.X));
            material = Hash(material,
                BitConverter.SingleToUInt32Bits(
                    hero.Material.AbsorptionCoefficient.Y));
            material = Hash(material,
                BitConverter.SingleToUInt32Bits(
                    hero.Material.AbsorptionCoefficient.Z));

            geometry = Hash(geometry, hero.StableHeroId);
            geometry = Hash(geometry, hero.GeometryRevision);
            transform = Hash(transform, hero.StableHeroId);
            transform = Hash(transform, hero.TransformRevision);
            stable = Hash(stable, hero.StableHeroId);
        }
        stable = Hash(stable, snapshot.TopLevelInstanceSignature);

        ulong aggregate = FnvOffset;
        aggregate = Hash(aggregate, snapshot.SceneContentRevision);
        aggregate = Hash(aggregate, snapshot.RaySceneContentEpoch);
        aggregate = Hash(aggregate, snapshot.TopLevelInstanceSignature);
        aggregate = Hash(aggregate, material);
        aggregate = Hash(aggregate, geometry);
        aggregate = Hash(aggregate, transform);
        aggregate = Hash(aggregate, stable);
        aggregate = Hash(aggregate, checked((uint)heroes.Length));
        return new GiCausticHeroRevisionIdentity(
            NonZero(aggregate),
            NonZero(material),
            NonZero(geometry),
            NonZero(transform),
            NonZero(stable),
            heroes.Length);
    }

    public static ulong ComputeEmitterDistributionRevision(
        ulong punctualLightRevision,
        ulong emissiveDistributionRevision)
    {
        if (punctualLightRevision == 0UL || emissiveDistributionRevision == 0UL)
            return 0UL;
        ulong hash = Hash(FnvOffset, punctualLightRevision);
        hash = Hash(hash, emissiveDistributionRevision);
        return NonZero(hash);
    }

    public static bool TryCompile(
        ReadOnlySpan<GiCausticEmitterSource> emitterSources,
        ReadOnlySpan<GiCausticHeroSource> heroSources,
        int taskCount,
        in GiCausticCacheRevision revision,
        out GiCausticTaskGenerationBatch? batch,
        out string reason)
    {
        batch = null;
        if (emitterSources.IsEmpty || heroSources.IsEmpty || taskCount <= 0)
        {
            reason = "caustic-task-generation-source-set-empty";
            return false;
        }
        var orderedEmitters = emitterSources.ToArray();
        var orderedHeroes = heroSources.ToArray();
        Array.Sort(orderedEmitters, static (left, right) =>
            left.StableSourceId.CompareTo(right.StableSourceId));
        Array.Sort(orderedHeroes, static (left, right) =>
            left.StableHeroId.CompareTo(right.StableHeroId));

        var emitters = new GPUCausticEmitterV1[orderedEmitters.Length];
        for (int index = 0; index < emitters.Length; index++)
        {
            if (!orderedEmitters[index].TryCompile(out emitters[index], out reason))
                return false;
            if (index > 0 && emitters[index - 1].StableSourceId == emitters[index].StableSourceId)
            {
                reason = "caustic-task-generation-source-id-duplicate";
                return false;
            }
        }
        var heroes = new GPUCausticHeroV1[orderedHeroes.Length];
        for (int index = 0; index < heroes.Length; index++)
        {
            if (!orderedHeroes[index].TryCompile(out heroes[index], out reason))
                return false;
            if (index > 0 && heroes[index - 1].StableHeroId == heroes[index].StableHeroId)
            {
                reason = "caustic-task-generation-hero-id-duplicate";
                return false;
            }
        }

        var relationWeights = new double[emitters.Length, heroes.Length];
        var emitterTotals = new double[emitters.Length];
        double total = 0.0;
        for (int emitterIndex = 0; emitterIndex < emitters.Length; emitterIndex++)
        {
            for (int heroIndex = 0; heroIndex < heroes.Length; heroIndex++)
            {
                double potential = Potential(emitters[emitterIndex], heroes[heroIndex]);
                double relationship = potential *
                    orderedHeroes[heroIndex].ProposalWeight;
                if (!double.IsFinite(relationship) || relationship <= 0.0)
                    continue;
                relationWeights[emitterIndex, heroIndex] = relationship;
                emitterTotals[emitterIndex] += relationship;
            }
            double sourceWeight = emitters[emitterIndex]
                .RadiometricValueAndSelectionWeight.W;
            emitterTotals[emitterIndex] *= sourceWeight;
            total += emitterTotals[emitterIndex];
        }
        if (!double.IsFinite(total) || total <= 0.0)
        {
            reason = "caustic-task-generation-no-emitter-hero-support";
            return false;
        }

        var pairs = new List<GPUCausticProposalPairV1>(
            Math.Min(emitters.Length * heroes.Length,
                GiCausticGpuTaskGenerationFlags.MaximumProposalPairCount));
        double cdf = 0.0;
        for (int emitterIndex = 0; emitterIndex < emitters.Length; emitterIndex++)
        {
            if (emitterTotals[emitterIndex] <= 0.0)
            {
                reason = "caustic-task-generation-emitter-has-no-supported-hero";
                return false;
            }
            double sourceWeight = emitters[emitterIndex]
                .RadiometricValueAndSelectionWeight.W;
            double relationshipTotal = emitterTotals[emitterIndex] / sourceWeight;
            float emitterPdf = checked((float)(emitterTotals[emitterIndex] / total));
            for (int heroIndex = 0; heroIndex < heroes.Length; heroIndex++)
            {
                double relation = relationWeights[emitterIndex, heroIndex];
                if (relation <= 0.0)
                    continue;
                float casterPdf = checked((float)(relation / relationshipTotal));
                double mass = (double)emitterPdf * casterPdf;
                cdf += mass;
                float cdfUpper = checked((float)cdf);
                float prior = pairs.Count == 0 ? 0.0f : pairs[^1].CdfUpper;
                if (!(cdfUpper > prior))
                {
                    reason = "caustic-task-generation-proposal-probability-underflow";
                    return false;
                }
                pairs.Add(new GPUCausticProposalPairV1
                {
                    EmitterIndex = checked((uint)emitterIndex),
                    HeroIndex = checked((uint)heroIndex),
                    StablePairId = StablePairId(
                        emitters[emitterIndex].StableSourceId,
                        heroes[heroIndex].StableHeroId),
                    EmitterPdf = emitterPdf,
                    CasterGivenEmitterPdf = casterPdf,
                    CdfUpper = cdfUpper,
                    TargetingMixtureProbability = emitters[emitterIndex]
                        .NormalAndTargetingMix.W
                });
            }
        }
        if (pairs.Count == 0 ||
            pairs.Count > GiCausticGpuTaskGenerationFlags.MaximumProposalPairCount)
        {
            reason = "caustic-task-generation-proposal-pair-capacity";
            return false;
        }
        GPUCausticProposalPairV1 last = pairs[^1];
        last.CdfUpper = 1.0f;
        pairs[^1] = last;
        return GiCausticTaskGenerationBatch.TryCreate(
            emitters, heroes, CollectionsMarshal.AsSpan(pairs), taskCount,
            revision, out batch, out reason);
    }

    /// <summary>
    /// Converts the renderer's stable punctual-light snapshot. Area and
    /// emissive-triangle sources use the generic source contract above.
    /// </summary>
    public static bool TryCreatePunctualSources(
        in LightFrameSnapshot snapshot,
        float directionalDiskRadius,
        float targetingMixtureProbability,
        out GiCausticEmitterSource[] sources,
        out string reason)
    {
        sources = Array.Empty<GiCausticEmitterSource>();
        if (snapshot.Count <= 0 || snapshot.Lights.Length < snapshot.Count ||
            snapshot.StableIdentities.Length < snapshot.Count ||
            snapshot.ContentRevision == 0UL ||
            !float.IsFinite(directionalDiskRadius) || directionalDiskRadius <= 0.0f ||
            !float.IsFinite(targetingMixtureProbability) ||
            targetingMixtureProbability is < 0.0f or > 0.95f)
        {
            reason = "caustic-punctual-light-snapshot-invalid";
            return false;
        }

        var result = new List<GiCausticEmitterSource>(snapshot.Count);
        ReadOnlySpan<Light> lights = snapshot.Lights.Span[..snapshot.Count];
        ReadOnlySpan<uint> identities = snapshot.StableIdentities.Span[..snapshot.Count];
        for (int index = 0; index < lights.Length; index++)
        {
            Light light = lights[index];
            uint identity = identities[index];
            Vector3 radiometry = Vector3.Max(light.Color, Vector3.Zero) *
                Math.Max(light.Intensity, 0.0f);
            float luminance = Vector3.Dot(radiometry,
                new Vector3(0.2126f, 0.7152f, 0.0722f));
            if (identity == 0u || !Finite(radiometry) || luminance <= 0.0f)
                continue;

            GiCausticGpuEmitterType type;
            float range;
            float outerCosine = 0.0f;
            float innerCosine = 0.0f;
            float weight;
            switch (light.Type)
            {
                case LightType.Point:
                    type = GiCausticGpuEmitterType.Point;
                    range = Math.Max(light.Range, 1.0e-3f);
                    weight = luminance * 4.0f * MathF.PI;
                    break;
                case LightType.Spot:
                    type = GiCausticGpuEmitterType.Spot;
                    range = Math.Max(light.Range, 1.0e-3f);
                    outerCosine = MathF.Cos(Math.Clamp(light.SpotAngle,
                        1.0e-3f, MathF.PI - 1.0e-3f));
                    innerCosine = Math.Min(1.0f, outerCosine + 0.1f);
                    weight = luminance * 2.0f * MathF.PI *
                        (1.0f - outerCosine);
                    break;
                case LightType.Directional:
                    type = GiCausticGpuEmitterType.DirectionalDisk;
                    range = directionalDiskRadius;
                    weight = luminance * MathF.PI * range * range;
                    break;
                default:
                    continue;
            }
            Vector3 direction = light.Direction;
            if (type != GiCausticGpuEmitterType.Point)
            {
                float directionLength = direction.Length();
                if (!float.IsFinite(directionLength) || directionLength <= 1.0e-8f)
                    continue;
                direction /= directionLength;
            }
            result.Add(new GiCausticEmitterSource(
                type,
                identity,
                light.Position,
                direction,
                radiometry,
                range,
                Vector3.Zero,
                Vector3.Zero,
                outerCosine,
                innerCosine,
                false,
                Math.Max(weight, 1.0e-12f),
                type == GiCausticGpuEmitterType.DirectionalDisk
                    ? 0.0f
                    : targetingMixtureProbability,
                snapshot.ContentRevision));
        }
        if (result.Count == 0)
        {
            reason = "caustic-punctual-light-snapshot-has-no-eligible-emitter";
            return false;
        }

        sources = result.ToArray();
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Converts only exact emissive-triangle leaves from the prerequisite
    /// spatial-emitter snapshot. Hierarchy nodes, proxy rollback records, and
    /// coverage/dynamic-texture approximations are deliberately excluded from
    /// the reference C4 proposal distribution.
    /// </summary>
    public static bool TryCreateEmissiveTriangleSources(
        ReadOnlySpan<GPUDdgiEmissiveSource> snapshot,
        ulong contentRevision,
        float targetingMixtureProbability,
        out GiCausticEmitterSource[] sources,
        out string reason)
    {
        sources = Array.Empty<GiCausticEmitterSource>();
        if (contentRevision == 0UL ||
            !float.IsFinite(targetingMixtureProbability) ||
            targetingMixtureProbability is < 0.0f or > 0.95f)
        {
            reason = "caustic-emissive-source-snapshot-invalid";
            return false;
        }

        var result = new List<GiCausticEmitterSource>(snapshot.Length);
        for (int index = 0; index < snapshot.Length; index++)
        {
            ref readonly GPUDdgiEmissiveSource source = ref snapshot[index];
            DdgiEmissiveSourceFlags flags =
                DdgiEmissiveTriangleTable.DecodeFlags(source);
            const DdgiEmissiveSourceFlags prohibited =
                DdgiEmissiveSourceFlags.AlphaCoverageApproximation |
                DdgiEmissiveSourceFlags.ProxyRollback |
                DdgiEmissiveSourceFlags.SpatialHierarchy |
                DdgiEmissiveSourceFlags.MacroEmitter |
                DdgiEmissiveSourceFlags.DynamicEmissiveTexture;
            if ((flags & DdgiEmissiveSourceFlags.Triangle) == 0 ||
                (flags & prohibited) != 0)
            {
                continue;
            }

            Vector3 vertex0 = new(
                source.Vertex0Area.X,
                source.Vertex0Area.Y,
                source.Vertex0Area.Z);
            Vector3 edge1 = new(
                source.Edge1AliasProbability.X,
                source.Edge1AliasProbability.Y,
                source.Edge1AliasProbability.Z);
            Vector3 edge2 = new(
                source.Edge2AliasFlags.X,
                source.Edge2AliasFlags.Y,
                source.Edge2AliasFlags.Z);
            Vector3 radiance = Vector3.Max(new Vector3(
                source.RadianceSelectionProbability.X,
                source.RadianceSelectionProbability.Y,
                source.RadianceSelectionProbability.Z), Vector3.Zero);
            float area = 0.5f * Vector3.Cross(edge1, edge2).Length();
            float luminance = Vector3.Dot(
                radiance, new Vector3(0.2126f, 0.7152f, 0.0722f));
            bool twoSided = (flags & DdgiEmissiveSourceFlags.DoubleSided) != 0;
            float selectionWeight = luminance * area * (twoSided ? 2.0f : 1.0f);
            if (!Finite(vertex0) || !Finite(edge1) || !Finite(edge2) ||
                !Finite(radiance) || !float.IsFinite(area) || area <= 1.0e-10f ||
                !float.IsFinite(selectionWeight) || selectionWeight <= 0.0f)
            {
                reason = "caustic-emissive-triangle-source-nonfinite";
                return false;
            }

            uint stableId = 0x8000_0000u | checked((uint)index + 1u);
            result.Add(new GiCausticEmitterSource(
                GiCausticGpuEmitterType.EmissiveTriangle,
                stableId,
                vertex0,
                Vector3.Zero,
                radiance,
                1.0f,
                edge1,
                edge2,
                0.0f,
                0.0f,
                twoSided,
                selectionWeight,
                targetingMixtureProbability,
                contentRevision));
        }

        sources = result.ToArray();
        reason = sources.Length == 0
            ? "caustic-emissive-source-snapshot-has-no-exact-triangle"
            : "valid";
        return true;
    }

    private static double Potential(
        in GPUCausticEmitterV1 emitter,
        in GPUCausticHeroV1 hero)
    {
        if (emitter.Type == GiCausticGpuEmitterType.DirectionalDisk)
            return 1.0;
        Vector3 emitterPosition = new(
            emitter.PositionAndRange.X,
            emitter.PositionAndRange.Y,
            emitter.PositionAndRange.Z);
        if (emitter.Type is GiCausticGpuEmitterType.AreaTriangle or
            GiCausticGpuEmitterType.EmissiveTriangle)
        {
            emitterPosition += (new Vector3(
                    emitter.Edge1AndArea.X,
                    emitter.Edge1AndArea.Y,
                    emitter.Edge1AndArea.Z) +
                new Vector3(
                    emitter.Edge2AndCosInner.X,
                    emitter.Edge2AndCosInner.Y,
                    emitter.Edge2AndCosInner.Z)) / 3.0f;
        }
        Vector3 heroCenter = new(
            hero.BoundsCenterAndRadius.X,
            hero.BoundsCenterAndRadius.Y,
            hero.BoundsCenterAndRadius.Z);
        Vector3 toHero = heroCenter - emitterPosition;
        double distance = toHero.Length();
        double radius = hero.BoundsCenterAndRadius.W;
        if (!double.IsFinite(distance) || distance <= 1.0e-12)
            return 1.0;
        if (emitter.Type is GiCausticGpuEmitterType.Point or
                GiCausticGpuEmitterType.Spot &&
            distance - radius >= emitter.PositionAndRange.W)
        {
            return 0.0;
        }
        if (emitter.Type == GiCausticGpuEmitterType.Spot)
        {
            Vector3 direction = new(
                emitter.DirectionAndCosOuter.X,
                emitter.DirectionAndCosOuter.Y,
                emitter.DirectionAndCosOuter.Z);
            double centerCosine = Vector3.Dot(Vector3.Normalize(toHero), direction);
            double angularRadius = Math.Asin(Math.Clamp(radius / distance, 0.0, 1.0));
            double closestAngle = Math.Max(0.0,
                Math.Acos(Math.Clamp(centerCosine, -1.0, 1.0)) - angularRadius);
            if (Math.Cos(closestAngle) < emitter.DirectionAndCosOuter.W)
                return 0.0;
        }
        double ratio = Math.Clamp(radius / distance, 0.0, 1.0);
        return Math.Max(1.0e-12, 1.0 - Math.Sqrt(Math.Max(0.0, 1.0 - ratio * ratio)));
    }

    private static uint StablePairId(uint sourceId, uint heroId)
    {
        uint value = sourceId ^ RotateLeft(heroId, 16) ^ 0x9e3779b9u;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value == 0u ? 1u : value;
    }

    private static uint RotateLeft(uint value, int count) =>
        (value << count) | (value >> (32 - count));

    private static ulong Hash(ulong hash, uint value)
    {
        hash = HashByte(hash, (byte)value);
        hash = HashByte(hash, (byte)(value >> 8));
        hash = HashByte(hash, (byte)(value >> 16));
        return HashByte(hash, (byte)(value >> 24));
    }

    private static ulong Hash(ulong hash, ulong value)
    {
        hash = Hash(hash, unchecked((uint)value));
        return Hash(hash, unchecked((uint)(value >> 32)));
    }

    private static ulong HashByte(ulong hash, byte value) =>
        unchecked((hash ^ value) * FnvPrime);

    private static ulong NonZero(ulong value) => value == 0UL ? 1UL : value;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>
/// Concrete transfer producer. It uploads only immutable generation metadata;
/// the compute task pass creates every task and audits all proposal factors.
/// </summary>
public sealed unsafe class GiCausticTaggedTransportGpuProducer :
    IGiCausticTaggedTransportProducer
{
    private readonly VulkanContext _context;
    private readonly BufferManager _bufferManager;
    private readonly StagingRing _stagingRing;
    private readonly GiCausticGpuResourceLayout _layout;
    private readonly GiCausticTaskGenerationBatch _batch;

    public GiCausticTaggedTransportGpuProducer(
        VulkanContext context,
        BufferManager bufferManager,
        StagingRing stagingRing,
        in GiCausticGpuResourceLayout layout,
        GiCausticTaskGenerationBatch batch)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
        _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
        _batch = batch ?? throw new ArgumentNullException(nameof(batch));
        if (!GiCausticGpuVulkanRuntimeContract.TryValidateRecordingLayout(
                layout, out string reason))
        {
            throw new ArgumentException(reason, nameof(layout));
        }
        if (batch.TaskCount > layout.TaskCapacity ||
            batch.Emitters.Length > layout.EmitterCapacity ||
            batch.Heroes.Length > layout.HeroCapacity ||
            batch.ProposalPairs.Length > layout.ProposalPairCapacity)
        {
            throw new ArgumentException(
                "C4 task-generation batch exceeds the exact allocation.", nameof(batch));
        }
        _layout = layout;
        Contract = new GiCausticTaggedTransportProducerContract(
            IsAvailable: true,
            C4GpuAbiVersion: GiCausticGpuAbi.Version,
            TransportAbiVersion: batch.Revision.TransportAbi,
            TaskCount: batch.TaskCount,
            TaskRecordStrideBytes: GiCausticGpuAbi.TaskRecordBytes,
            TaskPayloadBytes: checked((ulong)batch.TaskCount *
                GiCausticGpuAbi.TaskRecordBytes),
            EmitterCount: batch.Emitters.Length,
            HeroCount: batch.Heroes.Length,
            ProposalPairCount: batch.ProposalPairs.Length,
            MetadataPayloadBytes: batch.MetadataPayloadBytes,
            RevisionFingerprint: batch.RevisionFingerprint,
            TaggedLightDistributionAvailable: true,
            HeroCasterMetadataAvailable: true,
            CurrentPoseAccelerationStructureAvailable: true,
            FirstDiffuseEndpointsOnly: true,
            SupportsTransactionStamping: true,
            GpuTaskGeneration: true,
            ExactTwoLevelProposal: true,
            CanonicalEmissionSupport: true,
            ProducerWriteStageMask: PipelineStageFlags2.TransferBit,
            ProducerWriteAccessMask: AccessFlags2.TransferWriteBit);
    }

    public GiCausticTaggedTransportProducerContract Contract { get; }

    public bool TryRecordTaskUpload(
        CommandBuffer commandBuffer,
        in GiCausticTaggedTransportTaskUploadTarget target,
        out string reason)
    {
        if (commandBuffer.Handle == 0 || !target.IsValid ||
            !target.Token.Revision.Equals(_batch.Revision) ||
            target.EmitterRecordOffsetBytes != _layout.EmitterRecordOffsetBytes ||
            target.HeroRecordOffsetBytes != _layout.HeroRecordOffsetBytes ||
            target.ProposalPairRecordOffsetBytes !=
                _layout.ProposalPairRecordOffsetBytes ||
            target.EmitterCount != _batch.Emitters.Length ||
            target.HeroCount != _batch.Heroes.Length ||
            target.ProposalPairCount != _batch.ProposalPairs.Length ||
            target.MetadataPayloadBytes != _batch.MetadataPayloadBytes)
        {
            reason = "caustic-task-generation-upload-target-mismatch";
            return false;
        }

        try
        {
            ulong totalBytes = _batch.MetadataPayloadBytes;
            (BufferHandle staging, ulong stagingOffset) = _stagingRing.Allocate(totalBytes);
            byte* mapped = (byte*)_bufferManager.GetMappedPointer(staging) +
                checked((nint)stagingOffset);
            Span<byte> destination = new(mapped, checked((int)totalBytes));
            int emitterBytes = checked(_batch.Emitters.Length *
                GiCausticGpuAbi.EmitterRecordBytes);
            int heroBytes = checked(_batch.Heroes.Length *
                GiCausticGpuAbi.HeroRecordBytes);
            MemoryMarshal.AsBytes(_batch.Emitters.Span).CopyTo(destination);
            MemoryMarshal.AsBytes(_batch.Heroes.Span).CopyTo(
                destination[emitterBytes..]);
            MemoryMarshal.AsBytes(_batch.ProposalPairs.Span).CopyTo(
                destination[(emitterBytes + heroBytes)..]);
            _stagingRing.Flush(staging, stagingOffset, totalBytes);

            VkBuffer source = _bufferManager.GetBuffer(staging);
            VkBuffer targetBuffer = _bufferManager.GetBuffer(target.TaskBuffer);
            Span<BufferCopy> regions = stackalloc BufferCopy[3];
            regions[0] = new BufferCopy
            {
                SrcOffset = stagingOffset,
                DstOffset = target.EmitterRecordOffsetBytes,
                Size = checked((ulong)emitterBytes)
            };
            regions[1] = new BufferCopy
            {
                SrcOffset = checked(stagingOffset + (ulong)emitterBytes),
                DstOffset = target.HeroRecordOffsetBytes,
                Size = checked((ulong)heroBytes)
            };
            regions[2] = new BufferCopy
            {
                SrcOffset = checked(stagingOffset + (ulong)emitterBytes +
                    (ulong)heroBytes),
                DstOffset = target.ProposalPairRecordOffsetBytes,
                Size = checked((ulong)(_batch.ProposalPairs.Length *
                    GiCausticGpuAbi.ProposalPairRecordBytes))
            };
            fixed (BufferCopy* regionPointer = regions)
            {
                _context.Api.CmdCopyBuffer(commandBuffer, source, targetBuffer,
                    checked((uint)regions.Length), regionPointer);
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is OverflowException or
            InvalidOperationException or ArgumentException)
        {
            reason = "caustic-task-generation-upload-failed:" + exception.Message;
            return false;
        }
    }
}
