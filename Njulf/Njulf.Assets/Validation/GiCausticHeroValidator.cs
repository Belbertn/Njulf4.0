using System;
using System.Collections.Generic;
using System.Numerics;

namespace Njulf.Assets.Validation;

/// <summary>
/// Serialized authoring intent for the deliberately bounded C4 path. It is
/// separate from generic metallic/transmission parameters so ordinary shiny
/// assets never cause photon/cache work.
/// </summary>
public enum ModelGiCausticParticipationMode : byte
{
    None = 0,
    MirrorHero = 1,
    ClosedDielectricHero = 2,
    RoughSpecularReference = 3
}

public enum ModelGiCausticHeroValidationReason : byte
{
    None = 0,
    Disabled,
    AlphaOrThinSurface,
    UnstableTopology,
    NotClosedManifold,
    InconsistentWinding,
    MissingNormals,
    InvalidIor,
    MissingThickness,
    NestedMedium,
    UnsupportedRoughness,
    InvalidAttenuation,
    MissingTopologyEvidence,
    MalformedTopologyEvidence
}

public readonly record struct ModelGiCausticHeroGeometryFacts(
    bool IsStaticOrCurrentPoseQualified,
    bool IsClosedManifold,
    bool HasConsistentWinding,
    bool HasGeometricNormals,
    bool HasUnsupportedNestedMedium);

/// <summary>
/// Authenticated, deterministic cooker evidence for one exact indexed triangle
/// stream.  Schema/algorithm versions are stored with the measurements so an
/// older or caller-fabricated boolean tuple can never admit C4 work.
/// </summary>
public readonly record struct ModelGiCausticHeroTopologyEvidence(
    uint SchemaVersion,
    uint AlgorithmVersion,
    ModelGiCausticHeroGeometryFacts Facts,
    int SourceVertexCount,
    int CanonicalVertexCount,
    int TriangleCount,
    int BoundaryEdgeCount,
    int NonManifoldEdgeCount,
    int InconsistentWindingEdgeCount,
    int ConnectedComponentCount,
    bool HasPositiveOrientation,
    double SignedVolume,
    ulong TopologyHash)
{
    public bool IsCurrent =>
        SchemaVersion == ModelGiCausticHeroTopologyAnalyzer.CurrentSchemaVersion &&
        AlgorithmVersion == ModelGiCausticHeroTopologyAnalyzer.CurrentAlgorithmVersion;

    public bool IsStructurallyValid =>
        IsCurrent && SourceVertexCount >= 3 &&
        CanonicalVertexCount >= 3 && CanonicalVertexCount <= SourceVertexCount &&
        TriangleCount > 0 && BoundaryEdgeCount >= 0 &&
        NonManifoldEdgeCount >= 0 && InconsistentWindingEdgeCount >= 0 &&
        ConnectedComponentCount > 0 && double.IsFinite(SignedVolume) &&
        TopologyHash != 0UL;
}

public readonly record struct ModelGiCausticHeroValidation(
    bool IsEligible,
    ModelGiCausticHeroValidationReason Reason,
    string Detail);

/// <summary>
/// Cooker-side deterministic validation. Runtime still validates current-pose
/// AS/revisions, but malformed/open hero content never needs to wait until a
/// photon pass to be rejected.
/// </summary>
public static class ModelGiCausticHeroValidator
{
    public static ModelGiCausticHeroValidation Validate(
        ModelGiCausticParticipationMode participation,
        ModelAlphaMode alphaMode,
        ModelGiTransmissionPolicy transmissionPolicy,
        float roughness,
        float ior,
        float thicknessFactor,
        float attenuationDistance,
        Vector4 attenuationColor,
        in ModelGiCausticHeroTopologyEvidence evidence)
    {
        if (participation == ModelGiCausticParticipationMode.None)
            return Reject(ModelGiCausticHeroValidationReason.Disabled);
        if (!evidence.IsCurrent)
            return Reject(ModelGiCausticHeroValidationReason.MissingTopologyEvidence);
        if (!evidence.IsStructurallyValid)
            return Reject(ModelGiCausticHeroValidationReason.MalformedTopologyEvidence);
        return Validate(
            participation,
            alphaMode,
            transmissionPolicy,
            roughness,
            ior,
            thicknessFactor,
            attenuationDistance,
            attenuationColor,
            evidence.Facts);
    }

