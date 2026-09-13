using Njulf.Assets;
using Njulf.Audio;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Framework;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Physics;

using var game = new SphereShooterGame();
game.Run();

sealed class SphereShooterGame : Game
{
    private InputVector2Action move = null!, look = null!;
    private InputAction fire = null!, quit = null!, pause = null!, replace = null!;
    private AudioSystem? audio;
    private Playground? playground;
    private Task? replacement;

    public SphereShooterGame()
    {
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 120);
        WindowTitle = "SphereShooter — WASD/mouse: move/aim, click: fire, P: pause, R: replace, Esc: exit";
    }

    protected override void Load()
    {
        move = Input.CreateWasd("Move");
        look = Input.CreateMouseLook("Look", .002f);
        fire = Input.CreateButton("Fire", MouseButton.Left);
        quit = Input.CreateButton("Quit", InputKey.Escape);
        pause = Input.CreateButton("Pause", InputKey.P);
        replace = Input.CreateButton("Replace level", InputKey.R);
        Input.SetCursorMode(InputCursorMode.Captured);
        try { audio = new AudioSystem(); }
        catch (InvalidOperationException error) { Console.Error.WriteLine($"Audio unavailable: {error.Message}"); }
        if (audio != null) RegisterModule(new AudioHostModule(audio));
        Console.WriteLine(WindowTitle);
    }

    protected override Task LoadAsync(CancellationToken cancellationToken) => ReplaceLevelAsync(cancellationToken);

    private async Task ReplaceLevelAsync(CancellationToken cancellationToken = default)
    {
        Playground? next = null;
        await LoadLevelAsync((level, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Register immediately so partial loading is cleaned up on failure.
            next = level.RegisterModule(new Playground(this, level));
            next.Load(audio);
            return Task.CompletedTask;
        }, cancellationToken);
        playground = next;
        Camera.Position = new Vector3(0, 1.7f, 6);
        ((FirstPersonCamera)Camera).Yaw = 0;
        ((FirstPersonCamera)Camera).Pitch = 0;
        Console.WriteLine($"Level replaced; paused={IsPaused}.");
    }

    protected override void Update(GameTime time)
    {
        if (quit.WasPressed) { Exit(); return; }
        if (replacement is { IsCompleted: true })
        {
            try { replacement.GetAwaiter().GetResult(); }
            catch (Exception error) { Console.Error.WriteLine($"Level replacement failed: {error.Message}"); }
            replacement = null;
        }
        if (pause.WasPressed)
        {
            IsPaused = !IsPaused;
            Input.SetCursorMode(IsPaused ? InputCursorMode.Normal : InputCursorMode.Captured);
            Console.WriteLine(IsPaused ? "Paused." : "Resumed.");
        }
        if (replace.WasPressed && replacement == null) replacement = ReplaceLevelAsync();
        if (IsSimulationPaused || pause.WasPressed || replacement != null) return;

        var camera = (FirstPersonCamera)Camera;
        // Mouse displacement already includes elapsed motion; do not multiply by time.
        camera.RotateYawPitch(look.Value.X, look.Value.Y);
        camera.Pitch = System.Math.Clamp(camera.Pitch, -1.5f, 1.5f);
        var forward = new Vector3(camera.Forward.X, 0, camera.Forward.Z).Normalized();
        var movement = camera.Right * move.Value.X + forward * move.Value.Y;
        if (movement.LengthSquared() > 1) movement = movement.Normalized();
        camera.Position += movement * (5 * (float)time.ElapsedGameTime.TotalSeconds);
        if (fire.WasPressed) playground?.Fire(camera);
        base.Update(time);
    }

    protected override void Unload() => Input.SetCursorMode(InputCursorMode.Normal);
}

// GameLevel owns the scene, physics and audio; this module owns shared handles and shot lifetimes.
sealed class Playground(SphereShooterGame game, GameLevel level) : IGameModule
{
    private PhysicsScene physics = null!;
    private AudioScope? sounds;
    private AudioClip? impact;
    private Mesh? sphere;
    private Material? bulletMaterial;
    private readonly List<(RenderObject Body, float Life)> shots = [];
    private readonly HashSet<Guid> ballIds = [], cubeIds = [];
    public GameModulePhase Phase => GameModulePhase.Physics;

