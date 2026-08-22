using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Njulf.Rendering.Debug;
using StbImageSharp;

namespace NjulfHelloGame;

public sealed record SampleSponzaTemporalFrameChange
{
    public string Route { get; init; } = string.Empty;
    public string Phase { get; init; } = string.Empty;
    public int PreviousFrameIndex { get; init; }
    public int FrameIndex { get; init; }
    public double MeanAbsoluteRgbDelta { get; init; }
    public double RootMeanSquareRgbDelta { get; init; }
    public double P95AbsoluteChannelDelta { get; init; }
    public double MaximumAbsoluteChannelDelta { get; init; }
    public double ChangedPixelFraction { get; init; }
    public double CameraTranslationMeters { get; init; }
    public double CameraAngularDegrees { get; init; }
    public double LocalSpikeScore { get; init; }
}

public readonly record struct SampleSponzaTemporalPixelChangeMetrics(
    double MeanAbsoluteRgbDelta,
    double RootMeanSquareRgbDelta,
    double P95AbsoluteChannelDelta,
    double MaximumAbsoluteChannelDelta,
    double ChangedPixelFraction);

public sealed record SampleSponzaTemporalContactSheet(
    string RelativePath,
    int FirstFrameIndex,
    int LastFrameIndex,
    int Columns,
    int Rows);

public sealed record SampleSponzaTemporalRankedChange(
    int Rank,
    int PreviousFrameIndex,
    int FrameIndex,
    double LocalSpikeScore,
    double RootMeanSquareRgbDelta,
    string SheetRelativePath,
    int SheetRow);

public sealed record SampleSponzaTemporalRouteReview(
    string Route,
    int FrameCount,
    int PairCount,
    string ChangesRelativePath,
    IReadOnlyList<SampleSponzaTemporalContactSheet> ContactSheets,
    IReadOnlyList<SampleSponzaTemporalRankedChange> TopChanges);

public sealed record SampleSponzaTemporalReviewIndex
{
    public const string SchemaVersionValue =
        "sponza-temporal-review-index/v1";

    public string SchemaVersion { get; init; } = SchemaVersionValue;
    public string ContractFingerprint { get; init; } =
        SampleSponzaTemporalCaptureContract.Fingerprint;
    public string Interpretation { get; init; } =
        "Advisory image-space change ranking; intended camera motion is not reprojected and no stability pass/fail is inferred.";
    public IReadOnlyList<SampleSponzaTemporalRouteReview> Routes { get; init; } =
        Array.Empty<SampleSponzaTemporalRouteReview>();
}

/// <summary>
/// CPU-only analyzer for a completed temporal bundle. It uses bounded image
/// memory, preserves raw capture files, and atomically replaces only the
/// analyzer-owned output directory.
/// </summary>
public static class SampleSponzaTemporalCaptureAnalyzer
{
    private const int ContactFramesPerSheet = 60;
    private const int ContactColumns = 10;
    private const int ContactRows = 6;
    private const int ContactThumbnailWidth = 160;
    private const int ContactThumbnailHeight = 90;
    private const int RankedChangeCount = 24;
    private const int RankedRowsPerSheet = 4;
    private const int RankedPanelWidth = 320;
    private const int RankedPanelHeight = 180;
    private const int ChangeThreshold = 8;
    private const int LocalWindowRadius = 15;

