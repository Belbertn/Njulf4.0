using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;

namespace Njulf.ApiExamples;

internal sealed class SpritesExample(ExampleOptions options) : ExampleGame(options)
{
    private SpriteFont _font = null!;
    private Texture _checker = null!;
    private int _loadingFrames;

    protected override void Initialize()
    {
        _font = Content.Load<SpriteFont>("Assets/Content/Sprites/sample.njfont.json");
        _checker = Graphics.CreateTexture2D(2, 2,
            [255, 120, 40, 255, 40, 160, 255, 255, 40, 160, 255, 255, 255, 120, 40, 255], TextureColorSpace.Srgb);
        DebugDraw.Enabled = true;
    }

    protected override async Task LoadAsync(CancellationToken cancellationToken)
    {
        Model template = await Content.LoadAsync<Model>("Assets/tetrahedron.gltf", cancellationToken: cancellationToken);
        Scene.Add(template.CreateInstance());
    }

    protected override void DrawLoading(GameTime gameTime)
    {
        Renderer.Clear(new Color(.015f, .025f, .04f));
        Sprites.Begin();
        Sprites.DrawString(_font, "Loading scene and shaders...", new(40, 48), Color.White);
        Sprites.FillRectangle(new(40, 102, 320, 8), new Color(.04f, .07f, .12f));
        float x = (float)(gameTime.UnscaledTotalGameTime.TotalSeconds % 2) * 130;
        Sprites.FillRectangle(new(40 + x, 102, 60, 8), Color.Cyan);
        Sprites.End();
        _loadingFrames++;
    }

    protected override void Draw(GameTime gameTime)
    {
        DebugDraw.Box(new BoundingBox(new(-1.1f), new(1.1f)), Color.Yellow);
        base.Draw(gameTime);
        // Submitted after DrawScene: the HUD must still appear in this frame.
        Sprites.Begin(SpriteSampler.PointClamp);
        Sprites.FillRectangle(new(18, 18, 424, 248), new Color(.012f, .02f, .04f, .92f));
        Sprites.DrawString(_font, "Sprites, text and diagnostics", new(30, 25), Color.White);
        Sprites.Draw(_checker, new(40, 85), Color.White, new Vector2(104, 104));
        Sprites.Draw(_checker, new(165, 138), new Color(1, 1, 1, .6f), new Vector2(100, 100),
            rotation: .35f, origin: new(50, 50), flip: SpriteFlip.Horizontal);
        Sprites.PushClip(new(225, 90, 192, 80));
        Sprites.DrawString(_font, "Clipped multiline text\nwith a fallback: \u2603", new(225, 90), Color.Cyan);
        Sprites.PopClip();
        Sprites.DrawRectangle(new(225, 90, 192, 80), Color.White);
        Sprites.DrawLine(new(30, 214), new(424, 214), Color.Yellow, 2);
        Sprites.DrawString(_font, "Top-left pixels; HUD after scene", new(30, 220), Color.White, .85f);
        var viewport = new SpriteRectangle(0, 0, Window.Size.X, Window.Size.Y);
        if (ScreenProjection.TryProject(Camera, new Vector3(0, 1.5f, 0), viewport, out Vector2 label))
            Sprites.DrawString(_font, "World label", label, Color.Yellow);
        Sprites.End();
    }

    protected override void Unload()
    {
        _checker?.Dispose();
        Console.WriteLine($"Sprite loading frames: {_loadingFrames}");
        base.Unload();
    }
}
