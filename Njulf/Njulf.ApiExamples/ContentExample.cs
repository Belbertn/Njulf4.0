using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Scene;
using Njulf.Graphics;

namespace Njulf.ApiExamples;

internal sealed class ContentExample(ExampleOptions options) : ExampleGame(options)
{
    private Texture? _sharedTexture;
    private Texture? _levelTexture;
    private Material? _sharedMaterial;
    private Material? _levelMaterial;
    private Task? _transition;
    private CancellationToken _cancellation;
    private bool _releasedOldLevel;
    private bool _preservedSharedAssets;
    private bool _releasedRoot;

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        _sharedTexture = await Content.LoadAsync<Texture>("Assets/Content/shared.png", cancellationToken: cancellationToken);
        await LoadLevelAsync(async (level, token) =>
        {
            await level.LoadSceneAsync("Assets/Content/level-a.njscene.json", cancellationToken: token);
            _sharedMaterial = await level.Content.LoadAsync<Material>("Assets/Content/shared.njmaterial.json", cancellationToken: token);
            _levelMaterial = await level.Content.LoadAsync<Material>("Assets/Content/level-only.njmaterial.json", cancellationToken: token);
            _levelTexture = level.Content.Load<Texture>("Assets/Content/level-only.png");
            level.Scene.RenderObjects[0].Material = _sharedMaterial;
            level.Scene.RenderObjects[1].Material = _levelMaterial;
        }, cancellationToken);
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (_transition == null && QualityFrames >= 40)
            _transition = SwitchLevelAsync();
        if (_transition?.IsFaulted == true) _transition.GetAwaiter().GetResult();
    }

    private async Task SwitchLevelAsync()
    {
        Material shared = null!;
        await LoadLevelAsync(async (level, token) =>
        {
            await level.LoadSceneAsync("Assets/Content/level-b.njscene.json", cancellationToken: token);
            shared = await level.Content.LoadAsync<Material>("Assets/Content/shared.njmaterial.json", cancellationToken: token);
            foreach (RenderObject placement in level.Scene.RenderObjects) placement.Material = shared;
        }, _cancellation);
        _releasedOldLevel = _levelTexture!.IsDisposed && _levelMaterial!.IsDisposed;
        _preservedSharedAssets = ReferenceEquals(shared, _sharedMaterial) && !shared.IsDisposed && !_sharedTexture!.IsDisposed;
        if (!_releasedOldLevel || !_preservedSharedAssets)
            throw new InvalidOperationException("Level transition did not preserve scoped content ownership.");
        Console.WriteLine("Content transition: old level released; shared material and root texture remain usable.");
    }

    protected override void Unload()
    {
        // Shutdown has drained pending loading and is already outside update/render callbacks.
        UnloadLevelAsync().GetAwaiter().GetResult();
        if (_sharedTexture != null)
        {
            bool survivedLevels = !_sharedTexture.IsDisposed;
            Content.Unload(_sharedTexture);
            _releasedRoot = survivedLevels && _sharedTexture.IsDisposed;
        }
        base.Unload();
    }

    public void ValidateContentCompletion()
    {
        if (_transition?.IsCompletedSuccessfully != true || !_releasedOldLevel || !_preservedSharedAssets || !_releasedRoot)
            throw new InvalidOperationException("Content example must complete its level transition and shutdown; use at least 180 frames.");
        Console.WriteLine("Content ownership: level release, shared reuse, and final root release passed.");
    }
}
