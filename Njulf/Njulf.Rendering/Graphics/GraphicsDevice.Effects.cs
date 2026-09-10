namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    private readonly List<VulkanEffectRegistration> _effects = [];
    internal ulong PostEffectRevision { get; set; }
    public override EffectRegistration AddFullscreenEffect(string name, ShaderEffectAsset asset, EffectStage stage,
        IReadOnlyList<EffectResourceBinding> bindings, EffectImage destination)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(destination);
        if (asset.Kind != ShaderEffectKind.Fullscreen) throw new ArgumentException("A fullscreen shader asset is required.");
        return AddEffect(name, asset, stage, bindings, destination, default, false);
    }
    public override EffectRegistration AddComputeEffect(string name, ShaderEffectAsset asset, EffectStage stage,
        IReadOnlyList<EffectResourceBinding> bindings, EffectDispatchSize dispatchSize)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Kind != ShaderEffectKind.Compute) throw new ArgumentException("A compute shader asset is required.");
        return AddEffect(name, asset, stage, bindings, null, dispatchSize, false);
    }
    public override EffectRegistration AddPostProcessEffect(string name, ShaderEffectAsset asset,
        IReadOnlyList<EffectResourceBinding>? bindings = null) =>
        AddEffect(name, asset, EffectStage.AfterToneMapping, bindings ?? [], null, default, true);
    private EffectRegistration AddEffect(string name, ShaderEffectAsset asset, EffectStage stage,
        IReadOnlyList<EffectResourceBinding> bindings, EffectImage? destination, EffectDispatchSize dispatch, bool post)
    {
        EnsureUsable();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(bindings);
        var effect = new VulkanEffectRegistration(this, name, asset, stage, bindings, destination, dispatch, post);
        _effects.Add(effect);
        return effect;
    }
    internal void PrepareEffects()
    {
        foreach (var effect in _effects) effect.Prepare();
    }
    internal void RemoveEffect(VulkanEffectRegistration effect) => _effects.Remove(effect);
    internal void ReleaseEffectsAfterDeviceIdle()
    {
        foreach (var effect in _effects) effect.ShutdownAfterDeviceIdle();
        _effects.Clear();
    }
}
