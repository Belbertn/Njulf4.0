using System;

namespace Njulf.Rendering.Diagnostics
{
    public readonly record struct PerformanceSampleStats(
        int Count,
        double Average,
        double Min,
        double Max,
        double P95);

    public sealed class PerformanceSampleWindow
    {
        private readonly double[] _samples;
        private readonly double[] _scratch;
        private int _nextIndex;
        private int _count;

        public PerformanceSampleWindow(int capacity)
        {
            if (capacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            _samples = new double[capacity];
            _scratch = new double[capacity];
        }

        public void Add(double value)
        {
            _samples[_nextIndex] = value;
            _nextIndex = (_nextIndex + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }

        public PerformanceSampleStats GetStats()
        {
            if (_count == 0)
                return new PerformanceSampleStats(0, 0, 0, 0, 0);

            double sum = 0;
            double min = double.MaxValue;
            double max = double.MinValue;
            for (int i = 0; i < _count; i++)
            {
                double sample = _samples[i];
                _scratch[i] = sample;
                sum += sample;
                min = Math.Min(min, sample);
                max = Math.Max(max, sample);
            }

            Array.Sort(_scratch, 0, _count);
            int p95Index = Math.Min(_count - 1, (int)Math.Ceiling(_count * 0.95) - 1);
            return new PerformanceSampleStats(_count, sum / _count, min, max, _scratch[p95Index]);
        }
    }
}
