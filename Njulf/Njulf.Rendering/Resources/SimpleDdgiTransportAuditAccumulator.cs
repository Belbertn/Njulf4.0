using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// A bounded CPU-side commit reducer for chunked GPU transport audits.  It
/// retains only scalar maxima/counters, never an atlas snapshot or per-probe
/// field.  Chunks are accepted in order and are tied to one immutable audit
/// generation, so a delayed readback cannot complete a newer audit.
/// </summary>
public sealed class SimpleDdgiTransportAuditAccumulator
{
    private readonly uint _auditEpoch;
    private readonly SimpleDdgiTransportGenerations _generations;
    private readonly uint _expectedParticipantCount;
    private readonly uint _expectedTexelCount;
    private readonly float _configuredContractionBound;
    private readonly float _relativeTolerance;
    private readonly float _canonicalQuantizationFloor;
    private readonly ulong _firstFrameSerial;
    private readonly uint _expectedChunkCount;

    private uint _nextChunkIndex;
    private uint _auditedParticipantCount;
    private uint _excludedInactiveCount;
    private uint _excludedNotVisibleCount;
    private uint _excludedStaleSourceCount;
    private uint _excludedInvalidCacheCount;
    private uint _nonFiniteCount;
    private uint _counterOverflowCount;
    private uint _auditedTexelCount;
    private float _fixedPointDefect;
    private float _fieldMagnitude;
    private float _observedContractionBound;
    private uint _channelEvidenceVersion = uint.MaxValue;
    private SimpleDdgiTransportRgbBounds _fixedPointDefectChannels;
    private SimpleDdgiTransportRgbBounds _fieldMagnitudeChannels;
    private SimpleDdgiTransportRgbBounds _observedContractionChannels;
    private ulong _auditMicroseconds;
    private ulong _finalFrameSerial;
    private bool _overflowed;

    public SimpleDdgiTransportAuditAccumulator(
        uint auditEpoch,
        SimpleDdgiTransportGenerations generations,
        uint expectedParticipantCount,
        uint expectedTexelCount,
        float configuredContractionBound,
        float relativeTolerance,
        float canonicalQuantizationFloor,
        ulong firstFrameSerial,
        uint expectedChunkCount)
    {
        if (expectedChunkCount == 0u)
            throw new ArgumentOutOfRangeException(nameof(expectedChunkCount));

        _auditEpoch = auditEpoch;
        _generations = generations;
        _expectedParticipantCount = expectedParticipantCount;
        _expectedTexelCount = expectedTexelCount;
        _configuredContractionBound = configuredContractionBound;
        _relativeTolerance = relativeTolerance;
        _canonicalQuantizationFloor = canonicalQuantizationFloor;
        _firstFrameSerial = firstFrameSerial;
        _expectedChunkCount = expectedChunkCount;
    }

    public uint NextChunkIndex => _nextChunkIndex;
    public uint AcceptedChunkCount => _nextChunkIndex;
    public bool IsOverflowed => _overflowed;

