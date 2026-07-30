namespace Njulf.Rendering.Resources;

/// <summary>
/// Runs cleanup that occurs after authoritative publication. Failures are
/// reported but never escape to the caller as a false upload failure.
/// </summary>
internal static class CommittedResourceCleanup
{
    public static void Execute(
        Action retireReplacedResources,
        Action releaseCompletionPrimitive,
        Action<Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(retireReplacedResources);
        ArgumentNullException.ThrowIfNull(releaseCompletionPrimitive);
        ArgumentNullException.ThrowIfNull(reportFailure);

        TryCleanup(
            retireReplacedResources,
            reportFailure);
        TryCleanup(
            releaseCompletionPrimitive,
            reportFailure);
    }

    private static void TryCleanup(
        Action cleanup,
        Action<Exception> reportFailure)
    {
        try
        {
            cleanup();
        }
        catch (Exception cleanupFailure)
        {
            try
            {
                reportFailure(cleanupFailure);
            }
            catch
            {
                // Publication is already authoritative. Diagnostics must never
                // turn a cleanup problem into a misleading upload failure.
            }
        }
    }
}
