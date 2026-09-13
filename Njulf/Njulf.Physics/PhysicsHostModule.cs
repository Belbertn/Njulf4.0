using Njulf.Core;

namespace Njulf.Physics;

/// <summary>Opt-in host adapter owning a physics world. Register once with Game or GameLevel; do not also step manually.</summary>
public sealed class PhysicsHostModule(PhysicsScene world) : IGameModule
{
    /// <summary>Borrowed world, released with this module.</summary>
    public PhysicsScene World { get; } = world ?? throw new ArgumentNullException(nameof(world));
    /// <inheritdoc />
    public GameModulePhase Phase => GameModulePhase.Physics;
    /// <inheritdoc />
    public bool RequiresFixedTimeStep => World.Mode == PhysicsMode.Simulation;
    /// <summary>Steps and delivers contacts after gameplay, using the host's full fixed interval.</summary>
    public void FixedUpdate(GameTime time)
    {
        if (RequiresFixedTimeStep) World.Step((float)time.ElapsedGameTime.TotalSeconds);
    }
    /// <summary>Keeps queries synchronized, including paused and zero-fixed-step frames.</summary>
    public void Update(GameModuleFrame frame) => World.UpdatePresentation(frame.InterpolationAlpha, frame.IsSimulationPaused);
    /// <inheritdoc />
    public void SetActive(bool active, bool isSimulationPaused)
    {
        if (!active || isSimulationPaused) World.UpdatePresentation(1, true);
    }
    /// <inheritdoc />
    public void Dispose() => World.Dispose();
}
