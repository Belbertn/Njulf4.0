using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialGiRolloutPolicyTests
{
    private static readonly DateOnly QualificationDate = new(2026, 7, 28);

    [Test]
    public void RenderSettings_DefaultToUnqualifiedLegacyMaterialGi()
    {
        var settings = new RenderSettings();
        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.GiMaterialTransportV2, Is.False);
            Assert.That(settings.GlobalIllumination.GiEmissiveMeshSampling, Is.False);
            Assert.That(settings.GlobalIllumination.GiFarFieldMaterialV2, Is.False);
            Assert.That(settings.GlobalIllumination.GiHybridCompositionV2, Is.False);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.None));
            Assert.That(
                settings.GlobalIllumination.EffectiveGiMaterialTransportV2,
                Is.False);
            Assert.That(evaluation.Mode, Is.EqualTo(MaterialGiRolloutMode.LegacyUnqualified));
            Assert.That(evaluation.ActiveFeatures, Is.EqualTo(MaterialGiV2Feature.None));
            Assert.That(evaluation.ReleaseQualificationRequired, Is.False);
        });
    }

    [Test]
    public void ConformanceOptIn_IsExplicitAndNeverClaimsReleaseQualification()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.EnableMaterialGiV2ForConformance(
            MaterialGiV2Feature.MaterialTransport |
            MaterialGiV2Feature.FarFieldMaterial);

        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(settings.GlobalIllumination.GiMaterialTransportV2, Is.True);
            Assert.That(settings.GlobalIllumination.GiFarFieldMaterialV2, Is.True);
            Assert.That(settings.GlobalIllumination.GiEmissiveMeshSampling, Is.False);
            Assert.That(settings.GlobalIllumination.GiHybridCompositionV2, Is.False);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(
                    MaterialGiV2Feature.MaterialTransport |
                    MaterialGiV2Feature.FarFieldMaterial));
            Assert.That(evaluation.Mode, Is.EqualTo(MaterialGiRolloutMode.Conformance));
            Assert.That(evaluation.ReleaseQualificationRequired, Is.False);
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(evaluation.QualificationFailureCount, Is.Zero);
        });
    }

    [Test]
    public void QualificationCandidate_IsExplicitEvidenceModeAndNeverClaimsApproval()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination
            .EnableMaterialGiV2ForQualificationCandidate();

        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination
                .EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(
                evaluation.Mode,
                Is.EqualTo(MaterialGiRolloutMode.QualificationCandidate));
            Assert.That(
                evaluation.ActiveFeatures,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(evaluation.ReleaseQualificationRequired, Is.True);
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(evaluation.QualificationFailureCount, Is.Zero);
            Assert.That(evaluation.ApprovalId, Is.Empty);
            Assert.That(evaluation.EvidenceSha256, Is.Empty);
            Assert.That(evaluation.QualifiedDeviceCount, Is.Zero);
            Assert.That(
                evaluation.QualificationSummary,
                Does.Contain("non-shipping qualification candidate"));
        });
    }

    [Test]
    public void QualificationCandidate_RejectsPartialFeatureCoverage()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination
            .EnableMaterialGiV2ForQualificationCandidate();
        settings.GlobalIllumination.GiFarFieldMaterialV2 = false;

        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination
                .EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(
                evaluation.Mode,
                Is.EqualTo(MaterialGiRolloutMode.QualificationCandidate));
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(evaluation.QualificationFailureCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void QualificationCandidate_ExpiresWithV1CompatibilityContract()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination
            .EnableMaterialGiV2ForQualificationCandidate();

        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(
                MaterialGiV1CompatibilityContract.RemovalTargetDate.AddDays(1));

        Assert.Multiple(() =>
        {
            Assert.That(
                evaluation.Mode,
                Is.EqualTo(MaterialGiRolloutMode.QualificationCandidate));
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(evaluation.QualificationFailureCount, Is.EqualTo(1));
            Assert.That(
                evaluation.QualificationSummary,
                Does.Contain("expired"));
        });
    }

    [Test]
    public void ManualV2Override_IsReleaseBlockingWithoutQualification()
    {
        var settings = new RenderSettings();
        settings.GlobalIllumination.GiMaterialTransportV2 = true;

        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.ReleaseQualificationRequired, Is.True);
            Assert.That(evaluation.ReleaseQualified, Is.False);
            Assert.That(evaluation.QualificationFailureCount, Is.GreaterThan(0));
            Assert.That(evaluation.QualificationSummary, Does.Contain("No qualified release manifest"));
            Assert.That(
                settings.GlobalIllumination.ConfiguredMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.MaterialTransport));
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.None));
            Assert.That(
                settings.GlobalIllumination.EffectiveGiMaterialTransportV2,
                Is.False);
        });
    }

    [Test]
    public void QualifiedRelease_RequiresTwoDevicesAndHumanApproval()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        var settings = new RenderSettings();
        MaterialGiRolloutQualificationManifest authenticated = artifacts.Load();
        MaterialGiRolloutQualificationManifest invalid = authenticated with
        {
            QualifiedDeviceIds =
            [
                SyntheticMaterialGiQualification.AlphaDeviceName,
                SyntheticMaterialGiQualification.AlphaDeviceName.ToUpperInvariant()
            ]
        };

        Assert.That(
            () => settings.GlobalIllumination.ApplyMaterialGiV2Qualification(
                invalid,
                QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("two distinct"));
        Assert.That(
            settings.GlobalIllumination.ActiveMaterialGiV2Features,
            Is.EqualTo(MaterialGiV2Feature.None));

        MaterialGiRolloutQualificationManifest missingApproval =
            authenticated with { ApprovalId = string.Empty };
        Assert.That(
            () => settings.GlobalIllumination.ApplyMaterialGiV2Qualification(
                missingApproval,
                QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("approval identifier"));

        settings.GlobalIllumination.ApplyMaterialGiV2Qualification(
            authenticated,
            QualificationDate);
        MaterialGiRolloutEvaluation evaluation =
            settings.GlobalIllumination.EvaluateMaterialGiRollout(QualificationDate);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.Mode, Is.EqualTo(MaterialGiRolloutMode.QualifiedRelease));
            Assert.That(evaluation.ReleaseQualificationRequired, Is.True);
            Assert.That(evaluation.ReleaseQualified, Is.True);
            Assert.That(evaluation.QualificationFailureCount, Is.Zero);
            Assert.That(
                settings.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(
                settings.GlobalIllumination.EffectiveGiMaterialTransportV2,
                Is.True);
            Assert.That(
                settings.GlobalIllumination.EffectiveGiEmissiveMeshSampling,
                Is.True);
            Assert.That(
                settings.GlobalIllumination.EffectiveGiFarFieldMaterialV2,
                Is.True);
            Assert.That(
                settings.GlobalIllumination.EffectiveGiHybridCompositionV2,
                Is.True);
            Assert.That(evaluation.ApprovalId, Is.EqualTo("material-gi-release-2026-07"));
            Assert.That(
                evaluation.EvidenceSha256,
                Is.EqualTo(artifacts.Manifest.EvidenceSha256));
            Assert.That(evaluation.QualifiedDeviceCount, Is.EqualTo(2));
            Assert.That(
                authenticated.AuthenticatedReleaseEvidenceRoleCount,
                Is.EqualTo(MaterialGiReleaseEvidenceContract.RequiredRoles.Count));
            Assert.That(authenticated.AuthenticatedTierDeviceCount, Is.EqualTo(2));
            Assert.That(
                authenticated.AuthenticatedLowerMemoryRayQueryDeviceCount,
                Is.EqualTo(1));
            Assert.That(
                authenticated.AuthenticatedRecoveryCapabilitySummary,
                Is.EqualTo("supported=0,unsupported=2"));
            Assert.That(evaluation.V1RemovalOwner,
                Is.EqualTo(MaterialGiV1CompatibilityContract.Owner));
            Assert.That(evaluation.V1RemovalTargetDate,
                Is.EqualTo(MaterialGiV1CompatibilityContract.RemovalTargetDate));
        });
    }

    [Test]
    public void QualifiedRelease_ExpiresAtTheV1RemovalContract()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        var settings = new RenderSettings();
        DateOnly expiredDate =
            MaterialGiV1CompatibilityContract.RemovalTargetDate.AddDays(1);

        Assert.That(
            () => settings.GlobalIllumination.ApplyMaterialGiV2Qualification(
                artifacts.Load(),
                expiredDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("has expired"));
    }

    [Test]
    public void Qualification_CannotBypassLoadAuthenticationByConstructionOrMutation()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();

        var directPolicy = new MaterialGiRolloutPolicy();
        Assert.That(
            () => directPolicy.ApplyQualification(
                artifacts.Manifest,
                QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("loaded from a manifest"));

        MaterialGiRolloutQualificationManifest authenticated = artifacts.Load();
        MaterialGiRolloutQualificationManifest mutated = authenticated with
        {
            ApprovalId = "unreviewed-approval"
        };
        var mutatedPolicy = new MaterialGiRolloutPolicy();
        Assert.That(
            () => mutatedPolicy.ApplyQualification(mutated, QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("authentication seal"));

        string serialized = JsonSerializer.Serialize(authenticated);
        Assert.That(
            serialized,
            Does.Not.Contain("authenticationSeal").IgnoreCase);
        MaterialGiRolloutQualificationManifest deserialized =
            JsonSerializer.Deserialize<MaterialGiRolloutQualificationManifest>(
                serialized)!;
        var roundTripPolicy = new MaterialGiRolloutPolicy();
        Assert.That(
            () => roundTripPolicy.ApplyQualification(
                deserialized,
                QualificationDate),
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("loaded from a manifest"));
    }

    [Test]
    public void QualificationLoad_RejectsUnknownJsonMetadata()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        JsonObject json = JsonNode.Parse(
                File.ReadAllText(artifacts.ManifestPath))!
            .AsObject();
        json["UnreviewedReleaseOverride"] = true;
        File.WriteAllText(artifacts.ManifestPath, json.ToJsonString());

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("unknown JSON metadata"));
    }

    [TestCase(
        false,
        "invalid or unknown JSON metadata",
        TestName = "QualificationLoad_RejectsMalformedManifest")]
    [TestCase(
        true,
        "invalid bounded length",
        TestName = "QualificationLoad_RejectsOversizedManifest")]
    public void QualificationLoad_RejectsInvalidBoundedManifest(
        bool oversized,
        string expectedMessage)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        byte[] bytes = oversized
            ? new byte[256 * 1024 + 1]
            : System.Text.Encoding.UTF8.GetBytes("{");
        File.WriteAllBytes(artifacts.ManifestPath, bytes);

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains(expectedMessage));
    }

    [TestCase(
        "manifest",
        TestName = "QualificationLoad_RejectsDuplicateManifestJsonProperty")]
    [TestCase(
        "bundle",
        TestName = "QualificationLoad_RejectsDuplicateBundleJsonProperty")]
    [TestCase(
        "role",
        TestName = "QualificationLoad_RejectsDuplicateRoleJsonProperty")]
    [TestCase(
        "producer",
        TestName = "QualificationLoad_RejectsDuplicateProducerJsonProperty")]
    public void QualificationLoad_RejectsAmbiguousDuplicateJsonProperties(
        string artifactKind)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
        switch (artifactKind)
        {
            case "manifest":
                {
                    File.WriteAllBytes(
                        artifacts.ManifestPath,
                        AddDuplicateRootProperty(
                            artifacts.ManifestPath,
                            "SchemaVersion"));
                    break;
                }
            case "bundle":
                {
                    File.WriteAllBytes(
                        artifacts.BundlePath,
                        AddDuplicateRootProperty(
                            artifacts.BundlePath,
                            "SchemaVersion"));
                    artifacts.WriteManifest(artifacts.Manifest with
                    {
                        ReleaseEvidenceBundleSha256 =
                            SyntheticMaterialGiQualification.ComputeSha256(
                                artifacts.BundlePath)
                    });
                    break;
                }
            case "role":
                {
                    string rolePath = artifacts.GetReleaseEvidencePath(role);
                    artifacts.WriteReleaseEvidenceAndRepin(
                        role,
                        AddDuplicateRootProperty(rolePath, "SchemaVersion"));
                    break;
                }
            case "producer":
                {
                    MaterialGiReleaseEvidenceReport report =
                        artifacts.GetReleaseEvidenceReport(role);
                    MaterialGiProducerEvidenceArtifact producer =
                        report.Producers[0];
                    string producerPath = Path.Combine(
                        artifacts.DirectoryPath,
                        producer.ManifestRelativePath
                            .Replace('\\', Path.DirectorySeparatorChar)
                            .Replace('/', Path.DirectorySeparatorChar));
                    File.WriteAllBytes(
                        producerPath,
                        AddDuplicateNestedProperty(
                            producerPath,
                            "producerIdentity",
                            "buildCommit"));
                    MaterialGiProducerEvidenceArtifact[] producers =
                        [.. report.Producers];
                    producers[0] = producer with
                    {
                        ByteLength = new FileInfo(producerPath).Length,
                        Sha256 =
                            SyntheticMaterialGiQualification.ComputeSha256(
                                producerPath)
                    };
                    artifacts.WriteReleaseEvidenceReportAndRepin(
                        role,
                        report with { Producers = producers });
                    break;
                }
            default:
                Assert.Fail($"Unknown artifact kind '{artifactKind}'.");
                break;
        }

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("duplicate JSON property"));
    }

    [Test]
    public void QualificationLoad_RequiresEveryKnownReleaseEvidenceRoleExactlyOnce()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiReleaseEvidenceBundle original = artifacts.Bundle;

        artifacts.WriteBundleAndRepin(original with
        {
            Artifacts =
            [
                .. original.Artifacts.Where(artifact =>
                    !string.Equals(
                        artifact.Role,
                        MaterialGiReleaseEvidenceContract.LifecycleResilienceRole,
                        StringComparison.Ordinal))
            ]
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("missing required role"));

        MaterialGiReleaseEvidenceArtifact[] duplicated =
            [.. original.Artifacts];
        duplicated[^1] = duplicated[^1] with
        {
            Role = duplicated[0].Role
        };
        artifacts.WriteBundleAndRepin(original with
        {
            Artifacts = duplicated
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("duplicated"));

        MaterialGiReleaseEvidenceArtifact[] unknown =
            [.. original.Artifacts];
        unknown[^1] = unknown[^1] with
        {
            Role = "unreviewed-release-override"
        };
        artifacts.WriteBundleAndRepin(original with
        {
            Artifacts = unknown
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("unknown"));
    }

    [Test]
    public void QualificationLoad_RejectsNonPassedReleaseEvidenceStatus()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            artifacts.GetReleaseEvidenceReport(role) with
            {
                Status = "Failed"
            });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("not Passed"));
    }

    [Test]
    public void QualificationLoad_RejectsRepinnedGenericPassedProducerBlob()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.MutateProducerAndRepin(
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
            json =>
            {
                json.Clear();
                json["SchemaVersion"] =
                    MaterialGiReleaseEvidenceContract.ArtifactSchemaVersion;
                json["Role"] =
                    MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
                json["Status"] =
                    MaterialGiReleaseEvidenceContract.PassedStatus;
                json["Summary"] = "repinned generic passed blob";
            });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("missing required property"));
    }

    [TestCase("build-commit")]
    [TestCase("shader-fingerprint")]
    [TestCase("settings-fingerprint")]
    [TestCase("gpu-identity")]
    [TestCase("driver-identity")]
    public void QualificationLoad_RejectsMismatchedReleaseIdentity(
        string identity)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
        MaterialGiReleaseEvidenceReport report =
            artifacts.GetReleaseEvidenceReport(role);
        switch (identity)
        {
            case "build-commit":
                report = report with
                {
                    BuildCommit =
                        "1123456789abcdef0123456789abcdef01234567"
                };
                break;
            case "shader-fingerprint":
                report = report with
                {
                    ShaderFingerprint =
                        "3111111111111111111111111111111111111111111111111111111111111111"
                };
                break;
            case "settings-fingerprint":
                report = report with
                {
                    SettingsContractFingerprint =
                        "4222222222222222222222222222222222222222222222222222222222222222"
                };
                break;
            case "gpu-identity":
                report = report with
                {
                    Devices =
                    [
                        report.Devices[0] with
                        {
                            GpuName = "Repinned Different GPU"
                        }
                    ]
                };
                break;
            case "driver-identity":
                report = report with
                {
                    Producers =
                    [
                        report.Producers[0] with
                        {
                            DriverVersion = "repinned-driver"
                        }
                    ]
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(identity));
        }
        artifacts.WriteReleaseEvidenceReportAndRepin(role, report);

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains(identity switch
                {
                    "build-commit" => "build commit",
                    "shader-fingerprint" => "shader fingerprint",
                    "settings-fingerprint" =>
                        "settings-contract fingerprint",
                    _ => "GPU/driver identity"
                }));
    }

    [TestCase("hdr-threshold")]
    [TestCase("hdr-inflated-threshold")]
    [TestCase("hdr-missing-roi")]
    [TestCase("graphics-missing-signal")]
    [TestCase("benchmark-status")]
    [TestCase("benchmark-legacy-schema")]
    [TestCase("benchmark-inflated-threshold")]
    [TestCase("benchmark-missing-gate")]
    [TestCase("benchmark-candidate-intent")]
    [TestCase("benchmark-conformance-rollout")]
    [TestCase("benchmark-partial-features")]
    [TestCase("benchmark-release-claim")]
    [TestCase("soak-telemetry")]
    [TestCase("soak-wall-clock")]
    [TestCase("soak-cadence")]
    [TestCase("soak-descriptor")]
    public void QualificationLoad_RejectsRepinnedProducerOutcomeTampering(
        string tamper)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        switch (tamper)
        {
            case "hdr-threshold":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
                    json =>
                    {
                        JsonObject image =
                            json["images"]!.AsArray()[0]!.AsObject();
                        image["relativeRmse"] = 0.5;
                    });
                break;
            case "hdr-inflated-threshold":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
                    json =>
                    {
                        JsonObject image =
                            json["images"]!.AsArray()[0]!.AsObject();
                        image["maximumRelativeRmse"] = 0.5;
                    });
                break;
            case "hdr-missing-roi":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
                    json => json["roiGates"]!.AsArray().RemoveAt(3));
                break;
            case "graphics-missing-signal":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract
                        .GraphicsAsyncEquivalenceRole,
                    json => json["outputs"]!.AsArray().RemoveAt(0));
                break;
            case "benchmark-status":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json =>
                    {
                        JsonObject metric =
                            json["BudgetMetrics"]!.AsArray()[0]!.AsObject();
                        metric["Status"] = 3;
                    },
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-legacy-schema":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json => json["Schema"] =
                        "njulf-renderer-benchmark/v2",
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-inflated-threshold":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json =>
                    {
                        JsonObject metric = json["BudgetMetrics"]!
                            .AsArray()
                            .Select(static node => node!.AsObject())
                            .Single(metric => string.Equals(
                                metric["Name"]!.GetValue<string>(),
                                "CPU renderer",
                                StringComparison.Ordinal));
                        metric["WarningThreshold"] = 849.15;
                        metric["FailureThreshold"] = 999.0;
                    },
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-missing-gate":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json => json["DdgiProductionGate"]!["Criteria"]!
                        .AsArray()
                        .RemoveAt(0),
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-candidate-intent":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json => json["Options"]![
                        "MaterialGiQualificationCandidate"] = false,
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-conformance-rollout":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json => json["LastDiagnostics"]![
                        "MaterialGiRolloutMode"] = "Conformance",
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-partial-features":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json => json["LastDiagnostics"]![
                        "MaterialGiV2ActiveFeatures"] = "MaterialTransport",
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "benchmark-release-claim":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    json =>
                    {
                        JsonNode diagnostics = json["LastDiagnostics"]!;
                        diagnostics["MaterialGiReleaseQualified"] = 1;
                        diagnostics["MaterialGiReleaseApprovalId"] =
                            "fabricated-approval";
                        diagnostics["MaterialGiReleaseEvidenceSha256"] =
                            new string('a', 64);
                        diagnostics["MaterialGiQualifiedDeviceCount"] = 2;
                    },
                    producer =>
                        string.Equals(
                            producer.DeviceId,
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            producer.QualityTier,
                            "Low",
                            StringComparison.Ordinal));
                break;
            case "soak-telemetry":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                    json =>
                        json["PostWarmupTelemetryCoverageFailureFrameCount"] =
                            1,
                    producer => string.Equals(
                        producer.Kind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        StringComparison.Ordinal));
                break;
            case "soak-wall-clock":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                    json =>
                        json["CompletedUtc"] =
                            "2026-07-28T12:01:00+00:00",
                    producer => string.Equals(
                        producer.Kind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        StringComparison.Ordinal));
                break;
            case "soak-cadence":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                    json => json["ExpectedSampleCount"] = 119,
                    producer => string.Equals(
                        producer.Kind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        StringComparison.Ordinal));
                break;
            case "soak-descriptor":
                artifacts.MutateProducerAndRepin(
                    MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                    json => json["DescriptorPressure"]![
                        "TextureExhaustionSampleCount"] = 1,
                    producer => string.Equals(
                        producer.Kind,
                        MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                        StringComparison.Ordinal));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tamper));
        }

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>());
    }

    [TestCase("buildCommit")]
    [TestCase("settingsFingerprint")]
    [TestCase("gpuName")]
    [TestCase("qualityTier")]
    public void QualificationLoad_RejectsRepinnedEmbeddedProducerIdentity(
        string field)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.MutateProducerAndRepin(
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
            json =>
            {
                JsonObject identity =
                    json["producerIdentity"]!.AsObject();
                identity[field] = field switch
                {
                    "buildCommit" =>
                        "1123456789abcdef0123456789abcdef01234567",
                    "settingsFingerprint" =>
                        "9999999999999999999999999999999999999999999999999999999999999999",
                    "gpuName" => "Repinned synthetic GPU",
                    "qualityTier" => "Ultra",
                    _ => throw new ArgumentOutOfRangeException(
                        nameof(field),
                        field,
                        null)
                };
            });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void QualificationLoad_RejectsGraphicsAsyncSettingsPairSubstitution()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.MutateProducerAndRepin(
            MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole,
            json =>
            {
                JsonArray sources = json["producerIdentity"]![
                    "sourceSettingsFingerprints"]!.AsArray();
                sources[1] = sources[0]!.GetValue<string>();
            });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void EvidenceAssembler_RefusesProducerOpenForConcurrentMutation()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-pin-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string producerPath = Path.Combine(directory, "producer.json");
        File.WriteAllText(producerPath, """{"Kind":"test"}""");
        try
        {
            using var mutationLease = new FileStream(
                producerPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
            Assert.That(
                () => MaterialGiReleaseEvidenceAssembler.PinProducer(
                    directory,
                    producerPath,
                    MaterialGiReleaseEvidenceContract.TestMatrixProducerKind,
                    MaterialGiReleaseEvidenceContract.TestMatrixProducerSchema,
                    new MaterialGiEvidenceDeviceIdentity
                    {
                        DeviceId =
                            SyntheticMaterialGiQualification.AlphaDeviceName,
                        GpuName =
                            SyntheticMaterialGiQualification.AlphaGpuName,
                        DriverVersion =
                            SyntheticMaterialGiQualification.AlphaDriverVersion
                    },
                    SyntheticMaterialGiQualification.BuildCommit,
                    SyntheticMaterialGiQualification.ShaderFingerprint,
                    SyntheticMaterialGiQualification.SettingsFingerprint),
                Throws.TypeOf<IOException>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void EvidenceAssembler_PreflightFailurePreservesPublishedRoleReport()
    {
        string directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-atomic-role-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "role.json");
        const string sentinel = "previous-authenticated-report";
        File.WriteAllText(reportPath, sentinel);
        try
        {
            MaterialGiEvidenceDeviceIdentity device = new()
            {
                DeviceId = SyntheticMaterialGiQualification.AlphaDeviceName,
                GpuName = SyntheticMaterialGiQualification.AlphaGpuName,
                DriverVersion =
                    SyntheticMaterialGiQualification.AlphaDriverVersion
            };
            var invalid = new MaterialGiReleaseEvidenceReport
            {
                Role = MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
                Status = MaterialGiReleaseEvidenceContract.PassedStatus,
                BuildCommit = SyntheticMaterialGiQualification.BuildCommit,
                ShaderFingerprint =
                    SyntheticMaterialGiQualification.ShaderFingerprint,
                SettingsContractFingerprint =
                    SyntheticMaterialGiQualification.SettingsFingerprint,
                DeviceIds = [device.DeviceId],
                Devices = [device],
                Producers = []
            };

            Assert.That(
                () => MaterialGiReleaseEvidenceAssembler.WriteRoleReport(
                    directory,
                    reportPath,
                    invalid),
                Throws.TypeOf<InvalidDataException>());
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(reportPath), Is.EqualTo(sentinel));
                Assert.That(
                    Directory.EnumerateFiles(directory, "*.tmp").ToArray(),
                    Is.Empty);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void QualificationLoad_RejectsMissingReleaseEvidenceHashes()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiRolloutQualificationManifest originalManifest =
            artifacts.Manifest;

        artifacts.WriteManifest(originalManifest with
        {
            ReleaseEvidenceBundleSha256 = string.Empty
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("release evidence bundle SHA-256"));

        artifacts.WriteManifest(originalManifest with
        {
            EvidenceSha256 = string.Empty
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("recomputed SHA-256"));

        artifacts.WriteManifest(originalManifest);
        artifacts.WriteBundleAndRepin(ReplaceReleaseEvidence(
            artifacts.Bundle,
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
            artifact => artifact with { Sha256 = string.Empty }));
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("release evidence role")
                .And.Message.Contains("SHA-256"));
    }

    [Test]
    public void QualificationLoad_RejectsUnknownBundleOrArtifactJsonMetadata()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        JsonObject json = JsonNode.Parse(
                File.ReadAllText(artifacts.BundlePath))!
            .AsObject();
        json["UnreviewedEvidenceClaim"] = true;
        File.WriteAllText(artifacts.BundlePath, json.ToJsonString());
        artifacts.WriteManifest(artifacts.Manifest with
        {
            ReleaseEvidenceBundleSha256 =
                SyntheticMaterialGiQualification.ComputeSha256(
                    artifacts.BundlePath)
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("unknown JSON metadata"));

        artifacts.WriteBundleAndRepin(artifacts.Bundle);
        const string role =
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
        string artifactPath = artifacts.GetReleaseEvidencePath(role);
        JsonObject artifactJson = JsonNode.Parse(
                File.ReadAllText(artifactPath))!
            .AsObject();
        artifactJson["UnreviewedArtifactClaim"] = true;
        File.WriteAllText(artifactPath, artifactJson.ToJsonString());
        artifacts.WriteReleaseEvidenceAndRepin(
            role,
            File.ReadAllBytes(artifactPath));
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("unknown JSON metadata"));
    }

    [Test]
    public void QualificationLoad_RejectsReleaseEvidenceAggregateMismatch()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiReleaseEvidenceArtifact replacementIdentity =
            artifacts.GetReleaseEvidence(
                MaterialGiReleaseEvidenceContract.ApprovedHdrRole);
        MaterialGiReleaseEvidenceBundle differentClaims =
            ReplaceReleaseEvidence(
                artifacts.Bundle,
                MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole,
                artifact => artifact with
                {
                    ByteLength = replacementIdentity.ByteLength,
                    Sha256 = replacementIdentity.Sha256
                });
        artifacts.WriteManifest(artifacts.Manifest with
        {
            EvidenceSha256 =
                MaterialGiReleaseEvidenceContract.ComputeAggregateSha256(
                    differentClaims)
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("recomputed SHA-256"));
    }

    [Test]
    public void QualificationLoad_RejectsUnqualifiedEvidenceDevices()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string approvedHdrRole =
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole;
        artifacts.WriteReleaseEvidenceReportAndRepin(
            approvedHdrRole,
            artifacts.GetReleaseEvidenceReport(approvedHdrRole) with
            {
                DeviceIds = ["Unqualified synthetic device"]
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("not represented"));

    }

    [Test]
    public void QualificationLoad_RejectsIncompleteRoleSpecificEvidence()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string tierRole =
            MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole;
        MaterialGiReleaseEvidenceReport originalTier =
            artifacts.GetReleaseEvidenceReport(tierRole);
        artifacts.WriteReleaseEvidenceReportAndRepin(
            tierRole,
            originalTier with
            {
                QualityTiers = ["Low", "Medium", "High"]
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("quality tiers"));

        artifacts.WriteReleaseEvidenceReportAndRepin(tierRole, originalTier);
        const string soakRole =
            MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole;
        MaterialGiReleaseEvidenceReport originalSoak =
            artifacts.GetReleaseEvidenceReport(soakRole);
        artifacts.WriteReleaseEvidenceReportAndRepin(
            soakRole,
            originalSoak with
            {
                DurationSeconds = null
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("1800 seconds"));

        artifacts.WriteReleaseEvidenceReportAndRepin(
            soakRole,
            originalSoak with
            {
                DurationSeconds =
                    MaterialGiReleaseEvidenceContract
                        .MinimumSoakDurationSeconds - 1
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("1800 seconds"));

        artifacts.WriteReleaseEvidenceReportAndRepin(soakRole, originalSoak);
        const string matrixRole =
            MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole;
        MaterialGiReleaseEvidenceReport originalMatrix =
            artifacts.GetReleaseEvidenceReport(matrixRole);
        artifacts.WriteReleaseEvidenceReportAndRepin(
            matrixRole,
            originalMatrix with
            {
                CoveredChecks =
                [
                    "CpuOracle",
                    "GpuOracle",
                    "ReleaseBuild"
                ]
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("covered checks"));

        artifacts.WriteReleaseEvidenceReportAndRepin(matrixRole, originalMatrix);
        const string validationRole =
            MaterialGiReleaseEvidenceContract.CleanValidationRole;
        MaterialGiReleaseEvidenceReport originalValidation =
            artifacts.GetReleaseEvidenceReport(validationRole);
        artifacts.WriteReleaseEvidenceReportAndRepin(
            validationRole,
            originalValidation with
            {
                ValidationWarningCount = 1
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("zero warnings and errors"));
    }

    [TestCase(MaterialGiReleaseEvidenceContract.ApprovedHdrRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.CleanValidationRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.LifecycleResilienceRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole)]
    [TestCase(MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole)]
    public void QualificationLoad_RequiresExactRuntimeRoleCoveredChecks(
        string role)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiReleaseEvidenceReport original =
            artifacts.GetReleaseEvidenceReport(role);

        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with
            {
                CoveredChecks = [.. original.CoveredChecks.Skip(1)]
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("exactly the required covered checks"));

        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with
            {
                CoveredChecks =
                [
                    .. original.CoveredChecks,
                    "unreviewed-release-claim"
                ]
            });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("exactly the required covered checks"));
    }

    [Test]
    public void QualificationLoad_TierEvidenceCoversEveryQualifiedDeviceAndTier()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole;
        MaterialGiReleaseEvidenceReport original =
            artifacts.GetReleaseEvidenceReport(role);
        MaterialGiRolloutQualificationManifest originalManifest =
            artifacts.Manifest;

        artifacts.WriteManifest(originalManifest with
        {
            QualifiedDeviceIds =
            [
                .. originalManifest.QualifiedDeviceIds,
                "Third qualified device without tier evidence"
            ]
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("GPU/driver identity"));
        artifacts.WriteManifest(originalManifest);

        MaterialGiTierDeviceEvidence[] noRayQuery =
            [.. original.TierDevices];
        noRayQuery[1] = noRayQuery[1] with
        {
            RayQuerySupported = false
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { TierDevices = noRayQuery });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("ray-query capability"));

        MaterialGiTierDeviceEvidence[] missingTier =
            [.. original.TierDevices];
        missingTier[1] = missingTier[1] with
        {
            QualityTiers = ["Low", "Medium", "High"]
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { TierDevices = missingTier });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("quality tiers"));
    }

    [Test]
    public void QualificationLoad_AllowsExactPerTierSettingsUnderOneContract()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        _ = artifacts.Load();
        MaterialGiReleaseEvidenceReport tierReport =
            artifacts.GetReleaseEvidenceReport(
                MaterialGiReleaseEvidenceContract
                    .TierPerformanceMatrixRole);

        Assert.Multiple(() =>
        {
            Assert.That(
                tierReport.SettingsContractFingerprint,
                Is.EqualTo(
                    SyntheticMaterialGiQualification
                        .SettingsFingerprint));
            foreach (IGrouping<string, MaterialGiProducerEvidenceArtifact>
                     device in tierReport.Producers.GroupBy(
                         static producer => producer.DeviceId,
                         StringComparer.Ordinal))
            {
                Assert.That(
                    device.Select(static producer =>
                            producer.SettingsFingerprint)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                    Is.EqualTo(
                        MaterialGiReleaseEvidenceContract
                            .RequiredQualityTiers.Count));
            }
        });
    }

    [Test]
    public void QualificationLoad_TierEvidenceProvesLowerMemoryDeviceClass()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole;
        MaterialGiReleaseEvidenceReport original =
            artifacts.GetReleaseEvidenceReport(role);

        MaterialGiTierDeviceEvidence[] noLowerMemoryClass =
            [.. original.TierDevices];
        noLowerMemoryClass[1] = noLowerMemoryClass[1] with
        {
            DeviceClass =
                MaterialGiReleaseEvidenceContract.ReferenceDeviceClass
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { TierDevices = noLowerMemoryClass });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("lower-memory ray-query device"));

        MaterialGiTierDeviceEvidence[] notActuallyLowerMemory =
            [.. original.TierDevices];
        notActuallyLowerMemory[1] = notActuallyLowerMemory[1] with
        {
            DeviceLocalMemoryBytes =
                original.TierDevices[0].DeviceLocalMemoryBytes
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { TierDevices = notActuallyLowerMemory });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("less device-local memory"));
    }

    [Test]
    public void QualificationLoad_AuthenticatesRecoveryCapabilityPerDevice()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole;
        MaterialGiReleaseEvidenceReport original =
            artifacts.GetReleaseEvidenceReport(role);

        MaterialGiRecoveryDeviceEvidence[] invalidSupported =
            [.. original.RecoveryDevices];
        invalidSupported[0] = invalidSupported[0] with
        {
            Supported = true,
            Attempted = false,
            Status = MaterialGiReleaseEvidenceContract.PassedStatus,
            Reason = string.Empty
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { RecoveryDevices = invalidSupported });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("attempted, Passed recovery"));

        MaterialGiRecoveryDeviceEvidence[] invalidUnsupported =
            [.. original.RecoveryDevices];
        invalidUnsupported[1] = invalidUnsupported[1] with
        {
            Reason = string.Empty
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { RecoveryDevices = invalidUnsupported });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("bounded canonical reason"));

        MaterialGiRecoveryDeviceEvidence[] mixedCapabilities =
            [.. original.RecoveryDevices];
        mixedCapabilities[0] = mixedCapabilities[0] with
        {
            Supported = true,
            Attempted = true,
            Status = MaterialGiReleaseEvidenceContract.PassedStatus,
            Reason = string.Empty
        };
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with { RecoveryDevices = mixedCapabilities });
        artifacts.MutateProducerAndRepin(
            role,
            json =>
            {
                JsonObject operation =
                    json["operations"]!.AsArray()[0]!.AsObject();
                operation["Status"] = "passed";
                operation["Detail"] =
                    "Synthetic deterministic recovery completed.";
            },
            producer => string.Equals(
                producer.DeviceId,
                SyntheticMaterialGiQualification.AlphaDeviceName,
                StringComparison.Ordinal));

        MaterialGiRolloutQualificationManifest authenticated =
            artifacts.Load();
        Assert.That(
            authenticated.AuthenticatedRecoveryCapabilitySummary,
            Is.EqualTo("supported=1,unsupported=1"));
    }

    [Test]
    public void QualificationLoad_RejectsDuplicateReleaseArtifactPaths()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiReleaseEvidenceArtifact first =
            artifacts.Bundle.Artifacts[0];
        MaterialGiReleaseEvidenceArtifact second =
            artifacts.Bundle.Artifacts[1] with
            {
                ManifestRelativePath = first.ManifestRelativePath,
                ByteLength = first.ByteLength,
                Sha256 = first.Sha256
            };
        MaterialGiReleaseEvidenceArtifact[] entries =
            [.. artifacts.Bundle.Artifacts];
        entries[1] = second;
        artifacts.WriteBundleAndRepin(artifacts.Bundle with
        {
            Artifacts = entries
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("path")
                .And.Message.Contains("duplicated"));
    }

    [Test]
    public void QualificationLoad_RejectsReleaseArtifactLengthMismatch()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.WriteBundleAndRepin(ReplaceReleaseEvidence(
            artifacts.Bundle,
            MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole,
            artifact => artifact with
            {
                ByteLength = artifact.ByteLength + 1
            }));

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("pinned byte length"));
    }

    [Test]
    public void QualificationLoad_RejectsRootedAndTraversalArtifactPaths()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        MaterialGiRolloutQualificationManifest original = artifacts.Manifest;

        artifacts.WriteManifest(original with
        {
            ReleaseEvidenceBundleRelativePath = artifacts.BundlePath
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("manifest-relative"));

        artifacts.WriteManifest(original with
        {
            AlphaVisibilityReportRelativePath = artifacts.ReportPath
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("manifest-relative"));

        artifacts.WriteManifest(original with
        {
            AlphaVisibilityEvidenceRelativePath =
                "../" + Path.GetFileName(artifacts.EvidencePath)
        });
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("traversal"));

        artifacts.WriteManifest(original);
        artifacts.WriteBundleAndRepin(ReplaceReleaseEvidence(
            artifacts.Bundle,
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole,
            artifact => artifact with
            {
                ManifestRelativePath = "../approved-hdr.json"
            }));
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("traversal"));
    }

    [TestCase(true, TestName = "QualificationLoad_RejectsMissingReport")]
    [TestCase(false, TestName = "QualificationLoad_RejectsMissingEvidence")]
    public void QualificationLoad_RejectsMissingArtifacts(bool removeReport)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        File.Delete(removeReport ? artifacts.ReportPath : artifacts.EvidencePath);

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<FileNotFoundException>());
    }

    [TestCase(true, TestName = "QualificationLoad_RejectsMissingReleaseBundle")]
    [TestCase(false, TestName = "QualificationLoad_RejectsMissingReleaseArtifact")]
    public void QualificationLoad_RejectsMissingReleaseEvidence(
        bool removeBundle)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        string path = removeBundle
            ? artifacts.BundlePath
            : artifacts.GetReleaseEvidencePath(
                MaterialGiReleaseEvidenceContract.ApprovedHdrRole);
        File.Delete(path);

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<FileNotFoundException>());
    }

    [TestCase(true, TestName = "QualificationLoad_RejectsTamperedReportIdentity")]
    [TestCase(false, TestName = "QualificationLoad_RejectsTamperedEvidenceIdentity")]
    public void QualificationLoad_RejectsTamperedPinnedArtifacts(bool tamperReport)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        string path = tamperReport ? artifacts.ReportPath : artifacts.EvidencePath;
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, [.. bytes, (byte)0]);

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("pinned SHA-256 identity"));
    }

    [TestCase(true, TestName = "QualificationLoad_RejectsTamperedReleaseBundle")]
    [TestCase(false, TestName = "QualificationLoad_RejectsTamperedReleaseArtifact")]
    public void QualificationLoad_RejectsTamperedReleaseEvidence(
        bool tamperBundle)
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        string path = tamperBundle
            ? artifacts.BundlePath
            : artifacts.GetReleaseEvidencePath(
                MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole);
        byte[] bytes = File.ReadAllBytes(path);
        if (tamperBundle)
            File.WriteAllBytes(path, [.. bytes, (byte)0]);
        else
        {
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
        }

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("pinned SHA-256 identity"));
    }

    [Test]
    public void QualificationLoad_RejectsRepinnedEvidenceThatFailsReportAuthentication()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        byte[] bytes = File.ReadAllBytes(artifacts.EvidencePath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(artifacts.EvidencePath, bytes);
        artifacts.WriteManifest(artifacts.Manifest with
        {
            AlphaVisibilityEvidenceSha256 =
                SyntheticMaterialGiQualification.ComputeSha256(
                    artifacts.EvidencePath)
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void QualificationLoad_RejectsFailedOrNonCleanAlphaReports()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        AlphaVisibilityConformanceReport failed =
            AlphaVisibilityConformanceReports.CreateFailed(
                new DateTimeOffset(2026, 7, 28, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 28, 10, 0, 1, TimeSpan.Zero),
                "Synthetic ray-query device was unavailable.");
        artifacts.WriteReportAndRepin(failed);
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("not Passed"));

        artifacts.RestorePassedReport();
        AlphaVisibilityConformanceReport nonClean = artifacts.Report with
        {
            ValidationWarningCount = 1
        };
        artifacts.WriteReportAndRepin(nonClean);
        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("clean Vulkan validation"));
    }

    [Test]
    public void QualificationLoad_RequiresAuthenticatedAlphaDeviceInQualifiedSet()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string releaseOnlyDevice =
            "Synthetic release device without alpha visibility evidence";
        foreach (string role in MaterialGiReleaseEvidenceContract.RequiredRoles)
        {
            MaterialGiReleaseEvidenceReport report =
                artifacts.GetReleaseEvidenceReport(role);
            artifacts.WriteReleaseEvidenceReportAndRepin(
                role,
                report with
                {
                    DeviceIds =
                    [
                        .. report.DeviceIds.Select(deviceId =>
                            string.Equals(
                                deviceId,
                                SyntheticMaterialGiQualification.AlphaDeviceName,
                                StringComparison.Ordinal)
                                ? releaseOnlyDevice
                                : deviceId)
                    ],
                    TierDevices =
                    [
                        .. report.TierDevices.Select(device =>
                            device with
                            {
                                DeviceId = string.Equals(
                                    device.DeviceId,
                                    SyntheticMaterialGiQualification
                                        .AlphaDeviceName,
                                    StringComparison.Ordinal)
                                    ? releaseOnlyDevice
                                    : device.DeviceId
                            })
                    ],
                    RecoveryDevices =
                    [
                        .. report.RecoveryDevices.Select(device =>
                            device with
                            {
                                DeviceId = string.Equals(
                                    device.DeviceId,
                                    SyntheticMaterialGiQualification
                                        .AlphaDeviceName,
                                    StringComparison.Ordinal)
                                    ? releaseOnlyDevice
                                    : device.DeviceId
                            })
                    ]
                });
        }
        artifacts.WriteManifest(artifacts.Manifest with
        {
            QualifiedDeviceIds =
            [
                releaseOnlyDevice,
                SyntheticMaterialGiQualification.SecondDeviceName
            ]
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("not represented in QualifiedDeviceIds"));
    }

    [Test]
    public void QualificationLoad_RejectsLegacySchemaBeforeReleasePreflight()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.WriteManifest(artifacts.Manifest with
        {
            SchemaVersion =
                MaterialGiRolloutQualificationManifest.CurrentSchemaVersion - 1
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("legacy contract")
                .And.Message.Contains("cannot be migrated implicitly"));
    }

    [Test]
    public void QualificationLoad_RejectsLegacyBundleWithoutImplicitMigration()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        artifacts.WriteBundleAndRepin(artifacts.Bundle with
        {
            SchemaVersion =
                MaterialGiReleaseEvidenceContract.PreviousBundleSchemaVersion
        });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("legacy bundle")
                .And.Message.Contains("cannot be migrated implicitly"));
    }

    [Test]
    public void QualificationLoad_RejectsLegacyArtifactWithoutImplicitMigration()
    {
        using SyntheticMaterialGiQualification artifacts =
            SyntheticMaterialGiQualification.Create();
        const string role =
            MaterialGiReleaseEvidenceContract.LifecycleResilienceRole;
        MaterialGiReleaseEvidenceReport original =
            artifacts.GetReleaseEvidenceReport(role);
        artifacts.WriteReleaseEvidenceReportAndRepin(
            role,
            original with
            {
                SchemaVersion =
                    MaterialGiReleaseEvidenceContract
                        .PreviousArtifactSchemaVersion
            });

        Assert.That(
            artifacts.Load,
            Throws.TypeOf<InvalidDataException>()
                .With.Message.Contains("legacy artifact")
                .And.Message.Contains("cannot be migrated implicitly"));
    }

    [Test]
    public void VersionFourSettings_CannotImplicitlyPromoteV2()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-v4-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Version": 4,
                  "GlobalIllumination": {
                    "GiMaterialTransportV2": true,
                    "GiEmissiveMeshSampling": true,
                    "GiFarFieldMaterialV2": true,
                    "GiHybridCompositionV2": true
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);

            Assert.That(
                loaded.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.None));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void VersionFiveSettings_CannotImplicitlyPromoteV2()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-v5-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(
                path,
                """
                {
                  "Version": 5,
                  "GlobalIllumination": {
                    "GiMaterialTransportV2": true,
                    "GiEmissiveMeshSampling": true,
                    "GiFarFieldMaterialV2": true,
                    "GiHybridCompositionV2": true
                  }
                }
                """);

            RenderSettings loaded = RenderSettings.Load(path);
            MaterialGiRolloutEvaluation evaluation =
                loaded.GlobalIllumination
                    .EvaluateMaterialGiRollout(QualificationDate);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination
                        .ConfiguredMaterialGiV2Features,
                    Is.EqualTo(MaterialGiV2Feature.None));
                Assert.That(
                    loaded.GlobalIllumination.ActiveMaterialGiV2Features,
                    Is.EqualTo(MaterialGiV2Feature.None));
                Assert.That(
                    evaluation.Mode,
                    Is.EqualTo(MaterialGiRolloutMode.LegacyUnqualified));
                Assert.That(
                    evaluation.ReleaseQualificationRequired,
                    Is.False);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Test]
    public void CopiedV2Booleans_CannotCopyRolloutAuthority()
    {
        var source = new RenderSettings();
        source.GlobalIllumination
            .EnableMaterialGiV2ForQualificationCandidate();
        var copy = new RenderSettings();
        copy.GlobalIllumination.GiMaterialTransportV2 =
            source.GlobalIllumination.GiMaterialTransportV2;
        copy.GlobalIllumination.GiEmissiveMeshSampling =
            source.GlobalIllumination.GiEmissiveMeshSampling;
        copy.GlobalIllumination.GiFarFieldMaterialV2 =
            source.GlobalIllumination.GiFarFieldMaterialV2;
        copy.GlobalIllumination.GiHybridCompositionV2 =
            source.GlobalIllumination.GiHybridCompositionV2;

        Assert.Multiple(() =>
        {
            Assert.That(
                source.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(
                copy.GlobalIllumination.ConfiguredMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.All));
            Assert.That(
                copy.GlobalIllumination.ActiveMaterialGiV2Features,
                Is.EqualTo(MaterialGiV2Feature.None));
            Assert.That(
                copy.GlobalIllumination.EffectiveGiMaterialTransportV2,
                Is.False);
        });
    }

    [Test]
    public void CurrentSettings_CannotPersistQualificationCandidateOrFeatureIntent()
    {
        string path = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"material-gi-candidate-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new RenderSettings();
            settings.GlobalIllumination
                .EnableMaterialGiV2ForQualificationCandidate();
            settings.Save(path);

            RenderSettings loaded = RenderSettings.Load(path);
            MaterialGiRolloutEvaluation evaluation =
                loaded.GlobalIllumination
                    .EvaluateMaterialGiRollout(QualificationDate);

            Assert.Multiple(() =>
            {
                Assert.That(
                    loaded.GlobalIllumination.ActiveMaterialGiV2Features,
                    Is.EqualTo(MaterialGiV2Feature.None));
                Assert.That(
                    loaded.GlobalIllumination
                        .ConfiguredMaterialGiV2Features,
                    Is.EqualTo(MaterialGiV2Feature.None));
                Assert.That(
                    evaluation.Mode,
                    Is.EqualTo(MaterialGiRolloutMode.LegacyUnqualified));
                Assert.That(evaluation.ReleaseQualificationRequired, Is.False);
                Assert.That(evaluation.ReleaseQualified, Is.False);
                Assert.That(
                    evaluation.QualificationFailureCount,
                    Is.Zero);
            });
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static MaterialGiReleaseEvidenceBundle ReplaceReleaseEvidence(
        MaterialGiReleaseEvidenceBundle bundle,
        string role,
        Func<
            MaterialGiReleaseEvidenceArtifact,
            MaterialGiReleaseEvidenceArtifact> update)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(update);
        bool replaced = false;
        MaterialGiReleaseEvidenceArtifact[] artifacts =
        [
            .. bundle.Artifacts.Select(artifact =>
            {
                if (!string.Equals(
                        artifact.Role,
                        role,
                        StringComparison.Ordinal))
                {
                    return artifact;
                }
                replaced = true;
                return update(artifact);
            })
        ];
        Assert.That(
            replaced,
            Is.True,
            $"Synthetic bundle did not contain role '{role}'.");
        return bundle with { Artifacts = artifacts };
    }

    private static byte[] AddDuplicateRootProperty(
        string path,
        string propertyName)
    {
        string json = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonNode value = root[propertyName] ??
            throw new InvalidDataException(
                $"Synthetic JSON has no root property '{propertyName}'.");
        int openingBrace = json.IndexOf('{', StringComparison.Ordinal);
        if (openingBrace < 0)
            throw new InvalidDataException("Synthetic JSON has no root object.");
        string duplicate =
            $"{Environment.NewLine}  \"{propertyName}\": {value.ToJsonString()},";
        return System.Text.Encoding.UTF8.GetBytes(
            json.Insert(openingBrace + 1, duplicate));
    }

    private static byte[] AddDuplicateNestedProperty(
        string path,
        string objectPropertyName,
        string duplicatePropertyName)
    {
        string json = File.ReadAllText(path);
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        JsonObject nested = root[objectPropertyName]?.AsObject() ??
            throw new InvalidDataException(
                $"Synthetic JSON has no object '{objectPropertyName}'.");
        JsonNode value = nested[duplicatePropertyName] ??
            throw new InvalidDataException(
                $"Synthetic JSON object '{objectPropertyName}' has no " +
                $"property '{duplicatePropertyName}'.");
        string objectMarker = $"\"{objectPropertyName}\"";
        int objectProperty = json.IndexOf(
            objectMarker,
            StringComparison.Ordinal);
        int colon = objectProperty < 0
            ? -1
            : json.IndexOf(':', objectProperty + objectMarker.Length);
        int openingBrace = colon < 0
            ? -1
            : json.IndexOf('{', colon + 1);
        if (openingBrace < 0)
        {
            throw new InvalidDataException(
                $"Synthetic JSON object '{objectPropertyName}' has no body.");
        }
        string duplicate =
            $"{Environment.NewLine}    \"{duplicatePropertyName}\": " +
            $"{value.ToJsonString()},";
        return System.Text.Encoding.UTF8.GetBytes(
            json.Insert(openingBrace + 1, duplicate));
    }

}

