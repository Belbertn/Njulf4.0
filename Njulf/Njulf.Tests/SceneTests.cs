using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class SceneTests
    {
        [Test]
        public void Clear_RemovesObjectsWithoutDisposingOwnedInstances()
        {
            var scene = new Scene();
            var updateable = new DisposableUpdateable();

            scene.Add(updateable);

            scene.Clear();
            scene.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(scene.Updateables, Is.Empty);
                Assert.That(updateable.DisposeCount, Is.EqualTo(0));
            });
        }

        [Test]
        public void ClearAndDispose_DisposesOwnedInstancesOnce()
        {
            var scene = new Scene();
            var updateable = new DisposableUpdateable();

            scene.Add(updateable);
            scene.Add(updateable);

            scene.ClearAndDispose();

            Assert.Multiple(() =>
            {
                Assert.That(scene.Updateables, Is.Empty);
                Assert.That(updateable.DisposeCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void ClearAndDispose_AllowsSceneReuse()
        {
            var scene = new Scene();
            var first = new DisposableUpdateable();
            var second = new DisposableUpdateable();
            scene.Add(first);

            scene.ClearAndDispose();
            scene.Add(second);
            scene.Update(0.016f);
            scene.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(first.DisposeCount, Is.EqualTo(1));
                Assert.That(second.DisposeCount, Is.EqualTo(1));
                Assert.That(second.UpdateSequence, Is.EqualTo(1));
            });
        }

        [Test]
        public void ClearAndDispose_WhenEntityDisposalFails_RetainsFailureUntilRetry()
        {
            var scene = new Scene();
            var failing = new DisposableUpdateable { DisposeFailure = new InvalidOperationException("expected") };
            var healthy = new DisposableUpdateable();
            scene.Add(failing);
            scene.Add(healthy);

            AggregateException failure =
                Assert.Throws<AggregateException>(scene.ClearAndDispose)!;
            Assert.That(
                () => scene.Add(new DisposableUpdateable()),
                Throws.TypeOf<ObjectDisposedException>());
            failing.DisposeFailure = null;
            scene.ClearAndDispose();
            var replacement = new DisposableUpdateable();
            scene.Add(replacement);
            scene.Dispose();

            Assert.Multiple(() =>
            {
                Assert.That(failure.InnerExceptions, Has.Count.EqualTo(1));
                Assert.That(failure.InnerExceptions[0].Message, Is.EqualTo("expected"));
                Assert.That(scene.Updateables, Is.Empty);
                Assert.That(failing.DisposeCount, Is.EqualTo(2));
                Assert.That(healthy.DisposeCount, Is.EqualTo(1));
                Assert.That(replacement.DisposeCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void Dispose_PermanentlyClosesScene()
        {
            var scene = new Scene();

            scene.Dispose();
            scene.Dispose();

            Assert.Multiple(() =>
            {
                Assert.Throws<ObjectDisposedException>(
                    () => scene.Add(new DisposableUpdateable()));
                Assert.Throws<ObjectDisposedException>(scene.Clear);
                Assert.Throws<ObjectDisposedException>(() => scene.Update(0.016f));
            });
        }

        [Test]
        public void Remove_DropsOneOwnershipReferenceAtATime()
        {
            var scene = new Scene();
            var updateable = new DisposableUpdateable();

            scene.Add(updateable);
            scene.Add(updateable);
            scene.Remove(updateable);

            scene.Dispose();

            Assert.That(updateable.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Update_SortsBeforeCallingUpdate()
        {
            OrderedUpdateable.ResetSequence();
            var scene = new Scene();
            var late = new OrderedUpdateable { UpdateOrder = 20 };
            var early = new OrderedUpdateable { UpdateOrder = 10 };

            scene.Add(late);
            scene.Add(early);

            scene.Update(0.016f);

            Assert.Multiple(() =>
            {
                Assert.That(early.UpdateSequence, Is.EqualTo(1));
                Assert.That(late.UpdateSequence, Is.EqualTo(2));
            });
        }

        [Test]
        public void Scene_AddStaticInstanceBatch_StoresBatch()
        {
            var scene = new Scene();
            var batch = new StaticInstanceBatch(new[] { Matrix4x4.Identity });

            scene.Add(batch);

            Assert.That(scene.StaticInstanceBatches, Is.EquivalentTo(new[] { batch }));
        }

        [Test]
        public void Scene_Clear_RemovesStaticInstanceBatches()
        {
            var scene = new Scene();
            scene.Add(new StaticInstanceBatch(new[] { Matrix4x4.Identity }));

            scene.Clear();

            Assert.That(scene.StaticInstanceBatches, Is.Empty);
        }

        [Test]
        public void StaticInstanceBatch_RejectsNullTransformCollection()
        {
            Assert.Throws<System.ArgumentNullException>(() => new StaticInstanceBatch(null!));
        }

        [Test]
        public void StaticInstanceBatch_TracksRevisionWhenTransformsChange()
        {
            var batch = new StaticInstanceBatch(new[] { Matrix4x4.Identity });
            uint initialRevision = batch.Revision;

            batch.ReplaceWorldMatrices(new[] { Matrix4x4.CreateTranslation(new Vector3(1f, 2f, 3f)) });

            Assert.Multiple(() =>
            {
                Assert.That(batch.Revision, Is.Not.EqualTo(initialRevision));
                Assert.That(batch.WorldMatrices, Has.Count.EqualTo(1));
            });
        }

        [Test]
        public void RenderPayloadRevision_TracksGeometryAndStaticInstanceChanges()
        {
            using var scene = new Scene();
            var renderObject = new RenderObject("mesh", "material");
            ulong initialRevision = scene.RenderPayloadRevision;

            scene.Add(renderObject);
            ulong addedObjectRevision = scene.RenderPayloadRevision;
            renderObject.Position = new Vector3(1f, 2f, 3f);
            ulong movedObjectRevision = scene.RenderPayloadRevision;

            var batch = new StaticInstanceBatch(new[] { Matrix4x4.Identity });
            scene.Add(batch);
            ulong addedBatchRevision = scene.RenderPayloadRevision;
            batch.ReplaceWorldMatrices(
                new[] { Matrix4x4.CreateTranslation(new Vector3(4f, 5f, 6f)) });

            Assert.Multiple(() =>
            {
                Assert.That(addedObjectRevision, Is.GreaterThan(initialRevision));
                Assert.That(movedObjectRevision, Is.GreaterThan(addedObjectRevision));
                Assert.That(addedBatchRevision, Is.GreaterThan(movedObjectRevision));
                Assert.That(scene.RenderPayloadRevision, Is.GreaterThan(addedBatchRevision));
            });
        }

        [Test]
        public void SkinnedPayloadProperties_AdvanceSceneRenderPayloadRevision()
        {
            using var scene = new Scene();
            var renderObject = new SkinnedRenderObject("mesh", "material");
            scene.Add(renderObject);
            ulong initialRevision = scene.RenderPayloadRevision;

            renderObject.SkinningBindTransform =
                Matrix4x4.CreateTranslation(new Vector3(1f, 0f, 0f));
            ulong bindRevision = scene.RenderPayloadRevision;
            renderObject.SkinnedVertexOffset = 42;
            ulong offsetRevision = scene.RenderPayloadRevision;
            renderObject.SkinningEnabled = true;
            ulong enabledRevision = scene.RenderPayloadRevision;
            renderObject.SkinningEnabled = true;

            Assert.Multiple(() =>
            {
                Assert.That(bindRevision, Is.GreaterThan(initialRevision));
                Assert.That(offsetRevision, Is.GreaterThan(bindRevision));
                Assert.That(enabledRevision, Is.GreaterThan(offsetRevision));
                Assert.That(scene.RenderPayloadRevision, Is.EqualTo(enabledRevision));
            });
        }

        private sealed class DisposableUpdateable : IUpdateable, System.IDisposable
        {
            public bool Enabled { get; set; } = true;
            public int UpdateOrder { get; set; }
            public int DisposeCount { get; private set; }
            public int UpdateSequence { get; private set; }
            public Exception? DisposeFailure { get; set; }

            public void Update(float deltaTime)
            {
                UpdateSequence++;
            }

            public void Dispose()
            {
                DisposeCount++;
                if (DisposeFailure != null)
                    throw DisposeFailure;
            }
        }

        private sealed class OrderedUpdateable : IUpdateable
        {
            private static int _nextSequence;

            public bool Enabled { get; set; } = true;
            public int UpdateOrder { get; set; }
            public int UpdateSequence { get; private set; }

            public static void ResetSequence()
            {
                _nextSequence = 0;
            }

            public void Update(float deltaTime)
            {
                UpdateSequence = ++_nextSequence;
            }
        }
    }
}
