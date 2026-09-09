using System.Collections.Concurrent;

namespace Njulf.Core;

/// <summary>Runs await continuations on the thread that owns the game device.</summary>
internal sealed class GameSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue((callback, state));
    public override SynchronizationContext CreateCopy() => this;
    public override void Send(SendOrPostCallback callback, object? state)
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new NotSupportedException("Use asynchronous dispatch to the game thread.");
        callback(state);
    }

    public void Pump()
    {
        // Bound each iteration so a continuation that posts again cannot starve uploads/events.
        int count = _pending.Count;
        while (count-- > 0 && _pending.TryDequeue(out var work))
            work.Callback(work.State);
    }
}
