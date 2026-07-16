using Njulf.Core.Camera;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class FirstPersonCameraTests
    {
        [Test]
        public void RotateYawPitch_UpdatesViewMatrixInPlace()
        {
            Vector3 position = new(0f, 1.25f, 5.5f);
            var camera = new FirstPersonCamera(position);

            _ = camera.ViewMatrix;

            camera.RotateYawPitch(System.MathF.PI * 0.5f, 0f);

            Matrix4x4 view = camera.ViewMatrix;
            Vector3 cameraOrigin = TransformPoint(position, view);
            Vector3 pointInFront = TransformPoint(position + camera.Forward, view);

            Assert.Multiple(() =>
            {
                AssertVector(cameraOrigin, Vector3.Zero);
                AssertVector(pointInFront, new Vector3(0f, 0f, -1f));
            });
        }

        private static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix) =>
            point * matrix;

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.0001f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.0001f));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(0.0001f));
        }
    }
}
