using Njulf.Framework;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Input;
using Njulf.Graphics;
using Njulf.Rendering;
#if (physics)
using Njulf.Physics;
#endif
#if (audio)
using Njulf.Audio;
#endif

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
#if (audio)
    private InputAction _playSound = null!;
    private AudioScope? _sounds;
    private AudioClip? _impact;
#endif
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
        // Keep the visible sky and ambient illumination at this small scene's light level.
        Scene.Environment = new SceneEnvironment { AtmosphereIntensity = .02f };
        Scene.Add(new SceneLight
        {
            Type = SceneLightType.Directional,
            Direction = new Vector3(-0.5f, -1, -1).Normalized(),
            Color = Vector3.One,
            Intensity = 1
        });
        Model model = Content.Load<Model>("Assets/tetrahedron.gltf", new ContentLoadOptions { RequireCooked = true });
        var instance = model.CreateInstance();
        Scene.Add(instance);
#if (physics)
        IsFixedTimeStep = true;
        var world = RegisterModule(new PhysicsHostModule(new PhysicsScene(PhysicsMode.Simulation, Scene))).World;
        instance.PlacementRoot.Position = new Vector3(0, 2, 0);
        world.RegisterDynamic(instance, ColliderShape.Box(Vector3.One));
        // An invisible static floor catches the object at y = -1.
        world.Register(Guid.NewGuid(), [ColliderShape.Box(new Vector3(20, 1, 20))],
            pose: new PhysicsPose(new Vector3(0, -1.5f, 0)));
#endif
#if (audio)
        _playSound = Input.CreateButton("Play sound", InputKey.Space);
        AudioSystem? audio = null;
        try { audio = new AudioSystem(); }
        catch (InvalidOperationException error) { Console.Error.WriteLine($"Audio unavailable: {error.Message}"); }
        if (audio != null)
        {
            RegisterModule(new AudioHostModule(audio));
            _sounds = RegisterModule(audio.CreateScope());
            _impact = _sounds.LoadWav(Path.Combine(ContentRoot, "Assets", "impact.wav"));
        }
#endif
        _loaded = true;
    }

    protected override void Update(GameTime gameTime)
    {
        base.Update(gameTime);
        if (_exit.WasPressed) Exit();
#if (audio)
        if (_playSound.WasPressed && _sounds != null) _sounds.PlayOneShot(_impact!, Camera.Position);
#endif
    }

    protected override void OnFramePresented()
    {
        if (!_loaded || Renderer is IProgressiveScenePipelinePreparer { StartupSnapshot.FullQualityPresented: false }) return;
        PresentedFrames++;
        if (frameLimit > 0 && PresentedFrames >= frameLimit) Exit();
    }
}
