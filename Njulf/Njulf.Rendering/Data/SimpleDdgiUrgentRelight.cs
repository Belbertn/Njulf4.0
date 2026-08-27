using System;

namespace Njulf.Rendering.Data;

/// <summary>
/// Fence-complete evidence for the bounded pre-forward relight lane. Accepted
/// probes are producer-complete private transactions; committed probes are the
/// subset published coherently to the canonical receiver field.
/// </summary>
public readonly record struct SimpleDdgiUrgentRelightEvidence(
    uint AcceptedProbeCount,
    uint CommittedProbeCount)
{
    public uint RejectedProbeCount =>
        AcceptedProbeCount >= CommittedProbeCount
            ? AcceptedProbeCount - CommittedProbeCount
            : 0u;
}

/// <summary>
/// CPU mirror of the shader-side urgent-lane policy and compact telemetry ABI.
/// The lane is intentionally limited to one-byte cardinalities so commit can
/// increment the low byte atomically without carrying into accepted/frame data.
/// </summary>
public static class SimpleDdgiUrgentRelightPolicy
{
    public const int MaximumProbeBudget = byte.MaxValue;
    public const int MaximumSweepCount = 4;
    // Urgent work is allowed to consume only a small slice of measured frame
    // headroom. This is deliberately independent of the ordinary DDGI budget.
    public const long MaximumAdditionalGpuMicroseconds = 500;

    public static bool IsEligible(
        SimpleDdgiSourceRefreshMode sourceRefreshMode,
        bool cachedGeometryReady,
        bool visible,
        int ringIndex,
        bool regionalDirty,
        bool topologyInvalid,
        bool scrollExposed,
        bool atlasFresh,
        bool sourceInvalid,
        bool relocationPending,
        bool inactive)
    {
        bool radiometricRelight = sourceRefreshMode is
            SimpleDdgiSourceRefreshMode.EnvironmentMissRelight or
            SimpleDdgiSourceRefreshMode.CachedHitRelight;
        return radiometricRelight &&
            cachedGeometryReady &&
            visible &&
            ringIndex == 0 &&
            !regionalDirty &&
            !topologyInvalid &&
            !scrollExposed &&
            !atlasFresh &&
            !sourceInvalid &&
            !relocationPending &&
            !inactive;
    }

    public static uint ResolveBudget(int configuredBudget) =>
        checked((uint)Math.Clamp(
            configuredBudget,
            0,
            MaximumProbeBudget));

    /// <summary>
    /// Selects a bounded number of cached Jacobi sweeps from fence-complete
    /// timing and transport evidence. Missing or malformed evidence always
    /// resolves to the existing one-sweep lane.
    /// </summary>
    public static int ResolveSweepCount(
        int configuredMaximum,
        float residual,
        float tolerance,
        float contraction,
        long previousFrameGpuMicroseconds,
        long targetFrameGpuMicroseconds,
        long estimatedAdditionalSweepMicroseconds)
    {
        int maximum = Math.Clamp(configuredMaximum, 1, MaximumSweepCount);
        if (maximum == 1 ||
            !float.IsFinite(residual) || residual < 0.0f ||
            !float.IsFinite(tolerance) || tolerance < 0.0001f ||
            !float.IsFinite(contraction) || contraction < 0.0f ||
            contraction >= 1.0f ||
            previousFrameGpuMicroseconds <= 0L ||
            targetFrameGpuMicroseconds <= previousFrameGpuMicroseconds ||
            estimatedAdditionalSweepMicroseconds <= 0L)
        {
            return 1;
        }

        long measuredHeadroom = targetFrameGpuMicroseconds -
            previousFrameGpuMicroseconds;
        long urgentHeadroom = Math.Min(
            measuredHeadroom,
            MaximumAdditionalGpuMicroseconds);
        int affordable = 1 + checked((int)Math.Min(
            urgentHeadroom / estimatedAdditionalSweepMicroseconds,
            MaximumSweepCount - 1L));

        int useful = 1;
        float predictedResidual = residual;
        while (useful < maximum && predictedResidual > tolerance)
        {
            predictedResidual *= contraction;
            useful++;
        }

        return Math.Clamp(Math.Min(useful, affordable), 1, maximum);
    }

    public static uint PackTelemetry(
        uint frameSerialLow,
        uint acceptedProbeCount,
        uint committedProbeCount)
    {
        uint accepted = Math.Min(acceptedProbeCount, byte.MaxValue);
        uint committed = Math.Min(committedProbeCount, accepted);
        return ((frameSerialLow & 0xffffu) << 16) |
            (accepted << 8) |
            committed;
    }

    public static SimpleDdgiUrgentRelightEvidence UnpackTelemetry(
        uint packedTelemetry,
        uint expectedFrameSerialLow)
    {
        uint stampedFrame = packedTelemetry >> 16;
        if (stampedFrame != (expectedFrameSerialLow & 0xffffu))
            return default;

        uint accepted = (packedTelemetry >> 8) & 0xffu;
        uint committed = packedTelemetry & 0xffu;
        return new SimpleDdgiUrgentRelightEvidence(
            accepted,
            Math.Min(committed, accepted));
    }
}
