using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

/// <summary>
/// Frozen numerical gate for graphics-only versus forced-async material/GI
/// evidence. The absolute gate protects dark/zero-valued signals, the relative
/// gate protects HDR signals, and the per-component gate catches sparse
/// synchronization corruption that an aggregate RMSE could hide.
/// </summary>
public sealed record SampleMaterialGiComparisonTolerance(
    double MaximumAbsoluteRmse,
    double MaximumRelativeRmse,
    double MaximumAbsoluteComponentError)
{
    public static SampleMaterialGiComparisonTolerance GraphicsAsyncEquivalence { get; } =
        new(
            MaximumAbsoluteRmse: 0.002,
            MaximumRelativeRmse: 0.001,
            MaximumAbsoluteComponentError: 0.05);

    internal void Validate()
    {
        ValidateFiniteNonNegative(MaximumAbsoluteRmse, nameof(MaximumAbsoluteRmse));
        ValidateFiniteNonNegative(MaximumRelativeRmse, nameof(MaximumRelativeRmse));
        ValidateFiniteNonNegative(
            MaximumAbsoluteComponentError,
            nameof(MaximumAbsoluteComponentError));
    }

    private static void ValidateFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0)
            throw new ArgumentOutOfRangeException(name, "Comparison tolerances must be finite and non-negative.");
    }
}

public sealed record SampleMaterialGiOutputComparison(
    SampleMaterialGiCaptureSignal Signal,
    string RelativePath,
    string ReferenceSha256,
    string CandidateSha256,
    bool HashesEqual,
    long ComponentCount,
    double ReferenceRms,
    double AbsoluteRmse,
    double RelativeRmse,
    double MaximumAbsoluteComponentError,
    bool Passed);

public sealed record SampleMaterialGiCaptureComparisonReport(
    string SchemaVersion,
    string Status,
    string FailureReason,
    DateTimeOffset ComparedAtUtc,
    string ReferenceDirectory,
    string CandidateDirectory,
    string ReferenceManifestSha256,
    string CandidateManifestSha256,
    string ContractFingerprint,
    string RelativeRmseDefinition,
    SampleMaterialGiComparisonTolerance Tolerance,
    IReadOnlyList<SampleMaterialGiOutputComparison> Outputs)
{
    [JsonIgnore]
    public bool Passed => string.Equals(Status, "passed", StringComparison.Ordinal);

    [JsonPropertyName("producerIdentity")]
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }
}

/// <summary>
/// Strict manifest and linear-float comparison. Structural/provenance failures
/// are represented by a failed report instead of being mistaken for a numeric
/// mismatch, allowing CI to publish diagnostics for every failed gate.
/// </summary>
public static class SampleMaterialGiCaptureComparer
{
    public const string ReportSchemaVersion = "material-gi-graphics-async-comparison/v2";
    public const string DefaultReportFileName = "material-gi-graphics-async-comparison.json";
    public const string RelativeRmseDefinition =
        "absoluteRmse / max(referenceRms, 1.0 linear-radiance unit)";

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

