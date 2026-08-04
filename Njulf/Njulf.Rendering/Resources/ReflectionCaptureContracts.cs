using System;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Rendering.Resources;

public readonly record struct ReflectionCubeFaceContract(
    Vector3 Forward,
    Vector3 Up,
    Vector3 Right);

/// <summary>
/// Canonical cubemap orientation. Face order is +X, -X, +Y, -Y, +Z, -Z and is shared by capture
/// views, array layers, prefilter dispatches, and debug inspection. Keeping one table avoids the
/// common failure where capture and sampling disagree only on the Y faces.
/// </summary>
public static class ReflectionCubeViewContract
{
    private static readonly ReflectionCubeFaceContract[] Faces =
    [
        new(new Vector3(1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, -1)),
        new(new Vector3(-1, 0, 0), new Vector3(0, -1, 0), new Vector3(0, 0, 1)),
        new(new Vector3(0, 1, 0), new Vector3(0, 0, 1), new Vector3(1, 0, 0)),
        new(new Vector3(0, -1, 0), new Vector3(0, 0, -1), new Vector3(1, 0, 0)),
        new(new Vector3(0, 0, 1), new Vector3(0, -1, 0), new Vector3(1, 0, 0)),
        new(new Vector3(0, 0, -1), new Vector3(0, -1, 0), new Vector3(-1, 0, 0))
    ];

    public static ReflectionCubeFaceContract Get(int face)
    {
        if ((uint)face >= 6U)
            throw new ArgumentOutOfRangeException(nameof(face));
        return Faces[face];
    }

    public static ReadOnlySpan<ReflectionCubeFaceContract> All => Faces;
}

public readonly record struct ReflectionCaptureViewContext(
    int Face,
    int CubemapArrayLayer,
    uint Resolution,
    Matrix4x4 View,
    Matrix4x4 Projection,
    Vector3 Position,
    float NearPlane,
    float FarPlane,
    uint ResourceGeneration,
    uint SceneRevision,
    ReflectionCaptureVersion Version,
    bool IncludesDdgi);

public static class ReflectionCaptureViewFactory
{
    public static ReflectionCaptureViewContext Create(
        in ReflectionProbeCaptureSnapshot snapshot,
        int face,
        int cubemapArrayLayer,
        uint resolution,
        uint resourceGeneration,
        uint sceneRevision,
        in ReflectionCaptureVersion version,
        bool includesDdgi)
    {
        if (resolution == 0U)
            throw new ArgumentOutOfRangeException(nameof(resolution));
        ReflectionCubeFaceContract contract = ReflectionCubeViewContract.Get(face);
        Matrix4x4 rotation = snapshot.Rotation.Normalized().ToMatrix4x4();
        Vector3 forward = TransformDirection(contract.Forward, rotation).Normalized();
        Vector3 up = TransformDirection(contract.Up, rotation).Normalized();
        float nearPlane = ResolveNearPlane(snapshot);
        float farPlane = ResolveFarPlane(snapshot);
        return new ReflectionCaptureViewContext(
            face,
            checked(cubemapArrayLayer * 6 + face),
            resolution,
            Matrix4x4.CreateLookAt(snapshot.Position, snapshot.Position + forward, up),
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI * 0.5f,
                1.0f,
                nearPlane,
                farPlane),
            snapshot.Position,
            nearPlane,
            farPlane,
            resourceGeneration,
            sceneRevision,
            version,
            includesDdgi);
    }

    private static float ResolveNearPlane(in ReflectionProbeCaptureSnapshot snapshot)
    {
        float minimumExtent = snapshot.Shape == ReflectionProbeShape.Sphere
            ? snapshot.Radius
            : MathF.Min(snapshot.BoxExtents.X, MathF.Min(snapshot.BoxExtents.Y, snapshot.BoxExtents.Z));
        return Math.Clamp(MathF.Max(minimumExtent * 0.001f, 0.01f), 0.01f, 1.0f);
    }

    private static float ResolveFarPlane(in ReflectionProbeCaptureSnapshot snapshot)
    {
        float extent = snapshot.Shape == ReflectionProbeShape.Sphere
            ? snapshot.Radius
            : MathF.Sqrt(
                snapshot.BoxExtents.X * snapshot.BoxExtents.X +
                snapshot.BoxExtents.Y * snapshot.BoxExtents.Y +
                snapshot.BoxExtents.Z * snapshot.BoxExtents.Z);
        return MathF.Max(extent * 2.0f, 10.0f);
    }

    private static Vector3 TransformDirection(Vector3 value, Matrix4x4 matrix) => new(
        value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
        value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
        value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);
}

