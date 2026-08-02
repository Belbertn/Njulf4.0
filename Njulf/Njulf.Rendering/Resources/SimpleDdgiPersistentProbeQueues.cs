using System;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Allocation-free intrusive queues used by the simple-DDGI scheduler.
    /// A probe owns at most one link in each queue set, so state transitions are
    /// O(1) and pending counts never require a probe-pool scan.
    /// </summary>
    internal sealed class SimpleDdgiPersistentProbeQueues
    {
        internal const int NoQueue = -1;

        private readonly int _queueCount;
        private readonly int _workClassCount;
        private int[] _heads;
        private int[] _tails;
        private int[] _counts;
        private int[] _workClassCounts;
        private int[] _next = Array.Empty<int>();
        private int[] _previous = Array.Empty<int>();
        private int[] _probeQueues = Array.Empty<int>();

        public SimpleDdgiPersistentProbeQueues(int queueCount, int workClassCount)
        {
            if (queueCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(queueCount));
            if (workClassCount <= 0 || queueCount % workClassCount != 0)
                throw new ArgumentOutOfRangeException(nameof(workClassCount));

            _queueCount = queueCount;
            _workClassCount = workClassCount;
            _heads = new int[queueCount];
            _tails = new int[queueCount];
            _counts = new int[queueCount];
            _workClassCounts = new int[workClassCount];
            ClearQueueMetadata();
        }

        public int ProbeCapacity => _probeQueues.Length;

        public void EnsureProbeCapacity(int probeCount)
        {
            probeCount = Math.Max(0, probeCount);
            if (_probeQueues.Length == probeCount)
                return;

            _next = new int[probeCount];
            _previous = new int[probeCount];
            _probeQueues = new int[probeCount];
            Clear();
        }

        public void Clear()
        {
            ClearQueueMetadata();
            Array.Fill(_next, NoQueue);
            Array.Fill(_previous, NoQueue);
            Array.Fill(_probeQueues, NoQueue);
        }

        public int GetQueueCount(int queueIndex) =>
            (uint)queueIndex < (uint)_counts.Length ? _counts[queueIndex] : 0;

        public int GetWorkClassCount(int workClassIndex) =>
            (uint)workClassIndex < (uint)_workClassCounts.Length
                ? _workClassCounts[workClassIndex]
                : 0;

        public int GetProbeQueue(int probeIndex) =>
            (uint)probeIndex < (uint)_probeQueues.Length
                ? _probeQueues[probeIndex]
                : NoQueue;

        public void MoveToQueue(int probeIndex, int queueIndex)
        {
            if ((uint)probeIndex >= (uint)_probeQueues.Length)
                return;
            if (queueIndex != NoQueue && (uint)queueIndex >= (uint)_queueCount)
                throw new ArgumentOutOfRangeException(nameof(queueIndex));

            int previousQueue = _probeQueues[probeIndex];
            if (previousQueue == queueIndex)
                return;

            if (previousQueue != NoQueue)
                RemoveCore(probeIndex, previousQueue);
            if (queueIndex != NoQueue)
                AppendCore(probeIndex, queueIndex);
        }

        /// <summary>
        /// Returns the current head and rotates it to the tail. Callers bound a
        /// traversal with the queue's count captured before the first rotation.
        /// </summary>
        public bool TryRotateNext(int queueIndex, out int probeIndex)
        {
            probeIndex = NoQueue;
            if ((uint)queueIndex >= (uint)_queueCount)
                return false;

            int head = _heads[queueIndex];
            if (head == NoQueue)
                return false;

            probeIndex = head;
            if (_counts[queueIndex] <= 1)
                return true;

            int newHead = _next[head];
            int oldTail = _tails[queueIndex];
            _heads[queueIndex] = newHead;
            _previous[newHead] = NoQueue;
            _next[oldTail] = head;
            _previous[head] = oldTail;
            _next[head] = NoQueue;
            _tails[queueIndex] = head;
            return true;
        }

        private void AppendCore(int probeIndex, int queueIndex)
        {
            int tail = _tails[queueIndex];
            _probeQueues[probeIndex] = queueIndex;
            _previous[probeIndex] = tail;
            _next[probeIndex] = NoQueue;
            if (tail == NoQueue)
                _heads[queueIndex] = probeIndex;
            else
                _next[tail] = probeIndex;
            _tails[queueIndex] = probeIndex;
            _counts[queueIndex]++;
            _workClassCounts[queueIndex % _workClassCount]++;
        }

        private void RemoveCore(int probeIndex, int queueIndex)
        {
            int previous = _previous[probeIndex];
            int next = _next[probeIndex];
            if (previous == NoQueue)
                _heads[queueIndex] = next;
            else
                _next[previous] = next;
            if (next == NoQueue)
                _tails[queueIndex] = previous;
            else
                _previous[next] = previous;

            _probeQueues[probeIndex] = NoQueue;
            _previous[probeIndex] = NoQueue;
            _next[probeIndex] = NoQueue;
            _counts[queueIndex]--;
            _workClassCounts[queueIndex % _workClassCount]--;
        }

        private void ClearQueueMetadata()
        {
            Array.Fill(_heads, NoQueue);
            Array.Fill(_tails, NoQueue);
            Array.Clear(_counts);
            Array.Clear(_workClassCounts);
        }
    }
}
