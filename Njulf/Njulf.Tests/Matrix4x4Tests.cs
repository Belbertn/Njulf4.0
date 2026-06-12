using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class Matrix4x4Tests
    {
        [Test]
        public void CreatePerspectiveFieldOfView_UsesReverseZDepthRange()
        {
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)System.Math.PI / 2f,
                1f,
                0.1f,
                100f);

            float nearDepth = ProjectDepth(new Vector3(0f, 0f, -0.1f), projection);
            float farDepth = ProjectDepth(new Vector3(0f, 0f, -100f), projection);

            Assert.Multiple(() =>
            {
                Assert.That(nearDepth, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(farDepth, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(nearDepth, Is.GreaterThan(farDepth));
            });
        }

        [Test]
        public void CreatePerspectiveFieldOfView_UsesVulkanFramebufferY()
        {
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)System.Math.PI / 2f,
                1f,
                0.1f,
                100f);

            Assert.That(projection.M22, Is.LessThan(0f));
        }

        [Test]
        public void CreateOrthographic_UsesVulkanFramebufferY()
        {
            Matrix4x4 projection = Matrix4x4.CreateOrthographic(
                10f,
                10f,
                0.1f,
                100f);

            Assert.That(projection.M22, Is.LessThan(0f));
        }

        [Test]
        public void CreateOrthographic_UsesReverseZDepthRange()
        {
            Matrix4x4 projection = Matrix4x4.CreateOrthographic(
                10f,
                10f,
                0.1f,
                100f);

            float nearDepth = ProjectDepth(new Vector3(0f, 0f, -0.1f), projection);
            float farDepth = ProjectDepth(new Vector3(0f, 0f, -100f), projection);

            Assert.Multiple(() =>
            {
                Assert.That(nearDepth, Is.EqualTo(1f).Within(0.0001f));
                Assert.That(farDepth, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(nearDepth, Is.GreaterThan(farDepth));
            });
        }

        [Test]
        public void CreateLookAt_UsesRowVectorViewBasis()
        {
            Vector3 eye = new(10f, 2f, -4f);
            Vector3 forward = Vector3.UnitX;
            Vector3 right = Vector3.UnitZ;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);

            Vector3 cameraOrigin = TransformPoint(eye, view);
            Vector3 pointInFront = TransformPoint(eye + forward * 3f, view);
            Vector3 pointToRight = TransformPoint(eye + right * 2f, view);

            Assert.Multiple(() =>
            {
                AssertVector(cameraOrigin, Vector3.Zero);
                AssertVector(pointInFront, new Vector3(0f, 0f, -3f));
                AssertVector(pointToRight, new Vector3(2f, 0f, 0f));
            });
        }

        [Test]
        public void Invert_RoundTripsPerspectiveProjectionPoints()
        {
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                (float)System.Math.PI / 2f,
                16f / 9f,
                0.1f,
                100f);
            Matrix4x4 inverseProjection = projection.Invert();

            AssertMatrix(projection * inverseProjection, Matrix4x4.Identity);

            AssertRoundTrip(new Vector3(0f, 0f, -0.1f), projection, inverseProjection);
            AssertRoundTrip(new Vector3(0f, 0f, -50f), projection, inverseProjection);
            AssertRoundTrip(new Vector3(0f, 0f, -100f), projection, inverseProjection);
            AssertRoundTrip(new Vector3(2f, -1f, -37.5f), projection, inverseProjection);
        }

        private static float ProjectDepth(Vector3 point, Matrix4x4 matrix)
        {
            float clipZ = point.X * matrix.M13 +
                          point.Y * matrix.M23 +
                          point.Z * matrix.M33 +
                          matrix.M43;
            float clipW = point.X * matrix.M14 +
                          point.Y * matrix.M24 +
                          point.Z * matrix.M34 +
                          matrix.M44;

            return clipZ / clipW;
        }

        private static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix) =>
            point * matrix;

        private static Vector4 Transform(Vector4 point, Matrix4x4 matrix) => new(
            point.X * matrix.M11 + point.Y * matrix.M21 + point.Z * matrix.M31 + point.W * matrix.M41,
            point.X * matrix.M12 + point.Y * matrix.M22 + point.Z * matrix.M32 + point.W * matrix.M42,
            point.X * matrix.M13 + point.Y * matrix.M23 + point.Z * matrix.M33 + point.W * matrix.M43,
            point.X * matrix.M14 + point.Y * matrix.M24 + point.Z * matrix.M34 + point.W * matrix.M44);

        private static void AssertRoundTrip(Vector3 point, Matrix4x4 projection, Matrix4x4 inverseProjection)
        {
            Vector4 clip = Transform(new Vector4(point, 1f), projection);
            Vector4 reconstructed = Transform(clip, inverseProjection);
            Vector3 actual = new(
                reconstructed.X / reconstructed.W,
                reconstructed.Y / reconstructed.W,
                reconstructed.Z / reconstructed.W);

            AssertVector(actual, point);
        }

        private static void AssertMatrix(Matrix4x4 actual, Matrix4x4 expected)
        {
            Assert.That(actual.M11, Is.EqualTo(expected.M11).Within(0.0001f));
            Assert.That(actual.M12, Is.EqualTo(expected.M12).Within(0.0001f));
            Assert.That(actual.M13, Is.EqualTo(expected.M13).Within(0.0001f));
            Assert.That(actual.M14, Is.EqualTo(expected.M14).Within(0.0001f));
            Assert.That(actual.M21, Is.EqualTo(expected.M21).Within(0.0001f));
            Assert.That(actual.M22, Is.EqualTo(expected.M22).Within(0.0001f));
            Assert.That(actual.M23, Is.EqualTo(expected.M23).Within(0.0001f));
            Assert.That(actual.M24, Is.EqualTo(expected.M24).Within(0.0001f));
            Assert.That(actual.M31, Is.EqualTo(expected.M31).Within(0.0001f));
            Assert.That(actual.M32, Is.EqualTo(expected.M32).Within(0.0001f));
            Assert.That(actual.M33, Is.EqualTo(expected.M33).Within(0.0001f));
            Assert.That(actual.M34, Is.EqualTo(expected.M34).Within(0.0001f));
            Assert.That(actual.M41, Is.EqualTo(expected.M41).Within(0.0001f));
            Assert.That(actual.M42, Is.EqualTo(expected.M42).Within(0.0001f));
            Assert.That(actual.M43, Is.EqualTo(expected.M43).Within(0.0001f));
            Assert.That(actual.M44, Is.EqualTo(expected.M44).Within(0.0001f));
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.0001f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.0001f));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(0.0001f));
        }
    }
}
