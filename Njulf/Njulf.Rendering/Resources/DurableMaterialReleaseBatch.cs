namespace Njulf.Rendering.Resources;

/// <summary>
/// Retains exact material-release work until every logical reference has been
/// released. Successful entries are removed immediately, so retries neither
/// leak later entries nor double-release earlier ones.
/// </summary>
internal sealed class DurableMaterialReleaseBatch
{
    private readonly object _lock = new();
    private readonly Action<MaterialHandle> _release;
    private readonly List<MaterialHandle> _pending;

    public DurableMaterialReleaseBatch(
        IReadOnlyList<MaterialHandle> materials,
        Action<MaterialHandle> release)
    {
        ArgumentNullException.ThrowIfNull(materials);
        _release =
            release ?? throw new ArgumentNullException(nameof(release));
        _pending = new List<MaterialHandle>(materials.Count);
        foreach (MaterialHandle material in materials)
        {
            if (material.IsValid)
                _pending.Add(material);
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_lock)
                return _pending.Count;
        }
    }

    public void ReleaseOutstanding()
    {
        lock (_lock)
        {
            List<Exception>? failures = null;
            for (int index = _pending.Count - 1;
                 index >= 0;
                 index--)
            {
                try
                {
                    _release(_pending[index]);
                    _pending.RemoveAt(index);
                }
                catch (Exception releaseFailure)
                {
                    (failures ??= new List<Exception>())
                        .Add(releaseFailure);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more material references could not be released.",
                    failures);
            }
        }
    }
}
