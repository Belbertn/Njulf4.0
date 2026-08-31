using System;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// One committed, cell-aligned toroidal ring movement.  Deltas use the
/// current-origin to next-origin convention; exposed-cell tests are symmetric
/// with respect to direction.
/// </summary>
internal readonly record struct SimpleDdgiScrollStep(
    int DeltaX,
    int DeltaY,
    int DeltaZ,
    int ExposedProbeCount,
    int BootstrapRaysPerProbe,
    ulong ReservedPrimaryRays,
    bool EmergencyRebase = false)
{
    public bool HasMovement => DeltaX != 0 || DeltaY != 0 || DeltaZ != 0;
}

internal readonly record struct SimpleDdgiExposedCell(int X, int Y, int Z);

/// <summary>
/// Allocation-free enumeration of the unique logical cells exposed by a
/// toroidal movement.  Iterating the bounded lattice and applying the exact
/// overlap predicate keeps corner/edge cells unique for diagonal movement.
/// Deltas follow <c>old origin - new origin</c>, matching
/// <see cref="SimpleDdgiVolumeManager.TryResolveCellDelta"/>.
/// </summary>
internal readonly struct SimpleDdgiExposedCells
{
    private readonly int _countX;
    private readonly int _countY;
    private readonly int _countZ;
    private readonly int _deltaX;
    private readonly int _deltaY;
    private readonly int _deltaZ;

    public SimpleDdgiExposedCells(
        int countX,
        int countY,
        int countZ,
        int deltaX,
        int deltaY,
        int deltaZ)
    {
        _countX = Math.Max(0, countX);
        _countY = Math.Max(0, countY);
        _countZ = Math.Max(0, countZ);
        _deltaX = deltaX;
        _deltaY = deltaY;
        _deltaZ = deltaZ;
    }

    public Enumerator GetEnumerator() => new(
        _countX,
        _countY,
        _countZ,
        _deltaX,
        _deltaY,
        _deltaZ);

    internal struct Enumerator
    {
        private readonly int _countX;
        private readonly int _countY;
        private readonly int _countZ;
        private readonly int _deltaX;
        private readonly int _deltaY;
        private readonly int _deltaZ;
        private readonly int _total;
        private int _linearIndex;

        public Enumerator(
            int countX,
            int countY,
            int countZ,
            int deltaX,
            int deltaY,
            int deltaZ)
        {
            _countX = countX;
            _countY = countY;
            _countZ = countZ;
            _deltaX = deltaX;
            _deltaY = deltaY;
            _deltaZ = deltaZ;
            _total = checked(countX * countY * countZ);
            _linearIndex = -1;
            Current = default;
        }

        public SimpleDdgiExposedCell Current { get; private set; }

        public bool MoveNext()
        {
            while (++_linearIndex < _total)
            {
                int plane = _countX * _countY;
                int z = _linearIndex / plane;
                int remainder = _linearIndex - z * plane;
                int y = remainder / _countX;
                int x = remainder - y * _countX;
                if (!SimpleDdgiScrollPlanner.IsExposedCell(
                        x,
                        y,
                        z,
                        _countX,
                        _countY,
                        _countZ,
                        _deltaX,
                        _deltaY,
                        _deltaZ))
                {
                    continue;
                }

                Current = new SimpleDdgiExposedCell(x, y, z);
                return true;
            }

            return false;
        }
    }
}

/// <summary>
/// Pure, deterministic budget planner for ordinary camera-relative DDGI ring
/// scrolling.  A normal transaction advances no more than one cell per active
/// axis and is admitted only when the complete exposed cohort fits.
/// </summary>
internal static class SimpleDdgiScrollPlanner
{
    public const int MaximumCommittedCascadesPerFrame = 2;
    // Transport V2 normally admits a 16k-ray source envelope and explicitly
    // grants spatial recovery a second envelope.  Keeping scroll planning on
    // that same bound prevents camera movement from becoming a hidden spike.
    public const ulong MaximumSpatialRecoveryPrimaryRays = 32_768UL;

    public static int CountExposedProbes(
        int countX,
        int countY,
        int countZ,
        int deltaX,
        int deltaY,
        int deltaZ)
    {
        int safeX = Math.Max(0, countX);
        int safeY = Math.Max(0, countY);
        int safeZ = Math.Max(0, countZ);
        long total = (long)safeX * safeY * safeZ;
        long overlapX = Math.Max(0L, (long)safeX - Math.Abs((long)deltaX));
        long overlapY = Math.Max(0L, (long)safeY - Math.Abs((long)deltaY));
        long overlapZ = Math.Max(0L, (long)safeZ - Math.Abs((long)deltaZ));
        long exposed = total - overlapX * overlapY * overlapZ;
        return checked((int)Math.Clamp(exposed, 0L, int.MaxValue));
    }

