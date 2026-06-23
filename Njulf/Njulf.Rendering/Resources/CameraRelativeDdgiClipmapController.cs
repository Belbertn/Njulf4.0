using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public enum DdgiClipmapDirtyReason : uint
    {
        InitialActivation = 0,
        Scroll = 1,
        LargeScroll = 2,
        Teleport = 3,
        LayoutChange = 4,
        DirtyBounds = 5
    }

    public enum DdgiCameraMovementClass : uint
    {
        None = 0,
        LayoutChanged = 1,
        FirstActivation = 2,
        Normal = 3,
        Fast = 4,
        Teleport = 5,
        ViewResetOnly = 6
    }

    public readonly record struct DdgiClipmapDirtyRegion(
        DdgiClipmapCell MinCell,
        DdgiClipmapCell MaxCell,
        DdgiClipmapDirtyReason Reason);

    public readonly record struct DdgiClipmapCellState(
        bool Initialized,
        ulong AgeFrames,
        ulong LastUpdateFrame,
        int PhysicalProbeIndex);

    public readonly record struct CameraRelativeDdgiClipmapUpdateResult(
        bool LayoutChanged,
        bool FirstActivation,
        bool TeleportReset,
        bool CameraCutReset,
        int DirtyProbeCount,
        DdgiCameraMovementClass MovementClass,
        bool FastMovement,
        bool ViewPriorityHistoryReset);

    public sealed class CameraRelativeDdgiClipmapController
    {
        private readonly List<DdgiClipmapCascadeState> _cascades = new();
        private ulong _layoutSignature;
        private bool _hasLayoutSignature;
        private Vector3 _previousCameraPosition;
        private bool _hasPreviousCameraPosition;

        public IReadOnlyList<DdgiClipmapCascadeState> Cascades => _cascades;
        public int TotalProbeCount { get; private set; }
        public bool WarmUpActive { get; private set; }

        public CameraRelativeDdgiClipmapUpdateResult Update(
            Vector3 cameraPosition,
            ulong frameIndex,
            GlobalIlluminationSettings settings,
            bool cameraCut = false)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            ulong layoutSignature = CreateLayoutSignature(settings);
            bool layoutChanged = !_hasLayoutSignature || layoutSignature != _layoutSignature;
            if (layoutChanged)
            {
                RebuildLayout(settings, layoutSignature);
            }

            bool firstActivation = !_hasPreviousCameraPosition;
            float cameraDelta = firstActivation ? 0.0f : Vector3.Distance(_previousCameraPosition, cameraPosition);
            bool teleport = !firstActivation &&
                cameraDelta > settings.DdgiTeleportResetDistance;
            bool cameraCutReset = cameraCut && settings.DdgiCameraCutResetEnabled;
            bool fastMovement = !layoutChanged &&
                !firstActivation &&
                !teleport &&
                IsFastMovement(cameraDelta, settings);
            DdgiCameraMovementClass movementClass = ResolveMovementClass(
                layoutChanged,
                firstActivation,
                teleport,
                fastMovement,
                cameraCutReset);
            int dirtyProbeCount = 0;

            for (int i = 0; i < _cascades.Count; i++)
            {
                DdgiClipmapCascadeState cascade = _cascades[i];
                cascade.BeginFrame(frameIndex);

                Vector3 clipmapCenter = cameraPosition;
                clipmapCenter.Y += settings.DdgiClipmapVerticalCenterOffset;
                DdgiClipmapCell nextGridMin = CalculateCenteredGridMinimum(
                    clipmapCenter,
                    cascade.ProbeSpacing,
                    cascade.ProbeCountX,
                    cascade.ProbeCountY,
                    cascade.ProbeCountZ);

                DdgiClipmapDirtyReason resetReason = layoutChanged
                    ? DdgiClipmapDirtyReason.LayoutChange
                    : firstActivation
                        ? DdgiClipmapDirtyReason.InitialActivation
                        : DdgiClipmapDirtyReason.Teleport;

                bool cascadeOverlapTrustworthy = !teleport &&
                    HasTrustworthyOverlap(cascade.LogicalGridMinCell, nextGridMin, cascade);

                if (layoutChanged || firstActivation || teleport || !cascadeOverlapTrustworthy)
                    dirtyProbeCount += cascade.ResetTo(nextGridMin, frameIndex, resetReason);
                else
                    dirtyProbeCount += cascade.ScrollTo(nextGridMin, frameIndex);
            }

            _previousCameraPosition = cameraPosition;
            _hasPreviousCameraPosition = true;
            WarmUpActive = firstActivation || layoutChanged || teleport || cameraCutReset;

            return new CameraRelativeDdgiClipmapUpdateResult(
                layoutChanged,
                firstActivation,
                teleport,
                cameraCutReset,
                dirtyProbeCount,
                movementClass,
                fastMovement,
                cameraCutReset);
        }

        public static DdgiClipmapCell CalculateCenteredGridMinimum(
            Vector3 cameraPosition,
            float probeSpacing,
            int probeCountX,
            int probeCountY,
            int probeCountZ)
        {
            if (!float.IsFinite(probeSpacing) || probeSpacing <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(probeSpacing), "Probe spacing must be finite and positive.");

            DdgiClipmapCell cameraCell = new(
                FloorToCell(cameraPosition.X, probeSpacing),
                FloorToCell(cameraPosition.Y, probeSpacing),
                FloorToCell(cameraPosition.Z, probeSpacing));

            return new DdgiClipmapCell(
                CalculateGridMinimumAxis(cameraCell.X, probeCountX),
                CalculateGridMinimumAxis(cameraCell.Y, probeCountY),
                CalculateGridMinimumAxis(cameraCell.Z, probeCountZ));
        }

        public static int CalculatePhysicalProbeIndex(
            DdgiClipmapCell logicalCell,
            DdgiClipmapCell gridMinCell,
            DdgiClipmapCell ringOffset,
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            int physicalFirstProbeIndex)
        {
            return DdgiClipmapAddressing.CalculatePhysicalProbeIndex(
                logicalCell,
                gridMinCell,
                ringOffset,
                probeCountX,
                probeCountY,
                probeCountZ,
                physicalFirstProbeIndex);
        }

        private void RebuildLayout(GlobalIlluminationSettings settings, ulong layoutSignature)
        {
            _cascades.Clear();
            TotalProbeCount = 0;

            int cascadeCount = settings.DdgiClipmapCascadeCount;
            int probeCountX = settings.DdgiClipmapProbeCountX;
            int probeCountY = settings.DdgiClipmapProbeCountY;
            int probeCountZ = settings.DdgiClipmapProbeCountZ;
            float spacing = settings.DdgiClipmapBaseSpacing;
            float spacingScale = settings.DdgiClipmapSpacingScale;

            for (int i = 0; i < cascadeCount; i++)
            {
                _cascades.Add(new DdgiClipmapCascadeState(
                    i,
                    probeCountX,
                    probeCountY,
                    probeCountZ,
                    spacing,
                    TotalProbeCount));
                TotalProbeCount = checked(TotalProbeCount + probeCountX * probeCountY * probeCountZ);
                spacing *= spacingScale;
            }

            _layoutSignature = layoutSignature;
            _hasLayoutSignature = true;
            _hasPreviousCameraPosition = false;
        }

        private static ulong CreateLayoutSignature(GlobalIlluminationSettings settings)
        {
            unchecked
            {
                const ulong hashStart = 14695981039346656037UL;
                const ulong hashPrime = 1099511628211UL;
                ulong hash = hashStart;
                Add(unchecked((uint)settings.DdgiClipmapCascadeCount));
                Add(unchecked((uint)settings.DdgiClipmapProbeCountX));
                Add(unchecked((uint)settings.DdgiClipmapProbeCountY));
                Add(unchecked((uint)settings.DdgiClipmapProbeCountZ));
                Add(BitConverter.SingleToUInt32Bits(settings.DdgiClipmapBaseSpacing));
                Add(BitConverter.SingleToUInt32Bits(settings.DdgiClipmapSpacingScale));
                Add(BitConverter.SingleToUInt32Bits(settings.DdgiClipmapVerticalCenterOffset));
                return hash;

                void Add(uint value)
                {
                    hash ^= value;
                    hash *= hashPrime;
                }
            }
        }

        private static int FloorToCell(float value, float spacing)
        {
            if (!float.IsFinite(value))
                return 0;

            double cell = Math.Floor(value / spacing);
            if (cell <= int.MinValue)
                return int.MinValue;
            if (cell >= int.MaxValue)
                return int.MaxValue;

            return (int)cell;
        }

        private static int CalculateGridMinimumAxis(int cameraCell, int probeCount)
        {
            int safeProbeCount = Math.Max(1, probeCount);
            int halfExtent = safeProbeCount / 2;
            long min = (long)cameraCell - halfExtent;
            long maxSupportedMin = (long)int.MaxValue - safeProbeCount + 1L;

            if (min < int.MinValue)
                return int.MinValue;
            if (min > maxSupportedMin)
                return (int)maxSupportedMin;

            return (int)min;
        }

        private static bool IsFastMovement(float cameraDelta, GlobalIlluminationSettings settings)
        {
            float baseSpacing = MathF.Max(settings.DdgiClipmapBaseSpacing, 0.001f);
            float fastThreshold = baseSpacing * MathF.Max(2.0f, settings.DdgiClipmapSafetyMarginCells + 1.0f);
            return cameraDelta >= fastThreshold;
        }

        private static DdgiCameraMovementClass ResolveMovementClass(
            bool layoutChanged,
            bool firstActivation,
            bool teleport,
            bool fastMovement,
            bool cameraCutReset)
        {
            if (layoutChanged)
                return DdgiCameraMovementClass.LayoutChanged;
            if (firstActivation)
                return DdgiCameraMovementClass.FirstActivation;
            if (teleport)
                return DdgiCameraMovementClass.Teleport;
            if (fastMovement)
                return DdgiCameraMovementClass.Fast;
            if (cameraCutReset)
                return DdgiCameraMovementClass.ViewResetOnly;
            return DdgiCameraMovementClass.Normal;
        }

        private static bool HasTrustworthyOverlap(
            DdgiClipmapCell currentGridMin,
            DdgiClipmapCell nextGridMin,
            DdgiClipmapCascadeState cascade)
        {
            DdgiClipmapCell delta = SubtractSaturating(nextGridMin, currentGridMin);
            long overlapX = Math.Max(0L, (long)cascade.ProbeCountX - AbsLong(delta.X));
            long overlapY = Math.Max(0L, (long)cascade.ProbeCountY - AbsLong(delta.Y));
            long overlapZ = Math.Max(0L, (long)cascade.ProbeCountZ - AbsLong(delta.Z));
            long overlap = overlapX * overlapY * overlapZ;
            long total = (long)cascade.ProbeCountX * cascade.ProbeCountY * cascade.ProbeCountZ;
            if (total <= 0)
                return false;

            return (double)overlap / total >= 0.25;
        }

        internal static int PositiveModulo(int value, int divisor)
        {
            return DdgiClipmapAddressing.PositiveModulo(value, divisor);
        }

        internal static int PositiveModulo(long value, int divisor)
        {
            return DdgiClipmapAddressing.PositiveModulo(value, divisor);
        }

        internal static DdgiClipmapCell SubtractSaturating(DdgiClipmapCell left, DdgiClipmapCell right)
        {
            return new DdgiClipmapCell(
                SubtractSaturating(left.X, right.X),
                SubtractSaturating(left.Y, right.Y),
                SubtractSaturating(left.Z, right.Z));
        }

        private static int SubtractSaturating(int left, int right)
        {
            long result = (long)left - right;
            if (result < int.MinValue)
                return int.MinValue;
            if (result > int.MaxValue)
                return int.MaxValue;

            return (int)result;
        }

        private static long AbsLong(int value)
        {
            return value == int.MinValue ? (long)int.MaxValue + 1L : Math.Abs(value);
        }
    }

    public sealed class DdgiClipmapCascadeState
    {
        private readonly bool[] _initialized;
        private readonly ulong[] _lastUpdateFrames;
        private readonly ulong[] _ageFrames;
        private readonly bool[] _dirtyThisFrame;
        private readonly List<DdgiClipmapDirtyRegion> _dirtyRegions = new();
        private ulong _lastAgeFrame;
        private bool _hasAgeFrame;

        internal DdgiClipmapCascadeState(
            int cascadeIndex,
            int probeCountX,
            int probeCountY,
            int probeCountZ,
            float probeSpacing,
            int physicalFirstProbeIndex)
        {
            CascadeIndex = cascadeIndex;
            ProbeCountX = probeCountX;
            ProbeCountY = probeCountY;
            ProbeCountZ = probeCountZ;
            ProbeSpacing = probeSpacing;
            PhysicalFirstProbeIndex = physicalFirstProbeIndex;

            int probeCount = checked(probeCountX * probeCountY * probeCountZ);
            _initialized = new bool[probeCount];
            _lastUpdateFrames = new ulong[probeCount];
            _ageFrames = new ulong[probeCount];
            _dirtyThisFrame = new bool[probeCount];
            for (int i = 0; i < _ageFrames.Length; i++)
            {
                _ageFrames[i] = ulong.MaxValue;
            }
        }

        public int CascadeIndex { get; }
        public int ProbeCountX { get; }
        public int ProbeCountY { get; }
        public int ProbeCountZ { get; }
        public int ProbeCount => _initialized.Length;
        public float ProbeSpacing { get; }
        public DdgiClipmapCell LogicalGridMinCell { get; private set; }
        public DdgiClipmapCell PreviousLogicalGridMinCell { get; private set; }
        public Vector3 SnappedOrigin { get; private set; }
        public Vector3 PreviousSnappedOrigin { get; private set; }
        public DdgiClipmapCell RingOffset { get; private set; }
        public int PhysicalFirstProbeIndex { get; }
        public DdgiClipmapCell ScrollDelta { get; private set; }
        public int DirtyProbeCount { get; private set; }
        public IReadOnlyList<DdgiClipmapDirtyRegion> DirtyRegions => _dirtyRegions;

        public bool ContainsLogicalCell(DdgiClipmapCell logicalCell)
        {
            return DdgiClipmapAddressing.ContainsLogicalCell(
                logicalCell,
                LogicalGridMinCell,
                ProbeCountX,
                ProbeCountY,
                ProbeCountZ);
        }

        public int GetPhysicalProbeIndex(DdgiClipmapCell logicalCell)
        {
            if (!ContainsLogicalCell(logicalCell))
                throw new ArgumentOutOfRangeException(nameof(logicalCell), "The logical DDGI cell is outside this cascade.");

            return CameraRelativeDdgiClipmapController.CalculatePhysicalProbeIndex(
                logicalCell,
                LogicalGridMinCell,
                RingOffset,
                ProbeCountX,
                ProbeCountY,
                ProbeCountZ,
                PhysicalFirstProbeIndex);
        }

        public DdgiClipmapCellState GetCellState(DdgiClipmapCell logicalCell)
        {
            int localIndex = GetLocalPhysicalIndex(logicalCell);
            ulong lastUpdateFrame = _initialized[localIndex] ? _lastUpdateFrames[localIndex] : 0UL;
            return new DdgiClipmapCellState(
                _initialized[localIndex],
                _ageFrames[localIndex],
                lastUpdateFrame,
                PhysicalFirstProbeIndex + localIndex);
        }

        public void MarkLogicalCellUpdated(DdgiClipmapCell logicalCell, ulong frameIndex)
        {
            int localIndex = GetLocalPhysicalIndex(logicalCell);
            _initialized[localIndex] = true;
            _lastUpdateFrames[localIndex] = frameIndex;
            _ageFrames[localIndex] = 0UL;
        }

        internal void BeginFrame(ulong frameIndex)
        {
            Array.Clear(_dirtyThisFrame);
            _dirtyRegions.Clear();
            DirtyProbeCount = 0;

            if (!_hasAgeFrame)
            {
                _lastAgeFrame = frameIndex;
                _hasAgeFrame = true;
                return;
            }

            if (frameIndex <= _lastAgeFrame)
                return;

            ulong delta = frameIndex - _lastAgeFrame;
            for (int i = 0; i < _ageFrames.Length; i++)
            {
                if (_initialized[i])
                    _ageFrames[i] = SaturatingAdd(_ageFrames[i], delta);
            }

            _lastAgeFrame = frameIndex;
        }

        internal int ResetTo(
            DdgiClipmapCell nextGridMin,
            ulong frameIndex,
            DdgiClipmapDirtyReason reason)
        {
            PreviousLogicalGridMinCell = LogicalGridMinCell;
            PreviousSnappedOrigin = SnappedOrigin;
            LogicalGridMinCell = nextGridMin;
            SnappedOrigin = ToOrigin(nextGridMin, ProbeSpacing);
            RingOffset = DdgiClipmapCell.Zero;
            ScrollDelta = DdgiClipmapCell.Zero;
            _lastAgeFrame = frameIndex;
            _hasAgeFrame = true;

            return InvalidateRegion(
                LogicalGridMinCell,
                new DdgiClipmapCell(
                    LogicalGridMinCell.X + ProbeCountX - 1,
                    LogicalGridMinCell.Y + ProbeCountY - 1,
                    LogicalGridMinCell.Z + ProbeCountZ - 1),
                reason);
        }

        internal int ScrollTo(DdgiClipmapCell nextGridMin, ulong frameIndex)
        {
            PreviousLogicalGridMinCell = LogicalGridMinCell;
            PreviousSnappedOrigin = SnappedOrigin;
            ScrollDelta = CameraRelativeDdgiClipmapController.SubtractSaturating(nextGridMin, LogicalGridMinCell);

            if (ScrollDelta == DdgiClipmapCell.Zero)
            {
                SnappedOrigin = ToOrigin(LogicalGridMinCell, ProbeSpacing);
                return 0;
            }

            LogicalGridMinCell = nextGridMin;
            SnappedOrigin = ToOrigin(LogicalGridMinCell, ProbeSpacing);
            RingOffset = new DdgiClipmapCell(
                CameraRelativeDdgiClipmapController.PositiveModulo((long)RingOffset.X + ScrollDelta.X, ProbeCountX),
                CameraRelativeDdgiClipmapController.PositiveModulo((long)RingOffset.Y + ScrollDelta.Y, ProbeCountY),
                CameraRelativeDdgiClipmapController.PositiveModulo((long)RingOffset.Z + ScrollDelta.Z, ProbeCountZ));

            if (AbsLong(ScrollDelta.X) >= ProbeCountX ||
                AbsLong(ScrollDelta.Y) >= ProbeCountY ||
                AbsLong(ScrollDelta.Z) >= ProbeCountZ)
            {
                return InvalidateRegion(
                    LogicalGridMinCell,
                    new DdgiClipmapCell(
                        LogicalGridMinCell.X + ProbeCountX - 1,
                        LogicalGridMinCell.Y + ProbeCountY - 1,
                        LogicalGridMinCell.Z + ProbeCountZ - 1),
                    DdgiClipmapDirtyReason.LargeScroll);
            }

            int dirtyCount = 0;
            dirtyCount += InvalidateMovedAxisSlab(ScrollDelta.X, ProbeCountX, Axis.X);
            dirtyCount += InvalidateMovedAxisSlab(ScrollDelta.Y, ProbeCountY, Axis.Y);
            dirtyCount += InvalidateMovedAxisSlab(ScrollDelta.Z, ProbeCountZ, Axis.Z);
            _lastAgeFrame = frameIndex;
            _hasAgeFrame = true;
            return dirtyCount;
        }

        private int InvalidateMovedAxisSlab(int delta, int probeCount, Axis axis)
        {
            if (delta == 0)
                return 0;

            int slabSize = Math.Abs(delta);
            int start = delta > 0 ? probeCount - slabSize : 0;
            int end = delta > 0 ? probeCount - 1 : slabSize - 1;

            DdgiClipmapCell min = new(
                LogicalGridMinCell.X,
                LogicalGridMinCell.Y,
                LogicalGridMinCell.Z);
            DdgiClipmapCell max = new(
                LogicalGridMinCell.X + ProbeCountX - 1,
                LogicalGridMinCell.Y + ProbeCountY - 1,
                LogicalGridMinCell.Z + ProbeCountZ - 1);

            switch (axis)
            {
                case Axis.X:
                    min = min with { X = LogicalGridMinCell.X + start };
                    max = max with { X = LogicalGridMinCell.X + end };
                    break;
                case Axis.Y:
                    min = min with { Y = LogicalGridMinCell.Y + start };
                    max = max with { Y = LogicalGridMinCell.Y + end };
                    break;
                case Axis.Z:
                    min = min with { Z = LogicalGridMinCell.Z + start };
                    max = max with { Z = LogicalGridMinCell.Z + end };
                    break;
            }

            return InvalidateRegion(min, max, DdgiClipmapDirtyReason.Scroll);
        }

        private int InvalidateRegion(
            DdgiClipmapCell min,
            DdgiClipmapCell max,
            DdgiClipmapDirtyReason reason)
        {
            _dirtyRegions.Add(new DdgiClipmapDirtyRegion(min, max, reason));
            int uniqueDirtyCount = 0;

            for (int z = min.Z; z <= max.Z; z++)
            {
                for (int y = min.Y; y <= max.Y; y++)
                {
                    for (int x = min.X; x <= max.X; x++)
                    {
                        int localIndex = GetLocalPhysicalIndexUnchecked(new DdgiClipmapCell(x, y, z));
                        _initialized[localIndex] = false;
                        _lastUpdateFrames[localIndex] = 0UL;
                        _ageFrames[localIndex] = ulong.MaxValue;

                        if (!_dirtyThisFrame[localIndex])
                        {
                            _dirtyThisFrame[localIndex] = true;
                            uniqueDirtyCount++;
                        }
                    }
                }
            }

            DirtyProbeCount += uniqueDirtyCount;
            return uniqueDirtyCount;
        }

        private int GetLocalPhysicalIndex(DdgiClipmapCell logicalCell)
        {
            if (!ContainsLogicalCell(logicalCell))
                throw new ArgumentOutOfRangeException(nameof(logicalCell), "The logical DDGI cell is outside this cascade.");

            return GetLocalPhysicalIndexUnchecked(logicalCell);
        }

        private int GetLocalPhysicalIndexUnchecked(DdgiClipmapCell logicalCell)
        {
            return CameraRelativeDdgiClipmapController.CalculatePhysicalProbeIndex(
                logicalCell,
                LogicalGridMinCell,
                RingOffset,
                ProbeCountX,
                ProbeCountY,
                ProbeCountZ,
                0);
        }

        private static Vector3 ToOrigin(DdgiClipmapCell cell, float spacing)
        {
            return new Vector3(
                CellToWorld(cell.X, spacing),
                CellToWorld(cell.Y, spacing),
                CellToWorld(cell.Z, spacing));
        }

        private static float CellToWorld(int cell, float spacing)
        {
            double world = (double)cell * spacing;
            if (world <= -float.MaxValue)
                return -float.MaxValue;
            if (world >= float.MaxValue)
                return float.MaxValue;
            return (float)world;
        }

        private static ulong SaturatingAdd(ulong value, ulong delta)
        {
            ulong result = value + delta;
            return result < value ? ulong.MaxValue : result;
        }

        private static long AbsLong(int value)
        {
            return value == int.MinValue ? (long)int.MaxValue + 1L : Math.Abs(value);
        }

        private enum Axis
        {
            X,
            Y,
            Z
        }
    }
}