    public static int RunOffline(
        string outputDirectory,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        try
        {
            string fullDirectory = Path.GetFullPath(outputDirectory);
            SampleSponzaTemporalRunManifest manifest =
                ReadManifest(fullDirectory);
            if (!string.Equals(
                    manifest.Status,
                    "completed",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Offline analysis requires a completed capture manifest; " +
                    $"found status '{manifest.Status}'.");
            }

            Analyze(fullDirectory, manifest, standardOutput);
            standardOutput.WriteLine(
                $"Sponza temporal analysis completed: " +
                Path.Combine(fullDirectory, "analysis"));
            return 0;
        }
        catch (Exception exception)
        {
            standardError.WriteLine(
                $"Sponza temporal analysis failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    public static void Analyze(
        string outputDirectory,
        SampleSponzaTemporalRunManifest manifest,
        TextWriter? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        string bundleDirectory = Path.GetFullPath(outputDirectory);
        ValidateManifest(bundleDirectory, manifest);

        string analysisDirectory = Path.Combine(bundleDirectory, "analysis");
        string temporaryDirectory = Path.Combine(
            bundleDirectory,
            $".analysis.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var routeReviews = new List<SampleSponzaTemporalRouteReview>(2);
            foreach (string route in new[]
                     {
                         SampleSponzaTemporalCaptureContract.HorizontalRoute,
                         SampleSponzaTemporalCaptureContract.VerticalRoute
                     })
            {
                progress?.WriteLine(
                    $"Analyzing Sponza temporal route '{route}'...");
                SampleSponzaTemporalFrameArtifact[] frames = manifest.Frames
                    .Where(frame => string.Equals(
                        frame.Route,
                        route,
                        StringComparison.Ordinal))
                    .OrderBy(static frame => frame.RouteFrameIndex)
                    .ToArray();
                routeReviews.Add(AnalyzeRoute(
                    bundleDirectory,
                    temporaryDirectory,
                    route,
                    frames));
            }

            var reviewIndex = new SampleSponzaTemporalReviewIndex
            {
                ContractFingerprint = manifest.ContractFingerprint,
                Routes = routeReviews
            };
            SampleSponzaTemporalCaptureContract.WriteJsonAtomic(
                Path.Combine(temporaryDirectory, "review-index.json"),
                reviewIndex,
                "Sponza temporal review index");
            ReplaceAnalysisDirectory(
                bundleDirectory,
                temporaryDirectory,
                analysisDirectory);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
            throw;
        }
    }

    internal static SampleSponzaTemporalRouteReview AnalyzeRouteForTesting(
        string bundleDirectory,
        string analysisTemporaryDirectory,
        string route,
        IReadOnlyList<SampleSponzaTemporalFrameArtifact> frames) =>
        AnalyzeRoute(
            bundleDirectory,
            analysisTemporaryDirectory,
            route,
            frames);

    private static SampleSponzaTemporalRouteReview AnalyzeRoute(
        string bundleDirectory,
        string analysisTemporaryDirectory,
        string route,
        IReadOnlyList<SampleSponzaTemporalFrameArtifact> frames)
    {
        string routeOutput = Path.Combine(analysisTemporaryDirectory, route);
        Directory.CreateDirectory(routeOutput);
        var changes = new List<SampleSponzaTemporalFrameChange>(
            Math.Max(0, frames.Count - 1));
        var contactSheets = new List<SampleSponzaTemporalContactSheet>();
        DecodedImage? previous = null;
        SampleSponzaTemporalFrameArtifact? previousFrame = null;

        for (int sheetStart = 0;
             sheetStart < frames.Count;
             sheetStart += ContactFramesPerSheet)
        {
            int sheetCount = Math.Min(
                ContactFramesPerSheet,
                frames.Count - sheetStart);
            byte[] contactPixels = CreateOpaqueImage(
                ContactColumns * ContactThumbnailWidth,
                ContactRows * ContactThumbnailHeight);
            for (int localIndex = 0; localIndex < sheetCount; localIndex++)
            {
                SampleSponzaTemporalFrameArtifact frame =
                    frames[sheetStart + localIndex];
                DecodedImage current = LoadValidatedFrame(
                    bundleDirectory,
                    frame);
                byte[] thumbnail = ResizeBox(
                    current,
                    ContactThumbnailWidth,
                    ContactThumbnailHeight);
                CopyRgba(
                    thumbnail,
                    ContactThumbnailWidth,
                    ContactThumbnailHeight,
                    contactPixels,
                    ContactColumns * ContactThumbnailWidth,
                    (localIndex % ContactColumns) * ContactThumbnailWidth,
                    (localIndex / ContactColumns) * ContactThumbnailHeight);

                if (previous is not null && previousFrame is not null)
                {
                    changes.Add(CalculateChange(
                        route,
                        previousFrame,
                        frame,
                        previous,
                        current));
                }

                previous = current;
                previousFrame = frame;
            }

            int sheetEnd = sheetStart + sheetCount - 1;
            string fileName =
                $"contact-{sheetStart:D4}-{sheetEnd:D4}.png";
            PngScreenshotEncoder.WriteAtomic(
                Path.Combine(routeOutput, fileName),
                contactPixels,
                ContactColumns * ContactThumbnailWidth,
                ContactRows * ContactThumbnailHeight,
                ScreenshotPixelFormat.Rgba8);
            contactSheets.Add(new SampleSponzaTemporalContactSheet(
                $"analysis/{route}/{fileName}",
                sheetStart,
                sheetEnd,
                ContactColumns,
                ContactRows));
        }

        ApplyLocalSpikeScores(changes);
        string changesFileName = "changes.csv";
        File.WriteAllText(
            Path.Combine(routeOutput, changesFileName),
            CreateChangesCsv(changes),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        IReadOnlyList<SampleSponzaTemporalRankedChange> topChanges =
            WriteTopChangeSheets(
                bundleDirectory,
                routeOutput,
                route,
                frames,
                changes);

        return new SampleSponzaTemporalRouteReview(
            route,
            frames.Count,
            changes.Count,
            $"analysis/{route}/{changesFileName}",
            contactSheets,
            topChanges);
    }

    private static SampleSponzaTemporalFrameChange CalculateChange(
        string route,
        SampleSponzaTemporalFrameArtifact previousFrame,
        SampleSponzaTemporalFrameArtifact frame,
        DecodedImage previous,
        DecodedImage current)
    {
        EnsureSameDimensions(previous, current);
        SampleSponzaTemporalPixelChangeMetrics pixelMetrics =
            CalculatePixelChange(
                previous.Pixels,
                current.Pixels,
                current.Width,
                current.Height);
        double dx = frame.CameraPositionX - previousFrame.CameraPositionX;
        double dy = frame.CameraPositionY - previousFrame.CameraPositionY;
        double dz = frame.CameraPositionZ - previousFrame.CameraPositionZ;
        double yaw = frame.CameraYaw - previousFrame.CameraYaw;
        double pitch = frame.CameraPitch - previousFrame.CameraPitch;
        return new SampleSponzaTemporalFrameChange
        {
            Route = route,
            Phase = frame.Phase,
            PreviousFrameIndex = previousFrame.RouteFrameIndex,
            FrameIndex = frame.RouteFrameIndex,
            MeanAbsoluteRgbDelta = pixelMetrics.MeanAbsoluteRgbDelta,
            RootMeanSquareRgbDelta =
                pixelMetrics.RootMeanSquareRgbDelta,
            P95AbsoluteChannelDelta =
                pixelMetrics.P95AbsoluteChannelDelta,
            MaximumAbsoluteChannelDelta =
                pixelMetrics.MaximumAbsoluteChannelDelta,
            ChangedPixelFraction = pixelMetrics.ChangedPixelFraction,
            CameraTranslationMeters = Math.Sqrt(dx * dx + dy * dy + dz * dz),
            CameraAngularDegrees =
                Math.Sqrt(yaw * yaw + pitch * pitch) * 180.0 / Math.PI
        };
    }

    internal static SampleSponzaTemporalPixelChangeMetrics CalculatePixelChange(
        ReadOnlySpan<byte> previousPixels,
        ReadOnlySpan<byte> currentPixels,
        int width,
        int height)
    {
        int expectedLength = checked(width * height * 4);
        if (width <= 0 || height <= 0 ||
            previousPixels.Length != expectedLength ||
            currentPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                "Temporal metric inputs must be equally sized RGBA images.");
        }

        long absoluteSum = 0;
        long squareSum = 0;
        long changedPixelCount = 0;
        int maximum = 0;
        var histogram = new long[256];
        int pixelCount = checked(width * height);
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            int offset = pixel * 4;
            int pixelMaximum = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                int delta = Math.Abs(
                    currentPixels[offset + channel] -
                    previousPixels[offset + channel]);
                absoluteSum += delta;
                squareSum += (long)delta * delta;
                histogram[delta]++;
                pixelMaximum = Math.Max(pixelMaximum, delta);
                maximum = Math.Max(maximum, delta);
            }

            if (pixelMaximum >= ChangeThreshold)
                changedPixelCount++;
        }

        long channelCount = checked((long)pixelCount * 3L);
        long p95Target = (long)Math.Ceiling(channelCount * 0.95);
        long cumulative = 0;
        int p95 = 0;
        for (int delta = 0; delta < histogram.Length; delta++)
        {
            cumulative += histogram[delta];
            if (cumulative >= p95Target)
            {
                p95 = delta;
                break;
            }
        }

        return new SampleSponzaTemporalPixelChangeMetrics(
                absoluteSum / (double)channelCount / 255.0,
                Math.Sqrt(squareSum / (double)channelCount) / 255.0,
                p95 / 255.0,
                maximum / 255.0,
                changedPixelCount / (double)pixelCount);
    }