    public static ModelGiCausticHeroValidation Validate(
        ModelGiCausticParticipationMode participation,
        ModelAlphaMode alphaMode,
        ModelGiTransmissionPolicy transmissionPolicy,
        float roughness,
        float ior,
        float thicknessFactor,
        float attenuationDistance,
        Vector4 attenuationColor,
        in ModelGiCausticHeroGeometryFacts geometry)
    {
        if (participation == ModelGiCausticParticipationMode.None)
            return Reject(ModelGiCausticHeroValidationReason.Disabled);
        if (!float.IsFinite(roughness) || !float.IsFinite(ior) ||
            !float.IsFinite(thicknessFactor) ||
            (!float.IsFinite(attenuationDistance) && !float.IsPositiveInfinity(attenuationDistance)) ||
            !Finite(attenuationColor) || attenuationColor.X < 0f || attenuationColor.Y < 0f ||
            attenuationColor.Z < 0f)
        {
            return Reject(ModelGiCausticHeroValidationReason.InvalidAttenuation);
        }
        if (alphaMode != ModelAlphaMode.Opaque ||
            transmissionPolicy == ModelGiTransmissionPolicy.ThinSurface)
        {
            return Reject(ModelGiCausticHeroValidationReason.AlphaOrThinSurface);
        }
        if (!geometry.IsStaticOrCurrentPoseQualified)
            return Reject(ModelGiCausticHeroValidationReason.UnstableTopology);

        if (participation == ModelGiCausticParticipationMode.MirrorHero)
        {
            return roughness is >= 0f and <= 0.04f
                ? Accept()
                : Reject(ModelGiCausticHeroValidationReason.UnsupportedRoughness);
        }
        if (participation == ModelGiCausticParticipationMode.RoughSpecularReference)
        {
            return roughness is > 0.04f and <= 1f
                ? Accept()
                : Reject(ModelGiCausticHeroValidationReason.UnsupportedRoughness);
        }

        if (!geometry.IsClosedManifold)
            return Reject(ModelGiCausticHeroValidationReason.NotClosedManifold);
        if (!geometry.HasConsistentWinding)
            return Reject(ModelGiCausticHeroValidationReason.InconsistentWinding);
        if (!geometry.HasGeometricNormals)
            return Reject(ModelGiCausticHeroValidationReason.MissingNormals);
        if (geometry.HasUnsupportedNestedMedium)
            return Reject(ModelGiCausticHeroValidationReason.NestedMedium);
        if (ior is <= 1f or > 4f)
            return Reject(ModelGiCausticHeroValidationReason.InvalidIor);
        if (thicknessFactor <= 0f || transmissionPolicy != ModelGiTransmissionPolicy.Volume)
            return Reject(ModelGiCausticHeroValidationReason.MissingThickness);
        return roughness is >= 0f and <= 0.04f
            ? Accept()
            : Reject(ModelGiCausticHeroValidationReason.UnsupportedRoughness);
    }

    private static ModelGiCausticHeroValidation Accept() => new(true,
        ModelGiCausticHeroValidationReason.None, "eligible");

    private static ModelGiCausticHeroValidation Reject(ModelGiCausticHeroValidationReason reason) =>
        new(false, reason, reason.ToString());

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}

/// <summary>
/// Conservative exact-position topology analyzer used both while cooking and
/// when a cooked mesh is loaded. Positions that are byte-identical after
/// canonicalizing signed zero are welded, which supports ordinary UV seam
/// duplication without introducing tolerance-dependent false closures.
/// </summary>
public static class ModelGiCausticHeroTopologyAnalyzer
{
    public const uint CurrentSchemaVersion = 1u;
    public const uint CurrentAlgorithmVersion = 1u;

    private const ulong HashOffset = 14695981039346656037UL;
    private const ulong HashPrime = 1099511628211UL;

    public static bool TryAnalyze(
        ReadOnlySpan<Njulf.Core.Math.Vector3> positions,
        ReadOnlySpan<uint> indices,
        bool isSkinned,
        out ModelGiCausticHeroTopologyEvidence evidence,
        out string reason)
    {
        var converted = new Vector3[positions.Length];
        for (int index = 0; index < converted.Length; index++)
        {
            Njulf.Core.Math.Vector3 source = positions[index];
            converted[index] = new Vector3(source.X, source.Y, source.Z);
        }
        return TryAnalyze(converted, indices, isSkinned, out evidence, out reason);
    }

