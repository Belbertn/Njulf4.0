using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources
{
    public enum DdgiRoomVolumeValidationSeverity
    {
        Warning,
        Error
    }

    public enum DdgiRoomVolumeValidationCode
    {
        ProbeInsideGeometry,
        InsufficientFreeSpaceCoverage,
        ProbeTooCloseToThinWall,
        ExcessiveOverlap,
        InvalidBlendMargin,
        ProbeQuotaOverflow,
        ThinWallPolicyDisabled,
        ThinWallProxyTooThin,
        RoomPresetOutOfRange
    }

    public readonly record struct DdgiRoomVolumeValidationIssue(
        int VolumeIndex,
        DdgiRoomVolumeValidationSeverity Severity,
        DdgiRoomVolumeValidationCode Code,
        string Message);

    public sealed class DdgiRoomVolumeValidationReport
    {
        public DdgiRoomVolumeValidationReport(
            IReadOnlyList<DdgiRoomVolumeValidationIssue> issues,
            int activeVolumeCount,
            int totalProbeCount,
            int activeProbeBudget,
            int sampledProbeCount,
            int blockedProbeCount,
            int thinWallNearProbeCount)
        {
            Issues = issues ?? throw new ArgumentNullException(nameof(issues));
            ActiveVolumeCount = activeVolumeCount;
            TotalProbeCount = totalProbeCount;
            ActiveProbeBudget = activeProbeBudget;
            SampledProbeCount = sampledProbeCount;
            BlockedProbeCount = blockedProbeCount;
            ThinWallNearProbeCount = thinWallNearProbeCount;
        }

        public IReadOnlyList<DdgiRoomVolumeValidationIssue> Issues { get; }
        public int ActiveVolumeCount { get; }
        public int TotalProbeCount { get; }
        public int ActiveProbeBudget { get; }
        public int SampledProbeCount { get; }
        public int BlockedProbeCount { get; }
        public int ThinWallNearProbeCount { get; }
        public int ErrorCount => Count(DdgiRoomVolumeValidationSeverity.Error);
        public int WarningCount => Count(DdgiRoomVolumeValidationSeverity.Warning);
        public bool IsProductionReady => ErrorCount == 0;

        public bool HasIssue(DdgiRoomVolumeValidationCode code)
        {
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Code == code)
                    return true;
            }

            return false;
        }

        private int Count(DdgiRoomVolumeValidationSeverity severity)
        {
            int count = 0;
            for (int i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Severity == severity)
                    count++;
            }

            return count;
        }
    }

    public sealed class DdgiRoomVolumeValidationOptions
    {
        public int MaxProbeSamplesPerVolume { get; init; } = 4096;
        public float MinimumFreeProbeFraction { get; init; } = 0.7f;
        public float ThinWallMaximumThickness { get; init; } = 0.25f;
        public float ThinWallProbeClearanceSpacingScale { get; init; } = 0.35f;
        public float MinimumThinWallProxyThickness { get; init; } = 0.08f;
        public float MinimumBlendDistanceSpacingScale { get; init; } = 0.5f;
        public float MaximumBlendDistanceAxisFraction { get; init; } = 0.45f;
        public float MaximumOverlapFraction { get; init; } = 0.65f;
    }

    public static class DdgiRoomVolumeValidator
    {
        public static DdgiRoomVolumeValidationReport Validate(
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes,
            GlobalIlluminationSettings settings,
            IReadOnlyList<BoundingBox>? solidGeometryBounds = null,
            IReadOnlyList<BoundingBox>? thinWallBounds = null,
            DdgiRoomVolumeValidationOptions? options = null)
        {
            if (volumes == null)
                throw new ArgumentNullException(nameof(volumes));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            options ??= new DdgiRoomVolumeValidationOptions();
            solidGeometryBounds ??= Array.Empty<BoundingBox>();
            thinWallBounds ??= Array.Empty<BoundingBox>();

            var issues = new List<DdgiRoomVolumeValidationIssue>();
            int activeVolumeCount = 0;
            int totalProbeCount = 0;
            int sampledProbeCount = 0;
            int blockedProbeCount = 0;
            int thinWallNearProbeCount = 0;

            for (int i = 0; i < volumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? volume = volumes[i];
                if (volume == null || !volume.Enabled)
                    continue;

                activeVolumeCount++;
                totalProbeCount = checked(totalProbeCount + volume.ProbeCount);
                ValidateRoomPresetRange(i, volume, issues);
                ValidateBlendMargin(i, volume, options, issues);
                ValidateProbeFreeSpace(
                    i,
                    volume,
                    solidGeometryBounds,
                    thinWallBounds,
                    options,
                    issues,
                    ref sampledProbeCount,
                    ref blockedProbeCount,
                    ref thinWallNearProbeCount);
            }

            ValidateOverlap(volumes, options, issues);
            int activeProbeBudget = GlobalIlluminationProbeVolumeData.CalculateActiveProbeBudget(settings);
            if (totalProbeCount > activeProbeBudget)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    -1,
                    DdgiRoomVolumeValidationSeverity.Error,
                    DdgiRoomVolumeValidationCode.ProbeQuotaOverflow,
                    $"Active local DDGI volumes require {totalProbeCount} probes but the active DDGI budget is {activeProbeBudget}."));
            }

            ValidateThinWallPolicy(settings, thinWallBounds, options, issues);

            return new DdgiRoomVolumeValidationReport(
                issues,
                activeVolumeCount,
                totalProbeCount,
                activeProbeBudget,
                sampledProbeCount,
                blockedProbeCount,
                thinWallNearProbeCount);
        }

        private static void ValidateRoomPresetRange(
            int volumeIndex,
            GlobalIlluminationProbeVolume volume,
            List<DdgiRoomVolumeValidationIssue> issues)
        {
            if (!volume.Interior)
                return;

            float spacing = MinAxis(volume.ProbeSpacing);
            if (spacing < 0.4f || spacing > 0.75f ||
                volume.MaxRayDistance < 8.0f || volume.MaxRayDistance > 15.0f ||
                volume.RaysPerProbe < 32 ||
                volume.DirtyRaysPerProbe < 48 ||
                volume.MaxProbeUpdatesPerFrame < 48 ||
                volume.Hysteresis > 0.9f)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    volumeIndex,
                    DdgiRoomVolumeValidationSeverity.Warning,
                    DdgiRoomVolumeValidationCode.RoomPresetOutOfRange,
                    "Interior DDGI room volume is outside the production room preset range."));
            }
        }

        private static void ValidateBlendMargin(
            int volumeIndex,
            GlobalIlluminationProbeVolume volume,
            DdgiRoomVolumeValidationOptions options,
            List<DdgiRoomVolumeValidationIssue> issues)
        {
            float minAxis = MinAxis(volume.Size);
            float minSpacing = MinAxis(volume.ProbeSpacing);
            float minBlend = minSpacing * MathF.Max(options.MinimumBlendDistanceSpacingScale, 0.0f);
            float maxBlend = minAxis * Math.Clamp(options.MaximumBlendDistanceAxisFraction, 0.01f, 0.5f);
            if (volume.BlendDistance < minBlend || volume.BlendDistance > maxBlend)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    volumeIndex,
                    DdgiRoomVolumeValidationSeverity.Error,
                    DdgiRoomVolumeValidationCode.InvalidBlendMargin,
                    $"DDGI room blend distance {volume.BlendDistance:0.###} must be between {minBlend:0.###} and {maxBlend:0.###}."));
            }
        }

        private static void ValidateProbeFreeSpace(
            int volumeIndex,
            GlobalIlluminationProbeVolume volume,
            IReadOnlyList<BoundingBox> solidGeometryBounds,
            IReadOnlyList<BoundingBox> thinWallBounds,
            DdgiRoomVolumeValidationOptions options,
            List<DdgiRoomVolumeValidationIssue> issues,
            ref int sampledProbeCount,
            ref int blockedProbeCount,
            ref int thinWallNearProbeCount)
        {
            int sampled = 0;
            int blocked = 0;
            int nearThinWall = 0;
            int stepX = ResolveProbeSampleStep(volume.ProbeCountX, options.MaxProbeSamplesPerVolume);
            int stepY = ResolveProbeSampleStep(volume.ProbeCountY, options.MaxProbeSamplesPerVolume);
            int stepZ = ResolveProbeSampleStep(volume.ProbeCountZ, options.MaxProbeSamplesPerVolume);
            float clearance = MathF.Max(0.08f, MinAxis(volume.ProbeSpacing) * MathF.Max(options.ThinWallProbeClearanceSpacingScale, 0.0f));

            for (int z = 0; z < volume.ProbeCountZ; z += stepZ)
            {
                for (int y = 0; y < volume.ProbeCountY; y += stepY)
                {
                    for (int x = 0; x < volume.ProbeCountX; x += stepX)
                    {
                        Vector3 probePosition = ProbePosition(volume, x, y, z);
                        sampled++;

                        if (ContainedByAny(solidGeometryBounds, probePosition))
                            blocked++;
                        if (NearAnyThinWall(thinWallBounds, probePosition, clearance, options.ThinWallMaximumThickness))
                            nearThinWall++;
                    }
                }
            }

            sampledProbeCount += sampled;
            blockedProbeCount += blocked;
            thinWallNearProbeCount += nearThinWall;

            if (blocked > 0)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    volumeIndex,
                    DdgiRoomVolumeValidationSeverity.Error,
                    DdgiRoomVolumeValidationCode.ProbeInsideGeometry,
                    $"{blocked} of {sampled} sampled DDGI probes start inside solid geometry."));
            }

            float freeFraction = sampled > 0 ? (float)(sampled - blocked) / sampled : 1.0f;
            if (freeFraction < Math.Clamp(options.MinimumFreeProbeFraction, 0.0f, 1.0f))
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    volumeIndex,
                    DdgiRoomVolumeValidationSeverity.Error,
                    DdgiRoomVolumeValidationCode.InsufficientFreeSpaceCoverage,
                    $"Only {freeFraction:P1} of sampled DDGI probes are in free space."));
            }

            if (nearThinWall > 0)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    volumeIndex,
                    DdgiRoomVolumeValidationSeverity.Warning,
                    DdgiRoomVolumeValidationCode.ProbeTooCloseToThinWall,
                    $"{nearThinWall} sampled DDGI probes are closer than {clearance:0.###}m to a thin wall."));
            }
        }

        private static void ValidateOverlap(
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes,
            DdgiRoomVolumeValidationOptions options,
            List<DdgiRoomVolumeValidationIssue> issues)
        {
            for (int i = 0; i < volumes.Count; i++)
            {
                GlobalIlluminationProbeVolume? a = volumes[i];
                if (a == null || !a.Enabled)
                    continue;

                for (int j = i + 1; j < volumes.Count; j++)
                {
                    GlobalIlluminationProbeVolume? b = volumes[j];
                    if (b == null || !b.Enabled)
                        continue;

                    float overlapFraction = OverlapVolume(a.Bounds, b.Bounds) / MathF.Max(0.000001f, MathF.Min(Volume(a.Bounds), Volume(b.Bounds)));
                    if (overlapFraction > Math.Clamp(options.MaximumOverlapFraction, 0.0f, 1.0f))
                    {
                        issues.Add(new DdgiRoomVolumeValidationIssue(
                            i,
                            overlapFraction > 0.9f ? DdgiRoomVolumeValidationSeverity.Error : DdgiRoomVolumeValidationSeverity.Warning,
                            DdgiRoomVolumeValidationCode.ExcessiveOverlap,
                            $"DDGI room volume overlaps volume {j} by {overlapFraction:P1} of the smaller volume."));
                    }
                }
            }
        }

        private static void ValidateThinWallPolicy(
            GlobalIlluminationSettings settings,
            IReadOnlyList<BoundingBox> thinWallBounds,
            DdgiRoomVolumeValidationOptions options,
            List<DdgiRoomVolumeValidationIssue> issues)
        {
            if (thinWallBounds.Count == 0)
                return;

            if (!settings.DdgiThinWallPolicyEnabled)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    -1,
                    DdgiRoomVolumeValidationSeverity.Error,
                    DdgiRoomVolumeValidationCode.ThinWallPolicyDisabled,
                    "Thin-wall DDGI policy must be enabled when the scene declares thin-wall blockers."));
                return;
            }

            float requiredThickness = MathF.Max(options.MinimumThinWallProxyThickness, MaxThinWallThickness(thinWallBounds));
            if (settings.DdgiThinWallProxyThickness < requiredThickness)
            {
                issues.Add(new DdgiRoomVolumeValidationIssue(
                    -1,
                    DdgiRoomVolumeValidationSeverity.Warning,
                    DdgiRoomVolumeValidationCode.ThinWallProxyTooThin,
                    $"Thin-wall RT proxy thickness {settings.DdgiThinWallProxyThickness:0.###}m is below the required {requiredThickness:0.###}m."));
            }
        }

        private static Vector3 ProbePosition(GlobalIlluminationProbeVolume volume, int x, int y, int z)
        {
            Vector3 spacing = volume.ProbeSpacing;
            return volume.Origin + new Vector3(spacing.X * x, spacing.Y * y, spacing.Z * z);
        }

        private static int ResolveProbeSampleStep(int count, int maxSamplesPerVolume)
        {
            if (count <= 1 || maxSamplesPerVolume <= 0)
                return 1;

            int targetPerAxis = Math.Max(2, (int)MathF.Ceiling(MathF.Pow(maxSamplesPerVolume, 1.0f / 3.0f)));
            return Math.Max(1, (int)MathF.Ceiling((float)count / targetPerAxis));
        }

        private static bool ContainedByAny(IReadOnlyList<BoundingBox> boxes, Vector3 point)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Contains(point))
                    return true;
            }

            return false;
        }

        private static bool NearAnyThinWall(
            IReadOnlyList<BoundingBox> boxes,
            Vector3 point,
            float clearance,
            float thinWallMaximumThickness)
        {
            for (int i = 0; i < boxes.Count; i++)
            {
                BoundingBox box = boxes[i];
                if (MinAxis(box.Size) > thinWallMaximumThickness)
                    continue;
                if (DistanceToBox(point, box) <= clearance)
                    return true;
            }

            return false;
        }

        private static float DistanceToBox(Vector3 point, BoundingBox box)
        {
            float dx = point.X < box.Min.X ? box.Min.X - point.X : point.X > box.Max.X ? point.X - box.Max.X : 0.0f;
            float dy = point.Y < box.Min.Y ? box.Min.Y - point.Y : point.Y > box.Max.Y ? point.Y - box.Max.Y : 0.0f;
            float dz = point.Z < box.Min.Z ? box.Min.Z - point.Z : point.Z > box.Max.Z ? point.Z - box.Max.Z : 0.0f;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static float OverlapVolume(BoundingBox a, BoundingBox b)
        {
            float x = MathF.Max(0.0f, MathF.Min(a.Max.X, b.Max.X) - MathF.Max(a.Min.X, b.Min.X));
            float y = MathF.Max(0.0f, MathF.Min(a.Max.Y, b.Max.Y) - MathF.Max(a.Min.Y, b.Min.Y));
            float z = MathF.Max(0.0f, MathF.Min(a.Max.Z, b.Max.Z) - MathF.Max(a.Min.Z, b.Min.Z));
            return x * y * z;
        }

        private static float Volume(BoundingBox box)
        {
            Vector3 size = box.Size;
            return MathF.Max(size.X, 0.0f) * MathF.Max(size.Y, 0.0f) * MathF.Max(size.Z, 0.0f);
        }

        private static float MaxThinWallThickness(IReadOnlyList<BoundingBox> boxes)
        {
            float thickness = 0.0f;
            for (int i = 0; i < boxes.Count; i++)
                thickness = MathF.Max(thickness, MinAxis(boxes[i].Size));
            return thickness;
        }

        private static float MinAxis(Vector3 value)
        {
            return MathF.Min(value.X, MathF.Min(value.Y, value.Z));
        }
    }
}
