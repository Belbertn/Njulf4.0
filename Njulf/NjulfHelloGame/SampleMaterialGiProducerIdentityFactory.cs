using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;

namespace NjulfHelloGame;

internal static class SampleMaterialGiProducerIdentityFactory
{
    public static MaterialGiProducerIdentity Create(
        RendererDiagnostics diagnostics,
        string settingsFingerprint,
        string qualityTier = "")
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return Create(
            diagnostics.CaptureRun.Commit,
            diagnostics.CaptureRun.ShaderBundleHash,
            settingsFingerprint,
            diagnostics.CaptureGpuDeviceName,
            diagnostics.CaptureGpuDriverVersion,
            qualityTier);
    }

    public static MaterialGiProducerIdentity Create(
        SampleMaterialGiRendererProvenance provenance,
        string qualityTier = "")
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return Create(
            provenance.Commit,
            provenance.ShaderBundleHash,
            provenance.SettingsFingerprint,
            provenance.GpuDevice,
            provenance.GpuDriver,
            qualityTier);
    }

    public static MaterialGiProducerIdentity CreateGraphicsAsyncPair(
        SampleMaterialGiRendererProvenance graphics,
        SampleMaterialGiRendererProvenance async)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ArgumentNullException.ThrowIfNull(async);
        string graphicsSettings =
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                graphics.SettingsFingerprint);
        string asyncSettings =
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                async.SettingsFingerprint);
        MaterialGiProducerIdentity common = Create(graphics);
        RequireExact(
            common.BuildCommit,
            NormalizeBuildCommit(async.Commit),
            "build commit");
        RequireExact(
            common.ShaderFingerprint,
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                async.ShaderBundleHash),
            "shader fingerprint");
        RequireExact(common.GpuName, RequireIdentity(async.GpuDevice, "GPU name"), "GPU name");
        RequireExact(
            common.DriverVersion,
            RequireIdentity(async.GpuDriver, "GPU driver"),
            "GPU driver");
        return common with
        {
            SettingsFingerprint =
                MaterialGiProducerSettingsFingerprint.ComputeGraphicsAsyncPair(
                    graphicsSettings,
                    asyncSettings),
            SourceSettingsFingerprints = [graphicsSettings, asyncSettings]
        };
    }

    private static MaterialGiProducerIdentity Create(
        string buildCommit,
        string shaderFingerprint,
        string settingsFingerprint,
        string gpuName,
        string driverVersion,
        string qualityTier)
    {
        string normalizedSettings =
            MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                settingsFingerprint);
        return new MaterialGiProducerIdentity
        {
            BuildCommit = NormalizeBuildCommit(buildCommit),
            ShaderFingerprint =
                MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                    shaderFingerprint),
            SettingsFingerprint = normalizedSettings,
            SourceSettingsFingerprints = [normalizedSettings],
            GpuName = RequireIdentity(gpuName, "GPU name"),
            DriverVersion = RequireIdentity(driverVersion, "GPU driver"),
            QualityTier = qualityTier ?? string.Empty
        };
    }

    private static string NormalizeBuildCommit(string value)
    {
        string normalized = RequireIdentity(value, "build commit")
            .ToLowerInvariant();
        if (normalized.Length != 40 ||
            normalized.Any(static character => !char.IsAsciiHexDigit(character)))
        {
            throw new InvalidDataException(
                "Producer build identity must be an exact 40-character Git commit.");
        }
        return normalized;
    }

    private static string RequireIdentity(string value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 512 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Contains('\0') ||
            value.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("unavailable:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Producer {role} is absent, non-canonical, or unavailable.");
        }
        return value;
    }

    private static void RequireExact(
        string left,
        string right,
        string role)
    {
        if (!string.Equals(left, right, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Graphics/async producer {role} differs between captures.");
        }
    }
}