public readonly record struct ReflectionPrefilterMipWork(
    int Mip,
    uint Resolution,
    float Roughness,
    int SampleCount,
    int FaceCount);

/// <summary>GGX split-sum contract for the private complete mip chain.</summary>
public static class ReflectionPrefilterContract
{
    public static ReflectionPrefilterMipWork GetMipWork(
        int mip,
        uint baseResolution,
        uint mipCount,
        int maximumSamples = 128)
    {
        if (baseResolution == 0U || mipCount == 0U)
            throw new ArgumentOutOfRangeException(nameof(baseResolution));
        if (mip <= 0 || (uint)mip >= mipCount)
            throw new ArgumentOutOfRangeException(nameof(mip));
        uint resolution = Math.Max(1U, baseResolution >> mip);
        float roughness = mipCount <= 1U ? 1.0f : mip / (float)(mipCount - 1U);
        int sampleCount = Math.Clamp(
            maximumSamples - mip * 8,
            16,
            Math.Max(16, maximumSamples));
        return new ReflectionPrefilterMipWork(mip, resolution, roughness, sampleCount, 6);
    }

    public static bool IsComplete(ReadOnlySpan<byte> initializedMips, uint mipCount)
    {
        if (mipCount == 0U || initializedMips.Length < mipCount)
            return false;
        for (int mip = 1; (uint)mip < mipCount; mip++)
        {
            if (initializedMips[mip] == 0)
                return false;
        }
        return true;
    }
}

public readonly record struct ReflectionProbeMemoryPlan(
    int PublishedProbeCapacity,
    uint Resolution,
    uint MipCount,
    ulong PublishedBytes,
    ulong PrivateScratchBytes,
    ulong CaptureDepthBytes,
    ulong TotalBytes)
{
    public static ReflectionProbeMemoryPlan Build(
        int publishedProbeCapacity,
        uint resolution,
        uint mipCount,
        uint depthBytesPerPixel = 4U,
        uint depthTargetCount = 1U)
    {
        if (publishedProbeCapacity < 0 || resolution == 0U || mipCount == 0U ||
            depthBytesPerPixel == 0U || depthTargetCount == 0U)
            throw new ArgumentOutOfRangeException(nameof(publishedProbeCapacity));
        ulong cubeChainTexels = ChainTexels(resolution, mipCount);
        ulong published = checked((ulong)publishedProbeCapacity * 6UL * cubeChainTexels * 8UL);
        ulong scratch = checked(6UL * cubeChainTexels * 8UL);
        ulong depth = checked((ulong)depthTargetCount * resolution * resolution * depthBytesPerPixel);
        return new ReflectionProbeMemoryPlan(
            publishedProbeCapacity,
            resolution,
            mipCount,
            published,
            scratch,
            depth,
            checked(published + scratch + depth));
    }

    private static ulong ChainTexels(uint resolution, uint mipCount)
    {
        ulong total = 0UL;
        uint size = resolution;
        for (uint mip = 0; mip < mipCount; mip++)
        {
            total = checked(total + (ulong)size * size);
            size = Math.Max(1U, size / 2U);
        }
        return total;
    }
}

public readonly record struct ReflectionCapturePublicationContract(
    Guid ProbeId,
    int Layer,
    uint ResourceGeneration,
    ReflectionCaptureVersion Version,
    ulong CompletionValue,
    bool CopySubmitted,
    bool PrefilterComplete,
    bool Published);