    private static void ApplyLocalSpikeScores(
        List<SampleSponzaTemporalFrameChange> changes)
    {
        for (int index = 0; index < changes.Count; index++)
        {
            SampleSponzaTemporalFrameChange current = changes[index];
            var neighborhood = new List<double>(LocalWindowRadius * 2);
            int first = Math.Max(0, index - LocalWindowRadius);
            int last = Math.Min(changes.Count - 1, index + LocalWindowRadius);
            for (int candidate = first; candidate <= last; candidate++)
            {
                if (candidate == index ||
                    !string.Equals(
                        changes[candidate].Phase,
                        current.Phase,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                neighborhood.Add(
                    changes[candidate].RootMeanSquareRgbDelta);
            }

            if (neighborhood.Count == 0)
                continue;
            double median = Median(neighborhood);
            double medianAbsoluteDeviation = Median(
                neighborhood.Select(value => Math.Abs(value - median)).ToList());
            double denominator = Math.Max(
                1.4826 * medianAbsoluteDeviation,
                1.0 / 255.0);
            changes[index] = current with
            {
                LocalSpikeScore = Math.Max(
                    0.0,
                    (current.RootMeanSquareRgbDelta - median) /
                    denominator)
            };
        }
    }

    private static IReadOnlyList<SampleSponzaTemporalRankedChange>
        WriteTopChangeSheets(
            string bundleDirectory,
            string routeOutput,
            string route,
            IReadOnlyList<SampleSponzaTemporalFrameArtifact> frames,
            IReadOnlyList<SampleSponzaTemporalFrameChange> changes)
    {
        SampleSponzaTemporalFrameChange[] ranked = changes
            .OrderByDescending(static change => change.LocalSpikeScore)
            .ThenByDescending(static change =>
                change.RootMeanSquareRgbDelta)
            .ThenBy(static change => change.FrameIndex)
            .Take(RankedChangeCount)
            .ToArray();
        var result = new List<SampleSponzaTemporalRankedChange>(ranked.Length);
        int sheetWidth = RankedPanelWidth * 3;
        int sheetHeight = RankedPanelHeight * RankedRowsPerSheet;
        for (int sheetIndex = 0;
             sheetIndex * RankedRowsPerSheet < ranked.Length;
             sheetIndex++)
        {
            byte[] sheet = CreateOpaqueImage(sheetWidth, sheetHeight);
            int firstRank = sheetIndex * RankedRowsPerSheet;
            int rowCount = Math.Min(
                RankedRowsPerSheet,
                ranked.Length - firstRank);
            string fileName = $"top-changes-{sheetIndex:D2}.png";
            for (int row = 0; row < rowCount; row++)
            {
                int rankIndex = firstRank + row;
                SampleSponzaTemporalFrameChange change = ranked[rankIndex];
                SampleSponzaTemporalFrameArtifact previousFrame =
                    frames[change.PreviousFrameIndex];
                SampleSponzaTemporalFrameArtifact currentFrame =
                    frames[change.FrameIndex];
                DecodedImage previous = LoadValidatedFrame(
                    bundleDirectory,
                    previousFrame);
                DecodedImage current = LoadValidatedFrame(
                    bundleDirectory,
                    currentFrame);
                byte[] previousThumbnail = ResizeBox(
                    previous,
                    RankedPanelWidth,
                    RankedPanelHeight);
                byte[] currentThumbnail = ResizeBox(
                    current,
                    RankedPanelWidth,
                    RankedPanelHeight);
                byte[] differenceThumbnail = ResizeDifferenceBox(
                    previous,
                    current,
                    RankedPanelWidth,
                    RankedPanelHeight,
                    amplification: 4);
                int y = row * RankedPanelHeight;
                CopyRgba(previousThumbnail, RankedPanelWidth,
                    RankedPanelHeight, sheet, sheetWidth, 0, y);
                CopyRgba(currentThumbnail, RankedPanelWidth,
                    RankedPanelHeight, sheet, sheetWidth,
                    RankedPanelWidth, y);
                CopyRgba(differenceThumbnail, RankedPanelWidth,
                    RankedPanelHeight, sheet, sheetWidth,
                    RankedPanelWidth * 2, y);
                result.Add(new SampleSponzaTemporalRankedChange(
                    rankIndex + 1,
                    change.PreviousFrameIndex,
                    change.FrameIndex,
                    change.LocalSpikeScore,
                    change.RootMeanSquareRgbDelta,
                    $"analysis/{route}/{fileName}",
                    row));
            }

            PngScreenshotEncoder.WriteAtomic(
                Path.Combine(routeOutput, fileName),
                sheet,
                sheetWidth,
                sheetHeight,
                ScreenshotPixelFormat.Rgba8);
        }

        return result;
    }

    private static string CreateChangesCsv(
        IReadOnlyList<SampleSponzaTemporalFrameChange> changes)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "route,phase,previousFrame,frame,meanAbsoluteRgbDelta,rootMeanSquareRgbDelta,p95AbsoluteChannelDelta,maximumAbsoluteChannelDelta,changedPixelFraction,cameraTranslationMeters,cameraAngularDegrees,localSpikeScore");
        foreach (SampleSponzaTemporalFrameChange change in changes)
        {
            builder.Append(change.Route).Append(',')
                .Append(change.Phase).Append(',')
                .Append(change.PreviousFrameIndex).Append(',')
                .Append(change.FrameIndex).Append(',')
                .Append(Format(change.MeanAbsoluteRgbDelta)).Append(',')
                .Append(Format(change.RootMeanSquareRgbDelta)).Append(',')
                .Append(Format(change.P95AbsoluteChannelDelta)).Append(',')
                .Append(Format(change.MaximumAbsoluteChannelDelta)).Append(',')
                .Append(Format(change.ChangedPixelFraction)).Append(',')
                .Append(Format(change.CameraTranslationMeters)).Append(',')
                .Append(Format(change.CameraAngularDegrees)).Append(',')
                .Append(Format(change.LocalSpikeScore)).AppendLine();
        }

        return builder.ToString();
    }

