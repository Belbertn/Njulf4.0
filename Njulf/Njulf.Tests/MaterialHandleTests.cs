using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class MaterialHandleTests
    {
        [Test]
        public void InvalidHandle_IsNotValid()
        {
            Assert.That(MaterialHandle.Invalid.IsValid, Is.False);
        }

        [Test]
        public void PositiveIndexAndGeneration_IsValid()
        {
            var handle = new MaterialHandle(3, 7);

            Assert.That(handle.IsValid, Is.True);
        }

        [Test]
        public void Equality_UsesIndexAndGeneration()
        {
            var first = new MaterialHandle(2, 5);
            var same = new MaterialHandle(2, 5);
            var stale = new MaterialHandle(2, 6);

            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(same));
                Assert.That(first == same, Is.True);
                Assert.That(first, Is.Not.EqualTo(stale));
                Assert.That(first != stale, Is.True);
                Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            });
        }
    }
}
