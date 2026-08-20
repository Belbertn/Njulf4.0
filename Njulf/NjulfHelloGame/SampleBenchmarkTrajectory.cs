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
    SponzaHorizontal,
    SponzaVertical
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
    public const string BistroLoopName = "bistro-loop";
    public const string SponzaLowName = "sponza-low";
    public const string SponzaHighName = "sponza-high";
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
            BistroLoopName => SampleBenchmarkTrajectoryKind.BistroLoop,
            SponzaLowName => SampleBenchmarkTrajectoryKind.SponzaLow,
            SponzaHighName => SampleBenchmarkTrajectoryKind.SponzaHigh,
            SponzaHorizontalName => SampleBenchmarkTrajectoryKind.SponzaHorizontal,
            SponzaVerticalName => SampleBenchmarkTrajectoryKind.SponzaVertical,
            _ => throw new ArgumentException(
                $"Unknown benchmark trajectory '{value}'. Valid values: " +
                $"{StationaryName}, {BistroPresentationName}, {BistroLoopName}, " +
                $"{SponzaLowName}, {SponzaHighName}, {SponzaHorizontalName}, " +
                $"{SponzaVerticalName}.",
                nameof(value))
        };
    }

    public static string GetName(SampleBenchmarkTrajectoryKind kind) => kind switch
    {
        SampleBenchmarkTrajectoryKind.Stationary => StationaryName,
        SampleBenchmarkTrajectoryKind.BistroPresentation => BistroPresentationName,
        SampleBenchmarkTrajectoryKind.BistroLoop => BistroLoopName,
        SampleBenchmarkTrajectoryKind.SponzaLow => SponzaLowName,
        SampleBenchmarkTrajectoryKind.SponzaHigh => SponzaHighName,
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
        SampleBenchmarkTrajectoryKind.BistroLoop;

    public static bool RequiresSponza(
        SampleBenchmarkTrajectoryKind kind) => kind is
        SampleBenchmarkTrajectoryKind.SponzaLow or
        SampleBenchmarkTrajectoryKind.SponzaHigh or
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

    public static string CreateFingerprint(
        SampleBenchmarkTrajectoryKind kind,
        SampleBistroQualityCaptureVariant bistroVariant)
    {
        string contractFingerprint = kind switch
        {
            SampleBenchmarkTrajectoryKind.BistroPresentation or
                SampleBenchmarkTrajectoryKind.BistroLoop =>
                new SampleBistroQualityCaptureContract(bistroVariant).Fingerprint,
            SampleBenchmarkTrajectoryKind.SponzaLow or
                SampleBenchmarkTrajectoryKind.SponzaHigh or
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
            SampleBistroQualityCameraBookmark bookmark = kind ==
                SampleBenchmarkTrajectoryKind.BistroPresentation
                    ? contract.ReferenceBeautyBookmark
                    : contract.ResolveCamera(ValidateFrameIndex(
                        kind,
                        trajectoryFrameIndex));
            return FromBistro(bookmark);
        }

        SampleSponzaGiCaptureContract sponza =
            SampleSponzaGiCaptureContract.Default;
        SampleSponzaGiCameraBookmark sponzaBookmark = kind switch
        {
            SampleBenchmarkTrajectoryKind.SponzaLow => sponza.LowBookmark,
            SampleBenchmarkTrajectoryKind.SponzaHigh => sponza.HighBookmark,
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
