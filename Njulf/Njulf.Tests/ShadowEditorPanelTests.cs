using System.Reflection;
using Njulf.Editor;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class ShadowEditorPanelTests
{
    [Test]
    public void CatalogCoversEveryWritableScalarShadowSetting()
    {
        string[] expected = GetWritableScalarPropertyNames<ShadowSettings>();
        string[] actual = ShadowEditorPanel.EditableShadowProperties
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(expected.Length, Is.GreaterThan(20));
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual, Does.Contain(nameof(ShadowSettings.MaxShadowDistance)));
            Assert.That(actual, Does.Contain(nameof(ShadowSettings.ForceStaticCascadeCacheRefresh)));
            Assert.That(actual, Does.Contain(nameof(ShadowSettings.DirectionalShadowPreviewCascade)));
            Assert.That(ShadowEditorPanel.IsSupportedScalarType(typeof(ShadowDebugView)), Is.True);
            Assert.That(ShadowEditorPanel.IsSupportedScalarType(typeof(DirectionalShadowMode)), Is.True);
            Assert.That(actual, Does.Contain(nameof(ShadowSettings.RequestedDirectionalShadowMode)));
            Assert.That(actual, Does.Contain(nameof(ShadowSettings.DirectionalCascadeSplitLambda)));
        });
    }

    [Test]
    public void CatalogCoversEveryWritableScalarSceneSubmissionSetting()
    {
        string[] expected = GetWritableScalarPropertyNames<SceneSubmissionSettings>();
        string[] actual = ShadowEditorPanel.EditableSceneSubmissionProperties
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual, Does.Contain(nameof(SceneSubmissionSettings.GpuCompactionEnabled)));
            Assert.That(actual, Does.Contain(nameof(SceneSubmissionSettings.IndirectMeshletDispatchEnabled)));
            Assert.That(actual, Does.Contain(nameof(SceneSubmissionSettings.GpuShadowCompactionEnabled)));
        });
    }

    [Test]
    public void DedicatedReceiverCounterSettingDefaultsOff()
    {
        Assert.That(new RenderDiagnosticsSettings().DirectionalShadowReceiverCountersEnabled, Is.False);
    }

    private static string[] GetWritableScalarPropertyNames<T>() => typeof(T)
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(static property => property.SetMethod?.IsPublic == true)
        .Where(static property => ShadowEditorPanel.IsSupportedScalarType(property.PropertyType))
        .Select(static property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();
}
