using System;
using System.Collections.Generic;
using System.Numerics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

[Flags]
public enum DdgiLightTreeNodeFlags : ushort
{
    None = 0,
    Leaf = 1 << 0,
    ContainsMalformedRange = 1 << 1,
    ContainsSpotLight = 1 << 2,
    InvalidBound = 1 << 3,
    ContainsAreaLight = 1 << 4
}

public enum DdgiLightTreeBuildAction : uint
{
    BypassEmpty = 0,
    BuildInactive = 1,
    RefitInactive = 2,
    ReusePublished = 3,
    KeepPreviousComplete = 4,
    PublishEmpty = 5
}

public enum DdgiLightTreeRebuildReason : uint
{
    None = 0,
    EmptyLocalSet = 1,
    FirstPublication = 2,
    LeafMembershipChanged = 3,
    MortonOrderChanged = 4,
    RootExtentChanged = 5,
    MaximumRefitAge = 6,
    InvalidBound = 7,
    RevisionChangedDuringBuild = 8,
    AllocationFailed = 9,
    PublicationValidationFailed = 10
}

public readonly record struct DdgiLocalLightTreeInput(
    int PackedLightIndex,
    uint StableLightIdentity,
    ulong LightBufferRevision,
    Vector3 Position,
    Vector3 Color,
    float Intensity,
    float Range,
    Vector3 Direction,
    float SpotAngle,
    LightType Type,
    Vector3 Up = default,
    Vector2 Size = default,
    bool TwoSided = false)
{
    public static DdgiLocalLightTreeInput FromLight(
        int packedLightIndex,
        uint stableLightIdentity,
        ulong lightBufferRevision,
        in Light light) => new(
            packedLightIndex,
            stableLightIdentity,
            lightBufferRevision,
            light.Position,
            light.Color,
            light.Intensity,
            light.Range,
            light.Direction,
            light.SpotAngle,
            light.Type,
            light.Up,
            light.Size,
            light.TwoSided);
}

public readonly record struct DdgiLightTreeSample(
    bool HasSample,
    int PackedLightIndex,
    uint StableLightIdentity,
    float Pdf,
    bool UsedUniformComponent,
    bool RepairedInvalidBound,
    int LeafOrdinal)
{
    public static DdgiLightTreeSample None { get; } = new(
        false,
        -1,
        0,
        0f,
        false,
        false,
        -1);
}

public readonly record struct DdgiLightTreeReferenceDiagnostics(
    int LocalLightCount,
    int NodeCount,
    int MaximumDepth,
    int InvalidBoundCount,
    ulong StableOrderHash,
    Vector3 RootMinimum,
    Vector3 RootMaximum);

/// <summary>
/// Deterministic CPU oracle for the GPU local-light hierarchy and point-
/// dependent proposal. It is used by validation, captures, and unit tests; the
/// production trace consumes the same node/leaf ABI.
/// </summary>
public sealed class SimpleDdgiLightTreeReference
{
    private const float MalformedRangeExtent = 1_000_000f;
    private const float MinimumFiniteFlux = 1e-20f;

    private readonly Leaf[] _leaves;
    private readonly Node[] _nodes;
    private readonly int[] _leafNodeByOrdinal;
    private readonly int _root;

    private SimpleDdgiLightTreeReference(
        Leaf[] leaves,
        Node[] nodes,
        int[] leafNodeByOrdinal,
        int root,
        DdgiLightTreeReferenceDiagnostics diagnostics)
    {
        _leaves = leaves;
        _nodes = nodes;
        _leafNodeByOrdinal = leafNodeByOrdinal;
        _root = root;
        Diagnostics = diagnostics;
    }

    public DdgiLightTreeReferenceDiagnostics Diagnostics { get; }
    public int LocalLightCount => _leaves.Length;
    public bool IsEmpty => _root < 0;

