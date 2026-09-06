using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public enum SimpleDdgiSampledAtlasRangeReason : uint
{
    FullCanonical = 0,
    AuthoredReceiverHero = 1,
    AuthoredNavigableInterior = 2,
    AuthoredDynamicInfluence = 3,
    NearRing = 4,
    MidRing = 5,
    LegacyVolume = 6
}

/// <summary>One complete physical atlas range considered for compact mirroring.</summary>
public readonly record struct SimpleDdgiSampledAtlasRangeRequest(
    int VolumeIndex,
    string Identity,
    int SourceOrdinal,
    int CanonicalFirstProbe,
    int ProbeCount,
    bool IsAuthored,
    SimpleDdgiVolumePurpose Purpose,
    int RingIndex,
    int OwnershipOrder);

/// <summary>One admitted complete canonical-to-compact range.</summary>
public readonly record struct SimpleDdgiSampledAtlasRange(
    int VolumeIndex,
    string Identity,
    int SourceOrdinal,
    int CanonicalFirstProbe,
    int ProbeCount,
    int CompactFirstLayer,
    int Priority,
    SimpleDdgiSampledAtlasRangeReason Reason)
{
    public int CanonicalEndProbe => checked(CanonicalFirstProbe + ProbeCount);
    public int CompactEndLayer => checked(CompactFirstLayer + ProbeCount);
}

/// <summary>Pure whole-volume compact mirror plan.</summary>
public sealed record SimpleDdgiSampledAtlasLayout(
    SimpleDdgiSampledAtlasCoverageMode CoverageMode,
    IReadOnlyList<SimpleDdgiSampledAtlasRange> Ranges,
    int RequestedProbeCount,
    int EligibleProbeCount,
    int AdmittedProbeCount,
    int ProvisionedProbeCount,
    ulong IrradianceImageBytes,
    ulong VisibilityImageBytes,
    ulong TotalImageBytes,
    IReadOnlyList<string> ExcludedIdentities,
    string FallbackReason,
    ulong Fingerprint)
{
    public static SimpleDdgiSampledAtlasLayout Disabled(
        SimpleDdgiSampledAtlasCoverageMode mode =
            SimpleDdgiSampledAtlasCoverageMode.Disabled,
        string reason = "disabled") =>
        new(
            mode.Sanitize(),
            Array.Empty<SimpleDdgiSampledAtlasRange>(),
            0,
            0,
            0,
            0,
            0UL,
            0UL,
            0UL,
            Array.Empty<string>(),
            reason,
            SimpleDdgiSampledAtlasLayoutCompiler.AddCoverageModeToFingerprint(
                mode.Sanitize()));

    public SimpleDdgiSampledAtlasRange? FindVolume(int volumeIndex)
    {
        foreach (SimpleDdgiSampledAtlasRange range in Ranges)
        {
            if (range.VolumeIndex == volumeIndex)
                return range;
        }

        return null;
    }
}

public static class SimpleDdgiSampledAtlasLayoutCompiler
{
    public const int CapacityQuantum = 256;
    public const int BorderTexels = 1;
    public const int IrradianceImageTexels =
        SimpleDdgiVolumeManager.IrradianceTexelsPerProbe + 2 * BorderTexels;
    public const int VisibilityImageTexels =
        SimpleDdgiVolumeManager.VisibilityTexelsPerProbe + 2 * BorderTexels;
    public const ulong IrradianceBytesPerProbe =
        (ulong)IrradianceImageTexels * IrradianceImageTexels * 8UL;
    public const ulong VisibilityBytesPerProbe =
        (ulong)VisibilityImageTexels * VisibilityImageTexels * 4UL;
    internal const ulong InitialFingerprint = 14695981039346656037UL;
    private const ulong FingerprintPrime = 1099511628211UL;

