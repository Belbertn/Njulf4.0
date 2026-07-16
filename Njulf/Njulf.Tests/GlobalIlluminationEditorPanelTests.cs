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
}