    public void Load(AudioSystem? audio)
    {
        physics = level.RegisterModule(new PhysicsHostModule(new PhysicsScene(PhysicsMode.Simulation, level.Scene))).World;
        physics.Contact += OnContact;
        if (audio != null)
        {
            sounds = level.RegisterModule(audio.CreateScope());
            impact = sounds.LoadWav(Path.Combine(game.ContentRoot, "Assets", "impact.wav"));
        }
        level.Scene.Environment = new SceneEnvironment { AtmosphereIntensity = .02f };
        level.Scene.Add(new SceneLight
        {
            Type = SceneLightType.Directional, Direction = new Vector3(-.5f, -1, -1).Normalized(),
            Color = Vector3.One, Intensity = 1
        });
        using var cube = CreateMesh(PrimitiveMesh.Box(Vector3.One));
        using var floorMaterial = game.GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(.25f, .3f, .35f, 1), MetallicFactor = 0, RoughnessFactor = .9f
        });
        using var floorMesh = CreateMesh(PrimitiveMesh.Box(new Vector3(40, 1, 40)));
        var floor = new RenderObject(floorMesh, floorMaterial) { Position = new(0, -.5f, 0) };
        level.Scene.Add(floor);
        physics.RegisterStatic(floor, ColliderShape.Box(new Vector3(40, 1, 40)));
        for (int x = -3; x <= 3; x += 3)
        for (int y = 0; y < 3; y++)
        {
            var target = new RenderObject(cube, floorMaterial) { Position = new(x, .5f + y * 1.01f, -4) };
            level.Scene.Add(target);
            cubeIds.Add(target.Id);
            physics.RegisterDynamic(target, ColliderShape.Box(Vector3.One), new BodySettings { Mass = 1, Friction = .7f });
        }
        sphere = CreateMesh(PrimitiveMesh.Sphere(1, 16, 10));
        bulletMaterial = game.GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(1, .35f, .05f, 1), MetallicFactor = 0, RoughnessFactor = .5f,
            EmissiveFactor = new Vector3(1, .2f, 0), EmissiveStrength = 2
        });
    }

    private Mesh CreateMesh(ModelMesh primitive)
    {
        var vertices = new VertexPositionNormalTexture[primitive.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = new(primitive.Vertices[i], primitive.Normals[i], primitive.TexCoords[i]);
        return game.GraphicsDevice.CreateMesh(vertices, primitive.Indices);
    }

    public void Fire(FirstPersonCamera camera)
    {
        var body = new RenderObject(sphere!, bulletMaterial!)
        {
            Position = camera.Position + camera.Forward * .6f + camera.Right * .18f - camera.Up * .12f,
            Scale = new(.12f)
        };
        level.Scene.Add(body);
        var collider = physics.RegisterDynamic(body, ColliderShape.Sphere(1),
            new BodySettings { Mass = .5f, Friction = .5f, Restitution = .35f });
        physics.SetVelocity(collider, camera.Forward * 18);
        ballIds.Add(body.Id);
        shots.Add((body, 8));
    }

    public void FixedUpdate(GameTime time)
    {
        // Registered before PhysicsHostModule: expire bodies before the host steps physics.
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var shot = shots[i];
            shot.Life -= (float)time.ElapsedGameTime.TotalSeconds;
            if (shot.Life <= 0)
            {
                ballIds.Remove(shot.Body.Id);
                level.Scene.Remove(shot.Body); // Also removes its collider.
                shots.RemoveAt(i);
            }
            else shots[i] = shot;
        }
    }

    private void OnContact(PhysicsContact contact)
    {
        if (!contact.Began || sounds == null || contact.Details is not { } hit) return;
        if ((ballIds.Contains(contact.OwnerA) && cubeIds.Contains(contact.OwnerB)) ||
            (ballIds.Contains(contact.OwnerB) && cubeIds.Contains(contact.OwnerA)))
            sounds.PlayOneShot(impact!, hit.Position);
    }

    public void Dispose()
    {
        sphere?.Dispose();
        bulletMaterial?.Dispose();
        shots.Clear(); ballIds.Clear(); cubeIds.Clear();
    }
}
