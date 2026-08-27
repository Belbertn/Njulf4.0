using Njulf.Assets.Validation;

namespace Njulf.Assets.Cooked;

/// <summary>
/// Stable identity for the source-import semantics required by a cooked model.
/// Unlike the cook database identity, this intentionally excludes storage,
/// compression, signing, and platform texture-output choices.
/// </summary>
public static class CookedModelImportContract
{
    public const ushort MinimumFormatMinor = 4;
    public const int SchemaVersion = 1;

    internal const int MaterialTransportMetadataRevision = 3;
    internal const int MaterialTexturePolicyRevision = 2;
    internal const int MeshLodAlgorithmRevision = 2;

    public static ulong Compute(string sourcePath, ImporterOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        options ??= ImporterOptions.Default;

        ModelImportBackend backend = ModelImporter.ResolveBackend(sourcePath, options);
        string preferredFormat = (options.PreferredFormat ?? string.Empty)
            .Trim()
            .ToLowerInvariant();

        return CookedHash.Bytes(CookedJson.Serialize(new
        {
            SchemaVersion,
            Backend = backend,
            options.FlipUVs,
            options.GenerateNormals,
            options.GenerateTangents,
            options.Triangulate,
            options.JoinIdenticalVertices,
            options.SortByPrimitiveType,
            options.CalculateBoundingBoxes,
            options.GlobalScale,
            options.FlipWindingOrder,
            PreferredFormat = preferredFormat,
            options.AssimpMaterialTextureConvention,
            options.ImportLights,
            options.DefaultImportedLightRange,
            options.MaximumImportedLightRange,
            options.ImportedLightAttenuationCutoff,
            MaterialTransportMetadataRevision,
            MaterialTexturePolicyRevision,
            AmazonBistroMaterialProfileRevision =
                options.AssimpMaterialTextureConvention ==
                    AssimpMaterialTextureConvention.AmazonBistro
                    ? AmazonBistroMaterialProfile.ProfileRevision
                    : string.Empty,
            MeshLodAlgorithmRevision,
            CausticTopologyAlgorithmVersion =
                ModelGiCausticHeroTopologyAnalyzer.CurrentAlgorithmVersion,
            TextureStatisticsAlgorithmVersion =
                TextureTransportStatistics.CurrentAlgorithmVersion,
            PrimitiveTransportAlgorithmVersion =
                GiPrimitiveTransportProfile.CurrentAlgorithmVersion,
            TextureTransportStatistics.StbDecoderVersion,
            TextureTransportStatistics.WebPDecoderVersion,
            TextureTransportStatistics.BcDecoderVersion,
            TextureTransportStatistics.DdsDecoderVersion,
            TextureTransportStatistics.KtxStatisticsDecoderVersion
        }));
    }
}
