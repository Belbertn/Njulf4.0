using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Scene;
using Njulf.Rendering;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace NjulfHelloGame;

/// <summary>
/// Authored benchmark workload activation. A label is never sufficient: each
/// activation has an exact scene/route/variant contract and measured-frame
/// evidence policy.
/// </summary>
public static class SampleBenchmarkActivation
{
    public const string None = "none";
    public const string SponzaForwardGi = "sponza-forward-gi";
    public const string ReflectionRecapture = "reflection-recapture";
    public const string DirectionalShadowMovingCaster =
        "directional-shadow-moving-caster";
    public const string DirectionalShadowForcedRefresh =
        "directional-shadow-forced-refresh";
    public const int SponzaActivationFrameCount = 300;
    public const int DirectionalTimingFrameCount = 240;
    public const int ReflectionRecaptureIntervalFrames = 60;
    public const int MinimumReflectionActiveFrameCount = 16;
    public const int SponzaReflectionProbeCount = 2;
    public const uint SponzaReflectionProbeResolution = 128;
    public const uint SponzaReflectionProbeMipCount = 8;

    private static readonly ReadOnlyCollection<int> ReflectionSchedule =
        Array.AsReadOnly([0, 60, 120, 180, 240]);

    public static IReadOnlyList<int> ReflectionRecaptureSchedule =>
        ReflectionSchedule;

    public static string Normalize(string? activation)
    {
        if (activation is None or SponzaForwardGi or ReflectionRecapture or
            DirectionalShadowMovingCaster or
            DirectionalShadowForcedRefresh)
        {
            return activation;
        }
        string normalized = string.IsNullOrWhiteSpace(activation)
            ? None
            : activation.Trim().ToLowerInvariant();
        return normalized switch
        {
            None or SponzaForwardGi or ReflectionRecapture or
                DirectionalShadowMovingCaster or
                DirectionalShadowForcedRefresh => normalized,
            _ => throw new ArgumentException(
                $"Unknown benchmark activation '{activation}'. Supported " +
                $"activations: {None}, {SponzaForwardGi}, " +
                $"{ReflectionRecapture}, {DirectionalShadowMovingCaster}, " +
                $"{DirectionalShadowForcedRefresh}.",
                nameof(activation))
        };
    }

    public static bool RequiresPreDrawMeasurementArm(string? activation) =>
        Normalize(activation) != None;

    public static bool RequiresDeterministicAnimation(string? activation) =>
        Normalize(activation) is DirectionalShadowMovingCaster or
            DirectionalShadowForcedRefresh;

    public static bool ShouldRequestReflectionRecapture(
        string? activation,
        int routeFrameIndex) =>
        Normalize(activation) == ReflectionRecapture &&
        ReflectionSchedule.Contains(routeFrameIndex);

    public static void Validate(
        string? activation,
        SamplePerformanceScenario scenario,
        SampleBenchmarkTrajectoryKind trajectory,
        string? captureVariant,
        int measurementFrameCount,
        bool qualitySequence = false)
    {
        string normalized = Normalize(activation);
        string variant = SampleBenchmarkCaptureVariant.Normalize(captureVariant);
        if (normalized == None)
        {
            if (variant ==
                SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh)
            {
                throw new ArgumentException(
                    "The forced directional-shadow refresh variant requires " +
                    "its exact activation contract.",
                    nameof(activation));
            }
            return;
        }

        switch (normalized)
        {
            case SponzaForwardGi:
                if (!UsesHorizontalActivationRoute(
                        trajectory,
                        measurementFrameCount) ||
                    scenario !=
                        SamplePerformanceScenario.GiSponzaRightWallStationary ||
                    variant is not (SampleBenchmarkCaptureVariant.ForwardGiEnabled or
                        SampleBenchmarkCaptureVariant.ForwardGiDisabled or
                        SampleBenchmarkCaptureVariant.ForwardGiExact))
                {
                    throw new ArgumentException(
                        "Sponza Forward+ activation requires the stationary " +
                        "Sponza GI scenario and one exact forward-GI variant.",
                        nameof(activation));
                }
                break;
            case ReflectionRecapture:
                if (!UsesHorizontalActivationRoute(
                        trajectory,
                        measurementFrameCount) ||
                    scenario != SamplePerformanceScenario
                        .GiSponzaReflectionProbeLifecycle ||
                    variant != SampleBenchmarkCaptureVariant.Baseline)
                {
                    throw new ArgumentException(
                        "Reflection recapture activation requires the Sponza " +
                        "reflection lifecycle scenario and baseline variant.",
                        nameof(activation));
                }
                break;
            case DirectionalShadowMovingCaster:
                RequireDirectionalScenarioAndVariant(
                    scenario,
                    trajectory,
                    measurementFrameCount,
                    variant,
                    SampleBenchmarkCaptureVariant.Baseline,
                    normalized,
                    activation,
                    qualitySequence);
                break;
            case DirectionalShadowForcedRefresh:
                RequireDirectionalScenarioAndVariant(
                    scenario,
                    trajectory,
                    measurementFrameCount,
                    variant,
                    SampleBenchmarkCaptureVariant.DirectionalShadowForcedRefresh,
                    normalized,
                    activation,
                    qualitySequence);
                break;
        }
    }

