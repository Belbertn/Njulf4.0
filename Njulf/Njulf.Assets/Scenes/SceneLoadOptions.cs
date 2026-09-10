namespace Njulf.Assets.Scenes;

/// <summary>Model import options and optional application services used to populate a fresh scene.</summary>
/// <remarks>Optional stores retain their existing synchronous device-thread contracts.</remarks>
public sealed record SceneLoadOptions
{
    public ContentLoadOptions ContentOptions { get; init; } = ContentLoadOptions.Default;
    public ISceneMaterialOverrideStore? Materials { get; init; }
    public ISceneParticleEffectStore? ParticleEffects { get; init; }
}
