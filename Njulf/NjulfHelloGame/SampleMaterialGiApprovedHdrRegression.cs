using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using FlipBinding.CSharp;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

public enum SampleMaterialGiVisualRoiGateKind : byte
{
    Unknown = 0,
    UniformLuminance = 1,
    TransitionStep = 2,
    // Wire value 3 was used by the v2 radiance-threshold proxy. It is kept
    // reserved so stale numeric manifests fail with a precise diagnostic;
    // binary visibility is qualified by the standalone Vulkan gate instead.
    LegacyRadianceThresholdAlphaProxy = 3,
    LowFrequencyMean = 4,
    TemporalStability = 5
}

public enum SampleMaterialGiTemporalApplicability : byte
{
    Unknown = 0,
    Required = 1,
    NotApplicable = 2
}

/// <summary>
/// One fixed visual gate within a named ROI. Nullable fields are intentional:
/// the strict manifest validator can distinguish absent metadata from a valid
/// zero-valued coordinate or warmup count.
/// </summary>
public sealed record SampleMaterialGiApprovedRoiGate(
    SampleMaterialGiVisualRoiGateKind Kind,
    double? MaximumRelativeDifference,
    SampleMaterialGiCaptureSignal? Signal,
    SampleMaterialGiCaptureSignal? ComparisonSignal = null,
    IReadOnlyList<SampleMaterialGiPixelRegion>? TransitionSamples = null,
    double? CoverageThreshold = null,
    IReadOnlyList<string>? TemporalFrameRelativePaths = null,
    int? TemporalWarmupFrameCount = null);

public sealed record SampleMaterialGiApprovedRoi(
    string Name,
    SampleMaterialGiPixelRegion Bounds,
    IReadOnlyList<SampleMaterialGiApprovedRoiGate> Gates);

public sealed record SampleMaterialGiVisualApproval(
    string ApprovalId,
    string Reviewer,
    DateTimeOffset ApprovedAtUtc,
    string Reason);

public sealed record SampleMaterialGiTemporalPolicy(
    SampleMaterialGiTemporalApplicability Applicability,
    string Reason);

/// <summary>
/// Exact, reviewed NVIDIA HDR-FLIP configuration. String-valued automatic
/// exposures keep the JSON standards-compliant while making it explicit that
/// FLIP derives the exposure range and count from the pinned reference.
/// </summary>
public sealed record SampleMaterialGiHdrFlipConfiguration(
    string NvidiaFlipVersion,
    string NvidiaSourceRevision,
    string BindingPackage,
    string BindingVersion,
    double PixelsPerDegree,
    string ToneMapper,
    string StartExposure,
    string StopExposure,
    string NumberOfExposures);

/// <summary>
/// Reviewed, immutable input to the HDR gate. The manifest pins the complete
/// reference capture manifest by SHA-256 and supplies every screen-space ROI
/// needed to interpret material/cascade-specific metrics.
/// </summary>
public sealed record SampleMaterialGiApprovedHdrReferenceManifest(
    string SchemaVersion,
    string Status,
    string ContractFingerprint,
    string MetricVersion,
    SampleMaterialGiHdrFlipConfiguration FlipConfiguration,
    SampleMaterialGiVisualApproval Approval,
    string ReferenceCaptureManifestRelativePath,
    string ReferenceCaptureManifestSha256,
    int? Width,
    int? Height,
    double? MaximumRelativeRmse,
    double? MaximumFlipP95,
    IReadOnlyList<SampleMaterialGiCaptureSignal> GlobalSignals,
    IReadOnlyList<SampleMaterialGiApprovedRoi> Rois,
    SampleMaterialGiTemporalPolicy TemporalPolicy);

public sealed record SampleMaterialGiApprovedHdrImageResult(
    SampleMaterialGiCaptureSignal Signal,
    string ReferenceRelativePath,
    string CandidateRelativePath,
    string ReferenceSha256,
    string CandidateSha256,
    long ComponentCount,
    double ReferenceRms,
    double AbsoluteRmse,
    double RelativeRmse,
    double FlipP95,
    double MaximumRelativeRmse,
    double MaximumFlipP95,
    bool Passed);

public sealed record SampleMaterialGiApprovedRoiGateResult(
    string Roi,
    SampleMaterialGiVisualRoiGateKind Kind,
    SampleMaterialGiCaptureSignal Signal,
    SampleMaterialGiCaptureSignal? ComparisonSignal,
    double? ReferenceValue,
    double? CandidateValue,
    double MeasuredRelativeDifference,
    double MaximumRelativeDifference,
    string ComparisonDefinition,
    long SampleCount,
    IReadOnlyList<string> EvidenceSha256,
    bool Passed);

public sealed record SampleMaterialGiApprovedHdrRegressionReport(
    string SchemaVersion,
    string Status,
    string FailureReason,
    DateTimeOffset ComparedAtUtc,
    string ApprovedReferenceManifestPath,
    string ApprovedReferenceManifestSha256,
    string ReferenceCaptureManifestPath,
    string ReferenceCaptureManifestSha256,
    string CandidateDirectory,
    string CandidateCaptureManifestSha256,
    string ContractFingerprint,
    string MetricVersion,
    SampleMaterialGiHdrFlipConfiguration FlipConfiguration,
    string RelativeRmseDefinition,
    string FlipMetricDefinition,
    SampleMaterialGiVisualApproval? Approval,
    IReadOnlyList<SampleMaterialGiApprovedHdrImageResult> Images,
    IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> RoiGates)
{
    [JsonIgnore]
    public bool Passed => string.Equals(Status, "passed", StringComparison.Ordinal);

    [JsonPropertyName("producerIdentity")]
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }
}

/// <summary>
/// Exact NVIDIA HDR-FLIP v1.7 evaluation through the pinned native reference
/// implementation. Inputs are scene-linear RGB; HDR-FLIP performs its own
/// reference-derived exposure sweep and ACES tone mapping before LDR-FLIP.
/// </summary>
public static class SampleMaterialGiHdrFlipMetric
{
    public const string MetricVersion = "nvidia-hdr-flip/v1.7";
    public const string NvidiaSourceRevision = "b475eb4";
    public const string BindingPackage = "FlipBinding.CSharp";
    public const string BindingVersion = "1.0.3";
    public const double PixelsPerDegree = 67.0206451;
    public const string ToneMapper = "aces";
    public const string AutomaticExposure = "reference-auto";
    public const string AutomaticExposureCount = "reference-auto";
    public const string Definition =
        "Nearest-rank P95 of the NVIDIA HDR-FLIP v1.7 per-pixel error map; " +
        "scene-linear RGB, PPD=67.0206451, ACES, reference-auto start/stop/count exposures, " +
        "source b475eb4 via FlipBinding.CSharp 1.0.3.";

    public static SampleMaterialGiHdrFlipConfiguration FixedConfiguration { get; } =
        new(
            "1.7",
            NvidiaSourceRevision,
            BindingPackage,
            BindingVersion,
            PixelsPerDegree,
            ToneMapper,
            AutomaticExposure,
            AutomaticExposure,
            AutomaticExposureCount);

    internal static double ComputeP95(
        LinearFloatImage reference,
        LinearFloatImage candidate)
    {
        ValidateCompatibleImages(reference, candidate, "HDR-FLIP comparison");
        bool hasPositiveReferenceRadiance = false;
        for (int component = 0; component < reference.Pixels.Length; component++)
        {
            float referenceValue = reference.Pixels[component];
            float candidateValue = candidate.Pixels[component];
            if (!float.IsFinite(referenceValue) || !float.IsFinite(candidateValue))
            {
                throw new InvalidDataException(
                    $"HDR-FLIP comparison contains a non-finite component at index {component}.");
            }
            if (referenceValue < 0.0f || candidateValue < 0.0f)
            {
                throw new InvalidDataException(
                    $"HDR-FLIP comparison contains negative radiance at component {component}.");
            }
            hasPositiveReferenceRadiance |= referenceValue > 0.0f;
        }
        if (!hasPositiveReferenceRadiance)
        {
            throw new InvalidDataException(
                "HDR-FLIP reference has no positive radiance for its automatic exposure range.");
        }
        ValidateNativePlatform();

        FlipResult result;
        try
        {
            result = Flip.Evaluate(
                reference.Pixels,
                candidate.Pixels,
                reference.Width,
                reference.Height,
                useHdr: true,
                ppd: (float)PixelsPerDegree,
                tonemapper: Tonemapper.Aces,
                startExposure: float.PositiveInfinity,
                stopExposure: float.PositiveInfinity,
                numExposures: -1,
                applyMagmaMap: false);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException or
            TypeInitializationException)
        {
            throw new InvalidOperationException(
                $"NVIDIA HDR-FLIP v1.7 native runtime is unavailable or incompatible on " +
                $"{RuntimeInformation.OSDescription}/{RuntimeInformation.ProcessArchitecture}.",
                exception);
        }

        int expectedPixels = checked(reference.Width * reference.Height);
        if (result.IsMagmaMap ||
            result.Width != reference.Width ||
            result.Height != reference.Height ||
            result.ErrorMap == null ||
            result.ErrorMap.Length != expectedPixels)
        {
            throw new InvalidDataException(
                "NVIDIA HDR-FLIP returned an absent or incompatible scalar error map.");
        }

        var errors = new double[expectedPixels];
        for (int index = 0; index < errors.Length; index++)
        {
            float error = result.ErrorMap[index];
            if (!float.IsFinite(error) || error < 0.0f || error > 1.0f)
            {
                throw new InvalidDataException(
                    $"NVIDIA HDR-FLIP returned invalid error {error:R} at pixel {index}.");
            }
            errors[index] = error;
        }
        return SampleMaterialGiApprovedHdrComparer.PercentileInPlace(errors, 0.95);
    }

