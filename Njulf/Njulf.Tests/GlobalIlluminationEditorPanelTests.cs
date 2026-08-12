using System.Reflection;
using Njulf.Editor;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalIlluminationEditorPanelTests
{
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
