using Njulf.Core.Math;
using Silk.NET.OpenAL;
using Silk.NET.OpenAL.Extensions.Creative;

namespace Njulf.Audio;

/// <summary>Optional, game-owned audio. All operations and disposal run on the creating thread.</summary>
public sealed unsafe class AudioSystem : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private ALContext _alc = null!;
    private Device* _device;
    private Context* _context;
    private bool _disposed;
    private readonly List<AudioSource> _sources = [];
    private readonly List<AudioClip> _clips = [];
    private float _masterGain = 1, _blockedGain = .35f, _blockedHighFrequencyGain = .1f, _smoothingSeconds = .1f;
    internal AL Al { get; private set; } = null!;
    internal EffectExtension? Efx { get; private set; }
    public bool SupportsEfx => Efx != null;

    /// <summary>Opens the default output device, or the named device. Initialization failure throws.</summary>
    public AudioSystem(string? deviceName = null)
    {
        try
        {
            _alc = ALContext.GetApi(soft: true);
            _device = _alc.OpenDevice(deviceName ?? "");
            Initialize(null, true);
        }
        catch (Exception error)
        {
            Dispose();
            throw new InvalidOperationException("Could not initialize OpenAL audio. Check the native runtime and output device.", error);
        }
    }

    // Transfers device/API ownership; used by headless OpenAL Soft loopback tests.
    internal AudioSystem(ALContext alc, Device* device, int[] attributes, bool enableEfx = true)
    {
        _alc = alc; _device = device;
        try { Initialize(attributes, enableEfx); }
        catch { Dispose(); throw; }
    }

    private void Initialize(int[]? attributes, bool enableEfx)
    {
        if (_device == null) throw new InvalidOperationException("OpenAL could not open the output device.");
        fixed (int* values = attributes) _context = _alc.CreateContext(_device, values);
        if (_context == null || !_alc.MakeContextCurrent(_context))
            throw new InvalidOperationException("OpenAL could not create or activate the audio context.");
        Al = AL.GetApi(soft: true);
        if (enableEfx && _alc.IsExtensionPresent(_device, "ALC_EXT_EFX") && Al.TryGetExtension<EffectExtension>(out var efx))
            Efx = efx;
        Al.DistanceModel(DistanceModel.InverseDistanceClamped);
        Al.DopplerFactor(0);
        SetListener(Vector3.Zero, Vector3.Forward, Vector3.Up);
        CheckError();
    }

    public float MasterGain
    {
        get => _masterGain;
        set { Check(); Unit(value); Al.SetListenerProperty(ListenerFloat.Gain, value); CheckError(); _masterGain = value; }
    }
    public float BlockedGain
    {
        get => _blockedGain;
        set { Check(); Unit(value); _blockedGain = value; InvalidateOcclusion(); }
    }
    public float BlockedHighFrequencyGain
    {
        get => _blockedHighFrequencyGain;
        set { Check(); Unit(value); _blockedHighFrequencyGain = value; InvalidateOcclusion(); }
    }
    /// <summary>Exponential time constant in unscaled seconds. Zero applies occlusion immediately.</summary>
    public float OcclusionSmoothingSeconds
    {
        get => _smoothingSeconds;
        set { Check(); Nonnegative(value); _smoothingSeconds = value; }
    }

    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        Check(); Finite(position); Finite(forward); Finite(up);
        forward = Normalize(forward); up = Normalize(up);
        Vector3 right = Vector3.Cross(forward, up);
        if (right.LengthSquared() < 1e-8f) throw new ArgumentException("Listener forward and up must not be parallel.");
        up = Vector3.Cross(right.Normalized(), forward);
        Al.SetListenerProperty(ListenerVector3.Position, position.X, position.Y, position.Z);
        float* orientation = stackalloc float[6] { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        Al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
        CheckError();
    }

    /// <summary>Loads a reusable PCM buffer. Loading is synchronous and does not use the graphics content cache.</summary>
    public AudioClip LoadWav(string path)
    {
        Check();
        using var stream = File.OpenRead(path);
        return LoadWav(stream);
    }

    /// <summary>Reads from the current stream position, leaving the caller's stream open.</summary>
    public AudioClip LoadWav(Stream stream)
    {
        Check(); ArgumentNullException.ThrowIfNull(stream);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var clip = new AudioClip(this, WaveData.Read(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        _clips.Add(clip);
        return clip;
    }

    public AudioSource CreateSource(AudioClip clip, bool spatial = true)
    {
        Check(); ArgumentNullException.ThrowIfNull(clip); clip.Check();
        if (!ReferenceEquals(clip.Owner, this)) throw new ArgumentException("The clip belongs to another audio system.", nameof(clip));
        if (spatial && clip.Channels != 1) throw new ArgumentException("Spatial audio requires a mono clip.", nameof(clip));
        var source = new AudioSource(this, clip, spatial);
        _sources.Add(source);
        return source;
    }

    /// <summary>Call after updating listener/source positions. Playback itself is mixed by OpenAL.</summary>
    public void Update(float unscaledDeltaSeconds)
    {
        Check(); Nonnegative(unscaledDeltaSeconds);
        foreach (var source in _sources) source.Update(unscaledDeltaSeconds);
        CheckError();
    }

    private void InvalidateOcclusion() { foreach (var source in _sources) source.InvalidateOcclusion(); }
    internal void Remove(AudioSource source) => _sources.Remove(source);
    internal void Remove(AudioClip clip) => _clips.Remove(clip);
    internal void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        if (_alc.GetCurrentContext() != _context && !_alc.MakeContextCurrent(_context))
            throw new InvalidOperationException("Could not activate the audio context.");
    }
    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Audio must be used on its creating game thread.");
    }
    internal void CheckError()
    {
        var error = Al.GetError();
        if (error != AudioError.NoError) throw new InvalidOperationException($"OpenAL error: {error}.");
    }
    internal static void Unit(float value)
    {
        if (!float.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value), "Expected a finite value in [0, 1].");
    }
    internal static void Nonnegative(float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Expected a finite nonnegative value.");
    }
    internal static void Positive(float value)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Expected a finite positive value.");
    }
    internal static void Finite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new ArgumentException("Expected a finite vector.");
    }
    private static Vector3 Normalize(Vector3 value)
    {
        double length = System.Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y + (double)value.Z * value.Z);
        if (length == 0) throw new ArgumentException("Listener orientation must be nonzero.");
        return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length));
    }

    public void Dispose()
    {
        if (_disposed) return;
        CheckThread();
        if (_context != null)
        {
            _alc.MakeContextCurrent(_context);
            while (_sources.Count > 0) _sources[^1].Dispose();
            while (_clips.Count > 0) _clips[^1].Dispose();
            Efx?.Dispose();
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
            _context = null;
        }
        if (_device != null) { _alc.CloseDevice(_device); _device = null; }
        Al?.Dispose(); _alc?.Dispose();
        _disposed = true;
    }
}