    public static bool TryAnalyze(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        bool isSkinned,
        out ModelGiCausticHeroTopologyEvidence evidence,
        out string reason)
    {
        evidence = default;
        if (positions.Length < 3 || indices.Length < 3 || indices.Length % 3 != 0)
        {
            reason = "caustic-topology-requires-indexed-triangles";
            return false;
        }

        Vector3 minimum = new(float.PositiveInfinity);
        Vector3 maximum = new(float.NegativeInfinity);
        var canonicalByPosition = new Dictionary<PositionKey, int>(positions.Length);
        var canonicalPositions = new List<Vector3>(positions.Length);
        var canonicalIndices = new int[indices.Length];
        ulong hash = AddHash(HashOffset, CurrentSchemaVersion);
        hash = AddHash(hash, CurrentAlgorithmVersion);
        hash = AddHash(hash, checked((uint)positions.Length));
        hash = AddHash(hash, checked((uint)indices.Length));
        hash = AddHash(hash, isSkinned ? 1u : 0u);

        for (int index = 0; index < positions.Length; index++)
        {
            Vector3 position = positions[index];
            if (!Finite(position))
            {
                reason = "caustic-topology-position-must-be-finite";
                return false;
            }
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
            var key = new PositionKey(
                CanonicalBits(position.X),
                CanonicalBits(position.Y),
                CanonicalBits(position.Z));
            if (!canonicalByPosition.TryGetValue(key, out int canonicalIndex))
            {
                canonicalIndex = canonicalPositions.Count;
                canonicalByPosition.Add(key, canonicalIndex);
                canonicalPositions.Add(position);
            }
            hash = AddHash(hash, key.X);
            hash = AddHash(hash, key.Y);
            hash = AddHash(hash, key.Z);
        }

        for (int index = 0; index < indices.Length; index++)
        {
            uint sourceIndex = indices[index];
            if (sourceIndex >= positions.Length)
            {
                reason = "caustic-topology-index-out-of-range";
                return false;
            }
            Vector3 position = positions[checked((int)sourceIndex)];
            var key = new PositionKey(
                CanonicalBits(position.X),
                CanonicalBits(position.Y),
                CanonicalBits(position.Z));
            canonicalIndices[index] = canonicalByPosition[key];
            hash = AddHash(hash, sourceIndex);
        }

        double extentX = (double)maximum.X - minimum.X;
        double extentY = (double)maximum.Y - minimum.Y;
        double extentZ = (double)maximum.Z - minimum.Z;
        double diagonal = Math.Sqrt(
            extentX * extentX + extentY * extentY + extentZ * extentZ);
        if (!double.IsFinite(diagonal) || diagonal <= 0.0)
        {
            reason = "caustic-topology-bounds-degenerate";
            return false;
        }
        double lengthScale = Math.Max(diagonal, 1.0e-12);
        double faceAreaSquaredEpsilon =
            Math.Max(1.0e-30, Math.Pow(lengthScale, 4.0) * 1.0e-24);
        double volumeEpsilon =
            Math.Max(1.0e-30, Math.Pow(lengthScale, 3.0) * 1.0e-15);

        var edges = new Dictionary<EdgeKey, EdgeUse>(indices.Length);
        var disjointSet = new DisjointSet(canonicalPositions.Count);
        var referencedVertices = new HashSet<int>();
        Vector3 volumeOrigin = canonicalPositions[canonicalIndices[0]];
        double signedVolumeTimesSix = 0.0;
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            int i0 = canonicalIndices[triangle];
            int i1 = canonicalIndices[triangle + 1];
            int i2 = canonicalIndices[triangle + 2];
            if (i0 == i1 || i1 == i2 || i2 == i0)
            {
                reason = "caustic-topology-degenerate-welded-triangle";
                return false;
            }

            Vector3 p0 = canonicalPositions[i0];
            Vector3 p1 = canonicalPositions[i1];
            Vector3 p2 = canonicalPositions[i2];
            Double3 e1 = Double3.Subtract(p1, p0);
            Double3 e2 = Double3.Subtract(p2, p0);
            Double3 cross = Double3.Cross(e1, e2);
            double areaSquared = cross.LengthSquared;
            if (!double.IsFinite(areaSquared) || areaSquared <= faceAreaSquaredEpsilon)
            {
                reason = "caustic-topology-degenerate-triangle";
                return false;
            }

            AddEdge(edges, i0, i1);
            AddEdge(edges, i1, i2);
            AddEdge(edges, i2, i0);
            disjointSet.Union(i0, i1);
            disjointSet.Union(i1, i2);
            referencedVertices.Add(i0);
            referencedVertices.Add(i1);
            referencedVertices.Add(i2);

            Double3 relative0 = Double3.Subtract(p0, volumeOrigin);
            Double3 relative1 = Double3.Subtract(p1, volumeOrigin);
            Double3 relative2 = Double3.Subtract(p2, volumeOrigin);
            signedVolumeTimesSix += Double3.Dot(
                relative0,
                Double3.Cross(relative1, relative2));
        }

