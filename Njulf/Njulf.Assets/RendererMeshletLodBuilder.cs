using Njulf.Assets.Cooked;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets;

public sealed record RendererMeshletLodBuild(
    Meshlet[] Meshlets,
    uint[] MeshletVertices,
    uint[] MeshletTriangles,
    IReadOnlyList<ProcessedMeshLodRange> Ranges,
    IReadOnlyList<int> IndexCounts,
    IReadOnlyList<float> SimplificationErrors);

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
    private readonly MeshletBuilder _meshletBuilder;

    public RendererMeshletLodBuilder()
        : this(new MeshletBuilder(MaxVerticesPerMeshlet, MaxTrianglesPerMeshlet))
    {
    }

    public RendererMeshletLodBuilder(MeshletBuilder meshletBuilder) =>
        _meshletBuilder = meshletBuilder ?? throw new ArgumentNullException(nameof(meshletBuilder));

    public RendererMeshletLodBuild Build(Vector3[] vertices, uint[] indices, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        if (vertices.Length == 0 || indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("LOD generation requires a non-empty indexed triangle mesh.");

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
                lodIndices = MeshOptimizerCodec.Simplify(
                    indices,
                    vertices,
                    target,
                    Errors[level],
                    MeshOptimizerSimplificationOptions.LockBorder,
                    out resultError);
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
            ranges.Add(new ProcessedMeshLodRange(level, firstMeshlet, built.Meshlets.Length, Thresholds[level]));
            indexCounts.Add(lodIndices.Length);
            simplificationErrors.Add(resultError);
            previous = lodIndices;
            previousError = resultError;
        }

        ValidateProgression(ranges, indexCounts, meshlets.Count);
        return new RendererMeshletLodBuild(meshlets.ToArray(), meshletVertices.ToArray(), meshletTriangles.ToArray(), ranges, indexCounts, simplificationErrors);
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
