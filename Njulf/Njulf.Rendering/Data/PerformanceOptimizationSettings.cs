namespace Njulf.Rendering.Data;

[Flags]
public enum PerformanceOptimizationFeature : ulong
{
    None = 0,
    MeshletWorkingSetAdmission = 1UL << 0,
    ResolvedMeshletAddressing = 1UL << 1,
    StableDdgiRefinementAdmission = 1UL << 2,
    HybridOwnershipProjectionElision = 1UL << 3,
    ScreenLocalReceiverAdmission = 1UL << 4,
    SplitHybridForwardPrograms = 1UL << 5,
    RowMajorSpatialDdgiGather = 1UL << 6,
    SharedDdgiResolveStaging = 1UL << 7,
    StaticShaderSpecialization = 1UL << 8,
    DirectionalLatticeLoadSharing = 1UL << 9,
    DdgiPublicationGenerationReuse = 1UL << 10,
    AsymmetricSidedDrawStreams = 1UL << 11,
    CompactMaskedFeedback = 1UL << 12,
    SparseHybridLobePayload = 1UL << 13,
    AsyncGiFarFieldExecution = 1UL << 14,
    All = (1UL << 15) - 1
}

public sealed class PerformanceOptimizationSettings
{
    public bool Enabled { get; set; } = true;

    public PerformanceOptimizationFeature EnabledFeatures { get; set; } =
        PerformanceOptimizationFeature.All;

    public PerformanceOptimizationFeature EffectiveFeatures => Enabled
        ? EnabledFeatures & PerformanceOptimizationFeature.All
        : PerformanceOptimizationFeature.None;

    public bool IsEnabled(PerformanceOptimizationFeature feature) =>
        feature != PerformanceOptimizationFeature.None &&
        (EffectiveFeatures & feature) == feature;
}

/// <summary>
/// Stable command-line and environment representation for the campaign mask.
/// Names are deliberately independent of enum spelling so scripts remain
/// readable and durable when internal type names evolve.
/// </summary>
public static class PerformanceOptimizationFeatureMask
{
    private static readonly (string Name, PerformanceOptimizationFeature Feature)[]
        Entries =
        [
            ("meshlet-working-set", PerformanceOptimizationFeature.MeshletWorkingSetAdmission),
            ("resolved-meshlet-addressing", PerformanceOptimizationFeature.ResolvedMeshletAddressing),
            ("stable-ddgi-refinement", PerformanceOptimizationFeature.StableDdgiRefinementAdmission),
            ("hybrid-projection-elision", PerformanceOptimizationFeature.HybridOwnershipProjectionElision),
            ("screen-local-receiver", PerformanceOptimizationFeature.ScreenLocalReceiverAdmission),
            ("split-hybrid-forward", PerformanceOptimizationFeature.SplitHybridForwardPrograms),
            ("row-major-gather", PerformanceOptimizationFeature.RowMajorSpatialDdgiGather),
            ("shared-resolve-staging", PerformanceOptimizationFeature.SharedDdgiResolveStaging),
            ("static-shader-specialization", PerformanceOptimizationFeature.StaticShaderSpecialization),
            ("directional-lattice-sharing", PerformanceOptimizationFeature.DirectionalLatticeLoadSharing),
            ("generation-reuse", PerformanceOptimizationFeature.DdgiPublicationGenerationReuse),
            ("asymmetric-sided-streams", PerformanceOptimizationFeature.AsymmetricSidedDrawStreams),
            ("compact-masked-feedback", PerformanceOptimizationFeature.CompactMaskedFeedback),
            ("sparse-hybrid-lobe", PerformanceOptimizationFeature.SparseHybridLobePayload),
            ("async-gi", PerformanceOptimizationFeature.AsyncGiFarFieldExecution)
        ];

    public static PerformanceOptimizationFeature Parse(
        string value,
        string sourceName = "performance optimization mask")
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{sourceName} cannot be empty.", nameof(value));

        string[] tokens = value.Split(',', StringSplitOptions.TrimEntries);
        if (tokens.Any(static token => token.Length == 0))
        {
            throw new ArgumentException(
                $"{sourceName} contains an empty feature name.",
                nameof(value));
        }

        PerformanceOptimizationFeature mask = tokens[0].StartsWith('-')
            ? PerformanceOptimizationFeature.All
            : PerformanceOptimizationFeature.None;
        foreach (string rawToken in tokens)
        {
            bool remove = rawToken[0] == '-';
            bool add = rawToken[0] == '+';
            string token = (remove || add ? rawToken[1..] : rawToken)
                .Trim()
                .ToLowerInvariant();
            if (token.Length == 0)
            {
                throw new ArgumentException(
                    $"{sourceName} contains an empty feature name.",
                    nameof(value));
            }

            PerformanceOptimizationFeature feature;
            if (token == "all")
                feature = PerformanceOptimizationFeature.All;
            else if (token == "none")
                feature = PerformanceOptimizationFeature.All;
            else
            {
                feature = Entries.FirstOrDefault(entry => entry.Name == token)
                    .Feature;
                if (feature == PerformanceOptimizationFeature.None)
                {
                    throw new ArgumentException(
                        $"Unknown {sourceName} feature '{token}'. Valid values: " +
                        $"all, none, {string.Join(", ", Entries.Select(static entry => entry.Name))}.",
                        nameof(value));
                }
            }

            if (token == "none")
                mask = remove ? PerformanceOptimizationFeature.All : PerformanceOptimizationFeature.None;
            else if (remove)
                mask &= ~feature;
            else
                mask |= feature;
        }

        return mask & PerformanceOptimizationFeature.All;
    }

    public static string Format(PerformanceOptimizationFeature features)
    {
        features &= PerformanceOptimizationFeature.All;
        if (features == PerformanceOptimizationFeature.None)
            return "none";
        if (features == PerformanceOptimizationFeature.All)
            return "all";
        return string.Join(",", Entries
            .Where(entry => (features & entry.Feature) != 0)
            .Select(static entry => entry.Name));
    }
}
