using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed class CameraControllerTests
{
    [TestCase(false), TestCase(true)]
    public void LookAtSurvivesUpdateResizeAndNativeRotation(bool orbit)
    {
        ICamera camera = orbit ? new OrbitCamera() : new FirstPersonCamera();
        camera.Position = new(2, 3, 6);
        Vector3 target = new(-1, 1, 0);
        camera.LookAt(target, new(.2f, 1, .1f));
        Vector3 forward = (target - camera.Position).Normalized();
        Vector3 right = camera.Right;
        camera.Update();
        camera.AspectRatio = 1.25f;
        camera.FieldOfView = .7f;
        if (camera is FirstPersonCamera first) first.RotateYawPitch(0, 0);
        else ((OrbitCamera)camera).Rotate(0, 0);
        AssertVector(camera.Forward, forward);
        AssertVector(camera.Right, right);
        AssertVector(camera.Position * camera.ViewMatrix, Vector3.Zero);
        AssertVector((camera.Position + camera.Forward) * camera.ViewMatrix, new(0, 0, -1));
        AssertVector((camera.Position + camera.Right) * camera.ViewMatrix, Vector3.UnitX);
        Assert.That(camera.ViewProjectionMatrix.Equals(camera.ViewMatrix * camera.ProjectionMatrix), Is.True);
    }

    [TestCase(false), TestCase(true)]
    public void ControllersFollowParentedTargetsAndSwitchWithoutCasts(bool orbitCamera)
    {
        ICamera camera = orbitCamera ? new OrbitCamera() : new FirstPersonCamera();
        var parent = new SceneNode { Position = new(10, 0, 0) };
        var target = new SceneNode { Position = new(1, 0, 0) };
        target.SetParent(parent, false);
        ICameraController follow = new FollowCameraController(target) { Offset = new(0, 0, 5) };
        follow.Update(camera, default);
        AssertVector(camera.Position, new(11, 0, 5));
        AssertVector(camera.Forward, -Vector3.UnitZ);
        parent.Position = new(20, 0, 0);
        ICameraController orbit = new OrbitCameraController(target) { Yaw = MathF.PI / 2, Distance = 4 };
        orbit.Update(camera, default);
        AssertVector(camera.Position, new(25, 0, 0));
        AssertVector(camera.Forward, -Vector3.UnitX);
        follow.Update(camera, default);
        AssertVector(camera.Position, new(21, 0, 5));
    }

    private static void AssertVector(Vector3 actual, Vector3 expected) =>
        Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.0001f));
}
