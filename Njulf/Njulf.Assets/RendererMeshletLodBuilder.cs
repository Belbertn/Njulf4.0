using Njulf.Assets.Cooked;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Math;
using System.Runtime.InteropServices;

namespace Njulf.Assets;

public sealed record RendererMeshletLodBuild(
    Meshlet[] Meshlets,
    uint[] MeshletVertices,
    uint[] MeshletTriangles,
    IReadOnlyList<ProcessedMeshLodRange> Ranges,
    IReadOnlyList<int> IndexCounts,
    IReadOnlyList<float> SimplificationErrors,
    MeshletHierarchyNode[] HierarchyNodes,
    int HierarchyRootNode);

/// <summary>
/// Appearance streams used by the renderer LOD simplifier. Joint data is used
/// only to protect discontinuities; joint indices and weights are deliberately
/// never interpreted as continuous simplification attributes.
/// </summary>
public sealed record RendererMeshletLodAttributeStreams(
    Vector3[]? Normals = null,
    Vector3[]? Tangents = null,
    Vector2[]? TexCoords0 = null,
    Vector2[]? TexCoords1 = null,
    Vector4[]? VertexColors = null,
    VertexJointIndices[]? JointIndices0 = null,
    VertexJointWeights[]? JointWeights0 = null);

/// <summary>Builds the renderer's three deterministic, progressively simplified meshlet LODs.</summary>
public sealed class RendererMeshletLodBuilder
{
    public const int MaxVerticesPerMeshlet = 48;
    public const int MaxTrianglesPerMeshlet = 64;
    // Very small closed meshes, such as the validation cubes, cannot be reduced
    // to the 20% far-LOD target without losing faces altogether. Keep their
    // complete topology in each LOD slot; this is both visually stable and
    // negligible in the meshlet budget.
    private const int MinimumTriangleCountForSimplification = 24;
    private static readonly float[] Ratios = [1f, 0.5f, 0.2f];
    private static readonly float[] Errors = [0f, 0.01f, 0.03f];
    private static readonly float[] Thresholds = [1f, 0.35f, 0.12f];
    public const int HierarchyLeafPartitionMeshletCount = 16;
    public const int HierarchyFanout = 8;
    public const int HierarchyMaximumDepth = 12;
    public const float HierarchySimplificationRatio = 0.5f;
    public const float HierarchyStuckRatio = 0.85f;
    private readonly MeshletBuilder _meshletBuilder;

    public RendererMeshletLodBuilder()
        : this(RendererMeshletBuildProfiles.Production)
    {
    }

    public RendererMeshletLodBuilder(RendererMeshletBuildProfile profile)
        : this((profile ?? throw new ArgumentNullException(nameof(profile)))
            .CreateBuilder())
    {
    }

    public RendererMeshletLodBuilder(MeshletBuilder meshletBuilder) =>
        _meshletBuilder = meshletBuilder ?? throw new ArgumentNullException(nameof(meshletBuilder));

    public RendererMeshletLodBuild Build(Vector3[] vertices, uint[] indices, string? name = null)
        => Build(vertices, indices, attributes: null, name: name);

    public RendererMeshletLodBuild Build(ModelSubMesh subMesh)
    {
        ArgumentNullException.ThrowIfNull(subMesh);
        return Build(
            subMesh.Vertices,
            subMesh.Indices,
            new RendererMeshletLodAttributeStreams(
                subMesh.Normals,
                subMesh.Tangents,
                subMesh.TexCoords,
                subMesh.TexCoords1,
                subMesh.VertexColors,
                subMesh.JointIndices0,
                subMesh.JointWeights0),
            subMesh.Name);
    }

