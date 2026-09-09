using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Njulf.Assets;
using Njulf.Core.Scene;

namespace NjulfHelloGame;

/// <summary>Advances scene assembly and rollback on the content pump's device thread.</summary>
internal sealed class SampleScenePreparationWork(
    Scene scene,
    IEnumerable<object?> steps,
    Action publish) : IContentUploadWork<bool>
{
    internal const int MaximumObjectsPerStep = 512;
    private IEnumerator<object?>? _steps;
    private ExceptionDispatchInfo? _failure;
    private int _cancelled;
    private bool _published;

    // Dispatch may forward cancellation from a worker, including during shutdown.
    public void RequestCancellation() => Interlocked.Exchange(ref _cancelled, 1);

    public ContentUploadStepResult ExecuteStep(in ContentUploadSliceBudget budget)
    {
        if (_published)
            return ContentUploadStepResult.Complete();

        long started = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _cancelled) != 0 || _failure != null)
        {
            // Remove from the end so a cancelled large scene also yields between frames.
            for (int i = 0; i < MaximumObjectsPerStep && scene.RenderObjects.Count > 0; i++)
            {
                scene.Remove(scene.RenderObjects[^1]);
                if (BudgetUsed(started, budget))
                    return ContentUploadStepResult.Yield();
            }
            if (scene.RenderObjects.Count > 0)
                return ContentUploadStepResult.Yield();
            _steps?.Dispose();
            scene.Dispose();
            _failure?.Throw();
            return ContentUploadStepResult.Cancelled();
        }

        try
        {
            _steps ??= steps.GetEnumerator();
            for (int i = 0; i < MaximumObjectsPerStep; i++)
            {
                if (!_steps.MoveNext())
                {
                    _steps.Dispose();
                    publish();
                    _published = true; // Ownership has passed to the host.
                    return ContentUploadStepResult.Complete();
                }
                if (Volatile.Read(ref _cancelled) != 0 || BudgetUsed(started, budget))
                    break;
            }
        }
        catch (Exception failure)
        {
            // Keep the item queued until its partially constructed scene is released.
            _failure = ExceptionDispatchInfo.Capture(failure);
        }
        return ContentUploadStepResult.Yield();
    }

    public bool GetResult() => _published
        ? true
        : throw new InvalidOperationException("Scene preparation has not been published.");

    private static bool BudgetUsed(long started, in ContentUploadSliceBudget budget) =>
        budget.RemainingCpuTime > TimeSpan.Zero &&
        Stopwatch.GetElapsedTime(started) >= budget.RemainingCpuTime;
}
