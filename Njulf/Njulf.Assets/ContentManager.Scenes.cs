using Njulf.Assets.Scenes;
using Njulf.Core.Scene;

namespace Njulf.Assets;

public partial class ContentManager
{
    public Scene LoadScene(string path, SceneLoadOptions? options = null)
    {
        if (_currentAcquisition.Value == null) return LoadOwned(() => LoadScene(path, options));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new();
        SceneDocument document = SceneDocumentJson.Read(GetFullPath(path));
        IContentScope dependencies = CreateScope();
        try
        {
            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            foreach (string modelPath in SceneModelPaths(document))
                models.Add(modelPath, dependencies.Load<Model>(modelPath, options.ContentOptions));
            return PopulateContentScene(document, options, dependencies, models);
        }
        catch (Exception failure)
        {
            try { ReleaseUnusedDependencies(dependencies); }
            catch (Exception cleanup) { throw new AggregateException("Scene loading and rollback failed.", failure, cleanup); }
            throw;
        }
    }

    public async Task<Scene> LoadSceneAsync(string path, SceneLoadOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (_currentAcquisition.Value == null)
            return await LoadOwnedAsync(() => LoadSceneAsync(path, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        using var operation = BeginOperation(cancellationToken);
        cancellationToken = operation.Token;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options ??= new();
        var request = new ContentPreloadRequest(path);
        var progress = options.ContentOptions.Progress;
        ReportContentProgress(progress, request, ContentLoadStage.Queued, null);
        IContentScope? dependencies = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReportContentProgress(progress, request, ContentLoadStage.Started, null);
            if (_contentUploadDispatcher == null)
            {
                Scene synchronous = LoadScene(path, options);
                ReportContentProgress(progress, request, ContentLoadStage.Ready, null);
                return synchronous;
            }
            ReportContentProgress(progress, request, ContentLoadStage.Preparing, null);
            SceneDocument document = await Task.Run(() => SceneDocumentJson.Read(GetFullPath(path)), cancellationToken).ConfigureAwait(false);
            dependencies = CreateScope();
            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            foreach (string modelPath in SceneModelPaths(document))
                models.Add(modelPath, await dependencies.LoadAsync<Model>(modelPath, options.ContentOptions, cancellationToken).ConfigureAwait(false));
            ReportContentProgress(progress, request, ContentLoadStage.WaitingForUpload, null);
            Scene result = await _contentUploadDispatcher.DispatchAsync(() =>
            {
                ReportContentProgress(progress, request, ContentLoadStage.Uploading, null);
                return PopulateContentScene(document, options, dependencies, models);
            }, cancellationToken).ConfigureAwait(false);
            ReportContentProgress(progress, request, ContentLoadStage.Ready, null);
            return result;
        }
        catch (Exception failure)
        {
            ReportContentProgress(progress, request, failure is OperationCanceledException ? ContentLoadStage.Cancelled : ContentLoadStage.Failed, failure.Message);
            try
            {
                if (dependencies != null)
                {
                    await ReleaseAfterFailedLoadAsync(() => ReleaseUnusedDependencies(dependencies)).ConfigureAwait(false);
                }
            }
            catch (Exception cleanup) { throw new AggregateException("Scene loading and rollback failed.", failure, cleanup); }
            throw;
        }
    }

    private static IEnumerable<string> SceneModelPaths(SceneDocument document) =>
        document.Objects.Select(o => o.Model.Path)
            .Concat(document.InstanceBatches.Select(o => o.Model.Path))
            .Concat(document.FoliagePrototypes.Select(o => o.Model.Path))
            .Distinct(StringComparer.Ordinal);

    private Scene PopulateContentScene(SceneDocument document, SceneLoadOptions options,
        IContentScope dependencies, Dictionary<string, Model> models)
    {
        lock (_stateLock) ValidateAcquisition(_currentAcquisition.Value!);
        var scene = new Scene();
        try
        {
            new SceneDocumentLoader(dependencies, path => models[path])
                .Populate(document, scene, particleEffects: options.ParticleEffects, materials: options.Materials);
        }
        catch (Exception failure)
        {
            try { scene.Dispose(); }
            catch (Exception cleanup)
            {
                lock (_stateLock)
                {
                    RetainFailedPublication(scene);
                    _assetDependencies.Add(scene, dependencies);
                }
                throw new AggregateException("Scene population and rollback failed; cleanup is retained by Content.", failure, cleanup);
            }
            throw;
        }
        lock (_stateLock)
        {
            PublishWithDependencies($"scene-instance|{++_snapshotOwnershipSequence}", scene, dependencies);
        }
        return scene;
    }
}
