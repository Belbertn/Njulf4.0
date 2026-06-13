using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Diagnostics
{
    public enum RuntimeStallReason
    {
        Unknown,
        FrameFenceWait,
        SwapchainAcquire,
        QueueSubmit,
        Present,
        DeviceWaitIdle,
        ResourceResize,
        SynchronousUpload
    }

    public sealed record RuntimeStallEvent(RuntimeStallReason Reason, long Microseconds, string Description);

    public sealed record RuntimeStallSnapshot(
        long TotalMicrosecondsThisFrame,
        long WorstMicrosecondsThisFrame,
        RuntimeStallReason WorstReasonThisFrame,
        int DeviceWaitIdleCount,
        IReadOnlyList<RuntimeStallEvent> RecentEvents);

    public sealed class RuntimeStallTracker
    {
        private readonly RuntimeStallEvent[] _recentEvents;
        private int _nextEventIndex;
        private int _eventCount;
        private long _totalMicrosecondsThisFrame;
        private long _worstMicrosecondsThisFrame;
        private RuntimeStallReason _worstReasonThisFrame = RuntimeStallReason.Unknown;
        private int _deviceWaitIdleCount;

        public RuntimeStallTracker(int capacity = 32)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _recentEvents = new RuntimeStallEvent[capacity];
        }

        public void BeginFrame()
        {
            _totalMicrosecondsThisFrame = 0;
            _worstMicrosecondsThisFrame = 0;
            _worstReasonThisFrame = RuntimeStallReason.Unknown;
        }

        public void Record(RuntimeStallReason reason, long microseconds, string? description = null)
        {
            if (microseconds <= 0)
                return;

            _totalMicrosecondsThisFrame += microseconds;
            if (microseconds > _worstMicrosecondsThisFrame)
            {
                _worstMicrosecondsThisFrame = microseconds;
                _worstReasonThisFrame = reason;
            }

            if (reason == RuntimeStallReason.DeviceWaitIdle)
                _deviceWaitIdleCount++;

            var stall = new RuntimeStallEvent(reason, microseconds, description ?? string.Empty);
            _recentEvents[_nextEventIndex] = stall;
            _nextEventIndex = (_nextEventIndex + 1) % _recentEvents.Length;
            if (_eventCount < _recentEvents.Length)
                _eventCount++;
        }

        public RuntimeStallSnapshot CreateSnapshot()
        {
            var events = new List<RuntimeStallEvent>(_eventCount);
            int start = (_nextEventIndex - _eventCount + _recentEvents.Length) % _recentEvents.Length;
            for (int i = 0; i < _eventCount; i++)
            {
                RuntimeStallEvent stall = _recentEvents[(start + i) % _recentEvents.Length];
                if (stall != null)
                    events.Add(stall);
            }

            return new RuntimeStallSnapshot(
                _totalMicrosecondsThisFrame,
                _worstMicrosecondsThisFrame,
                _worstReasonThisFrame,
                _deviceWaitIdleCount,
                events);
        }
    }
}