    /// <summary>
    /// Adds exactly one contiguous chunk.  A false result means the chunk is
    /// stale, duplicated, out of order, or structurally incompatible; callers
    /// should discard the accumulator and restart the audit epoch.
    /// </summary>
    public bool TryAddChunk(SimpleDdgiTransportAuditChunk chunk)
    {
        if (!TryResolveChunkChannelEvidence(
                chunk,
                out uint channelEvidenceVersion,
                out SimpleDdgiTransportRgbBounds defectChannels,
                out SimpleDdgiTransportRgbBounds fieldChannels,
                out SimpleDdgiTransportRgbBounds contractionChannels))
        {
            return false;
        }

        if (_overflowed ||
            chunk.AuditEpoch != _auditEpoch ||
            chunk.Generations != _generations ||
            chunk.ChunkIndex != _nextChunkIndex ||
            chunk.ExpectedChunkCount != _expectedChunkCount ||
            chunk.ExpectedParticipantCount != _expectedParticipantCount ||
            chunk.ExpectedTexelCount != _expectedTexelCount ||
            !IsFiniteNonNegative(chunk.FixedPointDefect) ||
            !IsFiniteNonNegative(chunk.FieldMagnitude) ||
            !IsFiniteNonNegative(chunk.ObservedContractionBound) ||
            chunk.ObservedContractionBound > _configuredContractionBound)
        {
            return false;
        }
        if (_channelEvidenceVersion != uint.MaxValue &&
            _channelEvidenceVersion != channelEvidenceVersion)
        {
            return false;
        }

        if (chunk.AuditMilliseconds > ulong.MaxValue / 1000UL ||
            !TryAdd(ref _auditedParticipantCount, chunk.AuditedParticipantCount) ||
            !TryAdd(ref _excludedInactiveCount, chunk.ExcludedInactiveCount) ||
            !TryAdd(ref _excludedNotVisibleCount, chunk.ExcludedNotVisibleCount) ||
            !TryAdd(ref _excludedStaleSourceCount, chunk.ExcludedStaleSourceCount) ||
            !TryAdd(ref _excludedInvalidCacheCount, chunk.ExcludedInvalidCacheCount) ||
            !TryAdd(ref _nonFiniteCount, chunk.NonFiniteCount) ||
            !TryAdd(ref _auditedTexelCount, chunk.AuditedTexelCount) ||
            !TryAdd(ref _auditMicroseconds, chunk.AuditMilliseconds * 1000UL))
        {
            _overflowed = true;
            _counterOverflowCount = 1u;
            return false;
        }

        _fixedPointDefect = MathF.Max(_fixedPointDefect, chunk.FixedPointDefect);
        _fieldMagnitude = MathF.Max(_fieldMagnitude, chunk.FieldMagnitude);
        _observedContractionBound = MathF.Max(
            _observedContractionBound,
            chunk.ObservedContractionBound);
        _channelEvidenceVersion = channelEvidenceVersion;
        _fixedPointDefectChannels = SimpleDdgiTransportRgbBounds.Max(
            _fixedPointDefectChannels,
            defectChannels);
        _fieldMagnitudeChannels = SimpleDdgiTransportRgbBounds.Max(
            _fieldMagnitudeChannels,
            fieldChannels);
        _observedContractionChannels = SimpleDdgiTransportRgbBounds.Max(
            _observedContractionChannels,
            contractionChannels);
        _finalFrameSerial = Math.Max(_finalFrameSerial, chunk.FinalFrameSerial);
        _nextChunkIndex++;
        return true;
    }

