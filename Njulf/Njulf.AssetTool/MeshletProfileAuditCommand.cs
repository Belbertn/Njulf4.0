using System.Text.Json;
using Njulf.Assets;

namespace Njulf.AssetTool;

internal static class MeshletProfileAuditCommand
{
    private const string ReportKind = "meshlet-profile-audit";
    private const int ReportSchema = 1;

    public static int Run(string[] args)
    {
        var sources = new List<string>();
        var profileIds = new List<string>();
        string? outputPath = null;
        ModelImportBackend backend = ModelImportBackend.Auto;
        AssimpMaterialTextureConvention materialConvention =
            AssimpMaterialTextureConvention.Standard;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out":
                    outputPath = RequireValue(args, ref i, "--out");
                    break;
                case "--profile":
                    profileIds.Add(RequireValue(args, ref i, "--profile"));
                    break;
                case "--backend":
                    backend = Enum.Parse<ModelImportBackend>(
                        RequireValue(args, ref i, "--backend"),
                        ignoreCase: true);
                    break;
                case "--assimp-material-texture-convention":
                    materialConvention =
                        Enum.Parse<AssimpMaterialTextureConvention>(
                            RequireValue(
                                args,
                                ref i,
                                "--assimp-material-texture-convention"),
                            ignoreCase: true);
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException(
                            $"Unknown meshlet-profile-audit option '{args[i]}'.");
                    sources.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }

        if (sources.Count == 0)
            throw new ArgumentException(
                "meshlet-profile-audit requires at least one model source.");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException(
                "meshlet-profile-audit requires --out <json>.");
        foreach (string source in sources)
            if (!File.Exists(source))
                throw new FileNotFoundException(
                    "Meshlet audit source was not found.", source);

        RendererMeshletBuildProfile[] profiles = profileIds.Count == 0
            ? RendererMeshletBuildProfiles.AvailableProfiles.ToArray()
            : profileIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(RendererMeshletBuildProfiles.Resolve)
                .ToArray();
        var perProfile = profiles.ToDictionary(
            static profile => profile.Id,
            static _ => new List<MeshletProfileSourceStatistics>(),
            StringComparer.Ordinal);
        using var importer = new ModelImporter();
        foreach (string source in sources)
        {
            ModelMesh model = importer.Import(source, new ImporterOptions
            {
                Backend = backend,
                AssimpMaterialTextureConvention = materialConvention
            });
            foreach (RendererMeshletBuildProfile profile in profiles)
            {
                ProcessedMeshAsset processed =
                    new ProcessedMeshAssetBuilder(profile).Build(model, source);
                perProfile[profile.Id].Add(new MeshletProfileSourceStatistics(
                    source.Replace('\\', '/'),
                    Measure(processed, profile)));
            }
        }

        MeshletProfileResult[] results = profiles.Select(profile =>
        {
            MeshletProfileSourceStatistics[] sourceStatistics =
                perProfile[profile.Id].ToArray();
            return new MeshletProfileResult(
                profile,
                profile.MaxVertices <=
                    RendererMeshletLodBuilder.MaxVerticesPerMeshlet &&
                profile.MaxTriangles <=
                    RendererMeshletLodBuilder.MaxTrianglesPerMeshlet,
                Combine(
                    sourceStatistics.Select(static entry => entry.Statistics),
                    profile),
                sourceStatistics);
        }).ToArray();
        var report = new MeshletProfileAuditReport(
            ReportKind,
            ReportSchema,
            sources.Select(static source => source.Replace('\\', '/')).ToArray(),
            results);

        string fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(
            fullOutputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        foreach (MeshletProfileResult result in results)
        {
            MeshletProfileStatistics statistics = result.Aggregate;
            Console.WriteLine(
                $"{result.Profile.Id}: meshlets={statistics.Lod0MeshletCount}, " +
                $"triFill={statistics.TriangleFill:P1}, " +
                $"vertexFill={statistics.VertexFill:P1}, " +
                $"validCones={statistics.ValidConeFraction:P1}, " +
                $"hierarchyNodes={statistics.HierarchyNodeCount}, " +
                $"runtimeCompatible={result.RuntimeCompatible}");
        }
        Console.WriteLine(fullOutputPath);
        return 0;
    }

