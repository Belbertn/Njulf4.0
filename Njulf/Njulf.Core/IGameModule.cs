using Njulf.Core.Math;

namespace Njulf.Core;

/// <summary>Ordered optional host work, after gameplay and before rendering.</summary>
public enum GameModulePhase
{
    /// <summary>Fixed physics stepping/contact delivery, and per-frame query synchronization.</summary>
    Physics,
    /// <summary>Listener/emitter transforms and scene-local playback pause.</summary>
    AudioSpatial,
    /// <summary>Unscaled audio maintenance after all spatial updates.</summary>
    AudioMaintenance
}

/// <summary>Per-host-update timing, effective pause, and the current camera's listener pose.</summary>
public readonly record struct GameModuleFrame(GameTime Time, bool IsSimulationPaused,
    Vector3 ListenerPosition, Vector3 ListenerForward, Vector3 ListenerUp)
{
    /// <summary>Existing host fixed-step interpolation fraction; one while paused or awaiting a completed step.</summary>
    public float InterpolationAlpha { get; init; } = 1;
}

/// <summary>An optional game-thread module. Successful registration transfers disposal ownership to the host or level.</summary>
/// <remarks>Callbacks run independently of gameplay overrides calling base. Do not manually drive a registered module.</remarks>
public interface IGameModule : IDisposable
{
    /// <summary>Determines per-frame callback order. Must remain constant after registration.</summary>
    GameModulePhase Phase { get; }
    /// <summary>Whether activation requires fixed simulation. Must remain constant after registration.</summary>
    bool RequiresFixedTimeStep => false;
    /// <summary>Called when registered/activated or detached. Inactive level resources must not start playback.</summary>
    void SetActive(bool active, bool isSimulationPaused) { }
    /// <summary>Physics-phase work after each gameplay FixedUpdate; never called while effectively paused.</summary>
    void FixedUpdate(GameTime time) { }
    /// <summary>Runs once after all fixed steps, including while paused. Use unscaled time for audio maintenance.</summary>
    void Update(GameModuleFrame frame) { }
}
