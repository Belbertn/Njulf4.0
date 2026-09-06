using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal static class SampleBenchmarkControlledIsolationSequence
{
    private const string SequenceSchema =
        "njulf-benchmark-controlled-isolation-sequence/v1";

    public static IReadOnlyList<SampleBenchmarkControlledIsolationFrameEvidence>
        CreateFrames(
            IReadOnlyList<RendererDiagnostics> samples,
            string controlledSettingsFingerprint)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var frames = new SampleBenchmarkControlledIsolationFrameEvidence[
            samples.Count];
        for (int index = 0; index < samples.Count; index++)
        {
            RendererDiagnostics sample = samples[index] ??
                throw new InvalidDataException(
                    $"Directional isolation sample {index} is null.");
            DirectionalShadowRuntimeDiagnostics runtime =
                sample.DirectionalShadowRuntime ??
                    throw new InvalidDataException(
                        $"Directional isolation sample {index} has no " +
                        "directional-shadow runtime evidence.");
            SampleBenchmarkControlledIsolationCascadeEvidence[] cascades =
                (runtime.CacheLayerProvenance ??
                    throw new InvalidDataException(
                        $"Directional isolation sample {index} has a null " +
                        "cache-provenance collection."))
                .Where(static layer => layer.Active != 0)
                .OrderBy(static layer => layer.CascadeIndex)
                .Select(static layer =>
                    new SampleBenchmarkControlledIsolationCascadeEvidence(
                        layer.CascadeIndex,
                        layer.CacheSignature,
                        layer.DynamicWorkAppended,
                        layer.FoliageWorkAppended))
                .ToArray();
            frames[index] =
                new SampleBenchmarkControlledIsolationFrameEvidence(
                    index,
                    sample.CaptureCamera,
                    sample.CaptureSceneAssetHash,
                    sample.CaptureSceneStateHash,
                    sample.CaptureSceneContentRevision,
                    sample.ResolvedGiSettings.StableHash,
                    sample.ActiveFeatureIsolation,
                    sample.GlobalIlluminationDebugView,
                    controlledSettingsFingerprint,
                    runtime.StaticCacheActiveMask,
                    sample.PlayingAnimatorCount,
                    sample.SkinningDispatchCount,
                    sample.SkinnedObjectCount,
                    sample.DirectionalDynamicShadowMeshletCount,
                    sample.DirectionalShadowSkinnedObjectCount,
                    Array.AsReadOnly(cascades));
        }

        return Array.AsReadOnly(frames);
    }

    public static string ValidateAndCreateHash(
        IReadOnlyList<SampleBenchmarkControlledIsolationFrameEvidence> frames,
        int expectedFrameCount,
        string trajectory,
        string trajectoryFingerprint,
        string trajectoryRouteHash,
        string activation,
        string controlledSettingsFingerprint)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (expectedFrameCount !=
                SampleBenchmarkActivation.DirectionalTimingFrameCount ||
            frames.Count != expectedFrameCount)
        {
            throw new InvalidDataException(
                "Directional isolation requires exactly " +
                $"{SampleBenchmarkActivation.DirectionalTimingFrameCount} " +
                $"measured route rows; found {frames.Count}.");
        }
        if (!string.Equals(
                trajectory,
                SampleBenchmarkTrajectory.SponzaLowName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Directional isolation sequence must use the authored " +
                "Sponza-low trajectory.");
        }
        RequireSha256Identity(
            trajectoryFingerprint,
            "trajectory fingerprint");
        RequireSha256Identity(trajectoryRouteHash, "trajectory route hash");
        RequireSha256Identity(
            controlledSettingsFingerprint,
            "controlled settings fingerprint");
        string familyFingerprint =
            SampleBenchmarkActivation.CreateControlledIsolationFingerprint(
                activation);

        var canonical = new StringBuilder();
        canonical.Append(SequenceSchema).Append('|')
            .Append(trajectory).Append('|')
            .Append(trajectoryFingerprint).Append('|')
            .Append(trajectoryRouteHash).Append('|')
            .Append(familyFingerprint).Append('|')
            .Append(controlledSettingsFingerprint).Append('|')
            .Append(expectedFrameCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        for (int index = 0; index < frames.Count; index++)
        {
            SampleBenchmarkControlledIsolationFrameEvidence frame =
                frames[index] ?? throw new InvalidDataException(
                    $"Directional isolation frame {index} is null.");
            if (frame.MeasurementFrameIndex != index)
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} has route index " +
                    $"{frame.MeasurementFrameIndex}.");
            }
            SampleBenchmarkQualitySequenceReferenceLoader.ValidateCamera(
                frame.Camera,
                $"directional isolation frame {index} camera");
            RequireSha256Identity(
                frame.SceneAssetHash,
                $"frame {index} scene asset hash");
            RequireSha256Identity(
                frame.SceneStateHash,
                $"frame {index} scene state hash");
            RequireRawSha256(
                frame.ResolvedGiSettingsHash,
                $"frame {index} resolved-settings hash");
            if (!Enum.IsDefined(frame.FeatureIsolation) ||
                !Enum.IsDefined(frame.DebugView))
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} has an invalid " +
                    "feature-isolation or debug-view value.");
            }
            if (!string.Equals(
                    frame.ControlledSettingsFingerprint,
                    controlledSettingsFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} changed its " +
                    "normalized full render-settings identity.");
            }
            int activeMask = frame.DirectionalStaticCacheActiveMask;
            if (activeMask <= 0 || (activeMask & ~0b1111) != 0)
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} has invalid active " +
                    $"cascade mask 0x{activeMask:x}.");
            }
            if (frame.PlayingAnimatorCount <= 0 ||
                frame.SkinningDispatchCount <= 0 ||
                frame.SkinnedObjectCount <= 0 ||
                frame.DirectionalDynamicShadowMeshletCount <= 0 ||
                frame.DirectionalShadowSkinnedObjectCount <= 0)
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} is missing common " +
                    "animated/dynamic-caster work.");
            }
            IReadOnlyList<SampleBenchmarkControlledIsolationCascadeEvidence>
                cascades = frame.Cascades ??
                    throw new InvalidDataException(
                        $"Directional isolation frame {index} has a null " +
                        "cascade collection.");
            if (cascades.Count != BitOperations.PopCount((uint)activeMask))
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} does not contain " +
                    "exactly one role-neutral row for each active cascade.");
            }
            int observedMask = 0;
            foreach (SampleBenchmarkControlledIsolationCascadeEvidence cascade
                     in cascades)
            {
                if (cascade == null || cascade.CascadeIndex is < 0 or >= 4 ||
                    (activeMask & (1 << cascade.CascadeIndex)) == 0 ||
                    (observedMask & (1 << cascade.CascadeIndex)) != 0 ||
                    cascade.CacheSignature == 0 ||
                    cascade.DynamicWorkAppended <= 0 ||
                    cascade.FoliageWorkAppended < 0)
                {
                    throw new InvalidDataException(
                        $"Directional isolation frame {index} contains " +
                        "invalid, duplicate, or incomplete role-neutral " +
                        "cascade provenance.");
                }
                observedMask |= 1 << cascade.CascadeIndex;
            }
            if (observedMask != activeMask)
            {
                throw new InvalidDataException(
                    $"Directional isolation frame {index} cascade coverage " +
                    "does not equal its active mask.");
            }

            PerformanceCaptureCameraMetadata camera = frame.Camera;
            canonical.Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(camera.PositionX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PositionZ.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.YawRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.PitchRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.FarPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(camera.ViewHash).Append('|')
                .Append(camera.ProjectionHash).Append('|')
                .Append(camera.CameraCutSerial.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.SceneAssetHash).Append('|')
                .Append(frame.SceneStateHash).Append('|')
                .Append(frame.SceneContentRevision.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.ResolvedGiSettingsHash).Append('|')
                .Append(((uint)frame.FeatureIsolation).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(((uint)frame.DebugView).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.ControlledSettingsFingerprint).Append('|')
                .Append(activeMask.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.PlayingAnimatorCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.SkinningDispatchCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.SkinnedObjectCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.DirectionalDynamicShadowMeshletCount.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(frame.DirectionalShadowSkinnedObjectCount.ToString(CultureInfo.InvariantCulture));
            foreach (SampleBenchmarkControlledIsolationCascadeEvidence cascade
                     in cascades.OrderBy(static item => item.CascadeIndex))
            {
                canonical.Append('|')
                    .Append(cascade.CascadeIndex.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(cascade.CacheSignature.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(cascade.DynamicWorkAppended.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(cascade.FoliageWorkAppended.ToString(CultureInfo.InvariantCulture));
            }
            canonical.Append('\n');
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void RequireSha256Identity(string? value, string role)
    {
        if (value is not { Length: 71 } ||
            !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) >= 0)
        {
            throw new InvalidDataException(
                $"Directional isolation {role} is not a canonical sha256 identity.");
        }
    }

    private static void RequireRawSha256(string? value, string role)
    {
        if (value is not { Length: 64 } ||
            value.AsSpan().IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) >= 0)
        {
            throw new InvalidDataException(
                $"Directional isolation {role} is not a canonical sha256 hash.");
        }
    }
}

public sealed record SampleBenchmarkDirectionalIsolationTiming(
    double CachedCpuFrameP95Milliseconds,
    double ForcedCpuFrameP95Milliseconds,
    double CpuFrameDeltaMilliseconds,
    double CachedGpuFrameP95Milliseconds,
    double ForcedGpuFrameP95Milliseconds,
    double GpuFrameDeltaMilliseconds,
    double CachedDirectionalShadowP95Milliseconds,
    double ForcedDirectionalShadowP95Milliseconds,
    double DirectionalShadowDeltaMilliseconds);

public sealed record SampleBenchmarkControlledIsolationComparison(
    string Kind,
    string Schema,
    bool Passed,
    string ControlledIsolationPairId,
    string CachedPairId,
    string ForcedPairId,
    string ControlledIsolationIdentityHash,
    string ControlledIsolationSettingsFingerprint,
    string ControlledIsolationSequenceHash,
    string CachedSettingsFingerprint,
    string ForcedSettingsFingerprint,
    string Trajectory,
    string TrajectoryFingerprint,
    string TrajectoryRouteHash,
    string SponzaSceneAnimationConfigurationFingerprint,
    string SponzaSceneAnimationSequenceHash,
    string ActivationStructuralSequenceHash,
    string CachedActivationFingerprint,
    string CachedActivationExecutionSequenceHash,
    string ForcedActivationFingerprint,
    string ForcedActivationExecutionSequenceHash,
    string BuildCommit,
    string ExecutableHash,
    string ShaderBundleHash,
    SampleBenchmarkDirectionalIsolationTiming Timing,
    IReadOnlyList<string> Failures)
{
    public const string CurrentKind =
        "njulf-benchmark-controlled-isolation";
    public const string CurrentSchema =
        "njulf-benchmark-controlled-isolation/v2";
}

public sealed record SampleBenchmarkControlledIsolationVerificationResult(
    string Kind,
    string Schema,
    bool Passed,
    string CachedReportPath,
    string CachedReportSha256,
    string ForcedReportPath,
    string ForcedReportSha256,
    string ArtifactIdentityHash,
    SampleBenchmarkControlledIsolationComparison Comparison,
    IReadOnlyList<string> Failures)
{
    public const string CurrentKind =
        "njulf-benchmark-controlled-isolation-verification";
    public const string CurrentSchema =
        "njulf-benchmark-controlled-isolation-verification/v2";
}

public static class SampleBenchmarkControlledIsolationComparer
{
    private const string DirectionalPassName = "DirectionalShadowPass";

    public static SampleBenchmarkControlledIsolationComparison Compare(
        SampleBenchmarkReport first,
        SampleBenchmarkReport second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var failures = new List<string>();
        if (!HasRequiredShape(first) || !HasRequiredShape(second))
        {
            failures.Add(
                "Controlled-isolation reports contain null required " +
                "contracts, options, timing distributions, diagnostics, or " +
                "authenticated evidence.");
            return CreateUnavailableComparison(failures);
        }
        AddAuthenticatedEvidenceFailures(first, "First", failures);
        AddAuthenticatedEvidenceFailures(second, "Second", failures);

        SampleBenchmarkReport? cached = ResolveRole(
            first,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkCaptureVariant.Baseline);
        cached ??= ResolveRole(
            second,
            SampleBenchmarkActivation.DirectionalShadowMovingCaster,
            SampleBenchmarkCaptureVariant.Baseline);
        SampleBenchmarkReport? forced = ResolveRole(
            first,
            SampleBenchmarkActivation.DirectionalShadowForcedRefresh,
            SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh);
        forced ??= ResolveRole(
            second,
            SampleBenchmarkActivation.DirectionalShadowForcedRefresh,
            SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh);
        if (cached == null || forced == null || ReferenceEquals(cached, forced))
        {
            failures.Add(
                "Controlled directional isolation requires exactly one " +
                "cached moving-caster baseline report and one forced-refresh " +
                "report.");
            cached ??= first;
            forced ??= second;
        }

        ValidateReportRole(cached, forcedRefresh: false, failures);
        ValidateReportRole(forced, forcedRefresh: true, failures);
        ValidateSharedIdentity(cached, forced, failures);

        SampleBenchmarkTimingStats? cachedDirectional = FindPass(
            cached,
            DirectionalPassName,
            failures,
            "cached");
        SampleBenchmarkTimingStats? forcedDirectional = FindPass(
            forced,
            DirectionalPassName,
            failures,
            "forced");
        ValidateTiming(
            cached.CpuFrameMilliseconds,
            cached.MeasurementFrameCount,
            "cached CPU frame",
            failures);
        ValidateTiming(
            cached.GpuFrameMilliseconds,
            cached.MeasurementFrameCount,
            "cached GPU frame",
            failures);
        ValidateTiming(
            forced.CpuFrameMilliseconds,
            forced.MeasurementFrameCount,
            "forced CPU frame",
            failures);
        ValidateTiming(
            forced.GpuFrameMilliseconds,
            forced.MeasurementFrameCount,
            "forced GPU frame",
            failures);
        if (cachedDirectional != null)
        {
            ValidateTiming(
                cachedDirectional,
                cached.MeasurementFrameCount,
                "cached directional-shadow pass",
                failures);
        }
        if (forcedDirectional != null)
        {
            ValidateTiming(
                forcedDirectional,
                forced.MeasurementFrameCount,
                "forced directional-shadow pass",
                failures);
        }

        double cachedDirectionalP95 =
            cachedDirectional?.P95Milliseconds ?? double.NaN;
        double forcedDirectionalP95 =
            forcedDirectional?.P95Milliseconds ?? double.NaN;
        var timing = new SampleBenchmarkDirectionalIsolationTiming(
            cached.CpuFrameMilliseconds.P95Milliseconds,
            forced.CpuFrameMilliseconds.P95Milliseconds,
            forced.CpuFrameMilliseconds.P95Milliseconds -
                cached.CpuFrameMilliseconds.P95Milliseconds,
            cached.GpuFrameMilliseconds.P95Milliseconds,
            forced.GpuFrameMilliseconds.P95Milliseconds,
            forced.GpuFrameMilliseconds.P95Milliseconds -
                cached.GpuFrameMilliseconds.P95Milliseconds,
            cachedDirectionalP95,
            forcedDirectionalP95,
            forcedDirectionalP95 - cachedDirectionalP95);
        string[] distinct = failures
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new SampleBenchmarkControlledIsolationComparison(
            SampleBenchmarkControlledIsolationComparison.CurrentKind,
            SampleBenchmarkControlledIsolationComparison.CurrentSchema,
            distinct.Length == 0,
            CreateControlledIsolationPairId(cached, forced),
            cached.CaptureContract.PairId,
            forced.CaptureContract.PairId,
            cached.CaptureContract.ControlledIsolationIdentityHash,
            cached.CaptureContract
                .ControlledIsolationSettingsFingerprint,
            cached.CaptureContract.ControlledIsolationSequenceHash,
            cached.ProducerIdentity!.SettingsFingerprint,
            forced.ProducerIdentity!.SettingsFingerprint,
            cached.CaptureContract.Trajectory,
            cached.CaptureContract.TrajectoryFingerprint,
            cached.CaptureContract.TrajectoryRouteHash,
            cached.SponzaSceneAnimationEvidence
                .ConfigurationFingerprint,
            cached.SponzaSceneAnimationEvidence.SequenceHash,
            cached.ActivationEvidence.ActivationStructuralSequenceHash,
            cached.ActivationEvidence.Fingerprint,
            cached.ActivationEvidence.ActivationExecutionSequenceHash,
            forced.ActivationEvidence.Fingerprint,
            forced.ActivationEvidence.ActivationExecutionSequenceHash,
            cached.LastDiagnostics.CaptureRun.Commit,
            cached.LastDiagnostics.CaptureRun.ExecutableHash,
            cached.LastDiagnostics.CaptureRun.ShaderBundleHash,
            timing,
            Array.AsReadOnly(distinct));
    }

    private static bool HasRequiredShape(SampleBenchmarkReport report) =>
        report.Options != null &&
        report.CaptureContract != null &&
        report.CpuFrameMilliseconds != null &&
        report.GpuFrameMilliseconds != null &&
        report.LastDiagnostics != null &&
        report.ProducerIdentity != null &&
        report.ActivationEvidence != null &&
        report.SponzaSceneAnimationEvidence != null &&
        report.CaptureContract.ControlledIsolationFrames != null;

    private static SampleBenchmarkControlledIsolationComparison
        CreateUnavailableComparison(IEnumerable<string> failures) => new(
        SampleBenchmarkControlledIsolationComparison.CurrentKind,
        SampleBenchmarkControlledIsolationComparison.CurrentSchema,
        Passed: false,
        ControlledIsolationPairId: "unavailable",
        CachedPairId: string.Empty,
        ForcedPairId: string.Empty,
        ControlledIsolationIdentityHash: "unavailable",
        ControlledIsolationSettingsFingerprint: "unavailable",
        ControlledIsolationSequenceHash: "unavailable",
        CachedSettingsFingerprint: "unavailable",
        ForcedSettingsFingerprint: "unavailable",
        Trajectory: "unavailable",
        TrajectoryFingerprint: "unavailable",
        TrajectoryRouteHash: "unavailable",
        SponzaSceneAnimationConfigurationFingerprint: "unavailable",
        SponzaSceneAnimationSequenceHash: "unavailable",
        ActivationStructuralSequenceHash: "unavailable",
        CachedActivationFingerprint: "unavailable",
        CachedActivationExecutionSequenceHash: "unavailable",
        ForcedActivationFingerprint: "unavailable",
        ForcedActivationExecutionSequenceHash: "unavailable",
        BuildCommit: "unavailable",
        ExecutableHash: "unavailable",
        ShaderBundleHash: "unavailable",
        Timing: new SampleBenchmarkDirectionalIsolationTiming(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0),
        Failures: Array.AsReadOnly(
            failures.Distinct(StringComparer.Ordinal).ToArray()));

    private static void AddAuthenticatedEvidenceFailures(
        SampleBenchmarkReport report,
        string label,
        ICollection<string> failures)
    {
        try
        {
            foreach (string failure in
                     SampleBenchmarkPairComparer.ValidateAuthenticatedEvidence(
                         report))
            {
                failures.Add($"{label} report: {failure}");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NullReferenceException or OverflowException)
        {
            failures.Add(
                $"{label} report authenticated-evidence validation failed: " +
                exception.Message);
        }
    }

    private static SampleBenchmarkReport? ResolveRole(
        SampleBenchmarkReport report,
        string activation,
        string variant) =>
        string.Equals(
            report.CaptureContract.Activation,
            activation,
            StringComparison.Ordinal) &&
        string.Equals(
            report.CaptureContract.Variant,
            variant,
            StringComparison.Ordinal)
            ? report
            : null;

    private static void ValidateReportRole(
        SampleBenchmarkReport report,
        bool forcedRefresh,
        ICollection<string> failures)
    {
        string expectedActivation = forcedRefresh
            ? SampleBenchmarkActivation.DirectionalShadowForcedRefresh
            : SampleBenchmarkActivation.DirectionalShadowMovingCaster;
        string expectedVariant = forcedRefresh
            ? SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh
            : SampleBenchmarkCaptureVariant.Baseline;
        if (!string.Equals(
                report.CaptureContract.Activation,
                expectedActivation,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.CaptureContract.Variant,
                expectedVariant,
                StringComparison.Ordinal) ||
            report.Scenario !=
                SamplePerformanceScenario.GiSponzaRightWallStationary ||
            report.MeasurementFrameCount !=
                SampleBenchmarkActivation.DirectionalTimingFrameCount ||
            !report.CaptureContract.Comparable ||
            !report.CaptureContract.ProductionTiming ||
            report.CaptureContract.Mismatches is not { Count: 0 })
        {
            failures.Add(
                $"The {(forcedRefresh ? "forced" : "cached")} report does " +
                "not match its exact production directional role.");
        }
        if (!report.Options.Enabled || !report.Options.DisableVSync ||
            !report.Options.RequireProductionTiming ||
            report.Options.MeasureFrameCount !=
                SampleBenchmarkActivation.DirectionalTimingFrameCount ||
            !string.Equals(
                report.Options.CapturePairId,
                report.CaptureContract.PairId,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Options.CaptureVariant,
                expectedVariant,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Options.Activation,
                expectedActivation,
                StringComparison.Ordinal) ||
            report.Options.Trajectory !=
                SampleBenchmarkTrajectoryKind.SponzaLow ||
            !string.Equals(
                report.Options.ActivationFingerprint,
                report.CaptureContract.ActivationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.Options.TrajectoryFingerprint,
                report.CaptureContract.TrajectoryFingerprint,
                StringComparison.Ordinal))
        {
            failures.Add(
                $"The {(forcedRefresh ? "forced" : "cached")} report " +
                "options do not reproduce its exact production role.");
        }
        if (report.GpuTimingSupported != 1 ||
            report.GpuTimingValidSampleCount != report.MeasurementFrameCount ||
            !string.IsNullOrEmpty(report.GpuTimingUnavailableReason) ||
            report.FirstMeasurementFrameIndex < 0 ||
            (long)report.LastMeasurementFrameIndex !=
                (long)report.FirstMeasurementFrameIndex +
                report.MeasurementFrameCount - 1L)
        {
            failures.Add(
                $"The {(forcedRefresh ? "forced" : "cached")} report " +
                "does not contain one consecutive valid GPU-timed window.");
        }
        if (!string.Equals(
                report.CaptureContract.Trajectory,
                SampleBenchmarkTrajectory.SponzaLowName,
                StringComparison.Ordinal) ||
            report.CaptureContract.TrajectoryFrameCount != 1)
        {
            failures.Add(
                "Directional controlled-isolation timing must use the exact " +
                "stationary Sponza-low camera route.");
        }
        string expectedTrajectoryFingerprint =
            SampleBenchmarkTrajectory.CreateFingerprint(
                SampleBenchmarkTrajectoryKind.SponzaLow,
                SampleBistroQualityCaptureVariant.SunScaleStep);
        string expectedRouteHash = SampleBenchmarkTrajectory.CreateRouteHash(
            SampleBenchmarkTrajectoryKind.SponzaLow,
            SampleBistroQualityCaptureVariant.SunScaleStep);
        if (!string.Equals(
                report.CaptureContract.TrajectoryFingerprint,
                expectedTrajectoryFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                report.CaptureContract.TrajectoryRouteHash,
                expectedRouteHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation timing changed its " +
                "authored Sponza-low trajectory contract.");
        }
    }

    private static void ValidateSharedIdentity(
        SampleBenchmarkReport cached,
        SampleBenchmarkReport forced,
        ICollection<string> failures)
    {
        SampleBenchmarkCaptureContract left = cached.CaptureContract;
        SampleBenchmarkCaptureContract right = forced.CaptureContract;
        if (string.IsNullOrWhiteSpace(left.PairId) ||
            string.IsNullOrWhiteSpace(right.PairId) ||
            string.Equals(left.PairId, right.PairId, StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation requires two distinct " +
                "nonempty workload ABBA pair IDs.");
        }
        string expectedLeft =
            SampleBenchmarkAnalyzer.CreateControlledIsolationIdentityHash(
                cached.LastDiagnostics,
                left.Activation);
        string expectedRight =
            SampleBenchmarkAnalyzer.CreateControlledIsolationIdentityHash(
                forced.LastDiagnostics,
                right.Activation);
        if (!IsSha256Identity(left.ControlledIsolationIdentityHash) ||
            !string.Equals(
                left.ControlledIsolationIdentityHash,
                expectedLeft,
                StringComparison.Ordinal) ||
            !string.Equals(
                right.ControlledIsolationIdentityHash,
                expectedRight,
                StringComparison.Ordinal) ||
            !string.Equals(
                left.ControlledIsolationIdentityHash,
                right.ControlledIsolationIdentityHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation build/scene/camera identity " +
                "is invalid or different.");
        }
        if (!IsSha256Identity(left.TrajectoryFingerprint) ||
            !IsSha256Identity(left.TrajectoryRouteHash) ||
            !string.Equals(
                left.Trajectory,
                right.Trajectory,
                StringComparison.Ordinal) ||
            left.TrajectoryFrameCount != right.TrajectoryFrameCount ||
            !string.Equals(
                left.TrajectoryFingerprint,
                right.TrajectoryFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                left.TrajectoryRouteHash,
                right.TrajectoryRouteHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation authored route identity " +
                "differs.");
        }
        ValidateControlledSequence(cached, "cached", failures);
        ValidateControlledSequence(forced, "forced", failures);
        if (!IsSha256Identity(
                left.ControlledIsolationSequenceHash) ||
            !string.Equals(
                left.ControlledIsolationSequenceHash,
                right.ControlledIsolationSequenceHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation normalized full-route " +
                "sequence differs.");
        }
        string? cachedRawSettings =
            cached.ProducerIdentity?.SettingsFingerprint;
        string? forcedRawSettings =
            forced.ProducerIdentity?.SettingsFingerprint;
        if (!IsSha256Identity(
                left.ControlledIsolationSettingsFingerprint) ||
            !string.Equals(
                left.ControlledIsolationSettingsFingerprint,
                right.ControlledIsolationSettingsFingerprint,
                StringComparison.Ordinal) ||
            !IsRawSha256(cachedRawSettings) ||
            !IsRawSha256(forcedRawSettings) ||
            string.Equals(
                cachedRawSettings,
                forcedRawSettings,
                StringComparison.Ordinal) ||
            !string.Equals(
                left.ControlledIsolationSettingsFingerprint,
                "sha256:" + cachedRawSettings,
                StringComparison.Ordinal) ||
            string.Equals(
                left.ControlledIsolationSettingsFingerprint,
                "sha256:" + forcedRawSettings,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation full render settings do " +
                "not differ only through the authored forced-refresh role " +
                "or their normalized family identity differs.");
        }
        if (!string.Equals(
                cached.ActivationEvidence.ActivationStructuralSequenceHash,
                forced.ActivationEvidence.ActivationStructuralSequenceHash,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation shared animation/background " +
                "sequence differs.");
        }
        if (!string.Equals(
                cached.SponzaSceneAnimationEvidence.Fingerprint,
                forced.SponzaSceneAnimationEvidence.Fingerprint,
                StringComparison.Ordinal) ||
            cached.SponzaSceneAnimationEvidence.Mode !=
                forced.SponzaSceneAnimationEvidence.Mode ||
            !string.Equals(
                cached.SponzaSceneAnimationEvidence.ConfigurationFingerprint,
                forced.SponzaSceneAnimationEvidence.ConfigurationFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(
                cached.SponzaSceneAnimationEvidence.SequenceHash,
                forced.SponzaSceneAnimationEvidence.SequenceHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                cached.SponzaSceneAnimationEvidence.SidecarSha256,
                forced.SponzaSceneAnimationEvidence.SidecarSha256,
                StringComparison.Ordinal))
        {
            failures.Add(
                "Directional controlled-isolation common Sponza animation " +
                "identity differs.");
        }
        ValidateProducerAndBuild(cached, "cached", failures);
        ValidateProducerAndBuild(forced, "forced", failures);
        if (!CaptureRunEqual(
                cached.LastDiagnostics.CaptureRun,
                forced.LastDiagnostics.CaptureRun) ||
            !ProducerBuildEqual(cached.ProducerIdentity, forced.ProducerIdentity))
        {
            failures.Add(
                "Directional controlled-isolation producer or build identity " +
                "differs.");
        }
    }

    private static void ValidateControlledSequence(
        SampleBenchmarkReport report,
        string role,
        ICollection<string> failures)
    {
        try
        {
            SampleBenchmarkCaptureContract contract = report.CaptureContract;
            string computed = SampleBenchmarkControlledIsolationSequence
                .ValidateAndCreateHash(
                    contract.ControlledIsolationFrames,
                    report.MeasurementFrameCount,
                    contract.Trajectory,
                    contract.TrajectoryFingerprint,
                    contract.TrajectoryRouteHash,
                    contract.Activation,
                    contract.ControlledIsolationSettingsFingerprint);
            if (!string.Equals(
                    computed,
                    contract.ControlledIsolationSequenceHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "stored sequence hash does not match its 240 persisted " +
                    "route/state/cache rows.");
            }
            SampleBenchmarkControlledIsolationFrameEvidence expectedLast =
                SampleBenchmarkControlledIsolationSequence.CreateFrames(
                    [report.LastDiagnostics],
                    contract.ControlledIsolationSettingsFingerprint)[0] with
                {
                    MeasurementFrameIndex = report.MeasurementFrameCount - 1
                };
            SampleBenchmarkControlledIsolationFrameEvidence actualLast =
                contract.ControlledIsolationFrames[^1];
            if (!ControlledFrameEqual(actualLast, expectedLast))
            {
                throw new InvalidDataException(
                    "last persisted route row does not match the report's " +
                    "renderer diagnostics.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                NullReferenceException or OverflowException)
        {
            failures.Add(
                $"Directional {role} normalized full-route sequence " +
                $"validation failed: {exception.Message}");
        }
    }

    private static bool ControlledFrameEqual(
        SampleBenchmarkControlledIsolationFrameEvidence left,
        SampleBenchmarkControlledIsolationFrameEvidence right) =>
        left.MeasurementFrameIndex == right.MeasurementFrameIndex &&
        Equals(left.Camera, right.Camera) &&
        string.Equals(
            left.SceneAssetHash,
            right.SceneAssetHash,
            StringComparison.Ordinal) &&
        string.Equals(
            left.SceneStateHash,
            right.SceneStateHash,
            StringComparison.Ordinal) &&
        left.SceneContentRevision == right.SceneContentRevision &&
        string.Equals(
            left.ResolvedGiSettingsHash,
            right.ResolvedGiSettingsHash,
            StringComparison.Ordinal) &&
        left.FeatureIsolation == right.FeatureIsolation &&
        left.DebugView == right.DebugView &&
        string.Equals(
            left.ControlledSettingsFingerprint,
            right.ControlledSettingsFingerprint,
            StringComparison.Ordinal) &&
        left.DirectionalStaticCacheActiveMask ==
            right.DirectionalStaticCacheActiveMask &&
        left.PlayingAnimatorCount == right.PlayingAnimatorCount &&
        left.SkinningDispatchCount == right.SkinningDispatchCount &&
        left.SkinnedObjectCount == right.SkinnedObjectCount &&
        left.DirectionalDynamicShadowMeshletCount ==
            right.DirectionalDynamicShadowMeshletCount &&
        left.DirectionalShadowSkinnedObjectCount ==
            right.DirectionalShadowSkinnedObjectCount &&
        left.Cascades != null && right.Cascades != null &&
        left.Cascades.SequenceEqual(right.Cascades);

    private static void ValidateProducerAndBuild(
        SampleBenchmarkReport report,
        string role,
        ICollection<string> failures)
    {
        try
        {
            MaterialGiProducerIdentity producer = report.ProducerIdentity ??
                throw new InvalidDataException(
                    $"{role} producer identity is absent.");
            PerformanceCaptureRunMetadata run =
                report.LastDiagnostics.CaptureRun;
            string? shaderFailure = LoadedShaderMeasurementEvidence.Validate(
                run.LoadedShaderIdentity, report.CaptureContract.LoadedShaders);
            if (shaderFailure != null) failures.Add($"{role}: {shaderFailure}");
            SampleBenchmarkQualitySequenceReferenceLoader.ValidateProducer(
                producer,
                $"{role} producer");
            SampleBenchmarkQualitySequenceReferenceLoader.ValidateCaptureRun(
                run,
                $"{role} capture run");
            if (!string.Equals(
                    run.Commit,
                    producer.BuildCommit,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    run.ShaderBundleHash[7..],
                    producer.ShaderFingerprint,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.LastDiagnostics.CaptureGpuDeviceName,
                    producer.GpuName,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.LastDiagnostics.CaptureGpuDriverVersion,
                    producer.DriverVersion,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.LastDiagnostics.ActiveBudgetProfile.ToString(),
                    producer.QualityTier,
                    StringComparison.Ordinal) ||
                report.LastDiagnostics.CaptureRenderWidth !=
                    SampleBenchmarkQualityCheckpointCatalog.RequiredWidth ||
                report.LastDiagnostics.CaptureRenderHeight !=
                    SampleBenchmarkQualityCheckpointCatalog.RequiredHeight ||
                !string.Equals(
                    run.SceneKind,
                    "Sponza",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    run.Scenario,
                    SamplePerformanceScenario.GiSponzaRightWallStationary
                        .ToString(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{role} producer does not match its capture run/device.");
            }
            SampleBenchmarkQualitySequenceReferenceLoader.ValidateCamera(
                report.LastDiagnostics.CaptureCamera,
                $"{role} capture camera");
            RequireSha256Identity(
                report.LastDiagnostics.CaptureSceneAssetHash,
                $"{role} scene asset hash");
            RequireSha256Identity(
                report.LastDiagnostics.CaptureSceneStateHash,
                $"{role} scene state hash");
            RequireRawSha256(
                report.LastDiagnostics.ResolvedGiSettings.StableHash,
                $"{role} resolved-settings hash");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
                OverflowException)
        {
            failures.Add(
                $"Directional {role} producer validation failed: " +
                exception.Message);
        }
    }

    private static bool CaptureRunEqual(
        PerformanceCaptureRunMetadata left,
        PerformanceCaptureRunMetadata right) =>
        LoadedShaderIdentity.Compare(left.LoadedShaderIdentity, right.LoadedShaderIdentity, false) == null &&
        string.Equals(left.SceneKind, right.SceneKind, StringComparison.Ordinal) &&
        string.Equals(left.Scenario, right.Scenario, StringComparison.Ordinal) &&
        string.Equals(
            left.BuildConfiguration,
            right.BuildConfiguration,
            StringComparison.Ordinal) &&
        string.Equals(
            left.ApplicationVersion,
            right.ApplicationVersion,
            StringComparison.Ordinal) &&
        string.Equals(left.Commit, right.Commit, StringComparison.Ordinal) &&
        string.Equals(
            left.ShaderBundleHash,
            right.ShaderBundleHash,
            StringComparison.Ordinal) &&
        string.Equals(
            left.ExecutableHash,
            right.ExecutableHash,
            StringComparison.Ordinal) &&
        string.Equals(
            left.DirtyWorktreeState,
            right.DirtyWorktreeState,
            StringComparison.Ordinal) &&
        left.SettingsSchemaVersion == right.SettingsSchemaVersion;

    private static bool ProducerBuildEqual(
        MaterialGiProducerIdentity? left,
        MaterialGiProducerIdentity? right) =>
        left != null && right != null &&
        string.Equals(left.Schema, right.Schema, StringComparison.Ordinal) &&
        string.Equals(
            left.BuildCommit,
            right.BuildCommit,
            StringComparison.Ordinal) &&
        string.Equals(
            left.ShaderFingerprint,
            right.ShaderFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(left.GpuName, right.GpuName, StringComparison.Ordinal) &&
        string.Equals(
            left.DriverVersion,
            right.DriverVersion,
            StringComparison.Ordinal) &&
        string.Equals(
            left.QualityTier,
            right.QualityTier,
            StringComparison.Ordinal);

    private static SampleBenchmarkTimingStats? FindPass(
        SampleBenchmarkReport report,
        string name,
        ICollection<string> failures,
        string role)
    {
        if (report.GpuPasses == null)
        {
            failures.Add($"Directional {role} GPU pass list is null.");
            return null;
        }
        SampleBenchmarkTimingStats[] matches = report.GpuPasses
            .Where(pass => pass != null && string.Equals(
                pass.Name,
                name,
                StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            failures.Add(
                $"Directional {role} report must contain exactly one " +
                $"'{name}' timing distribution.");
            return null;
        }
        return matches[0];
    }

    private static void ValidateTiming(
        SampleBenchmarkTimingStats stats,
        int expectedCount,
        string role,
        ICollection<string> failures)
    {
        double[] values =
        [
            stats.AverageMilliseconds,
            stats.MinMilliseconds,
            stats.MedianMilliseconds,
            stats.P50Milliseconds,
            stats.P95Milliseconds,
            stats.P99Milliseconds,
            stats.MaxMilliseconds
        ];
        if (stats.Count != expectedCount ||
            values.Any(static value => !double.IsFinite(value) || value <= 0) ||
            stats.MinMilliseconds > stats.AverageMilliseconds ||
            stats.AverageMilliseconds > stats.MaxMilliseconds ||
            stats.MinMilliseconds > stats.MedianMilliseconds ||
            stats.MedianMilliseconds > stats.MaxMilliseconds ||
            stats.MedianMilliseconds != stats.P50Milliseconds ||
            stats.MinMilliseconds > stats.P50Milliseconds ||
            stats.P50Milliseconds > stats.P95Milliseconds ||
            stats.P95Milliseconds > stats.P99Milliseconds ||
            stats.P99Milliseconds > stats.MaxMilliseconds)
        {
            failures.Add(
                $"Directional {role} timing statistics are incomplete, " +
                "non-finite, non-positive, or unordered.");
        }
    }

    private static string CreateControlledIsolationPairId(
        SampleBenchmarkReport cached,
        SampleBenchmarkReport forced)
    {
        string canonical = string.Join(
            "|",
            "njulf-benchmark-controlled-isolation-pair/v2",
            "directional-shadow",
            cached.CaptureContract.PairId,
            forced.CaptureContract.PairId,
            cached.CaptureContract.ControlledIsolationIdentityHash,
            cached.CaptureContract.ControlledIsolationSettingsFingerprint,
            cached.CaptureContract.ControlledIsolationSequenceHash,
            cached.ProducerIdentity!.SettingsFingerprint,
            forced.ProducerIdentity!.SettingsFingerprint,
            cached.CaptureContract.TrajectoryFingerprint,
            cached.CaptureContract.TrajectoryRouteHash,
            cached.SponzaSceneAnimationEvidence.SequenceHash,
            cached.ActivationEvidence.ActivationStructuralSequenceHash,
            cached.LastDiagnostics.CaptureRun.Commit,
            cached.LastDiagnostics.CaptureRun.ExecutableHash,
            cached.LastDiagnostics.CaptureRun.ShaderBundleHash);
        return "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    internal static string CreateArtifactIdentityHash(
        string controlledIsolationPairId,
        string cachedReportSha256,
        string forcedReportSha256)
    {
        string canonical = string.Join(
            "|",
            "njulf-benchmark-controlled-isolation-artifact/v1",
            controlledIsolationPairId,
            cachedReportSha256,
            forcedReportSha256);
        return "sha256:" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private static bool IsSha256Identity(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value.AsSpan(7).IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) < 0;

    private static bool IsRawSha256(string? value) =>
        value is { Length: 64 } &&
        value.AsSpan().IndexOfAnyExcept(
            "0123456789abcdef".AsSpan()) < 0;

    private static void RequireSha256Identity(string? value, string role)
    {
        if (!IsSha256Identity(value))
            throw new InvalidDataException($"{role} is not canonical.");
    }

    private static void RequireRawSha256(string? value, string role)
    {
        if (value is not { Length: 64 } ||
            value.AsSpan().IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) >= 0)
        {
            throw new InvalidDataException($"{role} is not canonical.");
        }
    }
}

/// <summary>
/// Frozen-original-build early exit for the only intentionally cross-role
/// timing pair: cached directional shadows versus forced static refresh.
/// Machine-readable evidence is emitted on stdout so the campaign driver can
/// atomically authenticate and persist the exact bytes.
/// </summary>
public static class SampleBenchmarkControlledIsolationVerificationCli
{
    public const string VerifyOption =
        "--verify-directional-controlled-isolation";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = null,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

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
        int index = Array.FindIndex(
            args,
            static argument => string.Equals(
                argument,
                VerifyOption,
                StringComparison.Ordinal));
        if (index < 0)
            return false;

        try
        {
            if (Array.FindLastIndex(
                    args,
                    static argument => string.Equals(
                        argument,
                        VerifyOption,
                        StringComparison.Ordinal)) != index ||
                index != 0 || args.Length != 3 ||
                string.IsNullOrWhiteSpace(args[1]) ||
                string.IsNullOrWhiteSpace(args[2]))
            {
                throw new ArgumentException(
                    $"{VerifyOption} must appear once as '{VerifyOption} " +
                    "<cached.json> <forced.json>'.");
            }
            string firstPath = Path.GetFullPath(args[1]);
            string secondPath = Path.GetFullPath(args[2]);
            if (string.Equals(
                    firstPath,
                    secondPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Controlled-isolation reports must use distinct paths.");
            }

            SampleEvidenceFileContent firstBytes = ReadReport(firstPath);
            SampleEvidenceFileContent secondBytes = ReadReport(secondPath);
            SampleBenchmarkReport first = Deserialize(firstBytes);
            SampleBenchmarkReport second = Deserialize(secondBytes);
            SampleBenchmarkControlledIsolationComparison comparison =
                SampleBenchmarkControlledIsolationComparer.Compare(
                    first,
                    second);
            var failures = new List<string>(comparison.Failures);

            SampleEvidenceFileContent finalFirst = ReadReport(firstPath);
            SampleEvidenceFileContent finalSecond = ReadReport(secondPath);
            if (!string.Equals(
                    finalFirst.Sha256,
                    firstBytes.Sha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    finalSecond.Sha256,
                    secondBytes.Sha256,
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "A controlled-isolation report changed during verification.");
            }
            SampleBenchmarkControlledIsolationComparison finalComparison =
                SampleBenchmarkControlledIsolationComparer.Compare(
                    Deserialize(finalFirst),
                    Deserialize(finalSecond));
            foreach (string failure in finalComparison.Failures)
            {
                if (!failures.Contains(failure, StringComparer.Ordinal))
                    failures.Add(failure);
            }
            string[] distinct = failures
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            finalComparison = finalComparison with
            {
                Passed = distinct.Length == 0,
                Failures = Array.AsReadOnly(distinct)
            };

            bool firstIsCached = string.Equals(
                first.CaptureContract?.Activation,
                SampleBenchmarkActivation.DirectionalShadowMovingCaster,
                StringComparison.Ordinal);
            bool secondIsCached = string.Equals(
                second.CaptureContract?.Activation,
                SampleBenchmarkActivation.DirectionalShadowMovingCaster,
                StringComparison.Ordinal);
            SampleEvidenceFileContent cachedBytes = firstIsCached
                ? finalFirst
                : secondIsCached
                    ? finalSecond
                    : finalFirst;
            SampleEvidenceFileContent forcedBytes = firstIsCached
                ? finalSecond
                : secondIsCached
                    ? finalFirst
                    : finalSecond;
            var result =
                new SampleBenchmarkControlledIsolationVerificationResult(
                    SampleBenchmarkControlledIsolationVerificationResult
                        .CurrentKind,
                    SampleBenchmarkControlledIsolationVerificationResult
                        .CurrentSchema,
                    distinct.Length == 0,
                    cachedBytes.Path,
                    cachedBytes.Sha256,
                    forcedBytes.Path,
                    forcedBytes.Sha256,
                    SampleBenchmarkControlledIsolationComparer
                        .CreateArtifactIdentityHash(
                            finalComparison.ControlledIsolationPairId,
                            cachedBytes.Sha256,
                            forcedBytes.Sha256),
                    finalComparison,
                    Array.AsReadOnly(distinct));
            output.WriteLine(JsonSerializer.Serialize(result, WriteOptions));
            exitCode = result.Passed ? 0 : 1;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or JsonException or
                InvalidDataException or NullReferenceException or
                UnauthorizedAccessException or OverflowException)
        {
            error.WriteLine(
                "Benchmark controlled-isolation verification failed: " +
                $"{exception.GetType().Name}: {exception.Message}");
            exitCode = 1;
        }
        return true;
    }

    private static SampleEvidenceFileContent ReadReport(string path)
    {
        SampleEvidenceFileContent evidence = SampleEvidenceFileIo.Read(
            path,
            SampleEvidenceFileIo.MaximumJsonBytes,
            "Benchmark controlled-isolation report input");
        SampleEvidenceFileIo.ValidateStrictJson(
            evidence.Bytes,
            ReadOptions.MaxDepth,
            "Benchmark controlled-isolation report input");
        return evidence;
    }

    private static SampleBenchmarkReport Deserialize(
        SampleEvidenceFileContent evidence) =>
        JsonSerializer.Deserialize<SampleBenchmarkReport>(
            evidence.Bytes,
            ReadOptions) ??
        throw new InvalidDataException(
            "Benchmark controlled-isolation report deserialized to null.");
}
