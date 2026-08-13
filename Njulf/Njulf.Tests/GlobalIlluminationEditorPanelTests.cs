using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Editor;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationEditorPanelTests
{
    [Test]
    public void TextInputBuffers_StartAsNonNullEmptyStrings()
    {
        var panel = new GlobalIlluminationEditorPanel();
        FieldInfo filter = typeof(GlobalIlluminationEditorPanel).GetField(
            "_filter",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException("Filter field is unavailable.");
        FieldInfo settingsPath = typeof(GlobalIlluminationEditorPanel).GetField(
            "_settingsPath",
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Settings-path field is unavailable.");

        Assert.Multiple(() =>
        {
            Assert.That(filter.GetValue(panel), Is.EqualTo(string.Empty));
            Assert.That(settingsPath.GetValue(panel), Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public void AdvancedGiUnconfiguredContracts_ExposeNonNullEmptyText()
    {
        AdvancedGiRuntimeContentBinding binding =
            AdvancedGiRuntimeContentBinding.Empty;
        AdvancedGiRuntimeContentState state =
            AdvancedGiRuntimeContentState.Unconfigured;
        AdvancedGiEditorStartupContext startup =
            AdvancedGiEditorStartupContext.Unconfigured;
        var profileDocument = new AdvancedGiStartupProfileDocument();
        var renderingOptions = new RenderingOptions();

        Assert.Multiple(() =>
        {
            Assert.That(binding.CorpusSha256, Is.EqualTo(string.Empty));
            Assert.That(binding.ContentProfileId, Is.EqualTo(string.Empty));
            Assert.That(binding.SceneAssetSha256, Is.EqualTo(string.Empty));
            Assert.That(state.Expected, Is.EqualTo(binding));
            Assert.That(state.ObservedContentProfileId,
                Is.EqualTo(string.Empty));
            Assert.That(state.ObservedSceneAssetSha256,
                Is.EqualTo(string.Empty));
            Assert.That(startup.ContentBinding, Is.EqualTo(binding));
            Assert.That(profileDocument.ContentBinding, Is.EqualTo(binding));
            Assert.That(renderingOptions.AdvancedGiContentBinding,
                Is.EqualTo(binding));
        });
    }

    [Test]
    public void AdvancedGiFeatureSelection_DefaultSettingsExposeFiveEnabledSwitches()
    {
        var settings = new GlobalIlluminationSettings();

        AdvancedGiFeatureSelection selection =
            AdvancedGiFeatureSelection.From(settings);

        Assert.Multiple(() =>
        {
            Assert.That(selection, Is.EqualTo(
                AdvancedGiFeatureSelection.AllEnabled));
            Assert.That(selection.AreAllEnabled, Is.True);
        });
    }

    [Test]
    public void AdvancedGiFeatureSelection_AppliesExplicitModesAndClearsCredentials()
    {
        var settings = new GlobalIlluminationSettings
        {
            SimpleDdgiReceiverFeedbackMode =
                SimpleDdgiReceiverFeedbackMode.AutoQualified,
            DdgiOpacityMicromapMode =
                DdgiOpacityMicromapMode.AutoQualified,
            SimpleDdgiDirectionalGuidingMode =
                SimpleDdgiDirectionalGuidingMode.AutoQualified,
            GiCausticMode = GiCausticMode.AutoQualified,
            SimpleDdgiNearFieldResidualMode =
                SimpleDdgiNearFieldResidualMode.AutoQualified,
            SimpleDdgiReceiverFeedbackQualificationId = "b1",
            DdgiOpacityMicromapQualificationId = "c1",
            SimpleDdgiDirectionalGuidingQualificationId = "c3",
            GiCausticQualificationId = "c4",
            SimpleDdgiNearFieldResidualQualificationId = "c5"
        };

        AdvancedGiFeatureSelection.AllEnabled.ApplyTo(settings);

        Assert.Multiple(() =>
        {
            Assert.That(settings.SimpleDdgiReceiverFeedbackMode,
                Is.EqualTo(SimpleDdgiReceiverFeedbackMode.ExactCompacted));
            Assert.That(settings.DdgiOpacityMicromapMode,
                Is.EqualTo(DdgiOpacityMicromapMode.ExtFourStateExperiment));
            Assert.That(settings.SimpleDdgiDirectionalGuidingMode,
                Is.EqualTo(SimpleDdgiDirectionalGuidingMode
                    .PerProbeHistogramExperiment));
            Assert.That(settings.GiCausticMode,
                Is.EqualTo(GiCausticMode.WorldCacheExperiment));
            Assert.That(settings.SimpleDdgiNearFieldResidualMode,
                Is.EqualTo(SimpleDdgiNearFieldResidualMode
                    .HiZHalfResolutionExperiment));
            Assert.That(settings.SimpleDdgiReceiverFeedbackQualificationId,
                Is.Empty);
            Assert.That(settings.DdgiOpacityMicromapQualificationId, Is.Empty);
            Assert.That(settings.SimpleDdgiDirectionalGuidingQualificationId,
                Is.Empty);
            Assert.That(settings.GiCausticQualificationId, Is.Empty);
            Assert.That(settings.SimpleDdgiNearFieldResidualQualificationId,
                Is.Empty);
        });
    }

    [Test]
    public void CatalogCoversEveryWritableScalarGiSetting()
    {
        string[] expected = typeof(GlobalIlluminationSettings)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod?.IsPublic == true)
            .Where(property => property.Name != nameof(GlobalIlluminationSettings.SimpleDdgiAuthoredVolumes))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = GlobalIlluminationEditorPanel.EditableProperties
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(expected.Length, Is.GreaterThan(100));
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(
                GlobalIlluminationEditorPanel.IsSupportedScalarType(typeof(DdgiQualityTier)),
                Is.True);
        });
    }

    [Test]
    public void DebugViewHints_DistinguishExpectedFlatAutomaticRingViews()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiGatherLocalVolume),
                Does.Contain("authored local volumes").And.Contain("Sponza"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiSpatialCoverage),
                Does.Contain("does not prove"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiDataConfidence),
                Does.Contain("scalar support mask").And.Contain("nearly white"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiGatherFallback),
                Does.Contain("Black is the healthy endpoint"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiProbeIndex),
                Does.Contain("many stable colored cells"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiProbeRelocationDirection),
                Does.Contain("Neutral gray"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiUpdateReasons),
                Does.Contain("compact receiver ABI").And.Contain("debug overlay"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiClassificationInvalidScore),
                Does.Contain("does not publish"));
            Assert.That(
                GlobalIlluminationEditorPanel.GetDdgiDebugViewHint(
                    GlobalIlluminationDebugView.DdgiSampledIrradiance),
                Is.Null);
        });
    }
}
