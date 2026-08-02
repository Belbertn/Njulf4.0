using System;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Fixed-capacity indexed min-heap. Each probe has at most one wake-up, so
    /// retry and periodic-refresh deadlines can be changed without stale nodes or
    /// render-thread allocation.
    /// </summary>
    internal sealed class SimpleDdgiSchedulerWakeHeap
    {
        private int[] _heapProbes = Array.Empty<int>();
        private ulong[] _heapFrames = Array.Empty<ulong>();
        private int[] _probePositions = Array.Empty<int>();
        private int _count;

        public int Count => _count;

        public void EnsureProbeCapacity(int probeCount)
        {
            probeCount = Math.Max(0, probeCount);
            if (_probePositions.Length == probeCount)
                return;

            _heapProbes = new int[probeCount];
            _heapFrames = new ulong[probeCount];
            _probePositions = new int[probeCount];
            Clear();
        }

        public void Clear()
        {
            _count = 0;
            Array.Fill(_probePositions, -1);
        }

        public void Remove(int probeIndex)
        {
            if ((uint)probeIndex >= (uint)_probePositions.Length)
                return;
            int position = _probePositions[probeIndex];
            if (position < 0)
                return;

            RemoveAt(position);
        }

        public void Schedule(int probeIndex, ulong frameSerial)
        {
            if ((uint)probeIndex >= (uint)_probePositions.Length)
                return;

            int position = _probePositions[probeIndex];
            if (position >= 0)
            {
                ulong previousFrame = _heapFrames[position];
                _heapFrames[position] = frameSerial;
                if (frameSerial < previousFrame)
                    SiftUp(position);
                else if (frameSerial > previousFrame)
                    SiftDown(position);
                return;
            }

            position = _count++;
            _heapProbes[position] = probeIndex;
            _heapFrames[position] = frameSerial;
            _probePositions[probeIndex] = position;
            SiftUp(position);
        }

        public bool TryPopDue(ulong currentFrameSerial, out int probeIndex)
        {
            probeIndex = -1;
            if (_count <= 0 || _heapFrames[0] > currentFrameSerial)
                return false;

            probeIndex = _heapProbes[0];
            RemoveAt(0);
            return true;
        }

        public bool TryPeek(out int probeIndex, out ulong frameSerial)
        {
            if (_count <= 0)
            {
                probeIndex = -1;
                frameSerial = 0UL;
                return false;
            }

            probeIndex = _heapProbes[0];
            frameSerial = _heapFrames[0];
            return true;
        }

        private void RemoveAt(int position)
        {
            int removedProbe = _heapProbes[position];
            int lastPosition = --_count;
            _probePositions[removedProbe] = -1;
            if (position == lastPosition)
                return;

            int replacementProbe = _heapProbes[lastPosition];
            ulong replacementFrame = _heapFrames[lastPosition];
            _heapProbes[position] = replacementProbe;
            _heapFrames[position] = replacementFrame;
            _probePositions[replacementProbe] = position;

            int parent = (position - 1) / 2;
            if (position > 0 && IsEarlier(position, parent))
                SiftUp(position);
            else
                SiftDown(position);
        }

        private void SiftUp(int position)
        {
            while (position > 0)
            {
                int parent = (position - 1) / 2;
                if (!IsEarlier(position, parent))
                    break;
                Swap(position, parent);
                position = parent;
            }
        }

        private void SiftDown(int position)
        {
            while (true)
            {
                int left = position * 2 + 1;
                if (left >= _count)
                    return;
                int right = left + 1;
                int earliest = right < _count && IsEarlier(right, left) ? right : left;
                if (!IsEarlier(earliest, position))
                    return;
                Swap(position, earliest);
                position = earliest;
            }
        }

        private bool IsEarlier(int left, int right)
        {
            ulong leftFrame = _heapFrames[left];
            ulong rightFrame = _heapFrames[right];
            return leftFrame < rightFrame ||
                (leftFrame == rightFrame && _heapProbes[left] < _heapProbes[right]);
        }

        private void Swap(int left, int right)
        {
            int leftProbe = _heapProbes[left];
            int rightProbe = _heapProbes[right];
            (_heapProbes[left], _heapProbes[right]) = (rightProbe, leftProbe);
            (_heapFrames[left], _heapFrames[right]) = (_heapFrames[right], _heapFrames[left]);
            _probePositions[leftProbe] = right;
            _probePositions[rightProbe] = left;
        }
    }
}
