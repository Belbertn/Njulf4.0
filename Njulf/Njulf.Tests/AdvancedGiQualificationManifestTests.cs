using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiQualificationManifestTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-advanced-gi-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void CompletePinnedManifest_AdmitsOnlyExactConfiguredDeviceAndShaderIdentity()
    {
        string path = WriteValidC1Manifest();

        bool loaded = AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out AdvancedGiQualificationManifest manifest,
            out string loadReason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        AdvancedGiFeatureQualificationDocument document = ReadDocument(path).Features.Single();
        var exactContext = new AdvancedGiRuntimeQualificationContext(
            VendorId: AdvancedGiQualificationContract.NvidiaVendorId,
            DeviceId: 0x2520u,
            DriverVersion: 100u,
            ApiVersion: 1u,
            FeatureSupported: true,
            ShaderBundleSha256: document.ShaderBundleSha256,
            SettingsContractSha256: AdvancedGiQualificationContract.SettingsContractSha256);
        AdvancedGiQualificationGateResult admitted = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            exactContext,
            document.PrerequisiteQualificationId,
            document.QualificationId);
        AdvancedGiQualificationGateResult wrongDriver = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            exactContext with { DriverVersion = 101u },
            document.PrerequisiteQualificationId,
            document.QualificationId);
        AdvancedGiQualificationGateResult wrongShader = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            exactContext with { ShaderBundleSha256 = Hex('f') },
            document.PrerequisiteQualificationId,
            document.QualificationId);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.True, loadReason);
            Assert.That(manifest.Count, Is.EqualTo(1));
            Assert.That(admitted.Passed, Is.True, admitted.FailureDetail);
            Assert.That(admitted.QualificationId, Is.EqualTo(document.QualificationId));
            Assert.That(admitted.MatchedDeviceRuleId, Is.EqualTo("rtx3060-primary"));
            Assert.That(wrongDriver.Passed, Is.False);
            Assert.That(wrongDriver.FailureDetail,
                Is.EqualTo("advanced-gi-device-driver-class-not-qualified"));
            Assert.That(wrongShader.Passed, Is.False);
            Assert.That(wrongShader.FailureDetail,
                Is.EqualTo("advanced-gi-shader-bundle-evidence-mismatch"));
        });
    }

    [Test]
    public void AutoQualification_RequiresTheExplicitContentAddressedId()
    {
        string path = WriteValidC1Manifest();
        Assert.That(AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out AdvancedGiQualificationManifest manifest,
            out string reason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)), Is.True, reason);
        AdvancedGiFeatureQualificationDocument document = ReadDocument(path).Features.Single();
        var context = new AdvancedGiRuntimeQualificationContext(
            AdvancedGiQualificationContract.NvidiaVendorId,
            0x2520u,
            100u,
            1u,
            true,
            document.ShaderBundleSha256,
            AdvancedGiQualificationContract.SettingsContractSha256);

        AdvancedGiQualificationGateResult missing = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            context,
            document.PrerequisiteQualificationId,
            string.Empty);
        AdvancedGiQualificationGateResult stale = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            context,
            document.PrerequisiteQualificationId,
            Hex('9'));

        Assert.Multiple(() =>
        {
            Assert.That(missing.Passed, Is.False);
            Assert.That(missing.FailureDetail,
                Is.EqualTo("advanced-gi-configured-qualification-id-missing"));
            Assert.That(stale.Passed, Is.False);
            Assert.That(stale.FailureDetail,
                Is.EqualTo("advanced-gi-configured-qualification-id-mismatch"));
        });
    }

    [Test]
    public void ArtifactMutation_FailsClosedBeforeAnyEntryIsPublished()
    {
        string path = WriteValidC1Manifest();
        AdvancedGiFeatureQualificationDocument feature = ReadDocument(path).Features.Single();
        string artifact = Path.Combine(
            _directory,
            feature.Artifacts[0].RelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.AppendAllText(artifact, " ");

        bool loaded = AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out AdvancedGiQualificationManifest manifest,
            out string reason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(manifest.Count, Is.Zero);
            Assert.That(reason, Is.EqualTo("advanced-gi-qualification-artifact-length-mismatch"));
        });
    }

    [Test]
    public void AuthoredPerformanceReportBelowPromotionFloor_IsRejectedEvenWhenRepinned()
    {
        string path = WriteValidC1Manifest(candidateP95Milliseconds: 1.98);

        bool loaded = AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out AdvancedGiQualificationManifest manifest,
            out string reason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(manifest.Count, Is.Zero);
            Assert.That(reason, Is.EqualTo("advanced-gi-qualification-C1-performance-floor-failed"));
        });
    }

    [Test]
    public void MissingMandatoryDeviceClass_IsRejected()
    {
        string path = WriteValidC1Manifest();
        AdvancedGiQualificationManifestDocument document = ReadDocument(path);
        AdvancedGiFeatureQualificationDocument feature = document.Features.Single();
        feature = feature with
        {
            DeviceRules = feature.DeviceRules
                .Where(static rule => rule.RuleId != "ada-or-newer")
                .ToArray()
        };
        feature = feature with
        {
            QualificationId = AdvancedGiQualificationManifestCodec.ComputeQualificationId(feature)
        };
        File.WriteAllText(path, AdvancedGiQualificationManifestCodec.SerializeDocument(
            document with { Features = [feature] }));

        bool loaded = AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out _,
            out string reason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(reason, Is.EqualTo("advanced-gi-qualification-device-matrix-incomplete"));
        });
    }

    [Test]
    public void DisabledFallbackDevice_IsAuthenticatedButCannotActivateTheFeature()
    {
        string path = WriteValidC1Manifest();
        Assert.That(AdvancedGiQualificationManifestCodec.TryLoad(
            path,
            out AdvancedGiQualificationManifest manifest,
            out string reason,
            new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero)), Is.True, reason);
        AdvancedGiFeatureQualificationDocument document = ReadDocument(path).Features.Single();
        var fallback = new AdvancedGiRuntimeQualificationContext(
            VendorId: 0x1002u,
            DeviceId: 0x73BFu,
            DriverVersion: 200u,
            ApiVersion: 1u,
            FeatureSupported: false,
            ShaderBundleSha256: document.ShaderBundleSha256,
            SettingsContractSha256: AdvancedGiQualificationContract.SettingsContractSha256);

        AdvancedGiQualificationGateResult result = manifest.Evaluate(
            AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            fallback,
            document.PrerequisiteQualificationId,
            document.QualificationId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.FailureDetail,
                Is.EqualTo("advanced-gi-qualified-device-rule-confirms-canonical-fallback"));
        });
    }

    [Test]
    public void DirectionalGuidingQualification_RequiresMeasuredStatisticalCoverage()
    {
        AdvancedGiQualificationMeasurements valid =
            CreateDirectionalGuidingStatisticalMeasurements();

        bool accepted = AdvancedGiQualificationManifestCodec
            .TryValidateDirectionalGuidingCorrectness(valid, out string reason);
        bool insufficientCases = AdvancedGiQualificationManifestCodec
            .TryValidateDirectionalGuidingCorrectness(
                valid with { DirectionalGuidingStatisticalCaseCount = 6u },
                out string insufficientReason);
        bool biased = AdvancedGiQualificationManifestCodec
            .TryValidateDirectionalGuidingCorrectness(
                valid with
                {
                    DirectionalGuidingMaximumBiasStandardErrors = 4.0
                },
                out string biasReason);
        bool starved = AdvancedGiQualificationManifestCodec
            .TryValidateDirectionalGuidingCorrectness(
                valid with
                {
                    DirectionalGuidingUniformMaintenanceSampleCount = 9_999UL
                },
                out string maintenanceReason);
        bool mismatchedPdf = AdvancedGiQualificationManifestCodec
            .TryValidateDirectionalGuidingCorrectness(
                valid with
                {
                    DirectionalGuidingDirectionPdfIdentityMismatchCount = 1UL
                },
                out string identityReason);
        IReadOnlyList<string> requiredChecks =
            AdvancedGiQualificationContract.GetRequiredChecks(
                AdvancedGiPrerequisiteFeature.DirectionalGuiding,
                AdvancedGiQualificationEvidenceRole.Correctness);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True, reason);
            Assert.That(insufficientCases, Is.False);
            Assert.That(insufficientReason,
                Is.EqualTo(
                    "advanced-gi-qualification-C3-statistical-coverage-insufficient"));
            Assert.That(biased, Is.False);
            Assert.That(biasReason,
                Is.EqualTo(
                    "advanced-gi-qualification-C3-estimator-confidence-failed"));
            Assert.That(starved, Is.False);
            Assert.That(maintenanceReason,
                Is.EqualTo(
                    "advanced-gi-qualification-C3-uniform-maintenance-audit-failed"));
            Assert.That(mismatchedPdf, Is.False);
            Assert.That(identityReason,
                Is.EqualTo(
                    "advanced-gi-qualification-C3-direction-PDF-identity-failed"));
            Assert.That(requiredChecks, Does.Contain(
                "gpu-sampling-goodness-of-fit"));
            Assert.That(requiredChecks, Does.Contain(
                "independent-estimator-confidence"));
            Assert.That(requiredChecks, Does.Contain(
                "uniform-maintenance-audit"));
            Assert.That(requiredChecks, Does.Contain(
                "generation-time-pdf-identity"));
        });
    }

    private string WriteValidC1Manifest(double candidateP95Milliseconds = 1.80)
    {
        string prerequisite = Hex('b');
        string shader = Hex('c');
        string corpus = Hex('d');
        string build = new('e', 40);
        AdvancedGiQualificationDeviceRule[] rules =
        [
            new()
            {
                RuleId = "rtx3060-primary",
                Coverage = AdvancedGiQualificationDeviceCoverage.PrimaryRtx30 |
                    AdvancedGiQualificationDeviceCoverage.MinimumMemoryProfile,
                VendorId = AdvancedGiQualificationContract.NvidiaVendorId,
                MinimumDeviceId = 0x2520u,
                MaximumDeviceId = 0x2520u,
                MinimumDriverVersion = 100u,
                MaximumDriverVersion = 100u,
                MinimumApiVersion = 1u,
                MaximumApiVersion = 1u,
                ExpectedFeatureSupported = true
            },
            new()
            {
                RuleId = "ada-or-newer",
                Coverage = AdvancedGiQualificationDeviceCoverage.AdaOrNewer,
                VendorId = AdvancedGiQualificationContract.NvidiaVendorId,
                MinimumDeviceId = 0x2684u,
                MaximumDeviceId = 0x2684u,
                MinimumDriverVersion = 100u,
                MaximumDriverVersion = 100u,
                MinimumApiVersion = 1u,
                MaximumApiVersion = 1u,
                ExpectedFeatureSupported = true
            },
            new()
            {
                RuleId = "non-nvidia-fallback",
                Coverage = AdvancedGiQualificationDeviceCoverage.NonNvidiaRayQuery |
                    AdvancedGiQualificationDeviceCoverage.FeatureDisabledFallback,
                VendorId = 0x1002u,
                MinimumDeviceId = 0x73BFu,
                MaximumDeviceId = 0x73BFu,
                MinimumDriverVersion = 200u,
                MaximumDriverVersion = 200u,
                MinimumApiVersion = 1u,
                MaximumApiVersion = 1u,
                ExpectedFeatureSupported = false
            }
        ];
        var feature = new AdvancedGiFeatureQualificationDocument
        {
            Feature = AdvancedGiPrerequisiteFeature.OpacityMicromaps,
            FeatureAbiRevision = OpacityMicromapRuntimeAbi.Version,
            AlgorithmRevision = AdvancedGiQualificationContract.GetAlgorithmRevision(
                AdvancedGiPrerequisiteFeature.OpacityMicromaps),
            PrerequisiteQualificationId = prerequisite,
            ShaderBundleSha256 = shader,
            SettingsContractSha256 = AdvancedGiQualificationContract.SettingsContractSha256,
            CorpusSha256 = corpus,
            BuildCommit = build,
            ApprovalId = "reviewed-c1-qualification-20260801",
            ApprovedAtUtc = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            DeviceRules = rules
        };
        string bindingId = AdvancedGiQualificationManifestCodec.ComputeBindingId(feature);
        string evidenceDirectory = Path.Combine(_directory, "evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var pins = new List<AdvancedGiQualificationArtifactPin>();
        foreach (AdvancedGiQualificationDeviceRule rule in rules)
        {
            foreach (AdvancedGiQualificationEvidenceRole role in
                     AdvancedGiQualificationContract.GetRequiredRoles(rule.ExpectedFeatureSupported))
            {
                var report = new AdvancedGiQualificationEvidenceReport
                {
                    Role = role,
                    Feature = feature.Feature,
                    FeatureAbiRevision = feature.FeatureAbiRevision,
                    BindingId = bindingId,
                    DeviceRuleId = rule.RuleId,
                    Status = "Passed",
                    BuildCommit = build,
                    ShaderBundleSha256 = shader,
                    SettingsContractSha256 = feature.SettingsContractSha256,
                    CorpusSha256 = corpus,
                    PrerequisiteQualificationId = prerequisite,
                    PassedChecks = AdvancedGiQualificationContract.GetRequiredChecks(role).ToArray(),
                    Measurements = CreateMeasurements(role, candidateP95Milliseconds),
                    Summary = $"Pinned {role} evidence for {rule.RuleId}."
                };
                string relativePath = $"evidence/{rule.RuleId}-{role}.json";
                string reportJson = AdvancedGiQualificationManifestCodec.SerializeReport(report);
                byte[] reportBytes = Encoding.UTF8.GetBytes(reportJson);
                File.WriteAllBytes(Path.Combine(
                    _directory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar)), reportBytes);
                pins.Add(new AdvancedGiQualificationArtifactPin
                {
                    Role = role,
                    DeviceRuleId = rule.RuleId,
                    RelativePath = relativePath,
                    ByteLength = reportBytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(reportBytes)).ToLowerInvariant()
                });
            }
        }

        feature = feature with { Artifacts = pins.ToArray() };
        feature = feature with
        {
            QualificationId = AdvancedGiQualificationManifestCodec.ComputeQualificationId(feature)
        };
        string manifestPath = Path.Combine(_directory, "advanced-gi-qualification.json");
        File.WriteAllText(
            manifestPath,
            AdvancedGiQualificationManifestCodec.SerializeDocument(new()
            {
                Features = [feature]
            }));
        return manifestPath;
    }

    private static AdvancedGiQualificationMeasurements CreateMeasurements(
        AdvancedGiQualificationEvidenceRole role,
        double candidateP95Milliseconds)
    {
        return role switch
        {
            AdvancedGiQualificationEvidenceRole.Correctness => new()
            {
                FrameCount = AdvancedGiQualificationContract.MinimumReferenceFrames,
                IndependentRunCount = AdvancedGiQualificationContract.MinimumIndependentRuns,
                BaselineReferenceError = 0.0,
                CandidateReferenceError = 0.0
            },
            AdvancedGiQualificationEvidenceRole.Performance => new()
            {
                IndependentRunCount = AdvancedGiQualificationContract.MinimumIndependentRuns,
                BaselineTotalGiP95Milliseconds = 2.0,
                CandidateTotalGiP95Milliseconds = candidateP95Milliseconds,
                ConfidenceIntervalExcludesNoise = true
            },
            AdvancedGiQualificationEvidenceRole.Memory => new()
            {
                BudgetBytes = 1024,
                PeakLiveBytes = 768,
                RetiredButLiveBytes = 128
            },
            AdvancedGiQualificationEvidenceRole.LongRun => new()
            {
                FrameCount = AdvancedGiQualificationContract.MinimumReferenceFrames,
                DurationSeconds = AdvancedGiQualificationContract.MinimumLongRunSeconds
            },
            AdvancedGiQualificationEvidenceRole.Validation => new(),
            AdvancedGiQualificationEvidenceRole.Fallback => new()
            {
                FeatureOffCanonicalParity = true,
                UnsupportedZeroAllocation = true,
                FailureFallbackVerified = true
            },
            AdvancedGiQualificationEvidenceRole.Lifecycle => new()
            {
                LifecycleTransitionsVerified = true
            },
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };
    }

    private static AdvancedGiQualificationMeasurements
        CreateDirectionalGuidingStatisticalMeasurements() => new()
        {
            DirectionalGuidingStatisticalCaseCount =
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingStatisticalCases,
            DirectionalGuidingStatisticalSampleCount =
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingStatisticalCases *
                AdvancedGiQualificationContract
                    .MinimumDirectionalGuidingSamplesPerCase,
            DirectionalGuidingWorstGoodnessOfFitPValue = 0.05,
            DirectionalGuidingMaximumBiasStandardErrors = 1.25,
            DirectionalGuidingSampleCount = 100_000UL,
            DirectionalGuidingUniformMaintenanceSampleCount = 25_000UL,
            DirectionalGuidingDirectionPdfIdentityMismatchCount = 0UL,
            DirectionalGuidingIndependentAuditFailureCount = 0UL,
            DirectionalGuidingUniformMaintenanceFailureCount = 0UL
        };

    private static AdvancedGiQualificationManifestDocument ReadDocument(string path) =>
        JsonSerializer.Deserialize<AdvancedGiQualificationManifestDocument>(File.ReadAllText(path),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }) ?? throw new InvalidDataException("Test manifest could not be decoded.");

    private static string Hex(char value) => new(value, 64);
}