    public RendererMeshletLodBuild Build(
        Vector3[] vertices,
        uint[] indices,
        RendererMeshletLodAttributeStreams? attributes,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("LOD generation requires a non-empty indexed triangle mesh.");

        PreparedSimplificationAttributes preparedAttributes =
            PrepareAttributes(vertices, attributes);
        float simplificationScale = MeshOptimizerCodec.SimplifyScale(vertices);

        var meshlets = new List<Meshlet>();
        var meshletVertices = new List<uint>();
        var meshletTriangles = new List<uint>();
        var ranges = new List<ProcessedMeshLodRange>(3);
        var indexCounts = new List<int>(3);
        var simplificationErrors = new List<float>(3);
        uint[] previous = indices;
        float previousError = 0f;
        bool canSimplify = indices.Length / 3 > MinimumTriangleCountForSimplification;

        for (int level = 0; level < 3; level++)
        {
            float resultError = 0f;
            uint[] lodIndices;
            if (level == 0)
            {
                lodIndices = indices;
            }
            else if (canSimplify)
            {
                int target = Math.Max(3, (int)Math.Floor(indices.Length * Ratios[level] / 3f) * 3);
                float absoluteErrorLimit = simplificationScale * Errors[level];
                MeshOptimizerSimplificationOptions options =
                    MeshOptimizerSimplificationOptions.LockBorder |
                    MeshOptimizerSimplificationOptions.ErrorAbsolute;
                lodIndices = preparedAttributes.AttributeCount > 0
                    ? MeshOptimizerCodec.SimplifyWithAttributes(
                        indices,
                        vertices,
                        preparedAttributes.Values,
                        preparedAttributes.AttributeCount,
                        preparedAttributes.Weights,
                        preparedAttributes.VertexLocks,
                        target,
                        absoluteErrorLimit,
                        options,
                        out resultError)
                    : MeshOptimizerCodec.Simplify(
                        indices,
                        vertices,
                        target,
                        absoluteErrorLimit,
                        options,
                        out resultError);
                if (!float.IsFinite(resultError) || resultError < 0f)
                    throw new InvalidOperationException($"meshoptimizer returned invalid LOD{level} error {resultError}.");
                if (lodIndices.Length >= previous.Length)
                {
                    // A simplifier is allowed to stop before its requested target when
                    // topology or the error budget prevents another safe collapse. Never
                    // manufacture a lower LOD by sampling arbitrary triangles: that opens
                    // literal holes in closed meshes and along independently cooked parts.
                    lodIndices = previous;
                    resultError = previousError;
                }
            }
            else
            {
                lodIndices = indices;
            }

            MeshletMesh built = _meshletBuilder.BuildMeshlets(vertices, lodIndices, name: $"{name ?? "Mesh"}.LOD{level}");
            int firstMeshlet = meshlets.Count;
            int vertexBase = meshletVertices.Count;
            int triangleBase = meshletTriangles.Count;
            foreach (Meshlet source in built.Meshlets)
            {
                Meshlet value = source;
                value.VertexOffset = 0;
                value.IndexOffset = 0;
                value.IndexCount = checked((uint)lodIndices.Length);
                value.LocalVertexOffset = checked((uint)vertexBase + source.LocalVertexOffset);
                value.LocalTriangleOffset = checked((uint)triangleBase + source.LocalTriangleOffset * 3u);
                meshlets.Add(value);
            }
            meshletVertices.AddRange(built.MeshletVertices);
            meshletTriangles.AddRange(built.MeshletTriangles);
            ranges.Add(new ProcessedMeshLodRange(
                level,
                firstMeshlet,
                built.Meshlets.Length,
                Thresholds[level],
                Math.Max(0f, resultError)));
            indexCounts.Add(lodIndices.Length);
            simplificationErrors.Add(resultError);
            previous = lodIndices;
            previousError = resultError;
        }

        ValidateProgression(ranges, indexCounts, meshlets.Count);
        HierarchyBuild hierarchy = BuildHierarchy(
            vertices,
            preparedAttributes,
            simplificationScale,
            meshlets,
            meshletVertices,
            meshletTriangles,
            ranges[0]);
        return new RendererMeshletLodBuild(
            meshlets.ToArray(),
            meshletVertices.ToArray(),
            meshletTriangles.ToArray(),
            ranges,
            indexCounts,
            simplificationErrors,
            hierarchy.Nodes,
            hierarchy.RootNode);
    }

