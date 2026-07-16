namespace Njulf.Rendering.Debug
{
    public readonly record struct PassTiming(string Name, long CpuMicroseconds, long GpuMicroseconds, bool GpuAvailable);

    public sealed class FrameTimingSnapshot
    {
        private readonly Dictionary<string, PassTiming> _timings;

        public FrameTimingSnapshot(IEnumerable<PassTiming> timings)
        {
            _timings = timings?.ToDictionary(timing => timing.Name, StringComparer.Ordinal) ??
                new Dictionary<string, PassTiming>(StringComparer.Ordinal);
        }

        public static FrameTimingSnapshot Empty { get; } = new(Array.Empty<PassTiming>());

        public IReadOnlyCollection<PassTiming> Passes => _timings.Values;

        public bool TryGetPass(string passName, out PassTiming timing)
        {
            if (passName == null)
                throw new ArgumentNullException(nameof(passName));

            return _timings.TryGetValue(passName, out timing);
        }

        public long GetGpuMicrosecondsOrZero(string passName)
        {
            return TryGetPass(passName, out PassTiming timing) && timing.GpuAvailable
                ? timing.GpuMicroseconds
                : 0;
        }

        public static long ConvertTimestampDeltaToMicroseconds(ulong start, ulong end, float timestampPeriodNanoseconds)
        {
            if (end <= start || timestampPeriodNanoseconds <= 0.0f)
                return 0;

            double nanoseconds = (end - start) * (double)timestampPeriodNanoseconds;
            return (long)Math.Round(nanoseconds / 1000.0);
        }
    }
}
