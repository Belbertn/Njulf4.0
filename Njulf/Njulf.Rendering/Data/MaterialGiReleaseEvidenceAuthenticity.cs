using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>
/// Authenticates the reports emitted by the release-gate producers. The
/// qualification wrapper is only an index: a role passes when its pinned
/// producer payload has the expected schema and its decisive measurements
/// independently satisfy the frozen release thresholds.
/// </summary>
internal static class MaterialGiReleaseEvidenceAuthenticity
{
    private const int MaximumJsonDepth = 64;
    private const int MaximumIdentityLength = 512;
    private const double GraphicsMaximumAbsoluteRmse = 0.002;
    private const double GraphicsMaximumRelativeRmse = 0.001;
    private const double GraphicsMaximumAbsoluteComponentError = 0.05;
    private const int KhronosMinimumSemanticPixels = 32;
    private const double KhronosMaximumUnlitRelativeRmse = 0.005;
    private const double KhronosMaximumEmissiveRelativeError = 0.01;
    private const double KhronosMinimumEmissionCoverage = 0.98;
    private const double KhronosMinimumPbrLightingResponse = 0.01;
    private const double ApprovedHdrMaximumRelativeRmse = 0.12;
    private const double ApprovedHdrMaximumFlipP95 = 0.08;
    private const double ApprovedHdrMaximumUniformLuminanceDifference = 0.05;
    private const double ApprovedHdrMaximumTransitionStep = 0.10;
    private const double ApprovedHdrMaximumLowFrequencyMeanDifference = 0.02;
    private const double ApprovedHdrMaximumTemporalP95 = 0.03;
    private const string ApprovedHdrMetricVersion = "nvidia-hdr-flip/v1.7";
    private const string ApprovedHdrRelativeRmseDefinition =
        "sqrt(mean((candidate-reference)^2)) / max(sqrt(mean(reference^2)), 1e-6 linear-radiance units)";
    private const string ApprovedHdrFlipMetricDefinition =
        "Nearest-rank P95 of the NVIDIA HDR-FLIP v1.7 per-pixel error map; " +
        "scene-linear RGB, PPD=67.0206451, ACES, reference-auto start/stop/count exposures, " +
        "source b475eb4 via FlipBinding.CSharp 1.0.3.";

    private static readonly string[] RequiredGraphicsAsyncSignals =
    [
        "DirectDiffuse",
        "DirectSpecular",
        "RawDdgiIrradiance",
        "FinalDdgiDiffuse",
        "FinalComposedIndirect",
        "MaterialDiffuseReflectance",
        "CompiledEmission",
        "MaterialOcclusion",
        "GiOwnershipWeights",
        "MaterialSidedness"
    ];

    private static readonly HashSet<string> RequiredBenchmarkMetricNames =
        new(StringComparer.Ordinal)
        {
            "CPU renderer",
            "GPU frame",
            "GPU memory",
            "Tracked GPU memory",
            "Upload",
            "Objects",
            "Meshlets",
            "Foliage clusters",
            "Foliage meshlet draws",
            "Foliage grass blades",
            "Foliage memory",
            "Materials",
            "Material GI primitive profile memory",
            "Material GI non-finite values",
            "Material GI clamped values",
            "Material alpha candidate limit",
            "Material GI release qualification",
            "Material GI active V1 fallbacks",
            "Material GI active invalid profiles",
            "DDGI emissive truncated sources",
            "DDGI emissive skipped energy",
            "DDGI emissive unsupported skinned objects",
            "DDGI emissive unsupported skinned importance",
            "Material GI compile P95",
            "Material GI upload P95",
            "Material GI compile/upload P95",
            "Textures",
            "Lights",
            "Shadowed lights",
            "Reflection probes",
            "GI GPU",
            "GI forward gather incremental",
            "GI CPU scheduling and upload",
            "GI unique residency",
            "GI resident acceleration structures",
            "Far-field page cache",
            "DDGI probes",
            "DDGI active probe budget",
            "DDGI update request budget",
            "DDGI atlas memory",
            "DDGI total memory",
            "Transparent objects"
        };

    private static readonly HashSet<string> KnownBenchmarkMetricNames =
        new(RequiredBenchmarkMetricNames, StringComparer.Ordinal)
        {
            "GI forward gather (inclusive draw)",
            "DDGI dirty first-update latency",
            "DDGI dirty convergence latency",
            "DDGI Environment first-visible latency",
            "DDGI Environment affected-region latency",
            "DDGI Environment certified latency",
            "DDGI Light first-visible latency",
            "DDGI Light affected-region latency",
            "DDGI Light certified latency",
            "DDGI Emissive first-visible latency",
            "DDGI Emissive affected-region latency",
            "DDGI Emissive certified latency",
            "DDGI Material first-visible latency",
            "DDGI Material affected-region latency",
            "DDGI Material certified latency",
            "DDGI Transform first-visible latency",
            "DDGI Transform affected-region latency",
            "DDGI Transform certified latency",
            "DDGI Topology first-visible latency",
            "DDGI Topology affected-region latency",
            "DDGI Topology certified latency",
            "DDGI cold-start certified latency",
            "GI memory",
            "DDGI probes updated"
        };

    private static readonly HashSet<string> RequiredDdgiProductionCriteria =
        new(StringComparer.Ordinal)
        {
            "required-production-scene",
            "ddgi-high-profile",
            "ddgi-only-ray-query-active",
            "ddgi-split-passes-present",
            "no-recursive-ddgi-copy",
            "ddgi-async-compute-state-consistent",
            "no-static-frame-full-as-rebuild",
            "blas-compaction-settled-and-lossless",
            "ddgi-ray-query-scene-complete",
            "ddgi-static-ray-coverage-complete",
            "requested-paged-far-field-active",
            "clipmaps-preserved-with-authored-volumes",
            "phase10-forward-metrics-valid",
            "phase9-raw-atlas-to-final-energy",
            "phase9-environment-fallback-not-dominant",
            "phase9-emissive-bounce-present",
            "phase9-thin-wall-leak-policy-active",
            "phase10-cache-warmup-steady",
            "phase10-warmup-progress-valid",
            "simple-ddgi-probe-lifecycle-bounded",
            "gpu-timing-valid",
            "simple-ddgi-transport-blend-p95-budget",
            "simple-ddgi-upload-p95-budget",
            "simple-ddgi-capacity-p95-budget",
            "simple-ddgi-transport-settled",
            "phase8-emergency-degrade-preserves-near-field",
            "ddgi-memory-budget",
            "phase8-tier-memory-budget",
            "phase10-ddgi-memory-diagnostics",
            "tracked-memory-headroom-20-percent",
            "budget-metrics-within-gate",
            "foliage-ddgi-receiver-covered",
            "debug-views-expose-ddgi-gate-data"
        };

    public static IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
        ValidateBundleIdentity(
            MaterialGiReleaseEvidenceBundle bundle,
            IReadOnlySet<string> qualifiedDeviceIds)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        RequireBuildCommit(bundle.BuildCommit, "release evidence bundle");
        RequireSha256(bundle.ShaderFingerprint, "release evidence bundle shader fingerprint");
        RequireSha256(
            bundle.SettingsContractFingerprint,
            "release evidence bundle settings-contract fingerprint");

        MaterialGiEvidenceDeviceIdentity[] devices = bundle.Devices ??
            throw new InvalidDataException(
                "Release evidence bundle device identity collection is null.");
        if (devices.Length != qualifiedDeviceIds.Count ||
            devices.Any(static device => device is null))
        {
            throw new InvalidDataException(
                "Release evidence bundle must contain exactly one GPU/driver identity for every qualified device.");
        }