    public static SimpleDdgiLightTreeReference Build(
        ReadOnlySpan<DdgiLocalLightTreeInput> inputs)
    {
        var leaves = new List<Leaf>(inputs.Length);
        int invalidBounds = 0;
        for (int index = 0; index < inputs.Length; index++)
        {
            ref readonly DdgiLocalLightTreeInput input = ref inputs[index];
            if (input.Type == LightType.Directional)
                continue;

            bool finitePosition = IsFinite(input.Position);
            var light = new Light
            {
                Type = input.Type,
                Position = input.Position,
                Color = input.Color,
                Intensity = input.Intensity,
                Range = input.Range,
                Direction = input.Direction,
                SpotAngle = input.SpotAngle,
                Up = input.Up,
                Size = input.Size,
                TwoSided = input.TwoSided
            };
            bool validAreaGeometry = !AnalyticalLightGeometry.IsArea(input.Type) ||
                (AnalyticalLightGeometry.HasValidDimensions(light) &&
                 AnalyticalLightGeometry.TryGetFrame(light, out _, out _, out _));
            float flux = AnalyticalLightGeometry.ComputePowerWeight(light);
            if (!(flux > MinimumFiniteFlux) || !finitePosition ||
                !validAreaGeometry)
                continue;

            bool malformedRange = !float.IsFinite(input.Range) || input.Range <= 0f;
            Vector3 minimum = default;
            Vector3 maximum = default;
            bool hasBounds = !malformedRange &&
                AnalyticalLightGeometry.TryGetInfluenceBounds(
                    light,
                    out minimum,
                    out maximum);
            if (!hasBounds)
            {
                Vector3 fallbackExtent = new(MalformedRangeExtent);
                minimum = input.Position - fallbackExtent;
                maximum = input.Position + fallbackExtent;
                malformedRange = true;
            }
            bool invalidBound = !IsFinite(minimum) || !IsFinite(maximum);
            if (invalidBound)
            {
                invalidBounds++;
                minimum = new Vector3(-MalformedRangeExtent);
                maximum = new Vector3(MalformedRangeExtent);
                malformedRange = true;
            }

            leaves.Add(new Leaf(
                input,
                minimum,
                maximum,
                flux,
                malformedRange,
                invalidBound,
                0));
        }

        if (leaves.Count == 0)
        {
            return new SimpleDdgiLightTreeReference(
                Array.Empty<Leaf>(),
                Array.Empty<Node>(),
                Array.Empty<int>(),
                -1,
                new DdgiLightTreeReferenceDiagnostics(
                    0,
                    0,
                    0,
                    invalidBounds,
                    0,
                    Vector3.Zero,
                    Vector3.Zero));
        }

        Vector3 rootMinimum = new(float.PositiveInfinity);
        Vector3 rootMaximum = new(float.NegativeInfinity);
        foreach (Leaf leaf in leaves)
        {
            rootMinimum = Vector3.Min(rootMinimum, leaf.Minimum);
            rootMaximum = Vector3.Max(rootMaximum, leaf.Maximum);
        }

        for (int index = 0; index < leaves.Count; index++)
        {
            Leaf leaf = leaves[index];
            uint morton = EncodeMorton(leaf.Input.Position, rootMinimum, rootMaximum);
            leaves[index] = leaf with { MortonKey = morton };
        }

        leaves.Sort(static (left, right) =>
        {
            int morton = left.MortonKey.CompareTo(right.MortonKey);
            if (morton != 0)
                return morton;
            int identity = left.Input.StableLightIdentity.CompareTo(
                right.Input.StableLightIdentity);
            return identity != 0
                ? identity
                : left.Input.PackedLightIndex.CompareTo(right.Input.PackedLightIndex);
        });

        Leaf[] orderedLeaves = leaves.ToArray();
        var nodes = new List<Node>(checked(orderedLeaves.Length * 2 - 1));
        int[] leafNodes = new int[orderedLeaves.Length];
        int maximumDepth = 0;
        int root = BuildNode(0, orderedLeaves.Length, parent: -1, depth: 0);
        ulong orderHash = 14695981039346656037UL;
        foreach (Leaf leaf in orderedLeaves)
        {
            orderHash ^= leaf.Input.StableLightIdentity;
            orderHash *= 1099511628211UL;
        }

        return new SimpleDdgiLightTreeReference(
            orderedLeaves,
            nodes.ToArray(),
            leafNodes,
            root,
            new DdgiLightTreeReferenceDiagnostics(
                orderedLeaves.Length,
                nodes.Count,
                maximumDepth,
                invalidBounds,
                orderHash,
                rootMinimum,
                rootMaximum));

        int BuildNode(int first, int count, int parent, int depth)
        {
            maximumDepth = Math.Max(maximumDepth, depth);
            int nodeIndex = nodes.Count;
            nodes.Add(default);
            if (count == 1)
            {
                Leaf leaf = orderedLeaves[first];
                leafNodes[first] = nodeIndex;
                nodes[nodeIndex] = new Node(
                    leaf.Minimum,
                    leaf.Maximum,
                    leaf.Flux,
                    first,
                    -1,
                    parent,
                    first,
                    1,
                    leaf.MalformedRange,
                    leaf.InvalidBound,
                    leaf.Input.Type == LightType.Spot,
                    AnalyticalLightGeometry.IsArea(leaf.Input.Type),
                    depth);
                return nodeIndex;
            }

            int leftCount = count / 2;
            int rightCount = count - leftCount;
            int left = BuildNode(first, leftCount, nodeIndex, depth + 1);
            int right = BuildNode(first + leftCount, rightCount, nodeIndex, depth + 1);
            Node leftNode = nodes[left];
            Node rightNode = nodes[right];
            nodes[nodeIndex] = new Node(
                Vector3.Min(leftNode.Minimum, rightNode.Minimum),
                Vector3.Max(leftNode.Maximum, rightNode.Maximum),
                leftNode.Flux + rightNode.Flux,
                left,
                right,
                parent,
                first,
                count,
                leftNode.MalformedRange || rightNode.MalformedRange,
                leftNode.InvalidBound || rightNode.InvalidBound,
                leftNode.ContainsSpotLight || rightNode.ContainsSpotLight,
                leftNode.ContainsAreaLight || rightNode.ContainsAreaLight,
                depth);
            return nodeIndex;
        }
    }

