using Njulf.Core;

namespace Njulf.Rendering;

internal readonly record struct RendererAnimationTime(
    bool IsHosted, float SceneTime, float ElapsedSeconds, float ParticleTime,
    float ParticleDeltaSeconds, float UnscaledElapsedSeconds);

// Render-driven effects intentionally remain render-step, independent of fixed gameplay updates.
internal sealed class RendererAnimationClock
{
    private GameTime? _hostTime;
    private double _scale = 1;
    private bool _paused, _consumed;
    private float _particleTime;

    public void SetGameTime(GameTime time, double scale, bool paused)
    {
        if (!double.IsFinite(scale) || scale < 0) throw new ArgumentOutOfRangeException(nameof(scale));
        _hostTime = time;
        _scale = scale;
        _paused = paused || scale == 0;
        _consumed = false;
    }

    public RendererAnimationTime Read(float legacyParticleDelta, float particleOverride)
    {
        if (_hostTime is not { } time)
        {
            _particleTime += legacyParticleDelta;
            return new(false, 0, legacyParticleDelta, _particleTime, legacyParticleDelta, legacyParticleDelta);
        }
        float elapsed = _consumed || _paused ? 0 : (float)time.ElapsedGameTime.TotalSeconds;
        float particleDelta = _consumed || _paused ? 0 : particleOverride > 0
            ? (float)(particleOverride * _scale) : elapsed;
        float unscaled = _consumed ? 0 : (float)time.UnscaledElapsedGameTime.TotalSeconds;
        _particleTime += particleDelta;
        _consumed = true;
        return new(true, (float)time.TotalGameTime.TotalSeconds, elapsed, _particleTime, particleDelta, unscaled);
    }
}