    private HierarchyBuild BuildHierarchy(
        Vector3[] vertices,
        PreparedSimplificationAttributes attributes,
        float simplificationScale,
        List<Meshlet> meshlets,
        List<uint> meshletVertices,
        List<uint> meshletTriangles,
        ProcessedMeshLodRange lod0Range)
    {
        if (lod0Range.MeshletCount <= 0)
            return new HierarchyBuild([], -1);

        var nodes = new List<MeshletHierarchyNode>();
        var currentLevel = new List<HierarchyBuildNode>();
        int lod0End = checked(
            lod0Range.FirstMeshlet + lod0Range.MeshletCount);
        for (int first = lod0Range.FirstMeshlet;
             first < lod0End;
             first += HierarchyLeafPartitionMeshletCount)
        {
            int count = Math.Min(
                HierarchyLeafPartitionMeshletCount,
                lod0End - first);
            uint[] sourceIndices = ExtractSourceIndices(
                meshlets,
                meshletVertices,
                meshletTriangles,
                first,
                count);
            (Vector3 center, float radius) = CalculateRangeSphere(
                meshlets,
                first,
                count);
            int nodeIndex = nodes.Count;
            nodes.Add(new MeshletHierarchyNode
            {
                BoundingSphereCenter = center,
                BoundingSphereRadius = radius,
                GeometricError = 0f,
                FirstChild = uint.MaxValue,
                ChildCount = 0,
                MeshletOffset = checked((uint)first),
                MeshletCount = checked((uint)count),
                ParentIndex = uint.MaxValue,
                Depth = 0,
                Flags = MeshletHierarchyNodeFlags.Leaf
            });
            currentLevel.Add(new HierarchyBuildNode(
                nodeIndex,
                sourceIndices,
                sourceIndices.Length / 3));
        }

        int depth = 1;
        while (currentLevel.Count > 1 && depth <= HierarchyMaximumDepth)
        {
            var nextLevel = new List<HierarchyBuildNode>(
                (currentLevel.Count + HierarchyFanout - 1) /
                HierarchyFanout);
            for (int firstChild = 0;
                 firstChild < currentLevel.Count;
                 firstChild += HierarchyFanout)
            {
                int childCount = Math.Min(
                    HierarchyFanout,
                    currentLevel.Count - firstChild);
                ReadOnlySpan<HierarchyBuildNode> children =
                    CollectionsMarshal.AsSpan(currentLevel)
                        .Slice(firstChild, childCount);
                int firstChildNode = children[0].NodeIndex;
                for (int child = 1; child < children.Length; child++)
                {
                    if (children[child].NodeIndex != firstChildNode + child)
                    {
                        throw new InvalidOperationException(
                            "Meshlet hierarchy children must occupy a contiguous node range.");
                    }
                }

                uint[] sourceIndices = ConcatenateSourceIndices(children);
                int displayedChildTriangles = 0;
                float childError = 0f;
                (Vector3 center, float radius) sphere =
                    (nodes[firstChildNode].BoundingSphereCenter,
                     nodes[firstChildNode].BoundingSphereRadius);
                for (int child = 0; child < children.Length; child++)
                {
                    displayedChildTriangles = checked(
                        displayedChildTriangles +
                        children[child].DisplayedTriangleCount);
                    MeshletHierarchyNode childNode =
                        nodes[children[child].NodeIndex];
                    childError = MathF.Max(
                        childError,
                        childNode.GeometricError);
                    if (child != 0)
                    {
                        sphere = MergeSpheres(
                            sphere.center,
                            sphere.radius,
                            childNode.BoundingSphereCenter,
                            childNode.BoundingSphereRadius);
                    }
                }

                bool forceRefine = childCount == 1;
                uint parentMeshletOffset = 0;
                uint parentMeshletCount = 0;
                int displayedParentTriangles = displayedChildTriangles;
                float parentError = childError;
                if (!forceRefine)
                {
                    int targetIndexCount = Math.Max(
                        3,
                        (int)MathF.Floor(
                            displayedChildTriangles *
                            HierarchySimplificationRatio) * 3);
                    float absoluteErrorLimit = simplificationScale *
                        MathF.Min(0.01f * depth, 0.1f);
                    MeshOptimizerSimplificationOptions options =
                        MeshOptimizerSimplificationOptions.LockBorder |
                        MeshOptimizerSimplificationOptions.ErrorAbsolute;
                    uint[] simplified;
                    float resultError;
                    if (attributes.AttributeCount > 0)
                    {
                        simplified = MeshOptimizerCodec.SimplifyWithAttributes(
                            sourceIndices,
                            vertices,
                            attributes.Values,
                            attributes.AttributeCount,
                            attributes.Weights,
                            attributes.VertexLocks,
                            targetIndexCount,
                            absoluteErrorLimit,
                            options,
                            out resultError);
                    }
                    else
                    {
                        simplified = MeshOptimizerCodec.Simplify(
                            sourceIndices,
                            vertices,
                            targetIndexCount,
                            absoluteErrorLimit,
                            options,
                            out resultError);
                    }
                    forceRefine = !float.IsFinite(resultError) ||
                        resultError < 0f ||
                        simplified.Length >=
                        displayedChildTriangles * 3 *
                        HierarchyStuckRatio;
                    if (!forceRefine)
                    {
                        MeshletMesh built = _meshletBuilder.BuildMeshlets(
                            vertices,
                            simplified,
                            name: $"Hierarchy.Depth{depth}");
                        int vertexBase = meshletVertices.Count;
                        int triangleBase = meshletTriangles.Count;
                        parentMeshletOffset = checked((uint)meshlets.Count);
                        foreach (Meshlet source in built.Meshlets)
                        {
                            Meshlet value = source;
                            value.VertexOffset = 0;
                            value.IndexOffset = 0;
                            value.IndexCount = checked((uint)simplified.Length);
                            value.LocalVertexOffset = checked(
                                (uint)vertexBase +
                                source.LocalVertexOffset);
                            value.LocalTriangleOffset = checked(
                                (uint)triangleBase +
                                source.LocalTriangleOffset * 3u);
                            meshlets.Add(value);
                        }
                        meshletVertices.AddRange(built.MeshletVertices);
                        meshletTriangles.AddRange(built.MeshletTriangles);
                        parentMeshletCount = checked(
                            (uint)built.Meshlets.Length);
                        displayedParentTriangles = simplified.Length / 3;
                        parentError = MathF.Max(childError, resultError);
                    }
                }

                int parentIndex = nodes.Count;
                nodes.Add(new MeshletHierarchyNode
                {
                    BoundingSphereCenter = sphere.center,
                    BoundingSphereRadius = sphere.radius,
                    GeometricError = parentError,
                    FirstChild = checked((uint)firstChildNode),
                    ChildCount = checked((uint)childCount),
                    MeshletOffset = parentMeshletOffset,
                    MeshletCount = parentMeshletCount,
                    ParentIndex = uint.MaxValue,
                    Depth = checked((uint)depth),
                    Flags = forceRefine
                        ? MeshletHierarchyNodeFlags.ForceRefine
                        : MeshletHierarchyNodeFlags.None
                });
                for (int child = 0; child < children.Length; child++)
                {
                    int childNodeIndex = children[child].NodeIndex;
                    MeshletHierarchyNode childNode = nodes[childNodeIndex];
                    childNode.ParentIndex = checked((uint)parentIndex);
                    nodes[childNodeIndex] = childNode;
                }
                nextLevel.Add(new HierarchyBuildNode(
                    parentIndex,
                    sourceIndices,
                    displayedParentTriangles));
            }

            currentLevel = nextLevel;
            depth++;
        }

        if (currentLevel.Count != 1)
        {
            throw new InvalidOperationException(
                $"Meshlet hierarchy exceeded maximum depth {HierarchyMaximumDepth}.");
        }
        ValidateHierarchy(nodes, currentLevel[0].NodeIndex);
        return new HierarchyBuild(
            nodes.ToArray(),
            currentLevel[0].NodeIndex);
    }

