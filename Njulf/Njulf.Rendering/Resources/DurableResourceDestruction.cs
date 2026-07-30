using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Small, allocation-free destruction primitives for staged shutdown. Owned
/// state is invalidated or removed only after the destruction callback returns
/// successfully, which makes a later shutdown attempt safe and exact-once.
/// </summary>
internal static class DurableResourceDestruction
{
    public static Exception? TryDestroy<T>(
        ref T resource,
        T invalidResource,
        Func<T, bool> isValid,
        Action<T> destroy)
    {
        ArgumentNullException.ThrowIfNull(isValid);
        ArgumentNullException.ThrowIfNull(destroy);

        T ownedResource = resource;
        if (!isValid(ownedResource))
            return null;

        try
        {
            destroy(ownedResource);
        }
        catch (Exception failure)
        {
            return failure;
        }

        resource = invalidResource;
        return null;
    }

    /// <summary>
    /// Attempts every resource present when this call starts. Successful
    /// entries are removed immediately; failed entries retain their original
    /// relative order for the next retry.
    /// </summary>
    public static void TryDestroyAll<T>(
        List<T> resources,
        Func<T, bool> isValid,
        Action<T> destroy,
        ref List<Exception>? failures)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(isValid);
        ArgumentNullException.ThrowIfNull(destroy);

        int remainingAttempts = resources.Count;
        int index = 0;
        while (remainingAttempts > 0)
        {
            T resource = resources[index];
            Exception? failure = null;
            if (isValid(resource))
            {
                try
                {
                    destroy(resource);
                }
                catch (Exception destroyFailure)
                {
                    failure = destroyFailure;
                }
            }

            if (failure == null)
            {
                resources.RemoveAt(index);
            }
            else
            {
                (failures ??= new List<Exception>())
                    .Add(failure);
                index++;
            }

            remainingAttempts--;
        }
    }
}
