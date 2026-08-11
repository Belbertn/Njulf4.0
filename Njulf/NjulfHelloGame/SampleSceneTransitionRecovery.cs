using Njulf.Rendering;
using Silk.NET.Vulkan;

namespace NjulfHelloGame;

internal static class SampleSceneTransitionRecovery
{
    internal static bool Execute(
        Action loadRequestedScene,
        Action cleanupRequestedScene,
        Action loadSafeScene,
        Action cleanupFailedSafeScene,
        Action<Exception> reportRequestedFailure)
    {
        ArgumentNullException.ThrowIfNull(loadRequestedScene);
        ArgumentNullException.ThrowIfNull(cleanupRequestedScene);
        ArgumentNullException.ThrowIfNull(loadSafeScene);
        ArgumentNullException.ThrowIfNull(cleanupFailedSafeScene);
        ArgumentNullException.ThrowIfNull(reportRequestedFailure);

        Exception requestedFailure;
        try
        {
            loadRequestedScene();
            return true;
        }
        catch (Exception failure) when (ContainsDeviceOutOfMemory(failure))
        {
            requestedFailure = failure;
        }

        try
        {
            reportRequestedFailure(requestedFailure);
        }
        catch
        {
            // Failure reporting must not replace the authoritative load error.
        }

        try
        {
            cleanupRequestedScene();
        }
        catch (Exception cleanupFailure)
        {
            throw new AggregateException(
                "The requested scene exhausted device memory and recovery cleanup failed.",
                requestedFailure,
                cleanupFailure);
        }

        try
        {
            loadSafeScene();
            return false;
        }
        catch (Exception safeSceneFailure)
        {
            try
            {
                cleanupFailedSafeScene();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "The requested scene exhausted device memory, the safe scene failed, and safe-scene cleanup was incomplete.",
                    requestedFailure,
                    safeSceneFailure,
                    cleanupFailure);
            }

            throw new AggregateException(
                "The requested scene exhausted device memory and the safe scene also failed.",
                requestedFailure,
                safeSceneFailure);
        }
    }

    internal static bool ContainsDeviceOutOfMemory(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(
            ReferenceEqualityComparer.Instance);
        pending.Push(failure);
        while (pending.Count > 0)
        {
            Exception current = pending.Pop();
            if (!visited.Add(current))
                continue;
            if (current is VulkanException
                {
                    Result: Result.ErrorOutOfDeviceMemory
                })
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException != null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }
}

internal static class SampleScenePresetPolicy
{
    internal static bool ShouldApply(bool? requestedSceneLoaded) =>
        requestedSceneLoaded != false;
}