    public static SampleMaterialGiCaptureComparisonReport Compare(
        string referenceDirectory,
        string candidateDirectory,
        SampleMaterialGiComparisonTolerance? tolerance = null)
    {
        SampleMaterialGiComparisonTolerance resolvedTolerance =
            tolerance ?? SampleMaterialGiComparisonTolerance.GraphicsAsyncEquivalence;
        string reference = NormalizeDirectory(referenceDirectory, nameof(referenceDirectory));
        string candidate = NormalizeDirectory(candidateDirectory, nameof(candidateDirectory));
        resolvedTolerance.Validate();

        string referenceManifestPath =
            Path.Combine(reference, SampleMaterialGiArtifactPublisher.ManifestFileName);
        string candidateManifestPath =
            Path.Combine(candidate, SampleMaterialGiArtifactPublisher.ManifestFileName);
        string referenceManifestSha256 = TryComputeSha256(referenceManifestPath);
        string candidateManifestSha256 = TryComputeSha256(candidateManifestPath);

        try
        {
            SampleMaterialGiRunManifest referenceManifest =
                LoadPassedManifest(
                    referenceManifestPath,
                    reference,
                    "graphics reference",
                    out referenceManifestSha256);
            SampleMaterialGiRunManifest candidateManifest =
                LoadPassedManifest(
                    candidateManifestPath,
                    candidate,
                    "async candidate",
                    out candidateManifestSha256);
            ValidatePairProvenance(referenceManifest, candidateManifest);
            MaterialGiProducerIdentity producerIdentity =
                SampleMaterialGiProducerIdentityFactory.CreateGraphicsAsyncPair(
                    referenceManifest.Renderer!,
                    candidateManifest.Renderer!);

            var outputs = new List<SampleMaterialGiOutputComparison>(
                SampleMaterialGiConformanceCatalog.RequiredOutputs.Count);
            foreach (SampleMaterialGiCaptureOutput output in
                     SampleMaterialGiConformanceCatalog.RequiredOutputs)
            {
                SampleMaterialGiArtifact referenceArtifact =
                    GetArtifact(referenceManifest, output, "graphics reference");
                SampleMaterialGiArtifact candidateArtifact =
                    GetArtifact(candidateManifest, output, "async candidate");
                LinearFloatImage referenceImage =
                    LoadVerifiedImage(reference, referenceArtifact, "graphics reference");
                LinearFloatImage candidateImage =
                    LoadVerifiedImage(candidate, candidateArtifact, "async candidate");
                outputs.Add(
                    CompareImages(
                        output.Signal,
                        referenceArtifact.RelativePath,
                        referenceArtifact.Sha256,
                        candidateArtifact.Sha256,
                        referenceImage,
                        candidateImage,
                        resolvedTolerance));
            }

            SampleMaterialGiOutputComparison[] failed =
                outputs.Where(static output => !output.Passed).ToArray();
            string status = failed.Length == 0 ? "passed" : "failed";
            string failureReason = failed.Length == 0
                ? string.Empty
                : "Numerical equivalence failed for: " +
                  string.Join(
                      ", ",
                      failed.Select(static output =>
                          $"{output.Signal} " +
                          $"(absRMSE={output.AbsoluteRmse:R}, " +
                          $"relRMSE={output.RelativeRmse:R}, " +
                          $"maxAbs={output.MaximumAbsoluteComponentError:R})"));
            return CreateReport(
                status,
                failureReason,
                reference,
                candidate,
                referenceManifestSha256,
                candidateManifestSha256,
                referenceManifest.ContractFingerprint,
                resolvedTolerance,
                outputs) with
            {
                ProducerIdentity = producerIdentity
            };
        }
        catch (Exception exception)
        {
            return CreateReport(
                "failed",
                $"Comparison input validation failed: {DescribeException(exception)}",
                reference,
                candidate,
                referenceManifestSha256,
                candidateManifestSha256,
                string.Empty,
                resolvedTolerance,
                Array.Empty<SampleMaterialGiOutputComparison>());
        }
    }

