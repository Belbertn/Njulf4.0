using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Math;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

/// <summary>
/// Deterministic camera programs admitted by the production benchmark. Moving
/// programs are aligned to frame zero before measurement and are validated at
/// every measured frame; their observed camera/scene sequence is also hashed
/// into the capture contract.
/// </summary>
public enum SampleBenchmarkTrajectoryKind : byte
{
    Stationary,
    BistroPresentation,
    BistroLoop,
    SponzaLow,
    SponzaHigh,
    SponzaReceiverCacheIncident,
    SponzaHorizontal,
    SponzaVertical,
    BistroSnapshotIncident,
    SponzaSnapshotIncident
}

public sealed record SampleBenchmarkCameraPose(
    string Name,
    Vector3 Position,
    float Yaw,
    float Pitch,
    float FieldOfView,
    float NearPlane,
    float FarPlane);

public static class SampleBenchmarkTrajectory
{
    public const string StationaryName = "stationary";
    public const string BistroPresentationName = "bistro-presentation";
    public const string BistroSnapshotIncidentName =
        "bistro-snapshot-incident";
    public const string BistroLoopName = "bistro-loop";
    public const string SponzaLowName = "sponza-low";
    public const string SponzaHighName = "sponza-high";
    public const string SponzaReceiverCacheIncidentName =
        "sponza-receiver-cache-incident";
    public const string SponzaSnapshotIncidentName =
        "sponza-snapshot-incident";
    public const string SponzaHorizontalName = "sponza-horizontal";
    public const string SponzaVerticalName = "sponza-vertical";

    private const float CameraValueTolerance = 1.0e-4f;

