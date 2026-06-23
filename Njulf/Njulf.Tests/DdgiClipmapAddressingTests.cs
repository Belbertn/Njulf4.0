using System;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public class DdgiClipmapAddressingTests
    {
        [Test]
        public void CalculatePhysicalProbeIndex_UsesAuthoritativeToroidalFormula()
        {
            var logicalCell = new DdgiClipmapCell(-7, 3, 42);
            var gridMin = new DdgiClipmapCell(-10, 2, 40);
            var ringOffset = new DdgiClipmapCell(5, 1, 6);

            int physical = DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                logicalCell,
                gridMin,
                ringOffset,
                probeCountX: 8,
                probeCountY: 4,
                probeCountZ: 8,
                physicalFirstProbeIndex: 1024);

            int relativeX = logicalCell.X - gridMin.X;
            int relativeY = logicalCell.Y - gridMin.Y;
            int relativeZ = logicalCell.Z - gridMin.Z;
            int wrappedX = PositiveModulo(relativeX + ringOffset.X, 8);
            int wrappedY = PositiveModulo(relativeY + ringOffset.Y, 4);
            int wrappedZ = PositiveModulo(relativeZ + ringOffset.Z, 8);
            int expected = 1024 + wrappedX + wrappedY * 8 + wrappedZ * 8 * 4;

            Assert.That(physical, Is.EqualTo(expected));
        }

        [Test]
        public void DecodeLogicalCellFromPhysicalProbeIndex_RoundTripsNegativeCellsAndRingOffsets()
        {
            var gridMin = new DdgiClipmapCell(-2048, -7, 4096);
            var ringOffset = new DdgiClipmapCell(14, 3, 11);
            var logicalCell = new DdgiClipmapCell(-2033, -5, 4102);

            int physical = DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                logicalCell,
                gridMin,
                ringOffset,
                probeCountX: 16,
                probeCountY: 6,
                probeCountZ: 16,
                physicalFirstProbeIndex: 77);
            DdgiClipmapCell decoded = DdgiClipmapAddressing.DecodeLogicalCellFromPhysicalProbeIndex(
                physical,
                gridMin,
                ringOffset,
                probeCountX: 16,
                probeCountY: 6,
                probeCountZ: 16,
                physicalFirstProbeIndex: 77);

            Assert.That(decoded, Is.EqualTo(logicalCell));
        }

        [Test]
        public void CalculatePhysicalProbeIndex_HandlesLargeCoordinatesWithoutOverflow()
        {
            var gridMin = new DdgiClipmapCell(int.MaxValue - 31, int.MinValue, 1024);
            var logicalCell = new DdgiClipmapCell(int.MaxValue - 2, int.MinValue + 1, 1055);

            int physical = DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                logicalCell,
                gridMin,
                new DdgiClipmapCell(31, 1, 31),
                probeCountX: 32,
                probeCountY: 2,
                probeCountZ: 32,
                physicalFirstProbeIndex: 0);

            Assert.That(physical, Is.InRange(0, 32 * 2 * 32 - 1));
        }

        [Test]
        public void PositiveModulo_HandlesLargeScrollDeltasAndProbeCountTwo()
        {
            Assert.Multiple(() =>
            {
                Assert.That(DdgiClipmapAddressing.PositiveModulo(-65, 2), Is.EqualTo(1));
                Assert.That(DdgiClipmapAddressing.PositiveModulo(65, 2), Is.EqualTo(1));
                Assert.That(DdgiClipmapAddressing.PositiveModulo(-130, 2), Is.EqualTo(0));
                Assert.That(DdgiClipmapAddressing.PositiveModulo((long)int.MinValue - 17L, 32), Is.EqualTo(15));
            });
        }

        [Test]
        public void ContainsLogicalCell_UsesHalfOpenGridBounds()
        {
            var gridMin = new DdgiClipmapCell(-4, -1, 7);

            Assert.Multiple(() =>
            {
                Assert.That(DdgiClipmapAddressing.ContainsLogicalCell(new DdgiClipmapCell(-4, -1, 7), gridMin, 4, 2, 4), Is.True);
                Assert.That(DdgiClipmapAddressing.ContainsLogicalCell(new DdgiClipmapCell(-1, 0, 10), gridMin, 4, 2, 4), Is.True);
                Assert.That(DdgiClipmapAddressing.ContainsLogicalCell(new DdgiClipmapCell(0, 0, 10), gridMin, 4, 2, 4), Is.False);
                Assert.That(DdgiClipmapAddressing.ContainsLogicalCell(new DdgiClipmapCell(-1, 1, 10), gridMin, 4, 2, 4), Is.False);
            });
        }

        private static int PositiveModulo(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }
    }
}
