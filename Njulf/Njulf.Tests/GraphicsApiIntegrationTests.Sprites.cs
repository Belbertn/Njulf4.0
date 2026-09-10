using Hexa.NET.ImGui;
using Microsoft.Extensions.DependencyInjection;
using Njulf.Assets;
using Njulf.Assets.Scenes;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Editor;
using Njulf.Graphics;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

public sealed partial class GraphicsApiIntegrationTests
{
    [Test]
    public void SpriteOverlaySupportsClearOnlyFramesTextureRetirementEditorAndResize()
    {
        using var batch = _graphics.CreateSpriteBatch();
        using var editor = new ImGuiEditorOverlayHost();
        editor.SetEnabled(true);
        for (int frame = 0; frame < 6; frame++)
        {
            if (frame == 3) { _window.Size = new(96, 80); _window.DoEvents(); _renderer.Resize(96, 80); }
            if (!_renderer.BeginFrame()) { _window.DoEvents(); continue; }
            _renderer.SetSpriteDisplaySize(new(48, 40));
            _renderer.Clear(Color.Blue);
            var texture = _graphics.CreateTexture2D(1, 1, [255, 255, 255, 128], TextureColorSpace.Srgb);
            batch.Begin(SpriteSampler.PointClamp);
            batch.PushClip(new(2, 2, 40, 35));
            batch.Draw(texture, new(0, 0), Color.Red, new Vector2(30, 30));
            texture.Dispose();
            batch.PopClip(); batch.End();
            editor.BeginFrame(new(48, 40), new(2, 2), 1f / 60);
            ImGui.GetBackgroundDrawList().AddRectFilled(new(20, 20), new(25, 25), 0xff00ff00);
            editor.SubmitFrame(_renderer);
            _renderer.EndFrame();
        }
        Assert.That(_context.ValidationMessageSnapshot.ErrorCount, Is.Zero);
    }