    public static bool IsExposedCell(
        int x,
        int y,
        int z,
        int countX,
        int countY,
        int countZ,
        int deltaX,
        int deltaY,
        int deltaZ)
    {
        int oldX = x - deltaX;
        int oldY = y - deltaY;
        int oldZ = z - deltaZ;
        return oldX < 0 || oldX >= countX ||
               oldY < 0 || oldY >= countY ||
               oldZ < 0 || oldZ >= countZ;
    }

    public static bool HasAnyOverlap(
        int countX,
        int countY,
        int countZ,
        int deltaX,
        int deltaY,
        int deltaZ) =>
        Math.Abs((long)deltaX) < Math.Max(0, countX) &&
        Math.Abs((long)deltaY) < Math.Max(0, countY) &&
        Math.Abs((long)deltaZ) < Math.Max(0, countZ);

    public static bool TryPlanIncrementalStep(
        int desiredDeltaX,
        int desiredDeltaY,
        int desiredDeltaZ,
        int countX,
        int countY,
        int countZ,
        int maintenanceRaysPerProbe,
        int fullRaysPerProbe,
        ReadOnlySpan<uint> frameRayBuckets,
        int availableProbeRequests,
        ulong availablePrimaryRays,
        out SimpleDdgiScrollStep step)
    {
        step = default;
        if ((desiredDeltaX == 0 && desiredDeltaY == 0 && desiredDeltaZ == 0) ||
            countX <= 0 || countY <= 0 || countZ <= 0 ||
            fullRaysPerProbe <= 0 || availableProbeRequests <= 0 ||
            availablePrimaryRays == 0UL)
        {
            return false;
        }

        int unitX = Math.Sign(desiredDeltaX);
        int unitY = Math.Sign(desiredDeltaY);
        int unitZ = Math.Sign(desiredDeltaZ);
        int maximumRays = Math.Clamp(
            fullRaysPerProbe,
            1,
            GlobalIlluminationSettings.MaxSimpleDdgiRaysPerProbe);
        int minimumRays = Math.Clamp(
            maintenanceRaysPerProbe,
            1,
            maximumRays);

        bool found = false;
        int bestAxisCount = -1;
        long bestPressure = long.MinValue;
        int bestExposed = int.MaxValue;
        int bestMask = int.MaxValue;
        for (int mask = 1; mask < 8; mask++)
        {
            int deltaX = (mask & 1) != 0 ? unitX : 0;
            int deltaY = (mask & 2) != 0 ? unitY : 0;
            int deltaZ = (mask & 4) != 0 ? unitZ : 0;
            if (((mask & 1) != 0 && unitX == 0) ||
                ((mask & 2) != 0 && unitY == 0) ||
                ((mask & 4) != 0 && unitZ == 0))
            {
                continue;
            }

            int exposed = CountExposedProbes(
                countX,
                countY,
                countZ,
                deltaX,
                deltaY,
                deltaZ);
            ulong minimumCost = checked((ulong)exposed * (ulong)minimumRays);
            if (exposed <= 0 || exposed > availableProbeRequests ||
                minimumCost > availablePrimaryRays)
            {
                continue;
            }

            int axisCount = ((mask & 1) != 0 ? 1 : 0) +
                            ((mask & 2) != 0 ? 1 : 0) +
                            ((mask & 4) != 0 ? 1 : 0);
            long pressure = ((mask & 1) != 0 ? Math.Abs((long)desiredDeltaX) : 0L) +
                            ((mask & 2) != 0 ? Math.Abs((long)desiredDeltaY) : 0L) +
                            ((mask & 4) != 0 ? Math.Abs((long)desiredDeltaZ) : 0L);
            if (found &&
                (axisCount < bestAxisCount ||
                 (axisCount == bestAxisCount && pressure < bestPressure) ||
                 (axisCount == bestAxisCount && pressure == bestPressure && exposed > bestExposed) ||
                 (axisCount == bestAxisCount && pressure == bestPressure && exposed == bestExposed && mask >= bestMask)))
            {
                continue;
            }

            if (!SimpleDdgiRayBucketPolicy.TrySelectHighest(
                    frameRayBuckets,
                    minimumRays,
                    maximumRays,
                    availablePrimaryRays / (ulong)exposed,
                    out int bootstrapRays))
            {
                continue;
            }

            found = true;
            bestAxisCount = axisCount;
            bestPressure = pressure;
            bestExposed = exposed;
            bestMask = mask;
            step = new SimpleDdgiScrollStep(
                deltaX,
                deltaY,
                deltaZ,
                exposed,
                bootstrapRays,
                checked((ulong)exposed * (ulong)bootstrapRays));
        }

        return found;
    }
}
