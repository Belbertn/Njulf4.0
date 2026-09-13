using Microsoft.Extensions.DependencyInjection;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering;

namespace Njulf.ApiExamples;

// A finite sequence of gameplay, paused menu orbit, scripted cutscene, and restored gameplay.
internal sealed class CameraExample(ExampleOptions options) : ExampleGame(options)
{
    private SceneNode _target = null!;
    private ICamera _gameplay = null!;
    private ICamera? _lastRenderedCamera;
    private FollowCameraController _follow = null!;
    private OrbitCameraController? _orbit;
    private int _stage = -1, _verifiedCuts;
    private ulong _previousCutSerial;
    private bool _awaitingCut;
    private VulkanRenderer Native => Services.GetRequiredService<VulkanRenderer>();

    protected override void Initialize() => RegisterModule(new ListenerObserver(this));

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        ModelInstance instance = await Content.LoadModelInstanceAsync(Scene, "Assets/tetrahedron.gltf", cancellationToken: cancellationToken);
        _target = instance.PlacementRoot;
        _gameplay = Camera;
        _follow = new(_target) { Offset = new(0, 0, 5) };
        CameraController = _follow;
    }

    protected override void Update(GameTime time)
    {
        base.Update(time);
        int next = System.Math.Min(QualityFrames / 30, 3);
        if (next != _stage)
        {
            if (next > 0)
            {
                _previousCutSerial = Native.LastDiagnostics.CaptureCamera.CameraCutSerial;
                _awaitingCut = true;
            }
            switch (next)
            {
                case 1:
                    // Start at the gameplay pose: even an identical-pose replacement is a camera cut.
                    _gameplay = SetActiveCamera(new FirstPersonCamera(Camera.Position));
                    CameraController = _orbit = new(_target) { Distance = 5 };
                    IsPaused = true;
                    if (Options.Frames > 0) Window.Size = new(800, 600);
                    break;
                case 2:
                    SetActiveCamera(new FirstPersonCamera(Camera.Position));
                    IsPaused = false;
                    break;
                case 3:
                    SetActiveCamera(_gameplay);
                    CameraController = _follow;
                    break;
            }
            _stage = next;
            Console.WriteLine($"Camera stage: {new[] { "gameplay follow", "paused menu orbit", "scripted cutscene", "restored gameplay" }[next]}");
        }
        else if (_stage == 1)
            _orbit!.Yaw += (float)time.UnscaledElapsedGameTime.TotalSeconds * .3f;

        if (_stage == 2)
        {
            Camera.Position += new Vector3((float)time.ElapsedGameTime.TotalSeconds * .1f, 0, 0);
            Camera.LookAt(_target.WorldPosition, Vector3.UnitY);
        }
    }

    protected override void Draw(GameTime time)
    {
        base.Draw(time);
        _lastRenderedCamera = Camera;
        if (System.MathF.Abs(Camera.AspectRatio - (float)WindowWidth / WindowHeight) > .0001f)
            throw new InvalidOperationException("The active camera did not track window size.");
        if (_awaitingCut)
        {
            if (Native.LastDiagnostics.CaptureCamera.CameraCutSerial <= _previousCutSerial)
                throw new InvalidOperationException("Camera replacement did not invalidate temporal history.");
            _verifiedCuts++;
            _awaitingCut = false;
        }
    }

    public void ValidateCameraCompletion()
    {
        if (Options.Frames >= 120 && (_stage != 3 || _verifiedCuts != 3 || !ReferenceEquals(_lastRenderedCamera, _gameplay)))
            throw new InvalidOperationException("Camera transition sequence did not complete.");
        Console.WriteLine($"Camera switches verified: {_verifiedCuts}; active aspect ratio and spatial listener verified.");
    }

    private sealed class ListenerObserver(CameraExample game) : IGameModule
    {
        public GameModulePhase Phase => GameModulePhase.AudioSpatial;
        public void Update(GameModuleFrame frame)
        {
            if (frame.ListenerPosition != game.Camera.Position || frame.ListenerForward != game.Camera.Forward || frame.ListenerUp != game.Camera.Up)
                throw new InvalidOperationException("Spatial audio did not receive the controlled active camera pose.");
        }
        public void Dispose() { }
    }
}