    public static void WriteReportAtomic(
        string reportPath,
        SampleMaterialGiCaptureComparisonReport report)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
            throw new ArgumentException("A material/GI comparison report path is required.", nameof(reportPath));
        ArgumentNullException.ThrowIfNull(report);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            report,
            JsonOptions);
        SampleEvidenceFileIo.WriteAtomic(
            reportPath,
            payload,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Material/GI graphics/async comparison report");
    }

    internal static SampleMaterialGiOutputComparison CompareImages(
        SampleMaterialGiCaptureSignal signal,
        string relativePath,
        string referenceSha256,
        string candidateSha256,
        LinearFloatImage reference,
        LinearFloatImage candidate,
        SampleMaterialGiComparisonTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(tolerance);
        tolerance.Validate();
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            throw new InvalidDataException(
                $"Signal '{signal}' dimensions differ: " +
                $"{reference.Width}x{reference.Height} versus {candidate.Width}x{candidate.Height}.");
        }
        if (reference.Pixels.Length != candidate.Pixels.Length || reference.Pixels.Length == 0)
            throw new InvalidDataException($"Signal '{signal}' has incompatible or empty RGB payloads.");

        double squaredError = 0.0;
        double squaredReference = 0.0;
        double maximumAbsoluteError = 0.0;
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
            double absoluteDifference = Math.Abs(difference);
            squaredError += difference * difference;
            squaredReference += referenceValue * referenceValue;
            maximumAbsoluteError = Math.Max(maximumAbsoluteError, absoluteDifference);
        }

        double componentCount = reference.Pixels.Length;
        double absoluteRmse = Math.Sqrt(squaredError / componentCount);
        double referenceRms = Math.Sqrt(squaredReference / componentCount);
        double relativeRmse = absoluteRmse / Math.Max(referenceRms, 1.0);
        bool passed =
            absoluteRmse <= tolerance.MaximumAbsoluteRmse &&
            relativeRmse <= tolerance.MaximumRelativeRmse &&
            maximumAbsoluteError <= tolerance.MaximumAbsoluteComponentError;
        return new SampleMaterialGiOutputComparison(
            signal,
            relativePath,
            referenceSha256,
            candidateSha256,
            string.Equals(referenceSha256, candidateSha256, StringComparison.Ordinal),
            reference.Pixels.LongLength,
            referenceRms,
            absoluteRmse,
            relativeRmse,
            maximumAbsoluteError,
            passed);
    }

    internal static SampleMaterialGiRunManifest LoadPassedManifest(
        string manifestPath,
        string directory,
        string role) =>
        LoadPassedManifest(
            manifestPath,
            directory,
            role,
            out _);

    internal static SampleMaterialGiRunManifest LoadPassedManifest(
        string manifestPath,
        string directory,
        string role,
        out string manifestSha256)
    {
        SampleEvidenceFileContent evidence;
        try
        {
            evidence = SampleEvidenceFileIo.Read(
                manifestPath,
                SampleEvidenceFileIo.MaximumJsonBytes,
                $"{role} manifest");
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            throw new FileNotFoundException(
                $"{role} manifest is missing.",
                Path.GetFullPath(manifestPath),
                exception);
        }
        manifestSha256 = evidence.Sha256;
        SampleMaterialGiRunManifest manifest;
        try
        {
            SampleEvidenceFileIo.ValidateStrictJson(
                evidence.Bytes,
                JsonOptions.MaxDepth,
                $"{role} manifest");
            manifest = JsonSerializer.Deserialize<SampleMaterialGiRunManifest>(
                    evidence.Bytes,
                    JsonOptions)
                ?? throw new InvalidDataException($"{role} manifest deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{role} manifest is malformed JSON.", exception);
        }

        if (!string.Equals(
                manifest.SchemaVersion,
                SampleMaterialGiArtifactPublisher.ManifestSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{role} manifest schema '{manifest.SchemaVersion}' is unsupported.");
        }
        if (!string.Equals(manifest.Status, "passed", StringComparison.Ordinal))
            throw new InvalidDataException($"{role} manifest status is '{manifest.Status}', not 'passed'.");
        if (!string.IsNullOrEmpty(manifest.FailureReason))
            throw new InvalidDataException($"{role} passed manifest contains a failure reason.");
        if (!string.Equals(
                manifest.ContractFingerprint,
                SampleMaterialGiConformanceCatalog.Fingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{role} manifest has a foreign material/GI contract fingerprint.");
        }
        if (manifest.FloatFormat != SampleMaterialGiArtifactPublisher.FloatFormat)
            throw new InvalidDataException($"{role} manifest has a different linear-float format contract.");
        if (!manifest.CompletedAtUtc.HasValue ||
            manifest.CompletedAtUtc.Value < manifest.StartedAtUtc)
        {
            throw new InvalidDataException($"{role} manifest has invalid capture timestamps.");
        }
        if (manifest.Artifacts == null)
            throw new InvalidDataException($"{role} manifest has no artifact collection.");

        SampleMaterialGiArtifactPublisher.ValidateCompleteArtifactSet(directory, manifest.Artifacts);
        SampleMaterialGiSemanticEvidenceGate.ValidatePublishedEvidence(
            directory,
            manifest.Artifacts,
            manifest.SemanticEvidence);
        ValidateRendererProvenance(manifest.Renderer, role);
        return manifest;
    }

    internal static void ValidateRendererProvenance(
        SampleMaterialGiRendererProvenance? renderer,
        string role)
    {
        if (renderer == null)
            throw new InvalidDataException($"{role} manifest has no renderer provenance.");
        RequireValue(renderer.GpuDevice, role, nameof(renderer.GpuDevice));
        RequireValue(renderer.GpuDriver, role, nameof(renderer.GpuDriver));
        RequireValue(renderer.BuildConfiguration, role, nameof(renderer.BuildConfiguration));
        RequireValue(renderer.ApplicationVersion, role, nameof(renderer.ApplicationVersion));
        RequireValue(renderer.Commit, role, nameof(renderer.Commit));
        RequireValue(renderer.ShaderBundleHash, role, nameof(renderer.ShaderBundleHash));
        const string sha256Prefix = "sha256:";
        string shaderHash = renderer.ShaderBundleHash;
        if (!shaderHash.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase) ||
            shaderHash.Length != sha256Prefix.Length + 64 ||
            shaderHash[sha256Prefix.Length..].Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException($"{role} shader bundle provenance is not a SHA-256 identity.");
        }
        if (renderer.SettingsSchemaVersion <= 0)
            throw new InvalidDataException($"{role} settings schema version is invalid.");
        try
        {
            _ = MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                renderer.SettingsFingerprint);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"{role} renderer settings fingerprint is invalid.",
                exception);
        }
        if (renderer.RenderWidth != SampleMaterialGiConformanceCatalog.LockedWidth ||
            renderer.RenderHeight != SampleMaterialGiConformanceCatalog.LockedHeight)
        {
            throw new InvalidDataException($"{role} renderer extent does not match the locked contract.");
        }
        if (renderer.QualityPreset != RenderQualityPreset.DdgiHigh ||
            renderer.ActiveMaterialGiV2Features != MaterialGiV2Feature.All ||
            renderer.GlobalIlluminationMode != GlobalIlluminationMode.Hybrid)
        {
            throw new InvalidDataException($"{role} renderer did not use the locked Material-GI V2 profile.");
        }
        ArgumentNullException.ThrowIfNull(renderer.Camera);
    }

    private static void ValidatePairProvenance(
        SampleMaterialGiRunManifest reference,
        SampleMaterialGiRunManifest candidate)
    {
        SampleMaterialGiRendererProvenance graphics = reference.Renderer!;
        SampleMaterialGiRendererProvenance asyncCandidate = candidate.Renderer!;
        RequireEqual(graphics.GpuDevice, asyncCandidate.GpuDevice, "GPU device");
        RequireEqual(graphics.GpuDriver, asyncCandidate.GpuDriver, "GPU driver");
        RequireEqual(graphics.BuildConfiguration, asyncCandidate.BuildConfiguration, "build configuration");
        RequireEqual(graphics.ApplicationVersion, asyncCandidate.ApplicationVersion, "application version");
        RequireEqual(graphics.Commit, asyncCandidate.Commit, "build commit");
        RequireEqual(graphics.ShaderBundleHash, asyncCandidate.ShaderBundleHash, "shader bundle hash");
        RequireEqual(graphics.SettingsSchemaVersion, asyncCandidate.SettingsSchemaVersion, "settings schema");
        RequireEqual(graphics.RenderWidth, asyncCandidate.RenderWidth, "render width");
        RequireEqual(graphics.RenderHeight, asyncCandidate.RenderHeight, "render height");
        RequireEqual(graphics.SceneContentRevision, asyncCandidate.SceneContentRevision, "scene content revision");
        RequireEqual(graphics.QualityPreset, asyncCandidate.QualityPreset, "quality preset");
        RequireEqual(
            graphics.ActiveMaterialGiV2Features,
            asyncCandidate.ActiveMaterialGiV2Features,
            "Material-GI V2 feature mask");
        RequireEqual(
            graphics.GlobalIlluminationMode,
            asyncCandidate.GlobalIlluminationMode,
            "global illumination mode");
        RequireEqual(graphics.Camera, asyncCandidate.Camera, "camera");

        if (graphics.AsyncComputeRequestedMode != AsyncComputeMode.Disabled ||
            graphics.AsyncComputeEffectiveMode != AsyncComputeMode.Disabled ||
            graphics.AsyncComputeSubmittedComputeSegmentCount != 0)
        {
            throw new InvalidDataException(
                "The reference manifest is not a graphics-only capture.");
        }
        if (asyncCandidate.AsyncComputeRequestedMode != AsyncComputeMode.ForceEnabledForValidation ||
            asyncCandidate.AsyncComputeEffectiveMode != AsyncComputeMode.ForceEnabledForValidation ||
            asyncCandidate.AsyncComputeSubmittedComputeSegmentCount <= 0)
        {
            throw new InvalidDataException(
                "The candidate manifest does not prove forced async-compute execution.");
        }
    }

    internal static SampleMaterialGiArtifact GetArtifact(
        SampleMaterialGiRunManifest manifest,
        SampleMaterialGiCaptureOutput output,
        string role)
    {
        string relativePath = SampleMaterialGiArtifactPublisher.GetRelativeArtifactPath(output);
        SampleMaterialGiArtifact[] matches = manifest.Artifacts
            .Where(artifact =>
                artifact.Signal == output.Signal &&
                string.Equals(artifact.RelativePath, relativePath, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException($"{role} has {matches.Length} records for '{output.Signal}'.");
        return matches[0];
    }

    internal static LinearFloatImage LoadVerifiedImage(
        string directory,
        SampleMaterialGiArtifact artifact,
        string role)
    {
        string path = ResolveContainedPath(directory, artifact.RelativePath);
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"{role} artifact '{artifact.RelativePath}'");
        if (evidence.Bytes.LongLength != artifact.ByteLength)
            throw new InvalidDataException($"{role} artifact '{artifact.RelativePath}' byte length changed.");
        if (!string.Equals(
                evidence.Sha256,
                artifact.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{role} artifact '{artifact.RelativePath}' SHA-256 changed.");
        }
        if (!float.IsFinite(artifact.MinimumComponent) ||
            !float.IsFinite(artifact.MaximumComponent) ||
            artifact.MinimumComponent > artifact.MaximumComponent)
        {
            throw new InvalidDataException($"{role} artifact '{artifact.RelativePath}' range metadata is invalid.");
        }

        LinearFloatImage image = PfmLinearImageCodec.Decode(evidence.Bytes);
        if (image.Width != artifact.Width || image.Height != artifact.Height)
            throw new InvalidDataException($"{role} artifact '{artifact.RelativePath}' dimensions changed.");
        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        foreach (float component in image.Pixels)
        {
            minimum = Math.Min(minimum, component);
            maximum = Math.Max(maximum, component);
        }
        if (BitConverter.SingleToInt32Bits(minimum) !=
                BitConverter.SingleToInt32Bits(artifact.MinimumComponent) ||
            BitConverter.SingleToInt32Bits(maximum) !=
                BitConverter.SingleToInt32Bits(artifact.MaximumComponent))
        {
            throw new InvalidDataException(
                $"{role} artifact '{artifact.RelativePath}' range metadata does not match its pixels.");
        }
        return image;
    }

    private static SampleMaterialGiCaptureComparisonReport CreateReport(
        string status,
        string failureReason,
        string reference,
        string candidate,
        string referenceManifestSha256,
        string candidateManifestSha256,
        string contractFingerprint,
        SampleMaterialGiComparisonTolerance tolerance,
        IReadOnlyList<SampleMaterialGiOutputComparison> outputs)
    {
        return new SampleMaterialGiCaptureComparisonReport(
            ReportSchemaVersion,
            status,
            failureReason,
            DateTimeOffset.UtcNow,
            reference,
            candidate,
            referenceManifestSha256,
            candidateManifestSha256,
            contractFingerprint,
            RelativeRmseDefinition,
            tolerance,
            outputs.ToArray());
    }

    private static string NormalizeDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A capture directory is required.", parameterName);
        return Path.GetFullPath(path);
    }

    private static string ResolveContainedPath(string directory, string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(directory, relativePath));
        string root = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artifact path '{relativePath}' escapes '{directory}'.");
        return fullPath;
    }

    private static string TryComputeSha256(string path)
    {
        if (!File.Exists(path))
            return string.Empty;
        try
        {
            return SampleEvidenceFileIo.Read(
                    path,
                    SampleEvidenceFileIo.MaximumJsonBytes,
                    "Material/GI capture manifest")
                .Sha256;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void RequireValue(string value, string role, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{role} renderer provenance '{name}' is empty.");
    }

    private static void RequireEqual<T>(T reference, T candidate, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(reference, candidate))
            throw new InvalidDataException($"Capture provenance mismatch for {name}.");
    }

    private static string DescribeException(Exception exception)
    {
        string message = string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
        return $"{exception.GetType().Name}: {message}";
    }
}

