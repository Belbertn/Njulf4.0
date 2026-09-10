using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Graphics;
using Njulf.Rendering;

namespace NjulfGame;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            int frames = 0;
            if (args.Length != 0 && (args.Length != 2 || args[0] != "--frames" ||
                !int.TryParse(args[1], out frames) || frames <= 0))
                throw new ArgumentException("Usage: NjulfGame [--frames N], where N is positive.");
            using var game = new StarterGame(frames) { Name = "NjulfGame", WindowTitle = "NjulfGame" };
            game.Run();
            if (frames > 0 && game.PresentedFrames < frames)
                throw new InvalidOperationException("The game exited before completing the requested frames.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}

internal sealed class StarterGame(int frameLimit) : Game
{
    private InputAction _exit = null!;
    private bool _loaded;
    public int PresentedFrames { get; private set; }

    protected override void ConfigureRendering(RenderingOptions options)
    {
        // Start with direct lighting; raise the preset as the game's rendering needs grow.
        options.InitialSettings.ApplyQualityPreset(RenderQualityPreset.Low);
        options.InitialSettings.ResolutionScale = 1f;
    }

    protected override void Load()
    {
        _exit = Input.CreateAction("Exit");
        _exit.AddBinding(new InputBinding(InputKey.Escape));
        Scene.Add(new SceneLight
        {
            Type = SceneLightType.Directional,
            Direction = new Vector3(-0.5f, -1, -1).Normalized(),
            Color = Vector3.One,
            Intensity = 3
        });
        Model model = Content.Load<Model>("Assets/tetrahedron.gltf", new ContentLoadOptions { RequireCooked = true });
        Scene.Add(model.CreateInstance());
        _loaded = true;
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (_exit.WasPressed) Exit();
    }

    protected override void OnFramePresented()
    {
        if (!_loaded || Renderer is IProgressiveScenePipelinePreparer { StartupSnapshot.FullQualityPresented: false }) return;
        PresentedFrames++;
        if (frameLimit > 0 && PresentedFrames >= frameLimit) Exit();
    }
}
