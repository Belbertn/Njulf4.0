using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics
{
    public readonly record struct AsyncComputeTimingKey(
        string DeviceIdentity,
        string DriverIdentity,
        string WorkloadIdentity,
        AsyncComputePath Path);

    public readonly record struct AsyncComputeTimingStats(
        int Count,
        double MeanMilliseconds,
        double P95Milliseconds,
        double P99Milliseconds);

    public sealed record AsyncComputeTimingDecision(
        AsyncComputePathStatus Status,
        bool Eligible,
        bool Active,
        string Reason,
        AsyncComputeTimingStats GraphicsOnly,
        AsyncComputeTimingStats Async);

    /// <summary>
    /// Per-device, per-driver timing policy for Auto mode.  It deliberately keeps baseline and
    /// async windows separate and uses hysteresis/cooldown so a path cannot flap every frame.
    /// Callers are expected to invalidate a workload when resolution, scene, GI mode, or major
    /// feature state changes.
    /// </summary>
    public sealed class AsyncComputeTimingPolicy
    {
        private readonly Dictionary<AsyncComputeTimingKey, PathState> _states = new();
        private readonly int _windowCapacity;

        public AsyncComputeTimingPolicy(int windowCapacity = 120)
        {
            if (windowCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(windowCapacity));
            _windowCapacity = windowCapacity;
        }

        public void RecordGraphicsOnly(
            AsyncComputeTimingKey key,
            double frameMilliseconds,
            double cpuSubmitMilliseconds = 0.0)
        {
            if (!IsUsableSample(frameMilliseconds))
                return;

            PathState state = GetState(key);
            state.GraphicsOnly.Add(frameMilliseconds);
            state.GraphicsCpuSubmit.Add(NormalizeComponent(cpuSubmitMilliseconds));
        }

        public void RecordAsync(
            AsyncComputeTimingKey key,
            double frameMilliseconds,
            double computeDispatchMilliseconds,
            double transferBarrierMilliseconds,
            double graphicsWaitMilliseconds,
            double cpuSubmitMilliseconds)
        {
            if (!IsUsableSample(frameMilliseconds))
                return;

            PathState state = GetState(key);
            // The GPU frame-time window decides the queue-overlap portion of promote/demote. The
            // effective decision below also includes measured CPU barrier/submit cost. Graphics
            // wait is already represented by the overlap-adjusted frame measurement, so adding it
            // again here would double-count the same unhidden compute work.
            state.Async.Add(frameMilliseconds);
            state.ComputeDispatch.Add(NormalizeComponent(computeDispatchMilliseconds));
            state.TransferBarrier.Add(NormalizeComponent(transferBarrierMilliseconds));
            state.GraphicsWait.Add(NormalizeComponent(graphicsWaitMilliseconds));
            state.CpuSubmit.Add(NormalizeComponent(cpuSubmitMilliseconds));
        }

        public AsyncComputeTimingDecision Evaluate(
            AsyncComputeTimingKey key,
            AsyncComputeSettings settings,
            int frameNumber)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            PathState state = GetState(key);
            int minimumSamples = Math.Max(1, settings.AutoMinimumSampleCount);
            int warmupFrames = Math.Max(0, settings.AutoWarmupFrameCount);
            AsyncComputeTimingStats graphics = state.GraphicsOnly.GetStats();
            AsyncComputeTimingStats async = state.Async.GetStats();

            if (frameNumber < warmupFrames || graphics.Count < minimumSamples || async.Count < minimumSamples)
            {
                return new AsyncComputeTimingDecision(
                    AsyncComputePathStatus.PendingWarmup,
                    Eligible: false,
                    Active: false,
                    $"Waiting for warmup/baseline samples ({graphics.Count}/{minimumSamples} graphics, {async.Count}/{minimumSamples} async).",
                    graphics,
                    async);
            }

            AsyncComputeTimingStats graphicsCpuSubmit = state.GraphicsCpuSubmit.GetStats();
            AsyncComputeTimingStats transferBarrier = state.TransferBarrier.GetStats();
            AsyncComputeTimingStats asyncCpuSubmit = state.CpuSubmit.GetStats();
            double graphicsMean = graphics.MeanMilliseconds + graphicsCpuSubmit.MeanMilliseconds;
            double asyncMean = async.MeanMilliseconds +
                transferBarrier.MeanMilliseconds +
                asyncCpuSubmit.MeanMilliseconds;
            double graphicsP95 = graphics.P95Milliseconds + graphicsCpuSubmit.P95Milliseconds;
            double graphicsP99 = graphics.P99Milliseconds + graphicsCpuSubmit.P99Milliseconds;
            double asyncP95 = async.P95Milliseconds +
                transferBarrier.P95Milliseconds +
                asyncCpuSubmit.P95Milliseconds;
            double asyncP99 = async.P99Milliseconds +
                transferBarrier.P99Milliseconds +
                asyncCpuSubmit.P99Milliseconds;

            double absoluteBenefit = graphicsMean - asyncMean;
            double relativeBenefit = graphicsMean <= 0.0
                ? 0.0
                : absoluteBenefit / graphicsMean;
            double promoteAbsolute = Math.Max(0.0, settings.AutoMinimumAbsoluteBenefitMilliseconds);
            double promoteRelative = Math.Max(0.0, settings.AutoMinimumRelativeBenefit);
            bool tailStable = asyncP95 <= graphicsP95 * 1.01 &&
                              asyncP99 <= graphicsP99 * 1.01;
            bool promote = absoluteBenefit >= promoteAbsolute && relativeBenefit >= promoteRelative && tailStable;
            // Demotion is deliberately easier than promotion only when tails regress. Otherwise a
            // half-sized threshold gives stable hysteresis while still removing a real regression.
            bool demote = absoluteBenefit < promoteAbsolute * 0.5 ||
                          relativeBenefit < promoteRelative * 0.5 ||
                          asyncP95 > graphicsP95 * 1.03 ||
                          asyncP99 > graphicsP99 * 1.03;

            bool cooldownActive = state.LastDecisionFrame >= 0 &&
                                  frameNumber - state.LastDecisionFrame < Math.Max(0, settings.AutoDecisionCooldownFrames);
            if (!cooldownActive)
            {
                if (!state.Active && promote)
                {
                    state.Active = true;
                    state.LastDecisionFrame = frameNumber;
                }
                else if (state.Active && demote)
                {
                    state.Active = false;
                    state.LastDecisionFrame = frameNumber;
                }
            }

            if (state.Active)
            {
                return new AsyncComputeTimingDecision(
                    AsyncComputePathStatus.Enabled,
                    Eligible: true,
                    Active: true,
                    $"Mean benefit {absoluteBenefit:F3} ms ({relativeBenefit:P1}); p95/p99 remain within the Auto guard band.",
                    graphics,
                    async);
            }

            string reason = cooldownActive
                ? "Decision cooldown is active; retaining the prior Auto decision."
                : !tailStable
                    ? "Async p95/p99 frame time exceeds the Auto guard band."
                    : $"Mean benefit {absoluteBenefit:F3} ms ({relativeBenefit:P1}) is below the promote thresholds.";
            return new AsyncComputeTimingDecision(
                AsyncComputePathStatus.NoMeasuredBenefit,
                Eligible: false,
                Active: false,
                reason,
                graphics,
                async);
        }

        /// <summary>
        /// Returns true only after a stable graphics-only baseline exists and before the path has
        /// enough async samples to evaluate. The renderer uses this to run one path at a time in
        /// Auto mode, so a path cannot bootstrap itself from mixed, un-attributable async frames.
        /// </summary>
        public bool CanCollectAsyncProbe(
            AsyncComputeTimingKey key,
            AsyncComputeSettings settings,
            int frameNumber)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            PathState state = GetState(key);
            int minimumSamples = Math.Max(1, settings.AutoMinimumSampleCount);
            return frameNumber >= Math.Max(0, settings.AutoWarmupFrameCount) &&
                   state.GraphicsOnly.Count >= minimumSamples &&
                   state.Async.Count < minimumSamples;
        }

        public void InvalidateWorkload(string deviceIdentity, string driverIdentity, string workloadIdentity)
        {
            foreach (AsyncComputeTimingKey key in _states.Keys
                         .Where(key => string.Equals(key.DeviceIdentity, deviceIdentity, StringComparison.Ordinal) &&
                                       string.Equals(key.DriverIdentity, driverIdentity, StringComparison.Ordinal) &&
                                       string.Equals(key.WorkloadIdentity, workloadIdentity, StringComparison.Ordinal))
                         .ToArray())
            {
                _states.Remove(key);
            }
        }

        public void Clear() => _states.Clear();

        private PathState GetState(AsyncComputeTimingKey key)
        {
            if (!_states.TryGetValue(key, out PathState? state))
            {
                state = new PathState(_windowCapacity);
                _states.Add(key, state);
            }

            return state;
        }

        private static bool IsUsableSample(double value) => double.IsFinite(value) && value >= 0.0;
        private static double NormalizeComponent(double value) => IsUsableSample(value) ? value : 0.0;

        private sealed class PathState
        {
            public PathState(int capacity)
            {
                GraphicsOnly = new Window(capacity);
                GraphicsCpuSubmit = new Window(capacity);
                Async = new Window(capacity);
                ComputeDispatch = new Window(capacity);
                TransferBarrier = new Window(capacity);
                GraphicsWait = new Window(capacity);
                CpuSubmit = new Window(capacity);
            }

            public Window GraphicsOnly { get; }
            public Window GraphicsCpuSubmit { get; }
            public Window Async { get; }
            public Window ComputeDispatch { get; }
            public Window TransferBarrier { get; }
            public Window GraphicsWait { get; }
            public Window CpuSubmit { get; }
            public bool Active { get; set; }
            public int LastDecisionFrame { get; set; } = -1;
        }

        private sealed class Window
        {
            private readonly double[] _values;
            private readonly double[] _scratch;
            private int _next;
            private int _count;

            public Window(int capacity)
            {
                _values = new double[capacity];
                _scratch = new double[capacity];
            }

            public void Add(double value)
            {
                _values[_next] = value;
                _next = (_next + 1) % _values.Length;
                if (_count < _values.Length)
                    _count++;
            }

            public int Count => _count;

            public AsyncComputeTimingStats GetStats()
            {
                if (_count == 0)
                    return new AsyncComputeTimingStats(0, 0, 0, 0);

                double sum = 0;
                for (int i = 0; i < _count; i++)
                {
                    double value = _values[i];
                    _scratch[i] = value;
                    sum += value;
                }
                Array.Sort(_scratch, 0, _count);
                return new AsyncComputeTimingStats(
                    _count,
                    sum / _count,
                    Percentile(0.95),
                    Percentile(0.99));
            }

            private double Percentile(double percentile)
            {
                int index = Math.Min(_count - 1, (int)Math.Ceiling(_count * percentile) - 1);
                return _scratch[index];
            }
        }
    }
}