    public DdgiLightTreeSample Sample(
        Vector3 hitPosition,
        in DdgiStochasticIdentity identity,
        int sampleOrdinal,
        float uniformMixtureProbability)
    {
        if (IsEmpty || !IsFinite(hitPosition))
            return DdgiLightTreeSample.None;
        if (sampleOrdinal < 0)
            throw new ArgumentOutOfRangeException(nameof(sampleOrdinal));

        float mixture = Math.Clamp(uniformMixtureProbability, 0.001f, 0.25f);
        Span<int> eligible = _leaves.Length <= 1_024
            ? stackalloc int[_leaves.Length]
            : new int[_leaves.Length];
        int eligibleCount = CollectProposalLeaves(eligible);
        if (eligibleCount == 0)
            return DdgiLightTreeSample.None;

        bool invalidRoot = _nodes[_root].InvalidBound ||
            !float.IsFinite(_nodes[_root].Flux);
        float componentRandom = DecisionRandom(identity, sampleOrdinal, 0);
        bool useUniform = invalidRoot || componentRandom < mixture;
        int leafOrdinal;
        bool repaired = invalidRoot;
        if (useUniform)
        {
            float uniformRandom = DecisionRandom(identity, sampleOrdinal, 1);
            int selected = Math.Min(
                eligibleCount - 1,
                (int)(uniformRandom * eligibleCount));
            leafOrdinal = eligible[selected];
        }
        else
        {
            int nodeIndex = _root;
            int depth = 0;
            while (!_nodes[nodeIndex].IsLeaf)
            {
                Node node = _nodes[nodeIndex];
                float leftBound = ContributionBound(_nodes[node.Left], hitPosition);
                float rightBound = ContributionBound(_nodes[node.Right], hitPosition);
                if (!float.IsFinite(leftBound) || !float.IsFinite(rightBound) ||
                    leftBound < 0f || rightBound < 0f)
                {
                    repaired = true;
                    useUniform = true;
                    break;
                }

                float total = leftBound + rightBound;
                if (!(total > 0f))
                {
                    // Aggregate boxes can contain empty gaps. Falling back to
                    // the exact eligible set preserves support and PDF truth.
                    useUniform = true;
                    break;
                }

                float choose = DecisionRandom(identity, sampleOrdinal, depth + 2);
                nodeIndex = choose < leftBound / total ? node.Left : node.Right;
                depth++;
            }

            if (useUniform)
            {
                float uniformRandom = DecisionRandom(identity, sampleOrdinal, 0x100 + depth);
                int selected = Math.Min(
                    eligibleCount - 1,
                    (int)(uniformRandom * eligibleCount));
                leafOrdinal = eligible[selected];
            }
            else
            {
                leafOrdinal = _nodes[nodeIndex].FirstLeaf;
                // A conservative node proposal may select a zero term. It is
                // still a valid draw and retains the uniform-mixture PDF; the
                // estimator contribution itself is zero.
            }
        }

        float treePdf = invalidRoot ? 0f : ComputeTreePdf(leafOrdinal, hitPosition);
        float uniformPdf = 1f / eligibleCount;
        float pdf = invalidRoot
            ? uniformPdf
            : (1f - mixture) * treePdf + mixture * uniformPdf;
        if (!(pdf > 0f) || !float.IsFinite(pdf))
            return DdgiLightTreeSample.None;

        Leaf leaf = _leaves[leafOrdinal];
        return new DdgiLightTreeSample(
            true,
            leaf.Input.PackedLightIndex,
            leaf.Input.StableLightIdentity,
            pdf,
            useUniform,
            repaired,
            leafOrdinal);
    }

