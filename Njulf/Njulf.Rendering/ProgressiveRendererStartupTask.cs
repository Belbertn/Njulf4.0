namespace Njulf.Rendering;

/// <summary>
/// Keeps asynchronous orchestration outside the renderer's unsafe Vulkan
/// context. All Vulkan work still runs in the explicitly supplied actions.
/// </summary>
internal static class ProgressiveRendererStartupTask
{
    internal static async Task RunAsync(
        Task? initialization,
        Action prepare,
        Action publish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        ArgumentNullException.ThrowIfNull(publish);
        if (initialization != null)
        {
            await initialization.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(prepare, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        publish();
    }
}