    /// <summary>
    /// Produces the only summary shape accepted by the solve controller.  The
    /// tail is recomputed from the accumulated D and q instead of trusting a
    /// shader completion bit or a precomputed tail field.
    /// </summary>
    public bool TryFinalize(out SimpleDdgiTransportTailSummary summary)
    {
        uint channelEvidenceVersion = _channelEvidenceVersion == uint.MaxValue
            ? 0u
            : _channelEvidenceVersion;
        SimpleDdgiTransportRgbBounds certifiedContractionChannels = new(
            MathF.Min(_configuredContractionBound,
                _observedContractionChannels.Red),
            MathF.Min(_configuredContractionBound,
                _observedContractionChannels.Green),
            MathF.Min(_configuredContractionBound,
                _observedContractionChannels.Blue));
        SimpleDdgiTransportRgbBounds tailChannels = new(
            _fixedPointDefectChannels.Red / MathF.Max(
                1.0f - certifiedContractionChannels.Red, 1e-6f),
            _fixedPointDefectChannels.Green / MathF.Max(
                1.0f - certifiedContractionChannels.Green, 1e-6f),
            _fixedPointDefectChannels.Blue / MathF.Max(
                1.0f - certifiedContractionChannels.Blue, 1e-6f));
        SimpleDdgiTransportRgbBounds relativeTailChannels = new(
            tailChannels.Red / MathF.Max(
                _fieldMagnitudeChannels.Red,
                SimpleDdgiTransportTailEstimator.AbsoluteTolerance),
            tailChannels.Green / MathF.Max(
                _fieldMagnitudeChannels.Green,
                SimpleDdgiTransportTailEstimator.AbsoluteTolerance),
            tailChannels.Blue / MathF.Max(
                _fieldMagnitudeChannels.Blue,
                SimpleDdgiTransportTailEstimator.AbsoluteTolerance));
        SimpleDdgiTransportRgbBounds quantizationFloorChannels =
            SimpleDdgiTransportRgbBounds.Broadcast(_canonicalQuantizationFloor);
        summary = SimpleDdgiTransportTailSummary.Empty with
        {
            AuditEpoch = _auditEpoch,
            Generations = _generations,
            ExpectedParticipantCount = _expectedParticipantCount,
            ExpectedTexelCount = _expectedTexelCount,
            AuditedParticipantCount = _auditedParticipantCount,
            ExcludedInactiveCount = _excludedInactiveCount,
            ExcludedNotVisibleCount = _excludedNotVisibleCount,
            ExcludedStaleSourceCount = _excludedStaleSourceCount,
            ExcludedInvalidCacheCount = _excludedInvalidCacheCount,
            NonFiniteCount = _nonFiniteCount,
            CounterOverflowCount = _counterOverflowCount,
            AuditedTexelCount = _auditedTexelCount,
            FixedPointDefect = _fixedPointDefect,
            FieldMagnitude = _fieldMagnitude,
            ConfiguredContractionBound = _configuredContractionBound,
            ObservedContractionBound = _observedContractionBound,
            CertifiedContractionBound = MathF.Min(
                _configuredContractionBound,
                _observedContractionBound),
            AbsoluteTailBound = channelEvidenceVersion ==
                    SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion
                ? tailChannels.Maximum
                : _fixedPointDefect / MathF.Max(
                    1.0f - MathF.Min(
                        _configuredContractionBound,
                        _observedContractionBound),
                    1e-6f),
            RelativeTailBound = channelEvidenceVersion ==
                    SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion
                ? relativeTailChannels.Maximum
                : (_fixedPointDefect / MathF.Max(
                    1.0f - MathF.Min(
                        _configuredContractionBound,
                        _observedContractionBound),
                    1e-6f)) / MathF.Max(
                        _fieldMagnitude,
                        SimpleDdgiTransportTailEstimator.AbsoluteTolerance),
            Tolerance = MathF.Max(
                SimpleDdgiTransportTailEstimator.AbsoluteTolerance,
                _relativeTolerance * _fieldMagnitude),
            CanonicalQuantizationFloor = _canonicalQuantizationFloor,
            ChannelEvidenceVersion = channelEvidenceVersion,
            FixedPointDefectChannels = _fixedPointDefectChannels,
            FieldMagnitudeChannels = _fieldMagnitudeChannels,
            ObservedContractionChannels = _observedContractionChannels,
            CertifiedContractionChannels = certifiedContractionChannels,
            AbsoluteTailBoundChannels = tailChannels,
            RelativeTailBoundChannels = relativeTailChannels,
            CanonicalQuantizationFloorChannels = quantizationFloorChannels,
            AuditMicroseconds = _auditMicroseconds,
            FirstFrameSerial = _firstFrameSerial,
            FinalFrameSerial = _finalFrameSerial,
            ChunkCount = _nextChunkIndex,
            IsComplete = _nextChunkIndex == _expectedChunkCount
        };

        bool validScalarConfiguration =
            float.IsFinite(_configuredContractionBound) &&
            _configuredContractionBound >= 0.0f &&
            _configuredContractionBound <=
                SimpleDdgiTransportTailEstimator.MaximumCertifiedContraction &&
            float.IsFinite(_relativeTolerance) &&
            _relativeTolerance >= 0.0f &&
            float.IsFinite(_canonicalQuantizationFloor) &&
            _canonicalQuantizationFloor >= 0.0f;
        if (!validScalarConfiguration)
        {
            summary = summary with
            {
                Reason = SimpleDdgiTransportCertificationReason.InvalidContractionBound
            };
            return false;
        }

        if (_overflowed || !summary.IsComplete)
        {
            summary = summary with
            {
                Reason = _overflowed
                    ? SimpleDdgiTransportCertificationReason.CounterOverflow
                    : SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete
            };
            return false;
        }

        float certifiedQ = MathF.Min(
            _configuredContractionBound,
            _observedContractionBound);
        if (!float.IsFinite(certifiedQ) ||
            certifiedQ < 0.0f ||
            certifiedQ > _configuredContractionBound ||
            certifiedQ >= 1.0f)
        {
            summary = summary with
            {
                Reason = SimpleDdgiTransportCertificationReason.InvalidContractionBound
            };
            return false;
        }

        float tail = channelEvidenceVersion ==
                SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion
            ? tailChannels.Maximum
            : _fixedPointDefect / MathF.Max(1.0f - certifiedQ, 1e-6f);
        float tolerance = summary.Tolerance;
        bool coverage = summary.HasExactParticipantCoverage &&
            summary.HasExactTexelCoverage;
        bool finite = summary.HasFiniteEvidence &&
            float.IsFinite(tail) &&
            float.IsFinite(tolerance);
        // The field may still be exactly fixed-point even when the local
        // half-ULP interval is non-zero. Only an interval larger than the
        // authored tolerance makes the requested certificate unattainable.
        bool quantizationLimited = _canonicalQuantizationFloor > tolerance;

        summary = summary with
        {
            CertifiedContractionBound = certifiedQ,
            AbsoluteTailBound = tail,
            RelativeTailBound = channelEvidenceVersion ==
                    SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion
                ? relativeTailChannels.Maximum
                : tail / MathF.Max(
                    _fieldMagnitude,
                    SimpleDdgiTransportTailEstimator.AbsoluteTolerance),
            Tolerance = tolerance,
            Reason = !coverage
                ? SimpleDdgiTransportCertificationReason.ParticipantCoverageIncomplete
                : !finite
                    ? SimpleDdgiTransportCertificationReason.NonFiniteEvidence
                    : quantizationLimited
                        ? SimpleDdgiTransportCertificationReason.QuantizationLimited
                        : tail <= tolerance
                            ? SimpleDdgiTransportCertificationReason.Certified
                            : SimpleDdgiTransportCertificationReason.TailAboveTolerance
        };

        return summary.IsCertified;
    }

