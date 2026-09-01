using Njulf.Assets.Cooked;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

public enum AutomaticPlanarMaterialSemantic : uint
{
    Generic = 0,
    Mirror = 1,
    WaterSurface = 2
}

public enum AutomaticPlanarCandidateRejectionReason : uint
{
    None = 0,
    Invisible = 1,
    Deforming = 2,
    InvalidEvidence = 3,
    MaterialNotEligible = 4,
    TextureStatisticsIncomplete = 5,
    InsufficientProjectedCoverage = 6,
    InvalidTransform = 7,
    MemoryDenied = 8,
    CaptureLimit = 9,
    Stale = 10,
    MaterialOptInDisabled = 11
}

public readonly record struct AutomaticPlanarCandidateInput(
    ulong StableIdentity,
    uint ObjectIndex,
    ulong ContentRevision,
    uint ReceiverIdentity,
    GiPrimitivePlanarEvidence Evidence,
    Matrix4x4 WorldMatrix,
    bool MaterialOptInEnabled,
    AutomaticPlanarMaterialSemantic MaterialSemantic,
    float MeanRoughness,
    float MaximumF0,
    bool TextureStatisticsComplete,
    bool Visible,
    bool Deforming,
    float ProjectedPixels,
    float ViewFresnel,
    float DistanceToCamera,
    bool DynamicOrDirty);

public readonly record struct AutomaticPlanarCandidate(
    ulong StableIdentity,
    uint ObjectIndex,
    ulong ContentRevision,
    uint ReceiverIdentity,
    Vector4 WorldPlane,
    Vector3 WorldOrigin,
    Vector3 WorldTangent,
    Vector3 WorldBitangent,
    Vector2 ProjectedBoundsMin,
    Vector2 ProjectedBoundsMax,
    float WorldDiagonal,
    AutomaticPlanarMaterialSemantic MaterialSemantic,
    float MeanRoughness,
    float MaximumF0,
    float ProjectedPixels,
    float ViewFresnel,
    float DistanceToCamera,
    bool DynamicOrDirty)
{
    public float Gloss => 1.0f - Math.Clamp(MeanRoughness, 0.0f, 1.0f);
}

public readonly record struct AutomaticPlanarCandidateAdmission(
    bool Admitted,
    AutomaticPlanarCandidate Candidate,
    AutomaticPlanarCandidateRejectionReason RejectionReason,
    string Detail);

public static class AutomaticPlanarCandidateAnalyzer
{
    public const float MaximumGenericRoughness = 0.18f;
    public const float MinimumGenericF0 = 0.02f;
    public const float MinimumPixelsAt1080P = 4096.0f;

    public static float ResolveMinimumProjectedPixels(
        uint outputWidth,
        uint outputHeight)
    {
        if (outputWidth == 0u || outputHeight == 0u)
            return float.PositiveInfinity;
        double scaled = MinimumPixelsAt1080P *
            ((double)outputWidth * outputHeight / (1920.0 * 1080.0));
        return scaled >= float.MaxValue
            ? float.PositiveInfinity
            : (float)scaled;
    }