internal sealed class SyntheticMaterialGiQualification : IDisposable
{
    public const string AlphaDeviceName =
        "Deterministic Synthetic Alpha Qualification Device";
    public const string SecondDeviceName =
        "Second Deterministic Qualification Device";
    public const string BuildCommit =
        "0123456789abcdef0123456789abcdef01234567";
    public const string ShaderFingerprint =
        "1111111111111111111111111111111111111111111111111111111111111111";
    public const string SettingsFingerprint =
        "2222222222222222222222222222222222222222222222222222222222222222";
    private const string GraphicsSettingsFingerprint =
        "3333333333333333333333333333333333333333333333333333333333333333";
    private const string AsyncSettingsFingerprint =
        "4444444444444444444444444444444444444444444444444444444444444444";
    public const string AlphaGpuName = "Synthetic Reference GPU";
    public const string AlphaDriverVersion = "555.10-test";
    public const string SecondGpuName = "Synthetic Lower-Memory GPU";
    public const string SecondDriverVersion = "24.7-test";

    private readonly byte[] _passedEvidence;
    private readonly AlphaVisibilityConformanceReport _passedReport;

    private SyntheticMaterialGiQualification(
        string directoryPath,
        string manifestPath,
        string bundlePath,
        string reportPath,
        string evidencePath,
        byte[] passedEvidence,
        AlphaVisibilityConformanceReport passedReport,
        MaterialGiReleaseEvidenceBundle bundle,
        MaterialGiRolloutQualificationManifest manifest)
    {
        DirectoryPath = directoryPath;
        ManifestPath = manifestPath;
        BundlePath = bundlePath;
        ReportPath = reportPath;
        EvidencePath = evidencePath;
        _passedEvidence = passedEvidence;
        _passedReport = passedReport;
        Report = passedReport;
        Bundle = bundle;
        Manifest = manifest;
    }