/// <summary>
/// Early-exit command-line surface. It intentionally runs before window/Vulkan
/// initialization so CI can compare two completed captures on any host.
/// </summary>
public static class SampleMaterialGiComparisonCli
{
    public const string CompareOption = "--compare-material-gi-captures";
    public const string ReportOption = "--material-gi-comparison-report";

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
                    $"{CompareOption} requires <graphics-capture-dir> <async-capture-dir>.");
            }

            string referenceDirectory = RequireValue(args[compareIndex + 1], "graphics capture directory");
            string candidateDirectory = RequireValue(args[compareIndex + 2], "async capture directory");
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
                        "comparison report path");
                    consumed.Add(index);
                    continue;
                }
                if (string.Equals(argument, ReportOption, StringComparison.Ordinal))
                {
                    if (reportPath != null)
                        throw new ArgumentException($"{ReportOption} may be specified only once.");
                    if (index + 1 >= args.Length)
                        throw new ArgumentException($"{ReportOption} requires a path.");
                    reportPath = RequireValue(args[index + 1], "comparison report path");
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
                SampleMaterialGiCaptureComparer.DefaultReportFileName);
            SampleMaterialGiCaptureComparisonReport report =
                SampleMaterialGiCaptureComparer.Compare(referenceDirectory, candidateDirectory);
            SampleMaterialGiCaptureComparer.WriteReportAtomic(reportPath, report);
            if (report.Passed)
            {
                output.WriteLine(
                    $"Material/GI graphics/async equivalence passed: outputs={report.Outputs.Count} " +
                    $"report={Path.GetFullPath(reportPath)}");
                exitCode = 0;
            }
            else
            {
                error.WriteLine(
                    $"Material/GI graphics/async equivalence failed: {report.FailureReason} " +
                    $"report={Path.GetFullPath(reportPath)}");
                exitCode = 2;
            }

            return true;
        }
        catch (Exception exception)
        {
            error.WriteLine(
                $"Material/GI comparison command failed: {exception.GetType().Name}: {exception.Message}");
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
