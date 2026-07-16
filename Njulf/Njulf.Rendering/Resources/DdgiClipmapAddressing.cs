using System;

namespace Njulf.Rendering.Resources
{
    public readonly record struct DdgiClipmapCell(int X, int Y, int Z)
    {
        public static DdgiClipmapCell Zero => new(0, 0, 0);

        public static DdgiClipmapCell operator +(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static DdgiClipmapCell operator -(DdgiClipmapCell left, DdgiClipmapCell right) =>
            new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
    }

    public static class DdgiClipmapAddressing
    {
        public static int CalculatePhysicalProbeIndex(
            DdgiClipmapCell logicalCell,
            DdgiClipmapCell gridMinCell,
            DdgiClipmapCell ringOffset,
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            int physicalFirstProbeIndex)
        {
            int localIndex = CalculateLocalPhysicalProbeIndex(
                logicalCell,
                gridMinCell,
                ringOffset,
                probeCountX,
                probeCountY,
                probeCountZ);

            return checked(physicalFirstProbeIndex + localIndex);
        }

        public static int CalculateLocalPhysicalProbeIndex(
            DdgiClipmapCell logicalCell,
            DdgiClipmapCell gridMinCell,
            DdgiClipmapCell ringOffset,
            int probeCountX,
            int probeCountY,
            int probeCountZ)
        {
            ValidateProbeCounts(probeCountX, probeCountY, probeCountZ);

            long relativeX = (long)logicalCell.X - gridMinCell.X;
            long relativeY = (long)logicalCell.Y - gridMinCell.Y;
            long relativeZ = (long)logicalCell.Z - gridMinCell.Z;
            int wrappedX = PositiveModulo(relativeX + ringOffset.X, probeCountX);
            int wrappedY = PositiveModulo(relativeY + ringOffset.Y, probeCountY);
            int wrappedZ = PositiveModulo(relativeZ + ringOffset.Z, probeCountZ);

            return checked(wrappedX + wrappedY * probeCountX + wrappedZ * probeCountX * probeCountY);
        }

        public static DdgiClipmapCell DecodeLogicalCellFromPhysicalProbeIndex(
            int physicalProbeIndex,
            DdgiClipmapCell gridMinCell,
            DdgiClipmapCell ringOffset,
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            int physicalFirstProbeIndex)
        {
            ValidateProbeCounts(probeCountX, probeCountY, probeCountZ);
            int localIndex = physicalProbeIndex - physicalFirstProbeIndex;
            int probeCount = checked(probeCountX * probeCountY * probeCountZ);
            if ((uint)localIndex >= (uint)probeCount)
                throw new ArgumentOutOfRangeException(nameof(physicalProbeIndex), "The physical DDGI probe index is outside this clipmap volume.");

            int wrappedX = localIndex % probeCountX;
            int wrappedY = (localIndex / probeCountX) % probeCountY;
            int wrappedZ = localIndex / (probeCountX * probeCountY);
            return new DdgiClipmapCell(
                gridMinCell.X + PositiveModulo(wrappedX - ringOffset.X, probeCountX),
                gridMinCell.Y + PositiveModulo(wrappedY - ringOffset.Y, probeCountY),
                gridMinCell.Z + PositiveModulo(wrappedZ - ringOffset.Z, probeCountZ));
        }

        public static bool ContainsLogicalCell(
            DdgiClipmapCell logicalCell,
            DdgiClipmapCell gridMinCell,
            int probeCountX,
            int probeCountY,
            int probeCountZ)
        {
            ValidateProbeCounts(probeCountX, probeCountY, probeCountZ);
            return logicalCell.X >= gridMinCell.X &&
                logicalCell.Y >= gridMinCell.Y &&
                logicalCell.Z >= gridMinCell.Z &&
                logicalCell.X < gridMinCell.X + probeCountX &&
                logicalCell.Y < gridMinCell.Y + probeCountY &&
                logicalCell.Z < gridMinCell.Z + probeCountZ;
        }

        public static int PositiveModulo(int value, int divisor) => PositiveModulo((long)value, divisor);

        public static int PositiveModulo(long value, int divisor)
        {
            if (divisor <= 0)
                throw new ArgumentOutOfRangeException(nameof(divisor));

            long result = value % divisor;
            return (int)(result < 0 ? result + divisor : result);
        }

        private static void ValidateProbeCounts(int probeCountX, int probeCountY, int probeCountZ)
        {
            if (probeCountX <= 0)
                throw new ArgumentOutOfRangeException(nameof(probeCountX));
            if (probeCountY <= 0)
                throw new ArgumentOutOfRangeException(nameof(probeCountY));
            if (probeCountZ <= 0)
                throw new ArgumentOutOfRangeException(nameof(probeCountZ));
        }
    }
}