    public float ComputeTreePdf(int leafOrdinal, Vector3 hitPosition)
    {
        if ((uint)leafOrdinal >= (uint)_leaves.Length || !IsFinite(hitPosition))
            return 0f;

        int nodeIndex = _leafNodeByOrdinal[leafOrdinal];
        float probability = 1f;
        while (_nodes[nodeIndex].Parent >= 0)
        {
            int parentIndex = _nodes[nodeIndex].Parent;
            Node parent = _nodes[parentIndex];
            float leftBound = ContributionBound(_nodes[parent.Left], hitPosition);
            float rightBound = ContributionBound(_nodes[parent.Right], hitPosition);
            float total = leftBound + rightBound;
            if (!(total > 0f) || !float.IsFinite(total))
                return 0f;
            probability *= nodeIndex == parent.Left
                ? leftBound / total
                : rightBound / total;
            nodeIndex = parentIndex;
        }

        return float.IsFinite(probability) ? probability : 0f;
    }

    public GPUDdgiLightTreeNode[] CreateGpuNodes()
    {
        var result = new GPUDdgiLightTreeNode[_nodes.Length];
        for (int index = 0; index < _nodes.Length; index++)
        {
            Node node = _nodes[index];
            DdgiLightTreeNodeFlags flags = node.IsLeaf
                ? DdgiLightTreeNodeFlags.Leaf
                : DdgiLightTreeNodeFlags.None;
            if (node.InvalidBound)
                flags |= DdgiLightTreeNodeFlags.InvalidBound;
            if (node.MalformedRange)
                flags |= DdgiLightTreeNodeFlags.ContainsMalformedRange;
            if (node.ContainsSpotLight)
                flags |= DdgiLightTreeNodeFlags.ContainsSpotLight;
            if (node.ContainsAreaLight)
                flags |= DdgiLightTreeNodeFlags.ContainsAreaLight;
            uint checksum = ComputeNodeChecksum(node, flags) & 0xffffu;
            result[index] = new GPUDdgiLightTreeNode
            {
                BoundsMinimumAndFlux = new Vector4(node.Minimum, node.Flux),
                BoundsMaximumAndRange = new Vector4(node.Maximum, ResolveMaximumRange(node)),
                ConeAxisAndCosine = new Vector4(0f, 0f, 1f, -1f),
                LeftOrFirstLeaf = checked((uint)(node.IsLeaf ? node.FirstLeaf : node.Left)),
                RightOrLeafCount = checked((uint)(node.IsLeaf ? 1 : node.Right)),
                DescendantLeafCount = checked((uint)node.LeafCount),
                FlagsAndChecksum = (checksum << 16) | (uint)flags
            };
        }

        return result;
    }