    public static AutomaticPlanarCandidateAdmission Analyze(
        in AutomaticPlanarCandidateInput input,
        uint outputWidth,
        uint outputHeight)
    {
        if (!input.MaterialOptInEnabled)
        {
            return Reject(
                AutomaticPlanarCandidateRejectionReason.MaterialOptInDisabled,
                "The material has not opted in to automatic planar reflection.");
        }
        if (!input.Visible)
            return Reject(AutomaticPlanarCandidateRejectionReason.Invisible,
                "The rigid instance is not visible.");
        if (input.Deforming)
            return Reject(AutomaticPlanarCandidateRejectionReason.Deforming,
                "Deforming instances cannot own planar captures.");
        if (input.Evidence is null || !input.Evidence.IsValid ||
            input.Evidence.Validate().Count != 0)
        {
            return Reject(
                AutomaticPlanarCandidateRejectionReason.InvalidEvidence,
                input.Evidence?.Detail ?? "Planar evidence is unavailable.");
        }

        float minimumPixels = ResolveMinimumProjectedPixels(
            outputWidth,
            outputHeight);
        if (!float.IsFinite(input.ProjectedPixels) ||
            input.ProjectedPixels < minimumPixels)
        {
            return Reject(
                AutomaticPlanarCandidateRejectionReason
                    .InsufficientProjectedCoverage,
                $"Projected coverage {input.ProjectedPixels:R} is below {minimumPixels:R} pixels.");
        }

        if (!TryTransformEvidence(
                input.Evidence,
                input.WorldMatrix,
                out Vector4 worldPlane,
                out Vector3 worldOrigin,
                out Vector3 worldTangent,
                out Vector3 worldBitangent,
                out Vector2 worldBoundsMin,
                out Vector2 worldBoundsMax,
                out float worldDiagonal))
        {
            return Reject(
                AutomaticPlanarCandidateRejectionReason.InvalidTransform,
                "The instance transform cannot produce a finite world plane.");
        }

        return new AutomaticPlanarCandidateAdmission(
            true,
            new AutomaticPlanarCandidate(
                input.StableIdentity,
                input.ObjectIndex,
                input.ContentRevision,
                input.ReceiverIdentity,
                worldPlane,
                worldOrigin,
                worldTangent,
                worldBitangent,
                worldBoundsMin,
                worldBoundsMax,
                worldDiagonal,
                input.MaterialSemantic,
                Math.Clamp(input.MeanRoughness, 0.0f, 1.0f),
                Math.Clamp(input.MaximumF0, 0.0f, 1.0f),
                input.ProjectedPixels,
                Math.Clamp(input.ViewFresnel, 0.0f, 1.0f),
                Math.Max(input.DistanceToCamera, 0.0f),
                input.DynamicOrDirty),
            AutomaticPlanarCandidateRejectionReason.None,
            string.Empty);
    }

