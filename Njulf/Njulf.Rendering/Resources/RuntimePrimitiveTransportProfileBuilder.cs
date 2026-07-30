using Njulf.Assets;
using Njulf.Assets.Cooked;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Builds cooked-equivalent primitive transport profiles for uncooked runtime
/// models. All source decoding, geometry integration, retained records, and
/// cache storage have hard limits. Inputs outside those limits produce an
/// explicit invalid profile instead of a texture-wide or neutral guess.
/// </summary>
internal sealed class RuntimePrimitiveTransportProfileBuilder
{
    internal const int MaximumProfileTrianglesPerModel = 131_072;
    internal const int MaximumProfileVerticesPerModel = 262_144;
    internal const int MaximumProfileSubMeshesPerModel = 4_096;
    internal const int MaximumProfileCacheEntries = 128;
    internal const int MaximumCachedEmissiveRecords = 16_384;
    internal const int MaximumTextureCacheEntries = 32;
    internal const long MaximumTextureCacheBytes = 128L * 1024L * 1024L;
    internal const long MaximumTexturePixelsPerMaterial =
        TextureCooker.DefaultMaximumRuntimeTransportPixels;
    internal const int MaximumDiagnosticMessages = 32;
    private const int MaximumDiagnosticLength = 768;

    private readonly object _cacheLock = new();
    private readonly int _maximumProfileCacheEntries;
    private readonly int _maximumCachedEmissiveRecords;
    private readonly int _maximumTextureCacheEntries;
    private readonly long _maximumTextureCacheBytes;
    private readonly Dictionary<ProfileCacheKey, LinkedListNode<ProfileCacheEntry>> _profiles = new();
    private readonly LinkedList<ProfileCacheEntry> _profileLru = new();
    private readonly Dictionary<TextureCacheKey, LinkedListNode<TextureCacheEntry>> _textures = new();
    private readonly LinkedList<TextureCacheEntry> _textureLru = new();
    private int _cachedEmissiveRecords;
    private long _cachedTextureBytes;

