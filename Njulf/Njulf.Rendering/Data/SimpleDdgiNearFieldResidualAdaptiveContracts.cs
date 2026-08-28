using System;
using System.Runtime.InteropServices;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>Per-tile work selected by the optional C5 local scheduler.</summary>
public enum SimpleDdgiNearFieldResidualTileClass : uint
{
    Inactive = 0u,
    TraceHigh = 1u,
    TraceNormal = 2u,
    TraceInterleaved = 3u,
    HistoryOnly = 4u
}

/// <summary>
/// Compact persistent scheduler state. This is deliberately separate from the
/// diagnostic tile stream: diagnostics may be reset/read back without erasing
/// the state used to make the next frame's bounded scheduling decision.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 16)]
public struct GPUSimpleDdgiNearFieldResidualSchedulerRecord
{
    public uint PackedState;
    public float SignedResidualEnergy;
    public float Variance;
    public uint PackedEpochAndReceiver;
}

/// <summary>Shared packing and arena constants for C5 ABI V16.</summary>
public static class SimpleDdgiNearFieldResidualAdaptiveAbi
{
    public const uint SchedulerRecordByteCount = 16u;
    public const uint TileClassMask = 0x7u;
    public const uint CheckerboardPhaseShift = 3u;
    public const uint CheckerboardPhaseMask = 0x1u <<
        (int)CheckerboardPhaseShift;
    public const uint RayCountShift = 4u;
    public const uint RayCountMask = 0x7u << (int)RayCountShift;
    public const uint ValidBit = 1u << 7;
    public const uint AgeShift = 8u;
    public const uint AgeMask = 0xffu << (int)AgeShift;
    public const uint ConfidenceShift = 16u;
    public const uint ConfidenceMask = 0xffu << (int)ConfidenceShift;

    // Header words 8..31 are the eight VkDispatchIndirectCommand records.
    // Adaptive counters live after them and before the two full-capacity lists.
    public const uint TraceHighTileCountWord = 32u;
    public const uint TraceNormalTileCountWord = 33u;
    public const uint InterleavedTileCountWord = 34u;
    public const uint HistoryOnlyTileCountWord = 35u;
    public const uint InactiveTileCountWord = 36u;
    public const uint ForcedRefreshTileCountWord = 37u;
    public const uint RequestedPixelCountWord = 38u;
    public const uint RequestedRayCountWord = 39u;
    public const uint SavedPixelCountWord = 40u;
    public const uint SavedRayCountWord = 41u;
    public const uint MaximumAgeWord = 42u;
    public const uint FallbackFlagsWord = 43u;

    public static uint TraceListFirstWord =>
        SimpleDdgiNearFieldResidualGpuAbi.ActiveTileHeaderWordCount;

    public static uint ResolveListFirstWord(uint tileCapacity) => checked(
        TraceListFirstWord + tileCapacity);

    public static ulong ArenaByteCount(uint tileCapacity) => checked(
        (ulong)(ResolveListFirstWord(tileCapacity) + tileCapacity) *
        sizeof(uint));

    public static ulong SchedulerHistoryByteCount(uint tileCapacity) =>
        checked((ulong)tileCapacity * SchedulerRecordByteCount * 2UL);

    public static uint PackState(
        SimpleDdgiNearFieldResidualTileClass tileClass,
        uint checkerboardPhase,
        uint rayCount,
        bool valid,
        uint age,
        float confidence)
    {
        if (!Enum.IsDefined(tileClass))
            throw new ArgumentOutOfRangeException(nameof(tileClass));
        if (checkerboardPhase > 1u)
            throw new ArgumentOutOfRangeException(nameof(checkerboardPhase));
        if (rayCount > 4u)
            throw new ArgumentOutOfRangeException(nameof(rayCount));
        if (!float.IsFinite(confidence))
            throw new ArgumentOutOfRangeException(nameof(confidence));

        uint confidenceByte = checked((uint)MathF.Round(
            Math.Clamp(confidence, 0.0f, 1.0f) * 255.0f));
        return (uint)tileClass |
            (checkerboardPhase << (int)CheckerboardPhaseShift) |
            (rayCount << (int)RayCountShift) |
            (valid ? ValidBit : 0u) |
            (Math.Min(age, 255u) << (int)AgeShift) |
            (confidenceByte << (int)ConfidenceShift);
    }

    public static SimpleDdgiNearFieldResidualTileClass UnpackClass(uint packed) =>
        (SimpleDdgiNearFieldResidualTileClass)(packed & TileClassMask);

    public static uint UnpackPhase(uint packed) =>
        (packed & CheckerboardPhaseMask) >> (int)CheckerboardPhaseShift;

    public static uint UnpackRayCount(uint packed) =>
        (packed & RayCountMask) >> (int)RayCountShift;

    public static uint UnpackAge(uint packed) =>
        (packed & AgeMask) >> (int)AgeShift;

    public static float UnpackConfidence(uint packed) =>
        ((packed & ConfidenceMask) >> (int)ConfidenceShift) / 255.0f;

    public static void VerifyManagedLayout()
    {
        if (Marshal.SizeOf<GPUSimpleDdgiNearFieldResidualSchedulerRecord>() !=
            SchedulerRecordByteCount)
        {
            throw new InvalidOperationException(
                "C5 scheduler history record must remain exactly 16 bytes.");
        }
    }
}