    private static uint[] ExtractSourceIndices(
        IReadOnlyList<Meshlet> meshlets,
        IReadOnlyList<uint> meshletVertices,
        IReadOnlyList<uint> meshletTriangles,
        int firstMeshlet,
        int meshletCount)
    {
        int triangleCount = 0;
        for (int i = 0; i < meshletCount; i++)
            triangleCount = checked(
                triangleCount +
                (int)meshlets[firstMeshlet + i].LocalTriangleCount);
        var indices = new uint[checked(triangleCount * 3)];
        int destination = 0;
        for (int i = 0; i < meshletCount; i++)
        {
            Meshlet meshlet = meshlets[firstMeshlet + i];
            for (uint triangle = 0;
                 triangle < meshlet.LocalTriangleCount;
                 triangle++)
            {
                int triangleOffset = checked(
                    (int)meshlet.LocalTriangleOffset +
                    (int)triangle * 3);
                for (int corner = 0; corner < 3; corner++)
                {
                    uint localVertex = meshletTriangles[
                        triangleOffset + corner];
                    if (localVertex >= meshlet.LocalVertexCount)
                    {
                        throw new InvalidOperationException(
                            "Meshlet hierarchy source triangle references an invalid local vertex.");
                    }
                    indices[destination++] = meshletVertices[checked(
                        (int)meshlet.LocalVertexOffset +
                        (int)localVertex)];
                }
            }
        }
        return indices;
    }

