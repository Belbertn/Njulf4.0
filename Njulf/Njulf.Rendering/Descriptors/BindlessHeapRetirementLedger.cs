using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Descriptors;

internal enum BindlessHeapOwnedResource
{
    StorageBufferPool,
    StorageBufferSetLayout,
    TextureSamplerPool,
    TextureSamplerSetLayout,
    DefaultSampler,
    ScreenSampler,
    HiZSampler
}

/// <summary>
/// Retry-safe ownership ledger for bindless Vulkan objects. Descriptor pools
/// are retired before the layouts and samplers referenced by their sets, while
/// the storage and texture branches remain independently progressable.
/// </summary>
internal sealed class BindlessHeapRetirementLedger
{
    private readonly HashSet<BindlessHeapOwnedResource> _pending = new();

    public bool IsEmpty => _pending.Count == 0;

    public bool IsPending(BindlessHeapOwnedResource resource) =>
        _pending.Contains(resource);

    public void Add(BindlessHeapOwnedResource resource)
    {
        if (!_pending.Add(resource))
        {
            throw new InvalidOperationException(
                $"Bindless resource {resource} is already owned.");
        }
    }

    public void Retire(Action<BindlessHeapOwnedResource> destroy)
    {
        ArgumentNullException.ThrowIfNull(destroy);

        List<Exception>? failures = null;

        TryRetire(
            BindlessHeapOwnedResource.StorageBufferPool,
            destroy,
            ref failures);
        if (!IsPending(BindlessHeapOwnedResource.StorageBufferPool))
        {
            TryRetire(
                BindlessHeapOwnedResource.StorageBufferSetLayout,
                destroy,
                ref failures);
        }

        TryRetire(
            BindlessHeapOwnedResource.TextureSamplerPool,
            destroy,
            ref failures);
        if (!IsPending(BindlessHeapOwnedResource.TextureSamplerPool))
        {
            TryRetire(
                BindlessHeapOwnedResource.TextureSamplerSetLayout,
                destroy,
                ref failures);
            TryRetire(
                BindlessHeapOwnedResource.DefaultSampler,
                destroy,
                ref failures);
            TryRetire(
                BindlessHeapOwnedResource.ScreenSampler,
                destroy,
                ref failures);
            TryRetire(
                BindlessHeapOwnedResource.HiZSampler,
                destroy,
                ref failures);
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(
                "One or more bindless resources could not be retired.",
                failures);
        }
    }

    private void TryRetire(
        BindlessHeapOwnedResource resource,
        Action<BindlessHeapOwnedResource> destroy,
        ref List<Exception>? failures)
    {
        if (!_pending.Contains(resource))
            return;

        try
        {
            destroy(resource);
            _pending.Remove(resource);
        }
        catch (Exception failure)
        {
            (failures ??= new List<Exception>()).Add(failure);
        }
    }
}
