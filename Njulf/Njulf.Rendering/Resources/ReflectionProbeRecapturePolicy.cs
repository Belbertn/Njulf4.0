using System;

namespace Njulf.Rendering.Resources;

public readonly record struct ReflectionProbeRecaptureDecision(
    bool RequestCapture,
    bool Coalesced,
    ReflectionCaptureReason Reasons,
    ReflectionCaptureVersion Version,
    ulong RequestSerial,
    bool Deferred = false);

/// <summary>
/// Version-based recapture gate. A probe receives at most one pending request for a version; new
/// scene/lighting/DDGI revisions replace the pending candidate and OR their reasons. Stable frames
/// do not allocate or repeatedly enqueue the same work.
/// </summary>
public struct ReflectionProbeRecapturePolicy
{
    private ReflectionCaptureVersion _pendingVersion;
    private ReflectionCaptureReason _pendingReasons;
    private ulong _requestSerial;
    private ulong _lastStartedFrame;
    private ulong _earliestFrame;
    private ReflectionCaptureVersion _activeVersion;
    private bool _hasActiveVersion;
    private bool _requestIssued;

    public readonly bool HasPending => _pendingReasons != ReflectionCaptureReason.None;
    public readonly ReflectionCaptureVersion PendingVersion => _pendingVersion;
    public readonly ReflectionCaptureReason PendingReasons => _pendingReasons;
    public readonly ulong RequestSerial => _requestSerial;
    public readonly ulong LastStartedFrame => _lastStartedFrame;
    public readonly ulong EarliestFrame => _earliestFrame;
    public readonly bool HasActiveVersion => _hasActiveVersion;
    public readonly ReflectionCaptureVersion ActiveVersion => _activeVersion;

    public readonly bool CanSchedule(ulong currentFrame) =>
        _earliestFrame == 0UL || currentFrame >= _earliestFrame;

    public ReflectionProbeRecaptureDecision Observe(
        in ReflectionCaptureVersion version,
        ReflectionCaptureReason reasons,
        ulong currentFrame = 0UL,
        ulong minimumIntervalFrames = 0UL,
        bool bypassInterval = false)
    {
        if (version == default)
            throw new ArgumentException("A recapture candidate must carry a nonzero version.", nameof(version));
        if (reasons == ReflectionCaptureReason.None)
            return new ReflectionProbeRecaptureDecision(false, false, _pendingReasons, _pendingVersion, _requestSerial);

        bool sameVersion = HasPending && _pendingVersion == version;
        bool coalesced = sameVersion && (_pendingReasons & reasons) == reasons;
        if (coalesced)
        {
            bool deferred = !bypassInterval && !CanSchedule(currentFrame);
            if (!_requestIssued && !deferred)
            {
                _requestIssued = true;
                return new ReflectionProbeRecaptureDecision(
                    true,
                    true,
                    _pendingReasons,
                    _pendingVersion,
                    _requestSerial);
            }
            return new ReflectionProbeRecaptureDecision(
                false,
                true,
                _pendingReasons,
                _pendingVersion,
                _requestSerial,
                deferred);
        }

        // Stable frames can call the policy again after a request has started. The active
        // version is already being processed by the authoritative scheduler; only an explicit
        // force reason may restart it without a new owner revision.
        if (!HasPending && _hasActiveVersion && _activeVersion == version &&
            (reasons & (ReflectionCaptureReason.Manual |
                        ReflectionCaptureReason.ResourceChanged |
                        ReflectionCaptureReason.InitialLoad)) == 0)
        {
            return new ReflectionProbeRecaptureDecision(
                false,
                true,
                ReflectionCaptureReason.None,
                version,
                _requestSerial);
        }

        if (!sameVersion)
        {
            _pendingVersion = version;
            _requestSerial = _requestSerial == ulong.MaxValue ? 1UL : _requestSerial + 1UL;
        }
        _pendingReasons |= reasons;
        _requestIssued = false;
        if (!bypassInterval && minimumIntervalFrames > 0UL &&
            currentFrame < AddSaturating(_lastStartedFrame, minimumIntervalFrames))
        {
            _earliestFrame = AddSaturating(_lastStartedFrame, minimumIntervalFrames);
            return new ReflectionProbeRecaptureDecision(
                false,
                sameVersion,
                _pendingReasons,
                _pendingVersion,
                _requestSerial,
                Deferred: true);
        }

        _earliestFrame = 0UL;
        _requestIssued = true;
        return new ReflectionProbeRecaptureDecision(
            true,
            sameVersion,
            _pendingReasons,
            _pendingVersion,
            _requestSerial);
    }

    public void MarkStarted(in ReflectionCaptureVersion version) => MarkStarted(version, 0UL);

    public void MarkStarted(in ReflectionCaptureVersion version, ulong currentFrame)
    {
        if (HasPending && _pendingVersion == version)
        {
            _pendingReasons = ReflectionCaptureReason.None;
            _pendingVersion = default;
            _earliestFrame = 0UL;
        }
        _activeVersion = version;
        _hasActiveVersion = true;
        _requestIssued = false;
        _lastStartedFrame = currentFrame;
    }

    /// <summary>
    /// Releases an interval-deferred request without rescanning all probes. The caller submits the
    /// returned version/reason pair to the capture scheduler exactly once.
    /// </summary>
    public bool TryReleaseDeferred(ulong currentFrame, out ReflectionProbeRecaptureDecision decision)
    {
        if (!HasPending || _requestIssued || !CanSchedule(currentFrame))
        {
            decision = default;
            return false;
        }

        _requestIssued = true;
        _earliestFrame = 0UL;
        decision = new ReflectionProbeRecaptureDecision(
            true,
            true,
            _pendingReasons,
            _pendingVersion,
            _requestSerial);
        return true;
    }

    public void Reset()
    {
        _pendingVersion = default;
        _pendingReasons = ReflectionCaptureReason.None;
        _requestSerial = 0UL;
        _lastStartedFrame = 0UL;
        _earliestFrame = 0UL;
        _activeVersion = default;
        _hasActiveVersion = false;
        _requestIssued = false;
    }

    private static ulong AddSaturating(ulong value, ulong increment) =>
        increment > ulong.MaxValue - value ? ulong.MaxValue : value + increment;
}
