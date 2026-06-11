using Njulf.Core.Interfaces;
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

        private sealed class DisposableUpdateable : IUpdateable, System.IDisposable
        {
            public bool Enabled { get; set; } = true;
            public int UpdateOrder { get; set; }
            public int DisposeCount { get; private set; }

            public void Update(float deltaTime)
            {
            }

            public void Dispose()
            {
                DisposeCount++;
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
