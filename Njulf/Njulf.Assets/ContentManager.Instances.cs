using Njulf.Core.Scene;

namespace Njulf.Assets;

public partial class ContentManager
{
    /// <summary>Loads a content-owned template, creates an independent placement, and transfers it to the scene.</summary>
    /// <remarks>The returned instance is borrowed from the scene; remove it through Scene.Remove.
    /// The template remains owned by this content lifetime, including after attachment failure.
    /// The host must pump uploads. Without a dispatcher, call on the device/scene thread.</remarks>
    public async Task<ModelInstance> LoadModelInstanceAsync(Scene scene, string path,
        ContentLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var operation = BeginOperation(cancellationToken);
        CancellationToken token = operation.Token;
        // Complete ordinary content acquisition before attachment: failure must not unload a shared template.
        Model template = await LoadAsync<Model>(path, options, token).ConfigureAwait(false);

        ModelInstance Attach()
        {
            lock (_stateLock)
            {
                ThrowIfDisposed();
                token.ThrowIfCancellationRequested();
                ModelInstance instance = template.CreateInstance();
                try
                {
                    token.ThrowIfCancellationRequested();
                    scene.Add(instance);
                    return instance; // Successful attachment is the commit point, even if cancellation races it.
                }
                catch (Exception failure)
                {
                    try
                    {
                        if (scene.ModelInstances.Contains(instance)) scene.Remove(instance);
                        else instance.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        // Failed scene removal remains scene-owned for retry; otherwise Content retains cleanup.
                        if (!scene.ModelInstances.Contains(instance)) RetainFailedPublication(instance);
                        throw new AggregateException("Model attachment and rollback failed; cleanup remains owned for retry.", failure, cleanup);
                    }
                    throw;
                }
            }
        }

        return _contentUploadDispatcher == null
            ? Attach()
            : await _contentUploadDispatcher.DispatchAsync(Attach, token).ConfigureAwait(false);
    }
}