    [Test]
    public void BitmapFontContentScopesShareFontAndKeepAtlasUntilLastOwner()
    {
        using var content = new ContentManager(Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "Sprites")) { GraphicsDeviceProvider = () => _graphics };
        using var first = content.CreateScope(); using var second = content.CreateScope();
        SpriteFont font = first.Load<SpriteFont>("sample.njfont.json");
        Assert.That(second.Load<SpriteFont>("sample.njfont.json"), Is.SameAs(font));
        first.Dispose(); Assert.That(font.IsDisposed, Is.False);
        Assert.That(font.MeasureString("HUD").X, Is.GreaterThan(0));
        second.Dispose(); Assert.That(font.IsDisposed, Is.True);
        Assert.Throws<ObjectDisposedException>(() => font.MeasureString("HUD"));
    }

    [Test]
    public void GizmoMoveCancelCommitAndSelectionChangePreserveObjectTrsAndPersistence()
    {
        using var template = new Model(); using var scene = new Scene();
        var target = new RenderObject { Position = Vector3.Zero, Rotation = new Quaternion(new Vector3(0, 0, .4f)), Scale = new(-2, 3, 4), AssetReference = new() { Path = "test.glb", SubObject = "0" } };
        scene.Add(target);
        var camera = new FirstPersonCamera(new(0, 0, 5)) { FieldOfView = MathF.PI / 2, AspectRatio = 1 };
        var editor = new EditorController(scene, new EditorModelContent(template), _services.GetRequiredService<LightManager>(), _materials, camera: camera);
        editor.SetEnabled(true); editor.SelectEntity(EditorSelectionKind.Object, target.Id);
        var original = GizmoTransform.Read(target);
        void Update(float x, bool down, bool cancel = false) => editor.Gizmos.Update(editor, camera, new(200, 200), new(x, 100), down, true, true, cancel);
        Update(140, true); Assert.That(editor.Gizmos.IsDragging, Is.True);
        Assert.That(editor.TryPick(camera, new(140, 100), new(200, 200)), Is.False);
        Update(160, true); Assert.That(target.Position.X, Is.EqualTo(1).Within(.002));
        Assert.That(editor.IsDirty, Is.False);
        Update(160, false, true); Assert.That(GizmoTransform.Read(target), Is.EqualTo(original));
        Update(140, true); Update(160, true); Update(160, false);
        Assert.That(editor.IsDirty, Is.True);
        Assert.That(target.Rotation, Is.EqualTo(original.Rotation)); Assert.That(target.Scale, Is.EqualTo(original.Scale));
        SceneObjectDocument saved = new SceneDocumentWriter().CreateDocument(scene).Objects.Single();
        Assert.That(saved.Position.X, Is.EqualTo(1).Within(.002));
        var committed = GizmoTransform.Read(target);
        Update(160, true); Update(175, true); editor.SetEnabled(false);
        Assert.That(GizmoTransform.Read(target), Is.EqualTo(committed));
        Assert.That(editor.Gizmos.IsDragging, Is.False);
    }

    [Test]
    public void GizmoRotationAndLocalScalingDragFromOriginalTransform()
    {
        using var template = new Model(); using var scene = new Scene();
        var target = new RenderObject { Scale = new(-2, 3, 4) }; scene.Add(target);
        var camera = new FirstPersonCamera(new(0, 0, 5)) { FieldOfView = MathF.PI / 2, AspectRatio = 1 };
        var editor = new EditorController(scene, new EditorModelContent(template), _services.GetRequiredService<LightManager>(), _materials, camera: camera);
        editor.SetEnabled(true); editor.SelectEntity(EditorSelectionKind.Object, target.Id);
        void Update(float x, float y, bool down, bool focused = true) => editor.Gizmos.Update(editor, camera, new(200, 200), new(x, y), down, true, focused, false);
        editor.Gizmos.Mode = GizmoMode.Rotate;
        float offset = 80 / MathF.Sqrt(2);
        Update(100 + offset, 100 + offset, true); Assert.That(editor.Gizmos.IsDragging, Is.True);
        Update(100 + offset, 100 - offset, true); Update(100 + offset, 100 - offset, false);
        var rotation = new System.Numerics.Quaternion(target.Rotation.X, target.Rotation.Y, target.Rotation.Z, target.Rotation.W);
        var rotatedX = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX, rotation);
        Assert.That(rotatedX.X, Is.EqualTo(0).Within(.002)); Assert.That(rotatedX.Y, Is.EqualTo(1).Within(.002));
        Assert.That(target.Scale, Is.EqualTo(new Vector3(-2, 3, 4)));
        editor.Gizmos.Mode = GizmoMode.Scale; // local X now points up the screen
        Update(100, 60, true); Assert.That(editor.Gizmos.IsDragging, Is.True);
        Update(100, 40, true); Update(100, 40, false);
        Assert.That(target.Scale.X, Is.LessThan(-2)); Assert.That(target.Scale.Y, Is.EqualTo(3)); Assert.That(target.Scale.Z, Is.EqualTo(4));
        var beforeUniform = GizmoTransform.Read(target);
        Update(100, 100, true); Update(120, 80, true); Assert.That(target.Scale.Y, Is.GreaterThan(3));
        Update(120, 80, true, false); // focus loss cancels the complete preview
        Assert.That(GizmoTransform.Read(target), Is.EqualTo(beforeUniform));
    }

    [Test]
    public void SpriteBatchRejectsCommandsFromAnUnfinishedPreviousFrame()
    {
        using var batch = _graphics.CreateSpriteBatch();
        Assert.That(_renderer.BeginFrame(), Is.True);
        batch.Begin(); batch.FillRectangle(new(0, 0, 5, 5), Color.Red);
        _renderer.EndFrame();
        Assert.That(_renderer.BeginFrame(), Is.True);
        Assert.Throws<InvalidOperationException>(() => batch.End());
        batch.Begin(); batch.FillRectangle(new(0, 0, 5, 5), Color.Green); batch.End();
        _renderer.EndFrame();
    }
}