    private static void ValidateManifest(
        string bundleDirectory,
        SampleSponzaTemporalRunManifest manifest)
    {
        if (!Directory.Exists(bundleDirectory))
            throw new DirectoryNotFoundException(bundleDirectory);
        if (!string.Equals(
                manifest.SchemaVersion,
                SampleSponzaTemporalCaptureContract.RunSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported temporal run schema '{manifest.SchemaVersion}'.");
        }
        if (!string.Equals(
                manifest.ContractFingerprint,
                SampleSponzaTemporalCaptureContract.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Temporal capture contract fingerprint does not match this analyzer.");
        }
        if (manifest.Width != SampleSponzaTemporalCaptureContract.Width ||
            manifest.Height != SampleSponzaTemporalCaptureContract.Height ||
            manifest.FramesPerSecond !=
                SampleSponzaTemporalCaptureContract.FramesPerSecond)
        {
            throw new InvalidDataException(
                "Temporal capture dimensions or frame rate do not match the locked contract.");
        }
        if (manifest.Frames.Count !=
            SampleSponzaTemporalCaptureContract.ExpectedFrameCount)
        {
            throw new InvalidDataException(
                $"Expected {SampleSponzaTemporalCaptureContract.ExpectedFrameCount} " +
                $"frame records, found {manifest.Frames.Count}.");
        }
        if (!File.Exists(Path.Combine(
                bundleDirectory,
                SampleSponzaTemporalCaptureContract.ContractFileName)))
        {
            throw new FileNotFoundException(
                "The temporal capture contract is missing.");
        }

        ulong previousSerial = 0;
        int ordinal = 0;
        foreach (string route in new[]
                 {
                     SampleSponzaTemporalCaptureContract.HorizontalRoute,
                     SampleSponzaTemporalCaptureContract.VerticalRoute
                 })
        {
            int expectedRouteCount =
                SampleSponzaTemporalCaptureContract.GetRouteFrameCount(route);
            SampleSponzaTemporalFrameArtifact[] routeFrames = manifest.Frames
                .Where(frame => string.Equals(
                    frame.Route,
                    route,
                    StringComparison.Ordinal))
                .OrderBy(static frame => frame.RouteFrameIndex)
                .ToArray();
            if (routeFrames.Length != expectedRouteCount)
            {
                throw new InvalidDataException(
                    $"Route '{route}' expected {expectedRouteCount} frames, " +
                    $"found {routeFrames.Length}.");
            }
            if (!File.Exists(ResolveBundlePath(
                    bundleDirectory,
                    SampleSponzaTemporalCaptureContract
                        .GetTraceRelativePath(route))))
            {
                throw new FileNotFoundException(
                    $"Route '{route}' temporal trace is missing.");
            }

            for (int index = 0; index < routeFrames.Length; index++)
            {
                SampleSponzaTemporalFrameArtifact frame = routeFrames[index];
                string expectedPath =
                    SampleSponzaTemporalCaptureContract.GetFrameRelativePath(
                        route,
                        index);
                if (frame.CaptureOrdinal != ordinal ||
                    frame.RouteFrameIndex != index ||
                    !string.Equals(
                        frame.RelativePath.Replace('\\', '/'),
                        expectedPath,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Frame mapping is not contiguous at capture ordinal {ordinal}.");
                }
                if (ordinal > 0 && frame.RendererFrameSerial <= previousSerial)
                {
                    throw new InvalidDataException(
                        $"Renderer frame serial is not increasing at capture ordinal {ordinal}.");
                }
                if (frame.ByteLength <= 0 ||
                    string.IsNullOrWhiteSpace(frame.Sha256))
                {
                    throw new InvalidDataException(
                        $"Frame {expectedPath} has no verified file identity.");
                }

                ValidateExpectedCamera(frame);
                previousSerial = frame.RendererFrameSerial;
                ordinal++;
            }
        }
    }

    private static void ValidateExpectedCamera(
        SampleSponzaTemporalFrameArtifact frame)
    {
        SampleSponzaGiCaptureContract contract =
            SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiCameraBookmark expected = frame.Route switch
        {
            SampleSponzaTemporalCaptureContract.HorizontalRoute =>
                contract.SampleMotionTraversalFrame(frame.RouteFrameIndex),
            SampleSponzaTemporalCaptureContract.VerticalRoute =>
                contract.SampleVerticalTraversalFrame(frame.RouteFrameIndex),
            _ => throw new InvalidDataException(
                $"Unknown route '{frame.Route}'.")
        };
        const float tolerance = 0.0001f;
        if (MathF.Abs(frame.CameraPositionX - expected.Position.X) > tolerance ||
            MathF.Abs(frame.CameraPositionY - expected.Position.Y) > tolerance ||
            MathF.Abs(frame.CameraPositionZ - expected.Position.Z) > tolerance ||
            MathF.Abs(frame.CameraYaw - expected.Yaw) > tolerance ||
            MathF.Abs(frame.CameraPitch - expected.Pitch) > tolerance ||
            MathF.Abs(frame.CameraFieldOfView - expected.FieldOfView) > tolerance ||
            MathF.Abs(frame.CameraNearPlane - expected.NearPlane) > tolerance ||
            MathF.Abs(frame.CameraFarPlane - expected.FarPlane) > tolerance)
        {
            throw new InvalidDataException(
                $"Frame {frame.RelativePath} camera does not match the locked route.");
        }
        if (string.IsNullOrWhiteSpace(frame.ViewHash) ||
            string.IsNullOrWhiteSpace(frame.ProjectionHash))
        {
            throw new InvalidDataException(
                $"Frame {frame.RelativePath} is missing camera matrix hashes.");
        }
    }

    private static DecodedImage LoadValidatedFrame(
        string bundleDirectory,
        SampleSponzaTemporalFrameArtifact frame)
    {
        string path = ResolveBundlePath(bundleDirectory, frame.RelativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Temporal frame is missing: {frame.RelativePath}", path);
        var info = new FileInfo(path);
        if (info.Length != frame.ByteLength)
        {
            throw new InvalidDataException(
                $"Temporal frame length changed: {frame.RelativePath}.");
        }
        using (FileStream hashStream = File.OpenRead(path))
        {
            string actualHash = "sha256:" + Convert.ToHexString(
                SHA256.HashData(hashStream)).ToLowerInvariant();
            if (!string.Equals(
                    actualHash,
                    frame.Sha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Temporal frame hash changed: {frame.RelativePath}.");
            }
        }

        byte[] encoded = File.ReadAllBytes(path);
        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(
                encoded,
                ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception exception)
        {
            throw new InvalidDataException(
                $"Temporal frame is not a decodable PNG: {frame.RelativePath}.",
                exception);
        }
        if (image.Width != SampleSponzaTemporalCaptureContract.Width ||
            image.Height != SampleSponzaTemporalCaptureContract.Height ||
            image.Data.Length != checked(image.Width * image.Height * 4))
        {
            throw new InvalidDataException(
                $"Temporal frame has unexpected dimensions: " +
                $"{frame.RelativePath} ({image.Width}x{image.Height}).");
        }

        return new DecodedImage(image.Width, image.Height, image.Data);
    }

    private static byte[] ResizeBox(
        DecodedImage source,
        int targetWidth,
        int targetHeight)
    {
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Min(
                source.Height - 1,
                (int)(((long)y * 2L + 1L) * source.Height /
                    (targetHeight * 2L)));
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Min(
                    source.Width - 1,
                    (int)(((long)x * 2L + 1L) * source.Width /
                        (targetWidth * 2L)));
                int sourceOffset =
                    (sourceY * source.Width + sourceX) * 4;
                int targetOffset = (y * targetWidth + x) * 4;
                target[targetOffset] = source.Pixels[sourceOffset];
                target[targetOffset + 1] = source.Pixels[sourceOffset + 1];
                target[targetOffset + 2] = source.Pixels[sourceOffset + 2];
                target[targetOffset + 3] = source.Pixels[sourceOffset + 3];
            }
        }

        return target;
    }

    private static byte[] ResizeDifferenceBox(
        DecodedImage previous,
        DecodedImage current,
        int targetWidth,
        int targetHeight,
        int amplification)
    {
        EnsureSameDimensions(previous, current);
        var target = new byte[checked(targetWidth * targetHeight * 4)];
        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY0 = y * current.Height / targetHeight;
            int sourceY1 = Math.Max(
                sourceY0 + 1,
                (y + 1) * current.Height / targetHeight);
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX0 = x * current.Width / targetWidth;
                int sourceX1 = Math.Max(
                    sourceX0 + 1,
                    (x + 1) * current.Width / targetWidth);
                long red = 0;
                long green = 0;
                long blue = 0;
                int count = 0;
                for (int sourceY = sourceY0; sourceY < sourceY1; sourceY++)
                {
                    for (int sourceX = sourceX0; sourceX < sourceX1; sourceX++)
                    {
                        int offset =
                            (sourceY * current.Width + sourceX) * 4;
                        red += Math.Abs(
                            current.Pixels[offset] - previous.Pixels[offset]);
                        green += Math.Abs(
                            current.Pixels[offset + 1] - previous.Pixels[offset + 1]);
                        blue += Math.Abs(
                            current.Pixels[offset + 2] - previous.Pixels[offset + 2]);
                        count++;
                    }
                }

                int targetOffset = (y * targetWidth + x) * 4;
                target[targetOffset] = (byte)Math.Min(
                    255, red * amplification / count);
                target[targetOffset + 1] = (byte)Math.Min(
                    255, green * amplification / count);
                target[targetOffset + 2] = (byte)Math.Min(
                    255, blue * amplification / count);
                target[targetOffset + 3] = 255;
            }
        }

        return target;
    }

    private static void CopyRgba(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        byte[] destination,
        int destinationWidth,
        int destinationX,
        int destinationY)
    {
        int sourceStride = checked(sourceWidth * 4);
        int destinationStride = checked(destinationWidth * 4);
        for (int row = 0; row < sourceHeight; row++)
        {
            Buffer.BlockCopy(
                source,
                row * sourceStride,
                destination,
                (destinationY + row) * destinationStride +
                destinationX * 4,
                sourceStride);
        }
    }

    private static byte[] CreateOpaqueImage(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (int index = 3; index < pixels.Length; index += 4)
            pixels[index] = 255;
        return pixels;
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) * 0.5
            : values[middle];
    }

