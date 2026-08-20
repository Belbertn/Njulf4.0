using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;

namespace NjulfHelloGame;

public sealed record SampleBenchmarkHdrDifference(
    bool Available,
    bool Passed,
    string ReferencePath,
    string CandidatePath,
    string ReferenceSha256,
    string CandidateSha256,
    int Width,
    int Height,
    double Rmse,
    double RelativeRmse,
    double MeanAbsoluteError,
    double MaximumAbsoluteError,
    double MaximumRelativeRmse,
    string FailureReason)
{
    public const double DefaultMaximumRelativeRmse = 0.12;

    public double FlipP95 { get; init; }
    public double MaximumFlipP95 { get; init; } = 0.02;
    public string QualityContractPath { get; init; } = string.Empty;
    public string QualityContractSha256 { get; init; } = string.Empty;
    public IReadOnlyList<SampleBenchmarkHdrRoiResult> RoiResults { get; init; } =
        Array.Empty<SampleBenchmarkHdrRoiResult>();

    public static SampleBenchmarkHdrDifference Unavailable(string reason) => new(
        false,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        0.0,
        0.0,
        0.0,
        0.0,
        DefaultMaximumRelativeRmse,
        string.IsNullOrWhiteSpace(reason) ? "HDR comparison was not supplied." : reason.Trim());
}

public static class SampleBenchmarkHdrComparer
{
    private const double MinimumReferenceEnergy = 1.0e-20;

    public static SampleBenchmarkHdrDifference Compare(
        string referencePath,
        string candidatePath,
        double maximumRelativeRmse = SampleBenchmarkHdrDifference.DefaultMaximumRelativeRmse,
        double maximumFlipP95 = 0.02,
        string? qualityContractPath = null)
    {
        if (!double.IsFinite(maximumRelativeRmse) || maximumRelativeRmse < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maximumRelativeRmse));
        if (!double.IsFinite(maximumFlipP95) || maximumFlipP95 < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maximumFlipP95));

        SampleEvidenceFileContent referenceEvidence = SampleEvidenceFileIo.Read(
            NormalizePfmPath(referencePath, nameof(referencePath)),
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            "Benchmark HDR reference");
        SampleEvidenceFileContent candidateEvidence = SampleEvidenceFileIo.Read(
            NormalizePfmPath(candidatePath, nameof(candidatePath)),
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            "Benchmark HDR candidate");
        SampleEvidenceFileContent? qualityContractEvidence =
            string.IsNullOrWhiteSpace(qualityContractPath)
                ? null
                : SampleEvidenceFileIo.Read(
                    Path.GetFullPath(qualityContractPath),
                    SampleEvidenceFileIo.MaximumJsonBytes,
                    "Benchmark HDR quality contract");
        return Compare(
            referenceEvidence,
            candidateEvidence,
            maximumRelativeRmse,
            maximumFlipP95,
            qualityContractEvidence);
    }

    internal static SampleBenchmarkHdrDifference Compare(
        SampleEvidenceFileContent referenceEvidence,
        SampleEvidenceFileContent candidateEvidence,
        double maximumRelativeRmse,
        double maximumFlipP95,
        SampleEvidenceFileContent? qualityContractEvidence)
    {
        if (!double.IsFinite(maximumRelativeRmse) || maximumRelativeRmse < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maximumRelativeRmse));
        if (!double.IsFinite(maximumFlipP95) || maximumFlipP95 < 0.0)
            throw new ArgumentOutOfRangeException(nameof(maximumFlipP95));
        LinearFloatImage reference = PfmLinearImageCodec.Decode(referenceEvidence.Bytes);
        LinearFloatImage candidate = PfmLinearImageCodec.Decode(candidateEvidence.Bytes);
        if (reference.Width != candidate.Width || reference.Height != candidate.Height)
        {
            throw new InvalidDataException(
                $"HDR candidate extent {candidate.Width}x{candidate.Height} differs from " +
                $"reference extent {reference.Width}x{reference.Height}.");
        }
        if (reference.Pixels.Length != candidate.Pixels.Length ||
            reference.Pixels.Length != checked(reference.Width * reference.Height * 3))
        {
            throw new InvalidDataException("HDR reference or candidate has an invalid RGB payload length.");
        }

        double squaredError = 0.0;
        double squaredReference = 0.0;
        double absoluteError = 0.0;
        double maximumAbsoluteError = 0.0;
        for (int index = 0; index < reference.Pixels.Length; index++)
        {
            float referenceValue = reference.Pixels[index];
            float candidateValue = candidate.Pixels[index];
            if (!float.IsFinite(referenceValue) || !float.IsFinite(candidateValue))
            {
                throw new InvalidDataException(
                    $"HDR comparison contains a non-finite component at scalar index {index}.");
            }

            double difference = candidateValue - referenceValue;
            double magnitude = Math.Abs(difference);
            squaredError += difference * difference;
            squaredReference += (double)referenceValue * referenceValue;
            absoluteError += magnitude;
            maximumAbsoluteError = Math.Max(maximumAbsoluteError, magnitude);
        }

        int sampleCount = reference.Pixels.Length;
        double rmse = Math.Sqrt(squaredError / sampleCount);
        double relativeRmse = Math.Sqrt(
            squaredError / Math.Max(squaredReference, MinimumReferenceEnergy));
        double flipP95 =
            SampleMaterialGiHdrFlipMetric.ComputeP95(reference, candidate);
        SampleBenchmarkHdrRoiEvaluation roiEvaluation =
            SampleBenchmarkHdrQualityContractEvaluator.Evaluate(
                qualityContractEvidence,
                reference,
                candidate);
        SampleBenchmarkHdrRoiResult[] failedRois =
            roiEvaluation.Results.Where(static result => !result.Passed).ToArray();
        bool passed = relativeRmse <= maximumRelativeRmse &&
            flipP95 <= maximumFlipP95 &&
            failedRois.Length == 0;
        var failureReasons = new List<string>();
        if (relativeRmse > maximumRelativeRmse)
        {
            failureReasons.Add(
                $"HDR relative RMSE {relativeRmse:R} exceeds {maximumRelativeRmse:R}.");
        }
        if (flipP95 > maximumFlipP95)
        {
            failureReasons.Add(
                $"HDR-FLIP P95 {flipP95:R} exceeds {maximumFlipP95:R}.");
        }
        if (failedRois.Length > 0)
        {
            failureReasons.Add(
                "HDR ROI gates failed: " + string.Join(
                    ", ",
                    failedRois.Select(static roi =>
                        $"{roi.Name}(mean={roi.MeanLuminanceShift:R}/" +
                        $"{roi.MaximumMeanLuminanceShift:R}, p95=" +
                        $"{roi.P95LuminanceShift:R}/" +
                        $"{roi.MaximumP95LuminanceShift:R})")) + ".");
        }
        return new SampleBenchmarkHdrDifference(
            true,
            passed,
            referenceEvidence.Path,
            candidateEvidence.Path,
            referenceEvidence.Sha256,
            candidateEvidence.Sha256,
            reference.Width,
            reference.Height,
            rmse,
            relativeRmse,
            absoluteError / sampleCount,
            maximumAbsoluteError,
            maximumRelativeRmse,
            string.Join(" ", failureReasons))
        {
            FlipP95 = flipP95,
            MaximumFlipP95 = maximumFlipP95,
            QualityContractPath = roiEvaluation.ContractPath,
            QualityContractSha256 = roiEvaluation.ContractSha256,
            RoiResults = roiEvaluation.Results
        };
    }

    private static string NormalizePfmPath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A PFM path is required.", parameterName);
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".pfm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Benchmark HDR evidence must use linear RGB PFM files.", parameterName);
        return fullPath;
    }
}