        int boundaryEdges = 0;
        int nonManifoldEdges = 0;
        int inconsistentWindingEdges = 0;
        foreach (EdgeUse edge in edges.Values)
        {
            if (edge.Count == 1)
                boundaryEdges++;
            else if (edge.Count != 2)
                nonManifoldEdges++;
            if (edge.Count >= 2 && edge.OrientationBalance != 0)
                inconsistentWindingEdges++;
        }

        var components = new HashSet<int>();
        foreach (int vertex in referencedVertices)
            components.Add(disjointSet.Find(vertex));
        double signedVolume = signedVolumeTimesSix / 6.0;
        bool closed = boundaryEdges == 0 && nonManifoldEdges == 0;
        bool positiveOrientation = !closed || signedVolume > volumeEpsilon;
        bool consistentWinding = nonManifoldEdges == 0 &&
            inconsistentWindingEdges == 0 && positiveOrientation;
        var facts = new ModelGiCausticHeroGeometryFacts(
            IsStaticOrCurrentPoseQualified: !isSkinned,
            IsClosedManifold: closed,
            HasConsistentWinding: consistentWinding,
            HasGeometricNormals: true,
            HasUnsupportedNestedMedium: components.Count != 1);

        if (hash == 0UL)
            hash = 1UL;
        evidence = new ModelGiCausticHeroTopologyEvidence(
            CurrentSchemaVersion,
            CurrentAlgorithmVersion,
            facts,
            positions.Length,
            canonicalPositions.Count,
            indices.Length / 3,
            boundaryEdges,
            nonManifoldEdges,
            inconsistentWindingEdges,
            components.Count,
            positiveOrientation,
            signedVolume,
            hash);
        reason = string.Empty;
        return evidence.IsStructurallyValid;
    }

    public static bool Matches(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<uint> indices,
        bool isSkinned,
        in ModelGiCausticHeroTopologyEvidence expected,
        out string reason)
    {
        if (!expected.IsStructurallyValid)
        {
            reason = "caustic-topology-evidence-structure-invalid";
            return false;
        }
        if (!TryAnalyze(positions, indices, isSkinned, out var actual, out reason))
            return false;
        if (actual != expected)
        {
            reason = "caustic-topology-evidence-does-not-match-mesh";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static void AddEdge(Dictionary<EdgeKey, EdgeUse> edges, int from, int to)
    {
        int minimum = Math.Min(from, to);
        int maximum = Math.Max(from, to);
        var key = new EdgeKey(minimum, maximum);
        int orientation = from == minimum ? 1 : -1;
        edges.TryGetValue(key, out EdgeUse use);
        edges[key] = new EdgeUse(use.Count + 1,
            use.OrientationBalance + orientation);
    }

    private static uint CanonicalBits(float value) =>
        value == 0.0f ? 0u : BitConverter.SingleToUInt32Bits(value);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static ulong AddHash(ulong hash, uint value)
    {
        hash ^= value;
        return hash * HashPrime;
    }

    private readonly record struct PositionKey(uint X, uint Y, uint Z);
    private readonly record struct EdgeKey(int Minimum, int Maximum);
    private readonly record struct EdgeUse(int Count, int OrientationBalance);

    private readonly record struct Double3(double X, double Y, double Z)
    {
        public double LengthSquared => X * X + Y * Y + Z * Z;

        public static Double3 Subtract(Vector3 left, Vector3 right) =>
            new((double)left.X - right.X, (double)left.Y - right.Y,
                (double)left.Z - right.Z);

        public static Double3 Cross(Double3 left, Double3 right) => new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);

        public static double Dot(Double3 left, Double3 right) =>
            left.X * right.X + left.Y * right.Y + left.Z * right.Z;
    }

    private sealed class DisjointSet
    {
        private readonly int[] _parent;
        private readonly byte[] _rank;

        public DisjointSet(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (int index = 0; index < count; index++)
                _parent[index] = index;
        }

        public int Find(int value)
        {
            int root = value;
            while (_parent[root] != root)
                root = _parent[root];
            while (_parent[value] != value)
            {
                int next = _parent[value];
                _parent[value] = root;
                value = next;
            }
            return root;
        }

        public void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot == rightRoot)
                return;
            if (_rank[leftRoot] < _rank[rightRoot])
                _parent[leftRoot] = rightRoot;
            else if (_rank[leftRoot] > _rank[rightRoot])
                _parent[rightRoot] = leftRoot;
            else
            {
                _parent[rightRoot] = leftRoot;
                _rank[leftRoot]++;
            }
        }
    }
}
