using System;

namespace Njulf.Rendering.Resources
{
    /// <summary>
    /// Sliding age histogram whose origin advances once per frame. Existing
    /// entries age without touching their probe records.
    /// </summary>
    internal sealed class SimpleDdgiIncrementalAgeHistogram
    {
        private readonly int[] _buckets;
        private int _origin;
        private int _overflowCount;
        private ulong _frameSerial;

        public SimpleDdgiIncrementalAgeHistogram(int maximumExactAge)
        {
            if (maximumExactAge < 1)
                throw new ArgumentOutOfRangeException(nameof(maximumExactAge));
            MaximumExactAge = maximumExactAge;
            _buckets = new int[maximumExactAge + 1];
        }

        public int MaximumExactAge { get; }
        public int Count { get; private set; }

        public void Clear(ulong frameSerial)
        {
            Array.Clear(_buckets);
            _origin = 0;
            _overflowCount = 0;
            Count = 0;
            _frameSerial = frameSerial;
        }

        public void Add(uint age, ulong frameSerial)
        {
            Advance(frameSerial);
            if (age > MaximumExactAge)
                _overflowCount++;
            else
                _buckets[PhysicalBucket((int)age)]++;
            Count++;
        }

        public void Remove(uint age, ulong frameSerial)
        {
            Advance(frameSerial);
            if (Count <= 0)
                return;

            if (age > MaximumExactAge)
                _overflowCount = Math.Max(0, _overflowCount - 1);
            else
            {
                int bucket = PhysicalBucket((int)age);
                _buckets[bucket] = Math.Max(0, _buckets[bucket] - 1);
            }
            Count--;
        }

        public int CountAbove(int age, ulong frameSerial)
        {
            Advance(frameSerial);
            if (age < 0)
                return Count;
            if (age >= MaximumExactAge)
                return _overflowCount;

            int count = _overflowCount;
            for (int candidateAge = age + 1;
                 candidateAge <= MaximumExactAge;
                 candidateAge++)
            {
                count += _buckets[PhysicalBucket(candidateAge)];
            }
            return count;
        }

        /// <summary>Returns a one-based nearest-rank age within tracked entries.</summary>
        public int SelectRank(int rank, ulong frameSerial)
        {
            Advance(frameSerial);
            if (Count <= 0 || rank <= 0)
                return 0;

            int target = Math.Min(rank, Count);
            int cumulative = 0;
            for (int age = 0; age <= MaximumExactAge; age++)
            {
                cumulative += _buckets[PhysicalBucket(age)];
                if (cumulative >= target)
                    return age;
            }

            return MaximumExactAge + 1;
        }

        private void Advance(ulong frameSerial)
        {
            if (frameSerial <= _frameSerial)
                return;

            ulong elapsed = frameSerial - _frameSerial;
            int bucketCount = _buckets.Length;
            if (elapsed > (ulong)MaximumExactAge)
            {
                _overflowCount = Count;
                Array.Clear(_buckets);
                int shift = (int)(elapsed % (ulong)bucketCount);
                _origin = (_origin - shift + bucketCount) % bucketCount;
                _frameSerial = frameSerial;
                return;
            }

            for (ulong frame = 0; frame < elapsed; frame++)
            {
                _origin = (_origin - 1 + bucketCount) % bucketCount;
                _overflowCount += _buckets[_origin];
                _buckets[_origin] = 0;
            }
            _frameSerial = frameSerial;
        }

        private int PhysicalBucket(int age) =>
            (_origin + age) % _buckets.Length;
    }
}
