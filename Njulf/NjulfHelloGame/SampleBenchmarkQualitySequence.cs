using System.Globalization;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

public enum SampleBenchmarkQualitySequenceRole : byte
{
    Canonical,
    Repeat,
    Candidate
}

/// <summary>
/// A standalone, timing-ineligible linear-HDR route replay. It deliberately
/// does not share the benchmark timing report or its post-window endpoint
/// capture.
/// </summary>
public sealed record SampleBenchmarkQualitySequenceOptions
{
    public static SampleBenchmarkQualitySequenceOptions Disabled { get; } = new();

    public bool Enabled { get; init; }
    public SampleBenchmarkQualitySequenceRole Role { get; init; }
    public string SequenceId { get; init; } = string.Empty;
    public string ReportPath { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public int WarmupFrameCount { get; init; }
    public int MaximumAdditionalSettlingFrameCount { get; init; } =
        SampleBenchmarkOptions.ProductionMinimumAdditionalSettlingFrameCount;
    public int MaximumReadbackDrainFrameCount { get; init; } = 240;
    public RenderBudgetProfileKind? BudgetProfileOverride { get; init; }
    public string CaptureVariant { get; init; } =
        SampleBenchmarkCaptureVariant.Baseline;
    public SampleSceneKind SceneKind { get; init; } =
        SampleSceneKind.GlobalIlluminationTest;
    public SamplePerformanceScenario Scenario { get; init; } =
        SamplePerformanceScenario.Normal;
    public SampleBenchmarkTrajectoryKind Trajectory { get; init; } =
        SampleBenchmarkTrajectoryKind.Stationary;
    public string TrajectoryFingerprint { get; init; } = string.Empty;
    public SampleBistroQualityCaptureVariant TrajectoryBistroVariant { get; init; } =
        SampleBistroQualityCaptureVariant.SunScaleStep;
    public string ReferenceContractPath { get; init; } = string.Empty;
    public string HdrQualityContractPath { get; init; } = string.Empty;
    public double HdrMaximumRelativeRmse { get; init; } = 0.005;
    public double HdrMaximumFlipP95 { get; init; } = 0.02;
}

public readonly record struct SampleBenchmarkQualityTemporalPair(
    int FromRouteFrameIndex,
    int ToRouteFrameIndex);

public static class SampleBenchmarkQualityWorkloadIdentity
{
    public static string GetCaptureSceneKind(SampleSceneKind sceneKind) =>
        sceneKind switch
        {
            SampleSceneKind.SponzaPlaza => "Sponza",
            SampleSceneKind.Bistro => "Bistro",
            _ => sceneKind.ToString()
        };
}

public static class SampleBenchmarkQualityCheckpointCatalog
{
    public const int RequiredWidth = 1920;
    public const int RequiredHeight = 1080;

    private static readonly ReadOnlyCollection<int> Stationary =
        Array.AsReadOnly<int>([0]);
    private static readonly ReadOnlyCollection<int> Bistro =
        Array.AsReadOnly<int>([0, 59, 60, 61, 68, 76, 179, 180, 181, 239]);
    private static readonly ReadOnlyCollection<int> SponzaHorizontal =
        Array.AsReadOnly<int>(
            [0, 1, 118, 119, 120, 121, 178, 179, 180, 181, 298, 299]);
    private static readonly ReadOnlyCollection<int> SponzaVertical =
        Array.AsReadOnly<int>([0, 1, 239, 240, 479, 480, 719, 720, 958, 959]);

    public static IReadOnlyList<int> GetCheckpointIndices(
        SampleBenchmarkTrajectoryKind trajectory) => trajectory switch
        {
            SampleBenchmarkTrajectoryKind.BistroLoop => Bistro,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal => SponzaHorizontal,
            SampleBenchmarkTrajectoryKind.SponzaVertical => SponzaVertical,
            _ => Stationary
        };

    public static IReadOnlyList<SampleBenchmarkQualityTemporalPair>
        GetTemporalPairs(SampleBenchmarkTrajectoryKind trajectory)
    {
        IReadOnlyList<int> checkpoints = GetCheckpointIndices(trajectory);
        var pairs = new List<SampleBenchmarkQualityTemporalPair>();
        for (int index = 1; index < checkpoints.Count; index++)
        {
            if (checkpoints[index] == checkpoints[index - 1] + 1)
            {
                pairs.Add(new SampleBenchmarkQualityTemporalPair(
                    checkpoints[index - 1],
                    checkpoints[index]));
            }
        }
        return Array.AsReadOnly(pairs.ToArray());
    }

