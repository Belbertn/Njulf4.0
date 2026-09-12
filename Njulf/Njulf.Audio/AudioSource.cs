using Njulf.Core.Math;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Njulf.Audio;

public enum AudioPlaybackState { Initial, Playing, Paused, Stopped }

/// <summary>A reusable voice. Stop retains the clip; Dispose releases the native source and its filter.</summary>
public sealed class AudioSource : IDisposable
{
    private readonly AudioSystem _owner;
    private readonly AudioClip _clip;
    private uint _source, _filter;
    private Vector3 _position;
    private float _gain = 1, _pitch = 1, _referenceDistance = 1, _maximumDistance = 100, _rolloff = 1, _occlusion;
    private bool _looping, _occlusionDirty = true;
    public bool IsSpatial { get; }
    public float SmoothedOcclusion { get; private set; }

    internal AudioSource(AudioSystem owner, AudioClip clip, bool spatial)
    {
        _owner = owner; _clip = clip; IsSpatial = spatial;
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
    public bool Looping
    {
        get => _looping;
        set { Check(); _owner.Al.SetSourceProperty(_source, SourceBoolean.Looping, value); _owner.CheckError(); _looping = value; }
    }
    public float Gain
    {
        get => _gain;
        set { Check(); AudioSystem.Unit(value); _gain = value; ApplyOcclusion(); }
    }
    public float Pitch
    {
        get => _pitch;
        set { AudioSystem.Positive(value); Set(SourceFloat.Pitch, value); _pitch = value; }
    }
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
    public AudioPlaybackState State
    {
        get
        {
            Check(); _owner.Al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state); _owner.CheckError();
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
        if (State != AudioPlaybackState.Paused) SmoothedOcclusion = IsSpatial ? _occlusion : 0;
        ApplyOcclusion();
        _owner.Al.SourcePlay(_source); _owner.CheckError();
    }
    public void Pause() { Check(); _owner.Al.SourcePause(_source); _owner.CheckError(); }
    public void Stop() { Check(); _owner.Al.SourceStop(_source); _owner.CheckError(); }

    internal void InvalidateOcclusion() => _occlusionDirty = true;
    internal void Update(float seconds)
    {
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
        _owner.Al.SetSourceProperty(_source, SourceFloat.Gain, _gain * (1 + (_owner.BlockedGain - 1) * amount));
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
        _clip.Attachments--;
        _owner.Remove(this);
    }
}