    public static SampleBenchmarkTrajectoryKind Parse(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value)
            ? StationaryName
            : value.Trim().ToLowerInvariant();
        return normalized switch
        {
            StationaryName => SampleBenchmarkTrajectoryKind.Stationary,
            BistroPresentationName =>
                SampleBenchmarkTrajectoryKind.BistroPresentation,
            BistroSnapshotIncidentName =>
                SampleBenchmarkTrajectoryKind.BistroSnapshotIncident,
            BistroLoopName => SampleBenchmarkTrajectoryKind.BistroLoop,
            SponzaLowName => SampleBenchmarkTrajectoryKind.SponzaLow,
            SponzaHighName => SampleBenchmarkTrajectoryKind.SponzaHigh,
            SponzaReceiverCacheIncidentName =>
                SampleBenchmarkTrajectoryKind.SponzaReceiverCacheIncident,
            SponzaSnapshotIncidentName =>
                SampleBenchmarkTrajectoryKind.SponzaSnapshotIncident,
            SponzaHorizontalName => SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            SponzaVerticalName => SampleBenchmarkTrajectoryKind.SponzaVertical,
            _ => throw new ArgumentException(
                $"Unknown benchmark trajectory '{value}'. Valid values: " +
                $"{StationaryName}, {BistroPresentationName}, " +
                $"{BistroSnapshotIncidentName}, {BistroLoopName}, " +
                $"{SponzaLowName}, {SponzaHighName}, " +
                $"{SponzaReceiverCacheIncidentName}, {SponzaSnapshotIncidentName}, " +
                $"{SponzaHorizontalName}, " +
                $"{SponzaVerticalName}.",
                nameof(value))
        };
    }

    public static string GetName(SampleBenchmarkTrajectoryKind kind) => kind switch
    {
        SampleBenchmarkTrajectoryKind.Stationary => StationaryName,
        SampleBenchmarkTrajectoryKind.BistroPresentation => BistroPresentationName,
        SampleBenchmarkTrajectoryKind.BistroSnapshotIncident =>
            BistroSnapshotIncidentName,
        SampleBenchmarkTrajectoryKind.BistroLoop => BistroLoopName,
        SampleBenchmarkTrajectoryKind.SponzaLow => SponzaLowName,
        SampleBenchmarkTrajectoryKind.SponzaHigh => SponzaHighName,
        SampleBenchmarkTrajectoryKind.SponzaReceiverCacheIncident =>
            SponzaReceiverCacheIncidentName,
        SampleBenchmarkTrajectoryKind.SponzaSnapshotIncident =>
            SponzaSnapshotIncidentName,
        SampleBenchmarkTrajectoryKind.SponzaHorizontal => SponzaHorizontalName,
        SampleBenchmarkTrajectoryKind.SponzaVertical => SponzaVerticalName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public static bool IsMoving(SampleBenchmarkTrajectoryKind kind) => kind is
        SampleBenchmarkTrajectoryKind.BistroLoop or
        SampleBenchmarkTrajectoryKind.SponzaHorizontal or
        SampleBenchmarkTrajectoryKind.SponzaVertical;

    public static bool RequiresBistro(
        SampleBenchmarkTrajectoryKind kind) => kind is
        SampleBenchmarkTrajectoryKind.BistroPresentation or
        SampleBenchmarkTrajectoryKind.BistroSnapshotIncident or
        SampleBenchmarkTrajectoryKind.BistroLoop;

    public static bool RequiresSponza(
        SampleBenchmarkTrajectoryKind kind) => kind is
        SampleBenchmarkTrajectoryKind.SponzaLow or
        SampleBenchmarkTrajectoryKind.SponzaHigh or
        SampleBenchmarkTrajectoryKind.SponzaReceiverCacheIncident or
        SampleBenchmarkTrajectoryKind.SponzaSnapshotIncident or
        SampleBenchmarkTrajectoryKind.SponzaHorizontal or
        SampleBenchmarkTrajectoryKind.SponzaVertical;

    public static int GetFrameCount(SampleBenchmarkTrajectoryKind kind) => kind switch
    {
        SampleBenchmarkTrajectoryKind.BistroLoop =>
            SampleBistroQualityCaptureContract.LoopFrameCount,
        SampleBenchmarkTrajectoryKind.SponzaHorizontal =>
            SampleSponzaGiCaptureContract.Default.MotionTraversalFrameCount,
        SampleBenchmarkTrajectoryKind.SponzaVertical =>
            SampleSponzaGiCaptureContract.Default.VerticalTraversalFrameCount,
        _ => 1
    };

    public static bool IsMeasurementStartFrame(
        SampleBenchmarkTrajectoryKind kind,
        int absoluteFrameIndex)
    {
        if (!IsMoving(kind))
            return true;
        int frameCount = GetFrameCount(kind);
        return absoluteFrameIndex >= 0 && absoluteFrameIndex % frameCount == 0;
    }

    public static int GetTrajectoryFrameIndex(
        SampleBenchmarkTrajectoryKind kind,
        int absoluteFrameIndex)
    {
        if (!IsMoving(kind))
            return 0;
        int frameCount = GetFrameCount(kind);
        int remainder = absoluteFrameIndex % frameCount;
        return remainder < 0 ? remainder + frameCount : remainder;
    }

    /// <summary>
    /// Resolves the untimed camera program. Closed routes circulate through
    /// their authored warmup; the one-way vertical route remains at Low so it
    /// cannot introduce an untimed High-to-timed-Low cut.
    /// </summary>
    public static int GetWarmupFrameIndex(
        SampleBenchmarkTrajectoryKind kind,
        int absoluteFrameIndex)
    {
        if (kind == SampleBenchmarkTrajectoryKind.SponzaVertical)
            return 0;
        return GetTrajectoryFrameIndex(kind, absoluteFrameIndex);
    }

    public static bool CanStartMeasurementAfterFrame(
        SampleBenchmarkTrajectoryKind kind,
        int absoluteFrameIndex)
    {
        if (!IsMoving(kind) ||
            kind == SampleBenchmarkTrajectoryKind.SponzaVertical)
        {
            return true;
        }
        return GetWarmupFrameIndex(kind, absoluteFrameIndex) ==
            GetFrameCount(kind) - 1;
    }

    public static string CreateFingerprint(
        SampleBenchmarkTrajectoryKind kind,
        SampleBistroQualityCaptureVariant bistroVariant)
    {
        string contractFingerprint = kind switch
        {
            SampleBenchmarkTrajectoryKind.BistroPresentation or
                SampleBenchmarkTrajectoryKind.BistroSnapshotIncident or
                SampleBenchmarkTrajectoryKind.BistroLoop =>
                new SampleBistroQualityCaptureContract(bistroVariant).Fingerprint,
            SampleBenchmarkTrajectoryKind.SponzaLow or
                SampleBenchmarkTrajectoryKind.SponzaHigh or
                SampleBenchmarkTrajectoryKind.SponzaReceiverCacheIncident or
                SampleBenchmarkTrajectoryKind.SponzaSnapshotIncident or
                SampleBenchmarkTrajectoryKind.SponzaHorizontal or
                SampleBenchmarkTrajectoryKind.SponzaVertical =>
                SampleSponzaGiCaptureContract.Default.Fingerprint,
            _ => "stationary-camera-owned-by-scene"
        };
        string canonical = string.Join(
            "|",
            "njulf-benchmark-trajectory/v1",
            GetName(kind),
            GetFrameCount(kind).ToString(CultureInfo.InvariantCulture),
            contractFingerprint);
        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>
    /// Camera-only identity for one authored route cycle. It intentionally
    /// excludes scene state, renderer settings, and camera-cut serials so it
    /// remains comparable across isolated A/B variants. Generic stationary
    /// captures bind their runtime-frozen camera through <paramref
    /// name="stationaryCamera"/>.
    /// </summary>
    public static string CreateRouteHash(
        SampleBenchmarkTrajectoryKind kind,
        SampleBistroQualityCaptureVariant bistroVariant,
        PerformanceCaptureCameraMetadata? stationaryCamera = null)
    {
        var canonical = new StringBuilder();
        canonical.Append("njulf-benchmark-camera-route/v1|")
            .Append(GetName(kind))
            .Append('|')
            .Append(GetFrameCount(kind).ToString(CultureInfo.InvariantCulture))
            .Append('\n');
        if (kind == SampleBenchmarkTrajectoryKind.Stationary)
        {
            if (stationaryCamera == null)
            {
                throw new ArgumentNullException(
                    nameof(stationaryCamera),
                    "The generic stationary route requires its frozen runtime camera.");
            }
            AppendCamera(canonical, 0, stationaryCamera);
        }
        else
        {
            int frameCount = GetFrameCount(kind);
            for (int index = 0; index < frameCount; index++)
            {
                SampleBenchmarkCameraPose pose = ResolveCamera(
                    kind,
                    index,
                    bistroVariant) ??
                    throw new InvalidOperationException(
                        $"Trajectory '{GetName(kind)}' did not resolve frame {index}.");
                AppendCamera(canonical, index, pose);
            }
        }

        return "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    public static SampleBenchmarkCameraPose? ResolveCamera(
        SampleBenchmarkTrajectoryKind kind,
        int trajectoryFrameIndex,
        SampleBistroQualityCaptureVariant bistroVariant)
    {
        if (kind == SampleBenchmarkTrajectoryKind.Stationary)
            return null;

        if (RequiresBistro(kind))
        {
            var contract = new SampleBistroQualityCaptureContract(bistroVariant);
            SampleBistroQualityCameraBookmark bookmark = kind switch
            {
                SampleBenchmarkTrajectoryKind.BistroPresentation =>
                    contract.ReferenceBeautyBookmark,
                SampleBenchmarkTrajectoryKind.BistroSnapshotIncident =>
                    SampleBistroQualityCaptureContract
                        .SnapshotIncidentBookmark,
                _ => contract.ResolveCamera(ValidateFrameIndex(
                    kind,
                    trajectoryFrameIndex))
            };
            return FromBistro(bookmark);
        }

        SampleSponzaGiCaptureContract sponza =
            SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiCameraBookmark sponzaBookmark = kind switch
        {
            SampleBenchmarkTrajectoryKind.SponzaLow => sponza.LowBookmark,
            SampleBenchmarkTrajectoryKind.SponzaHigh => sponza.HighBookmark,
            SampleBenchmarkTrajectoryKind.SponzaReceiverCacheIncident =>
                SampleSponzaGiCaptureContract.ReceiverCacheIncidentBookmark,
            SampleBenchmarkTrajectoryKind.SponzaSnapshotIncident =>
                SampleSponzaGiCaptureContract.SnapshotIncidentBookmark,
            SampleBenchmarkTrajectoryKind.SponzaHorizontal =>
                sponza.SampleMotionTraversalFrame(ValidateFrameIndex(
                    kind,
                    trajectoryFrameIndex)),
            SampleBenchmarkTrajectoryKind.SponzaVertical =>
                sponza.SampleVerticalTraversalFrame(ValidateFrameIndex(
                    kind,
                    trajectoryFrameIndex)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return FromSponza(sponzaBookmark);
    }

    public static IReadOnlyList<string> ValidateCamera(
        SampleBenchmarkTrajectoryKind kind,
        int trajectoryFrameIndex,
        SampleBistroQualityCaptureVariant bistroVariant,
        PerformanceCaptureCameraMetadata actual)
    {
        ArgumentNullException.ThrowIfNull(actual);
        SampleBenchmarkCameraPose? expected = ResolveCamera(
            kind,
            trajectoryFrameIndex,
            bistroVariant);
        if (expected == null)
            return Array.Empty<string>();

        var mismatches = new List<string>();
        Compare(mismatches, "position X", expected.Position.X, actual.PositionX);
        Compare(mismatches, "position Y", expected.Position.Y, actual.PositionY);
        Compare(mismatches, "position Z", expected.Position.Z, actual.PositionZ);
        Compare(mismatches, "yaw", expected.Yaw, actual.YawRadians);
        Compare(mismatches, "pitch", expected.Pitch, actual.PitchRadians);
        Compare(
            mismatches,
            "field of view",
            expected.FieldOfView,
            actual.FieldOfViewRadians);
        Compare(mismatches, "near plane", expected.NearPlane, actual.NearPlane);
        Compare(mismatches, "far plane", expected.FarPlane, actual.FarPlane);
        return mismatches;
    }

    private static int ValidateFrameIndex(
        SampleBenchmarkTrajectoryKind kind,
        int frameIndex)
    {
        int frameCount = GetFrameCount(kind);
        if (frameIndex < 0 || frameIndex >= frameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex),
                frameIndex,
                $"Trajectory '{GetName(kind)}' contains frames 0 through " +
                $"{frameCount - 1}.");
        }
        return frameIndex;
    }

    private static void Compare(
        ICollection<string> mismatches,
        string field,
        float expected,
        float actual)
    {
        if (!float.IsFinite(actual) ||
            MathF.Abs(expected - actual) > CameraValueTolerance)
        {
            mismatches.Add(
                $"{field} expected {expected:R}, captured {actual:R}");
        }
    }

    private static void AppendCamera(
        StringBuilder canonical,
        int index,
        SampleBenchmarkCameraPose pose)
    {
        canonical.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Position.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Position.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Position.Z.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Yaw.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.Pitch.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.FieldOfView.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(pose.FarPlane.ToString("R", CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static void AppendCamera(
        StringBuilder canonical,
        int index,
        PerformanceCaptureCameraMetadata camera)
    {
        canonical.Append(index.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.PositionX.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.PositionY.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.PositionZ.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.YawRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.PitchRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.FieldOfViewRadians.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.NearPlane.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(camera.FarPlane.ToString("R", CultureInfo.InvariantCulture))
            .Append('\n');
    }

    private static SampleBenchmarkCameraPose FromBistro(
        SampleBistroQualityCameraBookmark bookmark) => new(
        bookmark.Name,
        bookmark.Position,
        bookmark.Yaw,
        bookmark.Pitch,
        bookmark.FieldOfView,
        bookmark.NearPlane,
        bookmark.FarPlane);

    private static SampleBenchmarkCameraPose FromSponza(
        SampleSponzaGiCameraBookmark bookmark) => new(
        bookmark.Name,
        bookmark.Position,
        bookmark.Yaw,
        bookmark.Pitch,
        bookmark.FieldOfView,
        bookmark.NearPlane,
        bookmark.FarPlane);
}
