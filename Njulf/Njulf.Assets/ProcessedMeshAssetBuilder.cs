using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Njulf.Core.Animation;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets;

public sealed class ProcessedMeshAssetBuilder
{
    private static readonly Regex LodSuffixPattern = new(@"(?:^|[_\-. ])LOD(?<level>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly MeshletBuilder _meshletBuilder;

    public ProcessedMeshAssetBuilder(MeshletBuilder? meshletBuilder = null)
    {
        _meshletBuilder = meshletBuilder ?? new MeshletBuilder();
    }

    public ProcessedMeshAsset Build(ModelMesh source, ProcessedMeshBuildOptions? options = null)
    {
        if (source == null)
            throw new ArgumentNullException(nameof(source));

        options ??= ProcessedMeshBuildOptions.Default;
        ValidateSourceMesh(source);

        IReadOnlyList<ModelSubMesh> sourceSubmeshes = source.SubMeshes.Count > 0
            ? source.SubMeshes
            : new[] { CreateWholeMeshSubmesh(source) };

        ProjectLodMetadata projectMetadata = ProjectLodMetadata.Load(options.ProjectMetadataPath);
        var vertices = new List<ProcessedMeshVertex>();
        var indices = new List<uint>();
        var processedMeshlets = new List<ProcessedMeshlet>();
        var meshletVertices = new List<uint>();
        var meshletTriangles = new List<uint>();
        var lods = new List<ProcessedMeshLod>();
        var submeshes = new List<ProcessedSubmesh>();
        var diagnostics = new List<ProcessedLodDiagnostic>();
        int materialSlotCount = Math.Max(1, source.Materials.Count);
        ProcessedMeshFlags flags = ClassifyFlags(source, sourceSubmeshes, options);

        if (options.LodMode == ProcessedMeshLodMode.Meshlet)
        {
            AppendMeshletLodPayloads(
                sourceSubmeshes,
                options,
                projectMetadata,
                vertices,
                indices,
                processedMeshlets,
                meshletVertices,
                meshletTriangles,
                lods,
                submeshes,
                diagnostics,
                materialSlotCount);
        }
        else
        {
            IReadOnlyList<ProcessedLodSource> lodSources = ResolveGeometryLodSources(sourceSubmeshes, options, projectMetadata);
            AppendGeometryLodPayloads(
                lodSources,
                vertices,
                indices,
                processedMeshlets,
                meshletVertices,
                meshletTriangles,
                lods,
                submeshes,
                diagnostics,
                materialSlotCount);
        }

        var asset = new ProcessedMeshAsset(
            options.AssetId ?? source.Name,
            options.SourcePath ?? source.Name,
            options.SourceContentHash ?? ProcessedMeshAssetValidator.ComputeSourceHash(CreateDeterministicSourceFingerprint(source)),
            ProcessedMeshVersion.Current,
            submeshes,
            lods,
            Enumerable.Range(0, materialSlotCount).ToArray(),
            flags,
            BuildFoliageMetadata(source, sourceSubmeshes, options, flags),
            BuildImpostorMetadata(source, options, flags),
            ContentBudgetStatus.Pass,
            vertices,
            indices,
            processedMeshlets,
            meshletVertices,
            meshletTriangles,
            diagnostics);

        ContentBudgetStatus budgetStatus = ProcessedMeshAssetValidator.EvaluateBudgets(asset, options.BudgetLimits);
        asset = asset with { BudgetStatus = budgetStatus };
        asset.Validate();
        return asset;
    }

    private void AppendGeometryLodPayloads(
        IReadOnlyList<ProcessedLodSource> lodSources,
        List<ProcessedMeshVertex> vertices,
        List<uint> indices,
        List<ProcessedMeshlet> processedMeshlets,
        List<uint> meshletVertices,
        List<uint> meshletTriangles,
        List<ProcessedMeshLod> lods,
        List<ProcessedSubmesh> submeshes,
        List<ProcessedLodDiagnostic> diagnostics,
        int materialSlotCount)
    {
        uint previousTriangleCount = uint.MaxValue;
        for (int lodIndex = 0; lodIndex < lodSources.Count; lodIndex++)
        {
            ProcessedLodSource lodSource = lodSources[lodIndex];
            uint vertexOffset = (uint)vertices.Count;
            uint indexOffset = (uint)indices.Count;
            uint meshletOffset = (uint)processedMeshlets.Count;

            AppendGeometryLodPayload(
                lodSource,
                vertices,
                indices,
                processedMeshlets,
                meshletVertices,
                meshletTriangles,
                submeshes,
                materialSlotCount,
                lodIndex == 0);

            uint vertexCount = (uint)vertices.Count - vertexOffset;
            uint indexCount = (uint)indices.Count - indexOffset;
            uint meshletCount = (uint)processedMeshlets.Count - meshletOffset;
            BoundingBox bounds = ComputeBounds(vertices, vertexOffset, vertexCount);
            uint triangleCount = indexCount / 3u;

            if (triangleCount > previousTriangleCount)
                throw new InvalidDataException($"LOD{lodIndex} triangle count {triangleCount} is greater than previous LOD triangle count {previousTriangleCount}.");

            ProcessedLodQualityMetrics quality = lodSource.Quality ?? ComputeQuality(lodSources[0], lodSource);
            lods.Add(new ProcessedMeshLod(
                lodIndex,
                lodSource.Provenance,
                vertexOffset,
                vertexCount,
                indexOffset,
                indexCount,
                meshletOffset,
                meshletCount,
                bounds,
                quality,
                lodSource.SwitchDistance,
                lodSource.ScreenRelativeTransitionHeight));
            diagnostics.Add(new ProcessedLodDiagnostic(
                lodIndex,
                lodSource.Provenance,
                triangleCount,
                vertexCount,
                true,
                bounds,
                lodSource.Message));

            previousTriangleCount = triangleCount;
        }
    }

    private static IReadOnlyList<ProcessedLodSource> ResolveGeometryLodSources(
        IReadOnlyList<ModelSubMesh> submeshes,
        ProcessedMeshBuildOptions options,
        ProjectLodMetadata projectMetadata)
    {
        Dictionary<int, List<ModelSubMesh>> authored = GroupAuthoredLods(submeshes, projectMetadata);
        if (authored.Count > 0)
        {
            List<ProcessedLodSource> lods = CreateAuthoredLodSources(authored, projectMetadata);
            ValidateAuthoredLodMaterialCompatibility(lods);
            return lods;
        }

        var generated = new List<ProcessedLodSource>
        {
            new(0, MeshLodProvenance.Authored, submeshes, ZeroQuality, "Source mesh used as LOD0.")
        };

        if (!options.GenerateFallbackLods)
            return generated;

        for (int i = 0; i < options.GeneratedLodRatios.Count; i++)
        {
            float ratio = Math.Clamp(options.GeneratedLodRatios[i], 0.05f, 0.95f);
            ModelSubMesh[] simplified = submeshes.Select(submesh => SimplifySubmesh(submesh, ratio)).ToArray();
            generated.Add(new ProcessedLodSource(
                generated.Count,
                MeshLodProvenance.GeneratedFallback,
                simplified,
                ComputeQuality(submeshes, simplified),
                $"Generated fallback LOD{generated.Count} with triangle ratio {ratio:0.###}.",
                projectMetadata.FindLevel(generated.Count)?.SwitchDistance ?? options.GeneratedLodSwitchDistances.ElementAtOrDefault(generated.Count - 1),
                projectMetadata.FindLevel(generated.Count)?.ScreenRelativeTransitionHeight ?? 0f));
        }

        return generated;
    }

    private void AppendGeometryLodPayload(
        ProcessedLodSource lod,
        List<ProcessedMeshVertex> vertices,
        List<uint> indices,
        List<ProcessedMeshlet> processedMeshlets,
        List<uint> meshletVertices,
        List<uint> meshletTriangles,
        List<ProcessedSubmesh> processedSubmeshes,
        int materialSlotCount,
        bool writeSubmeshes)
    {
        for (int submeshIndex = 0; submeshIndex < lod.Submeshes.Count; submeshIndex++)
        {
            ModelSubMesh submesh = lod.Submeshes[submeshIndex];
            ValidateSubmesh(submesh, materialSlotCount, $"LOD{lod.Level}");

            uint vertexOffset = (uint)vertices.Count;
            uint indexOffset = (uint)indices.Count;
            AppendVertices(submesh, vertices);
            for (int i = 0; i < submesh.Indices.Length; i++)
                indices.Add(vertexOffset + submesh.Indices[i]);

            AppendSubmeshMeshlets(
                submesh,
                vertexOffset,
                indexOffset,
                submeshIndex,
                MeshletBuildSettings.Default,
                processedMeshlets,
                meshletVertices,
                meshletTriangles);

            if (writeSubmeshes)
                processedSubmeshes.Add(new ProcessedSubmesh(submesh.MaterialIndex, indexOffset, (uint)submesh.Indices.Length, submesh.BoundingBox));
        }
    }

    private void AppendMeshletLodPayloads(
        IReadOnlyList<ModelSubMesh> sourceSubmeshes,
        ProcessedMeshBuildOptions options,
        ProjectLodMetadata projectMetadata,
        List<ProcessedMeshVertex> vertices,
        List<uint> indices,
        List<ProcessedMeshlet> processedMeshlets,
        List<uint> meshletVertices,
        List<uint> meshletTriangles,
        List<ProcessedMeshLod> lods,
        List<ProcessedSubmesh> processedSubmeshes,
        List<ProcessedLodDiagnostic> diagnostics,
        int materialSlotCount)
    {
        IReadOnlyList<ModelSubMesh> baseSubmeshes = ResolveMeshletLodBaseSubmeshes(sourceSubmeshes, projectMetadata, out bool ignoredGeometryLods);
        MeshletBuildSettings[] lodSettings = ResolveMeshletLodSettings(options.MeshletLodSettings);

        uint vertexOffset = (uint)vertices.Count;
        uint indexOffset = (uint)indices.Count;
        var appendedSubmeshes = new List<AppendedSubmeshPayload>(baseSubmeshes.Count);

        for (int submeshIndex = 0; submeshIndex < baseSubmeshes.Count; submeshIndex++)
        {
            ModelSubMesh submesh = baseSubmeshes[submeshIndex];
            ValidateSubmesh(submesh, materialSlotCount, "meshlet LOD0");

            uint submeshVertexOffset = (uint)vertices.Count;
            uint submeshIndexOffset = (uint)indices.Count;
            AppendVertices(submesh, vertices);
            for (int i = 0; i < submesh.Indices.Length; i++)
                indices.Add(submeshVertexOffset + submesh.Indices[i]);

            appendedSubmeshes.Add(new AppendedSubmeshPayload(
                submesh,
                submeshVertexOffset,
                submeshIndexOffset,
                submeshIndex));
            processedSubmeshes.Add(new ProcessedSubmesh(
                submesh.MaterialIndex,
                submeshIndexOffset,
                (uint)submesh.Indices.Length,
                submesh.BoundingBox));
        }

        uint vertexCount = (uint)vertices.Count - vertexOffset;
        uint indexCount = (uint)indices.Count - indexOffset;
        BoundingBox bounds = ComputeBounds(vertices, vertexOffset, vertexCount);
        uint triangleCount = indexCount / 3u;

        for (int lodLevel = 0; lodLevel < lodSettings.Length; lodLevel++)
        {
            uint meshletOffset = (uint)processedMeshlets.Count;
            MeshletBuildSettings settings = lodSettings[lodLevel];
            foreach (AppendedSubmeshPayload payload in appendedSubmeshes)
            {
                AppendSubmeshMeshlets(
                    payload.Submesh,
                    payload.VertexOffset,
                    payload.IndexOffset,
                    payload.SubmeshIndex,
                    settings,
                    processedMeshlets,
                    meshletVertices,
                    meshletTriangles);
            }

            uint meshletCount = (uint)processedMeshlets.Count - meshletOffset;
            lods.Add(new ProcessedMeshLod(
                lodLevel,
                MeshLodProvenance.MeshletGenerated,
                vertexOffset,
                vertexCount,
                indexOffset,
                indexCount,
                meshletOffset,
                meshletCount,
                bounds,
                ZeroQuality,
                projectMetadata.FindLevel(lodLevel)?.SwitchDistance ?? 0f,
                projectMetadata.FindLevel(lodLevel)?.ScreenRelativeTransitionHeight ?? 0f));

            string message = lodLevel == 0
                ? "Source mesh used as meshlet LOD0."
                : $"Meshlet LOD{lodLevel} generated from LOD0 geometry with {settings.MaxVerticesPerMeshlet} vertices/{settings.MaxTrianglesPerMeshlet} triangles per meshlet.";
            if (ignoredGeometryLods && lodLevel == 0)
                message += " Authored geometry LODs were ignored because meshlet LOD mode is active.";

            diagnostics.Add(new ProcessedLodDiagnostic(
                lodLevel,
                MeshLodProvenance.MeshletGenerated,
                triangleCount,
                vertexCount,
                true,
                bounds,
                message));
        }
    }

    private void AppendSubmeshMeshlets(
        ModelSubMesh submesh,
        uint vertexOffset,
        uint indexOffset,
        int submeshIndex,
        MeshletBuildSettings settings,
        List<ProcessedMeshlet> processedMeshlets,
        List<uint> meshletVertices,
        List<uint> meshletTriangles)
    {
        MeshletMesh meshletMesh = _meshletBuilder.BuildMeshlets(
            submesh.Vertices,
            submesh.Indices,
            submesh.Normals,
            submesh.Tangents,
            submesh.Bitangents,
            submesh.TexCoords,
            submesh.Name,
            settings);

        uint localVertexBase = (uint)meshletVertices.Count;
        uint localTriangleBase = (uint)meshletTriangles.Count;
        meshletVertices.AddRange(meshletMesh.MeshletVertices);
        meshletTriangles.AddRange(meshletMesh.MeshletTriangles);

        foreach (Meshlet meshlet in meshletMesh.Meshlets)
        {
            processedMeshlets.Add(new ProcessedMeshlet(
                meshlet.BoundingSphereCenter,
                meshlet.BoundingSphereRadius,
                EstimateConeAxis(submesh, meshlet, meshletMesh.MeshletVertices, meshletMesh.MeshletTriangles),
                0f,
                vertexOffset,
                (uint)submesh.Vertices.Length,
                indexOffset,
                (uint)submesh.Indices.Length,
                meshlet.LocalVertexOffset + localVertexBase,
                meshlet.LocalVertexCount,
                meshlet.LocalTriangleOffset * 3u + localTriangleBase,
                meshlet.LocalTriangleCount,
                submesh.MaterialIndex,
                submeshIndex));
        }
    }

    private static IReadOnlyList<ModelSubMesh> ResolveMeshletLodBaseSubmeshes(
        IReadOnlyList<ModelSubMesh> submeshes,
        ProjectLodMetadata projectMetadata,
        out bool ignoredGeometryLods)
    {
        Dictionary<int, List<ModelSubMesh>> authored = GroupAuthoredLods(submeshes, projectMetadata);
        if (authored.Count == 0)
        {
            ignoredGeometryLods = false;
            return submeshes;
        }

        List<ProcessedLodSource> authoredSources = CreateAuthoredLodSources(authored, projectMetadata);
        ValidateAuthoredLodMaterialCompatibility(authoredSources);

        var baseSubmeshes = new List<ModelSubMesh>(submeshes.Count);
        ignoredGeometryLods = false;
        foreach (ModelSubMesh submesh in submeshes)
        {
            int? lodLevel = ResolveAuthoredLodLevel(submesh, projectMetadata);
            if (!lodLevel.HasValue || lodLevel.Value == 0)
            {
                baseSubmeshes.Add(submesh);
                continue;
            }

            ignoredGeometryLods = true;
        }

        return baseSubmeshes;
    }

    private static MeshletBuildSettings[] ResolveMeshletLodSettings(IReadOnlyList<MeshletBuildSettings> settings)
    {
        if (settings == null)
            throw new ArgumentNullException(nameof(settings));
        if (settings.Count == 0)
            throw new InvalidOperationException("Meshlet LOD mode requires at least one meshlet LOD setting.");
        if (settings.Count > 3)
            throw new InvalidOperationException("Meshlet LOD mode supports at most three LOD levels.");

        var resolved = new MeshletBuildSettings[settings.Count];
        for (int i = 0; i < resolved.Length; i++)
        {
            resolved[i] = settings[i];
            resolved[i].Validate();
        }

        return resolved;
    }

    private static List<ProcessedLodSource> CreateAuthoredLodSources(
        Dictionary<int, List<ModelSubMesh>> authored,
        ProjectLodMetadata projectMetadata)
    {
        var lods = new List<ProcessedLodSource>();
        foreach (int level in authored.Keys.OrderBy(static level => level))
        {
            ProjectLodEntry? metadata = projectMetadata.FindLevel(level);
            lods.Add(new ProcessedLodSource(
                level,
                MeshLodProvenance.Authored,
                authored[level],
                null,
                $"Authored LOD{level} imported from submesh naming or metadata.",
                metadata?.SwitchDistance ?? 0f,
                metadata?.ScreenRelativeTransitionHeight ?? 0f));
        }

        return lods;
    }

    private static Dictionary<int, List<ModelSubMesh>> GroupAuthoredLods(IReadOnlyList<ModelSubMesh> submeshes, ProjectLodMetadata projectMetadata)
    {
        var groups = new Dictionary<int, List<ModelSubMesh>>();
        foreach (ModelSubMesh submesh in submeshes)
        {
            int? lodLevel = ResolveAuthoredLodLevel(submesh, projectMetadata);
            if (!lodLevel.HasValue)
                continue;

            int level = lodLevel.Value;
            if (!groups.TryGetValue(level, out List<ModelSubMesh>? group))
            {
                group = new List<ModelSubMesh>();
                groups[level] = group;
            }

            group.Add(submesh);
        }

        if (groups.Count == 0)
            return groups;
        if (!groups.ContainsKey(0))
            throw new InvalidDataException("Authored LOD chain is missing LOD0.");

        int expected = 0;
        foreach (int level in groups.Keys.OrderBy(static level => level))
        {
            if (level != expected)
                throw new InvalidDataException($"Authored LOD chain has non-contiguous LOD level {level}; expected {expected}.");
            expected++;
        }

        return groups;
    }

    private static int? ResolveAuthoredLodLevel(ModelSubMesh submesh, ProjectLodMetadata projectMetadata)
    {
        int? metadataLevel = submesh.LodLevel >= 0
            ? submesh.LodLevel
            : projectMetadata.FindSubmeshLevel(submesh.Name);
        if (metadataLevel.HasValue)
            return metadataLevel.Value;

        Match match = LodSuffixPattern.Match(submesh.Name ?? string.Empty);
        return match.Success ? int.Parse(match.Groups["level"].Value) : null;
    }

    private static void ValidateAuthoredLodMaterialCompatibility(IReadOnlyList<ProcessedLodSource> lods)
    {
        int[] lod0Slots = lods[0].Submeshes.Select(static submesh => submesh.MaterialIndex).OrderBy(static slot => slot).ToArray();
        foreach (ProcessedLodSource lod in lods.Skip(1))
        {
            int[] slots = lod.Submeshes.Select(static submesh => submesh.MaterialIndex).OrderBy(static slot => slot).ToArray();
            if (!lod0Slots.SequenceEqual(slots))
                throw new InvalidDataException($"Authored LOD{lod.Level} material slots do not match LOD0.");
        }
    }

    private static ModelSubMesh SimplifySubmesh(ModelSubMesh source, float triangleRatio)
    {
        int sourceTriangleCount = source.Indices.Length / 3;
        int targetTriangleCount = Math.Max(1, (int)MathF.Round(sourceTriangleCount * triangleRatio));
        targetTriangleCount = Math.Min(targetTriangleCount, sourceTriangleCount);
        int step = Math.Max(1, sourceTriangleCount / targetTriangleCount);
        var selectedTriangles = new List<int>(targetTriangleCount);

        for (int i = 0; i < sourceTriangleCount && selectedTriangles.Count < targetTriangleCount; i += step)
            selectedTriangles.Add(i);
        for (int i = sourceTriangleCount - 1; selectedTriangles.Count < targetTriangleCount && i >= 0; i--)
        {
            if (!selectedTriangles.Contains(i))
                selectedTriangles.Add(i);
        }

        selectedTriangles.Sort();
        var remap = new Dictionary<uint, uint>();
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var tangents = new List<Vector3>();
        var bitangents = new List<Vector3>();
        var texCoords = new List<Vector2>();
        var texCoords1 = new List<Vector2>();
        var vertexColors = new List<Vector4>();
        var jointIndices = new List<VertexJointIndices>();
        var jointWeights = new List<VertexJointWeights>();
        var indices = new List<uint>(targetTriangleCount * 3);

        foreach (int triangle in selectedTriangles)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                uint oldIndex = source.Indices[triangle * 3 + corner];
                if (!remap.TryGetValue(oldIndex, out uint newIndex))
                {
                    newIndex = (uint)vertices.Count;
                    remap[oldIndex] = newIndex;
                    CopyVertex(source, (int)oldIndex, vertices, normals, tangents, bitangents, texCoords, texCoords1, vertexColors, jointIndices, jointWeights);
                }

                indices.Add(newIndex);
            }
        }