    internal RuntimePrimitiveTransportProfileBuilder(
        int maximumProfileCacheEntries = MaximumProfileCacheEntries,
        int maximumCachedEmissiveRecords = MaximumCachedEmissiveRecords,
        int maximumTextureCacheEntries = MaximumTextureCacheEntries,
        long maximumTextureCacheBytes = MaximumTextureCacheBytes)
    {
        if (maximumProfileCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumProfileCacheEntries));
        if (maximumCachedEmissiveRecords < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCachedEmissiveRecords));
        if (maximumTextureCacheEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTextureCacheEntries));
        if (maximumTextureCacheBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTextureCacheBytes));

        _maximumProfileCacheEntries = maximumProfileCacheEntries;
        _maximumCachedEmissiveRecords = maximumCachedEmissiveRecords;
        _maximumTextureCacheEntries = maximumTextureCacheEntries;
        _maximumTextureCacheBytes = maximumTextureCacheBytes;
    }

    internal RuntimePrimitiveTransportProfileBuildResult Build(
        IReadOnlyList<ModelSubMesh> subMeshes,
        IReadOnlyList<ModelMaterial> materials)
    {
        ArgumentNullException.ThrowIfNull(subMeshes);
        ArgumentNullException.ThrowIfNull(materials);
        if (materials.Count == 0)
            throw new ArgumentException("At least one material is required.", nameof(materials));

        var diagnostics = new MutableDiagnostics();
        long totalTriangles = 0;
        long totalVertices = 0;
        foreach (ModelSubMesh subMesh in subMeshes)
        {
            ArgumentNullException.ThrowIfNull(subMesh);
            totalTriangles = checked(totalTriangles + subMesh.Indices.Length / 3L);
            totalVertices = checked(totalVertices + subMesh.Vertices.LongLength);
        }

        bool modelExceedsWorkBudget =
            subMeshes.Count > MaximumProfileSubMeshesPerModel ||
            totalTriangles > MaximumProfileTrianglesPerModel ||
            totalVertices > MaximumProfileVerticesPerModel;
        if (modelExceedsWorkBudget)
        {
            diagnostics.AddMessage(
                $"Runtime primitive transport rejected all {subMeshes.Count} submeshes because the model " +
                $"contains {totalVertices} vertices and {totalTriangles} triangles; hard per-model limits are " +
                $"{MaximumProfileSubMeshesPerModel} submeshes, {MaximumProfileVerticesPerModel} vertices, and " +
                $"{MaximumProfileTrianglesPerModel} triangles.");
        }

        var generated = new GiPrimitiveTransportProfile[subMeshes.Count];
        int activeMaterialSlot = -1;
        ModelMaterial? activeMaterial = null;
        GiPrimitiveTextureInputs? activeTextureInputs = null;
        IReadOnlyList<string>? activeFailures = null;
        for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
        {
            ModelSubMesh subMesh = subMeshes[subMeshIndex];
            if ((uint)subMesh.MaterialIndex >= (uint)materials.Count)
            {
                throw new InvalidDataException(
                    $"Runtime submesh {subMeshIndex} references material slot {subMesh.MaterialIndex}, " +
                    $"but only {materials.Count} materials exist.");
            }

            ModelMaterial material;
            if (modelExceedsWorkBudget)
            {
                material = NormalizeLegacyTextureBindings(materials[subMesh.MaterialIndex]);
                generated[subMeshIndex] = CreateRejectedProfile(
                    subMeshIndex,
                    subMesh,
                    material,
                    $"Runtime primitive integration was not attempted because the model's " +
                    $"{subMeshes.Count} submeshes, {totalVertices} vertices, and {totalTriangles} triangles " +
                    $"exceed the hard per-model work limits.");
                diagnostics.InvalidProfileCount++;
                continue;
            }

            if (activeMaterialSlot != subMesh.MaterialIndex)
            {
                activeMaterialSlot = subMesh.MaterialIndex;
                activeMaterial = NormalizeLegacyTextureBindings(
                    materials[subMesh.MaterialIndex]);
                var resolvedFailures = new List<string>();
                activeTextureInputs = ResolveTextureInputs(
                    activeMaterial,
                    resolvedFailures,
                    diagnostics);
                activeFailures = resolvedFailures;
            }
            material = activeMaterial!;
            GiPrimitiveTextureInputs textureInputs = activeTextureInputs!;
            IReadOnlyList<string> failures = activeFailures!;
            ulong inputHash = GiPrimitiveTransportProfileGenerator.CalculateInputHash(
                subMeshIndex,
                subMesh,
                material,
                textureInputs);
            var cacheKey = new ProfileCacheKey(
                inputHash,
                subMesh.Vertices.Length,
                subMesh.Indices.Length);

            GiPrimitiveTransportProfile profile;
            if (TryGetProfile(cacheKey, out GiPrimitiveTransportProfile cached))
            {
                diagnostics.ProfileCacheHitCount++;
                profile = cached;
            }
            else
            {
                diagnostics.ProfileCacheMissCount++;
                profile = GiPrimitiveTransportProfileGenerator.Generate(
                    subMeshIndex,
                    subMesh,
                    material,
                    textureInputs,
                    inputHash);
                profile = FailClosedIfIncomplete(profile, failures);
                AddProfile(cacheKey, profile);
            }

            if (profile.IsComplete &&
                profile.Quality != GiPrimitiveTransportProfileQuality.Invalid)
            {
                diagnostics.CompleteProfileCount++;
            }
            else
            {
                diagnostics.InvalidProfileCount++;
                diagnostics.AddMessage(
                    $"Submesh {subMeshIndex} ('{subMesh.Name}') has no authoritative runtime primitive " +
                    $"transport profile: {profile.InvalidReason}");
            }
            generated[subMeshIndex] = profile;
        }

        GiPrimitiveTransportProfile[] bounded =
            GiPrimitiveTransportProfileGenerator.ApplyPackageEmissiveRecordBudget(generated)
                .ToArray();
        diagnostics.PackageOmittedEmissiveRecordCount = bounded.Sum(
            static profile => Math.Max(
                profile.EmissiveCandidateTriangleCount - profile.EmissiveTriangles.Length,
                0));
        return new RuntimePrimitiveTransportProfileBuildResult(
            bounded,
            diagnostics.Freeze());
    }

    private GiPrimitiveTextureInputs ResolveTextureInputs(
        ModelMaterial material,
        ICollection<string> failures,
        MutableDiagnostics diagnostics)
    {
        long remainingPixels = MaximumTexturePixelsPerMaterial;
        var accountedTextures = new HashSet<TextureCacheKey>();
        MaterialFeatureFlags featureFlags =
            (MaterialFeatureFlags)material.FeatureFlags;
        return new GiPrimitiveTextureInputs(
        BaseColor: ResolveTexture(
            material.BaseColorTexture,
            TextureSemantic.Color,
            nameof(ModelMaterial.BaseColorTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        MetallicRoughness: ResolveTexture(
            material.MetallicRoughnessTexture,
            TextureSemantic.Data,
            nameof(ModelMaterial.MetallicRoughnessTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Occlusion: ResolveTexture(
            material.OcclusionTexture,
            TextureSemantic.Scalar,
            nameof(ModelMaterial.OcclusionTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Emissive: ResolveTexture(
            material.EmissiveTexture,
            material.EmissiveTexture?.ColorSpace == TextureColorSpace.HdrLinear
                ? TextureSemantic.Hdr
                : TextureSemantic.Color,
            nameof(ModelMaterial.EmissiveTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Normal: ResolveTexture(
            material.NormalTexture,
            TextureSemantic.Normal,
            nameof(ModelMaterial.NormalTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Clearcoat: ResolveTexture(
            HasFeatures(
                featureFlags,
                MaterialFeatureFlags.Clearcoat |
                MaterialFeatureFlags.ClearcoatTexture)
                ? material.ClearcoatTexture
                : null,
            TextureSemantic.Scalar,
            nameof(ModelMaterial.ClearcoatTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        SheenColor: ResolveTexture(
            HasFeatures(
                featureFlags,
                MaterialFeatureFlags.Sheen |
                MaterialFeatureFlags.SheenColorTexture)
                ? material.SheenColorTexture
                : null,
            TextureSemantic.Color,
            nameof(ModelMaterial.SheenColorTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Transmission: ResolveTexture(
            HasFeatures(
                featureFlags,
                MaterialFeatureFlags.Transmission |
                MaterialFeatureFlags.TransmissionTexture)
                ? material.TransmissionTexture
                : null,
            TextureSemantic.Scalar,
            nameof(ModelMaterial.TransmissionTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        Specular: ResolveTexture(
            HasFeatures(
                featureFlags,
                MaterialFeatureFlags.Specular |
                MaterialFeatureFlags.SpecularTexture)
                ? material.SpecularTexture
                : null,
            TextureSemantic.Scalar,
            nameof(ModelMaterial.SpecularTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures),
        SpecularColor: ResolveTexture(
            HasFeatures(
                featureFlags,
                MaterialFeatureFlags.Specular |
                MaterialFeatureFlags.SpecularColorTexture)
                ? material.SpecularColorTexture
                : null,
            TextureSemantic.Color,
            nameof(ModelMaterial.SpecularColorTexture),
            failures,
            diagnostics,
            ref remainingPixels,
            accountedTextures));
    }

    private static bool HasFeatures(
        MaterialFeatureFlags actual,
        MaterialFeatureFlags required) =>
        (actual & required) == required;

    private TextureTransportImage? ResolveTexture(
        ModelTextureSlot? slot,
        TextureSemantic semantic,
        string propertyName,
        ICollection<string> failures,
        MutableDiagnostics diagnostics,
        ref long remainingPixels,
        ISet<TextureCacheKey> accountedTextures)
    {
        if (slot?.Source is not ModelTextureSource source)
            return null;

        string identity = ResolveSourceIdentity(source, propertyName);
        if (!TryReadSourceBytes(source, out byte[] encoded, out string? readFailure))
        {
            TextureTransportStatistics unavailable = TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.InvalidData,
                readFailure!,
                0,
                semantic,
                slot.ColorSpace,
                "runtime bounded source reader");
            failures.Add($"{propertyName} '{identity}': {readFailure}");
            diagnostics.TextureAnalysisFailureCount++;
            return TextureTransportImage.Unavailable(unavailable);
        }

        ulong sourceHash = CookedHash.Bytes(encoded);
        var key = new TextureCacheKey(
            sourceHash,
            source.ContainerKind,
            slot.ColorSpace,
            semantic);
        if (TryGetTexture(key, out TextureTransportImage cached))
        {
            diagnostics.TextureCacheHitCount++;
            if (!cached.Statistics.IsValid)
            {
                diagnostics.TextureAnalysisFailureCount++;
                failures.Add(
                    $"{propertyName} '{identity}': {cached.Statistics.InvalidReason}");
            }
            else if (!accountedTextures.Contains(key) &&
                     cached.Statistics.PixelCount > remainingPixels)
            {
                string reason =
                    $"decoded source requires {cached.Statistics.PixelCount} pixels but only " +
                    $"{remainingPixels} remain in the hard per-material limit " +
                    $"{MaximumTexturePixelsPerMaterial}.";
                failures.Add($"{propertyName} '{identity}': {reason}");
                diagnostics.TextureAnalysisFailureCount++;
                return CreateUnavailableInput(
                    sourceHash,
                    semantic,
                    slot.ColorSpace,
                    reason);
            }
            else if (accountedTextures.Add(key))
            {
                remainingPixels -= cached.Statistics.PixelCount;
            }
            return cached;
        }

        diagnostics.TextureCacheMissCount++;
        if (remainingPixels <= 0)
        {
            string reason =
                $"no pixels remain in the hard per-material transport-analysis limit " +
                $"{MaximumTexturePixelsPerMaterial}.";
            failures.Add($"{propertyName} '{identity}': {reason}");
            diagnostics.TextureAnalysisFailureCount++;
            return CreateUnavailableInput(
                sourceHash,
                semantic,
                slot.ColorSpace,
                reason);
        }
        long pixelLimit = remainingPixels;
        TextureTransportSourceAnalysis analysis = TextureCooker.AnalyzeTransportSource(
            encoded,
            source.ContainerKind,
            identity,
            new TextureCookOptions(
                MaxDimension: 2048,
                ColorSpace: slot.ColorSpace,
                TargetFormatPolicy: TextureTargetFormatPolicy.Rgba8,
                Semantic: semantic),
            TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes,
            pixelLimit);
        TextureTransportImage result = analysis.Image ??
                                       TextureTransportImage.Unavailable(analysis.Statistics);
        if (analysis.IsSampleable || pixelLimit == MaximumTexturePixelsPerMaterial)
            AddTexture(key, result);
        if (analysis.IsSampleable)
        {
            if (accountedTextures.Add(key))
                remainingPixels -= analysis.Statistics.PixelCount;
        }
        else
        {
            diagnostics.TextureAnalysisFailureCount++;
            failures.Add($"{propertyName} '{identity}': {analysis.Statistics.InvalidReason}");
        }
        return result;
    }

    private static TextureTransportImage CreateUnavailableInput(
        ulong sourceHash,
        TextureSemantic semantic,
        TextureColorSpace colorSpace,
        string reason) =>
        TextureTransportImage.Unavailable(
            TextureTransportStatistics.Invalid(
                TextureTransportStatisticsStatus.UnsupportedEncoding,
                reason,
                sourceHash,
                semantic,
                colorSpace,
                "runtime primitive-profile work limiter"));

    private static GiPrimitiveTransportProfile FailClosedIfIncomplete(
        GiPrimitiveTransportProfile profile,
        IReadOnlyList<string> textureFailures)
    {
        if (profile.IsComplete &&
            profile.Quality != GiPrimitiveTransportProfileQuality.Invalid)
        {
            return profile;
        }

        string reason = profile.InvalidReason ??
                        "Runtime primitive transport inputs were incomplete.";
        if (textureFailures.Count > 0)
        {
            string details = string.Join(" ", textureFailures.Select(TrimDiagnostic));
            reason = $"{reason} Runtime source analysis failed closed: {details}";
        }
        return InvalidateProfile(profile, reason);
    }

    internal static GiPrimitiveTransportProfile InvalidateProfile(
        GiPrimitiveTransportProfile profile,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        reason = TrimDiagnostic(reason);
        GiPrimitiveTransportProfileValidity validity =
            profile.Validity &
            (GiPrimitiveTransportProfileValidity.Geometry |
             GiPrimitiveTransportProfileValidity.Finite);
        GiPrimitiveEmissiveTriangleFlags flags =
            validity.HasFlag(GiPrimitiveTransportProfileValidity.Finite)
                ? GiPrimitiveEmissiveTriangleFlags.Finite
                : GiPrimitiveEmissiveTriangleFlags.None;
        return profile with
        {
            Validity = validity,
            Quality = GiPrimitiveTransportProfileQuality.Invalid,
            InvalidReason = reason,
            EmissiveTriangleFlags = flags,
            EmissiveCandidateTriangleCount = 0,
            EmissiveTriangles = Array.Empty<GiPrimitiveEmissiveTriangleRecord>(),
            EmissiveTotalCookedImportance = 0.0,
            EmissiveRetainedCookedImportance = 0.0,
            EmissiveOmittedCookedImportance = 0.0,
            CookedEmissiveFactor = SanitizeUnitVector(profile.CookedEmissiveFactor),
            CookedEmissiveStrength = SanitizeHdr(profile.CookedEmissiveStrength),
            CookedBaseAlphaFactor = SanitizeUnit(profile.CookedBaseAlphaFactor),
            CookedAlphaMode = Enum.IsDefined(profile.CookedAlphaMode)
                ? profile.CookedAlphaMode
                : ModelAlphaMode.Opaque,
            CookedAlphaCutoff =
                double.IsFinite(profile.CookedAlphaCutoff) &&
                profile.CookedAlphaCutoff >= 0.0
                    ? profile.CookedAlphaCutoff
                    : 0.0,
            CookedEmissionEligible = false,
            BaseColorSamplingBinding = SanitizeBinding(
                profile.BaseColorSamplingBinding),
            EmissiveSamplingBinding = SanitizeBinding(
                profile.EmissiveSamplingBinding)
        };
    }

    private static TextureTransportVector4 SanitizeUnitVector(
        TextureTransportVector4 value) => new(
        SanitizeUnit(value.X),
        SanitizeUnit(value.Y),
        SanitizeUnit(value.Z),
        1.0);

    private static double SanitizeUnit(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 1.0) : 0.0;

    private static double SanitizeHdr(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0.0, 65504.0) : 0.0;

    private static GiPrimitiveTextureBindingSnapshot SanitizeBinding(
        GiPrimitiveTextureBindingSnapshot? binding)
    {
        if (binding is null ||
            binding.TexCoordSet is < 0 or > 1 ||
            !float.IsFinite(binding.Offset.X) ||
            !float.IsFinite(binding.Offset.Y) ||
            !float.IsFinite(binding.Scale.X) ||
            !float.IsFinite(binding.Scale.Y) ||
            !float.IsFinite(binding.RotationRadians) ||
            !float.IsFinite(binding.Sampler.MaxAnisotropy) ||
            binding.Sampler.MaxAnisotropy <= 0.0f)
        {
            return new GiPrimitiveTextureBindingSnapshot();
        }
        return binding with { };
    }

    private static GiPrimitiveTransportProfile CreateRejectedProfile(
        int subMeshIndex,
        ModelSubMesh subMesh,
        ModelMaterial material,
        string reason)
    {
        int sourceTriangleCount = subMesh.Indices.Length / 3;
        return new GiPrimitiveTransportProfile
        {
            SubMeshIndex = subMeshIndex,
            SubMeshName = subMesh.Name,
            MaterialSlot = subMesh.MaterialIndex,
            Validity = GiPrimitiveTransportProfileValidity.None,
            Quality = GiPrimitiveTransportProfileQuality.Invalid,
            TextureSourceHashes = new ulong[GiPrimitiveTransportProfile.TextureSourceHashCount],
            InvalidReason = TrimDiagnostic(reason),
            EmissiveSourceTriangleCount = sourceTriangleCount,
            EmissiveTriangleFlags = GiPrimitiveEmissiveTriangleFlags.None,
            CookedAlphaMode = Enum.IsDefined(material.AlphaMode)
                ? material.AlphaMode
                : ModelAlphaMode.Opaque,
            CookedAlphaCutoff = float.IsFinite(material.AlphaCutoff) && material.AlphaCutoff >= 0.0f
                ? material.AlphaCutoff
                : 0.0,
            CookedBaseAlphaFactor = float.IsFinite(material.Albedo.W)
                ? Math.Clamp(material.Albedo.W, 0.0f, 1.0f)
                : 0.0,
            CookedEmissionEligible = false
        };
    }

    private static ModelMaterial NormalizeLegacyTextureBindings(ModelMaterial source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ModelMaterial material = source.Clone();
        material.BaseColorTexture = NormalizeLegacySlot(
            source.BaseColorTexture,
            source.AlbedoTexturePath,
            TextureColorSpace.Srgb);
        material.MetallicRoughnessTexture = NormalizeLegacySlot(
            source.MetallicRoughnessTexture,
            source.MetallicRoughnessTexturePath,
            TextureColorSpace.Linear);
        material.OcclusionTexture = NormalizeLegacySlot(
            source.OcclusionTexture,
            source.OcclusionTexturePath,
            TextureColorSpace.Linear);
        material.EmissiveTexture = NormalizeLegacySlot(
            source.EmissiveTexture,
            source.EmissiveTexturePath,
            TextureColorSpace.Srgb);
        material.NormalTexture = NormalizeLegacySlot(
            source.NormalTexture,
            source.NormalTexturePath,
            TextureColorSpace.Linear);
        material.ClearcoatTexture = NormalizeLegacySlot(
            source.ClearcoatTexture,
            source.ClearcoatTexturePath,
            TextureColorSpace.Linear);
        material.SheenColorTexture = NormalizeLegacySlot(
            source.SheenColorTexture,
            source.SheenColorTexturePath,
            TextureColorSpace.Srgb);
        material.TransmissionTexture = NormalizeLegacySlot(
            source.TransmissionTexture,
            source.TransmissionTexturePath,
            TextureColorSpace.Linear);
        material.SpecularTexture = NormalizeLegacySlot(
            source.SpecularTexture,
            source.SpecularTexturePath,
            TextureColorSpace.Linear);
        material.SpecularColorTexture = NormalizeLegacySlot(
            source.SpecularColorTexture,
            source.SpecularColorTexturePath,
            TextureColorSpace.Srgb);
        return material;
    }

    private static ModelTextureSlot? NormalizeLegacySlot(
        ModelTextureSlot? slot,
        string? path,
        TextureColorSpace defaultColorSpace)
    {
        if (slot?.Source is not null || string.IsNullOrWhiteSpace(path))
            return slot;

        string identity;
        try
        {
            identity = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            identity = path;
        }
        string extension = Path.GetExtension(identity);
        TextureContainerKind container =
            string.Equals(extension, ".ktx2", StringComparison.OrdinalIgnoreCase)
                ? TextureContainerKind.Ktx2
                : string.Equals(extension, ".webp", StringComparison.OrdinalIgnoreCase)
                    ? TextureContainerKind.WebP
                    : TextureContainerKind.StandardImage;
        return new ModelTextureSlot
        {
            Source = new ModelTextureSource
            {
                DebugName = Path.GetFileName(identity),
                SourceKind = TextureSourceKind.ExternalFile,
                FilePath = identity,
                CacheIdentity = identity,
                ContainerKind = container
            },
            Sampler = slot?.Sampler ?? TextureSamplerDescription.Default,
            ColorSpace = slot?.ColorSpace ?? defaultColorSpace,
            TexCoordSet = slot?.TexCoordSet ?? 0,
            Offset = slot?.Offset ?? Vector2.Zero,
            Scale = slot?.Scale ?? Vector2.One,
            RotationRadians = slot?.RotationRadians ?? 0.0f
        };
    }

    private static string ResolveSourceIdentity(
        ModelTextureSource source,
        string fallback)
    {
        if (!string.IsNullOrWhiteSpace(source.CacheIdentity))
            return source.CacheIdentity;
        if (!string.IsNullOrWhiteSpace(source.FilePath))
        {
            try
            {
                return Path.GetFullPath(source.FilePath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return source.FilePath;
            }
        }
        return string.IsNullOrWhiteSpace(source.DebugName) ? fallback : source.DebugName;
    }

    internal static bool TryReadSourceBytes(
        ModelTextureSource source,
        out byte[] encoded,
        out string? failure)
    {
        encoded = Array.Empty<byte>();
        failure = null;
        try
        {
            if (source.Bytes is { Length: > 0 } bytes)
            {
                if (bytes.Length > TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes)
                {
                    failure =
                        $"encoded payload contains {bytes.Length} bytes, exceeding the hard limit " +
                        $"{TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes}.";
                    return false;
                }
                encoded = bytes.ToArray();
                return true;
            }

            if (string.IsNullOrWhiteSpace(source.FilePath))
            {
                failure = "source has neither encoded bytes nor a file path.";
                return false;
            }
            string path = Path.GetFullPath(source.FilePath);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            long declaredLength = stream.Length;
            if (declaredLength is <= 0 or > TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes)
            {
                failure =
                    $"source file contains {declaredLength} bytes; expected a size in (0, " +
                    $"{TextureCooker.DefaultMaximumRuntimeTransportEncodedBytes}].";
                return false;
            }

            // Allocate only the already-admitted length, fill it exactly, then
            // probe one additional byte. A concurrent grow cannot make this
            // path allocate or read beyond the hard cap; a shrink/short read
            // also fails closed instead of authenticating partial content.
            encoded = GC.AllocateUninitializedArray<byte>(
                checked((int)declaredLength));
            int totalRead = 0;
            while (totalRead < encoded.Length)
            {
                int read = stream.Read(encoded, totalRead, encoded.Length - totalRead);
                if (read == 0)
                {
                    failure =
                        $"source file changed during its bounded read ({declaredLength} bytes " +
                        $"declared, {totalRead} bytes available).";
                    encoded = Array.Empty<byte>();
                    return false;
                }
                totalRead += read;
            }

            if (stream.ReadByte() != -1)
            {
                failure =
                    $"source file grew beyond its admitted {declaredLength}-byte length during " +
                    "the bounded read.";
                encoded = Array.Empty<byte>();
                return false;
            }
            return true;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            failure = $"bounded source read failed: {ex.Message}";
            encoded = Array.Empty<byte>();
            return false;
        }
    }

    private bool TryGetProfile(
        ProfileCacheKey key,
        out GiPrimitiveTransportProfile profile)
    {
        lock (_cacheLock)
        {
            if (!_profiles.TryGetValue(key, out LinkedListNode<ProfileCacheEntry>? node))
            {
                profile = null!;
                return false;
            }
            _profileLru.Remove(node);
            _profileLru.AddFirst(node);
            profile = CloneProfile(node.Value.Profile);
            return true;
        }
    }

    private void AddProfile(
        ProfileCacheKey key,
        GiPrimitiveTransportProfile profile)
    {
        int recordCount = profile.EmissiveTriangles.Length;
        if (recordCount > _maximumCachedEmissiveRecords)
            return;

        lock (_cacheLock)
        {
            if (_profiles.TryGetValue(key, out LinkedListNode<ProfileCacheEntry>? existing))
            {
                _profileLru.Remove(existing);
                _cachedEmissiveRecords -= existing.Value.EmissiveRecordCount;
                _profiles.Remove(key);
            }
            var entry = new ProfileCacheEntry(key, CloneProfile(profile), recordCount);
            LinkedListNode<ProfileCacheEntry> node = _profileLru.AddFirst(entry);
            _profiles.Add(key, node);
            _cachedEmissiveRecords += recordCount;
            while (_profiles.Count > _maximumProfileCacheEntries ||
                   _cachedEmissiveRecords > _maximumCachedEmissiveRecords)
            {
                LinkedListNode<ProfileCacheEntry> victim = _profileLru.Last!;
                _profileLru.RemoveLast();
                _profiles.Remove(victim.Value.Key);
                _cachedEmissiveRecords -= victim.Value.EmissiveRecordCount;
            }
        }
    }

    private bool TryGetTexture(
        TextureCacheKey key,
        out TextureTransportImage image)
    {
        lock (_cacheLock)
        {
            if (!_textures.TryGetValue(key, out LinkedListNode<TextureCacheEntry>? node))
            {
                image = null!;
                return false;
            }
            _textureLru.Remove(node);
            _textureLru.AddFirst(node);
            image = node.Value.Image;
            return true;
        }
    }

    private void AddTexture(
        TextureCacheKey key,
        TextureTransportImage image)
    {
        long bytes = EstimateTextureBytes(image);
        if (bytes > _maximumTextureCacheBytes)
            return;

        lock (_cacheLock)
        {
            if (_textures.TryGetValue(key, out LinkedListNode<TextureCacheEntry>? existing))
            {
                _textureLru.Remove(existing);
                _cachedTextureBytes -= existing.Value.EstimatedBytes;
                _textures.Remove(key);
            }
            var entry = new TextureCacheEntry(key, image, bytes);
            LinkedListNode<TextureCacheEntry> node = _textureLru.AddFirst(entry);
            _textures.Add(key, node);
            _cachedTextureBytes += bytes;
            while (_textures.Count > _maximumTextureCacheEntries ||
                   _cachedTextureBytes > _maximumTextureCacheBytes)
            {
                LinkedListNode<TextureCacheEntry> victim = _textureLru.Last!;
                _textureLru.RemoveLast();
                _textures.Remove(victim.Value.Key);
                _cachedTextureBytes -= victim.Value.EstimatedBytes;
            }
        }
    }

    private static long EstimateTextureBytes(TextureTransportImage image)
    {
        if (!image.Statistics.IsValid)
            return 1;
        try
        {
            return checked(image.Statistics.PixelCount * 4L * sizeof(double));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static GiPrimitiveTransportProfile CloneProfile(
        GiPrimitiveTransportProfile profile) => profile with
        {
            TextureSourceHashes = (ulong[])profile.TextureSourceHashes.Clone(),
            EmissiveTriangles = profile.EmissiveTriangles
            .Select(static record => record with { })
            .ToArray(),
            BaseColorSamplingBinding = profile.BaseColorSamplingBinding with { },
            EmissiveSamplingBinding = profile.EmissiveSamplingBinding with { }
        };

    private static string TrimDiagnostic(string value)
    {
        string normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= MaximumDiagnosticLength
            ? normalized
            : normalized[..MaximumDiagnosticLength] + "…";
    }

    private readonly record struct ProfileCacheKey(
        ulong InputHash,
        int VertexCount,
        int IndexCount);

    private sealed record ProfileCacheEntry(
        ProfileCacheKey Key,
        GiPrimitiveTransportProfile Profile,
        int EmissiveRecordCount);

    private readonly record struct TextureCacheKey(
        ulong SourceContentHash,
        TextureContainerKind ContainerKind,
        TextureColorSpace ColorSpace,
        TextureSemantic Semantic);

    private sealed record TextureCacheEntry(
        TextureCacheKey Key,
        TextureTransportImage Image,
        long EstimatedBytes);

    private sealed class MutableDiagnostics
    {
        private readonly List<string> _messages = new();
        private readonly HashSet<string> _uniqueMessages = new(StringComparer.Ordinal);

        public int CompleteProfileCount;
        public int InvalidProfileCount;
        public int ProfileCacheHitCount;
        public int ProfileCacheMissCount;
        public int TextureCacheHitCount;
        public int TextureCacheMissCount;
        public int TextureAnalysisFailureCount;
        public int PackageOmittedEmissiveRecordCount;

        public void AddMessage(string message)
        {
            string bounded = TrimDiagnostic(message);
            if (_messages.Count >= MaximumDiagnosticMessages ||
                !_uniqueMessages.Add(bounded))
            {
                return;
            }
            _messages.Add(bounded);
        }

        public RuntimePrimitiveTransportProfileBuildDiagnostics Freeze() => new(
            CompleteProfileCount,
            InvalidProfileCount,
            ProfileCacheHitCount,
            ProfileCacheMissCount,
            TextureCacheHitCount,
            TextureCacheMissCount,
            TextureAnalysisFailureCount,
            PackageOmittedEmissiveRecordCount,
            _messages.ToArray());
    }
}

internal sealed record RuntimePrimitiveTransportProfileBuildResult(
    GiPrimitiveTransportProfile[] Profiles,
    RuntimePrimitiveTransportProfileBuildDiagnostics Diagnostics);

internal sealed record RuntimePrimitiveTransportProfileBuildDiagnostics(
    int CompleteProfileCount,
    int InvalidProfileCount,
    int ProfileCacheHitCount,
    int ProfileCacheMissCount,
    int TextureCacheHitCount,
    int TextureCacheMissCount,
    int TextureAnalysisFailureCount,
    int PackageOmittedEmissiveRecordCount,
    IReadOnlyList<string> Messages)
{
    public string Summary => Messages.Count == 0
        ? string.Empty
        : string.Join(" | ", Messages);
}
