namespace Njulf.Audio;

/// <summary>Controls whether scope-owned voices follow simulation pause; inactive scopes always suspend playback.</summary>
public enum AudioPausePolicy { FollowScope, IgnoreSimulationPause }

/// <summary>A fixed mix group owned by one audio system. All controls use its game thread.</summary>
public sealed class AudioGroup
{
    internal AudioSystem Owner { get; }
    private float _volume = 1, _fadeStart, _fadeTarget, _fadeDuration, _fadeElapsed;
    private bool _muted, _paused;
    private AudioPausePolicy _pausePolicy;
    internal AudioGroup(AudioSystem owner, AudioPausePolicy policy) { Owner = owner; _pausePolicy = policy; }
    /// <summary>Mix multiplier in [0,1], default 1. Setting it cancels any fade.</summary>
    public float Volume
    {
        get => _volume;
        set { Owner.Check(); AudioSystem.Unit(value); _fadeDuration = 0; _volume = value; Changed(); }
    }
    /// <summary>Silences the group without changing volume, playback position or pause state.</summary>
    public bool Muted
    {
        get => _muted;
        set { Owner.Check(); _muted = value; Changed(); }
    }
    /// <summary>Whether the group was explicitly paused.</summary>
    public bool IsPaused => _paused;
    /// <summary>Scope pause behavior: SFX follows simulation; Music/UI ignore it by default.</summary>
    public AudioPausePolicy PausePolicy
    {
        get => _pausePolicy;
        set
        {
            Owner.Check();
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _pausePolicy = value; Changed();
        }
    }
    internal float EffectiveGain => _muted ? 0 : _volume;
    /// <summary>Suspends group voices; independent source and scope pause reasons remain intact.</summary>
    public void Pause() { Owner.Check(); _paused = true; Changed(); }
    /// <summary>Clears group pause; voices resume only when no other pause reason remains.</summary>
    public void Resume() { Owner.Check(); _paused = false; Changed(); }
    /// <summary>Linear volume fade in unscaled seconds, including while paused or muted.
    /// Replaces the current fade; zero duration is immediate. Setting Volume cancels a fade.</summary>
    public void FadeTo(float volume, float seconds)
    {
        Owner.Check(); AudioSystem.Unit(volume); AudioSystem.Nonnegative(seconds);
        if (seconds == 0) { Volume = volume; return; }
        _fadeStart = _volume; _fadeTarget = volume; _fadeDuration = seconds; _fadeElapsed = 0;
    }
    internal void Update(float seconds)
    {
        if (_fadeDuration == 0) return;
        _fadeElapsed = MathF.Min(_fadeDuration, _fadeElapsed + seconds);
        _volume = _fadeStart + (_fadeTarget - _fadeStart) * (_fadeElapsed / _fadeDuration);
        if (_fadeElapsed == _fadeDuration) { _volume = _fadeTarget; _fadeDuration = 0; }
        Changed();
    }
    private void Changed() => Owner.RefreshGroup(this);
}