    public static SimpleDdgiSampledAtlasLayout Compile(
        IReadOnlyList<SimpleDdgiSampledAtlasRangeRequest> requests,
        SimpleDdgiSampledAtlasCoverageMode coverageMode,
        ulong availableBytes,
        bool requested = true)
    {
        if (requests == null)
            throw new ArgumentNullException(nameof(requests));
        coverageMode = coverageMode.Sanitize();
        if (!requested || coverageMode == SimpleDdgiSampledAtlasCoverageMode.Disabled)
            return SimpleDdgiSampledAtlasLayout.Disabled(coverageMode, requested ? "coverage-disabled" : "feature-disabled");

        int requestedProbeCount = 0;
        var candidates = new List<(SimpleDdgiSampledAtlasRangeRequest Request, int Priority, SimpleDdgiSampledAtlasRangeReason Reason)>();
        var excluded = new List<string>();
        var seenVolumes = new HashSet<int>();
        var seenSourceOrdinals = new HashSet<int>();
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var canonicalRanges = new List<(int First, int End, int VolumeIndex)>(
            requests.Count);
        foreach (SimpleDdgiSampledAtlasRangeRequest request in requests)
        {
            if (request.VolumeIndex < 0 ||
                request.CanonicalFirstProbe < 0 || request.ProbeCount < 0)
                throw new ArgumentOutOfRangeException(nameof(requests), "Mirror ranges must be non-negative.");
            if (!seenVolumes.Add(request.VolumeIndex))
            {
                throw new ArgumentException(
                    $"Duplicate sampled-atlas volume {request.VolumeIndex}.",
                    nameof(requests));
            }
            if (request.SourceOrdinal < 0 ||
                !seenSourceOrdinals.Add(request.SourceOrdinal))
            {
                throw new ArgumentException(
                    $"Sampled-atlas source ordinal {request.SourceOrdinal} is invalid or duplicated.",
                    nameof(requests));
            }
            string identity = request.Identity ?? string.Empty;
            if (string.IsNullOrWhiteSpace(identity) ||
                !seenIdentities.Add(identity))
            {
                throw new ArgumentException(
                    $"Sampled-atlas identity '{identity}' is empty or duplicated.",
                    nameof(requests));
            }
            int canonicalEnd = checked(
                request.CanonicalFirstProbe + request.ProbeCount);
            if (request.ProbeCount > 0)
            {
                foreach ((int first, int end, int volumeIndex) in canonicalRanges)
                {
                    if (request.CanonicalFirstProbe < end && first < canonicalEnd)
                    {
                        throw new ArgumentException(
                            $"Sampled-atlas canonical ranges for volumes {volumeIndex} and {request.VolumeIndex} overlap.",
                            nameof(requests));
                    }
                }
                canonicalRanges.Add((
                    request.CanonicalFirstProbe,
                    canonicalEnd,
                    request.VolumeIndex));
            }
            requestedProbeCount = checked(requestedProbeCount + request.ProbeCount);
            if (request.ProbeCount == 0)
                continue;
            if (TryClassify(request, coverageMode, out int priority, out SimpleDdgiSampledAtlasRangeReason reason))
                candidates.Add((request, priority, reason));
            else
                excluded.Add(identity);
        }

        candidates.Sort(static (left, right) =>
        {
            int priority = left.Priority.CompareTo(right.Priority);
            if (priority != 0)
                return priority;
            int ownership = left.Request.OwnershipOrder.CompareTo(right.Request.OwnershipOrder);
            return ownership != 0
                ? ownership
                : left.Request.VolumeIndex.CompareTo(right.Request.VolumeIndex);
        });

        int eligibleProbeCount = candidates.Sum(static candidate => candidate.Request.ProbeCount);
        if (coverageMode == SimpleDdgiSampledAtlasCoverageMode.FullCanonical)
        {
            int provisioned = ResolveProvisionedProbeCount(eligibleProbeCount);
            ulong required = ResolveTotalBytes(provisioned);
            if (required > availableBytes)
            {
                return new SimpleDdgiSampledAtlasLayout(
                    coverageMode,
                    Array.Empty<SimpleDdgiSampledAtlasRange>(),
                    requestedProbeCount,
                    eligibleProbeCount,
                    0,
                    0,
                    0UL,
                    0UL,
                    0UL,
                    requests.Select(static request => request.Identity ?? string.Empty).ToArray(),
                    "full-canonical-budget-exhausted",
                    AddCoverageModeToFingerprint(coverageMode));
            }
        }

        var ranges = new List<SimpleDdgiSampledAtlasRange>(candidates.Count);
        int compactCursor = 0;
        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            (SimpleDdgiSampledAtlasRangeRequest request, int priority,
                SimpleDdgiSampledAtlasRangeReason reason) = candidates[candidateIndex];
            int candidateCount = checked(compactCursor + request.ProbeCount);
            int provisioned = ResolveProvisionedProbeCount(candidateCount);
            if (ResolveTotalBytes(provisioned) > availableBytes)
            {
                // Coverage is a deterministic priority prefix. Letting a
                // lower-priority small range leapfrog an excluded owner would
                // make a budget change silently rewrite the policy.
                for (int excludedIndex = candidateIndex;
                     excludedIndex < candidates.Count;
                     excludedIndex++)
                {
                    excluded.Add(
                        candidates[excludedIndex].Request.Identity ?? string.Empty);
                }
                break;
            }

            ranges.Add(new SimpleDdgiSampledAtlasRange(
                request.VolumeIndex,
                request.Identity ?? string.Empty,
                request.SourceOrdinal,
                request.CanonicalFirstProbe,
                request.ProbeCount,
                compactCursor,
                priority,
                reason));
            compactCursor = candidateCount;
        }