        var result = new Dictionary<string, MaterialGiEvidenceDeviceIdentity>(
            StringComparer.OrdinalIgnoreCase);
        foreach (MaterialGiEvidenceDeviceIdentity device in devices)
        {
            RequireCanonicalText(
                device.DeviceId,
                "release evidence device identifier");
            RequireCanonicalText(
                device.GpuName,
                $"release evidence GPU name for '{device.DeviceId}'");
            RequireCanonicalText(
                device.DriverVersion,
                $"release evidence driver version for '{device.DeviceId}'");
            if (!qualifiedDeviceIds.Contains(device.DeviceId))
            {
                throw new InvalidDataException(
                    $"Release evidence device identity '{device.DeviceId}' is not represented in QualifiedDeviceIds.");
            }
            if (!result.TryAdd(device.DeviceId, device))
            {
                throw new InvalidDataException(
                    $"Release evidence device identity '{device.DeviceId}' is duplicated.");
            }
        }
        return result;
    }

    public static void ValidateRole(
        string manifestDirectory,
        MaterialGiReleaseEvidenceBundle bundle,
        MaterialGiReleaseEvidenceReport report,
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            authenticatedDevices,
        ISet<string> allPinnedPaths)
    {
        RequireCommonIdentity(bundle, report);
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            reportDevices = ValidateReportDevices(report, authenticatedDevices);
        MaterialGiProducerEvidenceArtifact[] producers = report.Producers ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' producer collection is null.");
        if (producers.Length == 0 ||
            producers.Length > MaterialGiReleaseEvidenceContract.MaximumProducerArtifactCount ||
            producers.Any(static producer => producer is null))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' must pin a bounded, non-empty producer artifact collection.");
        }

        var producerKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (MaterialGiProducerEvidenceArtifact producer in producers)
        {
            ValidateProducerIdentity(
                bundle,
                report,
                producer,
                reportDevices);
            string producerKey =
                $"{producer.Kind}\0{producer.DeviceId}\0{producer.QualityTier}";
            if (!producerKeys.Add(producerKey))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' duplicates producer '{producer.Kind}' " +
                    $"for device '{producer.DeviceId}' and tier '{producer.QualityTier}'.");
            }

            string producerPath = ResolveContainedPath(
                manifestDirectory,
                producer.ManifestRelativePath,
                $"producer '{producer.Kind}' path");
            if (!allPinnedPaths.Add(producerPath))
            {
                throw new InvalidDataException(
                    $"Producer artifact '{producer.ManifestRelativePath}' is duplicated or aliases another pinned qualification file.");
            }
            using JsonDocument document = OpenPinnedJson(producerPath, producer);
            ValidateProducerPayload(report, producer, document.RootElement);
        }

        ValidateProducerMatrix(report, producers, reportDevices.Keys);
    }

    private static void RequireCommonIdentity(
        MaterialGiReleaseEvidenceBundle bundle,
        MaterialGiReleaseEvidenceReport report)
    {
        RequireBuildCommit(report.BuildCommit, $"release evidence role '{report.Role}'");
        if (!string.Equals(
                report.BuildCommit,
                bundle.BuildCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' build commit does not match the bundle.");
        }
        RequireMatchingSha256(
            report.ShaderFingerprint,
            bundle.ShaderFingerprint,
            $"release evidence role '{report.Role}' shader fingerprint");
        RequireMatchingSha256(
            report.SettingsContractFingerprint,
            bundle.SettingsContractFingerprint,
            $"release evidence role '{report.Role}' settings-contract fingerprint");
    }

    private static IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
        ValidateReportDevices(
            MaterialGiReleaseEvidenceReport report,
            IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
                authenticatedDevices)
    {
        MaterialGiEvidenceDeviceIdentity[] devices = report.Devices ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' device identity collection is null.");
        string[] reportDeviceIds = report.DeviceIds ??
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' device collection is null.");
        if (devices.Length != reportDeviceIds.Length ||
            devices.Any(static device => device is null))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' must include exactly one GPU/driver identity per DeviceIds entry.");
        }

        var result = new Dictionary<string, MaterialGiEvidenceDeviceIdentity>(
            StringComparer.OrdinalIgnoreCase);
        foreach (MaterialGiEvidenceDeviceIdentity device in devices)
        {
            if (!authenticatedDevices.TryGetValue(
                    device.DeviceId,
                    out MaterialGiEvidenceDeviceIdentity? expected) ||
                !string.Equals(
                    device.DeviceId,
                    expected.DeviceId,
                    StringComparison.Ordinal) ||
                !string.Equals(device.GpuName, expected.GpuName, StringComparison.Ordinal) ||
                !string.Equals(
                    device.DriverVersion,
                    expected.DriverVersion,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' has mismatched GPU/driver identity for device '{device.DeviceId}'.");
            }
            if (!result.TryAdd(device.DeviceId, device))
            {
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' duplicates device identity '{device.DeviceId}'.");
            }
        }
        if (reportDeviceIds.Any(deviceId => !result.ContainsKey(deviceId)))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' device identities do not exactly cover DeviceIds.");
        }
        return result;
    }

    private static void ValidateProducerIdentity(
        MaterialGiReleaseEvidenceBundle bundle,
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact producer,
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            reportDevices)
    {
        RequireCanonicalText(producer.Kind, "producer kind");
        RequireCanonicalText(producer.Schema, $"producer '{producer.Kind}' schema");
        RequireCanonicalText(producer.DeviceId, $"producer '{producer.Kind}' device identifier");
        if (!reportDevices.TryGetValue(
                producer.DeviceId,
                out MaterialGiEvidenceDeviceIdentity? device) ||
            !string.Equals(
                producer.DeviceId,
                device.DeviceId,
                StringComparison.Ordinal) ||
            !string.Equals(producer.GpuName, device.GpuName, StringComparison.Ordinal) ||
            !string.Equals(
                producer.DriverVersion,
                device.DriverVersion,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' GPU/driver identity does not match role '{report.Role}'.");
        }
        if (!string.Equals(
                producer.BuildCommit,
                bundle.BuildCommit,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' build commit does not match the release bundle.");
        }
        RequireMatchingSha256(
            producer.ShaderFingerprint,
            bundle.ShaderFingerprint,
            $"producer '{producer.Kind}' shader fingerprint");
        RequireSha256(
            producer.SettingsFingerprint,
            $"producer '{producer.Kind}' actual settings fingerprint");
        if (producer.ByteLength <= 0 ||
            producer.ByteLength > MaterialGiReleaseEvidenceContract.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' has an invalid bounded byte length.");
        }
        RequireSha256(producer.Sha256, $"producer '{producer.Kind}' SHA-256");
        if (producer.QualityTier.Length != 0 &&
            !MaterialGiReleaseEvidenceContract.RequiredQualityTiers.Contains(
                producer.QualityTier,
                StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' has unknown quality tier '{producer.QualityTier}'.");
        }
    }

    private static void ValidateProducerMatrix(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact[] producers,
        IEnumerable<string> deviceIds)
    {
        string[] devices = deviceIds
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        switch (report.Role)
        {
            case MaterialGiReleaseEvidenceContract.ApprovedHdrRole:
                RequireExactKindCount(
                    report,
                    producers,
                    MaterialGiReleaseEvidenceContract.ApprovedHdrProducerKind,
                    1);
                break;
            case MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole:
                RequireExactKindCount(
                    report,
                    producers,
                    MaterialGiReleaseEvidenceContract.KhronosRenderedProducerKind,
                    1);
                break;
            case MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole:
                RequireExactKindCount(
                    report,
                    producers,
                    MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerKind,
                    1);
                break;
            case MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole:
                RequireOneProducerPerDevice(
                    report,
                    producers,
                    devices,
                    MaterialGiReleaseEvidenceContract.TestMatrixProducerKind);
                break;
            case MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole:
                RequireTierMatrix(report, producers, devices);
                break;
            case MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole:
                RequireOneProducerPerDevice(
                    report,
                    producers.Where(static producer =>
                        string.Equals(
                            producer.Kind,
                            MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                            StringComparison.Ordinal)).ToArray(),
                    devices,
                    MaterialGiReleaseEvidenceContract.LongRunProducerKind);
                RequireOneProducerPerDevice(
                    report,
                    producers.Where(static producer =>
                        string.Equals(
                            producer.Kind,
                            MaterialGiReleaseEvidenceContract.HealthProducerKind,
                            StringComparison.Ordinal)).ToArray(),
                    devices,
                    MaterialGiReleaseEvidenceContract.HealthProducerKind);
                if (producers.Length != devices.Length * 2)
                {
                    throw new InvalidDataException(
                        "Thirty-minute soak evidence must contain exactly one long-run and one renderer-health producer per device.");
                }
                break;
            case MaterialGiReleaseEvidenceContract.CleanValidationRole:
            case MaterialGiReleaseEvidenceContract.LifecycleResilienceRole:
            case MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole:
            case MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole:
            case MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole:
                RequireOneProducerPerDevice(
                    report,
                    producers,
                    devices,
                    MaterialGiReleaseEvidenceContract.HealthProducerKind);
                break;
            default:
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' has no producer authenticity contract.");
        }
    }

    private static void RequireExactKindCount(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact[] producers,
        string kind,
        int count)
    {
        if (producers.Length != count ||
            producers.Any(producer =>
                !string.Equals(producer.Kind, kind, StringComparison.Ordinal) ||
                producer.QualityTier.Length != 0))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' must contain exactly {count} '{kind}' producer artifact(s).");
        }
    }

    private static void RequireOneProducerPerDevice(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact[] producers,
        IReadOnlyCollection<string> devices,
        string kind)
    {
        if (producers.Length != devices.Count ||
            producers.Any(producer =>
                !string.Equals(producer.Kind, kind, StringComparison.Ordinal) ||
                producer.QualityTier.Length != 0) ||
            devices.Any(device =>
                producers.Count(producer =>
                    string.Equals(
                        producer.DeviceId,
                        device,
                        StringComparison.OrdinalIgnoreCase)) != 1))
        {
            throw new InvalidDataException(
                $"Release evidence role '{report.Role}' must contain exactly one '{kind}' producer per device.");
        }
    }

    private static void RequireTierMatrix(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact[] producers,
        IReadOnlyCollection<string> devices)
    {
        int expectedCount = checked(
            devices.Count *
            MaterialGiReleaseEvidenceContract.RequiredQualityTiers.Count);
        if (producers.Length != expectedCount ||
            producers.Any(producer =>
                !string.Equals(
                    producer.Kind,
                    MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
                    StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "Tier performance evidence must contain exactly one benchmark producer for every qualified device/tier pair.");
        }
        foreach (string device in devices)
        {
            foreach (string tier in
                     MaterialGiReleaseEvidenceContract.RequiredQualityTiers)
            {
                if (producers.Count(producer =>
                        string.Equals(
                            producer.DeviceId,
                            device,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(
                            producer.QualityTier,
                            tier,
                            StringComparison.Ordinal)) != 1)
                {
                    throw new InvalidDataException(
                        $"Tier performance evidence is missing the exact '{device}'/'{tier}' benchmark producer.");
                }
            }
        }
    }

    private static void ValidateProducerPayload(
        MaterialGiReleaseEvidenceReport report,
        MaterialGiProducerEvidenceArtifact producer,
        JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' payload must be a JSON object.");
        }

        if (!string.Equals(
                producer.Kind,
                MaterialGiReleaseEvidenceContract.TestMatrixProducerKind,
                StringComparison.Ordinal))
        {
            ValidateEmbeddedProducerIdentity(root, producer);
        }

        switch (report.Role)
        {
            case MaterialGiReleaseEvidenceContract.ApprovedHdrRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.ApprovedHdrProducerKind,
                    MaterialGiReleaseEvidenceContract.ApprovedHdrProducerSchema);
                ValidateApprovedHdr(root);
                break;
            case MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.KhronosRenderedProducerKind,
                    MaterialGiReleaseEvidenceContract.KhronosRenderedProducerSchema);
                ValidateKhronosRendered(root, producer);
                break;
            case MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerKind,
                    MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerSchema);
                ValidateGraphicsAsync(root);
                break;
            case MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.TestMatrixProducerKind,
                    MaterialGiReleaseEvidenceContract.TestMatrixProducerSchema);
                ValidateTestMatrix(root, producer);
                break;
            case MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
                    MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema);
                ValidateBenchmark(root, producer);
                break;
            case MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole:
                if (string.Equals(
                        producer.Kind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        StringComparison.Ordinal))
                {
                    RequireProducerContract(
                        producer,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerSchema);
                    ValidateLongRun(root);
                }
                else
                {
                    RequireProducerContract(
                        producer,
                        MaterialGiReleaseEvidenceContract.HealthProducerKind,
                        MaterialGiReleaseEvidenceContract.HealthProducerSchema);
                    ValidateHealth(root, producer, report);
                }
                break;
            case MaterialGiReleaseEvidenceContract.CleanValidationRole:
            case MaterialGiReleaseEvidenceContract.LifecycleResilienceRole:
            case MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole:
            case MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole:
            case MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole:
                RequireProducerContract(
                    producer,
                    MaterialGiReleaseEvidenceContract.HealthProducerKind,
                    MaterialGiReleaseEvidenceContract.HealthProducerSchema);
                ValidateHealth(root, producer, report);
                break;
            default:
                throw new InvalidDataException(
                    $"Release evidence role '{report.Role}' has no producer validator.");
        }
    }

    private static void ValidateEmbeddedProducerIdentity(
        JsonElement root,
        MaterialGiProducerEvidenceArtifact producer)
    {
        JsonElement identity = RequireObject(root, "producerIdentity");
        RequireString(
            identity,
            "schema",
            MaterialGiProducerIdentity.CurrentSchema);
        RequireString(identity, "buildCommit", producer.BuildCommit);
        RequireString(
            identity,
            "shaderFingerprint",
            producer.ShaderFingerprint);
        RequireString(
            identity,
            "settingsFingerprint",
            producer.SettingsFingerprint);
        RequireString(identity, "gpuName", producer.GpuName);
        RequireString(identity, "driverVersion", producer.DriverVersion);
        RequireString(identity, "qualityTier", producer.QualityTier);

        JsonElement settingsSources =
            RequireProperty(identity, "sourceSettingsFingerprints");
        if (settingsSources.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' settings source identities must be an array.");
        }
        string[] sources = settingsSources
            .EnumerateArray()
            .Select((source, index) =>
            {
                if (source.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        $"Producer '{producer.Kind}' settings source identity {index} must be a string.");
                }
                string value = source.GetString() ?? string.Empty;
                RequireSha256(
                    value,
                    $"producer '{producer.Kind}' settings source identity {index}");
                return value;
            })
            .ToArray();

        if (string.Equals(
                producer.Kind,
                MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerKind,
                StringComparison.Ordinal))
        {
            if (sources.Length != 2)
            {
                throw new InvalidDataException(
                    "Graphics/async producer identity must bind exactly the ordered graphics and async settings fingerprints.");
            }
            string aggregate =
                MaterialGiProducerSettingsFingerprint.ComputeGraphicsAsyncPair(
                    sources[0],
                    sources[1]);
            RequireMatchingSha256(
                aggregate,
                producer.SettingsFingerprint,
                "graphics/async producer settings pair fingerprint");
            return;
        }

        if (sources.Length != 1)
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' identity must bind exactly one runtime settings fingerprint.");
        }
        RequireMatchingSha256(
            sources[0],
            producer.SettingsFingerprint,
            $"producer '{producer.Kind}' runtime settings fingerprint");
    }

    private static void RequireProducerContract(
        MaterialGiProducerEvidenceArtifact producer,
        string kind,
        string schema)
    {
        if (!string.Equals(producer.Kind, kind, StringComparison.Ordinal) ||
            !string.Equals(producer.Schema, schema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer contract '{producer.Kind}'/'{producer.Schema}' is invalid; expected '{kind}'/'{schema}'.");
        }
    }

    private static void ValidateApprovedHdr(JsonElement root)
    {
        RequireString(root, "schemaVersion", MaterialGiReleaseEvidenceContract.ApprovedHdrProducerSchema);
        RequireString(root, "status", "passed");
        RequireEmptyString(root, "failureReason");
        RequireJsonSha256(root, "approvedReferenceManifestSha256");
        RequireJsonSha256(root, "referenceCaptureManifestSha256");
        RequireJsonSha256(root, "candidateCaptureManifestSha256");
        RequireJsonSha256(root, "contractFingerprint");
        RequireString(root, "metricVersion", ApprovedHdrMetricVersion);
        RequireString(
            root,
            "relativeRmseDefinition",
            ApprovedHdrRelativeRmseDefinition);
        RequireString(
            root,
            "flipMetricDefinition",
            ApprovedHdrFlipMetricDefinition);
        JsonElement flip = RequireObject(root, "flipConfiguration");
        RequireString(flip, "nvidiaFlipVersion", "1.7");
        RequireString(flip, "nvidiaSourceRevision", "b475eb4");
        RequireString(flip, "bindingPackage", "FlipBinding.CSharp");
        RequireString(flip, "bindingVersion", "1.0.3");
        RequireExactDouble(flip, "pixelsPerDegree", 67.0206451);
        RequireString(flip, "toneMapper", "aces");
        RequireString(flip, "startExposure", "reference-auto");
        RequireString(flip, "stopExposure", "reference-auto");
        RequireString(flip, "numberOfExposures", "reference-auto");

        JsonElement approval = RequireObject(root, "approval");
        RequireCanonicalJsonString(approval, "approvalId");
        RequireCanonicalJsonString(approval, "reviewer");
        RequireCanonicalJsonString(approval, "reason");
        DateTimeOffset approvedAt = RequireDateTimeOffset(approval, "approvedAtUtc");
        if (approvedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Approved HDR producer approval timestamp must be UTC.");

        JsonElement images = RequireNonEmptyArray(root, "images");
        var imageSignals = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement image in images.EnumerateArray())
        {
            string signal = RequireJsonString(image, "signal");
            if (!imageSignals.Add(signal))
            {
                throw new InvalidDataException(
                    $"Approved HDR image signal '{signal}' is duplicated.");
            }
            RequireTrue(image, "passed");
            RequirePositiveInt64(image, "componentCount");
            RequireFiniteNonNegative(image, "referenceRms");
            RequireFiniteNonNegative(image, "absoluteRmse");
            double relativeRmse = RequireFiniteNonNegative(image, "relativeRmse");
            double flipP95 = RequireFiniteNonNegative(image, "flipP95");
            RequireExactDouble(
                image,
                "maximumRelativeRmse",
                ApprovedHdrMaximumRelativeRmse);
            RequireExactDouble(
                image,
                "maximumFlipP95",
                ApprovedHdrMaximumFlipP95);
            if (relativeRmse > ApprovedHdrMaximumRelativeRmse ||
                flipP95 > ApprovedHdrMaximumFlipP95)
            {
                throw new InvalidDataException(
                    "Approved HDR producer measurements exceed the frozen release thresholds.");
            }
            RequireJsonSha256(image, "referenceSha256");
            RequireJsonSha256(image, "candidateSha256");
        }
        if (!imageSignals.SetEquals(["FinalComposedIndirect"]))
        {
            throw new InvalidDataException(
                "Approved HDR producer must contain exactly the FinalComposedIndirect global image gate.");
        }

        JsonElement roiGates = RequireNonEmptyArray(root, "roiGates");
        var roiKinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement gate in roiGates.EnumerateArray())
        {
            RequireCanonicalJsonString(gate, "roi");
            string kind = RequireJsonString(gate, "kind");
            if (!roiKinds.Add(kind))
            {
                throw new InvalidDataException(
                    $"Approved HDR ROI gate kind '{kind}' is duplicated.");
            }
            RequireCanonicalJsonString(gate, "signal");
            RequireCanonicalJsonString(gate, "comparisonDefinition");
            RequireTrue(gate, "passed");
            RequirePositiveInt64(gate, "sampleCount");
            double measured =
                RequireFiniteNonNegative(gate, "measuredRelativeDifference");
            double maximum = kind switch
            {
                "UniformLuminance" =>
                    ApprovedHdrMaximumUniformLuminanceDifference,
                "TransitionStep" =>
                    ApprovedHdrMaximumTransitionStep,
                "LowFrequencyMean" =>
                    ApprovedHdrMaximumLowFrequencyMeanDifference,
                "TemporalStability" =>
                    ApprovedHdrMaximumTemporalP95,
                _ => throw new InvalidDataException(
                    $"Approved HDR ROI gate kind '{kind}' is not part of the frozen release contract.")
            };
            RequireExactDouble(gate, "maximumRelativeDifference", maximum);
            if (measured > maximum)
            {
                throw new InvalidDataException(
                    "Approved HDR ROI measurement exceeds its frozen release threshold.");
            }
            JsonElement hashes = RequireNonEmptyArray(gate, "evidenceSha256");
            foreach (JsonElement hash in hashes.EnumerateArray())
            {
                if (hash.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Approved HDR ROI evidence hash must be a string.");
                RequireSha256(hash.GetString(), "approved HDR ROI evidence hash");
            }
        }
        if (!roiKinds.SetEquals(
            [
                "UniformLuminance",
                "TransitionStep",
                "LowFrequencyMean",
                "TemporalStability"
            ]))
        {
            throw new InvalidDataException(
                "Approved HDR producer must contain exactly the frozen uniform, transition, low-frequency, and temporal ROI gates.");
        }
    }

    private static void ValidateKhronosRendered(
        JsonElement root,
        MaterialGiProducerEvidenceArtifact producer)
    {
        RequireInt32(root, "schemaVersion", 3);
        RequireString(root, "schema", MaterialGiReleaseEvidenceContract.KhronosRenderedProducerSchema);
        RequireString(root, "status", "Passed");
        RequireNullOrEmpty(root, "failure");
        RequireEmptyArray(root, "failures");
        RequirePositiveInt64(root, "assetCount");
        RequirePositiveInt64(root, "semanticMaterialCount");
        RequirePositiveInt64(root, "semanticSubMeshCount");
        RequirePositiveInt64(root, "runtimeMaterialCount");
        RequirePositiveInt64(root, "runtimeSubMeshCount");
        RequirePositiveInt64(root, "renderedFrameCount");
        RequireString(root, "gpuDevice", producer.GpuName);
        RequireString(root, "gpuDriver", producer.DriverVersion);
        RequireTrue(root, "strictCookedPolicy");
        RequireFalse(root, "sourceFallbackEnabled");
        RequireJsonSha256(root, "manifestSha256");
        RequireJsonSha256(root, "semanticGateReportSha256");
        RequireJsonSha256(root, "packageSha256");
        RequireJsonSha256(root, "captureSha256");

        JsonElement validation = RequireObject(root, "validation");
        string mode = RequireJsonString(validation, "mode");
        if (string.Equals(mode, "Off", StringComparison.Ordinal))
            throw new InvalidDataException("Khronos rendered validation was disabled.");
        RequireInt32(validation, "warningMessageCount", 0);
        RequireInt32(validation, "errorMessageCount", 0);
        _ = RequireObject(root, "capture");
        JsonElement semantic = RequireObject(root, "semanticRender");
        JsonElement metrics = RequireObject(semantic, "metrics");
        if (RequireInt64(metrics, "unlitPixelCount") < KhronosMinimumSemanticPixels ||
            RequireFiniteNonNegative(metrics, "unlitLightingRelativeRmse") >
                KhronosMaximumUnlitRelativeRmse ||
            RequireInt64(metrics, "lightingResponsivePbrPixelCount") <
                KhronosMinimumSemanticPixels ||
            RequireFiniteNonNegative(metrics, "meanPbrLightingResponse") <
                KhronosMinimumPbrLightingResponse)
        {
            throw new InvalidDataException(
                "Khronos rendered semantic metrics do not satisfy the frozen semantic gate.");
        }

        JsonElement strengths = RequireNonEmptyArray(metrics, "emissiveStrengths");
        double[] expectedStrengths = [1, 2, 4, 8, 16];
        double[] actualStrengths = strengths.EnumerateArray()
            .Select(strength =>
            {
                if (RequireInt64(strength, "pixelCount") < KhronosMinimumSemanticPixels ||
                    RequireFiniteNonNegative(
                        strength,
                        "maximumRelativeRadianceError") >
                        KhronosMaximumEmissiveRelativeError ||
                    RequireFiniteNonNegative(
                        strength,
                        "beautyEmissionCoverageRatio") <
                        KhronosMinimumEmissionCoverage)
                {
                    throw new InvalidDataException(
                        "Khronos rendered emissive semantic measurements fail the frozen thresholds.");
                }
                return RequireFiniteNonNegative(strength, "strength");
            })
            .OrderBy(static value => value)
            .ToArray();
        if (!actualStrengths.SequenceEqual(expectedStrengths))
        {
            throw new InvalidDataException(
                "Khronos rendered evidence does not cover the exact official emissive-strength matrix.");
        }
    }

    private static void ValidateGraphicsAsync(JsonElement root)
    {
        RequireString(root, "schemaVersion", MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerSchema);
        RequireString(root, "status", "passed");
        RequireEmptyString(root, "failureReason");
        JsonElement tolerance = RequireObject(root, "tolerance");
        RequireExactDouble(
            tolerance,
            "maximumAbsoluteRmse",
            GraphicsMaximumAbsoluteRmse);
        RequireExactDouble(
            tolerance,
            "maximumRelativeRmse",
            GraphicsMaximumRelativeRmse);
        RequireExactDouble(
            tolerance,
            "maximumAbsoluteComponentError",
            GraphicsMaximumAbsoluteComponentError);
        JsonElement outputs = RequireNonEmptyArray(root, "outputs");
        var signals = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement output in outputs.EnumerateArray())
        {
            string signal = RequireJsonString(output, "signal");
            if (!signals.Add(signal))
            {
                throw new InvalidDataException(
                    $"Graphics/async output signal '{signal}' is duplicated.");
            }
            RequireTrue(output, "passed");
            RequirePositiveInt64(output, "componentCount");
            if (RequireFiniteNonNegative(output, "absoluteRmse") >
                    GraphicsMaximumAbsoluteRmse ||
                RequireFiniteNonNegative(output, "relativeRmse") >
                    GraphicsMaximumRelativeRmse ||
                RequireFiniteNonNegative(
                    output,
                    "maximumAbsoluteComponentError") >
                    GraphicsMaximumAbsoluteComponentError)
            {
                throw new InvalidDataException(
                    "Graphics/async producer measurements exceed the frozen equivalence thresholds.");
            }
            RequireJsonSha256(output, "referenceSha256");
            RequireJsonSha256(output, "candidateSha256");
        }
        if (!signals.SetEquals(RequiredGraphicsAsyncSignals))
        {
            throw new InvalidDataException(
                "Graphics/async producer does not contain the exact material/GI conformance signal set.");
        }
    }

    private static void ValidateTestMatrix(
        JsonElement root,
        MaterialGiProducerEvidenceArtifact producer)
    {
        RequireString(root, "Schema", MaterialGiReleaseEvidenceContract.TestMatrixProducerSchema);
        RequireString(root, "Kind", MaterialGiReleaseEvidenceContract.TestMatrixProducerKind);
        RequireString(root, "Status", MaterialGiReleaseEvidenceContract.PassedStatus);
        RequireString(root, "BuildConfiguration", "Release");
        RequireString(root, "BuildCommit", producer.BuildCommit);
        RequireString(
            root,
            "ShaderFingerprint",
            producer.ShaderFingerprint);
        RequireString(
            root,
            "SettingsFingerprint",
            producer.SettingsFingerprint);
        JsonElement device = RequireObject(root, "Device");
        RequireString(device, "DeviceId", producer.DeviceId);
        RequireString(device, "GpuName", producer.GpuName);
        RequireString(
            device,
            "DriverVersion",
            producer.DriverVersion);
        JsonElement results = RequireNonEmptyArray(root, "Results");
        var resultNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement result in results.EnumerateArray())
        {
            string name = RequireJsonString(result, "Name");
            if (!resultNames.Add(name))
                throw new InvalidDataException($"Test-matrix result '{name}' is duplicated.");
            RequireString(result, "Status", MaterialGiReleaseEvidenceContract.PassedStatus);
            if (RequireInt64(result, "PassedCount") <= 0 ||
                RequireInt64(result, "FailedCount") != 0 ||
                RequireInt64(result, "SkippedCount") != 0)
            {
                throw new InvalidDataException(
                    $"Test-matrix result '{name}' is incomplete, failed, or skipped.");
            }
        }
        if (!resultNames.SetEquals(
                MaterialGiReleaseEvidenceContract.RequiredOracleReleaseChecks))
        {
            throw new InvalidDataException(
                "Test-matrix producer does not contain the exact CPU/GPU oracle and Release build/test checks.");
        }
    }

    private static void ValidateBenchmark(
        JsonElement root,
        MaterialGiProducerEvidenceArtifact producer)
    {
        RequireString(
            root,
            "Schema",
            MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema);
        RequireString(root, "Kind", MaterialGiReleaseEvidenceContract.BenchmarkProducerKind);
        DateTimeOffset capturedAt = RequireDateTimeOffset(root, "CapturedAtUtc");
        if (capturedAt.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Benchmark capture timestamp must be UTC.");
        RenderBudgetProfile profile =
            ResolveBenchmarkProfile(producer.QualityTier);
        RequireEnumValue(root, "Scenario", 14, "GiSimpleDdgiFurnace");
        JsonElement options = RequireObject(root, "Options");
        RequireTrue(options, "Enabled");
        RequireTrue(options, "MaterialGiQualificationCandidate");
        RequireInt32(options, "WarmupFrameCount", 30);
        RequireInt32(options, "MeasureFrameCount", 120);
        RequireTrue(options, "DisableVSync");
        RequireEnumValue(
            options,
            "BudgetProfileOverride",
            (int)profile.Kind,
            profile.Kind.ToString());
        RequireEnumValue(
            options,
            "Trajectory",
            0,
            "Stationary");
        RequireEnumValue(
            options,
            "TrajectoryBistroVariant",
            2,
            "SunScaleStep");
        JsonElement ddgiTransientRaw = RequireObject(
            root,
            "DdgiTransientRawEvidence");
        RequireString(
            ddgiTransientRaw,
            "Schema",
            MaterialGiReleaseEvidenceContract
                .BenchmarkDdgiTransientRawEvidenceSchema);
        RequireFalse(ddgiTransientRaw, "Applicable");
        RequireInt32(ddgiTransientRaw, "MeasurementFrameCount", 0);
        RequireEmptyArray(ddgiTransientRaw, "Frames");
        JsonElement ddgiTransient = RequireObject(
            root,
            "DdgiTransientEvidence");
        RequireString(
            ddgiTransient,
            "Schema",
            MaterialGiReleaseEvidenceContract
                .BenchmarkDdgiTransientEvidenceSchema);
        RequireFalse(ddgiTransient, "Applicable");
        RequireFalse(ddgiTransient, "Available");
        RequireEmptyArray(ddgiTransient, "Failures");
        RequireEmptyArray(ddgiTransient, "Windows");
        int requested = checked((int)RequirePositiveInt64(options, "MeasureFrameCount"));
        int measured = checked((int)RequirePositiveInt64(root, "MeasurementFrameCount"));
        if (requested != 120 ||
            measured != requested ||
            RequireInt64(root, "WarmupFrameCount") != 30 ||
            RequireInt64(root, "FirstMeasurementFrameIndex") != 30 ||
            RequireInt64(root, "LastMeasurementFrameIndex") != 149)
        {
            throw new InvalidDataException(
                "Benchmark producer does not cover the locked 30-frame warmup and 120-frame measurement interval.");
        }
        if (RequireInt64(root, "GpuTimingSupported") == 0 ||
            RequireInt64(root, "GpuTimingValidSampleCount") != measured)
        {
            throw new InvalidDataException(
                "Benchmark producer has incomplete GPU timing coverage.");
        }
        JsonElement gpuFrame = RequireObject(root, "GpuFrameMilliseconds");
        if (RequireInt64(gpuFrame, "Count") != measured)
            throw new InvalidDataException(
                "Benchmark producer GPU timing statistics do not cover every measured frame.");

        JsonElement metrics = RequireNonEmptyArray(root, "BudgetMetrics");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var metricElements = new Dictionary<string, JsonElement>(
            StringComparer.Ordinal);
        foreach (JsonElement metric in metrics.EnumerateArray())
        {
            string name = RequireJsonString(metric, "Name");
            if (!names.Add(name))
                throw new InvalidDataException($"Benchmark budget metric '{name}' is duplicated.");
            if (!KnownBenchmarkMetricNames.Contains(name))
            {
                throw new InvalidDataException(
                    $"Benchmark budget metric '{name}' is not part of the frozen renderer budget contract.");
            }
            metricElements.Add(name, metric);
            double value = RequireFiniteNonNegative(metric, "Value");
            double warning = RequireFiniteNonNegative(metric, "WarningThreshold");
            double threshold = RequireFiniteNonNegative(metric, "FailureThreshold");
            string expectedUnit = GetBenchmarkMetricUnit(name);
            RequireString(metric, "Unit", expectedUnit);
            if (TryGetExpectedBenchmarkThreshold(
                    name,
                    producer.QualityTier,
                    profile,
                    out double expectedFailure,
                    out double expectedWarning))
            {
                RequireExactNumber(
                    threshold,
                    expectedFailure,
                    $"benchmark metric '{name}' failure threshold");
                RequireExactNumber(
                    warning,
                    expectedWarning,
                    $"benchmark metric '{name}' warning threshold");
            }

            RenderBudgetStatus status =
                ReadBudgetStatus(RequireProperty(metric, "Status"), name);
            bool required = RequiredBenchmarkMetricNames.Contains(name);
            if (status == RenderBudgetStatus.Unavailable)
            {
                if (required)
                {
                    throw new InvalidDataException(
                        $"Required benchmark metric '{name}' is unavailable.");
                }
                continue;
            }
            RenderBudgetStatus expectedStatus =
                ClassifyBudgetMetric(value, warning, threshold);
            if (status != expectedStatus ||
                status is RenderBudgetStatus.Unknown or
                    RenderBudgetStatus.OverBudget)
            {
                throw new InvalidDataException(
                    $"Benchmark budget metric '{name}' has a false or failing budget status.");
            }
        }
        if (!names.SetEquals(KnownBenchmarkMetricNames))
        {
            throw new InvalidDataException(
                "Benchmark producer does not contain the exact frozen renderer budget metric set.");
        }

        JsonElement diagnostics = RequireObject(root, "LastDiagnostics");
        RequireString(diagnostics, "CaptureGpuDeviceName", producer.GpuName);
        RequireString(
            diagnostics,
            "CaptureGpuDriverVersion",
            producer.DriverVersion);
        RequireQualityPreset(
            diagnostics,
            "ActiveQualityPreset",
            "DdgiHigh");
        RequireEnumValue(
            diagnostics,
            "ActiveBudgetProfile",
            (int)profile.Kind,
            profile.Kind.ToString());
        RequireString(
            diagnostics,
            "ActiveBudgetProfileName",
            profile.Name);
        RequireEnumValue(
            diagnostics,
            "MaterialGiV2ActiveFeatures",
            (int)MaterialGiV2Feature.All,
            nameof(MaterialGiV2Feature.All));
        RequireEnumValue(
            diagnostics,
            "MaterialGiRolloutMode",
            (int)MaterialGiRolloutMode.QualificationCandidate,
            nameof(MaterialGiRolloutMode.QualificationCandidate));
        if (RequireInt64(diagnostics, "GlobalIlluminationRayQuerySupported") != 1 ||
            RequireInt64(diagnostics, "GlobalIlluminationRayQueryActive") != 1 ||
            RequireInt64(diagnostics, "GlobalIlluminationEnabled") != 1 ||
            RequireInt64(diagnostics, "GlobalIlluminationDdgiActive") != 1 ||
            RequireInt64(diagnostics, "SimpleDdgiActive") != 1 ||
            RequireInt64(diagnostics, "MaterialGiReleaseQualificationRequired") != 1 ||
            RequireInt64(diagnostics, "MaterialGiReleaseQualified") != 0 ||
            RequireInt64(diagnostics, "MaterialGiQualifiedDeviceCount") != 0 ||
            RequireInt64(diagnostics, "MaterialGiReleaseQualificationFailureCount") != 0)
        {
            throw new InvalidDataException(
                "Benchmark producer does not prove explicit non-shipping qualification-candidate Vulkan ray-query material/GI execution.");
        }
        RequireEmptyString(diagnostics, "MaterialGiReleaseApprovalId");
        RequireEmptyString(diagnostics, "MaterialGiReleaseEvidenceSha256");
        RequireString(
            diagnostics,
            "MaterialGiV1RemovalOwner",
            MaterialGiV1CompatibilityContract.Owner);
        RequireString(
            diagnostics,
            "MaterialGiV1RemovalTargetDate",
            MaterialGiV1CompatibilityContract.RemovalTargetDate
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        JsonElement captureRun = RequireObject(diagnostics, "CaptureRun");
        RequireString(captureRun, "Commit", producer.BuildCommit);
        RequireNormalizedSha256(
            captureRun,
            "ShaderBundleHash",
            producer.ShaderFingerprint);

        long budgetAvailable =
            TryGetInt64(diagnostics, "ActualGpuMemoryBudgetBytes", out long actual)
                ? actual
                : 0;
        if (RequireInt64(diagnostics, "GpuMemoryBudgetQueryAvailable") != 1)
        {
            throw new InvalidDataException(
                "Benchmark producer does not contain an actual GPU memory-budget query.");
        }
        long usage = RequireInt64(diagnostics, "ActualGpuMemoryUsageBytes");
        if (budgetAvailable <= 0 || usage < 0 || usage > budgetAvailable)
        {
            throw new InvalidDataException(
                "Benchmark producer exceeded or omitted the actual GPU memory budget.");
        }
        RequireBenchmarkMetricThreshold(
            metricElements,
            "GPU memory",
            budgetAvailable,
            hardLimit: false);
        RequireExactNumber(
            RequireFiniteNonNegative(metricElements["GPU memory"], "Value"),
            usage,
            "benchmark actual GPU memory usage");

        long ddgiProbeBudget =
            RequirePositiveInt64(diagnostics, "DdgiMaxActiveProbeBudget");
        long ddgiUpdateBudget =
            RequirePositiveInt64(diagnostics, "DdgiProbeUpdateRequestBudget");
        long ddgiAtlasBudget =
            RequirePositiveInt64(diagnostics, "DdgiAtlasMemoryBudgetBytes");
        long accelerationStructureBudget =
            RequirePositiveInt64(
                diagnostics,
                "AccelerationStructureMemoryBudgetBytes");
        long farFieldBudget =
            RequirePositiveInt64(diagnostics, "FarFieldMemoryBudgetBytes");
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI probes",
            ddgiProbeBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI active probe budget",
            ddgiProbeBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI update request budget",
            ddgiUpdateBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI atlas memory",
            ddgiAtlasBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI total memory",
            ddgiAtlasBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "GI resident acceleration structures",
            accelerationStructureBudget,
            hardLimit: true);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "Far-field page cache",
            farFieldBudget,
            hardLimit: true);

        ulong residentBudget = checked((ulong)accelerationStructureBudget);
        ulong transientBudget =
            AccelerationStructureManager.CalculateTransientMemoryBudgetBytes(
                residentBudget);
        ulong declaredGiBudget = SaturatingAdd(
            checked((ulong)ddgiAtlasBudget),
            checked((ulong)farFieldBudget));
        declaredGiBudget = SaturatingAdd(declaredGiBudget, residentBudget);
        declaredGiBudget = SaturatingAdd(declaredGiBudget, transientBudget);
        RequireBenchmarkMetricThreshold(
            metricElements,
            "GI unique residency",
            declaredGiBudget,
            hardLimit: false);
        long activeProbeCount =
            RequirePositiveInt64(diagnostics, "DdgiActiveProbeCount");
        RequireBenchmarkMetricThreshold(
            metricElements,
            "DDGI probes updated",
            activeProbeCount - 1,
            hardLimit: true);

        ValidateDdgiProductionGate(RequireObject(root, "DdgiProductionGate"));
        ValidateBenchmarkAccuracyOracle(
            RequireNonEmptyArray(root, "AccuracyOracleResults"));
    }

    private static RenderBudgetProfile ResolveBenchmarkProfile(
        string qualityTier) => qualityTier switch
        {
            "Low" => RenderBudgetProfile.LowSpec1080p30,
            "Medium" => RenderBudgetProfile.MidSpec1080p60,
            "High" => RenderBudgetProfile.HighSpec1440p60,
            "Ultra" => RenderBudgetProfile.Ultra4k60,
            _ => throw new InvalidDataException(
                $"Benchmark producer quality tier '{qualityTier}' has no frozen render budget profile.")
        };

    private static bool TryGetExpectedBenchmarkThreshold(
        string name,
        string qualityTier,
        RenderBudgetProfile profile,
        out double failure,
        out double warning)
    {
        if (TryClassifyDdgiMutationLatencyMetric(name, out int fixedDeadline))
        {
            if (fixedDeadline > 0)
            {
                failure = fixedDeadline;
                warning = failure;
                return true;
            }

            // Mathematical certification uses the producer's probe-count and
            // scheduler-budget-scaled deadline. It remains a hard gate, but it
            // cannot be reconstructed from the quality tier alone here.
            failure = double.NaN;
            warning = double.NaN;
            return false;
        }

        bool hardLimit = false;
        failure = name switch
        {
            "CPU renderer" => profile.CpuFrameBudgetMilliseconds,
            "GPU frame" => profile.GpuFrameBudgetMilliseconds,
            "Tracked GPU memory" => profile.GpuMemoryBudgetBytes,
            "Upload" => profile.UploadBudgetBytesPerFrame,
            "Objects" => profile.ObjectBudget,
            "Meshlets" => profile.MeshletBudget,
            "Foliage clusters" => profile.FoliageClusterBudget,
            "Foliage meshlet draws" => profile.FoliageMeshletDrawBudget,
            "Foliage grass blades" => profile.FoliageGrassBladeBudget,
            "Foliage memory" => profile.FoliageMemoryBudgetBytes,
            "Materials" => profile.MaterialBudget,
            "Material GI primitive profile memory" =>
                RenderBudgetEvaluator.ResolvePrimitiveProfileMemoryBudgetBytes(
                    RenderQualityPreset.DdgiHigh),
            "Material GI compile P95" or
            "Material GI upload P95" or
            "Material GI compile/upload P95" or
            "GI CPU scheduling and upload" =>
                profile.GlobalIlluminationCpuBudgetMilliseconds,
            "Textures" => profile.TextureBudget,
            "Lights" => profile.LightBudget,
            "Shadowed lights" => profile.ShadowedLightBudget,
            "Reflection probes" => profile.ReflectionProbeBudget,
            "GI GPU" or "GI forward gather incremental" =>
                profile.GlobalIlluminationGpuBudgetMilliseconds,
            "GI memory" => profile.GlobalIlluminationMemoryBudgetBytes,
            "Transparent objects" => profile.TransparentObjectBudget,
            "DDGI dirty first-update latency" => 1,
            "DDGI dirty convergence latency" => 8,
            "Material GI non-finite values" or
            "Material GI clamped values" or
            "Material alpha candidate limit" or
            "Material GI release qualification" or
            "Material GI active V1 fallbacks" or
            "Material GI active invalid profiles" or
            "DDGI emissive truncated sources" or
            "DDGI emissive skipped energy" or
            "DDGI emissive unsupported skinned objects" or
            "DDGI emissive unsupported skinned importance" => 0,
            _ => double.NaN
        };
        if (double.IsNaN(failure))
        {
            warning = double.NaN;
            return false;
        }

        hardLimit = IsHardLimitBenchmarkMetric(name);
        warning = hardLimit ? failure : failure * RenderBudgetEvaluator.WarningRatio;
        return true;
    }

    private static bool IsHardLimitBenchmarkMetric(string name) =>
        TryClassifyDdgiMutationLatencyMetric(name, out _) || name switch
        {
        "Material GI non-finite values" or
        "Material GI clamped values" or
        "Material alpha candidate limit" or
        "Material GI release qualification" or
        "Material GI active V1 fallbacks" or
        "Material GI active invalid profiles" or
        "DDGI emissive truncated sources" or
        "DDGI emissive skipped energy" or
        "DDGI emissive unsupported skinned objects" or
        "DDGI emissive unsupported skinned importance" or
        "DDGI dirty first-update latency" or
        "DDGI dirty convergence latency" => true,
            _ => false
        };

    private static void RequireBenchmarkMetricThreshold(
        IReadOnlyDictionary<string, JsonElement> metrics,
        string name,
        double failureThreshold,
        bool hardLimit)
    {
        if (!metrics.TryGetValue(name, out JsonElement metric))
        {
            throw new InvalidDataException(
                $"Benchmark producer is missing required metric '{name}'.");
        }
        RequireExactNumber(
            RequireFiniteNonNegative(metric, "FailureThreshold"),
            failureThreshold,
            $"benchmark metric '{name}' failure threshold");
        RequireExactNumber(
            RequireFiniteNonNegative(metric, "WarningThreshold"),
            hardLimit
                ? failureThreshold
                : failureThreshold * RenderBudgetEvaluator.WarningRatio,
            $"benchmark metric '{name}' warning threshold");
    }

    private static string GetBenchmarkMetricUnit(string name) =>
        TryClassifyDdgiMutationLatencyMetric(name, out _)
            ? "frames"
            : name switch
            {
        "CPU renderer" or
        "GPU frame" or
        "Material GI compile P95" or
        "Material GI upload P95" or
        "Material GI compile/upload P95" or
        "GI GPU" or
        "GI forward gather (inclusive draw)" or
        "GI forward gather incremental" or
        "GI CPU scheduling and upload" => "ms",
        "GPU memory" or
        "Tracked GPU memory" or
        "Upload" or
        "Foliage memory" or
        "Material GI primitive profile memory" or
        "GI memory" or
        "GI unique residency" or
        "GI resident acceleration structures" or
        "Far-field page cache" or
        "DDGI atlas memory" or
        "DDGI total memory" => "bytes",
        "Material alpha candidate limit" => "rays",
        "Material GI release qualification" => "failures",
        "Material GI active V1 fallbacks" or
        "Material GI active invalid profiles" => "materials",
        "DDGI emissive truncated sources" => "sources",
        "DDGI emissive skipped energy" => "fraction",
        "DDGI emissive unsupported skinned objects" => "objects",
        "DDGI emissive unsupported skinned importance" => "importance",
        "DDGI dirty first-update latency" or
        "DDGI dirty convergence latency" => "frames",
                _ => "count"
            };

    private static bool TryClassifyDdgiMutationLatencyMetric(
        string name,
        out int fixedDeadlineFrames)
    {
        fixedDeadlineFrames = name switch
        {
            "DDGI Environment first-visible latency" or
            "DDGI Light first-visible latency" or
            "DDGI Emissive first-visible latency" or
            "DDGI Material first-visible latency" or
            "DDGI Transform first-visible latency" or
            "DDGI Topology first-visible latency" => 1,
            "DDGI Environment affected-region latency" or
            "DDGI Light affected-region latency" or
            "DDGI Emissive affected-region latency" or
            "DDGI Material affected-region latency" or
            "DDGI Transform affected-region latency" or
            "DDGI Topology affected-region latency" => 8,
            _ => 0
        };
        if (fixedDeadlineFrames != 0)
            return true;
        bool scaledCertification = name is
            "DDGI Environment certified latency" or
            "DDGI Light certified latency" or
            "DDGI Emissive certified latency" or
            "DDGI Material certified latency" or
            "DDGI Transform certified latency" or
            "DDGI Topology certified latency" or
            "DDGI cold-start certified latency";
        if (scaledCertification)
            fixedDeadlineFrames = -1;
        return scaledCertification;
    }

    private static void ValidateDdgiProductionGate(JsonElement gate)
    {
        RequireTrue(gate, "Passed");
        RequireEmptyArray(gate, "Failures");
        JsonElement criteria = RequireNonEmptyArray(gate, "Criteria");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement criterion in criteria.EnumerateArray())
        {
            string name = RequireJsonString(criterion, "Name");
            if (!names.Add(name))
            {
                throw new InvalidDataException(
                    $"DDGI production criterion '{name}' is duplicated.");
            }
            RequireTrue(criterion, "Passed");
            RequireCanonicalJsonString(criterion, "Detail");
        }
        if (!names.SetEquals(RequiredDdgiProductionCriteria))
        {
            throw new InvalidDataException(
                "Benchmark DDGI production gate does not contain the exact frozen criterion set.");
        }
    }

    private static void ValidateBenchmarkAccuracyOracle(JsonElement results)
    {
        // System.Text.Json emits the frozen single-precision oracle constant
        // using its shortest round-trippable decimal representation.
        const double expectedFurnaceReference = 1.5707964;
        JsonElement[] entries = results.EnumerateArray().ToArray();
        if (entries.Length != 1)
        {
            throw new InvalidDataException(
                "Benchmark producer must contain exactly the locked furnace accuracy oracle.");
        }
        JsonElement result = entries[0];
        RequireString(result, "Name", "simple-ddgi-furnace");
        RequireEnumValue(result, "Scenario", 14, "GiSimpleDdgiFurnace");
        RequireString(
            result,
            "Metric",
            "SimpleDdgiAverageSampledIrradianceLuminance");
        RequireEnumValue(result, "Status", 0, "Passed");
        double measuredValue =
            RequireFiniteNonNegative(result, "MeasuredValue");
        double referenceValue =
            RequireFiniteNonNegative(result, "ReferenceValue");
        RequireExactNumber(
            referenceValue,
            expectedFurnaceReference,
            "benchmark furnace reference value");
        double relativeError =
            RequireFiniteNonNegative(result, "RelativeError");
        double recomputedRelativeError =
            Math.Abs(measuredValue - referenceValue) /
            Math.Max(Math.Abs(referenceValue), 0.0001);
        if (Math.Abs(relativeError - recomputedRelativeError) > 1e-6)
        {
            throw new InvalidDataException(
                "Benchmark furnace accuracy oracle relative error does not match its measured and reference values.");
        }
        if (relativeError > 0.05)
        {
            throw new InvalidDataException(
                "Benchmark furnace accuracy oracle exceeds the frozen five-percent error threshold.");
        }
        JsonElement latencyFrames = RequireProperty(result, "LatencyFrames");
        if (latencyFrames.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidDataException(
                "Benchmark furnace accuracy oracle must not claim a latency result.");
        }
        RequireCanonicalJsonString(result, "Detail");
    }

    private static void ValidateLongRun(JsonElement root)
    {
        const int maximumRetainedSampleCapacity = 4_096;
        const long minimumPostWarmupSamples = 30;

        RequireInt32(root, "SchemaVersion", 3);
        RequireString(root, "Kind", MaterialGiReleaseEvidenceContract.LongRunProducerKind);
        RequireString(root, "Status", "passed");
        RequireNullOrEmpty(root, "Failure");
        DateTimeOffset startedUtc = RequireDateTimeOffset(root, "StartedUtc");
        DateTimeOffset completedUtc = RequireDateTimeOffset(root, "CompletedUtc");
        if (startedUtc.Offset != TimeSpan.Zero ||
            completedUtc.Offset != TimeSpan.Zero ||
            completedUtc <= startedUtc)
        {
            throw new InvalidDataException(
                "Long-run producer timestamps must be increasing UTC timestamps.");
        }
        double elapsedSeconds =
            RequireFiniteNonNegative(root, "ElapsedSeconds");
        double requestedMinutes =
            RequireFiniteNonNegative(root, "RequestedMinutes");
        if (elapsedSeconds <
                MaterialGiReleaseEvidenceContract.MinimumSoakDurationSeconds ||
            requestedMinutes < 30.0 ||
            RequireInt64(root, "RequestedFrameCount") != 0)
        {
            throw new InvalidDataException(
                "Long-run producer did not execute a duration-owned literal thirty-minute soak.");
        }
        double wallClockSeconds = (completedUtc - startedUtc).TotalSeconds;
        double clockTolerance = Math.Max(5.0, elapsedSeconds * 0.01);
        if (wallClockSeconds <
                MaterialGiReleaseEvidenceContract.MinimumSoakDurationSeconds ||
            Math.Abs(wallClockSeconds - elapsedSeconds) > clockTolerance)
        {
            throw new InvalidDataException(
                "Long-run elapsed time is inconsistent with its authenticated UTC interval.");
        }

        int warmupFrames =
            checked((int)RequireNonNegativeInt64(root, "WarmupFrames"));
        int sampleIntervalFrames =
            checked((int)RequirePositiveInt64(root, "SampleIntervalFrames"));
        int retainedCapacity =
            checked((int)RequirePositiveInt64(root, "RetainedSampleCapacity"));
        int lastPreparedFrame =
            checked((int)RequireNonNegativeInt64(
                root,
                "LastPreparedFrameIndex"));
        if (retainedCapacity < 2 ||
            retainedCapacity > maximumRetainedSampleCapacity ||
            lastPreparedFrame < warmupFrames)
        {
            throw new InvalidDataException(
                "Long-run producer has invalid warmup, cadence, or bounded-retention parameters.");
        }
        long expectedSampleCount = CalculateExpectedLongRunSampleCount(
            lastPreparedFrame,
            warmupFrames,
            sampleIntervalFrames);
        long totalSamples = RequirePositiveInt64(root, "TotalSamples");
        if (RequirePositiveInt64(root, "ExpectedSampleCount") !=
                expectedSampleCount ||
            totalSamples != expectedSampleCount)
        {
            throw new InvalidDataException(
                "Long-run producer sample totals do not match its complete deterministic cadence.");
        }
        long postWarmupSampleCount =
            CalculatePostWarmupLongRunSampleCount(
                expectedSampleCount,
                warmupFrames,
                sampleIntervalFrames);
        if (postWarmupSampleCount < minimumPostWarmupSamples)
        {
            throw new InvalidDataException(
                "Long-run producer contains too few meaningful post-warmup telemetry samples.");
        }

        if (
            RequireInt64(root, "PostWarmupBudgetViolationFrameCount") != 0 ||
            RequireInt64(
                root,
                "PostWarmupTelemetryCoverageFailureFrameCount") != 0)
        {
            throw new InvalidDataException(
                "Long-run producer has incomplete samples, budget failures, or telemetry failures.");
        }
        RequireEmptyArray(root, "BudgetViolations");
        RequireEmptyArray(root, "TelemetryCoverageFailures");

        JsonElement retainedSamples = RequireNonEmptyArray(root, "RetainedSamples");
        int expectedRetainedCount =
            checked((int)Math.Min(totalSamples, retainedCapacity));
        if (retainedSamples.GetArrayLength() != expectedRetainedCount)
        {
            throw new InvalidDataException(
                "Long-run retained telemetry does not match its bounded tail capacity.");
        }
        ValidateRetainedLongRunSamples(
            retainedSamples,
            warmupFrames,
            sampleIntervalFrames,
            lastPreparedFrame);

        JsonElement descriptorPressure =
            RequireObject(root, "DescriptorPressure");
        if (RequirePositiveInt64(
                descriptorPressure,
                "PostWarmupSampleCount") != postWarmupSampleCount ||
            RequireInt64(
                descriptorPressure,
                "TextureExhaustionSampleCount") != 0 ||
            RequireInt64(
                descriptorPressure,
                "SamplerExhaustionSampleCount") != 0)
        {
            throw new InvalidDataException(
                "Long-run descriptor-pressure summary is incomplete or exhausted.");
        }
        ValidateDescriptorPressureMaximum(
            descriptorPressure,
            "MaximumTextureUsed",
            "MaximumTextureCapacity");
        ValidateDescriptorPressureMaximum(
            descriptorPressure,
            "MaximumSamplerUsed",
            "MaximumSamplerCapacity");

        int latestScheduledFrame = CalculateLatestScheduledFrame(
            lastPreparedFrame,
            warmupFrames,
            sampleIntervalFrames);
        ValidateMemoryTrend(
            RequireObject(root, "ManagedMemoryTrend"),
            "managed-memory",
            postWarmupSampleCount,
            warmupFrames,
            latestScheduledFrame);
        RequireString(root, "GpuMemorySignal", "VK_EXT_memory_budget");
        ValidateMemoryTrend(
            RequireObject(root, "GpuMemoryTrend"),
            "actual-gpu-memory",
            postWarmupSampleCount,
            warmupFrames,
            latestScheduledFrame);

        JsonElement workload = RequireObject(root, "Workload");
        RequireString(
            workload,
            "Name",
            "deterministic-dynamic-material-and-camera-path");
        RequireInt32(workload, "DeterministicSeed", 0x4D474932);
        RequireInt32(
            workload,
            "PreparedFrameCount",
            checked(lastPreparedFrame + 1));
        int mutationInterval =
            checked((int)RequirePositiveInt64(
                workload,
                "MaterialMutationIntervalFrames"));
        RequireInt32(workload, "MaterialMutationIntervalFrames", 30);
        long expectedMutations =
            (long)lastPreparedFrame / mutationInterval + 1L;
        if (RequirePositiveInt64(workload, "MaterialMutationCount") !=
            expectedMutations)
        {
            throw new InvalidDataException(
                "Long-run deterministic material mutation cadence is incomplete.");
        }
        RequireTrue(workload, "MaterialRollbackSucceeded");
        RequireTrue(workload, "CameraRollbackSucceeded");
        RequireString(
            workload,
            "CameraPath",
            "2400-frame elliptical path with bounded vertical/yaw/pitch modulation");

        JsonElement recovery = RequireObject(root, "DeviceLossRecovery");
        RequireFalse(recovery, "Supported");
        RequireFalse(recovery, "Attempted");
        RequireString(recovery, "Status", "rejected-unsupported");
        RequireCanonicalJsonString(recovery, "Reason");
    }

    private static void ValidateRetainedLongRunSamples(
        JsonElement retainedSamples,
        int warmupFrames,
        int sampleIntervalFrames,
        int lastPreparedFrame)
    {
        int previousFrame = -1;
        foreach (JsonElement sample in retainedSamples.EnumerateArray())
        {
            int frameIndex =
                checked((int)RequireNonNegativeInt64(sample, "FrameIndex"));
            if (frameIndex > lastPreparedFrame ||
                (frameIndex != warmupFrames &&
                 frameIndex % sampleIntervalFrames != 0) ||
                (previousFrame >= 0 &&
                 frameIndex != CalculateNextScheduledFrame(
                     previousFrame,
                     warmupFrames,
                     sampleIntervalFrames)))
            {
                throw new InvalidDataException(
                    "Long-run retained samples are not a complete chronological cadence tail.");
            }
            previousFrame = frameIndex;
            RequireNonNegativeInt64(sample, "ManagedBytes");
            RequireNonNegativeInt64(sample, "Gen0Collections");
            RequireNonNegativeInt64(sample, "Gen1Collections");
            RequireNonNegativeInt64(sample, "Gen2Collections");
            RequireNonNegativeInt64(sample, "TrackedGpuMemoryBytes");
            long actualGpuBytes =
                RequireNonNegativeInt64(
                    sample,
                    "ActualGpuMemoryUsageBytes");
            long effectiveBudget =
                RequirePositiveInt64(
                    sample,
                    "EffectiveGpuMemoryBudgetBytes");
            if (actualGpuBytes > effectiveBudget)
            {
                throw new InvalidDataException(
                    "Long-run retained sample exceeds its actual GPU memory budget.");
            }
            RenderBudgetStatus budgetStatus =
                ReadBudgetStatus(
                    RequireProperty(sample, "BudgetStatus"),
                    $"long-run frame {frameIndex}");
            if (budgetStatus is not (
                    RenderBudgetStatus.WithinBudget or
                    RenderBudgetStatus.Warning))
            {
                throw new InvalidDataException(
                    "Long-run retained sample has a false or failing budget status.");
            }
            RequireEmptyArray(sample, "OverBudgetMetrics");
            ValidateRetainedDescriptorPressure(
                RequireObject(sample, "DescriptorPressure"));
        }

        int expectedLast = CalculateLatestScheduledFrame(
            lastPreparedFrame,
            warmupFrames,
            sampleIntervalFrames);
        if (previousFrame != expectedLast)
        {
            throw new InvalidDataException(
                "Long-run retained cadence tail does not end at the latest scheduled frame.");
        }
    }

    private static void ValidateRetainedDescriptorPressure(
        JsonElement descriptorPressure)
    {
        int textureCapacity =
            checked((int)RequirePositiveInt64(
                descriptorPressure,
                "TextureCapacity"));
        int textureUsed =
            checked((int)RequireNonNegativeInt64(
                descriptorPressure,
                "TextureUsed"));
        int textureHighWater =
            checked((int)RequireNonNegativeInt64(
                descriptorPressure,
                "TextureHighWater"));
        int samplerCapacity =
            checked((int)RequirePositiveInt64(
                descriptorPressure,
                "SamplerCapacity"));
        int samplerUsed =
            checked((int)RequireNonNegativeInt64(
                descriptorPressure,
                "SamplerUsed"));
        int samplerHighWater =
            checked((int)RequireNonNegativeInt64(
                descriptorPressure,
                "SamplerHighWater"));
        RequireNonNegativeInt64(descriptorPressure, "DescriptorWrites");
        if (textureUsed >= textureCapacity ||
            textureHighWater < textureUsed ||
            textureHighWater >= textureCapacity ||
            samplerUsed >= samplerCapacity ||
            samplerHighWater < samplerUsed ||
            samplerHighWater >= samplerCapacity)
        {
            throw new InvalidDataException(
                "Long-run retained descriptor pressure is inconsistent or exhausted.");
        }
    }

    private static void ValidateDescriptorPressureMaximum(
        JsonElement summary,
        string usedName,
        string capacityName)
    {
        long used = RequireNonNegativeInt64(summary, usedName);
        long capacity = RequirePositiveInt64(summary, capacityName);
        if (used >= capacity)
        {
            throw new InvalidDataException(
                $"Long-run descriptor pressure '{usedName}' reached its bounded capacity.");
        }
    }

    private static void ValidateMemoryTrend(
        JsonElement trend,
        string expectedSignal,
        long expectedSampleCount,
        int expectedFirstFrame,
        int expectedLastFrame)
    {
        RequireString(trend, "Signal", expectedSignal);
        if (RequirePositiveInt64(trend, "SampleCount") != expectedSampleCount ||
            RequireInt64(trend, "FirstFrame") != expectedFirstFrame ||
            RequireInt64(trend, "LastFrame") != expectedLastFrame)
        {
            throw new InvalidDataException(
                $"Long-run '{expectedSignal}' trend does not cover the complete post-warmup cadence.");
        }
        ulong firstBytes =
            checked((ulong)RequireNonNegativeInt64(trend, "FirstBytes"));
        ulong lastBytes =
            checked((ulong)RequireNonNegativeInt64(trend, "LastBytes"));
        long expectedNetGrowth = SaturatingDifference(lastBytes, firstBytes);
        if (RequireInt64(trend, "NetGrowthBytes") != expectedNetGrowth)
        {
            throw new InvalidDataException(
                $"Long-run '{expectedSignal}' net growth is inconsistent with its endpoints.");
        }
        RequireFiniteNumber(trend, "SlopeBytesPerFrame");
        RequireNonNegativeInt64(trend, "NoiseToleranceBytes");
        RequireFalse(trend, "HasPositiveTrend");
    }

    private static long CalculateExpectedLongRunSampleCount(
        int lastPreparedFrame,
        int warmupFrames,
        int sampleIntervalFrames)
    {
        long intervalSamples =
            (long)lastPreparedFrame / sampleIntervalFrames + 1L;
        bool addWarmupBoundary =
            warmupFrames <= lastPreparedFrame &&
            warmupFrames % sampleIntervalFrames != 0;
        return checked(intervalSamples + (addWarmupBoundary ? 1L : 0L));
    }

    private static long CalculatePostWarmupLongRunSampleCount(
        long totalSampleCount,
        int warmupFrames,
        int sampleIntervalFrames)
    {
        long preWarmupCount = warmupFrames == 0
            ? 0
            : (long)(warmupFrames - 1) / sampleIntervalFrames + 1L;
        return checked(totalSampleCount - preWarmupCount);
    }

    private static int CalculateLatestScheduledFrame(
        int lastPreparedFrame,
        int warmupFrames,
        int sampleIntervalFrames)
    {
        int latestInterval =
            lastPreparedFrame / sampleIntervalFrames * sampleIntervalFrames;
        return warmupFrames <= lastPreparedFrame
            ? Math.Max(latestInterval, warmupFrames)
            : latestInterval;
    }

    private static int CalculateNextScheduledFrame(
        int currentFrame,
        int warmupFrames,
        int sampleIntervalFrames)
    {
        long nextInterval =
            ((long)currentFrame / sampleIntervalFrames + 1L) *
            sampleIntervalFrames;
        long next = warmupFrames > currentFrame
            ? Math.Min(nextInterval, warmupFrames)
            : nextInterval;
        return checked((int)next);
    }

    private static void ValidateHealth(
        JsonElement root,
        MaterialGiProducerEvidenceArtifact producer,
        MaterialGiReleaseEvidenceReport report)
    {
        RequireString(root, "kind", MaterialGiReleaseEvidenceContract.HealthProducerKind);
        RequireString(
            root,
            "schema",
            MaterialGiReleaseEvidenceContract.HealthProducerSchema);
        RequireString(root, "status", "passed");
        RequireNullOrEmpty(root, "failure");
        DateTimeOffset timestamp = RequireDateTimeOffset(root, "timestampUtc");
        if (timestamp.Offset != TimeSpan.Zero)
            throw new InvalidDataException(
                "Renderer-health producer timestamp must be UTC.");
        JsonElement diagnostics = RequireObject(root, "diagnostics");
        RequireString(diagnostics, "CaptureGpuDeviceName", producer.GpuName);
        RequireString(
            diagnostics,
            "CaptureGpuDriverVersion",
            producer.DriverVersion);
        JsonElement captureRun = RequireObject(diagnostics, "CaptureRun");
        RequireString(captureRun, "Commit", producer.BuildCommit);
        RequireNormalizedSha256(
            captureRun,
            "ShaderBundleHash",
            producer.ShaderFingerprint);
        JsonElement operations = RequireProperty(root, "operations");
        if (operations.ValueKind != JsonValueKind.Array ||
            operations.GetArrayLength() == 0)
        {
            throw new InvalidDataException(
                "Renderer-health operations must be a non-empty array.");
        }
        var operationNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            string operationName = RequireJsonString(operation, "Name");
            if (!operationNames.Add(operationName))
            {
                throw new InvalidDataException(
                    $"Renderer-health operation '{operationName}' is duplicated.");
            }
            string status = RequireJsonString(operation, "Status");
            if (!string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(
                    status,
                    "rejected-unsupported",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Renderer-health operation '{operationName}' is not a successful terminal result.");
            }
        }

        switch (report.Role)
        {
            case MaterialGiReleaseEvidenceContract.CleanValidationRole:
                {
                    RequireInt32(root, "validationWarningCount", 0);
                    RequireInt32(root, "validationErrorCount", 0);
                    JsonElement options = RequireObject(root, "options");
                    string mode = RequireJsonString(options, "ValidationMode");
                    if (string.Equals(mode, "Off", StringComparison.Ordinal))
                        throw new InvalidDataException("Renderer-health validation was disabled.");
                    break;
                }
            case MaterialGiReleaseEvidenceContract.LifecycleResilienceRole:
                foreach (string operation in
                         MaterialGiReleaseEvidenceContract.RequiredLifecycleChecks)
                {
                    RequirePassedOperation(operations, operation);
                }
                break;
            case MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole:
                {
                    JsonElement operation =
                        RequirePassedOperation(operations, "quality-switch");
                    string detail = RequireJsonString(operation, "Detail");
                    RequireSettingsFingerprintDetail(
                        detail,
                        producer.SettingsFingerprint);
                    if (!detail.Contains(
                            "rendererRestarted=false",
                            StringComparison.Ordinal) ||
                        !detail.Contains(
                            "tiers=Low,Medium,High,Ultra",
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Quality-switch producer does not prove exact settings rollback without a renderer restart.");
                    }
                    break;
                }
            case MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole:
                {
                    JsonElement operation =
                        RequirePassedOperation(operations, "texture-hot-reload");
                    string detail = RequireJsonString(operation, "Detail");
                    if (!detail.Contains("rollback=true", StringComparison.Ordinal) ||
                        !detail.Contains("rendererRestarted=false", StringComparison.Ordinal) ||
                        !detail.Contains("descriptorCount=", StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Texture hot-reload producer does not prove descriptor-stable in-process rollback.");
                    }
                    break;
                }
            case MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole:
                RequirePassedOperation(operations, "long-run-stability");
                break;
            case MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole:
                {
                    JsonElement operation =
                        FindOperation(operations, "device-loss-recovery") ??
                        throw new InvalidDataException(
                            "Recovery producer does not contain a device-loss-recovery result.");
                    MaterialGiRecoveryDeviceEvidence recovery =
                        (report.RecoveryDevices ??
                         throw new InvalidDataException(
                             "Recovery role structured device evidence is null."))
                        .Single(device => string.Equals(
                            device.DeviceId,
                            producer.DeviceId,
                            StringComparison.Ordinal));
                    string operationStatus = RequireJsonString(operation, "Status");
                    string detail = RequireJsonString(operation, "Detail");
                    if (recovery.Supported)
                    {
                        if (!string.Equals(
                                operationStatus,
                                "passed",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(
                                $"Recovery producer for '{producer.DeviceId}' did not pass its supported recovery attempt.");
                        }
                    }
                    else if (!string.Equals(
                                 operationStatus,
                                 "rejected-unsupported",
                                 StringComparison.OrdinalIgnoreCase) ||
                             string.IsNullOrWhiteSpace(detail) ||
                             detail.Length > 1024 ||
                             !string.Equals(
                                 detail,
                                 recovery.Reason,
                                 StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Recovery producer for '{producer.DeviceId}' does not authenticate its unsupported capability.");
                    }
                    break;
                }
        }
    }

    private static void RequireSettingsFingerprintDetail(
        string detail,
        string expectedFingerprint)
    {
        string[] tokens = detail.Split(
            ',',
            StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        string[] settingsTokens = tokens
            .Where(static token =>
                token.StartsWith("settings=", StringComparison.Ordinal))
            .ToArray();
        if (settingsTokens.Length != 1)
        {
            throw new InvalidDataException(
                "Quality-switch producer must contain exactly one settings fingerprint token.");
        }
        string actualFingerprint = settingsTokens[0]["settings=".Length..];
        string normalized;
        try
        {
            normalized =
                MaterialGiProducerSettingsFingerprint.NormalizeSha256(
                    actualFingerprint);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "Quality-switch producer settings fingerprint is invalid.",
                exception);
        }
        if (!string.Equals(
                normalized,
                expectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Quality-switch producer settings fingerprint does not match its pinned producer identity.");
        }
    }

    private static JsonElement RequirePassedOperation(
        JsonElement operations,
        string name)
    {
        JsonElement? operation = FindOperation(operations, name);
        if (operation is null ||
            !string.Equals(
                RequireJsonString(operation.Value, "Status"),
                "passed",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Renderer-health producer does not contain a passed '{name}' operation.");
        }
        return operation.Value;
    }

    private static JsonElement? FindOperation(JsonElement operations, string name)
    {
        foreach (JsonElement operation in operations.EnumerateArray())
        {
            if (string.Equals(
                    RequireJsonString(operation, "Name"),
                    name,
                    StringComparison.Ordinal))
            {
                return operation;
            }
        }
        return null;
    }

    private static JsonDocument OpenPinnedJson(
        string path,
        MaterialGiProducerEvidenceArtifact producer)
    {
        if (!TryDecodeSha256(producer.Sha256, out byte[] expectedHash))
            throw new InvalidDataException($"Producer '{producer.Kind}' SHA-256 is invalid.");
        byte[] bytes = BoundedFileReader.ReadStable(
            path,
            MaterialGiReleaseEvidenceContract.MaximumArtifactBytes,
            $"Producer '{producer.Kind}'",
            producer.ByteLength);
        byte[] hash = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(hash, expectedHash))
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' does not match its pinned SHA-256 identity.");
        }
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                bytes,
                MaximumJsonDepth,
                $"Producer '{producer.Kind}' payload");
            return JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth
                });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Producer '{producer.Kind}' payload is not valid bounded JSON.",
                exception);
        }
    }

    private static string ResolveContainedPath(
        string directory,
        string? relativePath,
        string name)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            !string.Equals(
                relativePath,
                relativePath.Trim(),
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) ||
            relativePath[0] is '/' or '\\' ||
            relativePath.Contains(':', StringComparison.Ordinal) ||
            relativePath.Split(['/', '\\']).Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"{name} must be a canonical manifest-relative path without traversal.");
        }
        string root = Path.GetFullPath(directory);
        string path = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));
        string boundary = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(boundary, comparison))
            throw new InvalidDataException($"{name} resolves outside the manifest directory.");
        return path;
    }

    private static void RequireBuildCommit(string? value, string name)
    {
        if (value is not { Length: 40 } ||
            value.Any(static character =>
                !char.IsAsciiHexDigit(character) ||
                char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidDataException(
                $"{name} build commit must be an exact lowercase 40-character Git commit.");
        }
    }

    private static void RequireMatchingSha256(
        string? actual,
        string? expected,
        string name)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal) ||
            !TryDecodeSha256(actual, out byte[] actualBytes) ||
            !TryDecodeSha256(expected, out byte[] expectedBytes) ||
            !CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes))
        {
            throw new InvalidDataException($"{name} does not match exactly.");
        }
    }

    private static void RequireSha256(string? value, string name)
    {
        if (!TryDecodeSha256(value, out _))
            throw new InvalidDataException($"{name} must be an exact lowercase SHA-256.");
        if (value!.Any(char.IsAsciiLetterUpper))
            throw new InvalidDataException($"{name} must use canonical lowercase hexadecimal.");
    }

    private static bool TryDecodeSha256(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is not { Length: 64 })
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void RequireCanonicalText(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > MaximumIdentityLength ||
            value.Contains('\0', StringComparison.Ordinal) ||
            value.StartsWith("unknown", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{name} is not bounded canonical text.");
        }
    }

    private static JsonElement RequireProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty(name, out JsonElement property))
        {
            throw new InvalidDataException($"Producer payload is missing required property '{name}'.");
        }
        return property;
    }

    private static JsonElement RequireObject(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Producer property '{name}' must be an object.");
        return property;
    }

    private static JsonElement RequireNonEmptyArray(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"Producer property '{name}' must be a non-empty array.");
        }
        return property;
    }

    private static void RequireEmptyArray(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.Array ||
            property.GetArrayLength() != 0)
        {
            throw new InvalidDataException($"Producer property '{name}' must be an empty array.");
        }
    }

    private static string RequireJsonString(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } result)
        {
            throw new InvalidDataException($"Producer property '{name}' must be a string.");
        }
        return result;
    }

    private static void RequireCanonicalJsonString(JsonElement value, string name)
    {
        RequireCanonicalText(RequireJsonString(value, name), $"producer property '{name}'");
    }

    private static void RequireString(
        JsonElement value,
        string name,
        string expected)
    {
        string actual = RequireJsonString(value, name);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Producer property '{name}' is '{actual}', expected '{expected}'.");
        }
    }

    private static void RequireEmptyString(JsonElement value, string name) =>
        RequireString(value, name, string.Empty);

    private static void RequireNullOrEmpty(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind == JsonValueKind.Null)
            return;
        if (property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: 0 })
        {
            return;
        }
        throw new InvalidDataException(
            $"Producer property '{name}' must be null or empty on success.");
    }

    private static long RequireInt64(JsonElement value, string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (!property.TryGetInt64(out long result))
            throw new InvalidDataException($"Producer property '{name}' must be an integer.");
        return result;
    }

    private static long RequireNonNegativeInt64(
        JsonElement value,
        string name)
    {
        long result = RequireInt64(value, name);
        if (result < 0)
        {
            throw new InvalidDataException(
                $"Producer property '{name}' must be non-negative.");
        }
        return result;
    }

    private static bool TryGetInt64(
        JsonElement value,
        string name,
        out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out JsonElement property) &&
               property.TryGetInt64(out result);
    }

    private static long RequirePositiveInt64(JsonElement value, string name)
    {
        long result = RequireInt64(value, name);
        if (result <= 0)
            throw new InvalidDataException($"Producer property '{name}' must be positive.");
        return result;
    }

    private static void RequireInt32(
        JsonElement value,
        string name,
        int expected)
    {
        long actual = RequireInt64(value, name);
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Producer property '{name}' is {actual}, expected {expected}.");
        }
    }

    private static double RequireFiniteNonNegative(
        JsonElement value,
        string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (!property.TryGetDouble(out double result) ||
            !double.IsFinite(result) ||
            result < 0.0)
        {
            throw new InvalidDataException(
                $"Producer property '{name}' must be finite and non-negative.");
        }
        return result;
    }

    private static double RequireFiniteNumber(
        JsonElement value,
        string name)
    {
        JsonElement property = RequireProperty(value, name);
        if (!property.TryGetDouble(out double result) ||
            !double.IsFinite(result))
        {
            throw new InvalidDataException(
                $"Producer property '{name}' must be finite.");
        }
        return result;
    }

    private static void RequireExactDouble(
        JsonElement value,
        string name,
        double expected)
    {
        double actual = RequireFiniteNonNegative(value, name);
        if (BitConverter.DoubleToInt64Bits(actual) !=
            BitConverter.DoubleToInt64Bits(expected))
        {
            throw new InvalidDataException(
                $"Producer threshold '{name}' is {actual.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"expected {expected.ToString("R", CultureInfo.InvariantCulture)}.");
        }
    }

    private static void RequireExactNumber(
        double actual,
        double expected,
        string name)
    {
        if (!double.IsFinite(actual) ||
            !double.IsFinite(expected) ||
            BitConverter.DoubleToInt64Bits(actual) !=
            BitConverter.DoubleToInt64Bits(expected))
        {
            throw new InvalidDataException(
                $"{name} is {actual.ToString("R", CultureInfo.InvariantCulture)}, " +
                $"expected {expected.ToString("R", CultureInfo.InvariantCulture)}.");
        }
    }

    private static void RequireEnumValue(
        JsonElement value,
        string name,
        int expectedNumeric,
        string expectedName)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int numeric) &&
            numeric == expectedNumeric)
        {
            return;
        }
        if (property.ValueKind == JsonValueKind.String &&
            string.Equals(
                property.GetString(),
                expectedName,
                StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidDataException(
            $"Producer property '{name}' does not match enum value '{expectedName}'.");
    }

    private static RenderBudgetStatus ReadBudgetStatus(
        JsonElement status,
        string metricName)
    {
        if (status.ValueKind == JsonValueKind.Number &&
            status.TryGetInt32(out int numeric) &&
            Enum.IsDefined(typeof(RenderBudgetStatus), numeric))
        {
            return (RenderBudgetStatus)numeric;
        }
        if (status.ValueKind == JsonValueKind.String &&
            Enum.TryParse(
                status.GetString(),
                ignoreCase: false,
                out RenderBudgetStatus parsed) &&
            Enum.IsDefined(parsed))
        {
            return parsed;
        }
        throw new InvalidDataException(
            $"Budget metric '{metricName}' has an invalid status.");
    }

    private static RenderBudgetStatus ClassifyBudgetMetric(
        double value,
        double warningThreshold,
        double failureThreshold)
    {
        if (BitConverter.DoubleToInt64Bits(warningThreshold) ==
            BitConverter.DoubleToInt64Bits(failureThreshold))
        {
            return value > failureThreshold
                ? RenderBudgetStatus.OverBudget
                : RenderBudgetStatus.WithinBudget;
        }
        return RenderBudgetEvaluator.Classify(value, failureThreshold);
    }

    private static void RequireNormalizedSha256(
        JsonElement value,
        string name,
        string expected)
    {
        string actual = RequireJsonString(value, name);
        string normalized;
        try
        {
            normalized =
                MaterialGiProducerSettingsFingerprint.NormalizeSha256(actual);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Producer property '{name}' is not a valid SHA-256 fingerprint.",
                exception);
        }
        if (!string.Equals(normalized, expected, StringComparison.Ordinal) ||
            (!string.Equals(actual, expected, StringComparison.Ordinal) &&
             !string.Equals(
                 actual,
                 $"sha256:{expected}",
                 StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"Producer property '{name}' does not match exactly.");
        }
    }

    private static void RequireTrue(JsonElement value, string name)
    {
        if (RequireProperty(value, name).ValueKind != JsonValueKind.True)
            throw new InvalidDataException($"Producer property '{name}' must be true.");
    }

    private static void RequireFalse(JsonElement value, string name)
    {
        if (RequireProperty(value, name).ValueKind != JsonValueKind.False)
            throw new InvalidDataException($"Producer property '{name}' must be false.");
    }

    private static DateTimeOffset RequireDateTimeOffset(
        JsonElement value,
        string name)
    {
        string text = RequireJsonString(value, name);
        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset result))
        {
            throw new InvalidDataException(
                $"Producer property '{name}' must be an ISO-8601 timestamp.");
        }
        return result;
    }

    private static void RequireJsonSha256(JsonElement value, string name) =>
        RequireSha256(RequireJsonString(value, name), $"producer property '{name}'");

    private static bool IsPassingBudgetStatus(JsonElement status)
    {
        if (status.ValueKind == JsonValueKind.Number &&
            status.TryGetInt32(out int numeric))
        {
            return numeric is 1 or 2;
        }
        if (status.ValueKind == JsonValueKind.String)
        {
            string? text = status.GetString();
            return string.Equals(text, "WithinBudget", StringComparison.Ordinal) ||
                   string.Equals(text, "Warning", StringComparison.Ordinal);
        }
        return false;
    }

    private static void RequireQualityPreset(
        JsonElement value,
        string name,
        string expected)
    {
        JsonElement property = RequireProperty(value, name);
        if (property.ValueKind == JsonValueKind.String &&
            string.Equals(
                property.GetString(),
                expected,
                StringComparison.Ordinal))
        {
            return;
        }
        int expectedNumeric = expected switch
        {
            "Low" => 0,
            "Medium" => 1,
            "High" => 2,
            "Ultra" => 3,
            "DdgiHigh" => 4,
            _ => -1
        };
        if (property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out int actualNumeric) &&
            actualNumeric == expectedNumeric)
        {
            return;
        }
        throw new InvalidDataException(
            $"Producer property '{name}' does not match quality tier '{expected}'.");
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static long SaturatingDifference(ulong left, ulong right)
    {
        if (left >= right)
        {
            ulong difference = left - right;
            return difference > long.MaxValue
                ? long.MaxValue
                : (long)difference;
        }
        ulong magnitude = right - left;
        return magnitude > (ulong)long.MaxValue
            ? long.MinValue
            : -(long)magnitude;
    }
}

