using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Njulf.Core.Math;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Diagnostics;

internal static class PerformanceCaptureHashing
{
    internal static float ExtractPitchRadians(Vector3 forward) =>
        -MathF.Asin(Math.Clamp(forward.Y, -1.0f, 1.0f));

    internal static string ComputeMatrixHash(Matrix4x4 matrix)
    {
        string canonical = string.Join("|", new[]
        {
            matrix.M11.ToString("R", CultureInfo.InvariantCulture), matrix.M12.ToString("R", CultureInfo.InvariantCulture), matrix.M13.ToString("R", CultureInfo.InvariantCulture), matrix.M14.ToString("R", CultureInfo.InvariantCulture),
            matrix.M21.ToString("R", CultureInfo.InvariantCulture), matrix.M22.ToString("R", CultureInfo.InvariantCulture), matrix.M23.ToString("R", CultureInfo.InvariantCulture), matrix.M24.ToString("R", CultureInfo.InvariantCulture),
            matrix.M31.ToString("R", CultureInfo.InvariantCulture), matrix.M32.ToString("R", CultureInfo.InvariantCulture), matrix.M33.ToString("R", CultureInfo.InvariantCulture), matrix.M34.ToString("R", CultureInfo.InvariantCulture),
            matrix.M41.ToString("R", CultureInfo.InvariantCulture), matrix.M42.ToString("R", CultureInfo.InvariantCulture), matrix.M43.ToString("R", CultureInfo.InvariantCulture), matrix.M44.ToString("R", CultureInfo.InvariantCulture)
        });
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ComputeSceneStateHash(SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);

        string canonical = string.Join("|", new[]
        {
            sceneData.SceneContentRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.GiTransportMaterialRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.DdgiEmissiveSourceRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.DrawPacketRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.DirectionalShadowMeshletDrawSignature.ToString(CultureInfo.InvariantCulture),
            sceneData.LocalShadowMeshletDrawSignature.ToString(CultureInfo.InvariantCulture),
            sceneData.ObjectCount.ToString(CultureInfo.InvariantCulture),
            sceneData.MeshletCount.ToString(CultureInfo.InvariantCulture),
            sceneData.MaterialCount.ToString(CultureInfo.InvariantCulture),
            sceneData.TextureCount.ToString(CultureInfo.InvariantCulture),
            sceneData.LightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.DirectionalLightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.LocalLightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.GeometryDecalObjectCount.ToString(CultureInfo.InvariantCulture),
            sceneData.CaptureSceneName ?? string.Empty
        });
        return HashCanonicalText(canonical);
    }

    internal static string ComputeSceneAssetHash(SceneRenderingData sceneData)
    {
        ArgumentNullException.ThrowIfNull(sceneData);

        string canonical = string.Join("|", new[]
        {
            sceneData.SceneContentRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.GiTransportMaterialRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.DdgiEmissiveSourceRevision.ToString(CultureInfo.InvariantCulture),
            sceneData.ObjectCount.ToString(CultureInfo.InvariantCulture),
            sceneData.MaterialCount.ToString(CultureInfo.InvariantCulture),
            sceneData.TextureCount.ToString(CultureInfo.InvariantCulture),
            sceneData.LightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.DirectionalLightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.LocalLightCount.ToString(CultureInfo.InvariantCulture),
            sceneData.GeometryDecalObjectCount.ToString(CultureInfo.InvariantCulture),
            sceneData.CaptureSceneName ?? string.Empty
        });
        return HashCanonicalText(canonical);
    }

    internal static string ResolveScenario(string? scenario) =>
        NormalizeMetadataValue(
            scenario,
            "unavailable:active-scenario-not-supplied-by-renderer-client");

    internal static string ResolveSceneKind(
        string? sceneKind,
        string? sceneName)
    {
        string? supplied = string.IsNullOrWhiteSpace(sceneKind)
            ? sceneName
            : sceneKind;
        return NormalizeMetadataValue(
            supplied,
            "unavailable:scene-kind-not-reported");
    }

    internal static string ResolveShaderBundleHash(string? shaderBundleHash) =>
        NormalizeMetadataValue(
            shaderBundleHash,
            "unavailable:shader-bundle-hash-not-reported");

    internal static string NormalizeMetadataValue(
        string? value,
        string unavailableValue)
    {
        if (string.IsNullOrWhiteSpace(value))
            return unavailableValue;

        string normalized = value.Trim();
        return normalized.StartsWith("unknown", StringComparison.OrdinalIgnoreCase)
            ? unavailableValue
            : normalized;
    }

    internal static string? NormalizeSourceRevision(string? sourceRevision)
    {
        if (string.IsNullOrWhiteSpace(sourceRevision))
            return null;

        string revision = sourceRevision.Trim();
        if (revision.StartsWith("sha", StringComparison.OrdinalIgnoreCase))
        {
            int separatorIndex = revision.IndexOfAny([':', '-', '=']);
            if (separatorIndex >= 0 && separatorIndex < revision.Length - 1)
                revision = revision[(separatorIndex + 1)..].Trim();
        }

        if (revision.Length is < 7 or > 128)
            return null;

        for (int i = 0; i < revision.Length; i++)
        {
            if (!Uri.IsHexDigit(revision[i]))
                return null;
        }

        return revision.ToLowerInvariant();
    }

    private static string HashCanonicalText(string canonical)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }
}
