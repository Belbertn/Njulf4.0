using Njulf.Editor;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class MaterialEditorPanelTests
{
    [Test]
    public void InteractiveCatalogIncludesEveryNonCaptureMaterialView()
    {
        MaterialDebugView[] expected = Enum.GetValues<MaterialDebugView>()
            .Where(static view => !MaterialDebugViewPolicy.IsLinearDirectCapture(view))
            .ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(MaterialEditorPanel.InteractiveDebugViews, Is.EqualTo(expected));
            Assert.That(
                MaterialEditorPanel.InteractiveDebugViews,
                Does.Contain(MaterialDebugView.MaterialOcclusion));
            Assert.That(
                MaterialEditorPanel.InteractiveDebugViews,
                Does.Contain(MaterialDebugView.CanonicalDiffuseReflectance));
            Assert.That(
                MaterialEditorPanel.InteractiveDebugViews,
                Does.Not.Contain(MaterialDebugView.CaptureLinearDirectDiffuse));
            Assert.That(
                MaterialEditorPanel.InteractiveDebugViews,
                Does.Not.Contain(MaterialDebugView.CaptureLinearDirectSpecular));
        });
    }
}
