namespace Njulf.Assets;

/// <summary>Deterministic, vendor-neutral meshlet clusterization parameters.</summary>
public sealed record RendererMeshletBuildProfile(
    string Id,
    int MaxVertices,
    int MinTriangles,
    int MaxTriangles,
    float ConeWeight,
    float SplitFactor)
{
    public MeshletBuilder CreateBuilder() => new(
        MaxVertices,
        MaxTriangles,
        MinTriangles,
        ConeWeight,
        SplitFactor);
}

public static class RendererMeshletBuildProfiles
{
    public static RendererMeshletBuildProfile Portable48V64T { get; } =
        new("portable-48v-64t", 48, 0, 64, 0f, 0f);

    public static RendererMeshletBuildProfile PortableFlexCone025 { get; } =
        new("portable-flex-48v-32-64t-cone025-split2", 48, 32, 64, 0.25f, 2f);

    public static RendererMeshletBuildProfile PortableFlexCone050 { get; } =
        new("portable-flex-48v-32-64t-cone050-split2", 48, 32, 64, 0.5f, 2f);

    public static RendererMeshletBuildProfile Connected64V126T { get; } =
        new("connected-64v-126t", 64, 0, 126, 0f, 0f);

    /// <summary>
    /// Production uses the portable compact contract. Alternate profiles are
    /// explicit cook-time controls and are never selected from a vendor ID.
    /// </summary>
    public static RendererMeshletBuildProfile Production { get; } =
        Portable48V64T;

    public static IReadOnlyList<RendererMeshletBuildProfile> AvailableProfiles { get; } =
    [
        Portable48V64T,
        PortableFlexCone025,
        PortableFlexCone050,
        Connected64V126T
    ];

    public static RendererMeshletBuildProfile Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return AvailableProfiles.FirstOrDefault(
                   profile => string.Equals(
                       profile.Id,
                       id,
                       StringComparison.OrdinalIgnoreCase)) ??
               throw new ArgumentException(
                   $"Unknown meshlet build profile '{id}'. Available profiles: " +
                   string.Join(", ", AvailableProfiles.Select(profile => profile.Id)),
                   nameof(id));
    }

    [Obsolete("Use Portable48V64T. Profile IDs are vendor-neutral.")]
    public static RendererMeshletBuildProfile Rtx3060Baseline => Portable48V64T;

    [Obsolete("Use PortableFlexCone025. Profile IDs are vendor-neutral.")]
    public static RendererMeshletBuildProfile Rtx3060FlexCone025 => PortableFlexCone025;

    [Obsolete("Use PortableFlexCone050. Profile IDs are vendor-neutral.")]
    public static RendererMeshletBuildProfile Rtx3060FlexCone050 => PortableFlexCone050;
}
