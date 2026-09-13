using Njulf.Core.Math;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Njulf.Audio;

/// <summary>Logical voice state, including source, scope and group suspension.</summary>
public enum AudioPlaybackState { Initial, Playing, Paused, Stopped }

/// <summary>A reusable voice. Stop retains the clip; Dispose releases the native source and its filter.</summary>
public sealed class AudioSource : IDisposable
{
    private readonly AudioSystem _owner;
    private AudioClip? _clip;
    internal bool HasClip => _clip != null;
    private uint _source, _filter;
    private Vector3 _position;
    private float _gain = 1, _pitch = 1, _referenceDistance = 1, _maximumDistance = 100, _rolloff = 1, _occlusion;
    private bool _looping, _occlusionDirty = true;
    private bool _hostSuspended, _resumeAfterHost, _deferredPause, _stopped;
    internal AudioScope? Scope { get; set; }
    private AudioGroup _group;
    private Func<Vector3>? _positionProvider;
    /// <summary>Mix group from the same audio system, default SFX. The source borrows the group.</summary>
    public AudioGroup Group
    {
        get => _group;
        set { Check(); _owner.CheckGroup(value); _group = value; RefreshGroup(); }
    }
    /// <summary>Optional world-space position binding. Null restores manual Position control.</summary>
    public Func<Vector3>? PositionProvider
    {
        get => _positionProvider;
        set { Check(); _positionProvider = value; }
    }
    /// <summary>Whether playback is positioned in world space; spatial sources require mono clips.</summary>
    public bool IsSpatial { get; }
    /// <summary>Current interpolated obstruction in [0,1], updated during audio maintenance.</summary>
    public float SmoothedOcclusion { get; private set; }

    internal AudioSource(AudioSystem owner, AudioClip clip, bool spatial)
    {
        _owner = owner; _clip = clip; IsSpatial = spatial; _group = owner.SFX;
        try
        {
            _source = owner.Al.GenSource();
            owner.Al.SetSourceProperty(_source, SourceInteger.Buffer, clip.Buffer);
            owner.Al.SetSourceProperty(_source, SourceBoolean.SourceRelative, !spatial);
            owner.Al.SetSourceProperty(_source, SourceFloat.ReferenceDistance, _referenceDistance);
            owner.Al.SetSourceProperty(_source, SourceFloat.MaxDistance, _maximumDistance);
            owner.Al.SetSourceProperty(_source, SourceFloat.RolloffFactor, spatial ? _rolloff : 0);
            owner.CheckError();
            clip.Attachments++;
        }
        catch { if (_source != 0) owner.Al.DeleteSource(_source); _source = 0; throw; }
    }