    public GPUDdgiLightTreeLeaf[] CreateGpuLeaves()
    {
        var result = new GPUDdgiLightTreeLeaf[_leaves.Length];
        for (int index = 0; index < _leaves.Length; index++)
        {
            Leaf leaf = _leaves[index];
            result[index] = new GPUDdgiLightTreeLeaf
            {
                PackedLightIndex = checked((uint)leaf.Input.PackedLightIndex),
                StableLightIdentity = leaf.Input.StableLightIdentity,
                LightBufferRevisionLow = (uint)leaf.Input.LightBufferRevision,
                LightBufferRevisionHigh = (uint)(leaf.Input.LightBufferRevision >> 32),
                CenterAndRange = new Vector4(
                    leaf.Input.Position,
                    ResolveLeafRange(leaf))
            };
        }

        return result;
    }

    private int CollectProposalLeaves(Span<int> destination)
    {
        int count = 0;
        for (int index = 0; index < _leaves.Length; index++)
        {
            // The GPU's O(log N) sampler mixes against every represented leaf.
            // Range rejection happens after selection. This preserves support
            // without an O(N) eligible-leaf scan at every surface hit.
            if (_leaves[index].Flux > 0f)
                destination[count++] = index;
        }

        return count;
    }

    private static bool IsEligible(in Leaf leaf, Vector3 hitPosition)
    {
        if (!(leaf.Flux > 0f))
            return false;
        if (leaf.MalformedRange)
            return true;
        Vector3 delta = hitPosition - leaf.Input.Position;
        float range = ResolveLeafRange(leaf);
        return Vector3.Dot(delta, delta) <= range * range;
    }

    private static float ContributionBound(in Node node, Vector3 hitPosition)
    {
        if (node.InvalidBound)
            return float.NaN;
        Vector3 closest = Vector3.Clamp(hitPosition, node.Minimum, node.Maximum);
        float distanceSquared = Vector3.DistanceSquared(hitPosition, closest);
        if (!float.IsFinite(distanceSquared))
            return float.NaN;
        return MathF.Max(node.Flux, 0f) / (1f + distanceSquared);
    }

    private float ResolveMaximumRange(in Node node)
    {
        float maximum = 0f;
        for (int index = node.FirstLeaf;
             index < node.FirstLeaf + node.LeafCount;
             index++)
        {
            Leaf leaf = _leaves[index];
            if (leaf.MalformedRange)
                return -1f;
            maximum = MathF.Max(maximum, ResolveLeafRange(leaf));
        }
        return maximum;
    }

    private static float ResolveLeafRange(in Leaf leaf)
    {
        if (leaf.MalformedRange)
            return -1f;
        if (!AnalyticalLightGeometry.IsArea(leaf.Input.Type))
            return leaf.Input.Range;
        var light = new Light
        {
            Type = leaf.Input.Type,
            Range = leaf.Input.Range,
            Size = leaf.Input.Size
        };
        return AnalyticalLightGeometry.GetBoundingRadius(light);
    }

    private static float DecisionRandom(
        in DdgiStochasticIdentity identity,
        int sampleOrdinal,
        int dimension)
    {
        uint discriminator = unchecked(
            (uint)sampleOrdinal * 0x9E3779B9u ^
            (uint)dimension * 0x85EBCA6Bu);
        return (identity with
        {
            DecisionDomain = DdgiStochasticDecisionDomain.LocalLightTreeTraversal,
            PrimitiveIdentity = identity.PrimitiveIdentity ^ discriminator
        }).UnitFloat();
    }

