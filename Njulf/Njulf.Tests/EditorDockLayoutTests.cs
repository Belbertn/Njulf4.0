using System.Numerics;
using Hexa.NET.ImGui;
using Njulf.Editor;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
[NonParallelizable]
public sealed unsafe class EditorDockLayoutTests
{
    // Submit the same windows, in the same order, as the editor shell without requiring a GPU scene.
    private static readonly string[] Windows =
    [
        EditorDockLayout.SceneManagementWindow,
        EditorDockLayout.HierarchyWindow,
        EditorDockLayout.InspectorWindow,
        EditorDockLayout.SceneLightsWindow,
        EditorDockLayout.RenderingSettingsWindow,
        EditorDockLayout.GlobalIlluminationWindow,
        EditorDockLayout.MaterialsWindow,
        EditorDockLayout.ShadowsWindow
    ];

    [TestCase(1280, 720)]
    [TestCase(1920, 1080)]
    public void DefaultLayout_DocksPanelsAroundClickableScene_AndFollowsResize(int width, int height)
    {
        using var host = CreateHost();
        // Old floating-window settings must not prevent the new default layout.
        ImGui.LoadIniSettingsFromMemory("""
            [Window][Hierarchy]
            Pos=60,60
            Size=312,261
            Collapsed=0

            [Window][Rendering Settings]
            Pos=60,60
            Size=675,894
            Collapsed=0

            """);
        var layout = new EditorDockLayout();
        var size = new Vector2(width, height);
        RenderFrames(host, layout, size);
        AssertDefaultLayout(size);

        host.AddMousePosition(new Vector2(width * 0.45f, height * 0.3f));
        RenderFrames(host, layout, size);
        Assert.That(host.WantCaptureMouse, Is.False, "The empty center must allow scene picking.");

        host.AddMousePosition(Window(EditorDockLayout.HierarchyWindow).Pos + new Vector2(30f, 40f));
        RenderFrames(host, layout, size);
        Assert.That(host.WantCaptureMouse, Is.True, "Docked panels must still capture mouse input.");

        size *= 0.8f;
        RenderFrames(host, layout, size);
        AssertDefaultLayout(size);
    }

    [Test]
    public void CustomizedLayout_SurvivesFramesAndContextRecreation_UntilReset()
    {
        string saved;
        var size = new Vector2(1600f, 900f);
        using (var host = CreateHost())
        {
            var layout = new EditorDockLayout();
            RenderFrames(host, layout, size);
            uint bottom = Window(EditorDockLayout.SceneManagementWindow).DockId;
            ImGuiP.DockBuilderDockWindow(EditorDockLayout.InspectorWindow, bottom);
            RenderFrames(host, layout, size);
            Assert.That(Window(EditorDockLayout.InspectorWindow).DockId, Is.EqualTo(bottom));
            saved = ImGui.SaveIniSettingsToMemoryS();
        }

        using (var host = CreateHost())
        {
            ImGui.LoadIniSettingsFromMemory(saved);
            var layout = new EditorDockLayout();
            RenderFrames(host, layout, size);
            Assert.That(Window(EditorDockLayout.InspectorWindow).DockId,
                Is.EqualTo(Window(EditorDockLayout.SceneManagementWindow).DockId),
                "An existing saved layout must take precedence over the defaults.");

            layout.RequestReset();
            RenderFrames(host, layout, size);
            AssertDefaultLayout(size);
        }
    }

    private static ImGuiEditorOverlayHost CreateHost()
    {
        var host = new ImGuiEditorOverlayHost();
        // Keep these native ImGui tests independent of the user's imgui.ini.
        ImGuiIOPtr io = ImGui.GetIO();
        io.IniFilename = null;
        host.SetEnabled(true);
        return host;
    }

    private static void RenderFrames(ImGuiEditorOverlayHost host, EditorDockLayout layout, Vector2 size)
    {
        // Dock tab selection and viewport work area settle over the first few frames.
        for (int frame = 0; frame < 4; frame++)
        {
            host.BeginFrame(size, Vector2.One, 1f / 60f);
            layout.Render();
            foreach (string name in Windows)
            {
                if (ImGui.Begin(name))
                    ImGui.Text("Panel contents");
                ImGui.End();
            }

            host.EndFrame();
        }
    }

    private static ImGuiWindowPtr Window(string name)
    {
        ImGuiWindowPtr window = ImGuiP.FindWindowByName(name);
        Assert.That(window.IsNull, Is.False, $"Missing window: {name}");
        return window;
    }

    private static void AssertDefaultLayout(Vector2 size)
    {
        ImGuiWindowPtr scene = Window(EditorDockLayout.SceneManagementWindow);
        ImGuiWindowPtr hierarchy = Window(EditorDockLayout.HierarchyWindow);
        ImGuiWindowPtr lights = Window(EditorDockLayout.SceneLightsWindow);
        ImGuiWindowPtr settings = Window(EditorDockLayout.RenderingSettingsWindow);
        Assert.Multiple(() =>
        {
            foreach (string name in Windows)
            {
                ImGuiWindowPtr window = Window(name);
                Assert.That(window.DockId, Is.Not.Zero, $"{name} should be docked.");
                Assert.That(window.Pos.X, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.Pos.Y, Is.GreaterThanOrEqualTo(0));
                Assert.That(window.Pos.X + window.Size.X, Is.LessThanOrEqualTo(size.X + 1f));
                Assert.That(window.Pos.Y + window.Size.Y, Is.LessThanOrEqualTo(size.Y + 1f));
            }

            Assert.That(scene.Pos.X, Is.EqualTo(0f).Within(1f));
            Assert.That(scene.Size.X, Is.EqualTo(size.X).Within(1f));
            Assert.That(scene.Pos.Y, Is.GreaterThan(size.Y * 0.6f));
            Assert.That(scene.Pos.Y + scene.Size.Y, Is.EqualTo(size.Y).Within(1f));
            Assert.That(hierarchy.Pos.X, Is.EqualTo(0f).Within(1f));
            Assert.That(lights.Pos.X, Is.EqualTo(hierarchy.Pos.X));
            Assert.That(lights.Pos.Y, Is.GreaterThanOrEqualTo(hierarchy.Pos.Y + hierarchy.Size.Y));
            Assert.That(lights.Pos.Y + lights.Size.Y, Is.LessThanOrEqualTo(scene.Pos.Y));
            Assert.That(settings.Pos.X, Is.GreaterThan(size.X * 0.6f));
            Assert.That(settings.Pos.X + settings.Size.X, Is.EqualTo(size.X).Within(1f));
            Assert.That(settings.Pos.Y + settings.Size.Y, Is.LessThanOrEqualTo(scene.Pos.Y));
            Assert.That(settings.DockTabIsVisible, Is.True, "Rendering Settings should be the selected right tab.");
            foreach (string name in new[] { EditorDockLayout.InspectorWindow, EditorDockLayout.GlobalIlluminationWindow,
                         EditorDockLayout.MaterialsWindow, EditorDockLayout.ShadowsWindow })
                Assert.That(Window(name).DockId, Is.EqualTo(settings.DockId), $"{name} belongs in the right tab group.");
        });
    }
}
