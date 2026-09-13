using Njulf.Core.Camera;
using Njulf.Core.Interfaces;

namespace Njulf.Framework;

public abstract partial class Game
{
    private ICameraController? _cameraController;

    /// <summary>Optional borrowed controller, updated after physics presentation and before spatial audio, including while paused.</summary>
    public ICameraController? CameraController
    {
        get => _cameraController;
        set { EnsureTimingThread(); _cameraController = value; }
    }

    /// <summary>Switches the borrowed active camera and clears its controller. Returns the previous camera for restoration.</summary>
    /// <remarks>Call on the game thread after initialization, outside Draw. Game.Camera is the active-camera lookup;
    /// the ICamera service registration remains the startup camera. Switching applies the current window aspect ratio.</remarks>
    public ICamera SetActiveCamera(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        EnsureTimingThread();
        if (_isShuttingDown || _isRenderingFrame) throw new InvalidOperationException("Switch cameras during game updates or loading, outside rendering/shutdown.");
        ICamera previous = Camera;
        camera.AspectRatio = (float)WindowWidth / WindowHeight;
        _camera = camera;
        _cameraController = null;
        return previous;
    }
}