    private static MeshletProfileStatistics Measure(
        ProcessedMeshAsset processed,
        RendererMeshletBuildProfile profile)
    {
        long meshletCount = 0;
        long triangleCount = 0;
        long vertexCount = 0;
        long validConeCount = 0;
        long hierarchyMeshletCount = 0;
        long hierarchyNodeCount = 0;
        long forceRefineNodeCount = 0;
        int hierarchyMaximumDepth = 0;
        foreach (ProcessedSubMeshAsset subMesh in processed.SubMeshes)
        {
            ProcessedMeshLodRange lod0 = subMesh.LodRanges.Single(
                static range => range.Level == 0);
            int lod0End = checked(lod0.FirstMeshlet + lod0.MeshletCount);
            for (int i = lod0.FirstMeshlet; i < lod0End; i++)
            {
                ref readonly Njulf.Core.Geometry.Meshlet meshlet =
                    ref subMesh.Meshlets[i];
                meshletCount++;
                triangleCount += meshlet.LocalTriangleCount;
                vertexCount += meshlet.LocalVertexCount;
                if (meshlet.NormalConeCutoff > 0f)
                    validConeCount++;
            }

            int flatMeshletCount = subMesh.LodRanges.Sum(
                static range => range.MeshletCount);
            hierarchyMeshletCount += subMesh.Meshlets.Length - flatMeshletCount;
            hierarchyNodeCount += subMesh.HierarchyNodes.Length;
            foreach (MeshletHierarchyNode node in subMesh.HierarchyNodes)
            {
                hierarchyMaximumDepth = Math.Max(
                    hierarchyMaximumDepth,
                    checked((int)node.Depth));
                if ((node.Flags & MeshletHierarchyNodeFlags.ForceRefine) != 0)
                    forceRefineNodeCount++;
            }
        }

        return CreateStatistics(
            meshletCount,
            triangleCount,
            vertexCount,
            validConeCount,
            hierarchyMeshletCount,
            hierarchyNodeCount,
            forceRefineNodeCount,
            hierarchyMaximumDepth,
            profile);
    }

    private static MeshletProfileStatistics Combine(
        IEnumerable<MeshletProfileStatistics> values,
        RendererMeshletBuildProfile profile)
    {
        MeshletProfileStatistics[] entries = values.ToArray();
        return CreateStatistics(
            entries.Sum(static value => value.Lod0MeshletCount),
            entries.Sum(static value => value.Lod0TriangleCount),
            entries.Sum(static value => value.Lod0VertexReferenceCount),
            entries.Sum(static value => value.ValidConeCount),
            entries.Sum(static value => value.HierarchyMeshletCount),
            entries.Sum(static value => value.HierarchyNodeCount),
            entries.Sum(static value => value.ForceRefineNodeCount),
            entries.Max(static value => value.HierarchyMaximumDepth),
            profile);
    }

    private static MeshletProfileStatistics CreateStatistics(
        long meshletCount,
        long triangleCount,
        long vertexCount,
        long validConeCount,
        long hierarchyMeshletCount,
        long hierarchyNodeCount,
        long forceRefineNodeCount,
        int hierarchyMaximumDepth,
        RendererMeshletBuildProfile profile) =>
        new(
            meshletCount,
            triangleCount,
            vertexCount,
            meshletCount == 0
                ? 0d
                : triangleCount /
                  (double)(meshletCount * profile.MaxTriangles),
            meshletCount == 0
                ? 0d
                : vertexCount /
                  (double)(meshletCount * profile.MaxVertices),
            validConeCount,
            meshletCount == 0 ? 0d : validConeCount / (double)meshletCount,
            hierarchyMeshletCount,
            hierarchyNodeCount,
            forceRefineNodeCount,
            hierarchyMaximumDepth);

    private static string RequireValue(
        IReadOnlyList<string> args,
        ref int index,
        string option)
    {
        if (++index >= args.Count ||
            string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException($"{option} requires a value.");
        }
        return args[index];
    }

    private sealed record MeshletProfileAuditReport(
        string Kind,
        int Schema,
        IReadOnlyList<string> Sources,
        IReadOnlyList<MeshletProfileResult> Profiles);

    private sealed record MeshletProfileResult(
        RendererMeshletBuildProfile Profile,
        bool RuntimeCompatible,
        MeshletProfileStatistics Aggregate,
        IReadOnlyList<MeshletProfileSourceStatistics> Sources);

    private sealed record MeshletProfileSourceStatistics(
        string Source,
        MeshletProfileStatistics Statistics);

    private sealed record MeshletProfileStatistics(
        long Lod0MeshletCount,
        long Lod0TriangleCount,
        long Lod0VertexReferenceCount,
        double TriangleFill,
        double VertexFill,
        long ValidConeCount,
        double ValidConeFraction,
        long HierarchyMeshletCount,
        long HierarchyNodeCount,
        long ForceRefineNodeCount,
        int HierarchyMaximumDepth);
}