public sealed record SampleShaderStageProfile(
    string Pass,
    string Shader,
    string Variant,
    int LiveRegisters,
    long SpillBytes,
    long LocalMemoryBytes,
    double OccupancyPercent,
    double ThreadCoherencePercent,
    long TextureLoadCount,
    long StorageLoadCount,
    long InstructionCount,
    IReadOnlyList<string> SampledDependencyReasons);

/// <summary>
/// Portable summary exported from an Nsight Graphics shader profile. The
/// identity fields prevent a profile from an older executable or shader bundle
/// from being attached to a benchmark accidentally.
/// </summary>
public sealed record SampleShaderProfileArtifact(
    string Schema,
    string Tool,
    string ToolVersion,
    string GpuDeviceName,
    string DriverVersion,
    string ExecutableHash,
    string ShaderBundleHash,
    IReadOnlyList<SampleShaderStageProfile> Stages)
{
    public const string CurrentSchema = "njulf-nsight-shader-profile-v1";
}

public sealed record SampleShaderProfileEvidence(
    bool Available,
    string ArtifactPath,
    string ArtifactSha256,
    string Tool,
    string ToolVersion,
    IReadOnlyList<SampleShaderStageProfile> Stages,
    string UnavailableReason)
{
    public static SampleShaderProfileEvidence Unavailable(string reason) => new(
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        Array.Empty<SampleShaderStageProfile>(),
        string.IsNullOrWhiteSpace(reason)
            ? "Nsight shader-profile evidence was not supplied."
            : reason.Trim());
}

public static class SampleShaderProfileEvidenceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public static SampleShaderProfileEvidence Load(
        string? artifactPath,
        RendererDiagnostics diagnostics)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
            return SampleShaderProfileEvidence.Unavailable(
                "Nsight shader-profile evidence was not supplied.");

        try
        {
            SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
                Path.GetFullPath(artifactPath),
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Nsight shader-profile artifact");
            SampleEvidenceFileIo.ValidateStrictJson(
                evidence.Bytes,
                JsonOptions.MaxDepth,
                "Nsight shader-profile artifact");
            SampleShaderProfileArtifact artifact =
                JsonSerializer.Deserialize<SampleShaderProfileArtifact>(evidence.Bytes, JsonOptions)
                ?? throw new InvalidDataException("Nsight shader-profile artifact deserialized to null.");
            Validate(artifact, diagnostics);
            return new SampleShaderProfileEvidence(
                true,
                evidence.Path,
                evidence.Sha256,
                artifact.Tool,
                artifact.ToolVersion,
                artifact.Stages.ToArray(),
                string.Empty);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                IOException or
                InvalidDataException or
                JsonException or
                UnauthorizedAccessException)
        {
            return SampleShaderProfileEvidence.Unavailable(
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void Validate(
        SampleShaderProfileArtifact artifact,
        RendererDiagnostics diagnostics)
    {
        if (!string.Equals(
                artifact.Schema,
                SampleShaderProfileArtifact.CurrentSchema,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Shader-profile schema '{artifact.Schema}' is not " +
                $"'{SampleShaderProfileArtifact.CurrentSchema}'.");
        }
        RequireText(artifact.Tool, nameof(artifact.Tool));
        RequireText(artifact.ToolVersion, nameof(artifact.ToolVersion));
        if (!artifact.Tool.Contains("Nsight Graphics", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Shader-profile evidence must be exported from NVIDIA Nsight Graphics.");
        }
        RequireIdentity(artifact.GpuDeviceName, diagnostics.CaptureGpuDeviceName, "GPU");
        RequireIdentity(artifact.DriverVersion, diagnostics.CaptureGpuDriverVersion, "driver");
        RequireIdentity(artifact.ExecutableHash, diagnostics.CaptureRun.ExecutableHash, "executable");
        RequireIdentity(artifact.ShaderBundleHash, diagnostics.CaptureRun.ShaderBundleHash, "shader bundle");
        if (artifact.Stages == null || artifact.Stages.Count == 0)
            throw new InvalidDataException("Shader-profile artifact contains no stages.");

        foreach (SampleShaderStageProfile stage in artifact.Stages)
        {
            RequireText(stage.Pass, nameof(stage.Pass));
            RequireText(stage.Shader, nameof(stage.Shader));
            RequireText(stage.Variant, nameof(stage.Variant));
            if (stage.LiveRegisters <= 0 || stage.SpillBytes < 0 ||
                stage.LocalMemoryBytes < 0 || stage.TextureLoadCount < 0 ||
                stage.StorageLoadCount < 0 || stage.InstructionCount <= 0)
            {
                throw new InvalidDataException(
                    $"Shader profile '{stage.Pass}/{stage.Shader}' contains an invalid count metric.");
            }
            RequirePercentage(stage.OccupancyPercent, "occupancy", stage);
            RequirePercentage(stage.ThreadCoherencePercent, "thread coherence", stage);
            if (stage.SampledDependencyReasons == null ||
                stage.SampledDependencyReasons.Count == 0 ||
                stage.SampledDependencyReasons.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidDataException(
                    $"Shader profile '{stage.Pass}/{stage.Shader}' omits dependency reasons; " +
                    "use 'none observed' when Nsight reports none.");
            }
        }

        bool hasOpaqueForward = artifact.Stages.Any(static stage =>
            string.Equals(stage.Pass, "ForwardPlusPass", StringComparison.Ordinal) &&
            stage.Shader.Contains("forward", StringComparison.OrdinalIgnoreCase));
        bool hasGeometryDecal = artifact.Stages.Any(static stage =>
            (string.Equals(stage.Pass, "TransparentPasses", StringComparison.Ordinal) ||
             stage.Pass.Contains("decal", StringComparison.OrdinalIgnoreCase)) &&
            stage.Variant.Contains("decal", StringComparison.OrdinalIgnoreCase));
        if (!hasOpaqueForward || !hasGeometryDecal)
        {
            throw new InvalidDataException(
                "Shader-profile evidence must contain both ForwardPlusPass opaque-forward " +
                "and geometry-decal fragment stages.");
        }
    }

    private static void RequireIdentity(string actual, string expected, string role)
    {
        RequireText(actual, role);
        RequireText(expected, role + " capture identity");
        if (!string.Equals(actual.Trim(), expected.Trim(), StringComparison.Ordinal))
            throw new InvalidDataException($"Shader-profile {role} identity does not match the benchmark.");
    }

    private static void RequirePercentage(
        double value,
        string role,
        SampleShaderStageProfile stage)
    {
        if (!double.IsFinite(value) || value < 0.0 || value > 100.0)
        {
            throw new InvalidDataException(
                $"Shader profile '{stage.Pass}/{stage.Shader}' has invalid {role} {value:R}.");
        }
    }

    private static void RequireText(string? value, string role)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Shader-profile {role} is absent.");
    }
}
