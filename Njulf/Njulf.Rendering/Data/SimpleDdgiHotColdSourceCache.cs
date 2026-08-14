using System;

namespace Njulf.Rendering.Data;

public readonly record struct SimpleDdgiHotColdAdmissionSample(
    ulong SurfaceHitCount,
    ulong MissCount,
    ulong RejectedBackFaceCount);

public readonly record struct SimpleDdgiHotColdAdmissionState(
    bool Admitted,
    ulong AcceptedSampleCount,
    ulong AcceptedRayCount,
    float ColdExitFraction,
    string Reason)
{
    public ulong LayoutIdentity { get; init; }
    public bool HasCompletedSampleForIdentity { get; init; }
    public ulong LastCompletedSampleFrameSerial { get; init; }
}

/// <summary>
/// Hysteretic admission controller for the source-cache SoA layout. It consumes
/// completed trace classifications only; missing readback cannot alter layout.
/// Enabling requires stronger evidence than disabling, so cache generations do
/// not churn around the break-even point.
/// </summary>
public sealed class SimpleDdgiHotColdAdmissionModel
{
    public const ulong MinimumMeasuredRayCount = 4_096;
    public const float AdmissionColdFraction = 0.30f;
    public const float RevocationColdFraction = 0.15f;
    private const double Alpha = 0.125;

    private double _coldFraction;
    private ulong _acceptedSampleCount;
    private ulong _acceptedRayCount;
    private bool _admitted;
    private string _reason = "awaiting-completed-work-sample";
    private ulong _layoutIdentity;
    private bool _identityInitialized;
    private bool _hasCompletedSampleForIdentity;
    private ulong _lastCompletedSampleFrameSerial;

    public SimpleDdgiHotColdAdmissionState State => new(
        _admitted,
        _acceptedSampleCount,
        _acceptedRayCount,
        (float)_coldFraction,
        _reason)
    {
        LayoutIdentity = _layoutIdentity,
        HasCompletedSampleForIdentity = _hasCompletedSampleForIdentity,
        LastCompletedSampleFrameSerial = _lastCompletedSampleFrameSerial
    };

    public bool EnsureIdentity(ulong layoutIdentity)
    {
        if (_identityInitialized && _layoutIdentity == layoutIdentity)
            return false;

        bool changed = _admitted;
        _layoutIdentity = layoutIdentity;
        _identityInitialized = true;
        _coldFraction = 0.0;
        _acceptedSampleCount = 0;
        _acceptedRayCount = 0;
        _admitted = false;
        _hasCompletedSampleForIdentity = false;
        _lastCompletedSampleFrameSerial = 0UL;
        _reason = "awaiting-completed-work-sample";
        return changed;
    }

    public bool Observe(
        SimpleDdgiHotColdAdmissionSample sample,
        ulong layoutIdentity = 0UL,
        ulong sampleFrameSerial = 0UL)
    {
        bool identityChanged = EnsureIdentity(layoutIdentity);
        ulong total;
        ulong cold;
        try
        {
            total = checked(sample.SurfaceHitCount + sample.MissCount +
                sample.RejectedBackFaceCount);
            cold = checked(sample.MissCount + sample.RejectedBackFaceCount);
        }
        catch (OverflowException)
        {
            _reason = identityChanged
                ? "counter-overflow-reset-for-layout-identity"
                : "counter-overflow-retained-last-layout";
            return identityChanged;
        }

        if (total == 0)
        {
            _reason = !_hasCompletedSampleForIdentity
                ? "awaiting-completed-work-sample"
                : _admitted
                    ? "hot-cold-layout-retained-no-new-sample"
                    : "fixed-record-retained-no-new-sample";
            return identityChanged;
        }

        _hasCompletedSampleForIdentity = true;
        _lastCompletedSampleFrameSerial = sampleFrameSerial;
        double measured = cold / (double)total;
        _coldFraction = _acceptedSampleCount == 0
            ? measured
            : _coldFraction + (measured - _coldFraction) * Alpha;
        _acceptedSampleCount++;
        _acceptedRayCount = SaturatingAdd(_acceptedRayCount, total);

        bool previous = _admitted;
        if (_acceptedRayCount < MinimumMeasuredRayCount)
        {
            _reason = "insufficient-measured-rays";
            return identityChanged;
        }

        if (!_admitted && _coldFraction >= AdmissionColdFraction)
        {
            _admitted = true;
            _reason = "measured-cold-exits-cover-sidecar-addressing";
        }
        else if (_admitted && _coldFraction <= RevocationColdFraction)
        {
            _admitted = false;
            _reason = "hit-heavy-workload-prefers-fixed-record";
        }
        else
        {
            _reason = _admitted
                ? "hot-cold-layout-retained-by-hysteresis"
                : "fixed-record-retained-by-hysteresis";
        }

        return identityChanged || previous != _admitted;
    }

