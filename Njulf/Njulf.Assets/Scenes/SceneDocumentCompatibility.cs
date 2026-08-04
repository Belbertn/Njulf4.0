namespace Njulf.Assets.Scenes;

/// <summary>
/// Applies source-schema compatibility rules without changing the document
/// supplied by the caller.
/// </summary>
internal static class SceneDocumentCompatibility
{
    private static readonly SceneColor LegacyAlbedo =
        new(1f, 1f, 1f, 1f);
    private static readonly SceneColor LegacyEmissive =
        new(0f, 0f, 0f, 0f);

    /// <summary>
    /// Schema-v1 material override fields were non-nullable and therefore
    /// acquired these defaults when absent from JSON. Preserve that behavior
    /// now that the current schema uses nullable fields for sparse overrides.
    /// </summary>
    public static SceneDocument MaterializeLegacyMaterialOverrideDefaults(
        SceneDocument source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SchemaVersion != 1 ||
            !source.Objects.Any(static item =>
                NeedsLegacyDefaults(item.MaterialOverride)))
        {
            return source;
        }

        return new SceneDocument
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            Name = source.Name,
            AmbientLight = source.AmbientLight,
            Objects = source.Objects.Select(MaterializeObject).ToList(),
            Lights = source.Lights.ToList(),
            ReflectionProbes = source.ReflectionProbes.ToList(),
            InstanceBatches = source.InstanceBatches.ToList(),
            FoliagePrototypes = source.FoliagePrototypes.ToList(),
            FoliagePatches = source.FoliagePatches.ToList(),
            ParticleEffects = source.ParticleEffects.ToList(),
            Dependencies = source.Dependencies.ToList()
        };
    }

    private static SceneObjectDocument MaterializeObject(
        SceneObjectDocument source)
    {
        if (!NeedsLegacyDefaults(source.MaterialOverride))
            return source;

        return new SceneObjectDocument
        {
            Id = source.Id,
            Name = source.Name,
            Model = source.Model,
            Position = source.Position,
            Rotation = source.Rotation,
            Scale = source.Scale,
            Visible = source.Visible,
            IsStatic = source.IsStatic,
            MaterialOverride = MaterializeOverride(source.MaterialOverride!)
        };
    }

    private static bool NeedsLegacyDefaults(
        SceneMaterialOverrideDocument? source) =>
        source is not null &&
        (source.Albedo is null ||
         source.Emissive is null ||
         source.Metallic is null ||
         source.Roughness is null ||
         source.NormalScale is null ||
         source.AlphaCutoff is null);

    private static SceneMaterialOverrideDocument MaterializeOverride(
        SceneMaterialOverrideDocument source) =>
        new()
        {
            Name = source.Name,
            Albedo = source.Albedo ?? LegacyAlbedo,
            Emissive = source.Emissive ?? LegacyEmissive,
            EmissiveColor = source.EmissiveColor,
            EmissiveStrength = source.EmissiveStrength,
            Metallic = source.Metallic ?? 0f,
            Roughness = source.Roughness ?? 1f,
            OcclusionStrength = source.OcclusionStrength,
            NormalScale = source.NormalScale ?? 1f,
            AlphaMode = source.AlphaMode,
            AlphaCutoff = source.AlphaCutoff ?? 0.5f,
            DoubleSided = source.DoubleSided,
            ReceivesShadows = source.ReceivesShadows,
            RenderBlendModeOverride = source.RenderBlendModeOverride,
            ShadingModel = source.ShadingModel,
            DiffuseGiParticipation = source.DiffuseGiParticipation,
            EmissionGiParticipation = source.EmissionGiParticipation,
            EmitsIntoGi = source.EmitsIntoGi,
            ReceivesDiffuseGi = source.ReceivesDiffuseGi
        };
}
