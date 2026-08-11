using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Deterministic complete-binary hierarchy over the selected emissive source
/// table. Nodes intentionally use the same 64-byte storage shape as an
/// emissive source so leaves and nodes can share one bindless buffer without
/// another descriptor or ABI-dependent address.
///
/// The hierarchy proposal is mixed with the existing global alias proposal.
/// Consequently every selected emitter retains support even when a spatial
/// importance bound is numerically tiny, while the exact mixed probability can
/// be reconstructed at the receiver.
/// </summary>
public sealed class DdgiEmissiveSpatialHierarchy
{
    public const float HierarchyTechniqueProbability = 0.875f;
    public const float ImportanceFloor = 1.0f / 1024.0f;

    private const uint NodeValid = 1u << 0;
    private const uint NodeContainsDoubleSided = 1u << 1;
    private const uint NodeConeUnbounded = 1u << 2;
    private const uint NodeHasCoverageApproximation = 1u << 3;

    private readonly GPUDdgiEmissiveSource[] _nodes;
    private readonly GPUDdgiEmissiveSource[] _previousSources;
    private readonly bool[] _dirtyNodes;
    private int _sourceCount;
    private int _leafCapacity;
    private int _nodeCount;
    private ulong _buildCount;
    private ulong _refitCount;
    private ulong _noWorkCount;
    private int _lastUpdatedNodeCount;