    internal static void ValidateCompatibleImages(
        LinearFloatImage reference,
        LinearFloatImage candidate,
        string role)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        if (reference.Width <= 0 ||
            reference.Height <= 0 ||
            reference.Width != candidate.Width ||
            reference.Height != candidate.Height)
        {
            throw new InvalidDataException(
                $"{role} has incompatible dimensions " +
                $"{reference.Width}x{reference.Height} and {candidate.Width}x{candidate.Height}.");
        }
        int expectedComponents = checked(reference.Width * reference.Height * 3);
        if (reference.Pixels.Length != expectedComponents ||
            candidate.Pixels.Length != expectedComponents)
        {
            throw new InvalidDataException($"{role} has an invalid RGB payload length.");
        }
    }

    private static void ValidateNativePlatform()
    {
        Architecture architecture = RuntimeInformation.ProcessArchitecture;
        bool supported =
            OperatingSystem.IsWindows() && architecture == Architecture.X64 ||
            OperatingSystem.IsLinux() && architecture == Architecture.X64 ||
            OperatingSystem.IsMacOS() &&
            (architecture == Architecture.X64 || architecture == Architecture.Arm64);
        if (!supported)
        {
            throw new PlatformNotSupportedException(
                $"NVIDIA HDR-FLIP v1.7 has no pinned native runtime for " +
                $"{RuntimeInformation.OSDescription}/{architecture}.");
        }
    }
}

/// <summary>
/// Offline approved-reference visual gate. All input validation failures become
/// failed machine-readable reports; only CLI usage or report-I/O failures use
/// the command-error exit code.
/// </summary>
public static class SampleMaterialGiApprovedHdrComparer
{
    public const string ManifestSchemaVersion = "material-gi-approved-hdr-reference/v3";
    public const string ReportSchemaVersion = "material-gi-approved-hdr-regression/v4";
    public const string DefaultReportFileName = "material-gi-approved-hdr-regression.json";
    public const double MaximumRelativeRmse = 0.12;
    public const double MaximumFlipP95 = 0.08;
    public const double MaximumUniformLuminanceDifference = 0.05;
    public const double MaximumTransitionStep = 0.10;
    public const double MaximumLowFrequencyMeanDifference = 0.02;
    public const double MaximumTemporalP95 = 0.03;
    public const int MaximumTemporalFrameCount = 120;
    public const long MaximumTemporalSampleBufferBytes = 256L * 1024L * 1024L;
    public const long MaximumTemporalSampleCount =
        MaximumTemporalSampleBufferBytes / sizeof(float);
    public const string RelativeRmseDefinition =
        "sqrt(mean((candidate-reference)^2)) / max(sqrt(mean(reference^2)), 1e-6 linear-radiance units)";
    public const string TemporalMetricDefinition =
        "Nearest-rank P95 over ROI pixels x post-warmup frames of " +
        "abs(pixelLuminance-perPixelTemporalMedian) / " +
        "max(abs(perPixelTemporalMedian), 1e-6).";

