using System;

namespace Njulf.Rendering.Data
{
    internal readonly record struct DynamicResolutionScaleDecision(
        float RequestedScale,
        float CommittedScale,
        bool CommittedScaleChanged,
        string CommitReason);

    internal sealed class DynamicResolutionScaleController
    {
        private const float ScaleBucketSize = 0.02f;
        private const float SlowFrameMultiplier = 1.08f;
        private const float FastFrameMultiplier = 0.85f;
        private const int ConfirmationFrameCount = 3;
        private const int CooldownFrameCount = 15;

        private bool _hasState;
        private bool _lastEnabled;
        private float _lastConfiguredScale;
        private float _lastMinimumScale;
        private float _lastMaximumScale;
        private float _requestedScale = 1.0f;
        private float _committedScale = 1.0f;
        private float _pendingCommittedScale;
        private int _pendingDirection;
        private int _pendingFrameCount;
        private int _framesSinceCommit = CooldownFrameCount;

        public float RequestedScale => _requestedScale;
        public float CommittedScale => _committedScale;

        public DynamicResolutionScaleDecision Resolve(RenderSettings settings, long frameMicroseconds)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            bool enabled = settings.DynamicResolution.Enabled;
            float configuredScale = settings.EffectiveResolutionScale;
            float minimumScale = enabled ? settings.DynamicResolution.MinimumScale : 0.5f;
            float maximumScale = enabled ? settings.DynamicResolution.MaximumScale : 1.0f;
            float clampedConfiguredScale = Math.Clamp(configuredScale, minimumScale, maximumScale);
            bool scaleConfigChanged = !_hasState ||
                enabled != _lastEnabled ||
                !NearlyEqual(configuredScale, _lastConfiguredScale) ||
                !NearlyEqual(minimumScale, _lastMinimumScale) ||
                !NearlyEqual(maximumScale, _lastMaximumScale);

            _hasState = true;
            _lastEnabled = enabled;
            _lastConfiguredScale = configuredScale;
            _lastMinimumScale = minimumScale;
            _lastMaximumScale = maximumScale;

            if (!enabled || scaleConfigChanged)
            {
                _requestedScale = clampedConfiguredScale;
                float nextCommittedScale = QuantizeScale(clampedConfiguredScale, minimumScale, maximumScale);
                return CommitImmediate(nextCommittedScale, scaleConfigChanged ? "Resolution scale setting" : string.Empty);
            }

            UpdateRequestedScale(settings, frameMicroseconds, minimumScale, maximumScale);
            float candidateCommittedScale = QuantizeScale(_requestedScale, minimumScale, maximumScale);
            if (NearlyEqual(candidateCommittedScale, _committedScale))
            {
                ClearPending();
                _framesSinceCommit++;
                return new DynamicResolutionScaleDecision(_requestedScale, _committedScale, false, string.Empty);
            }

            int candidateDirection = candidateCommittedScale > _committedScale ? 1 : -1;
            if (candidateDirection == _pendingDirection)
            {
                _pendingFrameCount++;
            }
            else
            {
                _pendingDirection = candidateDirection;
                _pendingFrameCount = 1;
            }

            _pendingCommittedScale = candidateCommittedScale;
            if (_pendingFrameCount >= ConfirmationFrameCount && _framesSinceCommit >= CooldownFrameCount)
                return CommitImmediate(candidateCommittedScale, "Dynamic resolution scale");

            _framesSinceCommit++;
            return new DynamicResolutionScaleDecision(_requestedScale, _committedScale, false, string.Empty);
        }

        private void UpdateRequestedScale(
            RenderSettings settings,
            long frameMicroseconds,
            float minimumScale,
            float maximumScale)
        {
            _requestedScale = Math.Clamp(_requestedScale, minimumScale, maximumScale);
            if (frameMicroseconds <= 0)
                return;

            float targetMicroseconds = settings.DynamicResolution.TargetFrameMilliseconds * 1000.0f;
            float adjustment = settings.DynamicResolution.AdjustmentRate;
            if (frameMicroseconds > targetMicroseconds * SlowFrameMultiplier)
            {
                _requestedScale = Math.Max(minimumScale, _requestedScale - adjustment);
            }
            else if (frameMicroseconds < targetMicroseconds * FastFrameMultiplier)
            {
                _requestedScale = Math.Min(maximumScale, _requestedScale + adjustment);
            }
        }

        private DynamicResolutionScaleDecision CommitImmediate(float scale, string reason)
        {
            bool changed = !NearlyEqual(scale, _committedScale);
            _committedScale = scale;
            _pendingCommittedScale = scale;
            _pendingDirection = 0;
            _pendingFrameCount = 0;
            _framesSinceCommit = 0;
            return new DynamicResolutionScaleDecision(_requestedScale, _committedScale, changed, changed ? reason : string.Empty);
        }

        private void ClearPending()
        {
            _pendingCommittedScale = _committedScale;
            _pendingDirection = 0;
            _pendingFrameCount = 0;
        }

        private static float QuantizeScale(float scale, float minimumScale, float maximumScale)
        {
            float bucketed = MathF.Round(scale / ScaleBucketSize) * ScaleBucketSize;
            return Math.Clamp(bucketed, minimumScale, maximumScale);
        }

        private static bool NearlyEqual(float left, float right)
        {
            return MathF.Abs(left - right) <= 0.0001f;
        }
    }
}
