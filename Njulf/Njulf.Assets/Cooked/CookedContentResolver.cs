namespace Njulf.Assets.Cooked;

public enum CookedResolutionStatus
{
    Found,
    Missing,
    Invalid
}

public sealed record CookedResolution(
    CookedResolutionStatus Status,
    string? PackagePath,
    string Reason,
    CookedAssetHeader? Header)
{
    /// <summary>
    /// The source identity captured during resolution. It is passed through to
    /// the package reader so strict callers do not hash the same source twice.
    /// </summary>
    public ulong? ExpectedSourceHash { get; init; }

    /// <summary>
    /// Optional immutable model snapshot shared between resolution and load.
    /// It is only populated for consumers that opt in, preserving custom test
    /// and editor snapshot factories.
    /// </summary>
    public CookedModelPackageSnapshot? ModelSnapshot { get; init; }
}

public sealed class CookedContentResolver
{
    private readonly string _contentRoot;

    public CookedContentResolver(string contentRoot) => _contentRoot = Path.GetFullPath(contentRoot);

    public CookedResolution ResolveModel(
        string requestedPath,
        string sourcePath,
        bool strictSourceHash,
        bool captureModelSnapshot = false)
    {
        bool packageRequestedDirectly = Path.GetExtension(requestedPath)
            .Equals(".njmodel", StringComparison.OrdinalIgnoreCase);
        string candidate = packageRequestedDirectly
            ? Path.GetFullPath(sourcePath)
            : ResolvePlatformCandidate(requestedPath);
        if (!File.Exists(candidate))
            return new CookedResolution(CookedResolutionStatus.Missing, candidate, $"cooked package was not found at '{candidate}'", null);
        ulong? sourceHash = null;
        try
        {
            // A direct .njmodel request has no source file to compare against.
            // Hashing the package and treating that value as its source hash
            // makes every valid package fail strict-source validation.
            sourceHash = !packageRequestedDirectly && File.Exists(sourcePath)
                ? CookedHash.File(sourcePath)
                : null;
            CookedAssetReaderFlags flags = strictSourceHash ? CookedAssetReaderFlags.StrictSourceHash : CookedAssetReaderFlags.None;
            CookedModelPackageSnapshot? snapshot = captureModelSnapshot
                ? CookedPackage.CaptureModelSnapshot(candidate)
                : null;
            using var reader = snapshot is null
                ? new CookedAssetReader(
                    candidate,
                    CookedAssetKind.Model,
                    flags,
                    sourceHash)
                : new CookedAssetReader(
                    snapshot.Content,
                    candidate,
                    CookedAssetKind.Model,
                    flags,
                    sourceHash);
            string reason = packageRequestedDirectly
                ? "cooked package was explicitly requested"
                : sourceHash.HasValue && reader.Header.SourceHash != sourceHash.Value
                ? $"source hash differs (package 0x{reader.Header.SourceHash:x16}, source 0x{sourceHash.Value:x16}); accepted in development mode"
                : "cooked package is current";
            return new CookedResolution(
                CookedResolutionStatus.Found,
                candidate,
                reason,
                reader.Header)
            {
                ExpectedSourceHash = sourceHash,
                ModelSnapshot = snapshot
            };
        }
        catch (Exception ex) when (ex is CookedAssetFormatException or CookedAssetHashException or IOException)
        {
            return new CookedResolution(
                CookedResolutionStatus.Invalid,
                candidate,
                ex.Message,
                null)
            {
                ExpectedSourceHash = sourceHash
            };
        }
    }

    private string ResolvePlatformCandidate(string requestedPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(requestedPath) + ".njmodel";
        string platformPath = Path.Combine(_contentRoot, "Cooked", CookedPlatform.Current, "models", fileName);
        if (File.Exists(platformPath))
            return platformPath;
        return Path.Combine(_contentRoot, "Cooked", "models", fileName);
    }
}

public sealed record CookedContentDiagnosticEntry(
    string RequestedPath,
    string? PackagePath,
    bool UsedCooked,
    string Reason,
    long BytesRead,
    double LoadMilliseconds,
    double UploadMilliseconds);

public sealed record CookedContentDiagnostics(
    int CookedAssetCount,
    long CookedBytesRead,
    double CookedLoadMilliseconds,
    double CookedUploadMilliseconds,
    int SourceFallbackCount,
    int VersionOrHashMismatchCount,
    IReadOnlyList<CookedContentDiagnosticEntry> Entries)
{
    public static CookedContentDiagnostics Empty { get; } = new(0, 0, 0, 0, 0, 0, Array.Empty<CookedContentDiagnosticEntry>());
}