    private static bool TryTransformEvidence(
        GiPrimitivePlanarEvidence evidence,
        Matrix4x4 world,
        out Vector4 plane,
        out Vector3 origin,
        out Vector3 tangent,
        out Vector3 bitangent,
        out Vector2 boundsMin,
        out Vector2 boundsMax,
        out float diagonal)
    {
        plane = default;
        origin = tangent = bitangent = default;
        boundsMin = boundsMax = default;
        diagonal = 0.0f;
        try
        {
            Matrix4x4 normalMatrix = world.Invert().Transpose();
            Vector3 localNormal = new(
                evidence.LocalPlane.X,
                evidence.LocalPlane.Y,
                evidence.LocalPlane.Z);
            Vector3 worldNormal = TransformDirection(
                localNormal,
                normalMatrix).Normalized();
            origin = evidence.LocalOrigin * world;
            tangent = TransformDirection(evidence.LocalTangent, world);
            tangent -= worldNormal * Vector3.Dot(tangent, worldNormal);
            tangent = tangent.Normalized();
            bitangent = Vector3.Cross(worldNormal, tangent).Normalized();
            if (!IsFinite(worldNormal) || !IsFinite(origin) ||
                !IsFinite(tangent) || !IsFinite(bitangent) ||
                worldNormal.LengthSquared() <= 1.0e-12f ||
                tangent.LengthSquared() <= 1.0e-12f ||
                bitangent.LengthSquared() <= 1.0e-12f)
            {
                return false;
            }
            worldNormal = OrientDeterministically(worldNormal);
            // Preserve right-handedness if deterministic normal orientation
            // flips the inverse-transpose result.
            bitangent = Vector3.Cross(worldNormal, tangent).Normalized();
            plane = new Vector4(
                worldNormal,
                -Vector3.Dot(worldNormal, origin));

            Span<Vector2> localCorners = stackalloc Vector2[4]
            {
                evidence.ProjectedBoundsMin,
                new Vector2(evidence.ProjectedBoundsMax.X,
                    evidence.ProjectedBoundsMin.Y),
                evidence.ProjectedBoundsMax,
                new Vector2(evidence.ProjectedBoundsMin.X,
                    evidence.ProjectedBoundsMax.Y)
            };
            boundsMin = new Vector2(float.PositiveInfinity);
            boundsMax = new Vector2(float.NegativeInfinity);
            foreach (Vector2 corner in localCorners)
            {
                Vector3 localPosition = evidence.LocalOrigin +
                    evidence.LocalTangent * corner.X +
                    evidence.LocalBitangent * corner.Y;
                Vector3 worldPosition = localPosition * world;
                Vector3 relative = worldPosition - origin;
                Vector2 projected = new(
                    Vector3.Dot(relative, tangent),
                    Vector3.Dot(relative, bitangent));
                boundsMin = new Vector2(
                    Math.Min(boundsMin.X, projected.X),
                    Math.Min(boundsMin.Y, projected.Y));
                boundsMax = new Vector2(
                    Math.Max(boundsMax.X, projected.X),
                    Math.Max(boundsMax.Y, projected.Y));
            }
            diagonal = (boundsMax - boundsMin).Length();
            return float.IsFinite(diagonal) && diagonal > 0.0f;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static AutomaticPlanarCandidateAdmission Reject(
        AutomaticPlanarCandidateRejectionReason reason,
        string detail) => new(false, default, reason, detail);

    private static Vector3 TransformDirection(
        Vector3 value,
        Matrix4x4 matrix) => new(
        value.X * matrix.M11 + value.Y * matrix.M21 +
            value.Z * matrix.M31,
        value.X * matrix.M12 + value.Y * matrix.M22 +
            value.Z * matrix.M32,
        value.X * matrix.M13 + value.Y * matrix.M23 +
            value.Z * matrix.M33);

    private static Vector3 OrientDeterministically(Vector3 normal)
    {
        float x = MathF.Abs(normal.X);
        float y = MathF.Abs(normal.Y);
        float z = MathF.Abs(normal.Z);
        float largest = x >= y && x >= z
            ? normal.X
            : y >= z
                ? normal.Y
                : normal.Z;
        return largest < 0.0f ? -normal : normal;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

/// <summary>
/// CPU mirror of the receiver identity written by
/// <c>NjulfHybridReflectionCreatePayload</c>. Automatic-planar selection and
/// shader lookup must use the same identity or a valid plane can never match
/// its visible receiver.
/// </summary>
public static class AutomaticPlanarReceiverIdentity
{
    public const uint Mask = 0x003fffffu;

    public static uint Create(
        uint objectIndex,
        uint materialIndex,
        uint materialRevision)
    {
        uint identity = Hash(objectIndex);
        identity = HashCombine(identity, materialIndex);
        identity = HashCombine(identity, materialRevision);
        return identity & Mask;
    }

    public static uint Hash(uint value)
    {
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        value *= 0x846ca68bu;
        value ^= value >> 16;
        return value;
    }

    public static uint HashCombine(uint seed, uint value) =>
        Hash(seed ^ unchecked(value + 0x9e3779b9u +
            (seed << 6) + (seed >> 2)));
}

public sealed record AutomaticPlanarCluster
{
    public required AutomaticPlanarCandidate Representative { get; init; }
    public required IReadOnlyList<AutomaticPlanarCandidate> Members
    {
        get;
        init;
    }
    public required IReadOnlySet<uint> ReceiverIdentities { get; init; }
    public float ProjectedPixels { get; init; }
    public float ViewFresnel { get; init; }
    public float Gloss { get; init; }
    public float DistanceToCamera { get; init; }
    public bool DynamicOrDirty { get; init; }
}

public static class AutomaticPlanarClusterer
{
    public const float MinimumNormalDot = 0.9995f;
    public const float MinimumOffsetToleranceMeters = 0.01f;
    public const float RelativeOffsetTolerance = 0.001f;

    public static IReadOnlyList<AutomaticPlanarCluster> ClusterAndRank(
        IEnumerable<AutomaticPlanarCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ordered = candidates
            .OrderBy(static candidate => candidate.StableIdentity)
            .ToArray();
        var groups = new List<List<AutomaticPlanarCandidate>>();
        foreach (AutomaticPlanarCandidate candidate in ordered)
        {
            List<AutomaticPlanarCandidate>? group = groups.FirstOrDefault(
                existing => SameCluster(existing[0], candidate));
            if (group is null)
            {
                group = new List<AutomaticPlanarCandidate>();
                groups.Add(group);
            }
            group.Add(candidate);
        }

        AutomaticPlanarCluster[] clusters = groups
            .Select(CreateCluster)
            .ToArray();
        Array.Sort(clusters, CompareRank);
        return clusters;
    }

    public static bool SameCluster(
        in AutomaticPlanarCandidate first,
        in AutomaticPlanarCandidate second)
    {
        Vector3 firstNormal = new(
            first.WorldPlane.X,
            first.WorldPlane.Y,
            first.WorldPlane.Z);
        Vector3 secondNormal = new(
            second.WorldPlane.X,
            second.WorldPlane.Y,
            second.WorldPlane.Z);
        float dot = Vector3.Dot(firstNormal, secondNormal);
        if (MathF.Abs(dot) < MinimumNormalDot)
            return false;
        float secondOffset = dot < 0.0f
            ? -second.WorldPlane.W
            : second.WorldPlane.W;
        float tolerance = Math.Max(
            MinimumOffsetToleranceMeters,
            Math.Max(first.WorldDiagonal, second.WorldDiagonal) *
                RelativeOffsetTolerance);
        return MathF.Abs(first.WorldPlane.W - secondOffset) <= tolerance;
    }

    private static AutomaticPlanarCluster CreateCluster(
        List<AutomaticPlanarCandidate> members)
    {
        AutomaticPlanarCandidate representative = members
            .OrderByDescending(static candidate => candidate.ProjectedPixels)
            .ThenBy(static candidate => candidate.StableIdentity)
            .First();
        return new AutomaticPlanarCluster
        {
            Representative = representative,
            Members = members.ToArray(),
            ReceiverIdentities = members
                .Select(static candidate => candidate.ReceiverIdentity)
                .ToHashSet(),
            ProjectedPixels = members.Sum(
                static candidate => candidate.ProjectedPixels),
            ViewFresnel = members.Max(
                static candidate => candidate.ViewFresnel),
            Gloss = members.Max(static candidate => candidate.Gloss),
            DistanceToCamera = members.Min(
                static candidate => candidate.DistanceToCamera),
            DynamicOrDirty = members.Any(
                static candidate => candidate.DynamicOrDirty)
        };
    }

    private static int CompareRank(
        AutomaticPlanarCluster first,
        AutomaticPlanarCluster second)
    {
        int compare = second.ProjectedPixels.CompareTo(first.ProjectedPixels);
        if (compare != 0)
            return compare;
        compare = second.ViewFresnel.CompareTo(first.ViewFresnel);
        if (compare != 0)
            return compare;
        compare = second.Gloss.CompareTo(first.Gloss);
        if (compare != 0)
            return compare;
        compare = SemanticPriority(second.Representative.MaterialSemantic)
            .CompareTo(SemanticPriority(
                first.Representative.MaterialSemantic));
        if (compare != 0)
            return compare;
        compare = first.DistanceToCamera.CompareTo(second.DistanceToCamera);
        return compare != 0
            ? compare
            : first.Representative.StableIdentity.CompareTo(
                second.Representative.StableIdentity);
    }

    private static int SemanticPriority(
        AutomaticPlanarMaterialSemantic semantic) => semantic switch
        {
            AutomaticPlanarMaterialSemantic.WaterSurface => 2,
            AutomaticPlanarMaterialSemantic.Mirror => 1,
            _ => 0
        };
}

public readonly record struct AutomaticPlanarQualityProfile(
    int MaximumCaptures,
    float PreferredLinearScale)
{
    public static AutomaticPlanarQualityProfile For(
        RenderQualityPreset preset) => preset switch
        {
            RenderQualityPreset.Low => new(1, 0.25f),
            RenderQualityPreset.Medium => new(1, 0.25f),
            RenderQualityPreset.Ultra => new(2, 0.50f),
            _ => new(1, 0.50f)
        };
}

public readonly record struct AutomaticPlanarMemoryPlan(
    bool Admitted,
    int CaptureCount,
    float LinearScale,
    ulong FixedReflectionBytes,
    ulong PlanarAllocationBytes,
    ulong TotalReflectionBytes,
    AutomaticPlanarCandidateRejectionReason RejectionReason,
    string Detail);

public static class AutomaticPlanarMemoryPlanner
{
    public const ulong HighBudgetBytes = 160UL * 1024UL * 1024UL;

    public static AutomaticPlanarMemoryPlan Compile(
        ulong fixedReflectionBytes,
        ulong budgetBytes,
        int requestedCaptureCount,
        float preferredScale,
        Func<int, float, ulong> queryExactAllocationBytes)
    {
        ArgumentNullException.ThrowIfNull(queryExactAllocationBytes);
        int maximumCaptures = Math.Max(requestedCaptureCount, 0);
        if (maximumCaptures == 0)
        {
            return new AutomaticPlanarMemoryPlan(
                true, 0, 0.0f, fixedReflectionBytes, 0UL,
                fixedReflectionBytes,
                AutomaticPlanarCandidateRejectionReason.None,
                string.Empty);
        }

        float[] scales = preferredScale >= 0.5f
            ? [0.5f, 0.375f, 0.25f]
            : preferredScale >= 0.375f
                ? [0.375f, 0.25f]
                : [0.25f];
        for (int count = maximumCaptures; count >= 1; count--)
        {
            foreach (float scale in scales)
            {
                ulong planarBytes = queryExactAllocationBytes(count, scale);
                ulong total = checked(fixedReflectionBytes + planarBytes);
                if (total <= budgetBytes)
                {
                    return new AutomaticPlanarMemoryPlan(
                        true,
                        count,
                        scale,
                        fixedReflectionBytes,
                        planarBytes,
                        total,
                        AutomaticPlanarCandidateRejectionReason.None,
                        string.Empty);
                }
            }
        }
        return new AutomaticPlanarMemoryPlan(
            false,
            0,
            0.0f,
            fixedReflectionBytes,
            0UL,
            fixedReflectionBytes,
            AutomaticPlanarCandidateRejectionReason.MemoryDenied,
            "The minimum 0.25-scale planar allocation exceeds the reflection budget.");
    }
}

public enum AutomaticPlanarCaptureAction : uint
{
    None = 0,
    Capture = 1,
    Reproject = 2,
    RejectStale = 3
}

public readonly record struct AutomaticPlanarCaptureState(
    bool Valid,
    ulong ClusterIdentity,
    uint CaptureGeneration,
    uint AgeFrames,
    bool DynamicOrDirty,
    float Confidence,
    Matrix4x4 CurrentReflectedViewProjection,
    Matrix4x4 PreviousReflectedViewProjection);

public static class AutomaticPlanarCapturePolicy
{
    public const uint StableMaximumReuseFrames = 4u;
    public const uint DynamicMaximumReuseFrames = 1u;

    public static AutomaticPlanarCaptureAction Resolve(
        in AutomaticPlanarCaptureState state,
        ulong selectedClusterIdentity,
        bool cameraCut,
        bool candidateChanged,
        bool materialOrTransformChanged,
        bool dirtyRegionIntersectsReflectedFrustum)
    {
        if (!state.Valid || cameraCut || candidateChanged ||
            materialOrTransformChanged ||
            dirtyRegionIntersectsReflectedFrustum ||
            state.ClusterIdentity != selectedClusterIdentity)
        {
            return AutomaticPlanarCaptureAction.Capture;
        }
        uint maximumAge = state.DynamicOrDirty
            ? DynamicMaximumReuseFrames
            : StableMaximumReuseFrames;
        return state.AgeFrames < maximumAge
            ? AutomaticPlanarCaptureAction.Reproject
            : AutomaticPlanarCaptureAction.Capture;
    }

    public static float ResolveReprojectedConfidence(
        float previousConfidence,
        float holeFraction,
        uint ageFrames)
    {
        float confidence = float.IsFinite(previousConfidence)
            ? Math.Clamp(previousConfidence, 0.0f, 1.0f)
            : 0.0f;
        float holes = float.IsFinite(holeFraction)
            ? Math.Clamp(holeFraction, 0.0f, 1.0f)
            : 1.0f;
        return confidence * (1.0f - holes) *
            MathF.Pow(0.9f, Math.Min(ageFrames, 16u));
    }
}

public static class AutomaticPlanarCameraMath
{
    public static Vector3 ReflectPoint(Vector3 point, Vector4 plane)
    {
        Vector3 normal = NormalizePlane(plane, out float offset);
        float distance = Vector3.Dot(normal, point) + offset;
        return point - 2.0f * distance * normal;
    }

    public static Vector3 ReflectDirection(Vector3 direction, Vector4 plane)
    {
        Vector3 normal = NormalizePlane(plane, out _);
        return direction - 2.0f * Vector3.Dot(normal, direction) * normal;
    }

    public static bool ReceiverMatches(
        uint receiverIdentity,
        uint expectedIdentity,
        Vector3 worldPosition,
        Vector3 worldNormal,
        Vector4 plane,
        float planeTolerance,
        float normalDotThreshold = 0.9995f)
    {
        if (receiverIdentity != expectedIdentity)
            return false;
        Vector3 normal = NormalizePlane(plane, out float offset);
        return MathF.Abs(Vector3.Dot(normal, worldPosition) + offset) <=
                   Math.Max(planeTolerance, 0.0005f) &&
               MathF.Abs(Vector3.Dot(
                   worldNormal.Normalized(),
                   normal)) >= normalDotThreshold;
    }

    private static Vector3 NormalizePlane(Vector4 plane, out float offset)
    {
        Vector3 normal = new(plane.X, plane.Y, plane.Z);
        float length = normal.Length();
        if (!float.IsFinite(length) || length <= 1.0e-12f)
        {
            offset = 0.0f;
            return Vector3.UnitZ;
        }
        offset = plane.W / length;
        return normal / length;
    }
}