        BoundingBox bounds = BoundingBox.FromPoints(vertices.ToArray());
        return new ModelSubMesh
        {
            Name = $"{source.Name}_GeneratedLOD",
            MaterialIndex = source.MaterialIndex,
            NodeIndex = source.NodeIndex,
            SkinIndex = source.SkinIndex,
            SkinningBindTransform = source.SkinningBindTransform,
            Vertices = vertices.ToArray(),
            Normals = normals.ToArray(),
            Tangents = tangents.ToArray(),
            Bitangents = bitangents.ToArray(),
            TexCoords = texCoords.ToArray(),
            TexCoords1 = texCoords1.ToArray(),
            VertexColors = vertexColors.ToArray(),
            JointIndices0 = jointIndices.ToArray(),
            JointWeights0 = jointWeights.ToArray(),
            Indices = indices.ToArray(),
            BoundingBox = bounds,
            BoundingSphere = BoundingSphere.FromBox(bounds)
        };
    }

    private static void CopyVertex(
        ModelSubMesh source,
        int index,
        List<Vector3> vertices,
        List<Vector3> normals,
        List<Vector3> tangents,
        List<Vector3> bitangents,
        List<Vector2> texCoords,
        List<Vector2> texCoords1,
        List<Vector4> vertexColors,
        List<VertexJointIndices> jointIndices,
        List<VertexJointWeights> jointWeights)
    {
        vertices.Add(source.Vertices[index]);
        if (source.Normals.Length == source.Vertices.Length) normals.Add(source.Normals[index]);
        if (source.Tangents.Length == source.Vertices.Length) tangents.Add(source.Tangents[index]);
        if (source.Bitangents.Length == source.Vertices.Length) bitangents.Add(source.Bitangents[index]);
        if (source.TexCoords.Length == source.Vertices.Length) texCoords.Add(source.TexCoords[index]);
        if (source.TexCoords1.Length == source.Vertices.Length) texCoords1.Add(source.TexCoords1[index]);
        if (source.VertexColors.Length == source.Vertices.Length) vertexColors.Add(source.VertexColors[index]);
        if (source.JointIndices0.Length == source.Vertices.Length) jointIndices.Add(source.JointIndices0[index]);
        if (source.JointWeights0.Length == source.Vertices.Length) jointWeights.Add(source.JointWeights0[index]);
    }

    private static void AppendVertices(ModelSubMesh submesh, List<ProcessedMeshVertex> vertices)
    {
        for (int i = 0; i < submesh.Vertices.Length; i++)
        {
            Vector3 normal = submesh.Normals.Length == submesh.Vertices.Length ? NormalizeOrDefault(submesh.Normals[i], Vector3.UnitZ) : Vector3.UnitZ;
            Vector3 tangent3 = submesh.Tangents.Length == submesh.Vertices.Length ? NormalizeOrDefault(submesh.Tangents[i], Vector3.UnitX) : Vector3.UnitX;
            Vector3 bitangent = submesh.Bitangents.Length == submesh.Vertices.Length ? NormalizeOrDefault(submesh.Bitangents[i], Vector3.Zero) : Vector3.Zero;
            float handedness = Vector3.Dot(Vector3.Cross(normal, tangent3), bitangent) < 0f ? -1f : 1f;
            VectorSkinningData(submesh, i, out VertexSkinningPayload skinning);

            vertices.Add(new ProcessedMeshVertex(
                submesh.Vertices[i],
                normal,
                new Vector4(tangent3.X, tangent3.Y, tangent3.Z, handedness),
                submesh.TexCoords.Length == submesh.Vertices.Length ? submesh.TexCoords[i] : Vector2.Zero,
                submesh.TexCoords1.Length == submesh.Vertices.Length ? submesh.TexCoords1[i] : Vector2.Zero,
                submesh.VertexColors.Length == submesh.Vertices.Length ? submesh.VertexColors[i] : new Vector4(1f, 1f, 1f, 1f),
                skinning));
        }
    }

    private static void VectorSkinningData(ModelSubMesh submesh, int vertexIndex, out VertexSkinningPayload skinning)
    {
        skinning = VertexSkinningPayload.Empty;
        if (submesh.JointIndices0.Length != submesh.Vertices.Length || submesh.JointWeights0.Length != submesh.Vertices.Length)
            return;

        VertexJointIndices joints = submesh.JointIndices0[vertexIndex];
        VertexJointWeights weights = submesh.JointWeights0[vertexIndex].Normalized();
        skinning = new VertexSkinningPayload(joints.X, joints.Y, joints.Z, joints.W, weights.X, weights.Y, weights.Z, weights.W);
    }

    private static Vector3 EstimateConeAxis(ModelSubMesh submesh, Meshlet meshlet, uint[] localVertices, uint[] localTriangles)
    {
        if (submesh.Normals.Length != submesh.Vertices.Length || meshlet.LocalVertexCount == 0)
            return Vector3.UnitZ;

        Vector3 axis = Vector3.Zero;
        uint start = meshlet.LocalVertexOffset;
        uint end = start + meshlet.LocalVertexCount;
        for (uint i = start; i < end; i++)
            axis += submesh.Normals[localVertices[i]];

        return NormalizeOrDefault(axis, Vector3.UnitZ);
    }

    private static ProcessedLodQualityMetrics ComputeQuality(ProcessedLodSource lod0, ProcessedLodSource lod) =>
        ComputeQuality(lod0.Submeshes, lod.Submeshes);

    private static ProcessedLodQualityMetrics ComputeQuality(IReadOnlyList<ModelSubMesh> lod0, IReadOnlyList<ModelSubMesh> lod)
    {
        uint sourceTriangles = (uint)lod0.Sum(static submesh => submesh.Indices.Length / 3);
        uint sourceVertices = (uint)lod0.Sum(static submesh => submesh.Vertices.Length);
        uint targetTriangles = (uint)lod.Sum(static submesh => submesh.Indices.Length / 3);
        uint targetVertices = (uint)lod.Sum(static submesh => submesh.Vertices.Length);
        float triangleReduction = sourceTriangles == 0 ? 0f : 1f - Math.Clamp((float)targetTriangles / sourceTriangles, 0f, 1f);
        float vertexReduction = sourceVertices == 0 ? 0f : 1f - Math.Clamp((float)targetVertices / sourceVertices, 0f, 1f);
        BoundingBox sourceBounds = ComputeBounds(lod0.SelectMany(static submesh => submesh.Vertices).ToArray());
        BoundingBox targetBounds = ComputeBounds(lod.SelectMany(static submesh => submesh.Vertices).ToArray());
        float geometricError = Vector3.Distance(sourceBounds.Center, targetBounds.Center) + Math.Abs(sourceBounds.Extents.Length() - targetBounds.Extents.Length());
        return new ProcessedLodQualityMetrics(triangleReduction, vertexReduction, geometricError, 0f, 0f, true);
    }

    private static FoliageAssetMetadata? BuildFoliageMetadata(
        ModelMesh source,
        IReadOnlyList<ModelSubMesh> submeshes,
        ProcessedMeshBuildOptions options,
        ProcessedMeshFlags flags)
    {
        if ((flags & ProcessedMeshFlags.Foliage) == 0)
            return null;

        BoundingBox bounds = source.BoundingBox.Equals(default(BoundingBox))
            ? ComputeBounds(submeshes.SelectMany(static submesh => submesh.Vertices).ToArray())
            : source.BoundingBox;

        return new FoliageAssetMetadata(
            options.AssetId ?? source.Name,
            submeshes.Select(static submesh => submesh.MaterialIndex).Distinct().OrderBy(static slot => slot).ToArray(),
            options.FoliageAlphaTestThreshold,
            options.FoliageTwoSided,
            options.FoliageHasNormalMap,
            options.FoliageMipBias,
            options.FoliageWindDirection,
            options.FoliageWindStrength,
            options.FoliageBendPivot,
            new[] { new FoliageCluster(bounds, Math.Max(1, options.FoliageInstancesPerCluster), Array.Empty<FoliageInstance>(), 0f, 0f) });
    }

    private static ImpostorAssetMetadata? BuildImpostorMetadata(ModelMesh source, ProcessedMeshBuildOptions options, ProcessedMeshFlags flags)
    {
        if ((flags & ProcessedMeshFlags.SupportsImpostor) == 0)
            return null;

        string atlasId = options.ImpostorAtlasAssetId ?? $"{options.AssetId ?? source.Name}/impostor";
        return new ImpostorAssetMetadata(
            atlasId,
            options.ImpostorAtlasWidth,
            options.ImpostorAtlasHeight,
            options.ImpostorViewDirectionCount,
            options.ImpostorType,
            new[] { new ImpostorAtlasTexture(ImpostorTextureKind.Albedo, $"{atlasId}/albedo", TextureColorSpace.Srgb) },
            source.BoundingBox,
            source.BoundingBox.Center,
            options.ImpostorAlphaCoverage);
    }

    private static ProcessedMeshFlags ClassifyFlags(ModelMesh source, IReadOnlyList<ModelSubMesh> submeshes, ProcessedMeshBuildOptions options)
    {
        ProcessedMeshFlags flags = ProcessedMeshFlags.None;
        if (submeshes.Any(static submesh => submesh.SkinIndex >= 0))
            flags |= ProcessedMeshFlags.Skinned;
        if (source.Materials.Any(static material => material.AlphaMode == ModelAlphaMode.Mask))
            flags |= ProcessedMeshFlags.AlphaTested;
        if (source.Materials.Any(static material => material.AlphaMode == ModelAlphaMode.Blend))
            flags |= ProcessedMeshFlags.Transparent;
        if (options.IsFoliage)
            flags |= ProcessedMeshFlags.Foliage;
        if (options.GenerateImpostorMetadata)
            flags |= ProcessedMeshFlags.SupportsImpostor;
        return flags;
    }

    private static void ValidateSourceMesh(ModelMesh mesh)
    {
        if (mesh.Vertices.Length == 0 && mesh.SubMeshes.Count == 0)
            throw new ArgumentException("Imported mesh has no vertices.", nameof(mesh));
        if (mesh.Indices.Length == 0 && mesh.SubMeshes.Count == 0)
            throw new ArgumentException("Imported mesh has no indices.", nameof(mesh));
    }

    private static void ValidateSubmesh(ModelSubMesh submesh, int materialSlotCount, string context)
    {
        if (submesh.Vertices.Length == 0)
            throw new InvalidDataException($"{context} submesh '{submesh.Name}' has no vertices.");
        if (submesh.Indices.Length == 0 || submesh.Indices.Length % 3 != 0)
            throw new InvalidDataException($"{context} submesh '{submesh.Name}' has invalid index count.");
        if (submesh.MaterialIndex < 0 || submesh.MaterialIndex >= materialSlotCount)
            throw new InvalidDataException($"{context} submesh '{submesh.Name}' references material {submesh.MaterialIndex}, but material count is {materialSlotCount}.");
        for (int i = 0; i < submesh.Indices.Length; i++)
        {
            if (submesh.Indices[i] >= submesh.Vertices.Length)
                throw new InvalidDataException($"{context} submesh '{submesh.Name}' index {i} references vertex {submesh.Indices[i]}, but vertex count is {submesh.Vertices.Length}.");
        }
    }

    private static ModelSubMesh CreateWholeMeshSubmesh(ModelMesh source) =>
        new()
        {
            Name = source.Name,
            MaterialIndex = 0,
            Vertices = source.Vertices,
            Normals = source.Normals,
            Tangents = source.Tangents,
            Bitangents = source.Bitangents,
            TexCoords = source.TexCoords,
            TexCoords1 = source.TexCoords1,
            VertexColors = source.VertexColors,
            JointIndices0 = source.JointIndices0,
            JointWeights0 = source.JointWeights0,
            Indices = source.Indices,
            BoundingBox = source.BoundingBox,
            BoundingSphere = source.BoundingSphere
        };

    private static BoundingBox ComputeBounds(IReadOnlyList<ProcessedMeshVertex> vertices, uint offset, uint count)
    {
        var points = new Vector3[count];
        for (int i = 0; i < points.Length; i++)
            points[i] = vertices[(int)offset + i].Position;
        return ComputeBounds(points);
    }

    private static BoundingBox ComputeBounds(Vector3[] points) =>
        points.Length == 0 ? new BoundingBox(Vector3.Zero, Vector3.Zero) : BoundingBox.FromPoints(points);

    private static Vector3 NormalizeOrDefault(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared <= float.Epsilon ? fallback : value / MathF.Sqrt(lengthSquared);
    }

    private static string CreateDeterministicSourceFingerprint(ModelMesh mesh)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(mesh.Name).Append('|').Append(mesh.Vertices.Length).Append('|').Append(mesh.Indices.Length);
        foreach (ModelSubMesh submesh in mesh.SubMeshes)
            builder.Append('|').Append(submesh.Name).Append(':').Append(submesh.Vertices.Length).Append(':').Append(submesh.Indices.Length).Append(':').Append(submesh.MaterialIndex);
        return builder.ToString();
    }

    private static readonly ProcessedLodQualityMetrics ZeroQuality = new(0f, 0f, 0f, 0f, 0f, true);

    private readonly record struct AppendedSubmeshPayload(
        ModelSubMesh Submesh,
        uint VertexOffset,
        uint IndexOffset,
        int SubmeshIndex);

    private sealed record ProcessedLodSource(
        int Level,
        MeshLodProvenance Provenance,
        IReadOnlyList<ModelSubMesh> Submeshes,
        ProcessedLodQualityMetrics? Quality,
        string Message,
        float SwitchDistance = 0f,
        float ScreenRelativeTransitionHeight = 0f);
}

