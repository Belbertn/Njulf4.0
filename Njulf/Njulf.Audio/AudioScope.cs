using Njulf.Core;
using Njulf.Core.Math;

namespace Njulf.Audio;

/// <summary>Local audio lifetime borrowing an AudioSystem. Register with GameLevel (or Game) to activate playback and follow pause.</summary>
/// <remarks>Starts inactive: Play requests wait for activation. Sources are released before locally loaded clips.
/// Clips supplied to CreateSource/PlayOneShot are borrowed and must outlive their sources.</remarks>
public sealed class AudioScope : IGameModule
{
    private readonly AudioSystem _audio;
    private readonly List<AudioSource> _sources = [];
    private readonly List<AudioClip> _clips = [];
    private readonly List<AudioSource> _oneShots = [];
    private bool _active, _paused, _disposed;
    internal bool Suspended => !_active || _paused;
    internal bool BlocksPlayback(AudioPausePolicy policy) => !_active || (_paused && policy == AudioPausePolicy.FollowScope);
    internal AudioScope(AudioSystem audio) => _audio = audio;
    /// <inheritdoc />
    public GameModulePhase Phase => GameModulePhase.AudioSpatial;

    /// <summary>Loads a clip owned by this scope.</summary>
    public AudioClip LoadWav(string path)
    {
        Check();
        var clip = _audio.LoadWav(path);
        _clips.Add(clip);
        return clip;
    }
    /// <summary>Loads a locally owned clip from a borrowed stream.</summary>
    public AudioClip LoadWav(Stream stream)
    {
        Check();
        var clip = _audio.LoadWav(stream);
        _clips.Add(clip);
        return clip;
    }
    /// <summary>Creates a locally owned source. An optional position provider runs after physics, even while paused.</summary>
    public AudioSource CreateSource(AudioClip clip, bool spatial = true, Func<Vector3>? position = null)
    {
        Check();
        var source = _audio.CreateSource(clip, spatial);
        source.Scope = this;
        _sources.Add(source);
        source.RefreshSuspension();
        source.PositionProvider = position;
        return source;
    }
    /// <summary>Creates a locally owned grouped voice; clips remain borrowed.</summary>
    public AudioSource CreateSource(AudioClip clip, AudioGroup group, bool spatial = true, Func<Vector3>? position = null)
    {
        Check(); _audio.CheckGroup(group);
        var source = CreateSource(clip, spatial, position);
        try { source.Group = group; return source; }
        catch { source.Dispose(); throw; }
    }
    /// <summary>Plays a local mono one-shot, or returns false at the device's per-scope voice limit. Paused requests wait for resume.</summary>
    public bool PlayOneShot(AudioClip clip, Vector3 position, float volume = 1)
        => PlayOneShot(clip, _audio.SFX, position, volume);

    /// <summary>Queues a spatial mono one-shot in the selected group, borrowing the clip. Returns false when the bounded pool is full; paused requests wait for resume.</summary>
    public bool PlayOneShot(AudioClip clip, AudioGroup group, Vector3 position, float volume = 1)
    {
        Check();
        _audio.CheckGroup(group);
        ArgumentNullException.ThrowIfNull(clip);
        clip.Check();
        if (!ReferenceEquals(clip.Owner, _audio)) throw new ArgumentException("The clip belongs to another audio system.", nameof(clip));
        if (clip.Channels != 1) throw new ArgumentException("Spatial audio requires a mono clip.", nameof(clip));
        AudioSystem.Finite(position); AudioSystem.Unit(volume);
        ReclaimOneShots();
        var source = _oneShots.Find(s => !s.HasClip);
        if (source == null)
        {
            if (_oneShots.Count == _audio.OneShotCapacity) return false;
            source = CreateSource(clip);
            _oneShots.Add(source);
        }
        try { source.Group = group; source.PlayOneShot(clip, position, volume); }
        catch { source.DetachClip(); throw; }
        return true;
    }
    private void ReclaimOneShots()
    {
        foreach (var source in _oneShots)
            if (source.HasClip && source.State is AudioPlaybackState.Stopped or AudioPlaybackState.Initial)
                source.DetachClip();
    }
    /// <summary>Activates local playback, or suspends it while detached/preparing.</summary>
    public void SetActive(bool active, bool isSimulationPaused)
    {
        Check();
        _active = active;
        SetPause(isSimulationPaused);
    }
    private void SetPause(bool paused)
    {
        _paused = paused;
        foreach (var source in _sources) source.RefreshSuspension();
    }
    /// <summary>Applies effective pause and emitter transforms; device maintenance remains the AudioHostModule's responsibility.</summary>
    public void Update(GameModuleFrame frame)
    {
        Check();
        SetPause(frame.IsSimulationPaused);
        foreach (var source in _sources)
            if (source.PositionProvider is { } position) source.Position = position();
        ReclaimOneShots();
    }
    internal void Remove(AudioSource source)
    {
        _sources.Remove(source);
        _oneShots.Remove(source);
    }
    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audio.Check();
    }
    /// <summary>Stops/releases local sources and clips. Persistent sources and the output device remain usable.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Check();
        // Use snapshots because source disposal removes itself from the owning scope.
        List<Exception> failures = [];
        foreach (var source in _sources.ToArray())
            try { source.Dispose(); } catch (Exception e) { failures.Add(e); }
        foreach (var clip in _clips)
            try { clip.Dispose(); } catch (Exception e) { failures.Add(e); }
        if (failures.Count != 0) throw new AggregateException("Audio scope cleanup failed.", failures);
        _clips.Clear();
        _disposed = true;
        _audio.Remove(this);
    }
}