    private static uint[] ConcatenateSourceIndices(
        ReadOnlySpan<HierarchyBuildNode> children)
    {
        int count = 0;
        foreach (HierarchyBuildNode child in children)
            count = checked(count + child.SourceIndices.Length);
        var result = new uint[count];
        int offset = 0;
        foreach (HierarchyBuildNode child in children)
        {
            child.SourceIndices.CopyTo(result, offset);
            offset += child.SourceIndices.Length;
        }
        return result;
    }

    private static (Vector3 center, float radius) CalculateRangeSphere(
        IReadOnlyList<Meshlet> meshlets,
        int firstMeshlet,
        int meshletCount)
    {
        Meshlet first = meshlets[firstMeshlet];
        (Vector3 center, float radius) sphere =
            (first.BoundingSphereCenter, first.BoundingSphereRadius);
        for (int i = 1; i < meshletCount; i++)
        {
            Meshlet next = meshlets[firstMeshlet + i];
            sphere = MergeSpheres(
                sphere.center,
                sphere.radius,
                next.BoundingSphereCenter,
                next.BoundingSphereRadius);
        }
        return sphere;
    }

    private static (Vector3 center, float radius) MergeSpheres(
        Vector3 leftCenter,
        float leftRadius,
        Vector3 rightCenter,
        float rightRadius)
    {
        Vector3 delta = rightCenter - leftCenter;
        float distance = delta.Length();
        if (leftRadius >= distance + rightRadius)
            return (leftCenter, leftRadius);
        if (rightRadius >= distance + leftRadius)
            return (rightCenter, rightRadius);
        if (distance <= 1e-20f)
            return (leftCenter, MathF.Max(leftRadius, rightRadius));

        float radius =
            (distance + leftRadius + rightRadius) * 0.5f;
        Vector3 center = leftCenter +
            delta * ((radius - leftRadius) / distance);
        return (center, radius);
    }

