using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Executes a topologically ordered shutdown plan with durable per-stage
/// completion. Failed stages remain pending, independent stages continue, and
/// dependent stages stay gated until every prerequisite completes.
/// </summary>
internal sealed class StagedDisposalPlan
{
    private readonly DisposalStage[] _stages;
    private readonly bool[] _completed;
    private readonly object _lock = new();
    private bool _draining;
    private int _drainThreadId;
    private int _completedCount;

    public StagedDisposalPlan(
        IReadOnlyList<StagedDisposalStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _stages = new DisposalStage[steps.Count];
        _completed = new bool[steps.Count];
        var indices = new Dictionary<string, int>(
            steps.Count,
            StringComparer.Ordinal);

        for (int index = 0;
             index < steps.Count;
             index++)
        {
            StagedDisposalStep step =
                steps[index] ??
                throw new ArgumentException(
                    "A disposal plan cannot contain a null step.",
                    nameof(steps));
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                throw new ArgumentException(
                    "Every disposal step requires a stable name.",
                    nameof(steps));
            }
            ArgumentNullException.ThrowIfNull(
                step.Dispose);
            if (!indices.TryAdd(step.Name, index))
            {
                throw new ArgumentException(
                    $"Disposal step '{step.Name}' is duplicated.",
                    nameof(steps));
            }

            string[] dependencyNames =
                step.Dependencies ?? Array.Empty<string>();
            var dependencies =
                new int[dependencyNames.Length];
            for (int dependencyIndex = 0;
                 dependencyIndex <
                     dependencyNames.Length;
                 dependencyIndex++)
            {
                string dependency =
                    dependencyNames[
                        dependencyIndex];
                if (!indices.TryGetValue(
                        dependency,
                        out int resolvedIndex))
                {
                    throw new ArgumentException(
                        $"Disposal step '{step.Name}' depends on '{dependency}', which must be declared earlier in topological order.",
                        nameof(steps));
                }

                dependencies[dependencyIndex] =
                    resolvedIndex;
            }

            _stages[index] =
                new DisposalStage(
                    step.Name,
                    step.Dispose,
                    dependencies);
        }
    }

    public bool IsComplete
    {
        get
        {
            lock (_lock)
            {
                return _completedCount ==
                    _stages.Length;
            }
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _stages.Length -
                    _completedCount;
            }
        }
    }

    public Exception? TryDrain()
    {
        int currentThreadId =
            Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            while (_draining)
            {
                if (_drainThreadId ==
                    currentThreadId)
                {
                    return new InvalidOperationException(
                        "A staged disposal plan cannot re-enter its active drain.");
                }

                Monitor.Wait(_lock);
            }

            if (_completedCount == _stages.Length)
                return null;

            _draining = true;
            _drainThreadId =
                currentThreadId;
        }

        try
        {
            List<Exception>? failures = null;
            for (int index = 0;
                 index < _stages.Length;
                 index++)
            {
                bool canRun;
                lock (_lock)
                {
                    if (_completed[index])
                        continue;
                    canRun =
                        DependenciesCompletedLocked(
                            _stages[index]);
                }

                if (!canRun)
                    continue;

                try
                {
                    _stages[index].Dispose();
                }
                catch (Exception stageFailure)
                {
                    (failures ??=
                            new List<Exception>())
                        .Add(
                            new InvalidOperationException(
                                $"Renderer disposal stage '{_stages[index].Name}' failed.",
                                stageFailure));
                    continue;
                }

                lock (_lock)
                {
                    if (!_completed[index])
                    {
                        _completed[index] = true;
                        _completedCount++;
                    }
                }
            }

            lock (_lock)
            {
                if (failures == null &&
                    _completedCount !=
                    _stages.Length)
                {
                    return new InvalidOperationException(
                        "The staged disposal plan made no path to all pending stages. Its dependency graph is inconsistent.");
                }
            }

            return failures == null
                ? null
                : new AggregateException(
                    "One or more renderer disposal stages remain pending.",
                    failures);
        }
        finally
        {
            lock (_lock)
            {
                _draining = false;
                _drainThreadId = 0;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private bool DependenciesCompletedLocked(
        DisposalStage stage)
    {
        foreach (int dependency in
                 stage.Dependencies)
        {
            if (!_completed[dependency])
                return false;
        }

        return true;
    }

    private sealed record DisposalStage(
        string Name,
        Action Dispose,
        int[] Dependencies);
}

internal sealed class StagedDisposalStep
{
    public StagedDisposalStep(
        string name,
        Action dispose,
        params string[] dependencies)
    {
        Name = name;
        Dispose = dispose;
        Dependencies =
            dependencies ??
            Array.Empty<string>();
    }

    public string Name { get; }

    public Action Dispose { get; }

    public string[] Dependencies { get; }
}
