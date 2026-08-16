using System.Security.Cryptography;
using Njulf.Rendering.Data;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class DirectionalShadowQualificationManifestTests
{
    private string _directory = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "directional-shadow-qualification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void ValidPinnedEvidence_MatchesOnlyExactRuntimeBinding()
    {
        string path = WriteManifest(out DirectionalShadowQualificationEntryDocument entry);

        bool loaded = DirectionalShadowQualificationManifestCodec.TryLoad(
            path,
            out DirectionalShadowQualificationManifest manifest,
            out string detail,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        DirectionalShadowQualificationGateResult accepted = manifest.Evaluate(
            CreateContext());
        DirectionalShadowQualificationRuntimeContext mismatchedContext =
            CreateContext() with { DriverVersion = 999u };
        DirectionalShadowQualificationGateResult rejected = manifest.Evaluate(
            mismatchedContext);

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.True, detail);
            Assert.That(manifest.Count, Is.EqualTo(1));
            Assert.That(accepted.Passed, Is.True);
            Assert.That(accepted.Level,
                Is.EqualTo(DirectionalShadowQualificationLevel.Production));
            Assert.That(accepted.QualificationId,
                Is.EqualTo("sha256:" + entry.QualificationId));
            Assert.That(accepted.MatchedTrackId, Is.EqualTo("reference-1440p"));
            Assert.That(rejected.Passed, Is.False);
            Assert.That(rejected.FailureDetail, Does.Contain("device-driver"));
        });
    }

    [Test]
    public void TamperedArtifact_IsRejectedFailClosed()
    {
        string path = WriteManifest(out _);
        string artifactPath = Path.Combine(_directory, "numericCorrectness.json");
        File.AppendAllText(artifactPath, "tampered");

        bool loaded = DirectionalShadowQualificationManifestCodec.TryLoad(
            path,
            out DirectionalShadowQualificationManifest manifest,
            out string detail,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.False);
            Assert.That(manifest.Count, Is.EqualTo(0));
            Assert.That(detail, Does.Contain("artifact size"));
        });
    }

    [Test]
    public void ProxyCategoryRequiresExplicitManifestApproval()
    {
        string path = WriteManifest(out _);
        Assert.That(DirectionalShadowQualificationManifestCodec.TryLoad(
            path,
            out DirectionalShadowQualificationManifest manifest,
            out _,
            new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero)),
            Is.True);
        const RaySceneGeometryCategory foliage =
            RaySceneGeometryCategory.FoliageOpaque |
            RaySceneGeometryCategory.FoliageAlphaTested;
        DirectionalShadowQualificationRuntimeContext context = CreateContext()
            with
            {
                ExactCategories =
                    RaySceneGeometryCategory.DirectionalShadowDefault & ~foliage,
                ProxyCategories = foliage
            };

        DirectionalShadowQualificationGateResult result = manifest.Evaluate(
            context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Passed, Is.False);
            Assert.That(result.FailureDetail, Does.Contain("geometry"));
        });
    }

    [Test]
    public void CaptureCatalog_IsDeterministicAndCoversProductionMatrix()
    {
        string first = DirectionalShadowQualificationTracks.SerializeCatalog();
        string second = DirectionalShadowQualificationTracks.SerializeCatalog();
        IReadOnlyList<DirectionalShadowCaptureVariant> variants =
            DirectionalShadowQualificationTracks.CreateReferenceVariants();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(DirectionalShadowQualificationTracks.All, Has.Count.EqualTo(9));
            Assert.That(variants, Has.Count.EqualTo(72));
            Assert.That(variants, Has.Some.Matches<DirectionalShadowCaptureVariant>(
                variant => variant.Width == 3840u &&
                    variant.Height == 2160u &&
                    variant.ShadowMode == DirectionalShadowMode.RayQuerySoft &&
                    variant.DdgiEnabled && variant.DdgiRayQueryBackend));
        });
    }

    [Test]
    public void SettingsFingerprint_BindsAdaptiveRadiusAndFiniteSunScale()
    {
        var settings = new RenderSettings();
        string baseline = DirectionalShadowSettingsFingerprint.Compute(settings);

        settings.Shadows.DirectionalSoftAngularDiameterScale = 2f;
        string scaledSun = DirectionalShadowSettingsFingerprint.Compute(settings);
        settings.Shadows.DirectionalSoftAngularDiameterScale = 1f;
        settings.Shadows.DirectionalPcfRadiusMode =
            DirectionalPcfRadiusMode.Constant;
        string constantRadius =
            DirectionalShadowSettingsFingerprint.Compute(settings);

        Assert.Multiple(() =>
        {
            Assert.That(scaledSun, Is.Not.EqualTo(baseline));
            Assert.That(constantRadius, Is.Not.EqualTo(baseline));
        });
    }

    private string WriteManifest(
        out DirectionalShadowQualificationEntryDocument entry)
    {
        var artifacts = new List<DirectionalShadowQualificationArtifactPin>();
        foreach (DirectionalShadowQualificationEvidenceRole role in
                 Enum.GetValues<DirectionalShadowQualificationEvidenceRole>())
        {
            string relativePath = role + ".json";
            string fullPath = Path.Combine(_directory, relativePath);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(
                $"{{\"role\":\"{role}\",\"passed\":true}}");
            File.WriteAllBytes(fullPath, bytes);
            artifacts.Add(new()
            {
                Role = role,
                RelativePath = relativePath,
                ByteLength = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                    .ToLowerInvariant()
            });
        }

        entry = new DirectionalShadowQualificationEntryDocument
        {
            Mode = DirectionalShadowMode.RayQueryHard,
            ShaderBundleSha256 = new string('a', 64),
            SettingsFingerprintSha256 = new string('b', 64),
            BuildCommit = new string('c', 40),
            ApprovalId = "shadow-review-2026-08-13",
            ApprovedAtUtc = new DateTimeOffset(
                2026, 8, 13, 12, 0, 0, TimeSpan.Zero),
            DeviceRules =
            [
                new DirectionalShadowQualificationDeviceRule
                {
                    RuleId = "rtx-local",
                    VendorId = 0x10deu,
                    MinimumDeviceId = 1u,
                    MaximumDeviceId = 100u,
                    MinimumDriverVersion = 10u,
                    MaximumDriverVersion = 20u,
                    MinimumApiVersion = 1u,
                    MaximumApiVersion = 10u
                }
            ],
            Profiles =
            [
                new DirectionalShadowQualificationProfile
                {
                    TrackId = "reference-1440p",
                    Width = 2560u,
                    Height = 1440u,
                    AntiAliasingMode = AntiAliasingMode.SmaaHigh,
                    QualityPreset = RenderQualityPreset.Ultra,
                    IndependentRuns = 3u,
                    ReferenceFrames = 240u,
                    MedianTotalGpuMicroseconds = 9000,
                    P95TotalGpuMicroseconds = 10500,
                    P95DirectionalShadowGpuMicroseconds = 1200,
                    DirectionalShadowMemoryBytes = 64UL * 1024UL * 1024UL,
                    TotalGpuBudgetMicroseconds = 11000,
                    P95TotalGpuBudgetMicroseconds = 12000,
                    DirectionalShadowGpuBudgetMicroseconds = 1500,
                    DirectionalShadowMemoryBudgetBytes =
                        96UL * 1024UL * 1024UL,
                    MaximumImageDifference = 0.01,
                    MeasuredImageDifference = 0.004,
                    VulkanValidationErrorCount = 0,
                    VisualReviewApproved = true
                }
            ],
            Artifacts = artifacts.ToArray()
        };
        entry = entry with
        {
            QualificationId =
                DirectionalShadowQualificationManifestCodec
                    .ComputeQualificationId(entry)
        };
        string manifestPath = Path.Combine(_directory, "manifest.json");
        File.WriteAllText(
            manifestPath,
            DirectionalShadowQualificationManifestCodec.SerializeDocument(
                new DirectionalShadowQualificationManifestDocument
                {
                    Entries = [entry]
                }));
        return manifestPath;
    }

    private static DirectionalShadowQualificationRuntimeContext CreateContext() =>
        new(
            DirectionalShadowMode.RayQueryHard,
            CsmTemporalRequested: false,
            Width: 2560u,
            Height: 1440u,
            AntiAliasingMode.SmaaHigh,
            RenderQualityPreset.Ultra,
            VendorId: 0x10deu,
            DeviceId: 50u,
            DriverVersion: 15u,
            ApiVersion: 5u,
            ShaderBundleSha256: new string('a', 64),
            SettingsFingerprintSha256: new string('b', 64),
            BuildCommit: new string('c', 40),
            DirtyWorktreeState: "clean",
            ExactCategories:
                RaySceneGeometryCategory.DirectionalShadowDefault,
            ProxyCategories: RaySceneGeometryCategory.None);
}
