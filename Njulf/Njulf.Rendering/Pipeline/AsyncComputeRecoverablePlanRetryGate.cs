using System;

namespace Njulf.Rendering.Pipeline
{
    /// <summary>
    /// Identifies the immutable inputs that make an async-compute submission plan safe to retry.
    /// A concrete-binding generation changes for resize, reload, swapchain recreation, and any
    /// rebuilt imported allocation. The settings signature covers a policy rebuild that changes
    /// scheduling without necessarily replacing an allocation.
    /// </summary>
    internal readonly record struct AsyncComputePlanRetryScope(
        ulong ResourcePlanGeneration,
        ulong SettingsSignature);

    /// <summary>
    /// Contains a recoverable async-plan rejection to the exact plan/settings scope that caused
    /// it. This is deliberately separate from the renderer's emergency device/submit latch:
    /// graph declaration mistakes must fail closed for the current scope, but must not disable
    /// validated async work after a resize, reload, or settings rebuild repairs the plan.
    /// </summary>
    internal sealed class AsyncComputeRecoverablePlanRetryGate
    {
        private AsyncComputePlanRetryScope? _rejectedScope;
        private string _reason = string.Empty;

        public AsyncComputePlanRetryScope? RejectedScope => _rejectedScope;
        public string Reason => _reason;

        public bool CanAttempt(AsyncComputePlanRetryScope scope) =>
            !_rejectedScope.HasValue || _rejectedScope.Value != scope;

        /// <summary>
        /// Records one rejected plan scope. Returns true only for a new scope/reason, allowing
        /// callers to keep diagnostics counters from growing every frame while a bad immutable
        /// plan remains unchanged.
        /// </summary>
        public bool RecordRejected(AsyncComputePlanRetryScope scope, string? reason)
        {
            string normalizedReason = string.IsNullOrWhiteSpace(reason)
                ? "Async compute plan validation failed."
                : reason;
            bool changed = !_rejectedScope.HasValue ||
                _rejectedScope.Value != scope ||
                !string.Equals(_reason, normalizedReason, StringComparison.Ordinal);
            _rejectedScope = scope;
            _reason = normalizedReason;
            return changed;
        }

        /// <summary>
        /// Clears the active recovery state after a different graph/settings scope is observed.
        /// Concrete binding generations are monotonic in the renderer, so a rebuilt scope can
        /// retry immediately without retaining stale failure telemetry for the active frame.
        /// </summary>
        public bool ObserveScope(AsyncComputePlanRetryScope scope)
        {
            if (!_rejectedScope.HasValue || _rejectedScope.Value == scope)
                return false;

            _rejectedScope = null;
            _reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Allows a settings implementation that updates its signature in place to explicitly
        /// clear a previous recovery state once a plan has validated successfully.
        /// </summary>
        public void RecordValidatedPlan(AsyncComputePlanRetryScope scope)
        {
            if (_rejectedScope.HasValue && _rejectedScope.Value == scope)
            {
                _rejectedScope = null;
                _reason = string.Empty;
            }
        }
    }
}
