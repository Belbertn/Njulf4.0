using Njulf.Core.Math;
using Njulf.Rendering.Debug;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DebugDrawListTests
    {
        [Test]
        public void BoxProducesTwelveLines()
        {
            var drawList = new DebugDrawList { Enabled = true };

            drawList.Box(new BoundingBox(new Vector3(-1f), new Vector3(1f)), Vector4.One);

            DebugDrawFrameSnapshot snapshot = drawList.Snapshot();
            Assert.That(snapshot.LineCount, Is.EqualTo(12));
            Assert.That(snapshot.DroppedLineCount, Is.EqualTo(0));
        }

        [Test]
        public void SphereClampsInvalidSegments()
        {
            var drawList = new DebugDrawList { Enabled = true };

            drawList.Sphere(Vector3.Zero, 1.0f, Vector4.One, segments: 1);

            Assert.That(drawList.Snapshot().LineCount, Is.EqualTo(24));
        }

        [Test]
        public void RespectsLineBudget()
        {
            var drawList = new DebugDrawList
            {
                Enabled = true,
                MaxLineSegments = 2
            };

            drawList.Line(Vector3.Zero, Vector3.UnitX, Vector4.One);
            drawList.Line(Vector3.Zero, Vector3.UnitY, Vector4.One);
            drawList.Line(Vector3.Zero, Vector3.UnitZ, Vector4.One);

            DebugDrawFrameSnapshot snapshot = drawList.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.LineCount, Is.EqualTo(2));
                Assert.That(snapshot.DroppedLineCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void ClearFrameKeepsPersistentLines()
        {
            var drawList = new DebugDrawList { Enabled = true };
            drawList.Line(Vector3.Zero, Vector3.UnitX, Vector4.One);
            drawList.Line(Vector3.Zero, Vector3.UnitY, Vector4.One, lifetime: DebugDrawLifetime.Persistent);

            drawList.ClearFrame();

            DebugDrawFrameSnapshot snapshot = drawList.Snapshot();
            Assert.Multiple(() =>
            {
                Assert.That(snapshot.LineCount, Is.EqualTo(1));
                Assert.That(snapshot.PersistentLineCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void ClearPersistentRemovesPersistentLines()
        {
            var drawList = new DebugDrawList { Enabled = true };
            drawList.Line(Vector3.Zero, Vector3.UnitY, Vector4.One, lifetime: DebugDrawLifetime.Persistent);

            drawList.ClearPersistent();

            Assert.That(drawList.Snapshot().LineCount, Is.EqualTo(0));
        }

        [Test]
        public void FrustumRejectsNonInvertibleMatrix()
        {
            var drawList = new DebugDrawList { Enabled = true };

            drawList.Frustum(Matrix4x4.Zero, Vector4.One);

            Assert.That(drawList.Snapshot().LineCount, Is.EqualTo(0));
        }

        [Test]
        public void DisabledSnapshotSuppressesFrameLinesButReportsPersistentCount()
        {
            var drawList = new DebugDrawList { Enabled = true };
            drawList.Line(Vector3.Zero, Vector3.UnitY, Vector4.One, lifetime: DebugDrawLifetime.Persistent);
            drawList.Enabled = false;

            DebugDrawFrameSnapshot snapshot = drawList.Snapshot();

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.LineCount, Is.EqualTo(0));
                Assert.That(snapshot.PersistentLineCount, Is.EqualTo(1));
            });
        }
    }
}