    public DdgiEmissiveSpatialHierarchy(int maximumSourceCount)
    {
        if (maximumSourceCount <= 0 ||
            maximumSourceCount > DdgiEmissiveTriangleTable.MaximumAliasEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourceCount));
        }

        int maximumNodeCount = GetNodeCapacity(maximumSourceCount);
        _nodes = new GPUDdgiEmissiveSource[maximumNodeCount];
        _previousSources = new GPUDdgiEmissiveSource[maximumSourceCount];
        _dirtyNodes = new bool[maximumNodeCount];
    }

    public int SourceCount => _sourceCount;
    public int NodeCount => _nodeCount;
    public ReadOnlySpan<GPUDdgiEmissiveSource> Nodes => _nodes.AsSpan(0, _nodeCount);
    public DdgiEmissiveHierarchyDiagnostics Diagnostics => new(
        _buildCount,
        _refitCount,
        _noWorkCount,
        _lastUpdatedNodeCount,
        _nodeCount);

    public static int GetNodeCapacity(int sourceCapacity)
    {
        if (sourceCapacity <= 0 ||
            sourceCapacity > DdgiEmissiveTriangleTable.MaximumAliasEntryCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceCapacity));
        }
        return checked(NextPowerOfTwo(sourceCapacity) * 2 - 1);
    }

    /// <summary>
    /// Builds a new topology when source cardinality changes and otherwise
    /// refits only changed leaves and their ancestors. The source span is
    /// stamped with the hierarchy capability bit before it is cached/uploaded.
    /// </summary>
    public void BuildOrRefit(Span<GPUDdgiEmissiveSource> sources)
    {
        if (sources.Length > _previousSources.Length)
            throw new ArgumentOutOfRangeException(nameof(sources));

        for (int i = 0; i < sources.Length; i++)
            sources[i] = WithHierarchyFlag(sources[i]);

        if (sources.IsEmpty)
        {
            Clear();
            return;
        }

        int leafCapacity = NextPowerOfTwo(sources.Length);
        int nodeCount = checked(leafCapacity * 2 - 1);
        bool topologyChanged =
            sources.Length != _sourceCount ||
            leafCapacity != _leafCapacity ||
            nodeCount != _nodeCount;

        if (topologyChanged)
        {
            Array.Clear(_nodes, 0, Math.Max(_nodeCount, nodeCount));
            _sourceCount = sources.Length;
            _leafCapacity = leafCapacity;
            _nodeCount = nodeCount;
            int leafBase = leafCapacity - 1;
            for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                _nodes[leafBase + sourceIndex] = BuildLeaf(sources[sourceIndex]);
            for (int nodeIndex = leafBase - 1; nodeIndex >= 0; nodeIndex--)
                _nodes[nodeIndex] = MergeNodes(_nodes[nodeIndex * 2 + 1], _nodes[nodeIndex * 2 + 2]);

            sources.CopyTo(_previousSources);
            _buildCount++;
            _lastUpdatedNodeCount = nodeCount;
            return;
        }

        Array.Clear(_dirtyNodes, 0, _nodeCount);
        int changedLeaves = 0;
        int existingLeafBase = _leafCapacity - 1;
        for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
        {
            if (SourcePayloadEquals(sources[sourceIndex], _previousSources[sourceIndex]))
                continue;

            int nodeIndex = existingLeafBase + sourceIndex;
            _nodes[nodeIndex] = BuildLeaf(sources[sourceIndex]);
            _dirtyNodes[nodeIndex] = true;
            changedLeaves++;
            while (nodeIndex > 0)
            {
                nodeIndex = (nodeIndex - 1) / 2;
                _dirtyNodes[nodeIndex] = true;
            }
        }

        if (changedLeaves == 0)
        {
            _noWorkCount++;
            _lastUpdatedNodeCount = 0;
            return;
        }

        int updatedNodes = changedLeaves;
        for (int nodeIndex = existingLeafBase - 1; nodeIndex >= 0; nodeIndex--)
        {
            if (!_dirtyNodes[nodeIndex])
                continue;

            _nodes[nodeIndex] = MergeNodes(_nodes[nodeIndex * 2 + 1], _nodes[nodeIndex * 2 + 2]);
            updatedNodes++;
        }

        sources.CopyTo(_previousSources);
        _refitCount++;
        _lastUpdatedNodeCount = updatedNodes;
    }

    public void Clear()
    {
        if (_nodeCount > 0)
            Array.Clear(_nodes, 0, _nodeCount);
        if (_sourceCount > 0)
            Array.Clear(_previousSources, 0, _sourceCount);
        _sourceCount = 0;
        _leafCapacity = 0;
        _nodeCount = 0;
        _lastUpdatedNodeCount = 0;
    }

    /// <summary>
    /// CPU oracle for the exact point-dependent hierarchy probability used by
    /// the shader. This excludes the global-alias mixture probability.
    /// </summary>
    public float EvaluateHierarchySelectionProbability(
        int sourceIndex,
        Vector3 receiverPosition,
        Vector3 receiverNormal)
    {
        if ((uint)sourceIndex >= (uint)_sourceCount || _nodeCount == 0)
            return 0.0f;

        receiverNormal = SafeNormalize(receiverNormal, new Vector3(0.0f, 1.0f, 0.0f));
        int nodeIndex = 0;
        int rangeStart = 0;
        int rangeSize = _leafCapacity;
        double probability = 1.0;
        while (rangeSize > 1)
        {
            int leftIndex = nodeIndex * 2 + 1;
            int rightIndex = leftIndex + 1;
            float leftWeight = EvaluateNodeImportance(_nodes[leftIndex], receiverPosition, receiverNormal);
            float rightWeight = EvaluateNodeImportance(_nodes[rightIndex], receiverPosition, receiverNormal);
            float totalWeight = leftWeight + rightWeight;
            if (!(totalWeight > 0.0f) || !float.IsFinite(totalWeight))
                return 0.0f;

            int half = rangeSize / 2;
            bool chooseLeft = sourceIndex < rangeStart + half;
            float branchProbability = chooseLeft
                ? leftWeight / totalWeight
                : rightWeight / totalWeight;
            probability *= Math.Clamp(branchProbability, 0.0f, 1.0f);
            if (!(probability > 0.0) || !double.IsFinite(probability))
                return 0.0f;

            if (chooseLeft)
            {
                nodeIndex = leftIndex;
            }
            else
            {
                nodeIndex = rightIndex;
                rangeStart += half;
            }
            rangeSize = half;
        }

        return (float)probability;
    }

    public float EvaluateMixedSelectionProbability(
        int sourceIndex,
        Vector3 receiverPosition,
        Vector3 receiverNormal,
        ReadOnlySpan<GPUDdgiEmissiveSource> sources)
    {
        if ((uint)sourceIndex >= (uint)_sourceCount || sources.Length < _sourceCount)
            return 0.0f;

        float globalProbability = Math.Max(
            sources[sourceIndex].RadianceSelectionProbability.W,
            0.0f);
        float hierarchyProbability = EvaluateHierarchySelectionProbability(
            sourceIndex,
            receiverPosition,
            receiverNormal);
        return (1.0f - HierarchyTechniqueProbability) * globalProbability +
               HierarchyTechniqueProbability * hierarchyProbability;
    }

    private static GPUDdgiEmissiveSource WithHierarchyFlag(GPUDdgiEmissiveSource source)
    {
        uint packed = BitConverter.SingleToUInt32Bits(source.Edge2AliasFlags.W);
        uint alias = packed & DdgiEmissiveTriangleTable.AliasIndexMask;
        var flags = (DdgiEmissiveSourceFlags)(packed >> DdgiEmissiveTriangleTable.FlagsShift);
        flags |= DdgiEmissiveSourceFlags.SpatialHierarchy;
        packed = alias | ((uint)flags << DdgiEmissiveTriangleTable.FlagsShift);
        source.Edge2AliasFlags.W = BitConverter.UInt32BitsToSingle(packed);
        return source;
    }

    private static GPUDdgiEmissiveSource BuildLeaf(GPUDdgiEmissiveSource source)
    {
        DdgiEmissiveSourceFlags sourceFlags = DdgiEmissiveTriangleTable.DecodeFlags(source);
        if ((sourceFlags & DdgiEmissiveSourceFlags.MacroEmitter) != 0)
            return BuildMacroLeaf(source, sourceFlags);

        Vector3 vertex0 = Xyz(source.Vertex0Area);
        Vector3 vertex1 = vertex0 + Xyz(source.Edge1AliasProbability);
        Vector3 vertex2 = vertex0 + Xyz(source.Edge2AliasFlags);
        Vector3 minimum = Vector3.Min(vertex0, Vector3.Min(vertex1, vertex2));
        Vector3 maximum = Vector3.Max(vertex0, Vector3.Max(vertex1, vertex2));
        Vector3 normal = SafeNormalize(
            Vector3.Cross(Xyz(source.Edge1AliasProbability), Xyz(source.Edge2AliasFlags)),
            new Vector3(0.0f, 1.0f, 0.0f));
        uint nodeFlags = NodeValid;
        float coneCosine = 1.0f;
        if ((sourceFlags & DdgiEmissiveSourceFlags.DoubleSided) != 0)
        {
            nodeFlags |= NodeContainsDoubleSided | NodeConeUnbounded;
            coneCosine = -1.0f;
        }
        if ((sourceFlags & DdgiEmissiveSourceFlags.AlphaCoverageApproximation) != 0)
            nodeFlags |= NodeHasCoverageApproximation;

        return PackNode(
            minimum,
            maximum,
            Math.Max(source.RadianceSelectionProbability.W, 0.0f),
            normal,
            coneCosine,
            nodeFlags,
            Vector3.Max(Xyz(source.RadianceSelectionProbability), Vector3.Zero));
    }

    private static GPUDdgiEmissiveSource BuildMacroLeaf(
        GPUDdgiEmissiveSource source,
        DdgiEmissiveSourceFlags sourceFlags)
    {
        Vector3 center = Xyz(source.Vertex0Area);
        Vector3 axis = SafeNormalize(Xyz(source.Edge1AliasProbability), Vector3.UnitY);
        float radius = Math.Max(source.Vertex0Area.W, 1e-4f);
        float axialExtent = Math.Max(source.Edge2AliasFlags.X, 0.0f);
        float secondaryExtent = Math.Max(source.Edge2AliasFlags.Y, 0.0f);
        var shape = (DdgiVfxMacroShape)(((uint)sourceFlags &
            (uint)DdgiEmissiveSourceFlags.MacroShapeMask) >> 8);

        Vector3 minimum;
        Vector3 maximum;
        if (shape is DdgiVfxMacroShape.Line or
            DdgiVfxMacroShape.Capsule or
            DdgiVfxMacroShape.Cone)
        {
            Vector3 endpoint = axis * axialExtent;
            Vector3 padding = new(Math.Max(radius, secondaryExtent));
            minimum = Vector3.Min(center - endpoint, center + endpoint) - padding;
            maximum = Vector3.Max(center - endpoint, center + endpoint) + padding;
        }
        else if (shape == DdgiVfxMacroShape.BoundedVolume)
        {
            Vector3 extent = new(radius, Math.Max(axialExtent, 1e-4f), Math.Max(secondaryExtent, 1e-4f));
            minimum = center - extent;
            maximum = center + extent;
        }
        else
        {
            Vector3 extent = new(Math.Max(radius, Math.Max(axialExtent, secondaryExtent)));
            minimum = center - extent;
            maximum = center + extent;
        }

        return PackNode(
            minimum,
            maximum,
            Math.Max(source.RadianceSelectionProbability.W, 0.0f),
            axis,
            -1.0f,
            NodeValid | NodeContainsDoubleSided | NodeConeUnbounded,
            Vector3.Max(Xyz(source.RadianceSelectionProbability), Vector3.Zero));
    }

    private static GPUDdgiEmissiveSource MergeNodes(
        GPUDdgiEmissiveSource left,
        GPUDdgiEmissiveSource right)
    {
        uint leftFlags = DecodeNodeFlags(left);
        uint rightFlags = DecodeNodeFlags(right);
        bool leftValid = (leftFlags & NodeValid) != 0;
        bool rightValid = (rightFlags & NodeValid) != 0;
        if (!leftValid)
            return rightValid ? right : default;
        if (!rightValid)
            return left;

        Vector3 minimum = Vector3.Min(Xyz(left.Vertex0Area), Xyz(right.Vertex0Area));
        Vector3 maximum = Vector3.Max(Xyz(left.Edge1AliasProbability), Xyz(right.Edge1AliasProbability));
        float leftPower = Math.Max(left.Vertex0Area.W, 0.0f);
        float rightPower = Math.Max(right.Vertex0Area.W, 0.0f);
        float power = leftPower + rightPower;
        uint flags = NodeValid |
                     ((leftFlags | rightFlags) & (NodeContainsDoubleSided | NodeHasCoverageApproximation));

        Vector3 leftAxis = SafeNormalize(Xyz(left.Edge2AliasFlags), new Vector3(0.0f, 1.0f, 0.0f));
        Vector3 rightAxis = SafeNormalize(Xyz(right.Edge2AliasFlags), leftAxis);
        Vector3 axis = SafeNormalize(leftAxis * leftPower + rightAxis * rightPower, leftAxis);
        float coneCosine;
        if ((flags & NodeContainsDoubleSided) != 0 ||
            (leftFlags & NodeConeUnbounded) != 0 ||
            (rightFlags & NodeConeUnbounded) != 0)
        {
            flags |= NodeConeUnbounded;
            coneCosine = -1.0f;
        }
        else
        {
            float leftAngle = MathF.Acos(Math.Clamp(left.Edge1AliasProbability.W, -1.0f, 1.0f));
            float rightAngle = MathF.Acos(Math.Clamp(right.Edge1AliasProbability.W, -1.0f, 1.0f));
            float leftOffset = MathF.Acos(Math.Clamp(Vector3.Dot(axis, leftAxis), -1.0f, 1.0f));
            float rightOffset = MathF.Acos(Math.Clamp(Vector3.Dot(axis, rightAxis), -1.0f, 1.0f));
            float halfAngle = Math.Max(leftOffset + leftAngle, rightOffset + rightAngle);
            if (halfAngle >= MathF.PI - 1e-5f)
            {
                flags |= NodeConeUnbounded;
                coneCosine = -1.0f;
            }
            else
            {
                coneCosine = MathF.Cos(halfAngle);
            }
        }

        return PackNode(
            minimum,
            maximum,
            power,
            axis,
            coneCosine,
            flags,
            Vector3.Max(Xyz(left.RadianceSelectionProbability), Xyz(right.RadianceSelectionProbability)));
    }

    private static GPUDdgiEmissiveSource PackNode(
        Vector3 minimum,
        Vector3 maximum,
        float power,
        Vector3 coneAxis,
        float coneCosine,
        uint flags,
        Vector3 radianceBound) => new()
    {
        Vertex0Area = new Vector4(minimum.X, minimum.Y, minimum.Z, power),
        Edge1AliasProbability = new Vector4(maximum.X, maximum.Y, maximum.Z, coneCosine),
        Edge2AliasFlags = new Vector4(
            coneAxis.X,
            coneAxis.Y,
            coneAxis.Z,
            BitConverter.UInt32BitsToSingle(flags)),
        RadianceSelectionProbability = new Vector4(
            radianceBound.X,
            radianceBound.Y,
            radianceBound.Z,
            0.0f)
    };

    private static float EvaluateNodeImportance(
        GPUDdgiEmissiveSource node,
        Vector3 receiverPosition,
        Vector3 receiverNormal)
    {
        uint flags = DecodeNodeFlags(node);
        float power = node.Vertex0Area.W;
        if ((flags & NodeValid) == 0 || !(power > 0.0f) || !float.IsFinite(power))
            return 0.0f;

        Vector3 minimum = Xyz(node.Vertex0Area);
        Vector3 maximum = Xyz(node.Edge1AliasProbability);
        Vector3 center = (minimum + maximum) * 0.5f;
        float radius = Math.Max((maximum - minimum).Length() * 0.5f, 1e-4f);
        Vector3 toCenter = center - receiverPosition;
        float centerDistance = toCenter.Length();
        float directionConeAngle;
        Vector3 centerDirection;
        if (!(centerDistance > radius) || !float.IsFinite(centerDistance))
        {
            directionConeAngle = MathF.PI;
            centerDirection = receiverNormal;
        }
        else
        {
            directionConeAngle = MathF.Asin(Math.Clamp(radius / centerDistance, 0.0f, 1.0f));
            centerDirection = toCenter / centerDistance;
        }

        float receiverBound = MaximumCosineWithinCone(
            receiverNormal,
            centerDirection,
            directionConeAngle);
        float sourceBound = 1.0f;
        if ((flags & (NodeContainsDoubleSided | NodeConeUnbounded)) == 0)
        {
            Vector3 sourceAxis = SafeNormalize(Xyz(node.Edge2AliasFlags), new Vector3(0.0f, 1.0f, 0.0f));
            float sourceConeAngle = MathF.Acos(Math.Clamp(node.Edge1AliasProbability.W, -1.0f, 1.0f));
            sourceBound = MaximumCosineWithinCone(
                sourceAxis,
                -centerDirection,
                Math.Min(sourceConeAngle + directionConeAngle, MathF.PI));
        }

        float distanceSquared = DistanceSquaredToAabb(receiverPosition, minimum, maximum);
        float scale = Math.Max(radius * radius * 1e-4f, 1e-6f);
        distanceSquared = Math.Max(distanceSquared, scale);
        float angularBound = Math.Max(receiverBound * sourceBound, ImportanceFloor);
        float importance = power * angularBound / distanceSquared;
        return float.IsFinite(importance) && importance > 0.0f ? importance : 0.0f;
    }

    private static float MaximumCosineWithinCone(
        Vector3 referenceAxis,
        Vector3 coneAxis,
        float coneHalfAngle)
    {
        float cosine = Math.Clamp(Vector3.Dot(
            SafeNormalize(referenceAxis, new Vector3(0.0f, 1.0f, 0.0f)),
            SafeNormalize(coneAxis, new Vector3(0.0f, 1.0f, 0.0f))), -1.0f, 1.0f);
        float angle = MathF.Acos(cosine);
        if (angle <= coneHalfAngle)
            return 1.0f;
        return Math.Max(MathF.Cos(Math.Min(angle - coneHalfAngle, MathF.PI)), 0.0f);
    }

    private static float DistanceSquaredToAabb(Vector3 point, Vector3 minimum, Vector3 maximum)
    {
        float dx = Math.Max(Math.Max(minimum.X - point.X, 0.0f), point.X - maximum.X);
        float dy = Math.Max(Math.Max(minimum.Y - point.Y, 0.0f), point.Y - maximum.Y);
        float dz = Math.Max(Math.Max(minimum.Z - point.Z, 0.0f), point.Z - maximum.Z);
        return dx * dx + dy * dy + dz * dz;
    }

    private static uint DecodeNodeFlags(GPUDdgiEmissiveSource node) =>
        BitConverter.SingleToUInt32Bits(node.Edge2AliasFlags.W);

    private static Vector3 Xyz(Vector4 value) => new(value.X, value.Y, value.Z);

    private static bool SourcePayloadEquals(
        GPUDdgiEmissiveSource left,
        GPUDdgiEmissiveSource right) =>
        left.Vertex0Area.Equals(right.Vertex0Area) &&
        left.Edge1AliasProbability.Equals(right.Edge1AliasProbability) &&
        left.Edge2AliasFlags.Equals(right.Edge2AliasFlags) &&
        left.RadianceSelectionProbability.Equals(right.RadianceSelectionProbability);

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-20f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static int NextPowerOfTwo(int value)
    {
        if (value <= 1)
            return 1;
        uint unsigned = checked((uint)(value - 1));
        unsigned |= unsigned >> 1;
        unsigned |= unsigned >> 2;
        unsigned |= unsigned >> 4;
        unsigned |= unsigned >> 8;
        unsigned |= unsigned >> 16;
        return checked((int)(unsigned + 1));
    }
}

public readonly record struct DdgiEmissiveHierarchyDiagnostics(
    ulong BuildCount,
    ulong RefitCount,
    ulong NoWorkCount,
    int LastUpdatedNodeCount,
    int NodeCount);