public readonly record struct SimpleDdgiNearFieldResidualSchedulerThresholds(
    float HighMotion,
    float HighVariance,
    float ActiveEnergy,
    float PerceptualEnergyFloor,
    float LowConfidence,
    uint MaximumHistoryOnlyAge,
    uint ForcedRefreshPeriod)
{
    public static SimpleDdgiNearFieldResidualSchedulerThresholds ForPreset(
        SimpleDdgiNearFieldResidualQualityPreset preset) => preset switch
        {
            SimpleDdgiNearFieldResidualQualityPreset.Performance => new(
                0.020f, 0.0060f, 0.0120f, 0.0030f, 0.30f, 8u, 8u),
            SimpleDdgiNearFieldResidualQualityPreset.Quality => new(
                0.010f, 0.0020f, 0.0040f, 0.0010f, 0.55f, 4u, 4u),
            _ => new(0.015f, 0.0040f, 0.0075f, 0.0018f, 0.42f, 6u, 6u)
        };

    public bool IsValid =>
        float.IsFinite(HighMotion) && HighMotion >= 0.0f &&
        float.IsFinite(HighVariance) && HighVariance >= 0.0f &&
        float.IsFinite(ActiveEnergy) && ActiveEnergy >= 0.0f &&
        float.IsFinite(PerceptualEnergyFloor) &&
        PerceptualEnergyFloor >= 0.0f &&
        PerceptualEnergyFloor <= ActiveEnergy &&
        float.IsFinite(LowConfidence) && LowConfidence is >= 0.0f and <= 1.0f &&
        MaximumHistoryOnlyAge is > 0u and <= 255u &&
        ForcedRefreshPeriod is > 0u and <= 255u;
}

public readonly record struct SimpleDdgiNearFieldResidualSchedulerInput(
    bool ReceiverOccupied,
    bool HistoryValid,
    bool ReprojectionValid,
    bool ReceiverIdentityMatches,
    bool StructuralEpochMatches,
    bool LightingEpochMatches,
    float MaximumMotion,
    float SignedResidualEnergy,
    float Variance,
    float Confidence,
    uint Age,
    uint TileX,
    uint TileY,
    ulong FrameSerial,
    uint MaximumRaysPerPixel);

public readonly record struct SimpleDdgiNearFieldResidualSchedulerDecision(
    SimpleDdgiNearFieldResidualTileClass TileClass,
    uint CheckerboardPhase,
    uint RaysPerSelectedPixel,
    bool AppendResolve,
    bool AppendTrace,
    bool ForcedRefresh,
    string Reason);

/// <summary>
/// Deterministic CPU mirror of the shader classifier. It is intentionally
/// conservative: uncertainty escalates work and can never select history-only.
/// </summary>
public static class SimpleDdgiNearFieldResidualScheduler
{
    public static SimpleDdgiNearFieldResidualSchedulerDecision Select(
        in SimpleDdgiNearFieldResidualSchedulerInput input,
        in SimpleDdgiNearFieldResidualSchedulerThresholds thresholds)
    {
        if (!thresholds.IsValid || input.MaximumRaysPerPixel is < 1u or > 4u)
            throw new ArgumentOutOfRangeException(nameof(input));

        uint phase = HashPhase(input.TileX, input.TileY, input.FrameSerial);
        uint highRays = Math.Min(4u, input.MaximumRaysPerPixel);
        uint normalRays = Math.Min(2u, input.MaximumRaysPerPixel);
        if (!input.ReceiverOccupied)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.Inactive,
                phase, 0u, false, false, false, "inactive-no-current-receiver");
        }

        bool finite = float.IsFinite(input.MaximumMotion) &&
            float.IsFinite(input.SignedResidualEnergy) &&
            float.IsFinite(input.Variance) &&
            float.IsFinite(input.Confidence);
        bool changed = !finite || !input.HistoryValid ||
            !input.ReprojectionValid || !input.ReceiverIdentityMatches ||
            !input.StructuralEpochMatches || !input.LightingEpochMatches ||
            input.MaximumMotion > thresholds.HighMotion;
        if (changed)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.TraceHigh,
                phase, highRays, true, true, false,
                "trace-high-new-disoccluded-or-changed");
        }

        bool forcedRefresh = input.Age >= thresholds.MaximumHistoryOnlyAge ||
            ((input.FrameSerial + HashTile(input.TileX, input.TileY)) %
                thresholds.ForcedRefreshPeriod) == 0UL;
        if (forcedRefresh)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.TraceNormal,
                phase, normalRays, true, true, true,
                "trace-normal-bounded-refresh");
        }

        float absoluteEnergy = MathF.Abs(input.SignedResidualEnergy);
        if (input.Variance >= thresholds.HighVariance ||
            input.Confidence < thresholds.LowConfidence)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.TraceHigh,
                phase, highRays, true, true, false,
                "trace-high-variance-or-low-confidence");
        }
        if (absoluteEnergy >= thresholds.ActiveEnergy)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.TraceNormal,
                phase, normalRays, true, true, false,
                "trace-normal-active-residual");
        }
        if (absoluteEnergy >= thresholds.PerceptualEnergyFloor ||
            MathF.Sqrt(MathF.Max(input.Variance, 0.0f)) >=
                thresholds.PerceptualEnergyFloor)
        {
            return new(SimpleDdgiNearFieldResidualTileClass.TraceInterleaved,
                phase, 1u, true, true, false,
                "trace-interleaved-stable-nonzero-residual");
        }
        return new(SimpleDdgiNearFieldResidualTileClass.HistoryOnly,
            phase, 0u, true, false, false,
            "history-only-stable-below-perceptual-floor");
    }

    public static uint HashPhase(uint tileX, uint tileY, ulong frameSerial) =>
        (uint)((HashTile(tileX, tileY) + frameSerial) & 1UL);

    private static uint HashTile(uint x, uint y)
    {
        uint value = x * 0x9e3779b9u ^ y * 0x85ebca6bu;
        value ^= value >> 16;
        value *= 0x7feb352du;
        value ^= value >> 15;
        return value;
    }
}