/// <summary>
/// Production helper for pinning existing producer outputs into qualification
/// artifacts. It computes identities from the bytes on disk; callers do not
/// supply byte counts or payload hashes.
/// </summary>
public static class MaterialGiReleaseEvidenceAssembler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions StrictJsonOptions = new()
    {
        AllowTrailingCommas = false,
        MaxDepth = 32,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static MaterialGiProducerEvidenceArtifact PinProducer(
        string manifestDirectory,
        string producerPath,
        string kind,
        string schema,
        MaterialGiEvidenceDeviceIdentity device,
        string buildCommit,
        string shaderFingerprint,
        string settingsFingerprint,
        string qualityTier = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(producerPath);
        ArgumentNullException.ThrowIfNull(device);
        string root = Path.GetFullPath(manifestDirectory);
        string path = Path.GetFullPath(producerPath);
        string relativePath = Path.GetRelativePath(root, path)
            .Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Producer path must be contained by the manifest directory.",
                nameof(producerPath));
        }
        byte[] producerBytes = BoundedFileReader.ReadStable(
            path,
            MaterialGiReleaseEvidenceContract.MaximumArtifactBytes,
            "Producer artifact");
        long byteLength = producerBytes.LongLength;
        string sha256 = Convert.ToHexString(SHA256.HashData(producerBytes))
            .ToLowerInvariant();
        return new MaterialGiProducerEvidenceArtifact
        {
            Kind = kind,
            Schema = schema,
            ManifestRelativePath = relativePath,
            ByteLength = byteLength,
            Sha256 = sha256,
            DeviceId = device.DeviceId,
            GpuName = device.GpuName,
            DriverVersion = device.DriverVersion,
            BuildCommit = buildCommit,
            ShaderFingerprint = shaderFingerprint,
            SettingsFingerprint = settingsFingerprint,
            QualityTier = qualityTier
        };
    }

    public static MaterialGiReleaseEvidenceArtifact WriteRoleReport(
        string manifestDirectory,
        string reportPath,
        MaterialGiReleaseEvidenceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string root = Path.GetFullPath(manifestDirectory);
        string path = Path.GetFullPath(reportPath);
        string relativePath = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                "Role report path must be contained by the manifest directory.",
                nameof(reportPath));
        }
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length <= 0 ||
            bytes.Length > MaterialGiReleaseEvidenceContract.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "Role report serialization has an invalid bounded length.");
        }
        var validationBundle = new MaterialGiReleaseEvidenceBundle
        {
            BuildCommit = report.BuildCommit,
            ShaderFingerprint = report.ShaderFingerprint,
            SettingsContractFingerprint = report.SettingsContractFingerprint,
            Devices = report.Devices
        };
        var qualifiedDevices = new HashSet<string>(
            report.DeviceIds,
            StringComparer.OrdinalIgnoreCase);
        MaterialGiRolloutQualificationManifest
            .ValidateReleaseEvidenceRoleForAssembly(
                report,
                qualifiedDevices);
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            authenticatedDevices =
                MaterialGiReleaseEvidenceAuthenticity.ValidateBundleIdentity(
                    validationBundle,
                    qualifiedDevices);
        var pinnedPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            path
        };
        MaterialGiReleaseEvidenceAuthenticity.ValidateRole(
            root,
            validationBundle,
            report,
            authenticatedDevices,
            pinnedPaths);
        WriteAtomically(path, bytes);
        return new MaterialGiReleaseEvidenceArtifact
        {
            Role = report.Role,
            ManifestRelativePath = relativePath,
            ByteLength = bytes.LongLength,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant()
        };
    }

    public static void WriteTestMatrixReport(
        string path,
        MaterialGiTestMatrixProducerReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions);
        if (bytes.Length <= 0 ||
            bytes.Length > MaterialGiReleaseEvidenceContract.MaximumArtifactBytes)
        {
            throw new InvalidDataException(
                "Test-matrix report serialization has an invalid bounded length.");
        }
        WriteAtomically(fullPath, bytes);
    }

    public static void WriteBundle(
        string manifestDirectory,
        string bundlePath,
        MaterialGiReleaseEvidenceBundle bundle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.SchemaVersion !=
            MaterialGiReleaseEvidenceContract.BundleSchemaVersion)
        {
            throw new InvalidDataException(
                $"Cannot publish release evidence bundle schema {bundle.SchemaVersion}; " +
                $"expected {MaterialGiReleaseEvidenceContract.BundleSchemaVersion}.");
        }
        string root = Path.GetFullPath(manifestDirectory);
        string path = Path.GetFullPath(bundlePath);
        RequireContainedOutput(root, path, nameof(bundlePath));
        MaterialGiEvidenceDeviceIdentity[] devices = bundle.Devices ??
            throw new InvalidDataException("Release evidence bundle devices are null.");
        var qualifiedDevices = new HashSet<string>(
            devices.Select(static device => device.DeviceId),
            StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, MaterialGiEvidenceDeviceIdentity>
            authenticatedDevices =
                MaterialGiReleaseEvidenceAuthenticity.ValidateBundleIdentity(
                    bundle,
                    qualifiedDevices);
        MaterialGiReleaseEvidenceArtifact[] artifacts = bundle.Artifacts ??
            throw new InvalidDataException("Release evidence bundle artifacts are null.");
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var pinnedPaths = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
        {
            path
        };
        foreach (MaterialGiReleaseEvidenceArtifact artifact in artifacts)
        {
            if (artifact is null ||
                !MaterialGiReleaseEvidenceContract.RequiredRoles.Contains(
                    artifact.Role,
                    StringComparer.Ordinal) ||
                !roles.Add(artifact.Role))
            {
                throw new InvalidDataException(
                    "Release evidence bundle has a null, unknown, or duplicate role.");
            }
            string reportPath = ResolveContainedInput(
                root,
                artifact.ManifestRelativePath,
                $"role '{artifact.Role}' report");
            if (!pinnedPaths.Add(reportPath))
            {
                throw new InvalidDataException(
                    $"Role report '{artifact.ManifestRelativePath}' is duplicated or aliases the bundle.");
            }
            byte[] reportBytes = ReadPinnedBytes(
                reportPath,
                artifact.ByteLength,
                artifact.Sha256,
                $"role '{artifact.Role}' report");
            MaterialGiReleaseEvidenceReport report;
            try
            {
                StrictJsonContract.RejectDuplicateProperties(
                    reportBytes,
                    StrictJsonOptions.MaxDepth,
                    $"Role '{artifact.Role}' report");
                report =
                    JsonSerializer.Deserialize<MaterialGiReleaseEvidenceReport>(
                        reportBytes,
                        StrictJsonOptions)
                    ?? throw new InvalidDataException(
                        $"Role '{artifact.Role}' report is null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Role '{artifact.Role}' report is invalid or contains unknown metadata.",
                    exception);
            }
            if (report.SchemaVersion !=
                    MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion ||
                !string.Equals(
                    report.Role,
                    artifact.Role,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    report.Status,
                    MaterialGiReleaseEvidenceContract.PassedStatus,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Role '{artifact.Role}' report is not a current passed artifact.");
            }
            MaterialGiRolloutQualificationManifest
                .ValidateReleaseEvidenceRoleForAssembly(
                    report,
                    qualifiedDevices);
            MaterialGiReleaseEvidenceAuthenticity.ValidateRole(
                root,
                bundle,
                report,
                authenticatedDevices,
                pinnedPaths);
        }
        if (!roles.SetEquals(MaterialGiReleaseEvidenceContract.RequiredRoles))
        {
            throw new InvalidDataException(
                "Release evidence bundle does not contain every required role exactly once.");
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(bundle, JsonOptions);
        if (bytes.Length <= 0 ||
            bytes.Length > MaterialGiReleaseEvidenceContract.MaximumBundleBytes)
        {
            throw new InvalidDataException(
                "Release evidence bundle serialization has an invalid bounded length.");
        }
        WriteAtomically(path, bytes);
    }

    /// <summary>
    /// Pins an already validated evidence bundle and alpha-visibility pair
    /// into a qualification manifest. Human approval remains an explicit
    /// caller-supplied identity and timestamp. The candidate manifest is fully
    /// authenticated from a same-directory temporary path before the stable
    /// manifest is atomically replaced.
    /// </summary>
    public static MaterialGiRolloutQualificationManifest
        WriteQualificationManifest(
            string manifestDirectory,
            string manifestPath,
            string bundlePath,
            string alphaVisibilityReportPath,
            string alphaVisibilityEvidencePath,
            IEnumerable<string> qualifiedDeviceIds,
            string approvalId,
            DateTimeOffset approvedAtUtc,
            MaterialGiV2Feature enabledFeatures =
                MaterialGiV2Feature.All)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            alphaVisibilityReportPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            alphaVisibilityEvidencePath);
        ArgumentNullException.ThrowIfNull(qualifiedDeviceIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);

        string root = Path.GetFullPath(manifestDirectory);
        string path = Path.GetFullPath(manifestPath);
        string bundle = Path.GetFullPath(bundlePath);
        string alphaReport = Path.GetFullPath(
            alphaVisibilityReportPath);
        string alphaEvidence = Path.GetFullPath(
            alphaVisibilityEvidencePath);
        RequireContainedOutput(root, path, nameof(manifestPath));
        RequireContainedOutput(root, bundle, nameof(bundlePath));
        RequireContainedOutput(
            root,
            alphaReport,
            nameof(alphaVisibilityReportPath));
        RequireContainedOutput(
            root,
            alphaEvidence,
            nameof(alphaVisibilityEvidencePath));

        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        string? manifestParent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(manifestParent) ||
            !pathComparer.Equals(
                Path.TrimEndingDirectorySeparator(root),
                Path.TrimEndingDirectorySeparator(manifestParent)))
        {
            throw new ArgumentException(
                "Qualification manifest must be written directly inside its manifest directory.",
                nameof(manifestPath));
        }
        if (new[] { path, bundle, alphaReport, alphaEvidence }
                .Distinct(pathComparer)
                .Count() != 4)
        {
            throw new ArgumentException(
                "Qualification manifest, bundle, alpha report, and alpha evidence paths must be distinct.");
        }
        if (approvedAtUtc == default ||
            approvedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Qualification approval timestamp must have a zero UTC offset.",
                nameof(approvedAtUtc));
        }
        if (!string.Equals(
                approvalId,
                approvalId.Trim(),
                StringComparison.Ordinal) ||
            approvalId.Length > 512 ||
            approvalId.Contains('\0'))
        {
            throw new ArgumentException(
                "Qualification approval ID must be canonical and at most 512 characters.",
                nameof(approvalId));
        }
        if (enabledFeatures == MaterialGiV2Feature.None ||
            (enabledFeatures & ~MaterialGiV2Feature.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enabledFeatures),
                enabledFeatures,
                "Qualification must enable a non-empty known V2 feature mask.");
        }

        string[] devices = qualifiedDeviceIds
            .Select(static id => id ??
                throw new ArgumentException(
                    "Qualified device identifiers cannot contain null."))
            .Take(
                MaterialGiReleaseEvidenceContract
                    .MaximumProducerArtifactCount + 1)
            .ToArray();
        if (devices.Length >
                MaterialGiReleaseEvidenceContract
                    .MaximumProducerArtifactCount ||
            devices.Any(static id =>
                string.IsNullOrWhiteSpace(id) ||
                !string.Equals(
                    id,
                    id.Trim(),
                    StringComparison.Ordinal)) ||
            devices.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                devices.Length)
        {
            throw new ArgumentException(
                "Qualified device identifiers must be canonical and unique.",
                nameof(qualifiedDeviceIds));
        }

        byte[] bundleBytes = BoundedFileReader.ReadStable(
            bundle,
            MaterialGiReleaseEvidenceContract.MaximumBundleBytes,
            "Release evidence bundle");
        MaterialGiReleaseEvidenceBundle parsedBundle;
        try
        {
            StrictJsonContract.RejectDuplicateProperties(
                bundleBytes,
                StrictJsonOptions.MaxDepth,
                "Release evidence bundle");
            parsedBundle =
                JsonSerializer.Deserialize<MaterialGiReleaseEvidenceBundle>(
                    bundleBytes,
                    StrictJsonOptions)
                ?? throw new InvalidDataException(
                    "Release evidence bundle is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Release evidence bundle is invalid or contains unknown metadata.",
                exception);
        }
        if (parsedBundle.SchemaVersion !=
            MaterialGiReleaseEvidenceContract.BundleSchemaVersion)
        {
            throw new InvalidDataException(
                "Release evidence bundle is not the current schema.");
        }

        byte[] alphaReportBytes = BoundedFileReader.ReadStable(
            alphaReport,
            MaterialGiReleaseEvidenceContract.MaximumArtifactBytes,
            "Alpha-visibility report");
        byte[] alphaEvidenceBytes = BoundedFileReader.ReadStable(
            alphaEvidence,
            MaterialGiReleaseEvidenceContract.MaximumArtifactBytes,
            "Alpha-visibility evidence");
        var manifest = new MaterialGiRolloutQualificationManifest
        {
            EnabledFeatures = enabledFeatures,
            QualifiedDeviceIds = devices,
            ReleaseEvidenceBundleRelativePath =
                ToCanonicalRelativePath(root, bundle, "bundle"),
            ReleaseEvidenceBundleSha256 =
                ComputeSha256(bundleBytes),
            EvidenceSha256 =
                MaterialGiReleaseEvidenceContract
                    .ComputeAggregateSha256(parsedBundle),
            ApprovalId = approvalId,
            ApprovedAtUtc = approvedAtUtc,
            AlphaVisibilityReportRelativePath =
                ToCanonicalRelativePath(
                    root,
                    alphaReport,
                    "alpha report"),
            AlphaVisibilityReportSha256 =
                ComputeSha256(alphaReportBytes),
            AlphaVisibilityEvidenceRelativePath =
                ToCanonicalRelativePath(
                    root,
                    alphaEvidence,
                    "alpha evidence"),
            AlphaVisibilityEvidenceSha256 =
                ComputeSha256(alphaEvidenceBytes)
        };

        Directory.CreateDirectory(manifestParent);
        string temporaryPath = Path.Combine(
            manifestParent,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.candidate");
        byte[] manifestBytes =
            JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        try
        {
            WriteAtomically(temporaryPath, manifestBytes);
            MaterialGiRolloutQualificationManifest authenticated =
                MaterialGiRolloutQualificationManifest.Load(
                    temporaryPath);
            IReadOnlyList<string> failures = authenticated.Validate(
                DateOnly.FromDateTime(DateTime.UtcNow));
            if (failures.Count != 0)
            {
                throw new InvalidDataException(
                    "Qualification candidate failed validation: " +
                    string.Join(" ", failures));
            }
            File.Move(temporaryPath, path, overwrite: true);
            return MaterialGiRolloutQualificationManifest.Load(path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static byte[] ReadPinnedBytes(
        string path,
        long expectedLength,
        string expectedSha256,
        string name)
    {
        if (expectedLength <= 0 ||
            expectedLength > MaterialGiReleaseEvidenceContract.MaximumArtifactBytes ||
            expectedSha256 is not { Length: 64 })
        {
            throw new InvalidDataException($"{name} has an invalid pinned identity.");
        }
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"{name} has an invalid pinned SHA-256.", exception);
        }
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new InvalidDataException($"{name} does not match its pinned byte length.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        byte[] actual = SHA256.HashData(bytes);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException($"{name} does not match its pinned SHA-256.");
        return bytes;
    }

    private static string ToCanonicalRelativePath(
        string root,
        string path,
        string role)
    {
        RequireContainedOutput(root, path, role);
        string relative = Path.GetRelativePath(root, path)
            .Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) ||
            relative.Split(
                '/',
                StringSplitOptions.None)
                .Any(static segment =>
                    segment.Length == 0 ||
                    segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"Qualification {role} path is not canonical.");
        }
        return relative;
    }

    private static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))
            .ToLowerInvariant();

    private static string ResolveContainedInput(
        string root,
        string relativePath,
        string name)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Split(['/', '\\']).Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"{name} path must be canonical and manifest-relative.");
        }
        string path = Path.GetFullPath(
            Path.Combine(
                root,
                relativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar)));
        RequireContainedOutput(root, path, name);
        return path;
    }

    private static void RequireContainedOutput(
        string root,
        string path,
        string name)
    {
        string boundary = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(boundary, comparison))
        {
            throw new ArgumentException(
                $"{name} must be contained by the manifest directory.",
                name);
        }
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidDataException("Evidence output path has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