    public static ulong EstimateSolveReadBytes(
        SimpleDdgiTransportCacheFormat format,
        ulong rayCount,
        float coldExitFraction,
        bool hotColdLayout,
        int conditionalSurfaceSidecarBytes = 0)
    {
        if (conditionalSurfaceSidecarBytes < 0)
            throw new ArgumentOutOfRangeException(
                nameof(conditionalSurfaceSidecarBytes));
        double cold = Math.Clamp(coldExitFraction, 0.0f, 1.0f);
        double sidecarBytes = rayCount * (1.0 - cold) *
            conditionalSurfaceSidecarBytes;
        if (format is not (SimpleDdgiTransportCacheFormat.Compact24 or
            SimpleDdgiTransportCacheFormat.Compact28) || rayCount == 0)
        {
            ulong ordinaryBytes = checked(
                rayCount * (ulong)Math.Max(format.WordCount(), 0) *
                sizeof(uint));
            return checked(ordinaryBytes + (ulong)Math.Ceiling(sidecarBytes));
        }

        int headerWords = format == SimpleDdgiTransportCacheFormat.Compact28
            ? 4
            : 3;
        if (!hotColdLayout)
        {
            ulong ordinaryBytes = checked(
                rayCount * (ulong)format.WordCount() * sizeof(uint));
            return checked(ordinaryBytes + (ulong)Math.Ceiling(sidecarBytes));
        }

        double expectedWords = rayCount * (headerWords + (1.0 - cold) * 3.0);
        return checked((ulong)Math.Ceiling(
            expectedWords * sizeof(uint) + sidecarBytes));
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

/// <summary>CPU address oracle for the page-local hot-header/sidecar shader ABI.</summary>
public static class SimpleDdgiHotColdCacheLayout
{
    public const uint LayoutFlag = 1u << 16;
    public const int ConditionalPayloadWords = 3;

    public static int HotHeaderWords(SimpleDdgiTransportCacheFormat format) =>
        format switch
        {
            SimpleDdgiTransportCacheFormat.Compact28 => 4,
            SimpleDdgiTransportCacheFormat.Compact24 => 3,
            _ => format.WordCount()
        };

    public static int ResolveHeaderWord(
        SimpleDdgiTransportCacheFormat format,
        int rayIndex)
    {
        ValidateFormat(format);
        if (rayIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(rayIndex));
        return checked(rayIndex * HotHeaderWords(format));
    }

    public static int ResolveGenerationWord(
        SimpleDdgiTransportCacheFormat format,
        int rayIndex)
    {
        int header = ResolveHeaderWord(format, rayIndex);
        return checked(header + HotHeaderWords(format) - 1);
    }

    public static int ResolvePayloadWord(
        SimpleDdgiTransportCacheFormat format,
        int rayIndex,
        int raysPerProbe)
    {
        Validate(format, rayIndex, raysPerProbe);
        return checked(
            raysPerProbe * HotHeaderWords(format) +
            rayIndex * ConditionalPayloadWords);
    }

    public static bool RequiresPayload(int hitKind) => hitKind is not (0 or 3);

    public static void TransposeProbeFromFixedRecords(
        SimpleDdgiTransportCacheFormat format,
        ReadOnlySpan<uint> fixedRecords,
        int raysPerProbe,
        Span<uint> hotColdRecords)
    {
        Validate(format, 0, raysPerProbe);
        int stride = format.WordCount();
        int requiredWords = checked(raysPerProbe * stride);
        if (fixedRecords.Length < requiredWords)
            throw new ArgumentException("Fixed-record source is too small.", nameof(fixedRecords));
        if (hotColdRecords.Length < requiredWords)
            throw new ArgumentException("Hot/cold destination is too small.", nameof(hotColdRecords));

        for (int ray = 0; ray < raysPerProbe; ray++)
        {
            int source = checked(ray * stride);
            int header = ResolveHeaderWord(format, ray);
            int payload = ResolvePayloadWord(format, ray, raysPerProbe);
            int fixedPayload = format == SimpleDdgiTransportCacheFormat.Compact28
                ? source + 3
                : source + 2;
            hotColdRecords[header + 0] = fixedRecords[source + 0];
            hotColdRecords[header + 1] = fixedRecords[source + 1];
            if (format == SimpleDdgiTransportCacheFormat.Compact28)
                hotColdRecords[header + 2] = fixedRecords[source + 2];
            hotColdRecords[header + HotHeaderWords(format) - 1] =
                fixedRecords[source + stride - 1];
            fixedRecords.Slice(fixedPayload, ConditionalPayloadWords)
                .CopyTo(hotColdRecords.Slice(payload, ConditionalPayloadWords));
        }
    }

    public static void CopyRecordToFixedOracle(
        SimpleDdgiTransportCacheFormat format,
        ReadOnlySpan<uint> hotColdRecords,
        int rayIndex,
        int raysPerProbe,
        Span<uint> fixedRecord)
    {
        Validate(format, rayIndex, raysPerProbe);
        int stride = format.WordCount();
        if (hotColdRecords.Length < checked(raysPerProbe * stride))
            throw new ArgumentException("Hot/cold source is too small.", nameof(hotColdRecords));
        if (fixedRecord.Length < stride)
            throw new ArgumentException("Fixed-record destination is too small.", nameof(fixedRecord));

        fixedRecord[..stride].Clear();
        int header = ResolveHeaderWord(format, rayIndex);
        int payload = ResolvePayloadWord(format, rayIndex, raysPerProbe);
        int fixedPayload = format == SimpleDdgiTransportCacheFormat.Compact28 ? 3 : 2;
        fixedRecord[0] = hotColdRecords[header + 0];
        fixedRecord[1] = hotColdRecords[header + 1];
        if (format == SimpleDdgiTransportCacheFormat.Compact28)
            fixedRecord[2] = hotColdRecords[header + 2];
        fixedRecord[stride - 1] =
            hotColdRecords[header + HotHeaderWords(format) - 1];
        hotColdRecords.Slice(payload, ConditionalPayloadWords)
            .CopyTo(fixedRecord.Slice(fixedPayload, ConditionalPayloadWords));
    }

    private static void Validate(
        SimpleDdgiTransportCacheFormat format,
        int rayIndex,
        int raysPerProbe)
    {
        ValidateFormat(format);
        if (raysPerProbe <= 0 || rayIndex < 0 || rayIndex >= raysPerProbe)
            throw new ArgumentOutOfRangeException(nameof(rayIndex));
    }

    private static void ValidateFormat(SimpleDdgiTransportCacheFormat format)
    {
        if (format is not (SimpleDdgiTransportCacheFormat.Compact24 or
            SimpleDdgiTransportCacheFormat.Compact28))
            throw new ArgumentOutOfRangeException(nameof(format));
    }
}
