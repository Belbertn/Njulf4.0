using Njulf.Core;
using Njulf.Core.Math;

namespace Njulf.Audio;

/// <summary>World-space listener position and forward/up directions, using the engine's Y-up, negative-Z-forward convention.</summary>
public readonly record struct AudioListenerPose(Vector3 Position, Vector3 Forward, Vector3 Up);

/// <summary>Owns the audio device and performs unscaled maintenance. Register once with Game; create scopes for local audio.</summary>
public sealed class AudioHostModule(AudioSystem audio) : IGameModule
{
    /// <summary>Borrowed device. Directly created sources (for example music) outlive levels and ignore host pause.</summary>
    public AudioSystem Audio { get; } = audio ?? throw new ArgumentNullException(nameof(audio));
    /// <summary>Optional world-space listener pose. Null follows the current camera.</summary>
    public Func<AudioListenerPose>? ListenerProvider { get; set; }
    /// <inheritdoc />
    public GameModulePhase Phase => GameModulePhase.AudioMaintenance;
    /// <summary>Updates the listener and maintains voices after emitter transforms have been published.</summary>
    public void Update(GameModuleFrame frame)
    {
        var pose = ListenerProvider?.Invoke() ?? new(frame.ListenerPosition, frame.ListenerForward, frame.ListenerUp);
        Audio.SetListener(pose.Position, pose.Forward, pose.Up);
        Audio.Update((float)frame.Time.UnscaledElapsedGameTime.TotalSeconds);
    }
    /// <inheritdoc />
    public void Dispose() { ListenerProvider = null; Audio.Dispose(); }
}