    private static bool IsFiniteNonNegative(float value) =>
        float.IsFinite(value) && value >= 0.0f;

    private bool TryResolveChunkChannelEvidence(
        SimpleDdgiTransportAuditChunk chunk,
        out uint version,
        out SimpleDdgiTransportRgbBounds defect,
        out SimpleDdgiTransportRgbBounds fieldMagnitude,
        out SimpleDdgiTransportRgbBounds observedContraction)
    {
        version = chunk.ChannelEvidenceVersion;
        if (version == 0u)
        {
            // A scalar infinity-norm proof remains conservative. Broadcast it
            // so legacy test/CPU producers can share the channel math without
            // pretending that they supplied tighter RGB evidence.
            defect = SimpleDdgiTransportRgbBounds.Broadcast(
                chunk.FixedPointDefect);
            fieldMagnitude = SimpleDdgiTransportRgbBounds.Broadcast(
                chunk.FieldMagnitude);
            observedContraction = SimpleDdgiTransportRgbBounds.Broadcast(
                chunk.ObservedContractionBound);
            return true;
        }

        defect = chunk.FixedPointDefectChannels;
        fieldMagnitude = chunk.FieldMagnitudeChannels;
        observedContraction = chunk.ObservedContractionChannels;
        return version == SimpleDdgiTransportTailSummary.PerChannelEvidenceVersion &&
            defect.IsFiniteNonNegative &&
            fieldMagnitude.IsFiniteNonNegative &&
            observedContraction.IsAtMost(_configuredContractionBound) &&
            defect.Maximum == chunk.FixedPointDefect &&
            fieldMagnitude.Maximum == chunk.FieldMagnitude &&
            observedContraction.Maximum == chunk.ObservedContractionBound;
    }

    private static bool TryAdd(ref uint target, uint value)
    {
        if (uint.MaxValue - target < value)
            return false;
        target += value;
        return true;
    }

    private static bool TryAdd(ref ulong target, ulong value)
    {
        if (ulong.MaxValue - target < value)
            return false;
        target += value;
        return true;
    }
}

/// <summary>One immutable chunk reduction returned by the GPU audit readback.</summary>
public readonly record struct SimpleDdgiTransportAuditChunk
{
    public uint AuditEpoch { get; init; }
    public SimpleDdgiTransportGenerations Generations { get; init; }
    public uint ChunkIndex { get; init; }
    public uint ExpectedChunkCount { get; init; }
    public uint ExpectedParticipantCount { get; init; }
    public uint ExpectedTexelCount { get; init; }
    public uint AuditedParticipantCount { get; init; }
    public uint ExcludedInactiveCount { get; init; }
    /// <summary>
    /// Virtual probes outside the frozen resident/published participant set.
    /// These do not count against exact coverage of that set.
    /// </summary>
    public uint ExcludedNotVisibleCount { get; init; }
    public uint ExcludedStaleSourceCount { get; init; }
    public uint ExcludedInvalidCacheCount { get; init; }
    public uint NonFiniteCount { get; init; }
    public uint AuditedTexelCount { get; init; }
    public float FixedPointDefect { get; init; }
    public float FieldMagnitude { get; init; }
    public float ObservedContractionBound { get; init; }
    public uint ChannelEvidenceVersion { get; init; }
    public SimpleDdgiTransportRgbBounds FixedPointDefectChannels { get; init; }
    public SimpleDdgiTransportRgbBounds FieldMagnitudeChannels { get; init; }
    public SimpleDdgiTransportRgbBounds ObservedContractionChannels { get; init; }
    public ulong AuditMilliseconds { get; init; }
    public ulong FinalFrameSerial { get; init; }
}

public readonly record struct SimpleDdgiTransportAuditChunkDispatch(
    uint AuditEpoch,
    uint ChunkIndex,
    uint ChunkCount,
    int ProbeOffset,
    int ProbeCount,
    int ExpectedParticipantCount,
    int ExpectedTexelCount,
    bool IsFinal);
