using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Scene;
using Njulf.Graphics;

namespace Njulf.ApiExamples;

internal sealed class ContentExample(ExampleOptions options) : ExampleGame(options)
{
    private IContentScope? _level;
    private IContentScope? _preparingLevel;
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
        _level = Content.CreateScope();
        Scene next = await _level.LoadSceneAsync("Assets/Content/level-a.njscene.json", cancellationToken: cancellationToken);
        _sharedMaterial = await _level.LoadAsync<Material>("Assets/Content/shared.njmaterial.json", cancellationToken: cancellationToken);
        _levelMaterial = await _level.LoadAsync<Material>("Assets/Content/level-only.njmaterial.json", cancellationToken: cancellationToken);
        _levelTexture = _level.Load<Texture>("Assets/Content/level-only.png");
        next.RenderObjects[0].Material = _sharedMaterial;
        next.RenderObjects[1].Material = _levelMaterial;
        ExchangeScene(next).Dispose();
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
        _preparingLevel = Content.CreateScope();
        Scene next = await _preparingLevel.LoadSceneAsync("Assets/Content/level-b.njscene.json", cancellationToken: _cancellation);
        Material shared = await _preparingLevel.LoadAsync<Material>("Assets/Content/shared.njmaterial.json", cancellationToken: _cancellation);
        foreach (RenderObject placement in next.RenderObjects) placement.Material = shared;
        ExchangeScene(next);
        _level!.Dispose();
        _level = _preparingLevel;
        _preparingLevel = null;
        _releasedOldLevel = _levelTexture!.IsDisposed && _levelMaterial!.IsDisposed;
        _preservedSharedAssets = ReferenceEquals(shared, _sharedMaterial) && !shared.IsDisposed && !_sharedTexture!.IsDisposed;
        if (!_releasedOldLevel || !_preservedSharedAssets)
            throw new InvalidOperationException("Level transition did not preserve scoped content ownership.");
        Console.WriteLine("Content transition: old level released; shared material and root texture remain usable.");
    }

    protected override void Unload()
    {
        ExchangeScene(new Scene());
        _preparingLevel?.Dispose();
        _level?.Dispose();
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