    private const double RelativeFloor = 1.0e-6;
    private const double FixedThresholdTolerance = 1.0e-12;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 64,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SampleMaterialGiApprovedHdrRegressionReport Compare(
        string approvedReferenceManifestPath,
        string candidateDirectory)
    {
        string approvedPath = NormalizeFilePath(
            approvedReferenceManifestPath,
            nameof(approvedReferenceManifestPath));
        string candidate = NormalizeDirectory(candidateDirectory, nameof(candidateDirectory));
        string approvedHash = TryComputeSha256(approvedPath);
        string candidateManifestPath =
            Path.Combine(candidate, SampleMaterialGiArtifactPublisher.ManifestFileName);
        string candidateManifestHash = TryComputeSha256(candidateManifestPath);
        string referenceManifestPath = string.Empty;
        string referenceManifestHash = string.Empty;
        SampleMaterialGiApprovedHdrReferenceManifest? approvedManifest = null;

        try
        {
            approvedManifest = LoadApprovedManifest(
                approvedPath,
                out approvedHash);
            string approvedDirectory = Path.GetDirectoryName(approvedPath)
                ?? throw new InvalidDataException("The approved-reference manifest has no parent directory.");
            referenceManifestPath = ResolveContainedPath(
                approvedDirectory,
                approvedManifest.ReferenceCaptureManifestRelativePath,
                "reference capture manifest");
            string referenceDirectory = Path.GetDirectoryName(referenceManifestPath)
                ?? throw new InvalidDataException("The reference capture manifest has no parent directory.");
            SampleMaterialGiRunManifest referenceManifest =
                SampleMaterialGiCaptureComparer.LoadPassedManifest(
                    referenceManifestPath,
                    referenceDirectory,
                    "approved HDR reference",
                    out referenceManifestHash);
            if (!string.Equals(
                    referenceManifestHash,
                    approvedManifest.ReferenceCaptureManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The approved reference capture manifest SHA-256 does not match the reviewed identity.");
            }
            SampleMaterialGiRunManifest candidateManifest =
                SampleMaterialGiCaptureComparer.LoadPassedManifest(
                    candidateManifestPath,
                    candidate,
                    "HDR candidate",
                    out candidateManifestHash);
            ValidateCaptureCompatibility(
                approvedManifest,
                referenceManifest,
                candidateManifest);

            var referenceImages = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>();
            var candidateImages = new Dictionary<SampleMaterialGiCaptureSignal, LinearFloatImage>();
            var referenceArtifacts = new Dictionary<SampleMaterialGiCaptureSignal, SampleMaterialGiArtifact>();
            var candidateArtifacts = new Dictionary<SampleMaterialGiCaptureSignal, SampleMaterialGiArtifact>();

            LinearFloatImage LoadReference(SampleMaterialGiCaptureSignal signal)
            {
                if (referenceImages.TryGetValue(signal, out LinearFloatImage? cached))
                    return cached;
                SampleMaterialGiCaptureOutput output = GetCaptureOutput(signal);
                SampleMaterialGiArtifact artifact =
                    SampleMaterialGiCaptureComparer.GetArtifact(
                        referenceManifest,
                        output,
                        "approved HDR reference");
                LinearFloatImage image =
                    SampleMaterialGiCaptureComparer.LoadVerifiedImage(
                        referenceDirectory,
                        artifact,
                        "approved HDR reference");
                referenceArtifacts.Add(signal, artifact);
                referenceImages.Add(signal, image);
                return image;
            }

            LinearFloatImage LoadCandidate(SampleMaterialGiCaptureSignal signal)
            {
                if (candidateImages.TryGetValue(signal, out LinearFloatImage? cached))
                    return cached;
                SampleMaterialGiCaptureOutput output = GetCaptureOutput(signal);
                SampleMaterialGiArtifact artifact =
                    SampleMaterialGiCaptureComparer.GetArtifact(
                        candidateManifest,
                        output,
                        "HDR candidate");
                LinearFloatImage image =
                    SampleMaterialGiCaptureComparer.LoadVerifiedImage(
                        candidate,
                        artifact,
                        "HDR candidate");
                candidateArtifacts.Add(signal, artifact);
                candidateImages.Add(signal, image);
                return image;
            }

            var imageResults =
                new List<SampleMaterialGiApprovedHdrImageResult>(
                    approvedManifest.GlobalSignals.Count);
            foreach (SampleMaterialGiCaptureSignal signal in approvedManifest.GlobalSignals)
            {
                LinearFloatImage referenceImage = LoadReference(signal);
                LinearFloatImage candidateImage = LoadCandidate(signal);
                SampleMaterialGiArtifact referenceArtifact = referenceArtifacts[signal];
                SampleMaterialGiArtifact candidateArtifact = candidateArtifacts[signal];
                imageResults.Add(
                    CompareImages(
                        signal,
                        referenceArtifact,
                        candidateArtifact,
                        referenceImage,
                        candidateImage));
            }

            foreach (SampleMaterialGiApprovedRoi roi in approvedManifest.Rois)
            {
                foreach (SampleMaterialGiApprovedRoiGate gate in roi.Gates)
                {
                    LoadReference(gate.Signal!.Value);
                    LoadCandidate(gate.Signal.Value);
                    if (gate.ComparisonSignal.HasValue)
                    {
                        LoadReference(gate.ComparisonSignal.Value);
                        LoadCandidate(gate.ComparisonSignal.Value);
                    }
                }
            }

            var referenceHashes = referenceArtifacts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Sha256);
            var candidateHashes = candidateArtifacts.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.Sha256);
            IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> roiResults =
                EvaluateRoiGates(
                    approvedManifest,
                    referenceImages,
                    candidateImages,
                    relativePath => LoadTemporalImage(candidate, relativePath),
                    referenceHashes,
                    candidateHashes);

            SampleMaterialGiApprovedHdrImageResult[] failedImages =
                imageResults.Where(static result => !result.Passed).ToArray();
            SampleMaterialGiApprovedRoiGateResult[] failedRois =
                roiResults.Where(static result => !result.Passed).ToArray();
            bool passed = failedImages.Length == 0 && failedRois.Length == 0;
            string failureReason = passed
                ? string.Empty
                : BuildMetricFailureReason(failedImages, failedRois);
            MaterialGiProducerIdentity producerIdentity =
                SampleMaterialGiProducerIdentityFactory.Create(
                    candidateManifest.Renderer!);
            return CreateReport(
                passed ? "passed" : "failed",
                failureReason,
                approvedPath,
                approvedHash,
                referenceManifestPath,
                referenceManifestHash,
                candidate,
                candidateManifestHash,
                approvedManifest,
                imageResults,
                roiResults) with
            {
                ProducerIdentity = producerIdentity
            };
        }
        catch (Exception exception)
        {
            return CreateReport(
                "failed",
                $"Approved HDR input validation failed: {DescribeException(exception)}",
                approvedPath,
                approvedHash,
                referenceManifestPath,
                referenceManifestHash,
                candidate,
                candidateManifestHash,
                approvedManifest,
                Array.Empty<SampleMaterialGiApprovedHdrImageResult>(),
                Array.Empty<SampleMaterialGiApprovedRoiGateResult>());
        }
    }

    public static void WriteReportAtomic(
        string reportPath,
        SampleMaterialGiApprovedHdrRegressionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        WriteJsonAtomic(reportPath, report, "approved HDR regression report");
    }

    public static void WriteApprovedManifestAtomic(
        string manifestPath,
        SampleMaterialGiApprovedHdrReferenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ValidateApprovedManifest(manifest);
        WriteJsonAtomic(manifestPath, manifest, "approved HDR reference manifest");
    }

    internal static SampleMaterialGiApprovedHdrImageResult CompareImages(
        SampleMaterialGiCaptureSignal signal,
        SampleMaterialGiArtifact referenceArtifact,
        SampleMaterialGiArtifact candidateArtifact,
        LinearFloatImage reference,
        LinearFloatImage candidate)
    {
        ArgumentNullException.ThrowIfNull(referenceArtifact);
        ArgumentNullException.ThrowIfNull(candidateArtifact);
        SampleMaterialGiHdrFlipMetric.ValidateCompatibleImages(
            reference,
            candidate,
            $"Signal '{signal}'");

        double squaredError = 0.0;
        double squaredReference = 0.0;
        for (int component = 0; component < reference.Pixels.Length; component++)
        {
            double referenceValue = reference.Pixels[component];
            double candidateValue = candidate.Pixels[component];
            if (!double.IsFinite(referenceValue) || !double.IsFinite(candidateValue))
            {
                throw new InvalidDataException(
                    $"Signal '{signal}' contains a non-finite component at index {component}.");
            }
            double difference = candidateValue - referenceValue;
            squaredError += difference * difference;
            squaredReference += referenceValue * referenceValue;
        }

        double componentCount = reference.Pixels.Length;
        double absoluteRmse = Math.Sqrt(squaredError / componentCount);
        double referenceRms = Math.Sqrt(squaredReference / componentCount);
        double relativeRmse = absoluteRmse <= 0.0 && referenceRms <= 0.0
            ? 0.0
            : absoluteRmse / Math.Max(referenceRms, RelativeFloor);
        double flipP95 =
            SampleMaterialGiHdrFlipMetric.ComputeP95(reference, candidate);
        bool passed =
            relativeRmse <= MaximumRelativeRmse &&
            flipP95 <= MaximumFlipP95;
        return new SampleMaterialGiApprovedHdrImageResult(
            signal,
            referenceArtifact.RelativePath,
            candidateArtifact.RelativePath,
            referenceArtifact.Sha256,
            candidateArtifact.Sha256,
            reference.Pixels.LongLength,
            referenceRms,
            absoluteRmse,
            relativeRmse,
            flipP95,
            MaximumRelativeRmse,
            MaximumFlipP95,
            passed);
    }

    internal static IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> EvaluateRoiGates(
        SampleMaterialGiApprovedHdrReferenceManifest manifest,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, LinearFloatImage> referenceImages,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, LinearFloatImage> candidateImages,
        Func<string, (LinearFloatImage Image, string Sha256)> temporalImageLoader,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, string>? referenceHashes = null,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, string>? candidateHashes = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(referenceImages);
        ArgumentNullException.ThrowIfNull(candidateImages);
        ArgumentNullException.ThrowIfNull(temporalImageLoader);
        var results = new List<SampleMaterialGiApprovedRoiGateResult>();

        foreach (SampleMaterialGiApprovedRoi roi in manifest.Rois)
        {
            foreach (SampleMaterialGiApprovedRoiGate gate in roi.Gates)
            {
                SampleMaterialGiCaptureSignal signal = gate.Signal!.Value;
                LinearFloatImage reference = RequireImage(referenceImages, signal, "reference");
                LinearFloatImage candidate = RequireImage(candidateImages, signal, "candidate");
                ValidateManifestExtent(manifest, reference, $"{roi.Name}/{gate.Kind} reference");
                SampleMaterialGiHdrFlipMetric.ValidateCompatibleImages(
                    reference,
                    candidate,
                    $"{roi.Name}/{gate.Kind}");
                string referenceHash = GetEvidenceHash(referenceHashes, signal);
                string candidateHash = GetEvidenceHash(candidateHashes, signal);

                results.Add(gate.Kind switch
                {
                    SampleMaterialGiVisualRoiGateKind.UniformLuminance =>
                        EvaluateUniformLuminance(
                            roi,
                            gate,
                            reference,
                            candidate,
                            referenceHash,
                            candidateHash),
                    SampleMaterialGiVisualRoiGateKind.TransitionStep =>
                        EvaluateTransitionStep(
                            roi,
                            gate,
                            reference,
                            candidate,
                            referenceHash,
                            candidateHash),
                    SampleMaterialGiVisualRoiGateKind.LegacyRadianceThresholdAlphaProxy =>
                        throw new InvalidDataException(
                            "Radiance-threshold alpha proxies are not release evidence. " +
                            "Use an authenticated material-gi-alpha-visibility Vulkan report."),
                    SampleMaterialGiVisualRoiGateKind.LowFrequencyMean =>
                        EvaluateLowFrequencyMean(
                            manifest,
                            roi,
                            gate,
                            referenceImages,
                            candidateImages,
                            referenceHashes,
                            candidateHashes),
                    SampleMaterialGiVisualRoiGateKind.TemporalStability =>
                        EvaluateTemporal(
                            manifest,
                            roi,
                            gate,
                            temporalImageLoader),
                    _ => throw new InvalidDataException(
                        $"ROI '{roi.Name}' has unsupported gate '{gate.Kind}'.")
                });
            }
        }

        return results;
    }

    internal static void ValidateApprovedManifest(
        SampleMaterialGiApprovedHdrReferenceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.SchemaVersion, ManifestSchemaVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"Approved HDR schema '{manifest.SchemaVersion}' is unsupported.");
        if (!string.Equals(manifest.Status, "approved", StringComparison.Ordinal))
            throw new InvalidDataException("Approved HDR manifest status must be exactly 'approved'.");
        if (!string.Equals(
                manifest.ContractFingerprint,
                SampleMaterialGiConformanceCatalog.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Approved HDR manifest has an incompatible contract fingerprint.");
        }
        if (!string.Equals(
                manifest.MetricVersion,
                SampleMaterialGiHdrFlipMetric.MetricVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Approved HDR manifest has an incompatible NVIDIA HDR-FLIP version.");
        }
        if (manifest.FlipConfiguration != SampleMaterialGiHdrFlipMetric.FixedConfiguration)
        {
            throw new InvalidDataException(
                "Approved HDR manifest has absent or incompatible NVIDIA HDR-FLIP parameters.");
        }
        ValidateApproval(manifest.Approval);
        if (string.IsNullOrWhiteSpace(manifest.ReferenceCaptureManifestRelativePath) ||
            Path.IsPathRooted(manifest.ReferenceCaptureManifestRelativePath))
        {
            throw new InvalidDataException(
                "Approved HDR reference capture manifest must be a non-empty relative path.");
        }
        RequireSha256(
            manifest.ReferenceCaptureManifestSha256,
            "approved reference capture manifest");
        if (manifest.Width != SampleMaterialGiConformanceCatalog.LockedWidth ||
            manifest.Height != SampleMaterialGiConformanceCatalog.LockedHeight)
        {
            throw new InvalidDataException(
                "Approved HDR manifest extent metadata is absent or incompatible with the locked capture.");
        }
        RequireFixedThreshold(
            manifest.MaximumRelativeRmse,
            MaximumRelativeRmse,
            nameof(manifest.MaximumRelativeRmse));
        RequireFixedThreshold(
            manifest.MaximumFlipP95,
            MaximumFlipP95,
            nameof(manifest.MaximumFlipP95));

        if (manifest.GlobalSignals == null ||
            manifest.GlobalSignals.Count == 0 ||
            manifest.GlobalSignals.Any(static signal => !Enum.IsDefined(signal)) ||
            manifest.GlobalSignals.Distinct().Count() != manifest.GlobalSignals.Count)
        {
            throw new InvalidDataException(
                "Approved HDR global signal metadata must be non-empty, defined, and unique.");
        }
        if (!manifest.GlobalSignals.Contains(SampleMaterialGiCaptureSignal.FinalComposedIndirect))
        {
            throw new InvalidDataException(
                "Approved HDR global signals must include FinalComposedIndirect.");
        }
        if (manifest.Rois == null || manifest.Rois.Count == 0)
            throw new InvalidDataException("Approved HDR reference has no named ROI metadata.");
        if (manifest.Rois.Any(static roi => roi == null))
            throw new InvalidDataException("Approved HDR reference contains a null ROI.");
        if (manifest.Rois.Select(static roi => roi.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Rois.Count)
        {
            throw new InvalidDataException("Approved HDR ROI names must be unique.");
        }

        var observedKinds = new HashSet<SampleMaterialGiVisualRoiGateKind>();
        foreach (SampleMaterialGiApprovedRoi roi in manifest.Rois)
        {
            if (string.IsNullOrWhiteSpace(roi.Name))
                throw new InvalidDataException("Every approved HDR ROI requires a stable name.");
            ValidateRegion(roi.Bounds, manifest.Width.Value, manifest.Height.Value, $"ROI '{roi.Name}'");
            if (roi.Gates == null || roi.Gates.Count == 0)
                throw new InvalidDataException($"ROI '{roi.Name}' has no gate metadata.");
            if (roi.Gates.Any(static gate => gate == null))
                throw new InvalidDataException($"ROI '{roi.Name}' contains a null gate.");
            if (roi.Gates.Select(static gate => gate.Kind).Distinct().Count() != roi.Gates.Count)
                throw new InvalidDataException($"ROI '{roi.Name}' repeats a gate kind.");
            foreach (SampleMaterialGiApprovedRoiGate gate in roi.Gates)
            {
                ValidateRoiGate(roi, gate, manifest.Width.Value, manifest.Height.Value);
                observedKinds.Add(gate.Kind);
            }
        }

        SampleMaterialGiVisualRoiGateKind[] alwaysRequired =
        [
            SampleMaterialGiVisualRoiGateKind.UniformLuminance,
            SampleMaterialGiVisualRoiGateKind.TransitionStep,
            SampleMaterialGiVisualRoiGateKind.LowFrequencyMean
        ];
        SampleMaterialGiVisualRoiGateKind[] missing =
            alwaysRequired.Where(kind => !observedKinds.Contains(kind)).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidDataException(
                "Approved HDR ROI metadata omits required gates: " +
                string.Join(", ", missing));
        }

        if (manifest.TemporalPolicy == null ||
            manifest.TemporalPolicy.Applicability == SampleMaterialGiTemporalApplicability.Unknown)
        {
            throw new InvalidDataException("Approved HDR temporal applicability metadata is absent.");
        }
        bool hasTemporal =
            observedKinds.Contains(SampleMaterialGiVisualRoiGateKind.TemporalStability);
        switch (manifest.TemporalPolicy.Applicability)
        {
            case SampleMaterialGiTemporalApplicability.Required when !hasTemporal:
                throw new InvalidDataException(
                    "Temporal stability is required but no named ROI supplies temporal evidence metadata.");
            case SampleMaterialGiTemporalApplicability.NotApplicable when hasTemporal:
                throw new InvalidDataException(
                    "Temporal stability is marked not applicable but temporal ROI metadata is present.");
            case SampleMaterialGiTemporalApplicability.NotApplicable
                when string.IsNullOrWhiteSpace(manifest.TemporalPolicy.Reason):
                throw new InvalidDataException(
                    "A not-applicable temporal policy requires a reviewed reason.");
        }
    }

    internal static void ValidateCaptureCompatibility(
        SampleMaterialGiApprovedHdrReferenceManifest approved,
        SampleMaterialGiRunManifest reference,
        SampleMaterialGiRunManifest candidate)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        SampleMaterialGiRendererProvenance referenceRenderer = reference.Renderer
            ?? throw new InvalidDataException("Approved HDR reference has no renderer provenance.");
        SampleMaterialGiRendererProvenance candidateRenderer = candidate.Renderer
            ?? throw new InvalidDataException("HDR candidate has no renderer provenance.");
        int approvedWidth = approved.Width
            ?? throw new InvalidDataException("Approved HDR width metadata is absent.");
        int approvedHeight = approved.Height
            ?? throw new InvalidDataException("Approved HDR height metadata is absent.");
        if (!string.Equals(
                reference.ContractFingerprint,
                approved.ContractFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                candidate.ContractFingerprint,
                approved.ContractFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Approved reference and candidate contract fingerprints differ.");
        }
        if (!string.Equals(
                referenceRenderer.BuildConfiguration,
                "Release",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                candidateRenderer.BuildConfiguration,
                "Release",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Approved HDR regression requires Release capture manifests.");
        }
        RequireCompatible(
            referenceRenderer.SettingsSchemaVersion,
            candidateRenderer.SettingsSchemaVersion,
            "settings schema");
        RequireCompatible(
            referenceRenderer.RenderWidth,
            candidateRenderer.RenderWidth,
            "render width");
        RequireCompatible(
            referenceRenderer.RenderHeight,
            candidateRenderer.RenderHeight,
            "render height");
        RequireCompatible(
            referenceRenderer.SceneContentRevision,
            candidateRenderer.SceneContentRevision,
            "scene content revision");
        RequireCompatible(
            referenceRenderer.QualityPreset,
            candidateRenderer.QualityPreset,
            "quality preset");
        RequireCompatible(
            referenceRenderer.ActiveMaterialGiV2Features,
            candidateRenderer.ActiveMaterialGiV2Features,
            "Material-GI V2 feature mask");
        RequireCompatible(
            referenceRenderer.GlobalIlluminationMode,
            candidateRenderer.GlobalIlluminationMode,
            "global illumination mode");
        RequireCompatible(referenceRenderer.Camera, candidateRenderer.Camera, "camera");
        if (referenceRenderer.RenderWidth != checked((uint)approvedWidth) ||
            referenceRenderer.RenderHeight != checked((uint)approvedHeight))
        {
            throw new InvalidDataException(
                "Approved ROI extent does not match the pinned reference capture.");
        }
    }

    private static SampleMaterialGiApprovedRoiGateResult EvaluateUniformLuminance(
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        LinearFloatImage reference,
        LinearFloatImage candidate,
        string referenceHash,
        string candidateHash)
    {
        double referenceMean = MeanLuminance(reference, roi.Bounds);
        double candidateMean = MeanLuminance(candidate, roi.Bounds);
        double difference = RelativeDifference(candidateMean, referenceMean);
        return CreateRoiResult(
            roi,
            gate,
            referenceMean,
            candidateMean,
            difference,
            "abs(candidateMeanLuminance-referenceMeanLuminance) / max(abs(referenceMeanLuminance), 1e-6)",
            PixelCount(roi.Bounds),
            [referenceHash, candidateHash],
            difference <= gate.MaximumRelativeDifference!.Value);
    }

    private static SampleMaterialGiApprovedRoiGateResult EvaluateTransitionStep(
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        LinearFloatImage reference,
        LinearFloatImage candidate,
        string referenceHash,
        string candidateHash)
    {
        IReadOnlyList<SampleMaterialGiPixelRegion> samples = gate.TransitionSamples!;
        double[] referenceMeans = samples.Select(region => MeanLuminance(reference, region)).ToArray();
        double[] candidateMeans = samples.Select(region => MeanLuminance(candidate, region)).ToArray();
        double referenceStep = MaximumAdjacentSymmetricDifference(referenceMeans);
        double candidateStep = MaximumAdjacentSymmetricDifference(candidateMeans);
        return CreateRoiResult(
            roi,
            gate,
            referenceStep,
            candidateStep,
            candidateStep,
            "max adjacent abs(meanB-meanA) / max((abs(meanA)+abs(meanB))/2, 1e-6)",
            samples.Sum(PixelCount),
            [referenceHash, candidateHash],
            candidateStep <= gate.MaximumRelativeDifference!.Value);
    }

    private static SampleMaterialGiApprovedRoiGateResult EvaluateLowFrequencyMean(
        SampleMaterialGiApprovedHdrReferenceManifest manifest,
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, LinearFloatImage> referenceImages,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, LinearFloatImage> candidateImages,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, string>? referenceHashes,
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, string>? candidateHashes)
    {
        SampleMaterialGiCaptureSignal composedSignal = gate.Signal!.Value;
        SampleMaterialGiCaptureSignal baselineSignal = gate.ComparisonSignal!.Value;
        LinearFloatImage referenceComposed =
            RequireImage(referenceImages, composedSignal, "reference");
        LinearFloatImage referenceBaseline =
            RequireImage(referenceImages, baselineSignal, "reference");
        LinearFloatImage candidateComposed =
            RequireImage(candidateImages, composedSignal, "candidate");
        LinearFloatImage candidateBaseline =
            RequireImage(candidateImages, baselineSignal, "candidate");
        ValidateManifestExtent(manifest, referenceBaseline, $"{roi.Name} reference DDGI");
        ValidateManifestExtent(manifest, candidateBaseline, $"{roi.Name} candidate DDGI");
        double referenceBaselineMean = MeanLuminance(referenceBaseline, roi.Bounds);
        double referenceComposedMean = MeanLuminance(referenceComposed, roi.Bounds);
        double candidateBaselineMean = MeanLuminance(candidateBaseline, roi.Bounds);
        double candidateComposedMean = MeanLuminance(candidateComposed, roi.Bounds);
        double referenceDifference =
            RelativeDifference(referenceComposedMean, referenceBaselineMean);
        double candidateDifference =
            RelativeDifference(candidateComposedMean, candidateBaselineMean);
        return CreateRoiResult(
            roi,
            gate,
            referenceDifference,
            candidateDifference,
            candidateDifference,
            "abs(composedMeanLuminance-ddgiMeanLuminance) / max(abs(ddgiMeanLuminance), 1e-6)",
            PixelCount(roi.Bounds),
            [
                GetEvidenceHash(referenceHashes, composedSignal),
                GetEvidenceHash(referenceHashes, baselineSignal),
                GetEvidenceHash(candidateHashes, composedSignal),
                GetEvidenceHash(candidateHashes, baselineSignal)
            ],
            candidateDifference <= gate.MaximumRelativeDifference!.Value);
    }

    private static SampleMaterialGiApprovedRoiGateResult EvaluateTemporal(
        SampleMaterialGiApprovedHdrReferenceManifest manifest,
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        Func<string, (LinearFloatImage Image, string Sha256)> temporalImageLoader)
    {
        IReadOnlyList<string> paths = gate.TemporalFrameRelativePaths!;
        int warmupFrameCount = gate.TemporalWarmupFrameCount!.Value;
        if (paths.Count > MaximumTemporalFrameCount)
        {
            throw new InvalidDataException(
                $"ROI '{roi.Name}' temporal evidence contains {paths.Count} frames; " +
                $"the bounded limit is {MaximumTemporalFrameCount}.");
        }

        int postWarmupFrameCount = paths.Count - warmupFrameCount;
        int pixelCount = checked((int)PixelCount(roi.Bounds));
        int sampleCount = GetTemporalSampleCount(
            roi.Bounds,
            postWarmupFrameCount,
            $"ROI '{roi.Name}' temporal evidence");
        var hashes = new string[paths.Count];
        float[] postWarmupLuminance =
            GC.AllocateUninitializedArray<float>(sampleCount);
        int postWarmupFrameIndex = 0;
        for (int frameIndex = 0; frameIndex < paths.Count; frameIndex++)
        {
            (LinearFloatImage image, string sha256) = temporalImageLoader(paths[frameIndex]);
            ValidateManifestExtent(manifest, image, $"{roi.Name} temporal frame {frameIndex}");
            hashes[frameIndex] = sha256;
            if (frameIndex < warmupFrameCount)
                continue;

            int destinationOffset = checked(postWarmupFrameIndex * pixelCount);
            CopyLuminance(
                image,
                roi.Bounds,
                postWarmupLuminance,
                destinationOffset);
            postWarmupFrameIndex++;
        }

        float[] pixelTimeline =
            GC.AllocateUninitializedArray<float>(postWarmupFrameCount);
        for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            for (int frameIndex = 0; frameIndex < postWarmupFrameCount; frameIndex++)
            {
                int sampleIndex = checked(frameIndex * pixelCount + pixelIndex);
                pixelTimeline[frameIndex] = postWarmupLuminance[sampleIndex];
            }

            float temporalMedian = MedianInPlace(pixelTimeline);
            for (int frameIndex = 0; frameIndex < postWarmupFrameCount; frameIndex++)
            {
                int sampleIndex = checked(frameIndex * pixelCount + pixelIndex);
                double variation =
                    RelativeDifference(postWarmupLuminance[sampleIndex], temporalMedian);
                postWarmupLuminance[sampleIndex] =
                    variation >= float.MaxValue
                        ? float.MaxValue
                        : (float)variation;
            }
        }

        double p95 = PercentileInPlace(postWarmupLuminance, 0.95);
        return CreateRoiResult(
            roi,
            gate,
            null,
            p95,
            p95,
            TemporalMetricDefinition,
            sampleCount,
            hashes,
            p95 < gate.MaximumRelativeDifference!.Value);
    }

    private static SampleMaterialGiApprovedRoiGateResult CreateRoiResult(
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        double? referenceValue,
        double? candidateValue,
        double measuredDifference,
        string definition,
        long sampleCount,
        IReadOnlyList<string> hashes,
        bool passed)
    {
        return new SampleMaterialGiApprovedRoiGateResult(
            roi.Name,
            gate.Kind,
            gate.Signal!.Value,
            gate.ComparisonSignal,
            referenceValue,
            candidateValue,
            measuredDifference,
            gate.MaximumRelativeDifference!.Value,
            definition,
            sampleCount,
            hashes.Where(static hash => !string.IsNullOrWhiteSpace(hash)).ToArray(),
            passed);
    }

    private static double MeanLuminance(
        LinearFloatImage image,
        SampleMaterialGiPixelRegion region)
    {
        double sum = 0.0;
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                int component = (y * image.Width + x) * 3;
                double red = RequireFinite(image.Pixels[component], x, y);
                double green = RequireFinite(image.Pixels[component + 1], x, y);
                double blue = RequireFinite(image.Pixels[component + 2], x, y);
                sum +=
                    Math.Max(red, 0.0) * 0.2126 +
                    Math.Max(green, 0.0) * 0.7152 +
                    Math.Max(blue, 0.0) * 0.0722;
            }
        }
        return sum / PixelCount(region);
    }

    private static void CopyLuminance(
        LinearFloatImage image,
        SampleMaterialGiPixelRegion region,
        float[] destination,
        int destinationOffset)
    {
        int destinationIndex = destinationOffset;
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            for (int x = region.X; x < region.X + region.Width; x++)
            {
                int component = (y * image.Width + x) * 3;
                double red = RequireFinite(image.Pixels[component], x, y);
                double green = RequireFinite(image.Pixels[component + 1], x, y);
                double blue = RequireFinite(image.Pixels[component + 2], x, y);
                double luminance =
                    Math.Max(red, 0.0) * 0.2126 +
                    Math.Max(green, 0.0) * 0.7152 +
                    Math.Max(blue, 0.0) * 0.0722;
                destination[destinationIndex++] = (float)luminance;
            }
        }

        int expectedEnd = checked(destinationOffset + (int)PixelCount(region));
        if (destinationIndex != expectedEnd || expectedEnd > destination.Length)
        {
            throw new InvalidDataException(
                "Temporal ROI luminance extraction exceeded its bounded sample buffer.");
        }
    }

    private static double MaximumAdjacentSymmetricDifference(double[] values)
    {
        double maximum = 0.0;
        for (int index = 1; index < values.Length; index++)
        {
            double denominator =
                Math.Max((Math.Abs(values[index - 1]) + Math.Abs(values[index])) * 0.5, RelativeFloor);
            maximum = Math.Max(
                maximum,
                Math.Abs(values[index] - values[index - 1]) / denominator);
        }
        return maximum;
    }

    private static double RelativeDifference(double value, double reference)
    {
        double difference = Math.Abs(value - reference);
        return difference <= 0.0 && Math.Abs(reference) <= 0.0
            ? 0.0
            : difference / Math.Max(Math.Abs(reference), RelativeFloor);
    }

    internal static double PercentileInPlace(double[] values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new InvalidDataException("A percentile requires at least one value.");
        if (!double.IsFinite(percentile) || percentile < 0.0 || percentile > 1.0)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        if (values.Any(static value => !double.IsFinite(value)))
            throw new InvalidDataException("A percentile input contains a non-finite value.");

        int target = Math.Clamp(
            (int)Math.Ceiling(percentile * values.Length) - 1,
            0,
            values.Length - 1);
        int left = 0;
        int right = values.Length - 1;
        while (left < right)
        {
            int pivotIndex = MedianOfThreeIndex(values, left, right);
            (int equalStart, int equalEnd) =
                PartitionThreeWay(values, left, right, values[pivotIndex]);
            if (target < equalStart)
                right = equalStart - 1;
            else if (target > equalEnd)
                left = equalEnd + 1;
            else
                break;
        }
        return values[target];
    }

    private static float PercentileInPlace(float[] values, double percentile)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new InvalidDataException("A percentile requires at least one value.");
        if (!double.IsFinite(percentile) || percentile < 0.0 || percentile > 1.0)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        if (values.Any(static value => !float.IsFinite(value)))
            throw new InvalidDataException("A percentile input contains a non-finite value.");

        int target = Math.Clamp(
            (int)Math.Ceiling(percentile * values.Length) - 1,
            0,
            values.Length - 1);
        return SelectInPlace(values, target);
    }

    private static float MedianInPlace(float[] values)
    {
        if (values.Length == 0)
            throw new InvalidDataException("A temporal median requires at least one value.");
        if (values.Any(static value => !float.IsFinite(value)))
            throw new InvalidDataException("A temporal median input contains a non-finite value.");

        int upperIndex = values.Length / 2;
        float upper = SelectInPlace(values, upperIndex);
        if ((values.Length & 1) != 0)
            return upper;

        float lower = values[0];
        for (int index = 1; index < upperIndex; index++)
            lower = Math.Max(lower, values[index]);
        return (float)(((double)lower + upper) * 0.5);
    }

    private static float SelectInPlace(float[] values, int target)
    {
        int left = 0;
        int right = values.Length - 1;
        while (left < right)
        {
            int pivotIndex = MedianOfThreeIndex(values, left, right);
            (int equalStart, int equalEnd) =
                PartitionThreeWay(values, left, right, values[pivotIndex]);
            if (target < equalStart)
                right = equalStart - 1;
            else if (target > equalEnd)
                left = equalEnd + 1;
            else
                break;
        }
        return values[target];
    }

    private static int MedianOfThreeIndex(double[] values, int left, int right)
    {
        int middle = left + (right - left) / 2;
        double a = values[left];
        double b = values[middle];
        double c = values[right];
        if (a < b)
            return b < c ? middle : a < c ? right : left;
        return a < c ? left : b < c ? right : middle;
    }

    private static int MedianOfThreeIndex(float[] values, int left, int right)
    {
        int middle = left + (right - left) / 2;
        float a = values[left];
        float b = values[middle];
        float c = values[right];
        if (a < b)
            return b < c ? middle : a < c ? right : left;
        return a < c ? left : b < c ? right : middle;
    }

    private static (int EqualStart, int EqualEnd) PartitionThreeWay(
        double[] values,
        int left,
        int right,
        double pivot)
    {
        int lower = left;
        int index = left;
        int upper = right;
        while (index <= upper)
        {
            if (values[index] < pivot)
            {
                (values[lower], values[index]) = (values[index], values[lower]);
                lower++;
                index++;
            }
            else if (values[index] > pivot)
            {
                (values[index], values[upper]) = (values[upper], values[index]);
                upper--;
            }
            else
            {
                index++;
            }
        }
        return (lower, upper);
    }

    private static (int EqualStart, int EqualEnd) PartitionThreeWay(
        float[] values,
        int left,
        int right,
        float pivot)
    {
        int lower = left;
        int index = left;
        int upper = right;
        while (index <= upper)
        {
            if (values[index] < pivot)
            {
                (values[lower], values[index]) = (values[index], values[lower]);
                lower++;
                index++;
            }
            else if (values[index] > pivot)
            {
                (values[index], values[upper]) = (values[upper], values[index]);
                upper--;
            }
            else
            {
                index++;
            }
        }
        return (lower, upper);
    }

    private static void ValidateApproval(SampleMaterialGiVisualApproval? approval)
    {
        if (approval == null ||
            string.IsNullOrWhiteSpace(approval.ApprovalId) ||
            string.IsNullOrWhiteSpace(approval.Reviewer) ||
            string.IsNullOrWhiteSpace(approval.Reason) ||
            approval.ApprovedAtUtc == default ||
            approval.ApprovedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new InvalidDataException(
                "Approved HDR reference lacks a complete UTC visual-review approval.");
        }
    }

    private static void ValidateRoiGate(
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate,
        int width,
        int height)
    {
        if (gate.Kind == SampleMaterialGiVisualRoiGateKind.Unknown ||
            !Enum.IsDefined(gate.Kind))
        {
            throw new InvalidDataException($"ROI '{roi.Name}' has an absent or unknown gate kind.");
        }
        if (!gate.Signal.HasValue || !Enum.IsDefined(gate.Signal.Value))
            throw new InvalidDataException($"ROI '{roi.Name}' gate '{gate.Kind}' has no valid signal.");

        double expectedThreshold = gate.Kind switch
        {
            SampleMaterialGiVisualRoiGateKind.UniformLuminance =>
                MaximumUniformLuminanceDifference,
            SampleMaterialGiVisualRoiGateKind.TransitionStep =>
                MaximumTransitionStep,
            SampleMaterialGiVisualRoiGateKind.LegacyRadianceThresholdAlphaProxy =>
                throw new InvalidDataException(
                    $"ROI '{roi.Name}' uses the retired radiance-threshold alpha proxy. " +
                    "Use an authenticated material-gi-alpha-visibility Vulkan report."),
            SampleMaterialGiVisualRoiGateKind.LowFrequencyMean =>
                MaximumLowFrequencyMeanDifference,
            SampleMaterialGiVisualRoiGateKind.TemporalStability =>
                MaximumTemporalP95,
            _ => throw new InvalidDataException($"ROI '{roi.Name}' has an unsupported gate.")
        };
        RequireFixedThreshold(
            gate.MaximumRelativeDifference,
            expectedThreshold,
            $"{roi.Name}/{gate.Kind}");

        switch (gate.Kind)
        {
            case SampleMaterialGiVisualRoiGateKind.UniformLuminance:
                if (gate.Signal != SampleMaterialGiCaptureSignal.FinalDdgiDiffuse)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' uniform-luminance gate must use FinalDdgiDiffuse.");
                }
                RequireNoSpecializedMetadata(roi, gate);
                break;
            case SampleMaterialGiVisualRoiGateKind.TransitionStep:
                if (gate.Signal != SampleMaterialGiCaptureSignal.FinalDdgiDiffuse)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' transition gate must use FinalDdgiDiffuse.");
                }
                if (gate.TransitionSamples == null || gate.TransitionSamples.Count < 2)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' transition gate requires at least two ordered samples.");
                }
                foreach (SampleMaterialGiPixelRegion sample in gate.TransitionSamples)
                {
                    ValidateRegion(sample, width, height, $"ROI '{roi.Name}' transition sample");
                    if (!Contains(roi.Bounds, sample))
                    {
                        throw new InvalidDataException(
                            $"ROI '{roi.Name}' transition sample escapes its named ROI bounds.");
                    }
                }
                if (gate.ComparisonSignal.HasValue ||
                    gate.CoverageThreshold.HasValue ||
                    gate.TemporalFrameRelativePaths != null ||
                    gate.TemporalWarmupFrameCount.HasValue)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' transition gate contains incompatible metadata.");
                }
                break;
            case SampleMaterialGiVisualRoiGateKind.LegacyRadianceThresholdAlphaProxy:
                throw new InvalidDataException(
                    $"ROI '{roi.Name}' uses the retired radiance-threshold alpha proxy. " +
                    "Use an authenticated material-gi-alpha-visibility Vulkan report.");
            case SampleMaterialGiVisualRoiGateKind.LowFrequencyMean:
                if (gate.Signal != SampleMaterialGiCaptureSignal.FinalComposedIndirect ||
                    gate.ComparisonSignal != SampleMaterialGiCaptureSignal.FinalDdgiDiffuse)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' low-frequency gate must compare FinalComposedIndirect to FinalDdgiDiffuse.");
                }
                if (gate.TransitionSamples != null ||
                    gate.CoverageThreshold.HasValue ||
                    gate.TemporalFrameRelativePaths != null ||
                    gate.TemporalWarmupFrameCount.HasValue)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' low-frequency gate contains incompatible metadata.");
                }
                break;
            case SampleMaterialGiVisualRoiGateKind.TemporalStability:
                if (gate.Signal != SampleMaterialGiCaptureSignal.FinalComposedIndirect)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' temporal gate must use FinalComposedIndirect.");
                }
                if (gate.ComparisonSignal.HasValue ||
                    gate.TransitionSamples != null ||
                    gate.CoverageThreshold.HasValue ||
                    gate.TemporalFrameRelativePaths == null ||
                    !gate.TemporalWarmupFrameCount.HasValue ||
                    gate.TemporalWarmupFrameCount.Value < 0 ||
                    gate.TemporalFrameRelativePaths.Count <
                    gate.TemporalWarmupFrameCount.Value + 2)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' temporal gate has absent or incompatible frame/warmup metadata.");
                }
                if (gate.TemporalFrameRelativePaths.Any(static path =>
                        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) ||
                    gate.TemporalFrameRelativePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    gate.TemporalFrameRelativePaths.Count)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' temporal paths must be non-empty, relative, and unique.");
                }
                if (gate.TemporalFrameRelativePaths.Count > MaximumTemporalFrameCount)
                {
                    throw new InvalidDataException(
                        $"ROI '{roi.Name}' temporal evidence contains " +
                        $"{gate.TemporalFrameRelativePaths.Count} frames; " +
                        $"the bounded limit is {MaximumTemporalFrameCount}.");
                }
                GetTemporalSampleCount(
                    roi.Bounds,
                    gate.TemporalFrameRelativePaths.Count -
                    gate.TemporalWarmupFrameCount.Value,
                    $"ROI '{roi.Name}' temporal evidence");
                break;
        }
    }

    private static void RequireNoSpecializedMetadata(
        SampleMaterialGiApprovedRoi roi,
        SampleMaterialGiApprovedRoiGate gate)
    {
        if (gate.ComparisonSignal.HasValue ||
            gate.TransitionSamples != null ||
            gate.CoverageThreshold.HasValue ||
            gate.TemporalFrameRelativePaths != null ||
            gate.TemporalWarmupFrameCount.HasValue)
        {
            throw new InvalidDataException(
                $"ROI '{roi.Name}' uniform-luminance gate contains incompatible metadata.");
        }
    }

    private static void ValidateRegion(
        SampleMaterialGiPixelRegion? region,
        int width,
        int height,
        string role)
    {
        if (region == null ||
            region.X < 0 ||
            region.Y < 0 ||
            region.Width <= 0 ||
            region.Height <= 0 ||
            (long)region.X + region.Width > width ||
            (long)region.Y + region.Height > height)
        {
            throw new InvalidDataException($"{role} has absent or out-of-bounds pixel metadata.");
        }
    }

    private static bool Contains(
        SampleMaterialGiPixelRegion outer,
        SampleMaterialGiPixelRegion inner) =>
        inner.X >= outer.X &&
        inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static long PixelCount(SampleMaterialGiPixelRegion region) =>
        checked((long)region.Width * region.Height);

    private static int GetTemporalSampleCount(
        SampleMaterialGiPixelRegion region,
        int postWarmupFrameCount,
        string role)
    {
        if (postWarmupFrameCount <= 0)
            throw new InvalidDataException($"{role} has no post-warmup frames.");

        long sampleCount;
        try
        {
            sampleCount = checked(PixelCount(region) * postWarmupFrameCount);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                $"{role} sample-count arithmetic overflowed.",
                exception);
        }

        if (sampleCount > MaximumTemporalSampleCount ||
            sampleCount > Array.MaxLength)
        {
            throw new InvalidDataException(
                $"{role} requires {sampleCount} pixel-frame samples, exceeding the " +
                $"{MaximumTemporalSampleBufferBytes}-byte bounded temporal sample-buffer budget.");
        }
        return checked((int)sampleCount);
    }

    private static double RequireFinite(float value, int x, int y)
    {
        if (!float.IsFinite(value))
            throw new InvalidDataException($"ROI image contains a non-finite component at ({x},{y}).");
        return value;
    }

    private static LinearFloatImage RequireImage(
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, LinearFloatImage> images,
        SampleMaterialGiCaptureSignal signal,
        string role)
    {
        if (!images.TryGetValue(signal, out LinearFloatImage? image) || image == null)
            throw new InvalidDataException($"{role} image set is missing signal '{signal}'.");
        return image;
    }

    private static string GetEvidenceHash(
        IReadOnlyDictionary<SampleMaterialGiCaptureSignal, string>? hashes,
        SampleMaterialGiCaptureSignal signal) =>
        hashes != null && hashes.TryGetValue(signal, out string? hash)
            ? hash
            : string.Empty;

    private static void ValidateManifestExtent(
        SampleMaterialGiApprovedHdrReferenceManifest manifest,
        LinearFloatImage image,
        string role)
    {
        int approvedWidth = manifest.Width
            ?? throw new InvalidDataException("Approved HDR width metadata is absent.");
        int approvedHeight = manifest.Height
            ?? throw new InvalidDataException("Approved HDR height metadata is absent.");
        if (image.Width != approvedWidth || image.Height != approvedHeight)
        {
            throw new InvalidDataException(
                $"{role} extent {image.Width}x{image.Height} differs from approved ROI metadata.");
        }
        int expected = checked(image.Width * image.Height * 3);
        if (image.Pixels.Length != expected)
            throw new InvalidDataException($"{role} has an invalid RGB payload length.");
    }

    private static void RequireFixedThreshold(
        double? actual,
        double expected,
        string role)
    {
        if (!actual.HasValue ||
            !double.IsFinite(actual.Value) ||
            Math.Abs(actual.Value - expected) > FixedThresholdTolerance)
        {
            throw new InvalidDataException(
                $"{role} threshold is absent or incompatible; exactly {expected:R} is required.");
        }
    }

    private static void RequireSha256(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != 64 ||
            value.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{role} SHA-256 identity is absent or malformed.");
        }
    }

    private static SampleMaterialGiApprovedHdrReferenceManifest LoadApprovedManifest(
        string path,
        out string manifestSha256)
    {
        SampleEvidenceFileContent evidence;
        try
        {
            evidence = SampleEvidenceFileIo.Read(
                path,
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Approved HDR reference manifest");
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                "Approved HDR reference manifest is missing.",
                Path.GetFullPath(path),
                exception);
        }
        manifestSha256 = evidence.Sha256;
        try
        {
            SampleEvidenceFileIo.ValidateStrictJson(
                evidence.Bytes,
                JsonOptions.MaxDepth,
                "Approved HDR reference manifest");
            SampleMaterialGiApprovedHdrReferenceManifest manifest =
                JsonSerializer.Deserialize<SampleMaterialGiApprovedHdrReferenceManifest>(
                    evidence.Bytes,
                    JsonOptions)
                ?? throw new InvalidDataException(
                    "Approved HDR reference manifest deserialized to null.");
            ValidateApprovedManifest(manifest);
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Approved HDR reference manifest is malformed or contains unknown metadata.",
                exception);
        }
    }

    private static (LinearFloatImage Image, string Sha256) LoadTemporalImage(
        string candidateDirectory,
        string relativePath)
    {
        string path = ResolveContainedPath(
            candidateDirectory,
            relativePath,
            "candidate temporal frame");
        if (!string.Equals(Path.GetExtension(path), ".pfm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Temporal frame '{relativePath}' is not a PFM artifact.");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Temporal frame '{relativePath}' is missing.", path);
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"Candidate temporal frame '{relativePath}'");
        return (
            PfmLinearImageCodec.Decode(evidence.Bytes),
            evidence.Sha256);
    }

    private static SampleMaterialGiCaptureOutput GetCaptureOutput(
        SampleMaterialGiCaptureSignal signal)
    {
        SampleMaterialGiCaptureOutput[] outputs =
            SampleMaterialGiConformanceCatalog.RequiredOutputs
                .Where(output => output.Signal == signal)
                .ToArray();
        if (outputs.Length != 1)
            throw new InvalidDataException($"Capture contract has {outputs.Length} outputs for '{signal}'.");
        return outputs[0];
    }

    private static string BuildMetricFailureReason(
        IReadOnlyList<SampleMaterialGiApprovedHdrImageResult> failedImages,
        IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> failedRois)
    {
        var failures = new List<string>(failedImages.Count + failedRois.Count);
        failures.AddRange(failedImages.Select(static result =>
            $"{result.Signal}(relativeRMSE={result.RelativeRmse:R}, flipP95={result.FlipP95:R})"));
        failures.AddRange(failedRois.Select(static result =>
            $"{result.Roi}/{result.Kind}={result.MeasuredRelativeDifference:R}"));
        return "Approved HDR visual gates failed: " + string.Join(", ", failures);
    }

    private static SampleMaterialGiApprovedHdrRegressionReport CreateReport(
        string status,
        string failureReason,
        string approvedPath,
        string approvedHash,
        string referenceManifestPath,
        string referenceManifestHash,
        string candidateDirectory,
        string candidateManifestHash,
        SampleMaterialGiApprovedHdrReferenceManifest? manifest,
        IReadOnlyList<SampleMaterialGiApprovedHdrImageResult> images,
        IReadOnlyList<SampleMaterialGiApprovedRoiGateResult> roiGates)
    {
        return new SampleMaterialGiApprovedHdrRegressionReport(
            ReportSchemaVersion,
            status,
            failureReason,
            DateTimeOffset.UtcNow,
            approvedPath,
            approvedHash,
            referenceManifestPath,
            referenceManifestHash,
            candidateDirectory,
            candidateManifestHash,
            manifest?.ContractFingerprint ?? string.Empty,
            manifest?.MetricVersion ?? SampleMaterialGiHdrFlipMetric.MetricVersion,
            manifest?.FlipConfiguration ?? SampleMaterialGiHdrFlipMetric.FixedConfiguration,
            RelativeRmseDefinition,
            SampleMaterialGiHdrFlipMetric.Definition,
            manifest?.Approval,
            images.ToArray(),
            roiGates.ToArray());
    }

    private static string ResolveContainedPath(
        string directory,
        string relativePath,
        string role)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{role} path must be non-empty and relative.");
        string root = Path.GetFullPath(directory);
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        string containedRoot = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(containedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{role} path '{relativePath}' escapes '{root}'.");
        return fullPath;
    }

    private static string NormalizeFilePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A manifest path is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A capture directory is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static string TryComputeSha256(string path)
    {
        try
        {
            return SampleEvidenceFileIo.Read(
                    path,
                    SampleEvidenceFileIo.MaximumJsonBytes,
                    "HDR evidence manifest")
                .Sha256;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RequireCompatible<T>(T reference, T candidate, string role)
    {
        if (!EqualityComparer<T>.Default.Equals(reference, candidate))
            throw new InvalidDataException($"Approved HDR capture metadata differs for {role}.");
    }

    private static string DescribeException(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return $"{exception.GetType().Name}: {message}";
    }

    private static void WriteJsonAtomic<T>(
        string path,
        T value,
        string role)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"A {role} path is required.", nameof(path));
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            value,
            JsonOptions);
        SampleEvidenceFileIo.WriteAtomic(
            path,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            role);
    }
}