        int provisionedProbeCount = ResolveProvisionedProbeCount(compactCursor);
        ulong irradianceBytes = checked((ulong)provisionedProbeCount * IrradianceBytesPerProbe);
        ulong visibilityBytes = checked((ulong)provisionedProbeCount * VisibilityBytesPerProbe);
        ulong totalBytes = checked(irradianceBytes + visibilityBytes);
        ulong fingerprint = AddCoverageModeToFingerprint(coverageMode);
        foreach (SimpleDdgiSampledAtlasRange range in ranges)
        {
            fingerprint = Add(fingerprint, checked((uint)range.VolumeIndex));
            fingerprint = Add(fingerprint, checked((uint)range.SourceOrdinal));
            fingerprint = AddString(fingerprint, range.Identity);
            fingerprint = Add(fingerprint, checked((uint)range.CanonicalFirstProbe));
            fingerprint = Add(fingerprint, checked((uint)range.ProbeCount));
            fingerprint = Add(fingerprint, checked((uint)range.CompactFirstLayer));
            fingerprint = Add(fingerprint, (uint)range.Reason);
        }

        string fallback = ranges.Count == 0
            ? "no-complete-range-admitted"
            : excluded.Count > 0
                ? "partial-budget-or-policy-fallback"
                : string.Empty;
        return new SimpleDdgiSampledAtlasLayout(
            coverageMode,
            ranges.AsReadOnly(),
            requestedProbeCount,
            eligibleProbeCount,
            compactCursor,
            provisionedProbeCount,
            irradianceBytes,
            visibilityBytes,
            totalBytes,
            excluded.AsReadOnly(),
            fallback,
            fingerprint);
    }

    public static int ResolveProvisionedProbeCount(int admittedProbeCount)
    {
        if (admittedProbeCount <= 0)
            return 0;
        return checked(
            ((admittedProbeCount + CapacityQuantum - 1) / CapacityQuantum) *
            CapacityQuantum);
    }

    public static ulong ResolveTotalBytes(int provisionedProbeCount) =>
        checked((ulong)Math.Max(0, provisionedProbeCount) *
            (IrradianceBytesPerProbe + VisibilityBytesPerProbe));

    private static bool TryClassify(
        in SimpleDdgiSampledAtlasRangeRequest request,
        SimpleDdgiSampledAtlasCoverageMode mode,
        out int priority,
        out SimpleDdgiSampledAtlasRangeReason reason)
    {
        if (mode == SimpleDdgiSampledAtlasCoverageMode.FullCanonical)
        {
            priority = 0;
            reason = SimpleDdgiSampledAtlasRangeReason.FullCanonical;
            return true;
        }

        if (request.IsAuthored)
        {
            switch (request.Purpose)
            {
                case SimpleDdgiVolumePurpose.ReceiverHero:
                    priority = 0;
                    reason = SimpleDdgiSampledAtlasRangeReason.AuthoredReceiverHero;
                    return true;
                case SimpleDdgiVolumePurpose.NavigableInterior:
                    priority = 0;
                    reason = SimpleDdgiSampledAtlasRangeReason.AuthoredNavigableInterior;
                    return true;
                case SimpleDdgiVolumePurpose.DynamicInfluence:
                    priority = 0;
                    reason = SimpleDdgiSampledAtlasRangeReason.AuthoredDynamicInfluence;
                    return true;
                default:
                    priority = int.MaxValue;
                    reason = default;
                    return false;
            }
        }

        if (request.RingIndex == 0)
        {
            priority = 2;
            reason = SimpleDdgiSampledAtlasRangeReason.NearRing;
            return true;
        }
        if (request.RingIndex == 1)
        {
            priority = 3;
            reason = SimpleDdgiSampledAtlasRangeReason.MidRing;
            return true;
        }
        if (request.RingIndex < 0)
        {
            priority = 1;
            reason = SimpleDdgiSampledAtlasRangeReason.LegacyVolume;
            return true;
        }

        priority = int.MaxValue;
        reason = default;
        return false;
    }

    private static ulong Add(ulong hash, ulong value) =>
        unchecked((hash ^ value) * FingerprintPrime);

    internal static ulong AddCoverageModeToFingerprint(
        SimpleDdgiSampledAtlasCoverageMode mode) =>
        Add(Add(Add(InitialFingerprint, (uint)mode.Sanitize()),
            IrradianceImageTexels), VisibilityImageTexels);

    private static ulong AddString(ulong hash, string value)
    {
        hash = Add(hash, checked((uint)value.Length));
        foreach (char character in value)
            hash = Add(hash, character);
        return hash;
    }
}
