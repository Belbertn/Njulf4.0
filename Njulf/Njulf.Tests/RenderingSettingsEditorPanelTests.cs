using System.Reflection;
using Njulf.Core.Math;
using Njulf.Editor;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class RenderingSettingsEditorPanelTests
{
    private static readonly Type[] CompleteSettingTypes =
    [
        typeof(DynamicResolutionSettings),
        typeof(AutoExposureSettings),
        typeof(BloomSettings),
        typeof(EnvironmentSettings),
        typeof(ReflectionSettings),
        typeof(AmbientOcclusionSettings),
        typeof(AntiAliasingSettings),
        typeof(FogSettings)
    ];

    [Test]
    public void TopLevelCatalogContainsTheExpectedOutputControls()
    {
        string[] actual = RenderingSettingsEditorPanel.EditableProperties
            .Where(static property => property.DeclaringType == typeof(RenderSettings))
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            nameof(RenderSettings.Exposure),
            nameof(RenderSettings.ResolutionScale),
            nameof(RenderSettings.ShowRawHdrSceneColor),
            nameof(RenderSettings.ToneMapper)
        ];
        Array.Sort(expected, StringComparer.Ordinal);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCaseSource(nameof(CompleteSettingTypes))]
    public void SectionCatalogCoversEveryWritableSupportedSetting(Type settingType)
    {
        string[] expected = settingType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.SetMethod?.IsPublic == true)
            .Where(static property => RenderingSettingsEditorPanel.IsSupportedSettingType(property.PropertyType))
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = RenderingSettingsEditorPanel.EditableProperties
            .Where(property => property.DeclaringType == settingType)
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void CatalogSupportsTheRendererSettingShapesUsedByTheSections()
    {
        Assert.Multiple(() =>
        {
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(bool)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(string)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(int)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(uint)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(float)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(Vector3)), Is.True);
            Assert.That(RenderingSettingsEditorPanel.IsSupportedSettingType(typeof(ToneMapper)), Is.True);
        });
    }
}