public enum ProcessedMeshLodMode
{
    Meshlet,
    GeometryFallback
}

public sealed record ProcessedMeshBuildOptions
{
    public static ProcessedMeshBuildOptions Default { get; } = new();

    public string? AssetId { get; init; }
    public string? SourcePath { get; init; }
    public string? SourceContentHash { get; init; }
    public ProcessedMeshLodMode LodMode { get; init; } = ProcessedMeshLodMode.Meshlet;
    public IReadOnlyList<MeshletBuildSettings> MeshletLodSettings { get; init; } = new[]
    {
        new MeshletBuildSettings(MeshletBuilder.HardwareMaxVerticesPerMeshlet, 64),
        new MeshletBuildSettings(MeshletBuilder.HardwareMaxVerticesPerMeshlet, 96),
        new MeshletBuildSettings(MeshletBuilder.HardwareMaxVerticesPerMeshlet, MeshletBuilder.HardwareMaxTrianglesPerMeshlet)
    };
    public bool GenerateFallbackLods { get; init; } = true;
    public IReadOnlyList<float> GeneratedLodRatios { get; init; } = new[] { 0.5f, 0.25f };
    public IReadOnlyList<float> GeneratedLodSwitchDistances { get; init; } = new[] { 40f, 120f };
    public string? ProjectMetadataPath { get; init; }
    public ContentBudgetLimits BudgetLimits { get; init; } = ContentBudgetLimits.Default;
    public bool IsFoliage { get; init; }
    public float FoliageAlphaTestThreshold { get; init; } = 0.5f;
    public bool FoliageTwoSided { get; init; } = true;
    public bool FoliageHasNormalMap { get; init; }
    public float FoliageMipBias { get; init; }
    public Vector3 FoliageWindDirection { get; init; } = Vector3.UnitX;
    public float FoliageWindStrength { get; init; } = 1f;
    public Vector3 FoliageBendPivot { get; init; } = Vector3.Zero;
    public int FoliageInstancesPerCluster { get; init; } = 1;
    public bool GenerateImpostorMetadata { get; init; }
    public string? ImpostorAtlasAssetId { get; init; }
    public int ImpostorAtlasWidth { get; init; } = 512;
    public int ImpostorAtlasHeight { get; init; } = 512;
    public int ImpostorViewDirectionCount { get; init; } = 16;
    public ImpostorType ImpostorType { get; init; } = ImpostorType.Octahedral;
    public float ImpostorAlphaCoverage { get; init; } = 1f;
}

