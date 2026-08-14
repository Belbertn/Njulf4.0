using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Turns B1's frame-local maximum-contribution witness into a stable
/// refinement-placement input. The maximum can legitimately alternate among
/// nearby receivers as samples converge, so it is latched until an observable
/// view or scene transition makes a new placement useful.
/// </summary>
internal sealed class SimpleDdgiRefinementFocusTracker
{
    // Ten degrees. Smaller view jitter must not churn brick topology.
    private const float CameraForwardResetDot = 0.98480775f;
    private const float MinimumTranslationResetDistance = 0.5f;

    private bool _initialized;
    private bool _hasMeasuredFocus;
    private Vector3 _focus;
    private Vector3 _anchorCameraPosition;
    private Vector3 _anchorCameraForward;
    private float _translationResetDistance;
    private ulong _cameraCutSerial;
    private ulong _sceneContentRevision;

    internal bool HasMeasuredFocus => _initialized && _hasMeasuredFocus;

    internal Vector3 Resolve(
        Vector3 fallbackFocus,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        float translationResetDistance,
        ulong cameraCutSerial,
        ulong sceneContentRevision,
        Vector3? measuredBaseFocus)
    {
        Vector3 normalizedForward = NormalizeForward(cameraForward);
        float resetDistance = float.IsFinite(translationResetDistance)
            ? Math.Max(MinimumTranslationResetDistance, translationResetDistance)
            : MinimumTranslationResetDistance;
        bool reset = !_initialized ||
            _cameraCutSerial != cameraCutSerial ||
            _sceneContentRevision != sceneContentRevision ||
            _translationResetDistance != resetDistance ||
            Vector3.DistanceSquared(cameraPosition, _anchorCameraPosition) >=
                resetDistance * resetDistance ||
            Vector3.Dot(normalizedForward, _anchorCameraForward) <
                CameraForwardResetDot;

        if (reset)
        {
            _initialized = true;
            _hasMeasuredFocus = false;
            _focus = IsFinite(fallbackFocus) ? fallbackFocus : cameraPosition;
            _anchorCameraPosition = cameraPosition;
            _anchorCameraForward = normalizedForward;
            _translationResetDistance = resetDistance;
            _cameraCutSerial = cameraCutSerial;
            _sceneContentRevision = sceneContentRevision;
        }

        if (!_hasMeasuredFocus &&
            measuredBaseFocus is { } measured &&
            IsFinite(measured))
        {
            _focus = measured;
            _hasMeasuredFocus = true;
        }

        return _focus;
    }

    internal void Reset()
    {
        _initialized = false;
        _hasMeasuredFocus = false;
    }

    private static Vector3 NormalizeForward(Vector3 forward)
    {
        if (!IsFinite(forward) || forward.LengthSquared() <= 1.0e-12f)
            return Vector3.Forward;
        return forward.Normalized();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
