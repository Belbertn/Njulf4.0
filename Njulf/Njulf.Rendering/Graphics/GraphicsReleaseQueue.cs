namespace Njulf.Graphics;

// Keeps manager references alive until submission completion; no per-frame allocation.
internal sealed class GraphicsReleaseQueue
{
    private readonly List<(ulong Serial, Action Release)> _pending = new();
    private ulong _submitted, _completed;
    internal int Count => _pending.Count;
    internal void Enqueue(Action release, bool recordingOrFaulted)
    {
        if (!recordingOrFaulted && _submitted <= _completed) { release(); return; }
        _pending.Add((recordingOrFaulted ? ulong.MaxValue : _submitted, release));
    }
    internal void EnqueueRetirement(Action release, bool recordingOrFaulted) =>
        _pending.Add((recordingOrFaulted ? ulong.MaxValue : _submitted, release));
    internal void Submitted(ulong serial)
    {
        _submitted = System.Math.Max(_submitted, serial);
        for (int i = 0; i < _pending.Count; i++)
            if (_pending[i].Serial == ulong.MaxValue) _pending[i] = (serial, _pending[i].Release);
    }
    internal void Complete(ulong serial, bool deviceIdle = false)
    {
        _completed = System.Math.Max(_completed, serial);
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (deviceIdle || (_pending[i].Serial != ulong.MaxValue && _pending[i].Serial <= _completed))
            {
                _pending[i].Release(); // Failure leaves the entry available for retry.
                _pending.RemoveAt(i);
            }
    }
}