internal sealed class ProjectLodMetadata
{
    private readonly Dictionary<string, int> _submeshLevels;
    private readonly Dictionary<int, ProjectLodEntry> _levels;

    private ProjectLodMetadata(Dictionary<string, int> submeshLevels, Dictionary<int, ProjectLodEntry> levels)
    {
        _submeshLevels = submeshLevels;
        _levels = levels;
    }

    public static ProjectLodMetadata Empty { get; } = new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<int, ProjectLodEntry>());

    public static ProjectLodMetadata Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Empty;
        if (!File.Exists(path))
            throw new FileNotFoundException($"LOD metadata file was not found: {Path.GetFullPath(path)}", Path.GetFullPath(path));

        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var submeshLevels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var levels = new Dictionary<int, ProjectLodEntry>();

        if (document.RootElement.TryGetProperty("submeshes", out JsonElement submeshes) &&
            submeshes.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in submeshes.EnumerateObject())
                submeshLevels[property.Name] = property.Value.GetInt32();
        }

        if (document.RootElement.TryGetProperty("lods", out JsonElement lods) &&
            lods.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement lod in lods.EnumerateArray())
            {
                int level = lod.GetProperty("level").GetInt32();
                float switchDistance = lod.TryGetProperty("switchDistance", out JsonElement switchElement) ? switchElement.GetSingle() : 0f;
                float screenHeight = lod.TryGetProperty("screenRelativeTransitionHeight", out JsonElement screenElement) ? screenElement.GetSingle() : 0f;
                levels[level] = new ProjectLodEntry(level, switchDistance, screenHeight);
            }
        }

        return new ProjectLodMetadata(submeshLevels, levels);
    }

    public int? FindSubmeshLevel(string? submeshName)
    {
        if (string.IsNullOrWhiteSpace(submeshName))
            return null;
        return _submeshLevels.TryGetValue(submeshName, out int level) ? level : null;
    }

    public ProjectLodEntry? FindLevel(int level) =>
        _levels.TryGetValue(level, out ProjectLodEntry? entry) ? entry : null;
}

internal sealed record ProjectLodEntry(
    int Level,
    float SwitchDistance,
    float ScreenRelativeTransitionHeight);