    private static void ValidateHierarchy(
        IReadOnlyList<MeshletHierarchyNode> nodes,
        int rootNode)
    {
        if ((uint)rootNode >= (uint)nodes.Count)
            throw new InvalidOperationException("Meshlet hierarchy root is invalid.");
        for (int i = 0; i < nodes.Count; i++)
        {
            MeshletHierarchyNode node = nodes[i];
            if (!float.IsFinite(node.BoundingSphereCenter.X) ||
                !float.IsFinite(node.BoundingSphereCenter.Y) ||
                !float.IsFinite(node.BoundingSphereCenter.Z) ||
                !float.IsFinite(node.BoundingSphereRadius) ||
                node.BoundingSphereRadius < 0f ||
                !float.IsFinite(node.GeometricError) ||
                node.GeometricError < 0f)
            {
                throw new InvalidOperationException(
                    $"Meshlet hierarchy node {i} contains invalid bounds or error.");
            }
            if (node.ChildCount > HierarchyFanout ||
                node.Depth > HierarchyMaximumDepth)
            {
                throw new InvalidOperationException(
                    $"Meshlet hierarchy node {i} exceeds fanout/depth limits.");
            }
            if (node.ChildCount != 0 &&
                (ulong)node.FirstChild + node.ChildCount >
                (ulong)nodes.Count)
            {
                throw new InvalidOperationException(
                    $"Meshlet hierarchy node {i} has an invalid child range.");
            }
        }
    }

    private readonly record struct HierarchyBuild(
        MeshletHierarchyNode[] Nodes,
        int RootNode);

    private readonly record struct HierarchyBuildNode(
        int NodeIndex,
        uint[] SourceIndices,
        int DisplayedTriangleCount);