    public static string CreateFingerprint(string? activation)
    {
        string normalized = Normalize(activation);
        var canonical = new StringBuilder(
            "njulf-benchmark-activation/v1|");
        canonical.Append(normalized).Append('|');
        if (normalized != None)
        {
            canonical.Append(
                "common-sponza-background=per-frame-playing-animator+" +
                "skinning-dispatch+skinned-object+directional-dynamic-meshlet+" +
                "directional-skinned-object-counts|");
        }
        if (RequiresDeterministicAnimation(normalized))
        {
            canonical.Append(
                "animation=strut:first-clip:looping:manual-route-seek|")
                .Append("animation-step=1/60|animation-start=0|")
                .Append("animation-pose=global-matrix-sha256|")
                .Append("animation-revision=route-relative-contiguous|");
        }
        switch (normalized)
        {
            case SponzaForwardGi:
                canonical.Append("scene=Sponza|timing-and-quality-route=" +
                    "sponza-horizontal:300|")
                    .Append(
                    "gi=active-every-frame|controls=enabled,disabled,exact|" +
                    "pipeline-and-cache-evidence=every-frame|");
                break;
            case ReflectionRecapture:
                canonical.Append("scene=Sponza|timing-and-quality-route=" +
                        "sponza-horizontal:300|")
                    .Append("schedule=")
                    .AppendJoin(',', ReflectionSchedule)
                    .Append("|interval=")
                    .Append(ReflectionRecaptureIntervalFrames.ToString(
                        CultureInfo.InvariantCulture))
                    .Append("|idle-before=required|face0-owning-frame=required|")
                    .Append("complete-before-next=required|min-active=")
                    .Append(MinimumReflectionActiveFrameCount.ToString(
                        CultureInfo.InvariantCulture))
                    .Append("|probe-count=")
                    .Append(SponzaReflectionProbeCount.ToString(
                        CultureInfo.InvariantCulture))
                    .Append("|probe-resolution=")
                    .Append(SponzaReflectionProbeResolution.ToString(
                        CultureInfo.InvariantCulture))
                    .Append("|probe-mips=")
                    .Append(SponzaReflectionProbeMipCount.ToString(
                        CultureInfo.InvariantCulture))
                    .Append("|work=6faces+(mips-1)+copy-per-probe|")
                    .Append("completed-slot=exact-gpu-timed-serial|")
                    .Append('|');
                break;
            case DirectionalShadowMovingCaster:
                canonical.Append("scene=Sponza|timing-route=sponza-low:240|" +
                    "quality-route=sponza-horizontal:300|")
                    .Append(
                    "timing-static-cache=reuse-every-frame|" +
                    "quality-cache=truthful-camera-driven-policy|" +
                    "skinned-dynamic-caster=every-frame|");
                break;
            case DirectionalShadowForcedRefresh:
                canonical.Append("scene=Sponza|timing-route=sponza-low:240|" +
                    "quality-route=sponza-horizontal:300|")
                    .Append(
                    "timing-and-quality-static-cache=force-refresh-every-frame|" +
                    "skinned-dynamic-caster=every-frame|");
                break;
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void RequireDirectionalScenarioAndVariant(
        SamplePerformanceScenario scenario,
        SampleBenchmarkTrajectoryKind trajectory,
        int measurementFrameCount,
        string actualVariant,
        string expectedVariant,
        string normalizedActivation,
        string? originalActivation,
        bool qualitySequence)
    {
        bool exactTimingRoute =
            trajectory == SampleBenchmarkTrajectoryKind.SponzaLow &&
            measurementFrameCount == DirectionalTimingFrameCount;
        bool exactQualityRoute = UsesHorizontalActivationRoute(
            trajectory,
            measurementFrameCount);
        bool exactPurposeRoute = qualitySequence
            ? exactQualityRoute
            : exactTimingRoute;
        if (exactPurposeRoute &&
            scenario == SamplePerformanceScenario.GiSponzaRightWallStationary &&
            actualVariant == expectedVariant)
        {
            return;
        }
        throw new ArgumentException(
            $"Activation '{normalizedActivation}' requires the stationary " +
            $"Sponza GI scenario, '{expectedVariant}' capture variant, and " +
            $"the exact {(qualitySequence ? "quality" : "timing")} route.",
            nameof(originalActivation));
    }

    private static bool UsesHorizontalActivationRoute(
        SampleBenchmarkTrajectoryKind trajectory,
        int measurementFrameCount) =>
        trajectory == SampleBenchmarkTrajectoryKind.SponzaHorizontal &&
        measurementFrameCount == SponzaActivationFrameCount;
}

public sealed record SampleBenchmarkReflectionActivationRequest(
    int MeasurementFrameIndex,
    ReflectionProbeRecaptureRequestSummary Admission);

public sealed record SampleBenchmarkActivationExecutionFrameEvidence(
    int RouteFrameIndex,
    int ReflectionProbeCount,
    uint ReflectionProbeResolution,
    uint ReflectionProbeMipCount,
    ReflectionProbeLifecycleFrameSnapshot ReflectionProbeCurrentLifecycle,
    ReflectionProbeLifecycleFrameSnapshot ReflectionProbeCompletedLifecycle,
    DirectionalShadowRuntimeDiagnostics DirectionalShadowRuntime,
    int DirectionalDynamicShadowMeshletCount,
    int DirectionalShadowSkinnedObjectCount,
    int PlayingAnimatorCount,
    int SkinningDispatchCount,
    int SkinnedObjectCount,
    long GpuDirectionalShadowMicroseconds,
    int GlobalIlluminationEnabled,
    int SimpleDdgiActive,
    int ForwardGiBenchmarkSuppressed,
    int ForwardGiBenchmarkForcedExact,
    int ForwardGiReceiverCacheConsumed,
    int ForwardGiDisabledPipelineUsed,
    int ForwardGiExactGatherUsed,
    long GpuForwardGiGatherMicroseconds)
{
    public static SampleBenchmarkActivationExecutionFrameEvidence Create(
        int routeFrameIndex,
        RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new SampleBenchmarkActivationExecutionFrameEvidence(
            routeFrameIndex,
            diagnostics.ReflectionProbeCount,
            diagnostics.ReflectionProbeResolution,
            diagnostics.ReflectionProbeMipCount,
            diagnostics.ReflectionProbeCurrentLifecycle,
            diagnostics.ReflectionProbeCompletedLifecycle,
            diagnostics.DirectionalShadowRuntime,
            diagnostics.DirectionalDynamicShadowMeshletCount,
            diagnostics.DirectionalShadowSkinnedObjectCount,
            diagnostics.PlayingAnimatorCount,
            diagnostics.SkinningDispatchCount,
            diagnostics.SkinnedObjectCount,
            diagnostics.GpuDirectionalShadowMicroseconds,
            diagnostics.GlobalIlluminationEnabled,
            diagnostics.SimpleDdgiActive,
            diagnostics.ForwardGiBenchmarkSuppressed,
            diagnostics.ForwardGiBenchmarkForcedExact,
            diagnostics.ForwardGiReceiverCacheConsumed,
            diagnostics.ForwardGiDisabledPipelineUsed,
            diagnostics.ForwardGiExactGatherUsed,
            diagnostics.GpuForwardGiGatherMicroseconds);
    }
}

public sealed record SampleBenchmarkActivationAnimatorState(
    string Identity,
    string ClipName,
    float ClipDurationSeconds,
    float TimeSeconds,
    ulong PoseRevision,
    int JointCount,
    int SkinCount,
    string PoseHash)
{
    public IReadOnlyList<uint> GlobalMatrixComponentBits { get; init; } =
        Array.Empty<uint>();
}

public sealed record SampleBenchmarkActivationFrameState(
    string Schema,
    int RouteFrameIndex,
    string ConfigurationFingerprint,
    string FrameHash,
    IReadOnlyList<SampleBenchmarkActivationAnimatorState> Animators)
{
    public const string CurrentSchema = "njulf-benchmark-activation-frame/v1";

    public static string CreateConfigurationFingerprint(
        IReadOnlyList<SampleBenchmarkActivationAnimatorState> animators)
    {
        ArgumentNullException.ThrowIfNull(animators);
        var canonical = new StringBuilder(
            "njulf-benchmark-activation-animators/v1|");
        foreach (SampleBenchmarkActivationAnimatorState animator in animators)
        {
            canonical.Append(animator.Identity).Append('|')
                .Append(animator.ClipName).Append('|')
                .Append(animator.ClipDurationSeconds.ToString(
                    "R",
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(animator.JointCount.ToString(
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(animator.SkinCount.ToString(
                    CultureInfo.InvariantCulture)).Append('\n');
        }
        return HashCanonical(canonical);
    }

    public static string CreateFrameHash(
        int routeFrameIndex,
        string configurationFingerprint,
        IReadOnlyList<SampleBenchmarkActivationAnimatorState> animators)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(routeFrameIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFingerprint);
        ArgumentNullException.ThrowIfNull(animators);
        var canonical = new StringBuilder(
            "njulf-benchmark-activation-frame-state/v1|");
        canonical.Append(routeFrameIndex.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(configurationFingerprint).Append('\n');
        foreach (SampleBenchmarkActivationAnimatorState animator in animators)
        {
            canonical.Append(animator.Identity).Append('|')
                .Append(animator.TimeSeconds.ToString(
                    "R",
                    CultureInfo.InvariantCulture)).Append('|')
                .Append(animator.PoseHash).Append('\n');
        }
        return HashCanonical(canonical);
    }

    private static string HashCanonical(StringBuilder canonical) =>
        "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();

    public static void ValidateCanonical(
        SampleBenchmarkActivationFrameState state,
        int expectedRouteFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Schema != CurrentSchema ||
            state.RouteFrameIndex != expectedRouteFrameIndex ||
            state.Animators.Count == 0 ||
            !string.Equals(
                state.ConfigurationFingerprint,
                CreateConfigurationFingerprint(state.Animators),
                StringComparison.Ordinal) ||
            !string.Equals(
                state.FrameHash,
                CreateFrameHash(
                    expectedRouteFrameIndex,
                    state.ConfigurationFingerprint,
                    state.Animators),
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Activation frame state is noncanonical or mislabeled.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (SampleBenchmarkActivationAnimatorState animator in
                 state.Animators)
        {
            int expectedComponentCount;
            try
            {
                expectedComponentCount = checked(animator.JointCount * 16);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "Activation animator matrix extent overflowed.",
                    exception);
            }
            if (string.IsNullOrWhiteSpace(animator.Identity) ||
                !identities.Add(animator.Identity) ||
                string.IsNullOrWhiteSpace(animator.ClipName) ||
                !float.IsFinite(animator.ClipDurationSeconds) ||
                animator.ClipDurationSeconds <= 0f ||
                !float.IsFinite(animator.TimeSeconds) ||
                animator.JointCount <= 0 || animator.SkinCount <= 0 ||
                animator.GlobalMatrixComponentBits.Count !=
                    expectedComponentCount ||
                animator.GlobalMatrixComponentBits.Any(static bits =>
                    !float.IsFinite(BitConverter.UInt32BitsToSingle(bits))) ||
                !string.Equals(
                    animator.PoseHash,
                    SampleAnimatedCharacter.CreatePoseHash(
                        animator.GlobalMatrixComponentBits),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Activation animator evidence is incomplete, non-finite, " +
                    "or does not match its raw pose matrices.");
            }
        }
    }
}

public sealed record SampleBenchmarkActivationEvidence(
    string Schema,
    string Activation,
    string Fingerprint,
    bool Passed,
    int MeasuredSampleCount,
    IReadOnlyList<string> Failures)
{
    public const string CurrentSchema = "njulf-benchmark-activation-evidence/v1";

    public IReadOnlyList<SampleBenchmarkReflectionActivationRequest>
        ReflectionRequests { get; init; } =
            Array.Empty<SampleBenchmarkReflectionActivationRequest>();
    public int ReflectionActiveFrameCount { get; init; }
    public int ReflectionSubmittedWorkFrameCount { get; init; }
    public int ReflectionCompletedWorkFrameCount { get; init; }
    public ulong ReflectionStartedDelta { get; init; }
    public ulong ReflectionCompletedDelta { get; init; }
    public ulong ReflectionPublishedDelta { get; init; }
    public ulong ReflectionCaptureFaceUnitDelta { get; init; }
    public ulong ReflectionPrefilterMipUnitDelta { get; init; }
    public ulong ReflectionPublishCopyUnitDelta { get; init; }
    public int DirectionalActiveFrameCount { get; init; }
    public int DirectionalStaticReuseFrameCount { get; init; }
    public int DirectionalStaticRefreshFrameCount { get; init; }
    public int DirectionalTruthfulCacheFrameCount { get; init; }
    public int DirectionalDynamicCasterFrameCount { get; init; }
    public int DirectionalSkinnedAnimatorFrameCount { get; init; }
    public int DirectionalPositiveGpuPassFrameCount { get; init; }
    public string AnimationConfigurationFingerprint { get; init; } =
        "unavailable";
    public string AnimationSequenceHash { get; init; } = "unavailable";
    public string ActivationStructuralSequenceHash { get; init; } =
        "unavailable";
    public string ActivationExecutionSequenceHash { get; init; } =
        "unavailable";
    public int ForwardGiActiveFrameCount { get; init; }
    public int ForwardSuppressedFrameCount { get; init; }
    public int ForwardExactFrameCount { get; init; }
    public int ForwardReceiverCacheFrameCount { get; init; }
    public int ForwardDisabledPipelineFrameCount { get; init; }
    public int ForwardExactPipelineFrameCount { get; init; }
    public int ForwardPositiveGpuPassFrameCount { get; init; }
    public SampleBenchmarkActivationExecutionFrameEvidence?
        BaselineExecutionFrame { get; init; }
    public IReadOnlyList<SampleBenchmarkActivationExecutionFrameEvidence>
        ExecutionFrames { get; init; } =
            Array.Empty<SampleBenchmarkActivationExecutionFrameEvidence>();
    public IReadOnlyList<SampleBenchmarkActivationFrameState>
        AnimationFrames { get; init; } =
            Array.Empty<SampleBenchmarkActivationFrameState>();

    public static SampleBenchmarkActivationEvidence Unavailable { get; } = new(
        CurrentSchema,
        SampleBenchmarkActivation.None,
        SampleBenchmarkActivation.CreateFingerprint(
            SampleBenchmarkActivation.None),
        Passed: false,
        MeasuredSampleCount: 0,
        Failures: ["Benchmark activation evidence was not evaluated."]);
}

public static class SampleBenchmarkActivationEvidenceValidator
{
    public static IReadOnlyList<string> Validate(
        SampleBenchmarkActivationEvidence evidence,
        string? activation,
        string? captureVariant,
        int expectedSampleCount,
        bool qualitySequence = false,
        SampleBenchmarkTrajectoryKind? trajectory = null,
        IReadOnlyList<SampleBenchmarkActivationFrameState>?
            authoredAnimationFrames = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        string normalized = SampleBenchmarkActivation.Normalize(activation);
        string variant = SampleBenchmarkCaptureVariant.Normalize(captureVariant);
        var failures = new List<string>();
        if (!string.Equals(
                evidence.Schema,
                SampleBenchmarkActivationEvidence.CurrentSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.Activation,
                normalized,
                StringComparison.Ordinal) ||
            !string.Equals(
                evidence.Fingerprint,
                SampleBenchmarkActivation.CreateFingerprint(normalized),
                StringComparison.Ordinal) ||
            evidence.MeasuredSampleCount != expectedSampleCount ||
            !evidence.Passed || evidence.Failures is not { Count: 0 })
        {
            failures.Add(
                "Activation evidence header, result, or sample count is invalid.");
        }

        if (normalized == SampleBenchmarkActivation.None)
        {
            if (evidence.ReflectionRequests is not { Count: 0 } ||
                evidence.ReflectionActiveFrameCount != 0 ||
                evidence.ReflectionSubmittedWorkFrameCount != 0 ||
                evidence.ReflectionCompletedWorkFrameCount != 0 ||
                evidence.ReflectionStartedDelta != 0 ||
                evidence.ReflectionCompletedDelta != 0 ||
                evidence.ReflectionPublishedDelta != 0 ||
                evidence.ReflectionCaptureFaceUnitDelta != 0 ||
                evidence.ReflectionPrefilterMipUnitDelta != 0 ||
                evidence.ReflectionPublishCopyUnitDelta != 0 ||
                evidence.DirectionalActiveFrameCount != 0 ||
                evidence.DirectionalStaticReuseFrameCount != 0 ||
                evidence.DirectionalStaticRefreshFrameCount != 0 ||
                evidence.DirectionalTruthfulCacheFrameCount != 0 ||
                evidence.DirectionalDynamicCasterFrameCount != 0 ||
                evidence.DirectionalSkinnedAnimatorFrameCount != 0 ||
                evidence.DirectionalPositiveGpuPassFrameCount != 0 ||
                evidence.ForwardGiActiveFrameCount != 0 ||
                evidence.ForwardSuppressedFrameCount != 0 ||
                evidence.ForwardExactFrameCount != 0 ||
                evidence.ForwardReceiverCacheFrameCount != 0 ||
                evidence.ForwardDisabledPipelineFrameCount != 0 ||
                evidence.ForwardExactPipelineFrameCount != 0 ||
                evidence.ForwardPositiveGpuPassFrameCount != 0 ||
                evidence.BaselineExecutionFrame != null ||
                evidence.ExecutionFrames is not { Count: 0 } ||
                evidence.AnimationFrames is not { Count: 0 } ||
                !string.Equals(
                    evidence.AnimationConfigurationFingerprint,
                    "unavailable",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.AnimationSequenceHash,
                    "unavailable",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.ActivationStructuralSequenceHash,
                    "unavailable",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    evidence.ActivationExecutionSequenceHash,
                    "unavailable",
                    StringComparison.Ordinal))
            {
                failures.Add(
                    "The no-activation report contains activation work evidence.");
            }
            return Array.AsReadOnly(failures.ToArray());
        }

        if (evidence.ReflectionRequests == null ||
            evidence.ExecutionFrames == null ||
            evidence.AnimationFrames == null)
        {
            failures.Add("Activation raw evidence collections are null.");
            return Array.AsReadOnly(failures.ToArray());
        }

        bool requiresAnimation =
            SampleBenchmarkActivation.RequiresDeterministicAnimation(
                normalized);
        if ((requiresAnimation &&
             (!IsSha256Identity(evidence.AnimationConfigurationFingerprint) ||
              !IsSha256Identity(evidence.AnimationSequenceHash))) ||
            (!requiresAnimation &&
             (!string.Equals(
                  evidence.AnimationConfigurationFingerprint,
                  "unavailable",
                  StringComparison.Ordinal) ||
              !string.Equals(
                  evidence.AnimationSequenceHash,
                  "unavailable",
                  StringComparison.Ordinal) ||
              evidence.AnimationFrames.Count != 0)) ||
            !IsSha256Identity(evidence.ActivationStructuralSequenceHash) ||
            !IsSha256Identity(evidence.ActivationExecutionSequenceHash))
        {
            failures.Add(
                "Activation animation configuration or sequence identity is invalid.");
        }

        switch (normalized)
        {
            case SampleBenchmarkActivation.ReflectionRecapture:
                ValidateReflection(evidence, failures);
                break;
            case SampleBenchmarkActivation.DirectionalShadowMovingCaster:
            case SampleBenchmarkActivation.DirectionalShadowForcedRefresh:
                bool forced = normalized ==
                    SampleBenchmarkActivation.DirectionalShadowForcedRefresh;
                if (evidence.DirectionalActiveFrameCount != expectedSampleCount ||
                    evidence.DirectionalDynamicCasterFrameCount !=
                        expectedSampleCount ||
                    evidence.DirectionalSkinnedAnimatorFrameCount !=
                        expectedSampleCount ||
                    evidence.DirectionalPositiveGpuPassFrameCount !=
                        expectedSampleCount ||
                    evidence.DirectionalTruthfulCacheFrameCount !=
                        expectedSampleCount ||
                    (forced
                        ? evidence.DirectionalStaticRefreshFrameCount !=
                              expectedSampleCount ||
                          evidence.DirectionalStaticReuseFrameCount != 0
                        : (!qualitySequence &&
                           evidence.DirectionalStaticReuseFrameCount !=
                               expectedSampleCount)))
                {
                    failures.Add(
                        "Directional activation aggregates do not cover every " +
                        "timing sample with the exact cache policy.");
                }
                break;
            case SampleBenchmarkActivation.SponzaForwardGi:
                ValidateForward(evidence, variant, expectedSampleCount, failures);
                break;
        }
        if (trajectory.HasValue)
        {
            ValidateRawEvidence(
                evidence,
                variant,
                expectedSampleCount,
                trajectory.Value,
                qualitySequence,
                authoredAnimationFrames,
                failures);
        }
        return Array.AsReadOnly(failures.ToArray());
    }

    private static void ValidateRawEvidence(
        SampleBenchmarkActivationEvidence evidence,
        string captureVariant,
        int expectedSampleCount,
        SampleBenchmarkTrajectoryKind trajectory,
        bool qualitySequence,
        IReadOnlyList<SampleBenchmarkActivationFrameState>?
            authoredAnimationFrames,
        ICollection<string> failures)
    {
        IReadOnlyList<SampleBenchmarkActivationFrameState> animationFrames =
            authoredAnimationFrames ?? evidence.AnimationFrames;
        if (evidence.BaselineExecutionFrame == null ||
            evidence.ExecutionFrames.Count != expectedSampleCount ||
            (SampleBenchmarkActivation.RequiresDeterministicAnimation(
                 evidence.Activation)
                ? animationFrames.Count != expectedSampleCount
                : animationFrames.Count != 0))
        {
            failures.Add(
                "Activation raw execution or animation evidence is incomplete.");
            return;
        }
        var requests = new SortedDictionary<
            int,
            ReflectionProbeRecaptureRequestSummary>();
        foreach (SampleBenchmarkReflectionActivationRequest request in
                 evidence.ReflectionRequests)
        {
            if (!requests.TryAdd(
                    request.MeasurementFrameIndex,
                    request.Admission))
            {
                failures.Add(
                    "Activation raw reflection requests are duplicated.");
                return;
            }
        }
        for (int index = 0; index < evidence.ExecutionFrames.Count; index++)
        {
            if (evidence.ExecutionFrames[index].RouteFrameIndex != index)
            {
                failures.Add(
                    "Activation raw execution frames are reordered or duplicated.");
                return;
            }
        }

        SampleBenchmarkActivationEvidence recomputed =
            SampleBenchmarkActivationEvidenceEvaluator.Evaluate(
                evidence.Activation,
                captureVariant,
                expectedSampleCount,
                evidence.BaselineExecutionFrame,
                evidence.ExecutionFrames,
                requests,
                animationFrames,
                trajectory,
                qualitySequence);
        if (!recomputed.Passed || recomputed.Failures.Count != 0 ||
            !AggregateEvidenceMatches(evidence, recomputed))
        {
            failures.Add(
                "Activation aggregates or sequence identities do not match " +
                "the persisted raw frame evidence.");
        }
    }

    private static bool AggregateEvidenceMatches(
        SampleBenchmarkActivationEvidence actual,
        SampleBenchmarkActivationEvidence recomputed) =>
        string.Equals(
            actual.AnimationConfigurationFingerprint,
            recomputed.AnimationConfigurationFingerprint,
            StringComparison.Ordinal) &&
        string.Equals(
            actual.AnimationSequenceHash,
            recomputed.AnimationSequenceHash,
            StringComparison.Ordinal) &&
        string.Equals(
            actual.ActivationStructuralSequenceHash,
            recomputed.ActivationStructuralSequenceHash,
            StringComparison.Ordinal) &&
        string.Equals(
            actual.ActivationExecutionSequenceHash,
            recomputed.ActivationExecutionSequenceHash,
            StringComparison.Ordinal) &&
        actual.ReflectionActiveFrameCount ==
            recomputed.ReflectionActiveFrameCount &&
        actual.ReflectionSubmittedWorkFrameCount ==
            recomputed.ReflectionSubmittedWorkFrameCount &&
        actual.ReflectionCompletedWorkFrameCount ==
            recomputed.ReflectionCompletedWorkFrameCount &&
        actual.ReflectionStartedDelta == recomputed.ReflectionStartedDelta &&
        actual.ReflectionCompletedDelta == recomputed.ReflectionCompletedDelta &&
        actual.ReflectionPublishedDelta == recomputed.ReflectionPublishedDelta &&
        actual.ReflectionCaptureFaceUnitDelta ==
            recomputed.ReflectionCaptureFaceUnitDelta &&
        actual.ReflectionPrefilterMipUnitDelta ==
            recomputed.ReflectionPrefilterMipUnitDelta &&
        actual.ReflectionPublishCopyUnitDelta ==
            recomputed.ReflectionPublishCopyUnitDelta &&
        actual.DirectionalActiveFrameCount ==
            recomputed.DirectionalActiveFrameCount &&
        actual.DirectionalStaticReuseFrameCount ==
            recomputed.DirectionalStaticReuseFrameCount &&
        actual.DirectionalStaticRefreshFrameCount ==
            recomputed.DirectionalStaticRefreshFrameCount &&
        actual.DirectionalTruthfulCacheFrameCount ==
            recomputed.DirectionalTruthfulCacheFrameCount &&
        actual.DirectionalDynamicCasterFrameCount ==
            recomputed.DirectionalDynamicCasterFrameCount &&
        actual.DirectionalSkinnedAnimatorFrameCount ==
            recomputed.DirectionalSkinnedAnimatorFrameCount &&
        actual.DirectionalPositiveGpuPassFrameCount ==
            recomputed.DirectionalPositiveGpuPassFrameCount &&
        actual.ForwardGiActiveFrameCount == recomputed.ForwardGiActiveFrameCount &&
        actual.ForwardSuppressedFrameCount ==
            recomputed.ForwardSuppressedFrameCount &&
        actual.ForwardExactFrameCount == recomputed.ForwardExactFrameCount &&
        actual.ForwardReceiverCacheFrameCount ==
            recomputed.ForwardReceiverCacheFrameCount &&
        actual.ForwardDisabledPipelineFrameCount ==
            recomputed.ForwardDisabledPipelineFrameCount &&
        actual.ForwardExactPipelineFrameCount ==
            recomputed.ForwardExactPipelineFrameCount &&
        actual.ForwardPositiveGpuPassFrameCount ==
            recomputed.ForwardPositiveGpuPassFrameCount;

    private static void ValidateReflection(
        SampleBenchmarkActivationEvidence evidence,
        ICollection<string> failures)
    {
        int expectedRequests =
            SampleBenchmarkActivation.ReflectionRecaptureSchedule.Count;
        int admitted = checked(
            expectedRequests *
            SampleBenchmarkActivation.SponzaReflectionProbeCount);
        bool requestTopology =
            evidence.ReflectionRequests.Count == expectedRequests &&
            evidence.ReflectionRequests.Select(static request =>
                    request.MeasurementFrameIndex)
                .SequenceEqual(
                    SampleBenchmarkActivation.ReflectionRecaptureSchedule) &&
            evidence.ReflectionRequests.All(static request =>
                request.Admission.RequestedProbeCount ==
                    SampleBenchmarkActivation.SponzaReflectionProbeCount &&
                request.Admission.AdmittedProbeCount ==
                    SampleBenchmarkActivation.SponzaReflectionProbeCount &&
                request.Admission.DeferredProbeCount == 0 &&
                request.Admission.CoalescedProbeCount == 0 &&
                request.Admission.RejectedProbeCount == 0);
        if (!requestTopology ||
            evidence.ReflectionActiveFrameCount <
                SampleBenchmarkActivation.MinimumReflectionActiveFrameCount ||
            evidence.ReflectionSubmittedWorkFrameCount <
                SampleBenchmarkActivation.MinimumReflectionActiveFrameCount ||
            evidence.ReflectionCompletedWorkFrameCount !=
                evidence.ReflectionSubmittedWorkFrameCount ||
            evidence.ReflectionStartedDelta != (ulong)admitted ||
            evidence.ReflectionCompletedDelta != (ulong)admitted ||
            evidence.ReflectionPublishedDelta != (ulong)admitted ||
            evidence.ReflectionCaptureFaceUnitDelta !=
                checked((ulong)admitted * 6UL) ||
            evidence.ReflectionPrefilterMipUnitDelta !=
                checked((ulong)admitted *
                    (SampleBenchmarkActivation.SponzaReflectionProbeMipCount -
                     1UL)) ||
            evidence.ReflectionPublishCopyUnitDelta != (ulong)admitted)
        {
            failures.Add(
                "Reflection activation aggregates do not match the authored " +
                "schedule, topology, or completed-slot evidence.");
        }
    }

    private static void ValidateForward(
        SampleBenchmarkActivationEvidence evidence,
        string variant,
        int count,
        ICollection<string> failures)
    {
        bool expected = evidence.ForwardGiActiveFrameCount == count &&
            evidence.ForwardPositiveGpuPassFrameCount == count &&
            (variant switch
            {
                SampleBenchmarkCaptureVariant.ForwardGiEnabled =>
                    evidence.ForwardReceiverCacheFrameCount == count &&
                    evidence.ForwardSuppressedFrameCount == 0 &&
                    evidence.ForwardExactFrameCount == 0 &&
                    evidence.ForwardDisabledPipelineFrameCount == 0 &&
                    evidence.ForwardExactPipelineFrameCount == 0,
                SampleBenchmarkCaptureVariant.ForwardGiDisabled =>
                    evidence.ForwardSuppressedFrameCount == count &&
                    evidence.ForwardDisabledPipelineFrameCount == count &&
                    evidence.ForwardExactFrameCount == 0 &&
                    evidence.ForwardReceiverCacheFrameCount == 0 &&
                    evidence.ForwardExactPipelineFrameCount == 0,
                SampleBenchmarkCaptureVariant.ForwardGiExact =>
                    evidence.ForwardExactFrameCount == count &&
                    evidence.ForwardExactPipelineFrameCount == count &&
                    evidence.ForwardSuppressedFrameCount == 0 &&
                    evidence.ForwardReceiverCacheFrameCount == 0 &&
                    evidence.ForwardDisabledPipelineFrameCount == 0,
                _ => false
            });
        if (!expected)
        {
            failures.Add(
                "Forward+ activation aggregates do not prove the exact " +
                "enabled/disabled/exact pipeline on every sample.");
        }
    }

    private static bool IsSha256Identity(string? value)
    {
        const string prefix = "sha256:";
        return value != null && value.Length == prefix.Length + 64 &&
            value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.AsSpan(prefix.Length).IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) < 0;
    }
}

internal sealed class SampleBenchmarkActivationObserver
{
    private readonly string _activation;
    private readonly string _captureVariant;
    private readonly int _expectedSampleCount;
    private readonly SampleBenchmarkTrajectoryKind _trajectory;
    private readonly bool _qualitySequence;
    private readonly ReflectionProbeRecaptureRequestSummary[]
        _reflectionRequests;
    private readonly bool[] _reflectionRequestRecorded;
    private readonly List<SampleBenchmarkActivationFrameState>
        _activationFrames;
    private readonly List<RendererDiagnostics> _samples;
    private SampleBenchmarkActivationAnimationCapture? _timingAnimationCapture;
    private RendererDiagnostics _baseline = RendererDiagnostics.Empty;
    private bool _baselineSet;
    private readonly List<string> _failures = new();

    public SampleBenchmarkActivationObserver(
        string? activation,
        SamplePerformanceScenario scenario,
        SampleBenchmarkTrajectoryKind trajectory,
        string? captureVariant,
        int expectedSampleCount,
        bool qualitySequence = false)
    {
        _activation = SampleBenchmarkActivation.Normalize(activation);
        _captureVariant = SampleBenchmarkCaptureVariant.Normalize(captureVariant);
        _expectedSampleCount = expectedSampleCount;
        _activationFrames = new List<SampleBenchmarkActivationFrameState>(
            expectedSampleCount);
        _samples = new List<RendererDiagnostics>(expectedSampleCount);
        _reflectionRequests =
            new ReflectionProbeRecaptureRequestSummary[expectedSampleCount];
        _reflectionRequestRecorded = new bool[expectedSampleCount];
        _trajectory = trajectory;
        _qualitySequence = qualitySequence;
        SampleBenchmarkActivation.Validate(
            _activation,
            scenario,
            trajectory,
            _captureVariant,
            expectedSampleCount,
            qualitySequence);
    }

    public void RecordReflectionRequest(
        int measurementFrameIndex,
        in ReflectionProbeRecaptureRequestSummary admission)
    {
        if (!SampleBenchmarkActivation.ShouldRequestReflectionRecapture(
                _activation,
                measurementFrameIndex))
        {
            throw new InvalidOperationException(
                $"Frame {measurementFrameIndex} is not in the authored " +
                "reflection recapture schedule.");
        }
        if ((uint)measurementFrameIndex >=
                (uint)_reflectionRequests.Length ||
            _reflectionRequestRecorded[measurementFrameIndex])
        {
            throw new InvalidOperationException(
                $"Reflection recapture frame {measurementFrameIndex} was " +
                "applied more than once.");
        }
        _reflectionRequests[measurementFrameIndex] = admission;
        _reflectionRequestRecorded[measurementFrameIndex] = true;
    }

    public void BeginMeasurement(RendererDiagnostics baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (_baselineSet)
            throw new InvalidOperationException(
                "Benchmark activation measurement was armed twice.");
        _baseline = baseline;
        _baselineSet = true;
    }

    public void RecordPreDrawFrame(
        int measurementFrameIndex,
        SampleBenchmarkActivationFrameState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (_activation == SampleBenchmarkActivation.None)
        {
            throw new InvalidOperationException(
                "The no-activation workload cannot record activation frames.");
        }
        if (measurementFrameIndex != _activationFrames.Count ||
            state.RouteFrameIndex != measurementFrameIndex)
        {
            throw new InvalidOperationException(
                $"Activation expected pre-Draw frame {_activationFrames.Count}, " +
                $"got measurement {measurementFrameIndex}/route " +
                $"{state.RouteFrameIndex}.");
        }
        _activationFrames.Add(state);
    }

    public void PrepareTimingAnimationFrame(
        Scene scene,
        int routeFrameIndex,
        int? measurementFrameIndex)
    {
        if (_qualitySequence)
        {
            throw new InvalidOperationException(
                "The production timing animation recorder cannot be used by " +
                "a quality-only sequence.");
        }
        if (_activation == SampleBenchmarkActivation.None)
            return;
        _timingAnimationCapture ??=
            new SampleBenchmarkActivationAnimationCapture(
                scene,
                _expectedSampleCount);
        if (measurementFrameIndex.HasValue)
        {
            if (measurementFrameIndex.Value != _samples.Count)
            {
                throw new InvalidOperationException(
                    $"Activation animation expected measurement frame " +
                    $"{_samples.Count}, got {measurementFrameIndex.Value}.");
            }
            _timingAnimationCapture.PrepareFrame(
                routeFrameIndex,
                measurementFrameIndex.Value);
        }
        else
        {
            _timingAnimationCapture.PrepareWarmupFrame(routeFrameIndex);
        }
    }

    public void Observe(int measurementFrameIndex, RendererDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!_baselineSet)
            throw new InvalidOperationException(
                "Benchmark activation requires a pre-measurement baseline.");
        if (measurementFrameIndex != _samples.Count)
        {
            throw new InvalidOperationException(
                $"Benchmark activation expected sample {_samples.Count}, got " +
                $"{measurementFrameIndex}.");
        }
        _samples.Add(diagnostics);
    }

    public void RecordFailure(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!_failures.Contains(reason, StringComparer.Ordinal))
            _failures.Add(reason);
    }

    public SampleBenchmarkActivationEvidence Build(
        IReadOnlyList<SampleBenchmarkActivationFrameState>?
            authoredAnimationFrames = null)
    {
        IReadOnlyList<SampleBenchmarkActivationFrameState> activationFrames =
            authoredAnimationFrames ??
            _timingAnimationCapture?.BuildEvidence(_samples.Count) ??
            _activationFrames;
        var reflectionRequests = new SortedDictionary<
            int,
            ReflectionProbeRecaptureRequestSummary>();
        for (int index = 0; index < _reflectionRequests.Length; index++)
        {
            if (_reflectionRequestRecorded[index])
                reflectionRequests.Add(index, _reflectionRequests[index]);
        }
        var executionFrames =
            new SampleBenchmarkActivationExecutionFrameEvidence[
                _samples.Count];
        for (int index = 0; index < _samples.Count; index++)
        {
            executionFrames[index] =
                SampleBenchmarkActivationExecutionFrameEvidence.Create(
                    index,
                    _samples[index]);
        }
        SampleBenchmarkActivationEvidence evidence =
            SampleBenchmarkActivationEvidenceEvaluator.Evaluate(
            _activation,
            _captureVariant,
            _expectedSampleCount,
            SampleBenchmarkActivationExecutionFrameEvidence.Create(
                -1,
                _baseline),
            executionFrames,
            reflectionRequests,
            activationFrames,
            _trajectory,
            _qualitySequence);
        if (_failures.Count == 0)
            return evidence;
        string[] failures = evidence.Failures
            .Concat(_failures)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return evidence with
        {
            Passed = false,
            Failures = Array.AsReadOnly(failures)
        };
    }
}

internal static class SampleBenchmarkActivationEvidenceEvaluator
{
    private readonly record struct ReflectionWorkEvidence(
        int FrameSlot,
        int FaceUnits,
        int PrefilterMipUnits,
        int PublishCopyUnits)
    {
        public int TotalUnits => checked(
            FaceUnits + PrefilterMipUnits + PublishCopyUnits);
    }

    public static SampleBenchmarkActivationEvidence Evaluate(
        string activation,
        string captureVariant,
        int expectedSampleCount,
        SampleBenchmarkActivationExecutionFrameEvidence baseline,
        IReadOnlyList<SampleBenchmarkActivationExecutionFrameEvidence> samples,
        IReadOnlyDictionary<int, ReflectionProbeRecaptureRequestSummary>
            reflectionRequests,
        IReadOnlyList<SampleBenchmarkActivationFrameState> activationFrames,
        SampleBenchmarkTrajectoryKind trajectory,
        bool qualitySequence)
    {
        var failures = new List<string>();
        if (samples.Count != expectedSampleCount)
        {
            failures.Add(
                $"Activation observed {samples.Count}/{expectedSampleCount} " +
                "measured samples.");
        }

        int reflectionActiveFrames = 0;
        int reflectionSubmittedWorkFrames = 0;
        int reflectionCompletedWorkFrames = 0;
        int directionalActiveFrames = 0;
        int directionalReuseFrames = 0;
        int directionalRefreshFrames = 0;
        int directionalTruthfulCacheFrames = 0;
        int directionalDynamicFrames = 0;
        int directionalAnimatorFrames = 0;
        int directionalGpuFrames = 0;
        int forwardActiveFrames = 0;
        int forwardSuppressedFrames = 0;
        int forwardExactFrames = 0;
        int forwardCacheFrames = 0;
        int forwardDisabledPipelineFrames = 0;
        int forwardExactPipelineFrames = 0;
        int forwardGpuFrames = 0;
        var currentReflectionWork =
            new Dictionary<ulong, ReflectionWorkEvidence>();
        var completedReflectionWork =
            new Dictionary<ulong, ReflectionWorkEvidence>();
        ulong firstReflectionFrameSerial = 0;
        int firstReflectionFrameSlot = -1;
        (string animationConfigurationFingerprint, string animationSequenceHash) =
            ValidateAnimationSequence(
                activation,
                expectedSampleCount,
                activationFrames,
                failures);

        for (int index = 0; index < samples.Count; index++)
        {
            SampleBenchmarkActivationExecutionFrameEvidence sample =
                samples[index];
            if (activation == SampleBenchmarkActivation.ReflectionRecapture)
            {
                ValidateReflectionFrame(
                    index,
                    sample,
                    reflectionRequests,
                    failures,
                    currentReflectionWork,
                    completedReflectionWork,
                    ref firstReflectionFrameSerial,
                    ref firstReflectionFrameSlot,
                    ref reflectionActiveFrames,
                    ref reflectionSubmittedWorkFrames);
            }

            if (activation is
                SampleBenchmarkActivation.DirectionalShadowMovingCaster or
                SampleBenchmarkActivation.DirectionalShadowForcedRefresh)
            {
                ValidateDirectionalFrame(
                    activation,
                    index,
                    sample,
                    qualitySequence &&
                    trajectory ==
                        SampleBenchmarkTrajectoryKind.SponzaHorizontal,
                    failures,
                    ref directionalActiveFrames,
                    ref directionalReuseFrames,
                    ref directionalRefreshFrames,
                    ref directionalTruthfulCacheFrames,
                    ref directionalDynamicFrames,
                    ref directionalAnimatorFrames,
                    ref directionalGpuFrames);
            }

            if (activation == SampleBenchmarkActivation.SponzaForwardGi)
            {
                ValidateForwardFrame(
                    captureVariant,
                    index,
                    sample,
                    failures,
                    ref forwardActiveFrames,
                    ref forwardSuppressedFrames,
                    ref forwardExactFrames,
                    ref forwardCacheFrames,
                    ref forwardDisabledPipelineFrames,
                    ref forwardExactPipelineFrames,
                    ref forwardGpuFrames);
            }
        }

        ulong startedDelta = 0;
        ulong completedDelta = 0;
        ulong publishedDelta = 0;
        ulong faceDelta = 0;
        ulong mipDelta = 0;
        ulong copyDelta = 0;
        if (samples.Count > 0)
        {
            ReflectionProbeLifecycleSnapshot first =
                baseline.ReflectionProbeCurrentLifecycle.Lifecycle;
            ReflectionProbeLifecycleSnapshot last =
                samples[^1].ReflectionProbeCurrentLifecycle.Lifecycle;
            startedDelta = SubtractMonotonic(
                last.CapturesStartedTotal,
                first.CapturesStartedTotal,
                "reflection started total",
                failures);
            completedDelta = SubtractMonotonic(
                last.CapturesCompletedTotal,
                first.CapturesCompletedTotal,
                "reflection completed total",
                failures);
            publishedDelta = SubtractMonotonic(
                last.CapturesPublishedTotal,
                first.CapturesPublishedTotal,
                "reflection published total",
                failures);
            faceDelta = SubtractMonotonic(
                last.CaptureFaceUnitsTotal,
                first.CaptureFaceUnitsTotal,
                "reflection face-unit total",
                failures);
            mipDelta = SubtractMonotonic(
                last.PrefilterMipUnitsTotal,
                first.PrefilterMipUnitsTotal,
                "reflection prefilter-unit total",
                failures);
            copyDelta = SubtractMonotonic(
                last.PublishCopyUnitsTotal,
                first.PublishCopyUnitsTotal,
                "reflection copy-unit total",
                failures);
        }

        if (activation == SampleBenchmarkActivation.ReflectionRecapture)
        {
            reflectionCompletedWorkFrames =
                ReconcileCompletedReflectionWork(
                    currentReflectionWork,
                    completedReflectionWork,
                    firstReflectionFrameSerial,
                    samples.Count,
                    faceDelta,
                    mipDelta,
                    copyDelta,
                    failures);
            ValidateReflectionAggregate(
                baseline,
                samples,
                reflectionRequests,
                reflectionActiveFrames,
                reflectionSubmittedWorkFrames,
                reflectionCompletedWorkFrames,
                startedDelta,
                completedDelta,
                publishedDelta,
                faceDelta,
                mipDelta,
                copyDelta,
                failures);
        }
        else if (reflectionRequests.Count != 0)
        {
            failures.Add(
                "A non-reflection activation recorded reflection requests.");
        }
        string activationStructuralSequenceHash = activation ==
            SampleBenchmarkActivation.None
                ? "unavailable"
                : CreateActivationStructuralSequenceHash(
                    activation,
                    samples.Count,
                    reflectionRequests,
                    activationFrames,
                    samples);
        string activationExecutionSequenceHash = activation ==
            SampleBenchmarkActivation.None
                ? "unavailable"
                : CreateActivationExecutionSequenceHash(
                    activation,
                    baseline,
                    samples,
                    firstReflectionFrameSerial);

        var requests = reflectionRequests
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new SampleBenchmarkReflectionActivationRequest(
                pair.Key,
                pair.Value))
            .ToArray();
        return new SampleBenchmarkActivationEvidence(
            SampleBenchmarkActivationEvidence.CurrentSchema,
            activation,
            SampleBenchmarkActivation.CreateFingerprint(activation),
            failures.Count == 0,
            samples.Count,
            Array.AsReadOnly(failures.Distinct(StringComparer.Ordinal).ToArray()))
        {
            ReflectionRequests = Array.AsReadOnly(requests),
            ReflectionActiveFrameCount = reflectionActiveFrames,
            ReflectionSubmittedWorkFrameCount = reflectionSubmittedWorkFrames,
            ReflectionCompletedWorkFrameCount = reflectionCompletedWorkFrames,
            ReflectionStartedDelta = startedDelta,
            ReflectionCompletedDelta = completedDelta,
            ReflectionPublishedDelta = publishedDelta,
            ReflectionCaptureFaceUnitDelta = faceDelta,
            ReflectionPrefilterMipUnitDelta = mipDelta,
            ReflectionPublishCopyUnitDelta = copyDelta,
            DirectionalActiveFrameCount = directionalActiveFrames,
            DirectionalStaticReuseFrameCount = directionalReuseFrames,
            DirectionalStaticRefreshFrameCount = directionalRefreshFrames,
            DirectionalTruthfulCacheFrameCount =
                directionalTruthfulCacheFrames,
            DirectionalDynamicCasterFrameCount = directionalDynamicFrames,
            DirectionalSkinnedAnimatorFrameCount = directionalAnimatorFrames,
            DirectionalPositiveGpuPassFrameCount = directionalGpuFrames,
            AnimationConfigurationFingerprint =
                animationConfigurationFingerprint,
            AnimationSequenceHash = animationSequenceHash,
            ActivationStructuralSequenceHash =
                activationStructuralSequenceHash,
            ActivationExecutionSequenceHash =
                activationExecutionSequenceHash,
            ForwardGiActiveFrameCount = forwardActiveFrames,
            ForwardSuppressedFrameCount = forwardSuppressedFrames,
            ForwardExactFrameCount = forwardExactFrames,
            ForwardReceiverCacheFrameCount = forwardCacheFrames,
            ForwardDisabledPipelineFrameCount = forwardDisabledPipelineFrames,
            ForwardExactPipelineFrameCount = forwardExactPipelineFrames,
            ForwardPositiveGpuPassFrameCount = forwardGpuFrames,
            BaselineExecutionFrame = activation ==
                SampleBenchmarkActivation.None ? null : baseline,
            ExecutionFrames = activation == SampleBenchmarkActivation.None
                ? Array.Empty<SampleBenchmarkActivationExecutionFrameEvidence>()
                : Array.AsReadOnly(samples.ToArray()),
            // The full raw pose route is persisted exactly once in the
            // authenticated common Sponza animation sidecar. Keeping it here
            // would exceed the bounded benchmark-report size for 960-frame
            // routes and would create two competing durable owners.
            AnimationFrames = Array.Empty<SampleBenchmarkActivationFrameState>()
        };
    }

    private static (string ConfigurationFingerprint, string SequenceHash)
        ValidateAnimationSequence(
            string activation,
            int expectedSampleCount,
            IReadOnlyList<SampleBenchmarkActivationFrameState> frames,
            ICollection<string> failures)
    {
        if (!SampleBenchmarkActivation.RequiresDeterministicAnimation(
                activation))
        {
            if (frames.Count != 0)
                failures.Add(
                    "The workload recorded animator frames without the " +
                    "directional moving-caster activation.");
            return ("unavailable", "unavailable");
        }
        if (frames.Count != expectedSampleCount)
        {
            failures.Add(
                $"Activation animator evidence contains {frames.Count}/" +
                $"{expectedSampleCount} route frames.");
        }
        if (frames.Count == 0)
            return ("unavailable", "unavailable");

        string configurationFingerprint =
            frames[0].ConfigurationFingerprint;
        var canonical = new StringBuilder(
            "njulf-benchmark-activation-animation-sequence/v1|");
        canonical.Append(activation).Append('|')
            .Append(configurationFingerprint).Append('\n');
        ulong[] firstRevisions = frames[0].Animators
            .Select(static animator => animator.PoseRevision)
            .ToArray();
        for (int frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            SampleBenchmarkActivationFrameState frame = frames[frameIndex];
            bool canonicalFrame = true;
            try
            {
                SampleBenchmarkActivationFrameState.ValidateCanonical(
                    frame,
                    frameIndex);
            }
            catch (InvalidDataException)
            {
                canonicalFrame = false;
            }
            bool valid = canonicalFrame &&
                string.Equals(
                    frame.ConfigurationFingerprint,
                    configurationFingerprint,
                    StringComparison.Ordinal) &&
                frame.Animators.Count == firstRevisions.Length;
            if (!valid)
            {
                failures.Add(
                    $"Activation animator frame {frameIndex} is noncanonical " +
                    "or changed its authored configuration.");
            }
            else
            {
                for (int animatorIndex = 0;
                     animatorIndex < frame.Animators.Count;
                     animatorIndex++)
                {
                    SampleBenchmarkActivationAnimatorState animator =
                        frame.Animators[animatorIndex];
                    ulong expectedRevision;
                    try
                    {
                        expectedRevision = checked(
                            firstRevisions[animatorIndex] +
                            (ulong)frameIndex);
                    }
                    catch (OverflowException)
                    {
                        expectedRevision = ulong.MaxValue;
                    }
                    float expectedTime = NormalizeAnimationTime(
                        frameIndex * HelloGame.BenchmarkSimulationDeltaSeconds,
                        animator.ClipDurationSeconds);
                    if (animator.PoseRevision != expectedRevision ||
                        !float.IsFinite(animator.TimeSeconds) ||
                        !float.IsFinite(animator.ClipDurationSeconds) ||
                        animator.ClipDurationSeconds <= 0f ||
                        BitConverter.SingleToInt32Bits(animator.TimeSeconds) !=
                        BitConverter.SingleToInt32Bits(expectedTime) ||
                        animator.JointCount <= 0 || animator.SkinCount <= 0 ||
                        !IsSha256Identity(animator.PoseHash))
                    {
                        failures.Add(
                            $"Activation animator frame {frameIndex}, animator " +
                            $"{animatorIndex} does not match its exact authored " +
                            "phase, pose, or relative revision.");
                    }
                }
            }
            canonical.Append(frameIndex.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(frame.FrameHash).Append('\n');
        }
        return (
            configurationFingerprint,
            "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
                .ToLowerInvariant());
    }

    private static string CreateActivationStructuralSequenceHash(
        string activation,
        int sampleCount,
        IReadOnlyDictionary<int, ReflectionProbeRecaptureRequestSummary>
            reflectionRequests,
        IReadOnlyList<SampleBenchmarkActivationFrameState> activationFrames,
        IReadOnlyList<SampleBenchmarkActivationExecutionFrameEvidence> samples)
    {
        string family = activation switch
        {
            SampleBenchmarkActivation.DirectionalShadowMovingCaster or
                SampleBenchmarkActivation.DirectionalShadowForcedRefresh =>
                    "directional-shadow",
            _ => activation
        };
        var canonical = new StringBuilder(
            "njulf-benchmark-activation-structural-sequence/v1|");
        canonical.Append(family).Append('|')
            .Append(sampleCount.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        for (int index = 0; index < sampleCount; index++)
        {
            canonical.Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('|');
            if (SampleBenchmarkActivation.RequiresDeterministicAnimation(
                    activation) && index < activationFrames.Count)
                canonical.Append(activationFrames[index].FrameHash);
            else if (!SampleBenchmarkActivation.RequiresDeterministicAnimation(
                         activation))
                canonical.Append("no-animation");
            else
                canonical.Append("missing-animation-frame");
            if (index < samples.Count)
            {
                SampleBenchmarkActivationExecutionFrameEvidence sample =
                    samples[index];
                canonical.Append("|common-background=")
                    .Append(sample.PlayingAnimatorCount).Append(':')
                    .Append(sample.SkinningDispatchCount).Append(':')
                    .Append(sample.SkinnedObjectCount).Append(':')
                    .Append(sample.DirectionalDynamicShadowMeshletCount)
                    .Append(':')
                    .Append(sample.DirectionalShadowSkinnedObjectCount);
            }
            else
            {
                canonical.Append("|common-background=missing");
            }
            if (family == SampleBenchmarkActivation.ReflectionRecapture)
            {
                bool scheduled = reflectionRequests.TryGetValue(
                    index,
                    out ReflectionProbeRecaptureRequestSummary admission);
                canonical.Append('|').Append(scheduled);
                if (scheduled)
                {
                    canonical.Append('|')
                        .Append(admission.RequestedProbeCount)
                        .Append('|').Append(
                            admission.BeforeLifecycle.QueuedCount +
                            admission.BeforeLifecycle.ActiveCount +
                            admission.BeforeLifecycle.AwaitingGpuCompletionCount +
                            admission.BeforeLifecycle.PublishedCount);
                }
            }
            canonical.Append('\n');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static string CreateActivationExecutionSequenceHash(
        string activation,
        SampleBenchmarkActivationExecutionFrameEvidence baseline,
        IReadOnlyList<SampleBenchmarkActivationExecutionFrameEvidence> samples,
        ulong firstReflectionFrameSerial)
    {
        var canonical = new StringBuilder(
            "njulf-benchmark-activation-execution-sequence/v1|");
        canonical.Append(activation).Append('|')
            .Append(samples.Count.ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        ReflectionProbeLifecycleSnapshot baselineLifecycle =
            baseline.ReflectionProbeCurrentLifecycle.Lifecycle;
        int firstReflectionSlot = samples.Count == 0
            ? 0
            : samples[0].ReflectionProbeCurrentLifecycle.FrameSlot;
        for (int index = 0; index < samples.Count; index++)
        {
            SampleBenchmarkActivationExecutionFrameEvidence sample =
                samples[index];
            canonical.Append(index.ToString(CultureInfo.InvariantCulture))
                .Append('|');
            switch (activation)
            {
                case SampleBenchmarkActivation.ReflectionRecapture:
                    AppendReflectionWorkFrame(
                        canonical,
                        sample.ReflectionProbeCurrentLifecycle,
                        sample.ReflectionProbeCompletedLifecycle,
                        baselineLifecycle,
                        firstReflectionFrameSerial,
                        firstReflectionSlot);
                    break;
                case SampleBenchmarkActivation.SponzaForwardGi:
                    canonical
                        .Append(sample.GlobalIlluminationEnabled).Append('|')
                        .Append(sample.SimpleDdgiActive).Append('|')
                        .Append(sample.ForwardGiBenchmarkSuppressed).Append('|')
                        .Append(sample.ForwardGiBenchmarkForcedExact).Append('|')
                        .Append(sample.ForwardGiReceiverCacheConsumed).Append('|')
                        .Append(sample.ForwardGiDisabledPipelineUsed).Append('|')
                        .Append(sample.ForwardGiExactGatherUsed).Append('|');
                    break;
                case SampleBenchmarkActivation.DirectionalShadowMovingCaster:
                case SampleBenchmarkActivation.DirectionalShadowForcedRefresh:
                    DirectionalShadowRuntimeDiagnostics runtime =
                        sample.DirectionalShadowRuntime;
                    canonical.Append(runtime.Enabled).Append('|')
                        .Append(runtime.StaticCacheActiveMask).Append('|')
                        .Append(runtime.StaticCacheRefreshMask).Append('|')
                        .Append(runtime.StaticCacheReuseMask).Append('|')
                        .Append(sample.DirectionalDynamicShadowMeshletCount)
                        .Append('|')
                        .Append(sample.DirectionalShadowSkinnedObjectCount)
                        .Append('|')
                        .Append(sample.PlayingAnimatorCount).Append('|')
                        .Append(sample.SkinningDispatchCount).Append('|');
                    foreach (DirectionalShadowCacheLayerProvenance layer in
                             runtime.CacheLayerProvenance)
                    {
                        canonical.Append(layer.CascadeIndex).Append(':')
                            .Append(layer.Active).Append(':')
                            .Append((int)layer.CacheState).Append(':')
                            .Append(layer.CopiedFromCache).Append(':')
                            .Append(layer.RefreshedThisFrame).Append(':')
                            .Append(layer.ExplicitlyCleared).Append(':')
                            .Append(layer.DynamicWorkAppended).Append(':')
                            .Append(layer.FinalWorkingLayerValid).Append(',');
                    }
                    break;
            }
            canonical.Append('\n');
        }
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void AppendReflectionWorkFrame(
        StringBuilder canonical,
        in ReflectionProbeLifecycleFrameSnapshot current,
        in ReflectionProbeLifecycleFrameSnapshot completed,
        in ReflectionProbeLifecycleSnapshot baseline,
        ulong firstSerial,
        int firstSlot)
    {
        canonical.Append("current:")
            .Append(current.Valid ? '1' : '0').Append(':')
            .Append(current.GpuTimingRecorded ? '1' : '0').Append(':')
            .Append(RelativeSerial(current.FrameSerial, firstSerial))
            .Append(':')
            .Append(RelativeSlot(current.FrameSlot, firstSlot)).Append(':');
        AppendReflectionLifecycle(canonical, current.Lifecycle, baseline);
        canonical.Append("|completed:")
            .Append(completed.Valid ? '1' : '0').Append(':')
            .Append(completed.GpuTimingRecorded ? '1' : '0').Append(':')
            .Append(RelativeSerial(completed.FrameSerial, firstSerial))
            .Append(':')
            .Append(RelativeSlot(completed.FrameSlot, firstSlot)).Append(':');
        AppendReflectionLifecycle(canonical, completed.Lifecycle, baseline);
    }

    private static void AppendReflectionLifecycle(
        StringBuilder canonical,
        in ReflectionProbeLifecycleSnapshot lifecycle,
        in ReflectionProbeLifecycleSnapshot baseline)
    {
        canonical.Append(lifecycle.QueuedCount).Append(':')
            .Append(lifecycle.ActiveCount).Append(':')
            .Append((int)lifecycle.State).Append(':')
            .Append(lifecycle.AwaitingGpuCompletionCount).Append(':')
            .Append(lifecycle.PublishedCount).Append(':')
            .Append(lifecycle.CapturesStartedThisFrame).Append(':')
            .Append(lifecycle.CapturesCompletedThisFrame).Append(':')
            .Append(lifecycle.CaptureFaceUnitsThisFrame).Append(':')
            .Append(lifecycle.PrefilterMipUnitsThisFrame).Append(':')
            .Append(lifecycle.PublishCopyUnitsThisFrame).Append(':')
            .Append(RelativeTotal(
                lifecycle.CapturesStartedTotal,
                baseline.CapturesStartedTotal)).Append(':')
            .Append(RelativeTotal(
                lifecycle.CapturesCompletedTotal,
                baseline.CapturesCompletedTotal)).Append(':')
            .Append(RelativeTotal(
                lifecycle.CapturesPublishedTotal,
                baseline.CapturesPublishedTotal)).Append(':')
            .Append(RelativeTotal(
                lifecycle.CaptureFaceUnitsTotal,
                baseline.CaptureFaceUnitsTotal)).Append(':')
            .Append(RelativeTotal(
                lifecycle.PrefilterMipUnitsTotal,
                baseline.PrefilterMipUnitsTotal)).Append(':')
            .Append(RelativeTotal(
                lifecycle.PublishCopyUnitsTotal,
                baseline.PublishCopyUnitsTotal));
    }

    private static string RelativeSerial(ulong serial, ulong firstSerial) =>
        serial >= firstSerial
            ? "+" + (serial - firstSerial).ToString(
                CultureInfo.InvariantCulture)
            : "-" + (firstSerial - serial).ToString(
                CultureInfo.InvariantCulture);

    private static int RelativeSlot(int slot, int firstSlot)
    {
        int relative = (slot - firstSlot) % RenderingConstants.FramesInFlight;
        return relative < 0
            ? relative + RenderingConstants.FramesInFlight
            : relative;
    }

    private static string RelativeTotal(ulong value, ulong baseline) =>
        value >= baseline
            ? (value - baseline).ToString(CultureInfo.InvariantCulture)
            : "regressed";

    private static float NormalizeAnimationTime(float time, float duration)
    {
        if (!float.IsFinite(time) || !float.IsFinite(duration) || duration <= 0f)
            return float.NaN;
        float wrapped = time % duration;
        return wrapped < 0f ? wrapped + duration : wrapped;
    }

    private static bool IsSha256Identity(string? value)
    {
        const string prefix = "sha256:";
        return value != null &&
            value.Length == prefix.Length + 64 &&
            value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.AsSpan(prefix.Length).IndexOfAnyExcept(
                "0123456789abcdef".AsSpan()) < 0;
    }

    private static void ValidateReflectionFrame(
        int frameIndex,
        SampleBenchmarkActivationExecutionFrameEvidence sample,
        IReadOnlyDictionary<int, ReflectionProbeRecaptureRequestSummary>
            reflectionRequests,
        ICollection<string> failures,
        IDictionary<ulong, ReflectionWorkEvidence> currentWork,
        IDictionary<ulong, ReflectionWorkEvidence> completedWork,
        ref ulong firstFrameSerial,
        ref int firstFrameSlot,
        ref int activeFrameCount,
        ref int submittedWorkFrameCount)
    {
        if (sample.ReflectionProbeCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount ||
            sample.ReflectionProbeResolution !=
                SampleBenchmarkActivation.SponzaReflectionProbeResolution ||
            sample.ReflectionProbeMipCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeMipCount)
        {
            failures.Add(
                $"Reflection frame {frameIndex} does not use the authored " +
                "two-probe 128px/8-mip Sponza topology.");
        }

        ReflectionProbeLifecycleFrameSnapshot current =
            sample.ReflectionProbeCurrentLifecycle;
        if (!current.Valid || !current.GpuTimingRecorded ||
            current.FrameSlot < 0 ||
            current.FrameSlot >= RenderingConstants.FramesInFlight ||
            current.FrameSerial == ulong.MaxValue)
        {
            failures.Add(
                $"Reflection frame {frameIndex} lacks a valid current " +
                "frame-slot identity with GPU timing enabled.");
            return;
        }

        if (frameIndex == 0)
        {
            firstFrameSerial = current.FrameSerial;
            firstFrameSlot = current.FrameSlot;
        }
        else
        {
            ulong expectedSerial;
            try
            {
                expectedSerial = checked(
                    firstFrameSerial + (ulong)frameIndex);
            }
            catch (OverflowException)
            {
                expectedSerial = ulong.MaxValue;
            }
            int expectedSlot =
                (firstFrameSlot + frameIndex) % RenderingConstants.FramesInFlight;
            if (current.FrameSerial != expectedSerial ||
                current.FrameSlot != expectedSlot)
            {
                failures.Add(
                    $"Reflection frame {frameIndex} is not contiguous with " +
                    "the measured current-slot sequence.");
            }
        }

        ReflectionProbeLifecycleSnapshot lifecycle = current.Lifecycle;
        if (lifecycle.ActiveCount > 0)
            activeFrameCount++;
        if (HasReflectionWork(lifecycle))
        {
            submittedWorkFrameCount++;
            var work = CreateReflectionWorkEvidence(current, lifecycle);
            if (!currentWork.TryAdd(current.FrameSerial, work))
            {
                failures.Add(
                    $"Reflection current frame serial {current.FrameSerial} " +
                    "was observed more than once.");
            }
        }

        ReflectionProbeLifecycleFrameSnapshot completed =
            sample.ReflectionProbeCompletedLifecycle;
        if (completed.Valid && HasReflectionWork(completed.Lifecycle))
        {
            if (!completed.GpuTimingRecorded ||
                completed.FrameSlot < 0 ||
                completed.FrameSlot >= RenderingConstants.FramesInFlight ||
                completed.FrameSerial == ulong.MaxValue)
            {
                failures.Add(
                    $"Reflection frame {frameIndex} exposes completed work " +
                    "without an exact GPU-timed frame-slot identity.");
            }
            else
            {
                var work = CreateReflectionWorkEvidence(
                    completed,
                    completed.Lifecycle);
                if (!completedWork.TryAdd(completed.FrameSerial, work))
                {
                    failures.Add(
                        $"Reflection completed frame serial " +
                        $"{completed.FrameSerial} was observed more than once.");
                }
            }
        }

        if (SampleBenchmarkActivation.ShouldRequestReflectionRecapture(
                SampleBenchmarkActivation.ReflectionRecapture,
                frameIndex))
        {
            if (!reflectionRequests.TryGetValue(frameIndex, out var request))
            {
                failures.Add(
                    $"Reflection request frame {frameIndex} was not applied.");
            }
            else
            {
                ValidateReflectionRequest(frameIndex, request, failures);
            }
            if (lifecycle.CapturesStartedThisFrame <= 0 ||
                lifecycle.CaptureFaceUnitsThisFrame <= 0)
            {
                failures.Add(
                    $"Reflection request frame {frameIndex} did not start " +
                    "face 0 on its owning Draw.");
            }
        }
    }

    private static ReflectionWorkEvidence CreateReflectionWorkEvidence(
        in ReflectionProbeLifecycleFrameSnapshot frame,
        in ReflectionProbeLifecycleSnapshot lifecycle) =>
        new(
            frame.FrameSlot,
            lifecycle.CaptureFaceUnitsThisFrame,
            lifecycle.PrefilterMipUnitsThisFrame,
            lifecycle.PublishCopyUnitsThisFrame);

    private static int ReconcileCompletedReflectionWork(
        IReadOnlyDictionary<ulong, ReflectionWorkEvidence> currentWork,
        IReadOnlyDictionary<ulong, ReflectionWorkEvidence> completedWork,
        ulong firstFrameSerial,
        int measuredFrameCount,
        ulong faceDelta,
        ulong mipDelta,
        ulong copyDelta,
        ICollection<string> failures)
    {
        ulong lastFrameSerial;
        try
        {
            lastFrameSerial = measuredFrameCount == 0
                ? firstFrameSerial
                : checked(firstFrameSerial + (ulong)(measuredFrameCount - 1));
        }
        catch (OverflowException)
        {
            failures.Add(
                "Reflection measured frame-serial range overflowed.");
            return 0;
        }

        int matched = 0;
        ulong completedFaces = 0;
        ulong completedMips = 0;
        ulong completedCopies = 0;
        foreach ((ulong serial, ReflectionWorkEvidence current) in currentWork)
        {
            if (!completedWork.TryGetValue(serial, out var completed) ||
                completed != current)
            {
                failures.Add(
                    $"Reflection submitted work at frame serial {serial} " +
                    "does not have one exact completed-slot counterpart.");
                continue;
            }
            matched++;
            completedFaces = checked(
                completedFaces + (ulong)completed.FaceUnits);
            completedMips = checked(
                completedMips + (ulong)completed.PrefilterMipUnits);
            completedCopies = checked(
                completedCopies + (ulong)completed.PublishCopyUnits);
        }

        foreach ((ulong serial, ReflectionWorkEvidence _) in completedWork)
        {
            if (serial >= firstFrameSerial && serial <= lastFrameSerial &&
                !currentWork.ContainsKey(serial))
            {
                failures.Add(
                    $"Reflection completed-slot work at route frame serial " +
                    $"{serial} has no current-route submission evidence.");
            }
        }
        if (completedFaces != faceDelta || completedMips != mipDelta ||
            completedCopies != copyDelta)
        {
            failures.Add(
                "Reflection completed-slot face/mip/copy units do not " +
                "reconcile to the current-route counter deltas.");
        }
        return matched;
    }

    private static void ValidateReflectionRequest(
        int frameIndex,
        in ReflectionProbeRecaptureRequestSummary request,
        ICollection<string> failures)
    {
        if (request.BeforeLifecycle.QueuedCount != 0 ||
            request.BeforeLifecycle.ActiveCount != 0 ||
            request.BeforeLifecycle.State != ReflectionProbeCaptureState.Published ||
            request.BeforeLifecycle.PublishedCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount)
        {
            failures.Add(
                $"Reflection request frame {frameIndex} did not begin from " +
                "an idle published lifecycle.");
        }
        if (request.RequestedProbeCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount ||
            request.AdmittedProbeCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount ||
            request.DeferredProbeCount != 0 ||
            request.CoalescedProbeCount != 0 ||
            request.RejectedProbeCount != 0)
        {
            failures.Add(
                $"Reflection request frame {frameIndex} was not admitted " +
                "exactly once for every active probe.");
        }
        if (request.AfterLifecycle.QueuedCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount ||
            request.AfterLifecycle.ActiveCount != 0 ||
            request.AfterLifecycle.State != ReflectionProbeCaptureState.Queued ||
            request.AfterLifecycle.PublishedCount !=
                SampleBenchmarkActivation.SponzaReflectionProbeCount)
        {
            failures.Add(
                $"Reflection request frame {frameIndex} did not publish an " +
                "exact queued scheduler state before Draw.");
        }
    }

    private static void ValidateReflectionAggregate(
        SampleBenchmarkActivationExecutionFrameEvidence baseline,
        IReadOnlyList<SampleBenchmarkActivationExecutionFrameEvidence> samples,
        IReadOnlyDictionary<int, ReflectionProbeRecaptureRequestSummary> requests,
        int activeFrames,
        int submittedWorkFrames,
        int completedWorkFrames,
        ulong startedDelta,
        ulong completedDelta,
        ulong publishedDelta,
        ulong faceDelta,
        ulong mipDelta,
        ulong copyDelta,
        ICollection<string> failures)
    {
        ReflectionProbeLifecycleFrameSnapshot baselineFrame =
            baseline.ReflectionProbeCurrentLifecycle;
        if (!baselineFrame.Valid || !baselineFrame.GpuTimingRecorded ||
            baselineFrame.FrameSerial == ulong.MaxValue ||
            baselineFrame.FrameSlot < 0 ||
            baselineFrame.FrameSlot >= RenderingConstants.FramesInFlight)
        {
            failures.Add(
                "Reflection activation lacks the prior rendered frame-slot " +
                "boundary required to arm request 0 before Draw.");
        }
        else if (samples.Count > 0)
        {
            ReflectionProbeLifecycleFrameSnapshot first =
                samples[0].ReflectionProbeCurrentLifecycle;
            ulong expectedFirst = baselineFrame.FrameSerial == ulong.MaxValue
                ? ulong.MaxValue
                : baselineFrame.FrameSerial + 1UL;
            int expectedSlot =
                (baselineFrame.FrameSlot + 1) % RenderingConstants.FramesInFlight;
            if (!first.Valid || first.FrameSerial != expectedFirst ||
                first.FrameSlot != expectedSlot)
            {
                failures.Add(
                    "Reflection request 0 was not recorded on the frame " +
                    "immediately after the activation arming boundary.");
            }
        }

        IReadOnlyList<int> expected =
            SampleBenchmarkActivation.ReflectionRecaptureSchedule;
        if (requests.Count != expected.Count ||
            !requests.Keys.SequenceEqual(expected))
        {
            failures.Add(
                "Reflection activation did not apply the exact authored schedule.");
        }
        int admitted = requests.Values.Sum(static request =>
            request.AdmittedProbeCount);
        if (activeFrames <
            SampleBenchmarkActivation.MinimumReflectionActiveFrameCount)
        {
            failures.Add(
                $"Reflection activation was active for {activeFrames} frames; " +
                $"at least {SampleBenchmarkActivation.MinimumReflectionActiveFrameCount} are required.");
        }
        if (submittedWorkFrames <
            SampleBenchmarkActivation.MinimumReflectionActiveFrameCount)
        {
            failures.Add(
                "Reflection activation did not submit enough measured work frames.");
        }
        if (completedWorkFrames != submittedWorkFrames)
        {
            failures.Add(
                "Reflection completed-slot work did not cover every submitted " +
                "measured work frame.");
        }
        if (admitted <= 0 || startedDelta != (ulong)admitted ||
            completedDelta != (ulong)admitted ||
            publishedDelta != (ulong)admitted)
        {
            failures.Add(
                "Reflection request/start/complete/publish totals do not " +
                "reconcile exactly.");
        }
        ulong expectedFaceUnits = checked((ulong)admitted * 6UL);
        ulong expectedMipUnits = checked(
            (ulong)admitted *
            (SampleBenchmarkActivation.SponzaReflectionProbeMipCount - 1UL));
        ulong expectedCopyUnits = checked((ulong)admitted);
        if (faceDelta != expectedFaceUnits ||
            mipDelta != expectedMipUnits ||
            copyDelta != expectedCopyUnits)
        {
            failures.Add(
                "Reflection activation work totals do not match the exact " +
                "two-probe face/mip/copy topology.");
        }
        if (samples.Count > 0)
        {
            ReflectionProbeLifecycleSnapshot terminal =
                samples[^1].ReflectionProbeCurrentLifecycle.Lifecycle;
            if (terminal.QueuedCount != 0 || terminal.ActiveCount != 0 ||
                terminal.State != ReflectionProbeCaptureState.Published)
            {
                failures.Add(
                    "Reflection activation did not publish its final request " +
                    "inside the measured route.");
            }
        }
    }

    private static void ValidateDirectionalFrame(
        string activation,
        int frameIndex,
        SampleBenchmarkActivationExecutionFrameEvidence sample,
        bool allowCameraDrivenRefresh,
        ICollection<string> failures,
        ref int activeFrames,
        ref int reuseFrames,
        ref int refreshFrames,
        ref int truthfulCacheFrames,
        ref int dynamicFrames,
        ref int animatorFrames,
        ref int gpuFrames)
    {
        DirectionalShadowRuntimeDiagnostics runtime =
            sample.DirectionalShadowRuntime;
        int activeMask = runtime.StaticCacheActiveMask;
        bool active = runtime.Enabled != 0 && activeMask != 0;
        if (active)
            activeFrames++;
        bool dynamic = sample.DirectionalDynamicShadowMeshletCount > 0 &&
            sample.DirectionalShadowSkinnedObjectCount > 0;
        if (dynamic)
            dynamicFrames++;
        bool animator = sample.PlayingAnimatorCount > 0 &&
            sample.SkinningDispatchCount > 0 &&
            sample.SkinnedObjectCount > 0;
        if (animator)
            animatorFrames++;
        if (sample.GpuDirectionalShadowMicroseconds > 0)
            gpuFrames++;

        bool forced = activation ==
            SampleBenchmarkActivation.DirectionalShadowForcedRefresh;
        int refreshMask = runtime.StaticCacheRefreshMask;
        int reuseMask = runtime.StaticCacheReuseMask;
        bool truthfulMasks = (refreshMask | reuseMask) == activeMask &&
            (refreshMask & reuseMask) == 0;
        bool allRefresh = refreshMask == activeMask && reuseMask == 0;
        bool allReuse = refreshMask == 0 && reuseMask == activeMask;
        bool cachePolicy = forced
            ? allRefresh
            : allowCameraDrivenRefresh
                ? truthfulMasks
                : allReuse;
        if (allRefresh)
            refreshFrames++;
        if (allReuse)
            reuseFrames++;

        bool provenanceValid = HasExactDirectionalCacheProvenance(runtime);
        if (truthfulMasks && provenanceValid)
            truthfulCacheFrames++;
        if (!active || !cachePolicy || !provenanceValid || !dynamic ||
            !animator || sample.GpuDirectionalShadowMicroseconds <= 0)
        {
            failures.Add(
                $"Directional activation frame {frameIndex} lacks exact " +
                "cache, provenance, skinned-caster, animator, or GPU-pass evidence.");
        }
    }

    internal static bool HasExactDirectionalCacheProvenance(
        DirectionalShadowRuntimeDiagnostics runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        int activeMask = runtime.StaticCacheActiveMask;
        int supportedMask =
            (1 << ShadowSettings.MaxDirectionalCascades) - 1;
        if (activeMask == 0 || (activeMask & ~supportedMask) != 0 ||
            runtime.CacheLayerProvenance == null ||
            runtime.CacheLayerProvenance.Count !=
                ShadowSettings.MaxDirectionalCascades)
        {
            return false;
        }

        int seenCascadeMask = 0;
        int activeProvenanceMask = 0;
        foreach (DirectionalShadowCacheLayerProvenance layer in
                 runtime.CacheLayerProvenance)
        {
            if ((uint)layer.CascadeIndex >=
                ShadowSettings.MaxDirectionalCascades)
            {
                return false;
            }
            int bit = 1 << layer.CascadeIndex;
            if ((seenCascadeMask & bit) != 0)
                return false;
            seenCascadeMask |= bit;

            bool expectedActive = (activeMask & bit) != 0;
            if (layer.Active != (expectedActive ? 1 : 0))
                return false;
            if (!expectedActive)
                continue;

            activeProvenanceMask |= bit;
            bool refreshed =
                (runtime.StaticCacheRefreshMask & bit) != 0;
            bool reused = (runtime.StaticCacheReuseMask & bit) != 0;
            if (layer.FinalWorkingLayerValid == 0 ||
                layer.DynamicWorkAppended == 0 ||
                layer.CopiedFromCache == 0 ||
                (refreshed
                    ? reused || layer.RefreshedThisFrame == 0 ||
                      layer.ExplicitlyCleared == 0 ||
                      layer.CacheState !=
                          DirectionalShadowCacheLayerState.RefreshRecorded
                    : !reused || layer.RefreshedThisFrame != 0 ||
                      layer.ExplicitlyCleared != 0 ||
                      layer.CacheState !=
                          DirectionalShadowCacheLayerState.Valid))
            {
                return false;
            }
        }
        return seenCascadeMask == supportedMask &&
            activeProvenanceMask == activeMask &&
            BitOperations.PopCount(unchecked((uint)activeProvenanceMask)) ==
            BitOperations.PopCount(unchecked((uint)activeMask));
    }

    private static void ValidateForwardFrame(
        string captureVariant,
        int frameIndex,
        SampleBenchmarkActivationExecutionFrameEvidence sample,
        ICollection<string> failures,
        ref int activeFrames,
        ref int suppressedFrames,
        ref int exactFrames,
        ref int cacheFrames,
        ref int disabledPipelineFrames,
        ref int exactPipelineFrames,
        ref int gpuFrames)
    {
        bool active = sample.GlobalIlluminationEnabled != 0 &&
            sample.SimpleDdgiActive != 0;
        if (active)
            activeFrames++;
        if (sample.ForwardGiBenchmarkSuppressed != 0)
            suppressedFrames++;
        if (sample.ForwardGiBenchmarkForcedExact != 0)
            exactFrames++;
        if (sample.ForwardGiReceiverCacheConsumed != 0)
            cacheFrames++;
        if (sample.ForwardGiDisabledPipelineUsed != 0)
            disabledPipelineFrames++;
        if (sample.ForwardGiExactGatherUsed != 0)
            exactPipelineFrames++;
        if (sample.GpuForwardGiGatherMicroseconds > 0)
            gpuFrames++;

        bool controls = captureVariant switch
        {
            SampleBenchmarkCaptureVariant.ForwardGiEnabled =>
                sample.ForwardGiBenchmarkSuppressed == 0 &&
                sample.ForwardGiBenchmarkForcedExact == 0 &&
                sample.ForwardGiReceiverCacheConsumed != 0 &&
                sample.ForwardGiDisabledPipelineUsed == 0 &&
                sample.ForwardGiExactGatherUsed == 0,
            SampleBenchmarkCaptureVariant.ForwardGiDisabled =>
                sample.ForwardGiBenchmarkSuppressed != 0 &&
                sample.ForwardGiBenchmarkForcedExact == 0 &&
                sample.ForwardGiReceiverCacheConsumed == 0 &&
                sample.ForwardGiDisabledPipelineUsed != 0 &&
                sample.ForwardGiExactGatherUsed == 0,
            SampleBenchmarkCaptureVariant.ForwardGiExact =>
                sample.ForwardGiBenchmarkSuppressed == 0 &&
                sample.ForwardGiBenchmarkForcedExact != 0 &&
                sample.ForwardGiReceiverCacheConsumed == 0 &&
                sample.ForwardGiDisabledPipelineUsed == 0 &&
                sample.ForwardGiExactGatherUsed != 0,
            _ => false
        };
        if (!active || !controls || sample.GpuForwardGiGatherMicroseconds <= 0)
        {
            failures.Add(
                $"Forward activation frame {frameIndex} lacks active GI, " +
                "effective pipeline/cache controls, or GPU-pass evidence.");
        }
    }

    private static bool HasReflectionWork(
        in ReflectionProbeLifecycleSnapshot lifecycle) =>
        lifecycle.CaptureFaceUnitsThisFrame > 0 ||
        lifecycle.PrefilterMipUnitsThisFrame > 0 ||
        lifecycle.PublishCopyUnitsThisFrame > 0;

    private static ulong SubtractMonotonic(
        ulong value,
        ulong baseline,
        string label,
        ICollection<string> failures)
    {
        if (value >= baseline)
            return value - baseline;
        failures.Add($"{label} regressed inside the measurement window.");
        return 0UL;
    }
}
