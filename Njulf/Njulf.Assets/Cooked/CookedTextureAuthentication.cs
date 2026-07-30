namespace Njulf.Assets.Cooked;

/// <summary>
/// Runtime expectations that bind one authored material slot to a cooked
/// texture payload. Every field is authenticated against the sibling
/// <c>.njtex</c> file before its transport statistics may be consumed.
/// </summary>
public sealed record CookedTextureRuntimeContract(
    string SourceIdentity,
    TextureSemantic Semantic,
    TextureColorSpace ColorSpace,
    TextureSamplerDescription Sampler,
    bool PreserveAlphaCoverage,
    float? AlphaCoverageCutoff);

/// <summary>
/// A cooked KTX2 payload and its source-resolution transport metadata after
/// whole-file, path, format, and authored-slot authentication.
/// </summary>
public sealed record AuthenticatedCookedTexture(
    CookedTextureMeta Metadata,
    string MetadataPath,
    string Ktx2Path,
    ulong MetadataContentHash,
    ulong Ktx2ContentHash,
    ulong PublicationContentHash);

public static class CookedTextureAuthentication
{
    public static AuthenticatedCookedTexture Authenticate(
        string ktx2Path,
        ReadOnlySpan<byte> ktx2Bytes,
        CookedTextureRuntimeContract contract,
        CookedAssetReaderFlags readerFlags = CookedAssetReaderFlags.None)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ktx2Path);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract.SourceIdentity);
        if (ktx2Bytes.IsEmpty)
            throw new InvalidDataException("A cooked KTX2 payload cannot be empty.");

        string fullKtx2Path = Path.GetFullPath(ktx2Path);
        if (!string.Equals(
                Path.GetExtension(fullKtx2Path),
                ".ktx2",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Cooked texture payload '{fullKtx2Path}' must use the .ktx2 extension.");
        }

        string metadataPath = Path.ChangeExtension(fullKtx2Path, ".njtex");
        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException(
                $"Cooked texture payload '{fullKtx2Path}' has no sibling .njtex metadata.",
                metadataPath);
        }

        CookedTextureMeta metadata =
            CookedPackage.LoadTextureMeta(
                metadataPath,
                readerFlags,
                out ulong metadataContentHash);
        if (readerFlags.HasFlag(CookedAssetReaderFlags.RequireSignature))
            CookedPackageSigner.VerifyRequired(fullKtx2Path, ktx2Bytes);

        string metadataDirectory = Path.GetDirectoryName(metadataPath)
            ?? throw new InvalidDataException(
                $"Cooked texture metadata '{metadataPath}' has no parent directory.");
        string referencedKtx2Path = Path.GetFullPath(
            Path.Combine(metadataDirectory, metadata.Ktx2RelativePath));
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(
                Path.GetDirectoryName(referencedKtx2Path),
                metadataDirectory,
                pathComparison))
        {
            throw new InvalidDataException(
                $"Cooked texture metadata '{metadataPath}' must reference a sibling KTX2 payload.");
        }
        if (!string.Equals(referencedKtx2Path, fullKtx2Path, pathComparison))
        {
            throw new InvalidDataException(
                $"Cooked texture metadata '{metadataPath}' references " +
                $"'{referencedKtx2Path}', not the requested payload '{fullKtx2Path}'.");
        }
        if (!string.Equals(
                metadata.SourceIdentity,
                contract.SourceIdentity,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' declares source identity " +
                $"'{metadata.SourceIdentity}', expected '{contract.SourceIdentity}'.");
        }

        ulong actualKtx2Hash = CookedHash.Bytes(ktx2Bytes);
        if (metadata.Ktx2ContentHash == 0 ||
            metadata.Ktx2ContentHash != actualKtx2Hash)
        {
            throw new CookedAssetHashException(
                fullKtx2Path,
                $"KTX2 payload expected whole-file hash " +
                $"0x{metadata.Ktx2ContentHash:x16}, got 0x{actualKtx2Hash:x16}");
        }
        if (metadata.EncodedBytes != ktx2Bytes.Length)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' contains {ktx2Bytes.Length} bytes, " +
                $"but its metadata declares {metadata.EncodedBytes}.");
        }

        (int width, int height, int mipCount, uint format) =
            TextureCooker.Inspect(ktx2Bytes, fullKtx2Path);
        if (metadata.CookedWidth != width ||
            metadata.CookedHeight != height ||
            metadata.MipCount != mipCount ||
            metadata.VulkanFormat != format)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' KTX2 header " +
                $"{width}x{height}, mips={mipCount}, format={format} does not match " +
                $"metadata {metadata.CookedWidth}x{metadata.CookedHeight}, " +
                $"mips={metadata.MipCount}, format={metadata.VulkanFormat}.");
        }
        if (metadata.Semantic != contract.Semantic)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' semantic {metadata.Semantic} " +
                $"does not match requested semantic {contract.Semantic}.");
        }
        if (metadata.ColorSpace != contract.ColorSpace)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' color space {metadata.ColorSpace} " +
                $"does not match requested color space {contract.ColorSpace}.");
        }
        if (metadata.Sampler != contract.Sampler)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' sampler does not match the authored material slot.");
        }
        if (metadata.AlphaCoveragePreserved != contract.PreserveAlphaCoverage)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' alpha-coverage policy does not match " +
                "the authored material slot.");
        }
        if (contract.PreserveAlphaCoverage)
        {
            if (!metadata.AlphaCoverageCutoff.HasValue ||
                !contract.AlphaCoverageCutoff.HasValue ||
                BitConverter.SingleToInt32Bits(metadata.AlphaCoverageCutoff.Value) !=
                BitConverter.SingleToInt32Bits(contract.AlphaCoverageCutoff.Value))
            {
                throw new InvalidDataException(
                    $"Cooked texture '{fullKtx2Path}' alpha cutoff does not exactly match " +
                    "the authored material slot.");
            }
        }
        else if (metadata.AlphaCoverageCutoff.HasValue ||
                 contract.AlphaCoverageCutoff.HasValue)
        {
            throw new InvalidDataException(
                $"Cooked texture '{fullKtx2Path}' carries an unexpected alpha cutoff " +
                "for a non-coverage-preserving slot.");
        }

        TextureTransportStatistics statistics = metadata.TransportStatistics;
        if (statistics.SourceContentHash != metadata.SourceHash)
        {
            throw new CookedAssetHashException(
                metadataPath,
                $"transport statistics expected source hash 0x{metadata.SourceHash:x16}, " +
                $"got 0x{statistics.SourceContentHash:x16}");
        }
        if (statistics.Semantic != contract.Semantic ||
            statistics.ColorSpace != contract.ColorSpace)
        {
            throw new InvalidDataException(
                $"Cooked texture '{metadataPath}' transport statistics do not match " +
                "the authenticated semantic and color space.");
        }
        if (statistics.Status == TextureTransportStatisticsStatus.Valid &&
            (statistics.Width != metadata.OriginalWidth ||
             statistics.Height != metadata.OriginalHeight))
        {
            throw new InvalidDataException(
                $"Cooked texture '{metadataPath}' source statistics dimensions " +
                $"{statistics.Width}x{statistics.Height} do not match the declared source " +
                $"{metadata.OriginalWidth}x{metadata.OriginalHeight}.");
        }

        ulong publicationContentHash = CookedHash.Ordered(
        [
            ("ktx2", actualKtx2Hash),
            ("metadata", metadataContentHash)
        ]);
        return new AuthenticatedCookedTexture(
            metadata,
            metadataPath,
            fullKtx2Path,
            metadataContentHash,
            actualKtx2Hash,
            publicationContentHash);
    }
}