    private static PreparedSimplificationAttributes PrepareAttributes(
        Vector3[] vertices,
        RendererMeshletLodAttributeStreams? source)
    {
        if (source is null)
            return PreparedSimplificationAttributes.Empty;

        int vertexCount = vertices.Length;
        Vector3[] normals = ValidateStream(source.Normals, vertexCount, nameof(source.Normals));
        Vector3[] tangents = ValidateStream(source.Tangents, vertexCount, nameof(source.Tangents));
        Vector2[] uv0 = ValidateStream(source.TexCoords0, vertexCount, nameof(source.TexCoords0));
        Vector2[] uv1 = ValidateStream(source.TexCoords1, vertexCount, nameof(source.TexCoords1));
        Vector4[] colors = ValidateStream(source.VertexColors, vertexCount, nameof(source.VertexColors));
        VertexJointIndices[] joints = ValidateStream(source.JointIndices0, vertexCount, nameof(source.JointIndices0));
        VertexJointWeights[] jointWeights = ValidateStream(source.JointWeights0, vertexCount, nameof(source.JointWeights0));
        if ((joints.Length == 0) != (jointWeights.Length == 0))
        {
            throw new ArgumentException(
                "Joint indices and weights must either both be absent or both contain one value per vertex.",
                nameof(source));
        }

        int attributeCount =
            (normals.Length == 0 ? 0 : 3) +
            (tangents.Length == 0 ? 0 : 3) +
            (uv0.Length == 0 ? 0 : 2) +
            (uv1.Length == 0 ? 0 : 2) +
            (colors.Length == 0 ? 0 : 4);
        byte[] locks = BuildDiscontinuityLocks(
            vertices,
            normals,
            tangents,
            uv0,
            uv1,
            colors,
            joints,
            jointWeights);
        bool hasLocks = Array.IndexOf(locks, (byte)1) >= 0;
        if (attributeCount == 0 && !hasLocks)
            return PreparedSimplificationAttributes.Empty;

        // The attribute overload is also the only simplifier entry point that
        // accepts explicit vertex locks. A zero-weight sentinel keeps lock-only
        // skinned inputs valid without inventing a continuous skinning metric.
        if (attributeCount == 0)
            attributeCount = 1;

        var values = new float[checked(vertexCount * attributeCount)];
        var weights = new float[attributeCount];
        Vector2 uv0Min = FindUvMinimum(uv0);
        Vector2 uv0Extent = FindUvExtent(uv0, uv0Min);
        Vector2 uv1Min = FindUvMinimum(uv1);
        Vector2 uv1Extent = FindUvExtent(uv1, uv1Min);
        int component = 0;
        if (normals.Length != 0)
        {
            SetWeight(weights, component, 3, 0.5f);
            for (int i = 0; i < vertexCount; i++)
                Write3(values, attributeCount, i, component, normals[i].X, normals[i].Y, normals[i].Z);
            component += 3;
        }
        if (tangents.Length != 0)
        {
            SetWeight(weights, component, 3, 0.25f);
            for (int i = 0; i < vertexCount; i++)
                Write3(values, attributeCount, i, component, tangents[i].X, tangents[i].Y, tangents[i].Z);
            component += 3;
        }
        if (uv0.Length != 0)
        {
            SetWeight(weights, component, 2, 1f);
            for (int i = 0; i < vertexCount; i++)
                Write2(values, attributeCount, i, component, NormalizeUv(uv0[i].X, uv0Min.X, uv0Extent.X), NormalizeUv(uv0[i].Y, uv0Min.Y, uv0Extent.Y));
            component += 2;
        }
        if (uv1.Length != 0)
        {
            SetWeight(weights, component, 2, 0.5f);
            for (int i = 0; i < vertexCount; i++)
                Write2(values, attributeCount, i, component, NormalizeUv(uv1[i].X, uv1Min.X, uv1Extent.X), NormalizeUv(uv1[i].Y, uv1Min.Y, uv1Extent.Y));
            component += 2;
        }
        if (colors.Length != 0)
        {
            SetWeight(weights, component, 4, 0.25f);
            for (int i = 0; i < vertexCount; i++)
                Write4(values, attributeCount, i, component, colors[i].X, colors[i].Y, colors[i].Z, colors[i].W);
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i]))
                throw new ArgumentException("LOD simplification attributes must contain finite values.", nameof(source));
        }
        return new PreparedSimplificationAttributes(values, weights, locks, attributeCount);
    }

    private static T[] ValidateStream<T>(T[]? stream, int vertexCount, string name)
    {
        if (stream is null || stream.Length == 0)
            return Array.Empty<T>();
        if (stream.Length != vertexCount)
            throw new ArgumentException($"{name} must be empty or match vertex count.", name);
        return stream;
    }

    private static byte[] BuildDiscontinuityLocks(
        Vector3[] positions,
        Vector3[] normals,
        Vector3[] tangents,
        Vector2[] uv0,
        Vector2[] uv1,
        Vector4[] colors,
        VertexJointIndices[] joints,
        VertexJointWeights[] jointWeights)
    {
        var groups = new Dictionary<Vector3, List<int>>(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            if (!groups.TryGetValue(positions[i], out List<int>? group))
                groups.Add(positions[i], group = []);
            group.Add(i);
        }

        var locks = new byte[positions.Length];
        foreach (List<int> group in groups.Values)
        {
            if (group.Count < 2)
                continue;
            int first = group[0];
            bool discontinuous = false;
            for (int i = 1; i < group.Count && !discontinuous; i++)
            {
                int candidate = group[i];
                discontinuous =
                    StreamDiffers(normals, first, candidate) ||
                    StreamDiffers(tangents, first, candidate) ||
                    StreamDiffers(uv0, first, candidate) ||
                    StreamDiffers(uv1, first, candidate) ||
                    StreamDiffers(colors, first, candidate) ||
                    !JointSetsEqual(joints, jointWeights, first, candidate);
            }
            if (!discontinuous)
                continue;
            foreach (int index in group)
                locks[index] = 1;
        }
        return locks;
    }

    private static bool StreamDiffers<T>(T[] stream, int left, int right)
        where T : struct, IEquatable<T> =>
        stream.Length != 0 && !stream[left].Equals(stream[right]);

    private static bool JointSetsEqual(
        VertexJointIndices[] joints,
        VertexJointWeights[] weights,
        int left,
        int right)
    {
        if (joints.Length == 0)
            return true;
        Span<ushort> leftSet = stackalloc ushort[4];
        Span<ushort> rightSet = stackalloc ushort[4];
        int leftCount = CopyActiveJointSet(joints[left], weights[left], leftSet);
        int rightCount = CopyActiveJointSet(joints[right], weights[right], rightSet);
        leftSet[..leftCount].Sort();
        rightSet[..rightCount].Sort();
        return leftSet[..leftCount].SequenceEqual(rightSet[..rightCount]);
    }

    private static int CopyActiveJointSet(
        VertexJointIndices joints,
        VertexJointWeights weights,
        Span<ushort> destination)
    {
        int count = 0;
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] <= 1e-6f || destination[..count].Contains(joints[i]))
                continue;
            destination[count++] = joints[i];
        }
        return count;
    }

    private static Vector2 FindUvMinimum(Vector2[] stream)
    {
        if (stream.Length == 0)
            return Vector2.Zero;
        var minimum = new Vector2(float.PositiveInfinity);
        foreach (Vector2 value in stream)
        {
            minimum.X = MathF.Min(minimum.X, value.X);
            minimum.Y = MathF.Min(minimum.Y, value.Y);
        }
        return minimum;
    }

    private static Vector2 FindUvExtent(Vector2[] stream, Vector2 minimum)
    {
        if (stream.Length == 0)
            return Vector2.Zero;
        var maximum = new Vector2(float.NegativeInfinity);
        foreach (Vector2 value in stream)
        {
            maximum.X = MathF.Max(maximum.X, value.X);
            maximum.Y = MathF.Max(maximum.Y, value.Y);
        }
        return maximum - minimum;
    }

    private static float NormalizeUv(float value, float minimum, float extent) =>
        extent > 1e-20f ? (value - minimum) / extent : 0f;

    private static void SetWeight(float[] weights, int offset, int count, float value)
    {
        for (int i = 0; i < count; i++)
            weights[offset + i] = value;
    }

    private static void Write2(float[] target, int stride, int vertex, int component, float x, float y)
    {
        int destination = checked(vertex * stride + component);
        target[destination] = x;
        target[destination + 1] = y;
    }

    private static void Write3(float[] target, int stride, int vertex, int component, float x, float y, float z)
    {
        int destination = checked(vertex * stride + component);
        target[destination] = x;
        target[destination + 1] = y;
        target[destination + 2] = z;
    }

    private static void Write4(float[] target, int stride, int vertex, int component, float x, float y, float z, float w)
    {
        int destination = checked(vertex * stride + component);
        target[destination] = x;
        target[destination + 1] = y;
        target[destination + 2] = z;
        target[destination + 3] = w;
    }

    private readonly record struct PreparedSimplificationAttributes(
        float[] Values,
        float[] Weights,
        byte[] VertexLocks,
        int AttributeCount)
    {
        public static PreparedSimplificationAttributes Empty { get; } =
            new([], [], [], 0);
    }

    private static void ValidateProgression(IReadOnlyList<ProcessedMeshLodRange> ranges, IReadOnlyList<int> indexCounts, int meshletCount)
    {
        if (ranges.Count != 3 || indexCounts.Count != 3)
            throw new InvalidOperationException("Renderer meshlet LOD generation must produce exactly three levels.");
        for (int i = 0; i < 3; i++)
        {
            ProcessedMeshLodRange range = ranges[i];
            if (range.Level != i || range.MeshletCount <= 0 || range.FirstMeshlet < 0 || range.FirstMeshlet + range.MeshletCount > meshletCount)
                throw new InvalidOperationException($"Generated meshlet LOD{i} range is invalid.");
            if (i > 0 && indexCounts[i] > indexCounts[i - 1])
                throw new InvalidOperationException($"Generated meshlet LOD{i} increased triangle work.");
        }
    }
}
