namespace Njulf.Graphics;

public enum EffectStage { BeforeScene, AfterScene, AfterPostProcessing, AfterToneMapping }
public enum EffectViewImage { SceneColor, SceneDepth, Backbuffer, PostProcessColor }
/// <summary>Exactly one texture or current-view image. View images have stage-specific availability.</summary>
public sealed record EffectImage(ITexture? Texture = null, EffectViewImage? ViewImage = null);
/// <summary>A named texture/view image or storage-buffer range. Size zero selects the remaining buffer.</summary>
public sealed record EffectResourceBinding(string Name, ITexture? Texture = null, EffectViewImage? ViewImage = null,
    GraphicsBuffer? Buffer = null, ulong Offset = 0, ulong Size = 0);

/// <summary>Owned effect execution. All mutations require the device thread and take effect at the next frame boundary.</summary>
public abstract class EffectRegistration : IDisposable
{
    internal EffectRegistration() { }
    public abstract ShaderEffectAsset Asset { get; }
    public abstract bool IsDisposed { get; }
    public abstract bool Enabled { get; set; }
    /// <summary>Sets a named parameter using its exact declared type. Range hints do not clamp values.</summary>
    public abstract void SetParameter<T>(string name, T value) where T : struct;
    /// <summary>Atomically replaces resource bindings, retaining new resources before returning.</summary>
    /// <remarks>Post effects supply SourceColor and (for compute) Destination automatically.</remarks>
    public abstract void Rebind(IReadOnlyList<EffectResourceBinding> bindings);
    /// <summary>Replaces an ordinary fullscreen pass's bindings and destination together.</summary>
    public virtual void Rebind(IReadOnlyList<EffectResourceBinding> bindings, EffectImage destination) => throw new NotSupportedException();
    /// <summary>Replaces an ordinary compute pass's bindings and thread extent together.</summary>
    public virtual void Rebind(IReadOnlyList<EffectResourceBinding> bindings, EffectDispatchSize dispatchSize) => throw new NotSupportedException();
    public abstract void Dispose();
}

public abstract partial class GraphicsDevice
{
    /// <summary>Registers a fullscreen triangle with replacement output and named resource bindings.</summary>
    public virtual EffectRegistration AddFullscreenEffect(string name, ShaderEffectAsset asset, EffectStage stage,
        IReadOnlyList<EffectResourceBinding> bindings, EffectImage destination) => throw new NotSupportedException();
    /// <summary>Registers compute on the graphics queue. Dispatch size is in threads; shaders must bounds-check excess invocations.</summary>
    public virtual EffectRegistration AddComputeEffect(string name, ShaderEffectAsset asset, EffectStage stage,
        IReadOnlyList<EffectResourceBinding> bindings, EffectDispatchSize dispatchSize) => throw new NotSupportedException();
    /// <summary>Registers a color effect after tone mapping and before AA, with automatically sized, separate input/output images.</summary>
    /// <remarks>The asset must declare sampled SourceColor and, for compute, writable rgba16f Destination.</remarks>
    public virtual EffectRegistration AddPostProcessEffect(string name, ShaderEffectAsset asset,
        IReadOnlyList<EffectResourceBinding>? bindings = null) => throw new NotSupportedException();
}
