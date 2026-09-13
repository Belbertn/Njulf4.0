using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Core.Camera;

/// <summary>Game-thread camera placement, independent of input bindings. The camera and target are borrowed.</summary>
public interface ICameraController
{
    void Update(ICamera camera, GameTime time);
}

/// <summary>Follows a node's world position with world-space camera and look-target offsets; no smoothing.</summary>
public sealed class FollowCameraController(SceneNode target) : ICameraController
{
    public SceneNode Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
    public Vector3 Offset { get; set; } = new(0, 2, 5);
    public Vector3 LookTargetOffset { get; set; }
    public void Update(ICamera camera, GameTime time)
    {
        camera.Position = Target.WorldPosition + Offset;
        camera.LookAt(Target.WorldPosition + LookTargetOffset, Vector3.UnitY);
    }
}

/// <summary>Orbits a node's world position. Angles are radians; zero yaw places the camera on +Z.</summary>
public sealed class OrbitCameraController(SceneNode target) : ICameraController
{
    private float _pitch, _distance = 5;
    public SceneNode Target { get; } = target ?? throw new ArgumentNullException(nameof(target));
    public Vector3 LookTargetOffset { get; set; }
    public float Yaw { get; set; }
    public float Pitch
    {
        get => _pitch;
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _pitch = System.Math.Clamp(value, -System.MathF.PI / 2 + .001f, System.MathF.PI / 2 - .001f);
        }
    }
    public float Distance
    {
        get => _distance;
        set
        {
            if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            _distance = value;
        }
    }
    public void Update(ICamera camera, GameTime time)
    {
        Vector3 targetPosition = Target.WorldPosition + LookTargetOffset;
        float horizontal = Distance * System.MathF.Cos(Pitch);
        camera.Position = targetPosition + new Vector3(horizontal * System.MathF.Sin(Yaw), Distance * System.MathF.Sin(Pitch), horizontal * System.MathF.Cos(Yaw));
        camera.LookAt(targetPosition, Vector3.UnitY);
    }
}
