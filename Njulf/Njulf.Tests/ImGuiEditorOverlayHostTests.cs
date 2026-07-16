using System.Numerics;
using Hexa.NET.ImGui;
using Njulf.Editor;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed class ImGuiEditorOverlayHostTests
{
    [Test]
    public void FirstFrameUsesRendererManagedFontAtlasTexture()
    {
        using var host = new ImGuiEditorOverlayHost();

        Assert.That(
            ImGui.GetIO().BackendFlags.HasFlag(ImGuiBackendFlags.RendererHasTextures),
            Is.True);

        host.BeginFrame(new Vector2(1280f, 720f), Vector2.One, 1f / 60f);
        ImGui.Text("First frame");
        ImDrawDataPtr drawData = host.EndFrame();

        Assert.That(drawData.IsNull, Is.False);
        Assert.That(drawData.Valid, Is.True);
    }

    [Test]
    public void CaptureFlagsAreFalseAfterDisposal()
    {
        var host = new ImGuiEditorOverlayHost();
        host.SetEnabled(true);

        host.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(host.WantCaptureMouse, Is.False);
            Assert.That(host.WantCaptureKeyboard, Is.False);
        });
    }
}