    private static void EnsureSameDimensions(
        DecodedImage left,
        DecodedImage right)
    {
        if (left.Width != right.Width || left.Height != right.Height)
            throw new InvalidDataException("Adjacent temporal frames differ in size.");
    }

    private static string ResolveBundlePath(
        string bundleDirectory,
        string relativePath)
    {
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(bundleDirectory, normalized));
        string prefix = bundleDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? bundleDirectory
            : bundleDirectory + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Temporal artifact escapes the bundle: {relativePath}");
        }
        return path;
    }

    private static void ReplaceAnalysisDirectory(
        string bundleDirectory,
        string temporaryDirectory,
        string analysisDirectory)
    {
        string backupDirectory = Path.Combine(
            bundleDirectory,
            $".analysis.{Guid.NewGuid():N}.old");
        bool movedExisting = false;
        try
        {
            if (Directory.Exists(analysisDirectory))
            {
                Directory.Move(analysisDirectory, backupDirectory);
                movedExisting = true;
            }
            Directory.Move(temporaryDirectory, analysisDirectory);
            if (movedExisting)
                Directory.Delete(backupDirectory, recursive: true);
        }
        catch
        {
            if (!Directory.Exists(analysisDirectory) &&
                movedExisting && Directory.Exists(backupDirectory))
            {
                Directory.Move(backupDirectory, analysisDirectory);
            }
            throw;
        }
    }

    private static SampleSponzaTemporalRunManifest ReadManifest(
        string outputDirectory)
    {
        string path = Path.Combine(
            outputDirectory,
            SampleSponzaTemporalCaptureContract.RunFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Sponza temporal run manifest is missing.", path);
        SampleSponzaTemporalRunManifest? manifest =
            JsonSerializer.Deserialize<SampleSponzaTemporalRunManifest>(
                File.ReadAllBytes(path),
                SampleSponzaTemporalCaptureContract.CreateJsonOptions());
        return manifest ?? throw new InvalidDataException(
            "Sponza temporal run manifest is empty.");
    }

    private static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private sealed record DecodedImage(int Width, int Height, byte[] Pixels);
}
