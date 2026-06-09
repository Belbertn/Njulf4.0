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

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That(actual.X, Is.EqualTo(expected.X).Within(0.0001f));
            Assert.That(actual.Y, Is.EqualTo(expected.Y).Within(0.0001f));
            Assert.That(actual.Z, Is.EqualTo(expected.Z).Within(0.0001f));
        }
    }
}