    private static uint EncodeMorton(Vector3 position, Vector3 minimum, Vector3 maximum)
    {
        Vector3 extent = Vector3.Max(maximum - minimum, new Vector3(1e-6f));
        Vector3 normalized = Vector3.Clamp((position - minimum) / extent, Vector3.Zero, Vector3.One);
        uint x = (uint)Math.Clamp((int)(normalized.X * 1023f), 0, 1023);
        uint y = (uint)Math.Clamp((int)(normalized.Y * 1023f), 0, 1023);
        uint z = (uint)Math.Clamp((int)(normalized.Z * 1023f), 0, 1023);
        return ExpandBits(x) | (ExpandBits(y) << 1) | (ExpandBits(z) << 2);
    }

    private static uint ExpandBits(uint value)
    {
        value &= 0x3ffu;
        value = (value | value << 16) & 0x030000FFu;
        value = (value | value << 8) & 0x0300F00Fu;
        value = (value | value << 4) & 0x030C30C3u;
        value = (value | value << 2) & 0x09249249u;
        return value;
    }

    private static uint ComputeNodeChecksum(in Node node, DdgiLightTreeNodeFlags flags)
    {
        uint hash = 2166136261u;
        Add(BitConverter.SingleToUInt32Bits(node.Minimum.X));
        Add(BitConverter.SingleToUInt32Bits(node.Minimum.Y));
        Add(BitConverter.SingleToUInt32Bits(node.Minimum.Z));
        Add(BitConverter.SingleToUInt32Bits(node.Maximum.X));
        Add(BitConverter.SingleToUInt32Bits(node.Maximum.Y));
        Add(BitConverter.SingleToUInt32Bits(node.Maximum.Z));
        Add(BitConverter.SingleToUInt32Bits(node.Flux));
        Add(unchecked((uint)node.Left));
        Add(unchecked((uint)node.Right));
        Add((uint)node.LeafCount);
        Add((uint)flags);
        return hash;

        void Add(uint value)
        {
            hash ^= value;
            hash *= 16777619u;
        }
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static float Luminance(Vector3 color) =>
        Vector3.Dot(color, new Vector3(0.2126f, 0.7152f, 0.0722f));

    private readonly record struct Leaf(
        DdgiLocalLightTreeInput Input,
        Vector3 Minimum,
        Vector3 Maximum,
        float Flux,
        bool MalformedRange,
        bool InvalidBound,
        uint MortonKey);

    private readonly record struct Node(
        Vector3 Minimum,
        Vector3 Maximum,
        float Flux,
        int Left,
        int Right,
        int Parent,
        int FirstLeaf,
        int LeafCount,
        bool MalformedRange,
        bool InvalidBound,
        bool ContainsSpotLight,
        bool ContainsAreaLight,
        int Depth)
    {
        public bool IsLeaf => Right < 0;
    }
}

/// <summary>Pure publication decision used by both the CPU wrapper and tests.</summary>
public static class SimpleDdgiLightTreePublicationPolicy
{
    public static DdgiLightTreeBuildAction SelectAction(
        int localLightCount,
        ulong lightBufferRevision,
        ulong topologyRevision,
        ulong contentRevision,
        ulong publishedLightBufferRevision,
        ulong publishedTopologyRevision,
        ulong publishedContentRevision,
        int refitAge,
        int maximumRefitAge,
        bool previousPublicationValid)
    {
        if (localLightCount <= 0)
            return DdgiLightTreeBuildAction.BypassEmpty;
        if (!previousPublicationValid)
            return DdgiLightTreeBuildAction.BuildInactive;
        if (lightBufferRevision == publishedLightBufferRevision &&
            topologyRevision == publishedTopologyRevision &&
            contentRevision == publishedContentRevision)
        {
            return DdgiLightTreeBuildAction.ReusePublished;
        }
        if (topologyRevision != publishedTopologyRevision ||
            refitAge >= Math.Max(1, maximumRefitAge))
        {
            return DdgiLightTreeBuildAction.BuildInactive;
        }
        return DdgiLightTreeBuildAction.RefitInactive;
    }
}