/// <summary>
/// Standalone pre-Vulkan command used by CI and release qualification.
/// </summary>
public static class SampleMaterialGiApprovedHdrCli
{
    public const string CompareOption = "--compare-material-gi-approved-hdr";
    public const string ReportOption = "--material-gi-approved-hdr-report";

    public static bool TryRun(
        string[] args,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        exitCode = 0;
        int compareIndex = Array.FindIndex(
            args,
            static argument => string.Equals(argument, CompareOption, StringComparison.Ordinal));
        if (compareIndex < 0)
            return false;

        try
        {
            if (Array.FindLastIndex(
                    args,
                    static argument => string.Equals(argument, CompareOption, StringComparison.Ordinal)) !=
                compareIndex)
            {
                throw new ArgumentException($"{CompareOption} may be specified only once.");
            }
            if (compareIndex + 2 >= args.Length)
            {
                throw new ArgumentException(
                    $"{CompareOption} requires <approved-reference-manifest> <candidate-capture-dir>.");
            }
            string approvedManifest =
                RequireValue(args[compareIndex + 1], "approved reference manifest");
            string candidateDirectory =
                RequireValue(args[compareIndex + 2], "candidate capture directory");
            string? reportPath = null;
            var consumed = new HashSet<int> { compareIndex, compareIndex + 1, compareIndex + 2 };
            for (int index = 0; index < args.Length; index++)
            {
                if (consumed.Contains(index))
                    continue;
                string argument = args[index];
                if (argument.StartsWith(ReportOption + "=", StringComparison.Ordinal))
                {
                    if (reportPath != null)
                        throw new ArgumentException($"{ReportOption} may be specified only once.");
                    reportPath = RequireValue(
                        argument[(ReportOption.Length + 1)..],
                        "approved HDR report path");
                    consumed.Add(index);
                    continue;
                }
                if (string.Equals(argument, ReportOption, StringComparison.Ordinal))
                {
                    if (reportPath != null)
                        throw new ArgumentException($"{ReportOption} may be specified only once.");
                    if (index + 1 >= args.Length)
                        throw new ArgumentException($"{ReportOption} requires a path.");
                    reportPath = RequireValue(args[index + 1], "approved HDR report path");
                    consumed.Add(index);
                    consumed.Add(index + 1);
                    index++;
                    continue;
                }
                throw new ArgumentException(
                    $"{CompareOption} is standalone and cannot be combined with '{argument}'.");
            }

            reportPath ??= Path.Combine(
                Path.GetFullPath(candidateDirectory),
                SampleMaterialGiApprovedHdrComparer.DefaultReportFileName);
            SampleMaterialGiApprovedHdrRegressionReport report =
                SampleMaterialGiApprovedHdrComparer.Compare(
                    approvedManifest,
                    candidateDirectory);
            SampleMaterialGiApprovedHdrComparer.WriteReportAtomic(reportPath, report);
            if (report.Passed)
            {
                output.WriteLine(
                    $"Material/GI approved HDR regression passed: " +
                    $"images={report.Images.Count} rois={report.RoiGates.Count} " +
                    $"report={Path.GetFullPath(reportPath)}");
                exitCode = 0;
            }
            else
            {
                error.WriteLine(
                    $"Material/GI approved HDR regression failed: {report.FailureReason} " +
                    $"report={Path.GetFullPath(reportPath)}");
                exitCode = 2;
            }
            return true;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"Material/GI approved HDR command failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 64;
            return true;
        }
    }

    private static string RequireValue(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"A non-option {description} is required.");
        return value;
    }
}
