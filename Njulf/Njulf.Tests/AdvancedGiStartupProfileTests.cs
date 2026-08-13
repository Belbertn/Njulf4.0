using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Editor;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AdvancedGiStartupProfileTests
{
    private string _directory = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "njulf-advanced-gi-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Test]
    public void Save_UsesContentAddressedSettingsAndRoundTripsExactBinding()
    {
        string profilePath = Path.Combine(_directory, "startup.json");
        RenderSettings settings = CreateAllOffSettings();
        AdvancedGiRuntimeContentBinding binding = CreateBinding('a', 'b');

        AdvancedGiStartupProfileCodec.Save(
            profilePath, settings, binding);
        string firstSettingsPath = ReadSettingsPath(profilePath);
        settings.GlobalIllumination.IndirectIntensity = 0.75f;
        AdvancedGiStartupProfileCodec.Save(
            profilePath, settings, binding);
        string secondSettingsPath = ReadSettingsPath(profilePath);
        settings.Exposure = 1.25f;
        AdvancedGiStartupProfileCodec.Save(
            profilePath, settings, binding);
        string thirdSettingsPath = ReadSettingsPath(profilePath);

        bool loaded = AdvancedGiStartupProfileCodec.TryLoad(
            profilePath,
            out AdvancedGiStartupProfile? profile,
            out string detail);
        Assert.Multiple(() =>
        {
            Assert.That(firstSettingsPath, Is.Not.EqualTo(secondSettingsPath));
            Assert.That(secondSettingsPath, Is.Not.EqualTo(thirdSettingsPath));
            Assert.That(File.Exists(Path.Combine(
                _directory, firstSettingsPath)), Is.True);
            Assert.That(File.Exists(Path.Combine(
                _directory, secondSettingsPath)), Is.True);
            Assert.That(File.Exists(Path.Combine(
                _directory, thirdSettingsPath)), Is.True);
            Assert.That(loaded, Is.True, detail);
            Assert.That(profile, Is.Not.Null);
            Assert.That(profile!.ContentBinding, Is.EqualTo(binding.Normalize()));
            Assert.That(profile.Settings.GlobalIllumination.IndirectIntensity,
                Is.EqualTo(0.75f));
            Assert.That(profile.Settings.Exposure, Is.EqualTo(1.25f));
            Assert.That(profile.RenderSettingsSha256,
                Is.EqualTo(profile.Settings.ComputePersistenceSha256()));
            Assert.That(profile.SettingsFingerprintSha256,
                Is.EqualTo(AdvancedGiSettingsFingerprint.Compute(
                    profile.Settings.GlobalIllumination)));
        });
    }

    [Test]
    public void SettingsSnapshotAndEditorDraft_AreDetachedFromLiveRendererIntent()
    {
        var live = new RenderSettings();
        live.GlobalIllumination.SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.ExactCompacted;
        var draft = new AdvancedGiEditorActivationDraft(
            new AdvancedGiStartupProfileInputs(
                Path.Combine(_directory, "profile.json"),
                CreateBinding('c', 'd')),
            SimpleDdgiReceiverFeedbackMode.Off,
            DdgiOpacityMicromapMode.Off,
            SimpleDdgiDirectionalGuidingMode.Off,
            GiCausticMode.Off,
            SimpleDdgiNearFieldResidualMode.Off,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);

        RenderSettings snapshot = draft.CreateSettingsSnapshot(live);
        snapshot.GlobalIllumination.IndirectIntensity = 0.25f;

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.SameAs(live));
            Assert.That(snapshot.GlobalIllumination,
                Is.Not.SameAs(live.GlobalIllumination));
            Assert.That(snapshot.GlobalIllumination
                    .SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.Off));
            Assert.That(live.GlobalIllumination
                    .SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(live.GlobalIllumination.IndirectIntensity,
                Is.Not.EqualTo(0.25f));
        });
    }

    [Test]
    public void Load_RejectsAChangedContentAddressedSettingsDocument()
    {
        string profilePath = Path.Combine(_directory, "startup.json");
        RenderSettings settings = CreateAllOffSettings();
        AdvancedGiStartupProfileCodec.Save(
            profilePath, settings, CreateBinding('1', '2'));
        string settingsPath = Path.Combine(
            _directory, ReadSettingsPath(profilePath));
        RenderSettings changed = RenderSettings.Load(settingsPath);
        changed.Exposure = 7.0f;
        changed.Save(settingsPath);

        bool accepted = AdvancedGiStartupProfileCodec.TryLoad(
            profilePath, out _, out string detail);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(detail, Is.EqualTo(
                "advanced-gi-startup-profile-render-settings-hash-mismatch"));
        });
    }

    [Test]
    public void Preflight_AllOffIsReadyButRequestedFeatureNeedsPrerequisites()
    {
        AdvancedGiStartupProfileInputs inputs = new(
            Path.Combine(_directory, "startup.json"),
            CreateBinding('e', 'f'));
        RenderSettings allOff = CreateAllOffSettings();
        AdvancedGiStartupProfilePreflightResult ready =
            AdvancedGiStartupProfilePreflight.Evaluate(allOff, inputs);

        RenderSettings requested = allOff.CreateSnapshot();
        requested.GlobalIllumination.SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.ExactCompacted;
        AdvancedGiStartupProfilePreflightResult blocked =
            AdvancedGiStartupProfilePreflight.Evaluate(requested, inputs);

        Assert.Multiple(() =>
        {
            Assert.That(ready.Ready, Is.True, ready.FailureSummary);
            Assert.That(blocked.Ready, Is.False);
            Assert.That(blocked.Checks.Any(check =>
                    check.Id == "prerequisite-manifest" && !check.Passed),
                Is.True);
        });
    }

    [Test]
    public void RejectedNamedProfile_ClearsAmbientEvidenceAndModes()
    {
        var options = new RenderingOptions
        {
            AdvancedGiPrerequisiteManifestPath =
                Path.Combine(_directory, "ambient-prerequisite.json"),
            AdvancedGiQualificationManifestPath =
                Path.Combine(_directory, "ambient-qualification.json"),
            AdvancedGiRuntimeEvidenceBundlePath =
                Path.Combine(_directory, "ambient-runtime.json"),
            AdvancedGiCandidateProfilePath =
                Path.Combine(_directory, "ambient-candidate.json"),
            AdvancedGiStartupProfilePath =
                Path.Combine(_directory, "missing-profile.json")
        };
        options.InitialSettings.GlobalIllumination.GiCausticMode =
            GiCausticMode.WorldCacheExperiment;

        options.ResolveAdvancedGiStartupProfile();

        Assert.Multiple(() =>
        {
            Assert.That(options.AdvancedGiStartupProfileStatus,
                Does.StartWith("rejected:"));
            Assert.That(options.AdvancedGiPrerequisiteManifestPath, Is.Null);
            Assert.That(options.AdvancedGiQualificationManifestPath, Is.Null);
            Assert.That(options.AdvancedGiRuntimeEvidenceBundlePath, Is.Null);
            Assert.That(options.AdvancedGiCandidateProfilePath, Is.Null);
            Assert.That(options.AdvancedGiContentBinding,
                Is.EqualTo(AdvancedGiRuntimeContentBinding.Empty));
            Assert.That(options.InitialSettings.GlobalIllumination.GiCausticMode,
                Is.EqualTo(GiCausticMode.Off));
        });
    }

    private static RenderSettings CreateAllOffSettings()
    {
        var settings = new RenderSettings();
        GlobalIlluminationSettings gi = settings.GlobalIllumination;
        gi.SimpleDdgiReceiverFeedbackMode =
            SimpleDdgiReceiverFeedbackMode.Off;
        gi.DdgiOpacityMicromapMode = DdgiOpacityMicromapMode.Off;
        gi.SimpleDdgiDirectionalGuidingMode =
            SimpleDdgiDirectionalGuidingMode.Off;
        gi.GiCausticMode = GiCausticMode.Off;
        gi.SimpleDdgiNearFieldResidualMode =
            SimpleDdgiNearFieldResidualMode.Off;
        return settings;
    }

    private static AdvancedGiRuntimeContentBinding CreateBinding(
        char corpus,
        char scene) => new(
        new string(corpus, 64),
        "gi-test-profile",
        new string(scene, 64));

    private static string ReadSettingsPath(string profilePath)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(profilePath));
        return document.RootElement.GetProperty("renderSettingsPath")
            .GetString()!;
    }
}
