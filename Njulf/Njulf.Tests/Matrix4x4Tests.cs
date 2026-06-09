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
    }
}