    /// <summary>World-space position in scene units; ignored for non-spatial playback.</summary>
    public Vector3 Position
    {
        get => _position;
        set
        {
            Check(); AudioSystem.Finite(value);
            if (IsSpatial) _owner.Al.SetSourceProperty(_source, SourceVector3.Position, value.X, value.Y, value.Z);
            _owner.CheckError(); _position = value;
        }
    }
    /// <summary>Whether the clip repeats; false by default.</summary>
    public bool Looping
    {
        get => _looping;
        set { Check(); _owner.Al.SetSourceProperty(_source, SourceBoolean.Looping, value); _owner.CheckError(); _looping = value; }
    }
    /// <summary>Voice volume multiplier in [0,1], default 1; multiplied by group/master volume and attenuation.</summary>
    public float Gain
    {
        get => _gain;
        set { Check(); AudioSystem.Unit(value); _gain = value; ApplyOcclusion(); }
    }
    /// <summary>Positive playback-rate multiplier, default 1. Simulation time scaling does not change pitch.</summary>
    public float Pitch
    {
        get => _pitch;
        set { AudioSystem.Positive(value); Set(SourceFloat.Pitch, value); _pitch = value; }
    }
    /// <summary>Positive distance where attenuation begins, default 1 scene unit; must not exceed MaximumDistance.</summary>
    public float ReferenceDistance
    {
        get => _referenceDistance;
        set
        {
            AudioSystem.Positive(value);
            if (value > _maximumDistance) throw new ArgumentOutOfRangeException(nameof(value), "Reference distance must not exceed maximum distance.");
            Set(SourceFloat.ReferenceDistance, value); _referenceDistance = value;
        }
    }
    /// <summary>Distance attenuation clamps here; this is not a hard audible-range cutoff.</summary>
    public float MaximumDistance
    {
        get => _maximumDistance;
        set
        {
            AudioSystem.Positive(value);
            if (value < _referenceDistance) throw new ArgumentOutOfRangeException(nameof(value), "Maximum distance must not be below reference distance.");
            Set(SourceFloat.MaxDistance, value); _maximumDistance = value;
        }
    }
    /// <summary>Nonnegative attenuation strength, default 1; zero disables distance attenuation.</summary>
    public float Rolloff
    {
        get => _rolloff;
        set { AudioSystem.Nonnegative(value); Set(SourceFloat.RolloffFactor, IsSpatial ? value : 0); _rolloff = value; }
    }
    /// <summary>Gameplay-supplied obstruction: 0 clear, 1 blocked. Non-spatial sources ignore occlusion.</summary>
    public float Occlusion
    {
        get => _occlusion;
        set { Check(); AudioSystem.Unit(value); _occlusion = value; }
    }
    /// <summary>Current logical playback state. Scope/group-suspended voices report Paused.</summary>
    public AudioPlaybackState State
    {
        get
        {
            Check();
            if (_stopped) return AudioPlaybackState.Stopped;
            if (_deferredPause || (_hostSuspended && _resumeAfterHost)) return AudioPlaybackState.Paused;
            _owner.Al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state); _owner.CheckError();
            return (SourceState)state switch
            {
                SourceState.Playing => AudioPlaybackState.Playing,
                SourceState.Paused => AudioPlaybackState.Paused,
                SourceState.Stopped => AudioPlaybackState.Stopped,
                _ => AudioPlaybackState.Initial
            };
        }
    }

    /// <summary>Starts/restarts playback; resumes a paused voice. Applies initial occlusion before starting.</summary>
    public void Play()
    {
        Check();
        UpdatePosition();
        RefreshSuspension();
        if (State != AudioPlaybackState.Paused) SmoothedOcclusion = IsSpatial ? _occlusion : 0;
        ApplyOcclusion();
        _stopped = false;
        _deferredPause = false;
        if (_hostSuspended) { _resumeAfterHost = true; return; }
        _owner.Al.SourcePlay(_source); _owner.CheckError();
    }
    /// <summary>Continues a paused voice at its current position. Other states are unchanged.</summary>
    public void Resume()
    {
        Check();
        if (State == AudioPlaybackState.Paused) Play();
    }
    /// <summary>Starts at sample zero, or queues that start until all suspension reasons clear.</summary>
    public void Restart()
    {
        Check(); Stop();
        _owner.Al.SourceRewind(_source); _owner.CheckError();
        Play();
    }
    /// <summary>Pauses at the current playback position; host resume does not undo this explicit pause.</summary>
    public void Pause()
    {
        Check();
        if (_hostSuspended && _resumeAfterHost) _deferredPause = true;
        _resumeAfterHost = false;
        _owner.Al.SourcePause(_source); _owner.CheckError();
    }
    /// <summary>Stops playback and cancels queued resume requests while retaining the clip for replay.</summary>
    public void Stop()
    {
        Check(); _resumeAfterHost = false; _deferredPause = false;
        _owner.Al.SourceStop(_source); _owner.CheckError();
        _stopped = true;
    }

    internal void SetHostSuspended(bool suspended)
    {
        Check();
        if (_hostSuspended == suspended) return;
        if (suspended)
        {
            bool playing = State == AudioPlaybackState.Playing;
            _hostSuspended = true;
            if (playing)
            {
                _owner.Al.SourcePause(_source); _owner.CheckError();
                _resumeAfterHost = true;
            }
        }
        else
        {
            _hostSuspended = false;
            bool resume = _resumeAfterHost;
            _resumeAfterHost = false;
            if (resume) Play();
        }
    }

    internal void RefreshSuspension() => SetHostSuspended(Group.IsPaused || (Scope?.BlocksPlayback(Group.PausePolicy) ?? false));
    internal void RefreshGroup() { RefreshSuspension(); ApplyOcclusion(); }
    private void UpdatePosition() { if (_positionProvider != null) Position = _positionProvider(); }

    internal void DetachClip()
    {
        Check();
        Stop();
        _owner.Al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        _owner.CheckError();
        if (_clip != null) { _clip.Attachments--; _clip = null; }
    }

    internal void PlayOneShot(AudioClip clip, Vector3 position, float volume)
    {
        DetachClip();
        _owner.Al.SetSourceProperty(_source, SourceInteger.Buffer, clip.Buffer);
        _owner.CheckError();
        _clip = clip; clip.Attachments++;
        Looping = false; Pitch = 1; ReferenceDistance = 1; MaximumDistance = 100; Rolloff = 1;
        Occlusion = 0; SmoothedOcclusion = 0; Position = position; Gain = volume;
        Play();
    }

    internal void InvalidateOcclusion() => _occlusionDirty = true;
    internal void Update(float seconds)
    {
        if (Scope == null) UpdatePosition();
        float target = IsSpatial ? _occlusion : 0;
        float previous = SmoothedOcclusion;
        float blend = _owner.OcclusionSmoothingSeconds == 0 ? 1 : 1 - MathF.Exp(-seconds / _owner.OcclusionSmoothingSeconds);
        SmoothedOcclusion += (target - SmoothedOcclusion) * blend;
        if (MathF.Abs(target - SmoothedOcclusion) < 1e-5f) SmoothedOcclusion = target;
        if (_occlusionDirty || previous != SmoothedOcclusion) ApplyOcclusion();
    }
    private void ApplyOcclusion()
    {
        float amount = SmoothedOcclusion;
        _owner.Al.SetSourceProperty(_source, SourceFloat.Gain, _gain * Group.EffectiveGain * (1 + (_owner.BlockedGain - 1) * amount));
        if (_owner.Efx is { } efx && IsSpatial)
        {
            if (amount > 0)
            {
                if (_filter == 0)
                {
                    _filter = efx.GenFilter();
                    efx.SetFilterProperty(_filter, FilterInteger.FilterType, (int)FilterType.Lowpass);
                    efx.SetFilterProperty(_filter, FilterFloat.LowpassGain, 1);
                }
                efx.SetFilterProperty(_filter, FilterFloat.LowpassGainHF, 1 + (_owner.BlockedHighFrequencyGain - 1) * amount);
            }
            // OpenAL copies filter parameters on attachment, so reattach after every change.
            efx.SetSourceProperty(_source, EFXSourceInteger.DirectFilter, amount > 0 ? (int)_filter : 0);
        }
        _owner.CheckError(); _occlusionDirty = false;
    }
    private void Set(SourceFloat property, float value) { Check(); _owner.Al.SetSourceProperty(_source, property, value); _owner.CheckError(); }
    private void Check() { _owner.Check(); ObjectDisposedException.ThrowIf(_source == 0, this); }

    public void Dispose()
    {
        if (_source == 0) return;
        _owner.Check();
        _owner.Al.SourceStop(_source);
        _owner.Al.DeleteSource(_source); _source = 0;
        if (_filter != 0) { _owner.Efx!.DeleteFilter(_filter); _filter = 0; }
        if (_clip != null) { _clip.Attachments--; _clip = null; }
        _positionProvider = null;
        _owner.Remove(this);
        Scope?.Remove(this);
    }
}