    public string DirectoryPath { get; }

    public string ManifestPath { get; }

    public string BundlePath { get; }

    public string ReportPath { get; }

    public string EvidencePath { get; }

    public AlphaVisibilityConformanceReport Report { get; private set; }

    public MaterialGiReleaseEvidenceBundle Bundle { get; private set; }

    public MaterialGiRolloutQualificationManifest Manifest { get; private set; }

    public static SyntheticMaterialGiQualification Create()
    {
        string directoryPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "material-gi-rollout-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        try
        {
            string manifestPath = Path.Combine(
                directoryPath,
                "qualification.json");
            string bundlePath = Path.Combine(
                directoryPath,
                "release-evidence-bundle.json");
            string reportPath = Path.Combine(
                directoryPath,
                "alpha-visibility.json");
            string evidencePath = Path.Combine(
                directoryPath,
                "alpha-visibility.bin");
            AlphaVisibilityRawEvidence raw = CreatePassingEvidence();
            byte[] evidence = AlphaVisibilityEvidenceCodec.Encode(raw);
            AlphaVisibilityConformanceReport report =
                AlphaVisibilityConformanceReports.Create(
                    new DateTimeOffset(
                        2026,
                        7,
                        28,
                        10,
                        0,
                        0,
                        TimeSpan.Zero),
                    new DateTimeOffset(
                        2026,
                        7,
                        28,
                        10,
                        0,
                        1,
                        TimeSpan.Zero),
                    CreateHardware(raw),
                    Path.GetFileName(evidencePath),
                    evidence);
            AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
                evidencePath,
                evidence);
            AlphaVisibilityConformanceReports.WriteAtomically(
                reportPath,
                report);
            MaterialGiReleaseEvidenceBundle bundle =
                CreateReleaseEvidenceBundle(directoryPath);
            MaterialGiReleaseEvidenceAssembler.WriteBundle(
                directoryPath,
                bundlePath,
                bundle);
            var manifest = new MaterialGiRolloutQualificationManifest
            {
                EnabledFeatures = MaterialGiV2Feature.All,
                QualifiedDeviceIds =
                [
                    AlphaDeviceName,
                    SecondDeviceName
                ],
                ReleaseEvidenceBundleRelativePath =
                    GetManifestRelativePath(directoryPath, bundlePath),
                ReleaseEvidenceBundleSha256 = ComputeSha256(bundlePath),
                EvidenceSha256 =
                    MaterialGiReleaseEvidenceContract.ComputeAggregateSha256(
                        bundle),
                ApprovalId = "material-gi-release-2026-07",
                ApprovedAtUtc = new DateTimeOffset(
                    2026,
                    7,
                    28,
                    12,
                    0,
                    0,
                    TimeSpan.Zero),
                AlphaVisibilityReportRelativePath =
                    GetManifestRelativePath(directoryPath, reportPath),
                AlphaVisibilityReportSha256 = ComputeSha256(reportPath),
                AlphaVisibilityEvidenceRelativePath =
                    GetManifestRelativePath(directoryPath, evidencePath),
                AlphaVisibilityEvidenceSha256 = ComputeSha256(evidencePath)
            };
            var fixture = new SyntheticMaterialGiQualification(
                directoryPath,
                manifestPath,
                bundlePath,
                reportPath,
                evidencePath,
                evidence,
                report,
                bundle,
                manifest);
            fixture.WriteManifest(manifest);
            return fixture;
        }
        catch
        {
            Directory.Delete(directoryPath, recursive: true);
            throw;
        }
    }

    public MaterialGiRolloutQualificationManifest Load() =>
        MaterialGiRolloutQualificationManifest.Load(ManifestPath);

    public void WriteManifest(
        MaterialGiRolloutQualificationManifest manifest)
    {
        Manifest = manifest;
        WriteJson(ManifestPath, manifest);
    }

    public MaterialGiReleaseEvidenceArtifact GetReleaseEvidence(
        string role) =>
        Bundle.Artifacts.Single(artifact =>
            string.Equals(artifact.Role, role, StringComparison.Ordinal));

    public string GetReleaseEvidencePath(string role)
    {
        string relativePath = GetReleaseEvidence(role).ManifestRelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(DirectoryPath, relativePath);
    }

    public MaterialGiReleaseEvidenceReport GetReleaseEvidenceReport(
        string role) =>
        JsonSerializer.Deserialize<MaterialGiReleaseEvidenceReport>(
            File.ReadAllBytes(GetReleaseEvidencePath(role)))!;

    public string GetProducerPath(
        string role,
        Func<MaterialGiProducerEvidenceArtifact, bool>? predicate = null)
    {
        MaterialGiProducerEvidenceArtifact producer =
            GetReleaseEvidenceReport(role).Producers.Single(
                predicate ?? (_ => true));
        return Path.Combine(
            DirectoryPath,
            producer.ManifestRelativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar));
    }

    public void MutateProducerAndRepin(
        string role,
        Action<JsonObject> mutate,
        Func<MaterialGiProducerEvidenceArtifact, bool>? predicate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(mutate);
        MaterialGiReleaseEvidenceReport report =
            GetReleaseEvidenceReport(role);
        int producerIndex = Array.FindIndex(
            report.Producers,
            producer => (predicate ?? (_ => true))(producer));
        Assert.That(producerIndex, Is.GreaterThanOrEqualTo(0));
        MaterialGiProducerEvidenceArtifact producer =
            report.Producers[producerIndex];
        string path = Path.Combine(
            DirectoryPath,
            producer.ManifestRelativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar));
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(json);
        File.WriteAllText(path, json.ToJsonString());
        MaterialGiProducerEvidenceArtifact[] updatedProducers =
            [.. report.Producers];
        updatedProducers[producerIndex] = producer with
        {
            ByteLength = new FileInfo(path).Length,
            Sha256 = ComputeSha256(path)
        };
        WriteReleaseEvidenceReportAndRepin(
            role,
            report with { Producers = updatedProducers });
    }

    public void WriteBundleAndRepin(
        MaterialGiReleaseEvidenceBundle bundle)
    {
        Bundle = bundle;
        WriteJson(BundlePath, bundle);
        WriteManifest(Manifest with
        {
            ReleaseEvidenceBundleSha256 = ComputeSha256(BundlePath),
            EvidenceSha256 =
                MaterialGiReleaseEvidenceContract.ComputeAggregateSha256(
                    bundle)
        });
    }

    public void WriteReleaseEvidenceAndRepin(
        string role,
        byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string path = GetReleaseEvidencePath(role);
        File.WriteAllBytes(path, bytes);
        MaterialGiReleaseEvidenceArtifact updated =
            GetReleaseEvidence(role) with
            {
                ByteLength = bytes.LongLength,
                Sha256 = ComputeSha256(path)
            };
        WriteBundleAndRepin(Bundle with
        {
            Artifacts =
            [
                .. Bundle.Artifacts.Select(artifact =>
                    string.Equals(
                        artifact.Role,
                        role,
                        StringComparison.Ordinal)
                        ? updated
                        : artifact)
            ]
        });
    }

    public void WriteReleaseEvidenceReportAndRepin(
        string role,
        MaterialGiReleaseEvidenceReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(report);
        WriteReleaseEvidenceAndRepin(
            role,
            JsonSerializer.SerializeToUtf8Bytes(
                report,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

    public void WriteReportAndRepin(
        AlphaVisibilityConformanceReport report)
    {
        AlphaVisibilityConformanceReports.WriteAtomically(
            ReportPath,
            report);
        Report = report;
        WriteManifest(Manifest with
        {
            AlphaVisibilityReportSha256 = ComputeSha256(ReportPath)
        });
    }

    public void RestorePassedReport()
    {
        AlphaVisibilityConformanceReports.WriteEvidenceAtomically(
            EvidencePath,
            _passedEvidence);
        AlphaVisibilityConformanceReports.WriteAtomically(
            ReportPath,
            _passedReport);
        Report = _passedReport;
        WriteManifest(Manifest with
        {
            AlphaVisibilityReportSha256 = ComputeSha256(ReportPath),
            AlphaVisibilityEvidenceSha256 = ComputeSha256(EvidencePath)
        });
    }

    public static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, recursive: true);
    }

    private static AlphaVisibilityHardwareOutput CreateHardware(
        AlphaVisibilityRawEvidence raw)
    {
        return new AlphaVisibilityHardwareOutput(
            AlphaDeviceName,
            DeviceApiVersion: 0x0040_3000,
            DriverVersion: 1,
            ValidationEnabled: true,
            ValidationWarningCount: 0,
            ValidationErrorCount: 0,
            FirstValidationError: string.Empty,
            ValidationMessages: Array.Empty<AlphaVisibilityValidationMessage>(),
            ValidationMessagesTruncated: false,
            ResultWords: ToWords(raw));
    }

    private static uint[] ToWords(AlphaVisibilityRawEvidence raw)
    {
        var words =
            new uint[AlphaVisibilityConformanceContract.ResultWordCount];
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        Copy(raw.RasterCandidates, words, 0);
        Copy(raw.RasterCovered, words, samples);
        Copy(raw.RayCandidates, words, samples * 2);
        Copy(raw.RayCovered, words, samples * 3);
        return words;
    }

    private static void Copy(
        ReadOnlySpan<byte> source,
        Span<uint> destination,
        int destinationOffset)
    {
        for (int index = 0; index < source.Length; index++)
            destination[destinationOffset + index] = source[index];
    }

    private static AlphaVisibilityRawEvidence CreatePassingEvidence()
    {
        int samples = AlphaVisibilityConformanceContract.TotalSamples;
        int samplesPerDistance =
            AlphaVisibilityConformanceContract.SamplesPerDistance;
        var rasterCandidates = new byte[samples];
        var rasterCovered = new byte[samples];
        var rayCandidates = new byte[samples];
        var rayCovered = new byte[samples];
        for (int distanceIndex = 0;
             distanceIndex < AlphaVisibilityConformanceContract.Distances.Count;
             distanceIndex++)
        {
            int offset = checked(distanceIndex * samplesPerDistance);
            rasterCandidates.AsSpan(offset, 10_000).Fill(1);
            rasterCovered.AsSpan(offset, 5_000).Fill(1);
            rayCandidates.AsSpan(offset, 10_000).Fill(1);
            rayCovered.AsSpan(offset, 5_100).Fill(1);
        }
        return AlphaVisibilityRawEvidence.CreateValidated(
            rasterCandidates,
            rasterCovered,
            rayCandidates,
            rayCovered);
    }

    private static MaterialGiReleaseEvidenceBundle CreateReleaseEvidenceBundle(
        string directoryPath)
    {
        string evidenceDirectory = Path.Combine(
            directoryPath,
            "release-evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var artifacts = new List<MaterialGiReleaseEvidenceArtifact>();
        for (int index = 0;
             index < MaterialGiReleaseEvidenceContract.RequiredRoles.Count;
             index++)
        {
            string role =
                MaterialGiReleaseEvidenceContract.RequiredRoles[index];
            bool requiresEveryQualifiedDevice =
                string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    StringComparison.Ordinal) ||
                string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole,
                    StringComparison.Ordinal);
            string[] deviceIds = requiresEveryQualifiedDevice
                ? [AlphaDeviceName, SecondDeviceName]
                : index % 2 == 0
                    ? [AlphaDeviceName]
                    : [SecondDeviceName];
            string artifactPath = Path.Combine(
                evidenceDirectory,
                role + ".json");
            var report = new MaterialGiReleaseEvidenceReport
            {
                Role = role,
                Status = MaterialGiReleaseEvidenceContract.PassedStatus,
                BuildCommit = BuildCommit,
                ShaderFingerprint = ShaderFingerprint,
                SettingsContractFingerprint = SettingsFingerprint,
                DeviceIds = deviceIds,
                Devices =
                [
                    .. deviceIds.Select(GetDeviceIdentity)
                ],
                Producers = CreateProducerArtifacts(
                    directoryPath,
                    role,
                    deviceIds),
                DurationSeconds = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole,
                    StringComparison.Ordinal)
                    ? MaterialGiReleaseEvidenceContract
                        .MinimumSoakDurationSeconds
                    : null,
                QualityTiers = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract
                        .TierPerformanceMatrixRole,
                    StringComparison.Ordinal)
                    ?
                    [
                        .. MaterialGiReleaseEvidenceContract
                            .RequiredQualityTiers
                    ]
                    : [],
                CoveredChecks =
                [
                    .. MaterialGiReleaseEvidenceContract
                        .GetRequiredCoveredChecks(role)
                ],
                TierDevices = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole,
                    StringComparison.Ordinal)
                    ?
                    [
                        new MaterialGiTierDeviceEvidence
                        {
                            DeviceId = AlphaDeviceName,
                            DeviceClass =
                                MaterialGiReleaseEvidenceContract
                                    .ReferenceDeviceClass,
                            DeviceLocalMemoryBytes = 8L * 1024 * 1024 * 1024,
                            RayQuerySupported = true,
                            QualityTiers =
                            [
                                .. MaterialGiReleaseEvidenceContract
                                    .RequiredQualityTiers
                            ]
                        },
                        new MaterialGiTierDeviceEvidence
                        {
                            DeviceId = SecondDeviceName,
                            DeviceClass =
                                MaterialGiReleaseEvidenceContract
                                    .LowerMemoryRayQueryDeviceClass,
                            DeviceLocalMemoryBytes = 4L * 1024 * 1024 * 1024,
                            RayQuerySupported = true,
                            QualityTiers =
                            [
                                .. MaterialGiReleaseEvidenceContract
                                    .RequiredQualityTiers
                            ]
                        }
                    ]
                    : [],
                RecoveryDevices = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole,
                    StringComparison.Ordinal)
                    ?
                    [
                        new MaterialGiRecoveryDeviceEvidence
                        {
                            DeviceId = AlphaDeviceName,
                            Supported = false,
                            Attempted = false,
                            Status =
                                MaterialGiReleaseEvidenceContract
                                    .UnsupportedStatus,
                            Reason =
                                "The synthetic renderer exposes no safe deterministic device-loss injection."
                        },
                        new MaterialGiRecoveryDeviceEvidence
                        {
                            DeviceId = SecondDeviceName,
                            Supported = false,
                            Attempted = false,
                            Status =
                                MaterialGiReleaseEvidenceContract
                                    .UnsupportedStatus,
                            Reason =
                                "The synthetic renderer exposes no safe deterministic device-loss injection."
                        }
                    ]
                    : [],
                ValidationEnabled = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.CleanValidationRole,
                    StringComparison.Ordinal)
                    ? true
                    : null,
                ValidationWarningCount = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.CleanValidationRole,
                    StringComparison.Ordinal)
                    ? 0
                    : null,
                ValidationErrorCount = string.Equals(
                    role,
                    MaterialGiReleaseEvidenceContract.CleanValidationRole,
                    StringComparison.Ordinal)
                    ? 0
                    : null,
                Summary = CreateSyntheticMeasurement(role)
            };
            WriteJson(artifactPath, report);
            artifacts.Add(
                new MaterialGiReleaseEvidenceArtifact
                {
                    Role = role,
                    ManifestRelativePath = GetManifestRelativePath(
                        directoryPath,
                        artifactPath),
                    ByteLength = new FileInfo(artifactPath).Length,
                    Sha256 = ComputeSha256(artifactPath)
                });
        }
        return new MaterialGiReleaseEvidenceBundle
        {
            BuildCommit = BuildCommit,
            ShaderFingerprint = ShaderFingerprint,
            SettingsContractFingerprint = SettingsFingerprint,
            Devices =
            [
                GetDeviceIdentity(AlphaDeviceName),
                GetDeviceIdentity(SecondDeviceName)
            ],
            Artifacts = [.. artifacts]
        };
    }

    private static MaterialGiProducerEvidenceArtifact[]
        CreateProducerArtifacts(
            string directoryPath,
            string role,
            IReadOnlyList<string> deviceIds)
    {
        var producers = new List<MaterialGiProducerEvidenceArtifact>();
        switch (role)
        {
            case MaterialGiReleaseEvidenceContract.ApprovedHdrRole:
                producers.Add(
                    WriteAndPinProducer(
                        directoryPath,
                        role,
                        deviceIds[0],
                        MaterialGiReleaseEvidenceContract.ApprovedHdrProducerKind,
                        MaterialGiReleaseEvidenceContract.ApprovedHdrProducerSchema,
                        string.Empty,
                        CreateApprovedHdrProducer()));
                break;
            case MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole:
                producers.Add(
                    WriteAndPinProducer(
                        directoryPath,
                        role,
                        deviceIds[0],
                        MaterialGiReleaseEvidenceContract.KhronosRenderedProducerKind,
                        MaterialGiReleaseEvidenceContract.KhronosRenderedProducerSchema,
                        string.Empty,
                        CreateKhronosRenderedProducer(deviceIds[0])));
                break;
            case MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole:
                producers.Add(
                    WriteAndPinProducer(
                        directoryPath,
                        role,
                        deviceIds[0],
                        MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerKind,
                        MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerSchema,
                        string.Empty,
                        CreateGraphicsAsyncProducer()));
                break;
            case MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole:
                foreach (string deviceId in deviceIds)
                {
                    producers.Add(
                        WriteAndPinProducer(
                            directoryPath,
                            role,
                            deviceId,
                            MaterialGiReleaseEvidenceContract.TestMatrixProducerKind,
                            MaterialGiReleaseEvidenceContract.TestMatrixProducerSchema,
                            string.Empty,
                            CreateTestMatrixProducer(deviceId)));
                }
                break;
            case MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole:
                foreach (string deviceId in deviceIds)
                {
                    foreach (string tier in
                             MaterialGiReleaseEvidenceContract.RequiredQualityTiers)
                    {
                        producers.Add(
                            WriteAndPinProducer(
                                directoryPath,
                                role,
                                deviceId,
                                MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
                                MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema,
                                tier,
                                CreateBenchmarkProducer(deviceId, tier)));
                    }
                }
                break;
            case MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole:
                foreach (string deviceId in deviceIds)
                {
                    producers.Add(
                        WriteAndPinProducer(
                            directoryPath,
                            role,
                            deviceId,
                            MaterialGiReleaseEvidenceContract.LongRunProducerKind,
                            MaterialGiReleaseEvidenceContract.LongRunProducerSchema,
                            string.Empty,
                            CreateLongRunProducer()));
                    producers.Add(
                        WriteAndPinProducer(
                            directoryPath,
                            role,
                            deviceId,
                            MaterialGiReleaseEvidenceContract.HealthProducerKind,
                            MaterialGiReleaseEvidenceContract.HealthProducerSchema,
                            string.Empty,
                            CreateHealthProducer(role, deviceId)));
                }
                break;
            case MaterialGiReleaseEvidenceContract.CleanValidationRole:
            case MaterialGiReleaseEvidenceContract.LifecycleResilienceRole:
            case MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole:
            case MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole:
            case MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole:
                foreach (string deviceId in deviceIds)
                {
                    producers.Add(
                        WriteAndPinProducer(
                            directoryPath,
                            role,
                            deviceId,
                            MaterialGiReleaseEvidenceContract.HealthProducerKind,
                            MaterialGiReleaseEvidenceContract.HealthProducerSchema,
                            string.Empty,
                            CreateHealthProducer(role, deviceId)));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
        return [.. producers];
    }

    private static MaterialGiProducerEvidenceArtifact WriteAndPinProducer(
        string directoryPath,
        string role,
        string deviceId,
        string kind,
        string schema,
        string qualityTier,
        object producerReport)
    {
        string producerDirectory = Path.Combine(
            directoryPath,
            "release-evidence",
            "producers");
        Directory.CreateDirectory(producerDirectory);
        int deviceIndex = string.Equals(
            deviceId,
            AlphaDeviceName,
            StringComparison.Ordinal)
            ? 0
            : 1;
        string suffix = qualityTier.Length == 0
            ? string.Empty
            : "-" + qualityTier.ToLowerInvariant();
        string path = Path.Combine(
            producerDirectory,
            $"{role}-{deviceIndex}-{kind}{suffix}.json");
        string[] sourceSettingsFingerprints;
        string producerSettingsFingerprint;
        if (string.Equals(
                kind,
                MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerKind,
                StringComparison.Ordinal))
        {
            sourceSettingsFingerprints =
            [
                GraphicsSettingsFingerprint,
                AsyncSettingsFingerprint
            ];
            producerSettingsFingerprint =
                MaterialGiProducerSettingsFingerprint.ComputeGraphicsAsyncPair(
                    GraphicsSettingsFingerprint,
                    AsyncSettingsFingerprint);
        }
        else
        {
            producerSettingsFingerprint =
                string.Equals(
                    kind,
                    MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
                    StringComparison.Ordinal)
                    ? GetTierSettingsFingerprint(qualityTier)
                    : SettingsFingerprint;
            sourceSettingsFingerprints = [producerSettingsFingerprint];
        }

        JsonObject payload =
            JsonSerializer.SerializeToNode(producerReport)!.AsObject();
        if (!string.Equals(
                kind,
                MaterialGiReleaseEvidenceContract.TestMatrixProducerKind,
                StringComparison.Ordinal))
        {
            MaterialGiEvidenceDeviceIdentity device =
                GetDeviceIdentity(deviceId);
            payload["producerIdentity"] =
                JsonSerializer.SerializeToNode(
                    new MaterialGiProducerIdentity
                    {
                        BuildCommit = BuildCommit,
                        ShaderFingerprint = ShaderFingerprint,
                        SettingsFingerprint =
                            producerSettingsFingerprint,
                        SourceSettingsFingerprints =
                            sourceSettingsFingerprints,
                        GpuName = device.GpuName,
                        DriverVersion = device.DriverVersion,
                        QualityTier = qualityTier
                    });
        }
        WriteJson(path, payload);
        return MaterialGiReleaseEvidenceAssembler.PinProducer(
            directoryPath,
            path,
            kind,
            schema,
            GetDeviceIdentity(deviceId),
            BuildCommit,
            ShaderFingerprint,
            producerSettingsFingerprint,
            qualityTier);
    }

    private static string GetTierSettingsFingerprint(
        string qualityTier) => qualityTier switch
        {
            "Low" =>
                "5555555555555555555555555555555555555555555555555555555555555555",
            "Medium" =>
                "6666666666666666666666666666666666666666666666666666666666666666",
            "High" =>
                "7777777777777777777777777777777777777777777777777777777777777777",
            "Ultra" =>
                "8888888888888888888888888888888888888888888888888888888888888888",
            _ => throw new ArgumentOutOfRangeException(
                nameof(qualityTier),
                qualityTier,
                "A frozen synthetic tier is required.")
        };

    private static MaterialGiEvidenceDeviceIdentity GetDeviceIdentity(
        string deviceId) =>
        string.Equals(deviceId, AlphaDeviceName, StringComparison.Ordinal)
            ? new MaterialGiEvidenceDeviceIdentity
            {
                DeviceId = AlphaDeviceName,
                GpuName = AlphaGpuName,
                DriverVersion = AlphaDriverVersion
            }
            : string.Equals(
                deviceId,
                SecondDeviceName,
                StringComparison.Ordinal)
                ? new MaterialGiEvidenceDeviceIdentity
                {
                    DeviceId = SecondDeviceName,
                    GpuName = SecondGpuName,
                    DriverVersion = SecondDriverVersion
                }
                : throw new ArgumentOutOfRangeException(
                    nameof(deviceId),
                    deviceId,
                    "Unknown synthetic device.");

    private static object CreateApprovedHdrProducer()
    {
        const string hash =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        return new
        {
            schemaVersion =
                MaterialGiReleaseEvidenceContract.ApprovedHdrProducerSchema,
            status = "passed",
            failureReason = string.Empty,
            approvedReferenceManifestSha256 = hash,
            referenceCaptureManifestSha256 = hash,
            candidateCaptureManifestSha256 = hash,
            contractFingerprint = hash,
            metricVersion = "nvidia-hdr-flip/v1.7",
            relativeRmseDefinition =
                "sqrt(mean((candidate-reference)^2)) / max(sqrt(mean(reference^2)), 1e-6 linear-radiance units)",
            flipMetricDefinition =
                "Nearest-rank P95 of the NVIDIA HDR-FLIP v1.7 per-pixel error map; " +
                "scene-linear RGB, PPD=67.0206451, ACES, reference-auto start/stop/count exposures, " +
                "source b475eb4 via FlipBinding.CSharp 1.0.3.",
            flipConfiguration = new
            {
                nvidiaFlipVersion = "1.7",
                nvidiaSourceRevision = "b475eb4",
                bindingPackage = "FlipBinding.CSharp",
                bindingVersion = "1.0.3",
                pixelsPerDegree = 67.0206451,
                toneMapper = "aces",
                startExposure = "reference-auto",
                stopExposure = "reference-auto",
                numberOfExposures = "reference-auto"
            },
            approval = new
            {
                approvalId = "approved-hdr-synthetic",
                reviewer = "Synthetic Release Reviewer",
                approvedAtUtc = new DateTimeOffset(
                    2026,
                    7,
                    28,
                    11,
                    0,
                    0,
                    TimeSpan.Zero),
                reason = "Reviewed synthetic linear HDR reference."
            },
            images = new[]
            {
                new
                {
                    signal = "FinalComposedIndirect",
                    referenceSha256 = hash,
                    candidateSha256 = hash,
                    componentCount = 1024,
                    referenceRms = 1.0,
                    absoluteRmse = 0.001,
                    relativeRmse = 0.001,
                    flipP95 = 0.002,
                    maximumRelativeRmse = 0.12,
                    maximumFlipP95 = 0.08,
                    passed = true
                }
            },
            roiGates = new object[]
            {
                CreateApprovedHdrRoi(
                    "uniform-luminance",
                    "UniformLuminance",
                    "FinalComposedIndirect",
                    "relative luminance difference",
                    0.05,
                    hash),
                CreateApprovedHdrRoi(
                    "transition-step",
                    "TransitionStep",
                    "FinalComposedIndirect",
                    "relative transition-step difference",
                    0.10,
                    hash),
                CreateApprovedHdrRoi(
                    "hybrid-low-frequency-mean",
                    "HybridLowFrequencyMean",
                    "FinalComposedIndirect",
                    "relative low-frequency mean difference",
                    0.02,
                    hash),
                CreateApprovedHdrRoi(
                    "temporal-stability",
                    "TemporalStability",
                    "FinalComposedIndirect",
                    "relative temporal P95 difference",
                    0.03,
                    hash)
            }
        };
    }

    private static object CreateApprovedHdrRoi(
        string roi,
        string kind,
        string signal,
        string comparisonDefinition,
        double maximum,
        string hash) =>
        new
        {
            roi,
            kind,
            signal,
            comparisonDefinition,
            measuredRelativeDifference = 0.001,
            maximumRelativeDifference = maximum,
            sampleCount = 64,
            evidenceSha256 = new[] { hash },
            passed = true
        };

    private static object CreateKhronosRenderedProducer(string deviceId)
    {
        const string hash =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        MaterialGiEvidenceDeviceIdentity device = GetDeviceIdentity(deviceId);
        return new
        {
            schemaVersion = 3,
            schema =
                MaterialGiReleaseEvidenceContract.KhronosRenderedProducerSchema,
            status = "Passed",
            manifestSha256 = hash,
            semanticGateReportSha256 = hash,
            packageSha256 = hash,
            captureSha256 = hash,
            assetCount = 11,
            semanticMaterialCount = 12,
            semanticSubMeshCount = 12,
            runtimeMaterialCount = 12,
            runtimeSubMeshCount = 12,
            renderedFrameCount = 8,
            gpuDevice = device.GpuName,
            gpuDriver = device.DriverVersion,
            strictCookedPolicy = true,
            sourceFallbackEnabled = false,
            validation = new
            {
                mode = "Standard",
                warningMessageCount = 0,
                errorMessageCount = 0
            },
            capture = new { artifact = new { sha256 = hash } },
            semanticRender = new
            {
                metrics = new
                {
                    unlitPixelCount = 64,
                    unlitLightingRelativeRmse = 0.001,
                    lightingResponsivePbrPixelCount = 64,
                    meanPbrLightingResponse = 0.1,
                    emissiveStrengths = new[]
                    {
                        CreateEmissionStrength(1),
                        CreateEmissionStrength(2),
                        CreateEmissionStrength(4),
                        CreateEmissionStrength(8),
                        CreateEmissionStrength(16)
                    }
                }
            },
            failure = (string?)null,
            failures = Array.Empty<string>()
        };
    }

    private static object CreateEmissionStrength(double strength) => new
    {
        strength,
        pixelCount = 64,
        maximumRelativeRadianceError = 0.001,
        beautyEmissionCoverageRatio = 0.99
    };

    private static object CreateGraphicsAsyncProducer()
    {
        const string hash =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        return new
        {
            schemaVersion =
                MaterialGiReleaseEvidenceContract.GraphicsAsyncProducerSchema,
            status = "passed",
            failureReason = string.Empty,
            tolerance = new
            {
                maximumAbsoluteRmse = 0.002,
                maximumRelativeRmse = 0.001,
                maximumAbsoluteComponentError = 0.05
            },
            outputs = new[]
            {
                "DirectDiffuse",
                "DirectSpecular",
                "RawDdgiIrradiance",
                "FinalDdgiDiffuse",
                "RawSsgiEstimate",
                "FinalComposedIndirect",
                "MaterialDiffuseReflectance",
                "CompiledEmission",
                "MaterialOcclusion",
                "GiOwnershipWeights",
                "MaterialSidedness"
            }.Select(signal => new
            {
                signal,
                referenceSha256 = hash,
                candidateSha256 = hash,
                componentCount = 1024,
                absoluteRmse = 0.0001,
                relativeRmse = 0.0001,
                maximumAbsoluteComponentError = 0.001,
                passed = true
            }).ToArray()
        };
    }

    private static MaterialGiTestMatrixProducerReport
        CreateTestMatrixProducer(string deviceId) =>
        new()
        {
            Status = MaterialGiReleaseEvidenceContract.PassedStatus,
            BuildConfiguration = "Release",
            BuildCommit = BuildCommit,
            ShaderFingerprint = ShaderFingerprint,
            SettingsFingerprint = SettingsFingerprint,
            Device = GetDeviceIdentity(deviceId),
            Results =
            [
                .. MaterialGiReleaseEvidenceContract
                    .RequiredOracleReleaseChecks
                    .Select(check => new MaterialGiTestMatrixProducerResult
                    {
                        Name = check,
                        Status =
                            MaterialGiReleaseEvidenceContract.PassedStatus,
                        PassedCount = 1
                    })
            ]
        };

    private static object CreateBenchmarkProducer(
        string deviceId,
        string qualityTier)
    {
        MaterialGiEvidenceDeviceIdentity device = GetDeviceIdentity(deviceId);
        RenderBudgetProfile profile = qualityTier switch
        {
            "Low" => RenderBudgetProfile.LowSpec1080p30,
            "Medium" => RenderBudgetProfile.MidSpec1080p60,
            "High" => RenderBudgetProfile.HighSpec1440p60,
            "Ultra" => RenderBudgetProfile.Ultra4k60,
            _ => throw new ArgumentOutOfRangeException(
                nameof(qualityTier),
                qualityTier,
                null)
        };
        const ulong actualGpuUsage = 1024;
        const ulong actualGpuBudget = 4096;
        const ulong ddgiBudget = 64UL * 1024UL * 1024UL;
        const ulong farFieldBudget = 16UL * 1024UL * 1024UL;
        const ulong accelerationStructureBudget =
            16UL * 1024UL * 1024UL;
        RendererDiagnostics diagnostics = RendererDiagnostics.Empty with
        {
            CaptureGpuDeviceName = device.GpuName,
            CaptureGpuDriverVersion = device.DriverVersion,
            CaptureRun = PerformanceCaptureRunMetadata.Unknown with
            {
                Commit = BuildCommit,
                ShaderBundleHash = $"sha256:{ShaderFingerprint}"
            },
            ActiveQualityPreset = RenderQualityPreset.DdgiHigh,
            ActiveBudgetProfile = profile.Kind,
            ActiveBudgetProfileName = profile.Name,
            CpuTotalDrawSceneMicroseconds = 1000,
            GpuFrameMicroseconds = 1000,
            GpuTimingValid = 1,
            GpuForwardGiIncrementalAttribution =
                GiTimingAttribution.Exclusive,
            GpuForwardGiIncrementalMicroseconds = 100,
            GpuDdgiUpdateMicroseconds = 100,
            GlobalIlluminationCpuTimingSampleCount = 120,
            CpuGlobalIlluminationRecordP95Microseconds = 100,
            MaterialCompileTimingSampleCount = 120,
            MaterialUploadTimingSampleCount = 120,
            MaterialCompileP95Microseconds = 50,
            MaterialUploadP95Microseconds = 50,
            GlobalIlluminationEnabled = 1,
            GlobalIlluminationDdgiActive = 1,
            GlobalIlluminationSsgiActive = 0,
            GlobalIlluminationRayQuerySupported = 1,
            GlobalIlluminationRayQueryActive = 1,
            SimpleDdgiActive = 1,
            MaterialGiV2ActiveFeatures = MaterialGiV2Feature.All,
            MaterialGiRolloutMode =
                MaterialGiRolloutMode.QualificationCandidate,
            MaterialGiReleaseQualificationRequired = 1,
            MaterialGiReleaseQualified = 0,
            MaterialGiReleaseQualificationFailureCount = 0,
            MaterialGiV1RemovalOwner =
                MaterialGiV1CompatibilityContract.Owner,
            MaterialGiV1RemovalTargetDate =
                MaterialGiV1CompatibilityContract.RemovalTargetDate
                    .ToString("yyyy-MM-dd"),
            DdgiProbeCount = 32,
            DdgiActiveProbeCount = 32,
            DdgiProbesUpdated = 1,
            DdgiMaxActiveProbeBudget = 64,
            DdgiProbeUpdateRequestBudget = 64,
            DdgiAtlasMemoryBudgetBytes = ddgiBudget,
            AccelerationStructureMemoryBudgetBytes =
                accelerationStructureBudget,
            FarFieldMemoryBudgetBytes = farFieldBudget,
            FarFieldPagedFeatureEnabled = 1,
            GpuMemoryBudgetQueryAvailable = 1,
            ActualGpuMemoryUsageBytes = actualGpuUsage,
            ActualGpuMemoryBudgetBytes = actualGpuBudget
        };
        var memory = new MemoryBudgetSnapshot(
            actualGpuUsage,
            profile.GpuMemoryBudgetBytes,
            [
                new MemoryBudgetEntry(
                    MemoryBudgetCategory.GlobalIllumination,
                    actualGpuUsage,
                    1,
                    "synthetic GI residency")
            ],
            new MemoryHeapBudgetSnapshot(
                true,
                [
                    new MemoryHeapBudgetEntry(
                        0,
                        true,
                        actualGpuUsage,
                        actualGpuBudget,
                        actualGpuUsage,
                        actualGpuUsage,
                        1,
                        1)
                ]));
        RenderBudgetSnapshot budget =
            new RenderBudgetEvaluator().Evaluate(
                profile,
                diagnostics,
                memory,
                new UploadBudgetSnapshot(
                    0,
                    profile.UploadBudgetBytesPerFrame,
                    0,
                    0,
                    [],
                    RenderBudgetStatus.WithinBudget),
                new RuntimeStallSnapshot(
                    0,
                    0,
                    RuntimeStallReason.Unknown,
                    0,
                    []));
        return new
        {
            Schema =
                MaterialGiReleaseEvidenceContract.BenchmarkProducerSchema,
            Kind = MaterialGiReleaseEvidenceContract.BenchmarkProducerKind,
            CapturedAtUtc = new DateTimeOffset(
                2026,
                7,
                28,
                12,
                0,
                0,
                TimeSpan.Zero),
            Options = new
            {
                Enabled = true,
                WarmupFrameCount = 30,
                MeasureFrameCount = 120,
                ReportPath = (string?)null,
                DisableVSync = true,
                BudgetProfileOverride = (int)profile.Kind,
                MaterialGiQualificationCandidate = true
            },
            Scenario = 14,
            WarmupFrameCount = 30,
            MeasurementFrameCount = 120,
            FirstMeasurementFrameIndex = 30,
            LastMeasurementFrameIndex = 149,
            GpuTimingSupported = 1,
            GpuTimingValidSampleCount = 120,
            GpuFrameMilliseconds = new { Count = 120 },
            BudgetMetrics = budget.Metrics,
            LastDiagnostics = diagnostics,
            DdgiProductionGate = new
            {
                Passed = true,
                Failures = Array.Empty<object>(),
                Criteria = CreateSyntheticDdgiProductionCriteria()
            },
            AccuracyOracleResults = new[]
            {
                new
                {
                    Name = "simple-ddgi-furnace",
                    Scenario = 14,
                    Metric =
                        "SimpleDdgiAverageSampledIrradianceLuminance",
                    Status = 0,
                    MeasuredValue = MathF.PI * 0.25f / 0.5f,
                    ReferenceValue =
                        (float?)(MathF.PI * 0.25f / 0.5f),
                    RelativeError = 0.0f,
                    LatencyFrames = (int?)null,
                    Detail =
                        "measured=1.5708, reference=1.5708, relError=0.0000"
                }
            }
        };
    }

    private static object[] CreateSyntheticDdgiProductionCriteria() =>
    [
        .. new[]
        {
            "required-production-scene",
            "ddgi-high-profile",
            "ddgi-only-ray-query-active",
            "no-ssgi-resources",
            "no-ssgi-passes",
            "ddgi-split-passes-present",
            "no-recursive-ddgi-copy",
            "ddgi-async-compute-enabled",
            "no-static-frame-full-as-rebuild",
            "ddgi-ray-query-scene-complete",
            "ddgi-static-ray-coverage-complete",
            "requested-paged-far-field-active",
            "clipmaps-preserved-with-authored-volumes",
            "ddgi-gather-tiles-valid",
            "ddgi-forward-exhaustive-fallback-unused",
            "phase10-forward-metrics-valid",
            "phase9-raw-atlas-to-final-energy",
            "phase9-environment-fallback-not-dominant",
            "phase9-emissive-bounce-present",
            "phase9-thin-wall-leak-policy-active",
            "phase10-cache-warmup-steady",
            "phase10-warmup-progress-valid",
            "phase10-scheduler-p95-budget",
            "phase10-scheduler-overflow-free",
            "phase10-scheduler-equivalence",
            "gpu-timing-valid",
            "ddgi-update-p95-budget",
            "phase8-emergency-degrade-preserves-near-field",
            "ddgi-memory-budget",
            "phase8-tier-memory-budget",
            "phase10-ddgi-memory-diagnostics",
            "budget-metrics-within-gate",
            "foliage-ddgi-receiver-covered",
            "debug-views-expose-ddgi-gate-data"
        }.Select(name => (object)new
        {
            Name = name,
            Passed = true,
            Detail = "synthetic authenticated criterion"
        })
    ];

    private static object CreateLongRunProducer()
    {
        DateTimeOffset startedUtc = new(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        object[] retainedSamples =
        [
            .. Enumerable.Range(0, 120).Select(index => (object)new
            {
                FrameIndex = index * 15,
                ManagedBytes = 1000L,
                Gen0Collections = 0,
                Gen1Collections = 0,
                Gen2Collections = 0,
                DescriptorPressure = new
                {
                    TextureCapacity = 64,
                    TextureUsed = 4,
                    TextureHighWater = 4,
                    SamplerCapacity = 64,
                    SamplerUsed = 4,
                    SamplerHighWater = 4,
                    DescriptorWrites = 10
                },
                TrackedGpuMemoryBytes = 2048L,
                ActualGpuMemoryUsageBytes = 2048L,
                EffectiveGpuMemoryBudgetBytes = 4096L,
                BudgetStatus = 1,
                OverBudgetMetrics = Array.Empty<string>()
            })
        ];
        return new
        {
            SchemaVersion = 3,
            Kind = MaterialGiReleaseEvidenceContract.LongRunProducerKind,
            Status = "passed",
            Failure = (string?)null,
            StartedUtc = startedUtc,
            CompletedUtc = startedUtc.AddSeconds(1800.5),
            ElapsedSeconds = 1800.5,
            RequestedFrameCount = 0,
            RequestedMinutes = 30.0,
            WarmupFrames = 120,
            SampleIntervalFrames = 15,
            RetainedSampleCapacity = 256,
            LastPreparedFrameIndex = 1799,
            ExpectedSampleCount = 120L,
            TotalSamples = 120L,
            RetainedSamples = retainedSamples,
            PostWarmupBudgetViolationFrameCount = 0,
            BudgetViolations = Array.Empty<string>(),
            PostWarmupTelemetryCoverageFailureFrameCount = 0,
            TelemetryCoverageFailures = Array.Empty<string>(),
            ManagedMemoryTrend = new
            {
                Signal = "managed-memory",
                SampleCount = 112,
                FirstFrame = 120,
                LastFrame = 1785,
                FirstBytes = 1000L,
                LastBytes = 1000L,
                NetGrowthBytes = 0L,
                SlopeBytesPerFrame = 0.0,
                NoiseToleranceBytes = 1_048_576L,
                HasPositiveTrend = false
            },
            GpuMemoryTrend = new
            {
                Signal = "actual-gpu-memory",
                SampleCount = 112,
                FirstFrame = 120,
                LastFrame = 1785,
                FirstBytes = 2048L,
                LastBytes = 2048L,
                NetGrowthBytes = 0L,
                SlopeBytesPerFrame = 0.0,
                NoiseToleranceBytes = 1_048_576L,
                HasPositiveTrend = false
            },
            GpuMemorySignal = "VK_EXT_memory_budget",
            DescriptorPressure = new
            {
                PostWarmupSampleCount = 112L,
                TextureExhaustionSampleCount = 0L,
                SamplerExhaustionSampleCount = 0L,
                MaximumTextureUsed = 4,
                MaximumTextureCapacity = 64,
                MaximumSamplerUsed = 4,
                MaximumSamplerCapacity = 64
            },
            Workload = new
            {
                Name = "deterministic-dynamic-material-and-camera-path",
                DeterministicSeed = 0x4D474932,
                PreparedFrameCount = 1800,
                MaterialMutationCount = 60,
                MaterialMutationIntervalFrames = 30,
                MaterialRollbackSucceeded = true,
                CameraRollbackSucceeded = true,
                CameraPath =
                    "2400-frame elliptical path with bounded vertical/yaw/pitch modulation"
            },
            DeviceLossRecovery = new
            {
                Supported = false,
                Attempted = false,
                Status = "rejected-unsupported",
                Reason =
                    "No safe deterministic device-loss injection is exposed by the synthetic renderer."
            }
        };
    }

    private static object CreateHealthProducer(string role, string deviceId)
    {
        MaterialGiEvidenceDeviceIdentity device = GetDeviceIdentity(deviceId);
        object[] operations = role switch
        {
            MaterialGiReleaseEvidenceContract.LifecycleResilienceRole =>
            [
                CreateOperation("resize", "passed", "observed=true"),
                CreateOperation(
                    "minimize-zero-framebuffer",
                    "passed",
                    "observed=true"),
                CreateOperation(
                    "restore-framebuffer",
                    "passed",
                    "observed=true"),
                CreateOperation(
                    "scene-reload",
                    "passed",
                    "postReloadFrameObserved=true")
            ],
            MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole =>
            [
                CreateOperation(
                    "quality-switch",
                    "passed",
                    "tiers=Low,Medium,High,Ultra, rollback=High, " +
                    $"settings=sha256:{SettingsFingerprint}, rendererRestarted=false")
            ],
            MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole =>
            [
                CreateOperation(
                    "texture-hot-reload",
                    "passed",
                    "descriptorCount=1, rollback=true, rendererRestarted=false")
            ],
            MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole =>
            [
                CreateOperation(
                    "long-run-stability",
                    "passed",
                    "report='synthetic-long-run.json'")
            ],
            MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole =>
            [
                CreateOperation(
                    "device-loss-recovery",
                    "rejected-unsupported",
                    "The synthetic renderer exposes no safe deterministic device-loss injection.")
            ],
            _ =>
            [
                CreateOperation(
                    "startup",
                    "passed",
                    "renderedFrameObserved=true")
            ]
        };
        return new
        {
            kind = MaterialGiReleaseEvidenceContract.HealthProducerKind,
            schema = MaterialGiReleaseEvidenceContract.HealthProducerSchema,
            timestampUtc = new DateTimeOffset(
                2026,
                7,
                28,
                12,
                0,
                0,
                TimeSpan.Zero),
            status = "passed",
            failure = (string?)null,
            validationWarningCount = 0,
            validationErrorCount = 0,
            options = new
            {
                ValidationMode = "Standard"
            },
            operations,
            diagnostics = new
            {
                CaptureGpuDeviceName = device.GpuName,
                CaptureGpuDriverVersion = device.DriverVersion,
                CaptureRun = new
                {
                    Commit = BuildCommit,
                    ShaderBundleHash = $"sha256:{ShaderFingerprint}"
                }
            }
        };
    }

    private static object CreateOperation(
        string name,
        string status,
        string detail) =>
        new
        {
            Name = name,
            Status = status,
            FrameIndex = 1,
            Detail = detail
        };

    private static string CreateSyntheticMeasurement(string role) =>
        role switch
        {
            MaterialGiReleaseEvidenceContract.ApprovedHdrRole =>
                "approved-linear-hdr-p95=0.004",
            MaterialGiReleaseEvidenceContract.KhronosRenderedSemanticRole =>
                "khronos-rendered-semantic-scenes=11/11",
            MaterialGiReleaseEvidenceContract.GraphicsAsyncEquivalenceRole =>
                "graphics-async-max-absolute-error=0",
            MaterialGiReleaseEvidenceContract.CpuGpuOracleReleaseMatrixRole =>
                "cpu-gpu-oracle=passed;release-build=passed;release-tests=passed",
            MaterialGiReleaseEvidenceContract.TierPerformanceMatrixRole =>
                "Low,Medium,High,Ultra=within-budget",
            MaterialGiReleaseEvidenceContract.ThirtyMinuteSoakRole =>
                "duration-seconds=1800;failures=0",
            MaterialGiReleaseEvidenceContract.CleanValidationRole =>
                "validation-enabled=true;warnings=0;errors=0",
            MaterialGiReleaseEvidenceContract.LifecycleResilienceRole =>
                "resize=passed;minimize=passed;restore=passed;reload=passed",
            MaterialGiReleaseEvidenceContract.QualitySwitchRollbackRole =>
                "quality-switch=passed;rollback=passed;restart=false",
            MaterialGiReleaseEvidenceContract.TextureHotReloadRollbackRole =>
                "texture-hot-reload=passed;rollback=passed;descriptor-delta=0",
            MaterialGiReleaseEvidenceContract.RecoveryCapabilityRole =>
                "supported=0;unsupported=2;limitations-authenticated=true",
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Unknown synthetic release evidence role.")
        };

    private static string GetManifestRelativePath(
        string directoryPath,
        string path) =>
        Path.GetRelativePath(directoryPath, path)
            .Replace('\\', '/');

    private static void WriteJson<T>(string path, T value)
    {
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                value,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
    }

}