    public static string CreateFingerprint(
        SampleBenchmarkTrajectoryKind trajectory)
    {
        var canonical = new StringBuilder(
            "njulf-benchmark-quality-checkpoints/v1|");
        canonical.Append(SampleBenchmarkTrajectory.GetName(trajectory))
            .Append('|')
            .Append(SampleBenchmarkTrajectory.GetFrameCount(trajectory)
                .ToString(CultureInfo.InvariantCulture))
            .Append('|')
            .AppendJoin(',', GetCheckpointIndices(trajectory))
            .Append('|');
        foreach (SampleBenchmarkQualityTemporalPair pair in
                 GetTemporalPairs(trajectory))
        {
            canonical.Append(pair.FromRouteFrameIndex)
                .Append("->")
                .Append(pair.ToRouteFrameIndex)
                .Append(',');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    public static void RequireExactCheckpointOrder(
        SampleBenchmarkTrajectoryKind trajectory,
        IReadOnlyList<int>? actual,
        string role)
    {
        IReadOnlyList<int> expected = GetCheckpointIndices(trajectory);
        if (actual == null || actual.Count != expected.Count)
        {
            throw new InvalidDataException(
                $"{role} must contain exactly {expected.Count} authored checkpoints.");
        }
        for (int index = 0; index < expected.Count; index++)
        {
            if (actual[index] != expected[index])
            {
                throw new InvalidDataException(
                    $"{role} checkpoint {index} must be route frame " +
                    $"{expected[index]}, not {actual[index]}.");
            }
        }
    }
}

public sealed record SampleBenchmarkQualitySequenceReferenceCheckpoint(
    int Ordinal,
    int RouteFrameIndex,
    int AbsoluteFrameIndex,
    string PfmPath,
    string PfmSha256,
    int Width,
    int Height,
    string CaptureToken,
    ulong DdgiFrameSerial,
    PerformanceCaptureCameraMetadata Camera,
    string SceneAssetHash,
    string SceneStateHash,
    ulong SceneContentRevision,
    string SettingsFingerprint,
    PerformanceCaptureRunMetadata CaptureRun,
    MaterialGiProducerIdentity ProducerIdentity);

public sealed record SampleBenchmarkQualityTemporalGate(
    int FromRouteFrameIndex,
    int ToRouteFrameIndex,
    double MaximumRelativeResidual);

public sealed record SampleBenchmarkQualitySequenceReferenceContract(
    string Schema,
    string SceneKind,
    string Scenario,
    string CaptureVariant,
    string BuildConfiguration,
    string Trajectory,
    string TrajectoryFingerprint,
    string TrajectoryRouteHash,
    string TrajectorySequenceHash,
    int TrajectoryFrameCount,
    int WarmupFrameCount,
    int MaximumAdditionalSettlingFrameCount,
    int MaximumReadbackDrainFrameCount,
    int FirstRouteAbsoluteFrameIndex,
    string CheckpointContractFingerprint,
    IReadOnlyList<int> CheckpointIndices,
    IReadOnlyList<SampleBenchmarkQualitySequenceReferenceCheckpoint> Checkpoints,
    string QualityContractPath,
    string QualityContractSha256,
    double MaximumRelativeRmse,
    double MaximumFlipP95,
    IReadOnlyList<SampleBenchmarkQualityTemporalGate> TemporalGates,
    PerformanceCaptureRunMetadata CaptureRun,
    MaterialGiProducerIdentity ProducerIdentity)
{
    public const string CurrentSchema =
        "njulf-benchmark-quality-sequence-reference/v1";

    public double TemporalResidualFloor { get; init; }
    public double TemporalResidualMultiplier { get; init; }
    public double TemporalResidualHardCeiling { get; init; }
    public IReadOnlyList<string> BaselineRepeatReportSha256 { get; init; } =
        Array.Empty<string>();
}

public sealed record SampleBenchmarkQualityCheckpointEvidence(
    int Ordinal,
    int RouteFrameIndex,
    int AbsoluteFrameIndex,
    string PfmPath,
    string PfmSha256,
    int Width,
    int Height,
    string CaptureToken,
    ulong DdgiFrameSerial,
    PerformanceCaptureCameraMetadata Camera,
    string SceneAssetHash,
    string SceneStateHash,
    ulong SceneContentRevision,
    string SettingsFingerprint,
    PerformanceCaptureRunMetadata CaptureRun,
    MaterialGiProducerIdentity ProducerIdentity,
    SampleBenchmarkHdrDifference HdrDifference);

public sealed record SampleBenchmarkQualityTemporalResult(
    int FromRouteFrameIndex,
    int ToRouteFrameIndex,
    double RelativeResidual,
    double? MaximumRelativeResidual,
    bool Passed);

internal sealed record SampleBenchmarkQualityRouteObservation(
    int RouteFrameIndex,
    SampleBenchmarkCameraPose PreDrawCamera,
    SampleBistroQualityFrameState? BistroFrameState,
    RendererDiagnostics Diagnostics,
    string SettingsFingerprint,
    MaterialGiProducerIdentity ProducerIdentity);

internal static class SampleBenchmarkQualityRouteSequenceHasher
{
    public static string Create(
        SampleBenchmarkQualitySequenceOptions options,
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observations);
        int expectedFrameCount =
            SampleBenchmarkTrajectory.GetFrameCount(options.Trajectory);
        if (observations.Count != expectedFrameCount)
        {
            throw new InvalidDataException(
                $"Observed route contains {observations.Count} of " +
                $"{expectedFrameCount} authored frames.");
        }

        var canonical = new StringBuilder();
        canonical.Append("njulf-benchmark-quality-route-sequence/v1|")
            .Append(SampleBenchmarkTrajectory.GetName(options.Trajectory))
            .Append('|')
            .Append(options.TrajectoryFingerprint)
            .Append('|')
            .Append(expectedFrameCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        for (int index = 0; index < observations.Count; index++)
        {
            SampleBenchmarkQualityRouteObservation observation = observations[index];
            if (observation.RouteFrameIndex != index)
            {
                throw new InvalidDataException(
                    $"Observed route frame {index} was reordered or duplicated.");
            }
            AppendPose(canonical, observation.PreDrawCamera);
            AppendBistroState(canonical, observation.BistroFrameState);
            PerformanceCaptureCameraMetadata camera =
                observation.Diagnostics.CaptureCamera;
            canonical.Append(camera.PositionX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionZ.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.YawRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PitchRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FarPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.ViewHash).Append('|')
                .Append(camera.ProjectionHash).Append('|');
            AppendRelativeCameraCut(canonical, observations, index);
            canonical
                .Append(observation.Diagnostics.CaptureSceneAssetHash).Append('|')
                .Append(observation.Diagnostics.CaptureSceneStateHash).Append('|')
                .Append(observation.Diagnostics.CaptureSceneContentRevision.ToString(
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(observation.SettingsFingerprint).Append('|')
                .Append(observation.ProducerIdentity.GpuName).Append('|')
                .Append(observation.ProducerIdentity.DriverVersion).Append('|')
                .Append(observation.ProducerIdentity.QualityTier).Append('|')
                .Append(observation.Diagnostics.CaptureRun.SceneKind).Append('|')
                .Append(observation.Diagnostics.CaptureRun.Scenario).Append('|')
                .Append(observation.Diagnostics.CaptureRun.BuildConfiguration).Append('|')
                .Append(observation.Diagnostics.CaptureRun.SettingsSchemaVersion.ToString(
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(observation.Diagnostics.ActiveFeatureIsolation).Append('|')
                .Append(observation.Diagnostics.GlobalIlluminationDebugView)
                .Append('\n');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendRelativeCameraCut(
        StringBuilder canonical,
        IReadOnlyList<SampleBenchmarkQualityRouteObservation> observations,
        int index)
    {
        if (index == 0)
        {
            canonical.Append("origin|");
            return;
        }
        RendererDiagnostics previous = observations[index - 1].Diagnostics;
        RendererDiagnostics current = observations[index].Diagnostics;
        ulong previousSerial = previous.CaptureCamera.CameraCutSerial;
        ulong currentSerial = current.CaptureCamera.CameraCutSerial;
        if (currentSerial >= previousSerial)
        {
            canonical.Append("delta:")
                .Append((currentSerial - previousSerial).ToString(
                    CultureInfo.InvariantCulture))
                .Append('|');
            return;
        }
        if (current.CaptureSceneContentRevision ==
            previous.CaptureSceneContentRevision)
        {
            throw new InvalidDataException(
                $"Camera-cut serial regressed within unchanged scene revision at route frame {index}.");
        }
        canonical.Append("scene-reset:")
            .Append(currentSerial.ToString(CultureInfo.InvariantCulture))
            .Append('|');
    }

    private static void AppendPose(
        StringBuilder canonical,
        SampleBenchmarkCameraPose pose)
    {
        canonical.Append(pose.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Position.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Yaw.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Pitch.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.FieldOfView.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.FarPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|');
    }

    private static void AppendBistroState(
        StringBuilder canonical,
        SampleBistroQualityFrameState? state)
    {
        if (state == null)
        {
            canonical.Append("none|");
            return;
        }
        canonical.Append(state.AbsoluteFrameIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.LoopFrameIndex.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(state.DirectionalLightScale.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(state.DirectionalLightYawOffsetRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(state.ReflectionCaptureIncludesDdgi ? '1' : '0').Append('|')
            .Append(state.LightingEventActive ? '1' : '0').Append('|');
    }
}

public sealed record SampleBenchmarkQualitySequenceReport(
    string Kind,
    string Schema,
    DateTimeOffset CapturedAtUtc,
    SampleBenchmarkQualitySequenceRole Role,
    string SequenceId,
    string SceneKind,
    string Scenario,
    string CaptureVariant,
    string Trajectory,
    string TrajectoryFingerprint,
    string TrajectoryRouteHash,
    string TrajectorySequenceHash,
    int TrajectoryFrameCount,
    int FirstRouteAbsoluteFrameIndex,
    string CheckpointContractFingerprint,
    IReadOnlyList<int> CheckpointIndices,
    IReadOnlyList<SampleBenchmarkQualityCheckpointEvidence> Checkpoints,
    IReadOnlyList<SampleBenchmarkQualityTemporalResult> TemporalResiduals,
    bool Passed,
    IReadOnlyList<string> Failures)
{
    public const string CurrentKind =
        "njulf-renderer-benchmark-quality-sequence";
    public const string CurrentSchema =
        "njulf-renderer-benchmark-quality-sequence/v1";

    public bool TimingEligible { get; init; } = false;
    public bool ProductionTiming { get; init; } = false;
    public int WarmupFrameCount { get; init; }
    public int MaximumAdditionalSettlingFrameCount { get; init; }
    public int MaximumReadbackDrainFrameCount { get; init; }
    public int AdditionalSettlingFrameCount { get; init; }
    public bool SettlingWaitTimedOut { get; init; }
    public string ReferenceContractPath { get; init; } = string.Empty;
    public string ReferenceContractSha256 { get; init; } = string.Empty;
    public string BuildConfiguration { get; init; } = string.Empty;
    public PerformanceCaptureRunMetadata? CaptureRun { get; init; }
    public MaterialGiProducerIdentity? ProducerIdentity { get; init; }
}

internal sealed record SampleBenchmarkQualitySequenceLoadedReference(
    string Path,
    string Sha256,
    SampleBenchmarkQualitySequenceReferenceContract Contract,
    IReadOnlyList<SampleEvidenceFileContent> CheckpointPfmEvidence,
    SampleEvidenceFileContent QualityContractEvidence);

internal static class SampleBenchmarkQualitySequenceReferenceLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32
    };

    public static SampleBenchmarkQualitySequenceLoadedReference Load(
        SampleBenchmarkQualitySequenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ReferenceContractPath))
        {
            throw new InvalidDataException(
                "A repeat or candidate quality sequence requires a reference contract.");
        }
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            Path.GetFullPath(options.ReferenceContractPath),
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark quality-sequence reference contract");
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            JsonOptions.MaxDepth,
            "Benchmark quality-sequence reference contract");
        SampleBenchmarkQualitySequenceReferenceContract contract =
            JsonSerializer.Deserialize<SampleBenchmarkQualitySequenceReferenceContract>(
                evidence.Bytes,
                JsonOptions) ??
            throw new InvalidDataException(
                "Benchmark quality-sequence reference contract deserialized to null.");
        Validate(options, contract);
        SampleEvidenceFileContent[] checkpointEvidence = contract.Checkpoints
            .Select(checkpoint =>
            {
                SampleEvidenceFileContent pfm = SampleEvidenceFileIo.Read(
                    Path.GetFullPath(checkpoint.PfmPath),
                    SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
                    $"Admitted quality reference checkpoint {checkpoint.RouteFrameIndex}");
                RequireExact(pfm.Sha256, checkpoint.PfmSha256, "reference PFM hash");
                return pfm;
            })
            .ToArray();
        SampleEvidenceFileContent qualityEvidence = SampleEvidenceFileIo.Read(
            Path.GetFullPath(options.HdrQualityContractPath),
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Admitted benchmark quality-sequence ROI contract");
        RequireExact(
            qualityEvidence.Sha256,
            contract.QualityContractSha256,
            "quality contract hash");
        LinearFloatImage firstReference = PfmLinearImageCodec.Decode(
            checkpointEvidence[0].Bytes);
        _ = SampleBenchmarkHdrQualityContractEvaluator.Evaluate(
            qualityEvidence,
            firstReference,
            firstReference);
        return new SampleBenchmarkQualitySequenceLoadedReference(
            evidence.Path,
            evidence.Sha256,
            contract,
            Array.AsReadOnly(checkpointEvidence),
            qualityEvidence);
    }

    internal static void Validate(
        SampleBenchmarkQualitySequenceOptions options,
        SampleBenchmarkQualitySequenceReferenceContract contract)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contract);
        RequireExact(contract.Schema, SampleBenchmarkQualitySequenceReferenceContract.CurrentSchema, "schema");
        RequireExact(
            contract.SceneKind,
            SampleBenchmarkQualityWorkloadIdentity.GetCaptureSceneKind(
                options.SceneKind),
            "scene kind");
        RequireExact(contract.Scenario, options.Scenario.ToString(), "scenario");
        RequireExact(
            contract.CaptureVariant,
            SampleBenchmarkCaptureVariant.Normalize(options.CaptureVariant),
            "capture variant");
        RequireText(contract.BuildConfiguration, "reference build configuration");
        ValidateCaptureRun(contract.CaptureRun, "reference top-level capture run");
        ValidateProducer(contract.ProducerIdentity, "reference top-level producer");
        RequireExact(
            contract.CaptureRun.SceneKind,
            contract.SceneKind,
            "top-level CaptureRun scene");
        RequireExact(
            contract.CaptureRun.Scenario,
            contract.Scenario,
            "top-level CaptureRun scenario");
        RequireExact(
            contract.CaptureRun.BuildConfiguration,
            contract.BuildConfiguration,
            "top-level CaptureRun build configuration");
        RequireExact(
            contract.CaptureRun.Commit.ToLowerInvariant(),
            contract.ProducerIdentity.BuildCommit,
            "top-level producer commit");
        RequireExact(
            contract.CaptureRun.ShaderBundleHash[7..],
            contract.ProducerIdentity.ShaderFingerprint,
            "top-level producer shader");
        RequireExact(
            contract.Trajectory,
            SampleBenchmarkTrajectory.GetName(options.Trajectory),
            "trajectory");
        RequireExact(
            contract.TrajectoryFingerprint,
            options.TrajectoryFingerprint,
            "trajectory fingerprint");
        RequireSha256Identity(contract.TrajectoryRouteHash, "trajectory route hash");
        RequireSha256Identity(
            contract.TrajectorySequenceHash,
            "trajectory sequence hash");
        if (contract.TrajectoryFrameCount !=
            SampleBenchmarkTrajectory.GetFrameCount(options.Trajectory))
        {
            throw new InvalidDataException(
                "Quality-sequence reference trajectory frame count differs from the authored route.");
        }
        ValidateExecutionBounds(
            options,
            contract.WarmupFrameCount,
            contract.MaximumAdditionalSettlingFrameCount,
            contract.MaximumReadbackDrainFrameCount);
        if (contract.FirstRouteAbsoluteFrameIndex < 0)
        {
            throw new InvalidDataException(
                "Quality-sequence reference first route frame is invalid.");
        }
        RequireExact(
            contract.CheckpointContractFingerprint,
            SampleBenchmarkQualityCheckpointCatalog.CreateFingerprint(
                options.Trajectory),
            "checkpoint contract fingerprint");
        SampleBenchmarkQualityCheckpointCatalog.RequireExactCheckpointOrder(
            options.Trajectory,
            contract.CheckpointIndices,
            "Quality-sequence reference");
        if (contract.Checkpoints == null ||
            contract.Checkpoints.Count != contract.CheckpointIndices.Count)
        {
            throw new InvalidDataException(
                "Quality-sequence reference checkpoint evidence is incomplete.");
        }

        var referencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referenceTokens = new HashSet<string>(StringComparer.Ordinal);
        ulong previousFrameSerial = 0;
        ulong firstCheckpointFrameSerial = 0;
        string? invariantSceneAssetHash = null;
        for (int index = 0; index < contract.Checkpoints.Count; index++)
        {
            SampleBenchmarkQualitySequenceReferenceCheckpoint checkpoint =
                contract.Checkpoints[index] ??
                throw new InvalidDataException(
                    "Quality-sequence reference contains a null checkpoint.");
            if (checkpoint.Ordinal != index ||
                checkpoint.RouteFrameIndex != contract.CheckpointIndices[index] ||
                checkpoint.AbsoluteFrameIndex != checked(
                    contract.FirstRouteAbsoluteFrameIndex +
                    checkpoint.RouteFrameIndex))
            {
                throw new InvalidDataException(
                    $"Quality-sequence reference checkpoint {index} is reordered or mislabeled.");
            }
            RequireCanonicalToken(checkpoint.CaptureToken, "reference capture token");
            if (!referenceTokens.Add(checkpoint.CaptureToken))
            {
                throw new InvalidDataException(
                    "Quality-sequence reference capture tokens must be unique.");
            }
            RequireSha256(checkpoint.PfmSha256, "reference PFM hash");
            RequireSha256Identity(checkpoint.SettingsFingerprint, "reference settings fingerprint");
            RequireSha256Identity(checkpoint.SceneAssetHash, "reference scene asset hash");
            RequireSha256Identity(checkpoint.SceneStateHash, "reference scene state hash");
            ValidateCamera(checkpoint.Camera, "reference camera");
            ValidateProducer(checkpoint.ProducerIdentity, "reference producer");
            ValidateCaptureRun(checkpoint.CaptureRun, "reference capture run");
            RequireExact(
                checkpoint.SettingsFingerprint[7..],
                checkpoint.ProducerIdentity.SettingsFingerprint,
                "checkpoint and producer settings fingerprint");
            RequireCaptureRunEqual(
                checkpoint.CaptureRun,
                contract.CaptureRun,
                "reference checkpoint CaptureRun");
            RequireProducerEqual(
                checkpoint.ProducerIdentity,
                contract.ProducerIdentity,
                "reference checkpoint producer");
            // Frame serial zero is a valid first submitted renderer frame; only
            // MaxValue is the explicit unavailable sentinel. Exact route-frame
            // alignment below still proves ownership for zero-based sequences.
            if (checkpoint.DdgiFrameSerial == ulong.MaxValue ||
                (index > 0 && checkpoint.DdgiFrameSerial <= previousFrameSerial))
            {
                throw new InvalidDataException(
                    "Reference DDGI frame serials must be available and strictly increasing.");
            }
            if (index == 0)
                firstCheckpointFrameSerial = checkpoint.DdgiFrameSerial;
            if (checkpoint.DdgiFrameSerial != checked(
                    firstCheckpointFrameSerial +
                    (ulong)checkpoint.RouteFrameIndex))
            {
                throw new InvalidDataException(
                    "Reference DDGI frame serial does not match its labeled route frame.");
            }
            previousFrameSerial = checkpoint.DdgiFrameSerial;
            if (checkpoint.Width !=
                    SampleBenchmarkQualityCheckpointCatalog.RequiredWidth ||
                checkpoint.Height !=
                    SampleBenchmarkQualityCheckpointCatalog.RequiredHeight)
            {
                throw new InvalidDataException(
                    "Reference PFM extent must be the campaign's exact " +
                    $"{SampleBenchmarkQualityCheckpointCatalog.RequiredWidth}x" +
                    $"{SampleBenchmarkQualityCheckpointCatalog.RequiredHeight} quality extent.");
            }
            string path = RequirePfmPath(checkpoint.PfmPath, "reference PFM path");
            if (!referencePaths.Add(path))
            {
                throw new InvalidDataException(
                    "Quality-sequence reference PFM paths must be unique.");
            }
            invariantSceneAssetHash ??= checkpoint.SceneAssetHash;
            RequireExact(
                checkpoint.SceneAssetHash,
                invariantSceneAssetHash,
                "invariant scene asset hash");
            SampleEvidenceFileContent pfm = SampleEvidenceFileIo.Read(
                path,
                SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
                $"Quality-sequence reference checkpoint {checkpoint.RouteFrameIndex}");
            RequireExact(pfm.Sha256, checkpoint.PfmSha256, "reference PFM hash");
            LinearFloatImage image = PfmLinearImageCodec.Decode(pfm.Bytes);
            if (image.Width != checkpoint.Width || image.Height != checkpoint.Height)
            {
                throw new InvalidDataException(
                    $"Reference checkpoint {checkpoint.RouteFrameIndex} PFM extent changed.");
            }
        }

        if (!double.IsFinite(contract.MaximumRelativeRmse) ||
            contract.MaximumRelativeRmse < 0.0 ||
            contract.MaximumRelativeRmse != options.HdrMaximumRelativeRmse ||
            !double.IsFinite(contract.MaximumFlipP95) ||
            contract.MaximumFlipP95 < 0.0 ||
            contract.MaximumFlipP95 != options.HdrMaximumFlipP95)
        {
            throw new InvalidDataException(
                "Quality-sequence image thresholds differ from the locked reference contract.");
        }
        string qualityPath = Path.GetFullPath(options.HdrQualityContractPath);
        RequireExactPath(contract.QualityContractPath, qualityPath, "quality contract path");
        RequireSha256(contract.QualityContractSha256, "quality contract hash");
        RequireExact(
            SampleEvidenceFileIo.Read(
                qualityPath,
                SampleEvidenceFileIo.MaximumJsonBytes,
                "Benchmark quality-sequence ROI contract").Sha256,
            contract.QualityContractSha256,
            "quality contract hash");

        IReadOnlyList<SampleBenchmarkQualityTemporalPair> expectedPairs =
            SampleBenchmarkQualityCheckpointCatalog.GetTemporalPairs(
                options.Trajectory);
        IReadOnlyList<SampleBenchmarkQualityTemporalGate> gates =
            contract.TemporalGates ?? Array.Empty<SampleBenchmarkQualityTemporalGate>();
        if (options.Role == SampleBenchmarkQualitySequenceRole.Candidate)
        {
            if (gates.Count != expectedPairs.Count)
            {
                throw new InvalidDataException(
                    "Candidate quality sequence requires one locked temporal gate per declared adjacent pair.");
            }
            ValidateTemporalDerivation(contract);
            for (int index = 0; index < gates.Count; index++)
            {
                SampleBenchmarkQualityTemporalGate gate = gates[index] ??
                    throw new InvalidDataException(
                        "Quality-sequence reference contains a null temporal gate.");
                if (gate.FromRouteFrameIndex != expectedPairs[index].FromRouteFrameIndex ||
                    gate.ToRouteFrameIndex != expectedPairs[index].ToRouteFrameIndex)
                {
                    throw new InvalidDataException(
                        $"Temporal gate {index} is missing, duplicated, or reordered.");
                }
                if (!double.IsFinite(gate.MaximumRelativeResidual) ||
                    gate.MaximumRelativeResidual < contract.TemporalResidualFloor ||
                    gate.MaximumRelativeResidual > contract.TemporalResidualHardCeiling)
                {
                    throw new InvalidDataException(
                        $"Temporal gate {index} lies outside the locked derivation bounds.");
                }
            }
        }
        else if (options.Role == SampleBenchmarkQualitySequenceRole.Repeat &&
                 gates.Count != 0)
        {
            throw new InvalidDataException(
                "Baseline repeat quality sequences must observe temporal residuals before thresholds are derived.");
        }
    }

    internal static void ValidateExecutionBounds(
        SampleBenchmarkQualitySequenceOptions options,
        int warmupFrameCount,
        int maximumAdditionalSettlingFrameCount,
        int maximumReadbackDrainFrameCount)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (warmupFrameCount != options.WarmupFrameCount)
        {
            throw new InvalidDataException(
                "Quality-sequence reference warmup frame count differs from the requested workload.");
        }
        if (maximumAdditionalSettlingFrameCount !=
            options.MaximumAdditionalSettlingFrameCount)
        {
            throw new InvalidDataException(
                "Quality-sequence reference maximum settling frame count differs from the requested workload.");
        }
        if (maximumReadbackDrainFrameCount !=
            options.MaximumReadbackDrainFrameCount)
        {
            throw new InvalidDataException(
                "Quality-sequence reference maximum readback-drain frame count differs from the requested workload.");
        }
    }

    private static void ValidateTemporalDerivation(
        SampleBenchmarkQualitySequenceReferenceContract contract)
    {
        if (!double.IsFinite(contract.TemporalResidualFloor) ||
            contract.TemporalResidualFloor < 0.0 ||
            !double.IsFinite(contract.TemporalResidualMultiplier) ||
            contract.TemporalResidualMultiplier <= 1.0 ||
            !double.IsFinite(contract.TemporalResidualHardCeiling) ||
            contract.TemporalResidualHardCeiling < contract.TemporalResidualFloor)
        {
            throw new InvalidDataException(
                "Temporal residual floor, multiplier, or hard ceiling is invalid.");
        }
        if (contract.BaselineRepeatReportSha256 == null ||
            contract.BaselineRepeatReportSha256.Count < 2 ||
            contract.BaselineRepeatReportSha256
                .Distinct(StringComparer.Ordinal)
                .Count() != contract.BaselineRepeatReportSha256.Count)
        {
            throw new InvalidDataException(
                "Candidate temporal gates require at least two locked baseline repeat reports.");
        }
        foreach (string hash in contract.BaselineRepeatReportSha256)
            RequireSha256(hash, "baseline repeat report hash");
    }

    internal static void ValidateProducer(
        MaterialGiProducerIdentity producer,
        string role)
    {
        ArgumentNullException.ThrowIfNull(producer);
        RequireExact(producer.Schema, MaterialGiProducerIdentity.CurrentSchema, $"{role} schema");
        if (producer.BuildCommit.Length != 40 ||
            producer.BuildCommit.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException($"{role} build commit is not canonical.");
        }
        RequireSha256(producer.ShaderFingerprint, $"{role} shader fingerprint");
        RequireSha256(producer.SettingsFingerprint, $"{role} settings fingerprint");
        RequireText(producer.GpuName, $"{role} GPU");
        RequireText(producer.DriverVersion, $"{role} driver");
        RequireText(producer.QualityTier, $"{role} quality tier");
        if (producer.SourceSettingsFingerprints == null ||
            producer.SourceSettingsFingerprints.Length != 1)
        {
            throw new InvalidDataException(
                $"{role} must contain exactly one settings source identity.");
        }
        RequireSha256(
            producer.SourceSettingsFingerprints[0],
            $"{role} source settings fingerprint");
        RequireExact(
            producer.SourceSettingsFingerprints[0],
            producer.SettingsFingerprint,
            $"{role} source settings fingerprint");
    }

    internal static void ValidateCaptureRun(
        PerformanceCaptureRunMetadata run,
        string role)
    {
        ArgumentNullException.ThrowIfNull(run);
        RequireText(run.SceneKind, $"{role} scene");
        RequireText(run.Scenario, $"{role} scenario");
        RequireText(run.BuildConfiguration, $"{role} build configuration");
        RequireText(run.ApplicationVersion, $"{role} application version");
        if (run.Commit.Length != 40 ||
            run.Commit.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException($"{role} commit is not canonical.");
        }
        RequireSha256Identity(run.ShaderBundleHash, $"{role} shader bundle");
        RequireSha256Identity(run.ExecutableHash, $"{role} executable bundle");
        if (!string.Equals(run.DirtyWorktreeState, "clean", StringComparison.Ordinal))
            throw new InvalidDataException($"{role} worktree is not clean.");
        if (run.SettingsSchemaVersion <= 0)
            throw new InvalidDataException($"{role} settings schema is invalid.");
    }

    internal static void ValidateCamera(
        PerformanceCaptureCameraMetadata camera,
        string role)
    {
        ArgumentNullException.ThrowIfNull(camera);
        float[] scalars =
        [
            camera.PositionX,
            camera.PositionY,
            camera.PositionZ,
            camera.YawRadians,
            camera.PitchRadians,
            camera.FieldOfViewRadians,
            camera.NearPlane,
            camera.FarPlane
        ];
        if (scalars.Any(static value => !float.IsFinite(value)) ||
            camera.FieldOfViewRadians <= 0.0f ||
            camera.NearPlane <= 0.0f ||
            camera.FarPlane <= camera.NearPlane)
        {
            throw new InvalidDataException($"{role} contains invalid projection or pose values.");
        }
        RequireSha256Identity(camera.ViewHash, $"{role} view hash");
        RequireSha256Identity(camera.ProjectionHash, $"{role} projection hash");
    }

    internal static void RequireCaptureRunEqual(
        PerformanceCaptureRunMetadata actual,
        PerformanceCaptureRunMetadata expected,
        string role)
    {
        if (actual != expected)
            throw new InvalidDataException($"{role} changed.");
    }

    internal static void RequireProducerEqual(
        MaterialGiProducerIdentity actual,
        MaterialGiProducerIdentity expected,
        string role)
    {
        if (!string.Equals(actual.Schema, expected.Schema, StringComparison.Ordinal) ||
            !string.Equals(actual.BuildCommit, expected.BuildCommit, StringComparison.Ordinal) ||
            !string.Equals(actual.ShaderFingerprint, expected.ShaderFingerprint, StringComparison.Ordinal) ||
            !string.Equals(actual.SettingsFingerprint, expected.SettingsFingerprint, StringComparison.Ordinal) ||
            !string.Equals(actual.GpuName, expected.GpuName, StringComparison.Ordinal) ||
            !string.Equals(actual.DriverVersion, expected.DriverVersion, StringComparison.Ordinal) ||
            !string.Equals(actual.QualityTier, expected.QualityTier, StringComparison.Ordinal) ||
            actual.SourceSettingsFingerprints == null ||
            expected.SourceSettingsFingerprints == null ||
            !actual.SourceSettingsFingerprints.SequenceEqual(
                expected.SourceSettingsFingerprints,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{role} changed.");
        }
    }

    internal static void RequireCanonicalToken(string value, string role)
    {
        RequireText(value, role);
        if (value.Length > 256 || value.Any(static character => char.IsControl(character)))
            throw new InvalidDataException($"{role} is not canonical.");
    }

    internal static void RequireSha256Identity(string value, string role)
    {
        if (!value.StartsWith("sha256:", StringComparison.Ordinal))
            throw new InvalidDataException($"{role} is not a sha256 identity.");
        RequireSha256(value[7..], role);
    }

    internal static void RequireSha256(string value, string role)
    {
        if (value.Length != 64 ||
            value.Any(static character => !char.IsAsciiHexDigit(character)) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{role} is not a canonical SHA-256 hash.");
        }
    }

    private static string RequirePfmPath(string value, string role)
    {
        RequireText(value, role);
        string path = Path.GetFullPath(value);
        if (!string.Equals(
                value,
                path,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{role} is not a canonical absolute path.");
        }
        if (!string.Equals(Path.GetExtension(path), ".pfm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{role} must use a PFM file.");
        return path;
    }

    private static void RequireExactPath(string actual, string expected, string role)
    {
        string normalized = Path.GetFullPath(actual);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(actual, normalized, comparison) ||
            !string.Equals(normalized, expected, comparison))
            throw new InvalidDataException($"{role} differs from the requested path.");
    }

    private static void RequireText(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('\0'))
        {
            throw new InvalidDataException($"{role} is absent or non-canonical.");
        }
    }

    private static void RequireExact(string actual, string expected, string role)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException($"Quality-sequence reference {role} differs.");
    }
}

public static class SampleBenchmarkQualityTemporalComparer
{
    private const double MinimumReferenceRms = 1.0e-10;

    public static double Compare(
        string referenceFromPath,
        string referenceToPath,
        string candidateFromPath,
        string candidateToPath)
    {
        LinearFloatImage referenceFrom = Read(referenceFromPath, "reference from");
        LinearFloatImage referenceTo = Read(referenceToPath, "reference to");
        LinearFloatImage candidateFrom = Read(candidateFromPath, "candidate from");
        LinearFloatImage candidateTo = Read(candidateToPath, "candidate to");
        return Compare(referenceFrom, referenceTo, candidateFrom, candidateTo);
    }

    internal static double Compare(
        SampleEvidenceFileContent referenceFrom,
        SampleEvidenceFileContent referenceTo,
        SampleEvidenceFileContent candidateFrom,
        SampleEvidenceFileContent candidateTo) => Compare(
            PfmLinearImageCodec.Decode(referenceFrom.Bytes),
            PfmLinearImageCodec.Decode(referenceTo.Bytes),
            PfmLinearImageCodec.Decode(candidateFrom.Bytes),
            PfmLinearImageCodec.Decode(candidateTo.Bytes));

    private static double Compare(
        LinearFloatImage referenceFrom,
        LinearFloatImage referenceTo,
        LinearFloatImage candidateFrom,
        LinearFloatImage candidateTo)
    {
        RequireSameExtent(referenceFrom, referenceTo, candidateFrom, candidateTo);

        double squaredResidual = 0.0;
        double squaredReferenceFrameEnergy = 0.0;
        for (int index = 0; index < referenceFrom.Pixels.Length; index++)
        {
            double referenceA = RequireFinite(referenceFrom.Pixels[index], index);
            double referenceB = RequireFinite(referenceTo.Pixels[index], index);
            double candidateA = RequireFinite(candidateFrom.Pixels[index], index);
            double candidateB = RequireFinite(candidateTo.Pixels[index], index);
            double residual = (candidateB - candidateA) - (referenceB - referenceA);
            squaredResidual += residual * residual;
            squaredReferenceFrameEnergy +=
                (referenceA * referenceA + referenceB * referenceB) * 0.5;
        }
        int scalarCount = referenceFrom.Pixels.Length;
        double residualRms = Math.Sqrt(squaredResidual / scalarCount);
        double referenceRms = Math.Sqrt(
            squaredReferenceFrameEnergy / scalarCount);
        double relative = residualRms /
            Math.Max(referenceRms, MinimumReferenceRms);
        if (!double.IsFinite(relative))
            throw new InvalidDataException("Temporal residual is non-finite.");
        return relative;
    }

    private static LinearFloatImage Read(string path, string role)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            Path.GetFullPath(path),
            SampleEvidenceFileIo.MaximumLinearFloatImageBytes,
            $"Quality-sequence temporal {role}");
        return PfmLinearImageCodec.Decode(evidence.Bytes);
    }

    private static void RequireSameExtent(params LinearFloatImage[] images)
    {
        if (images.Length != 4 ||
            images.Any(static image => image == null) ||
            images.Any(image =>
                image.Width != images[0].Width ||
                image.Height != images[0].Height ||
                image.Pixels.Length != images[0].Pixels.Length))
        {
            throw new InvalidDataException(
                "Temporal residual images do not share one exact extent.");
        }
    }

    private static double RequireFinite(float value, int index)
    {
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException(
                $"Temporal residual contains a non-finite scalar at index {index}.");
        }
        return value;
    }
}
