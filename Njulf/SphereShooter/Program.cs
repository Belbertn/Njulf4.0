using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Physics;
using Njulf.Audio;

using var game = new SphereShooterGame();
game.Run();

sealed class SphereShooterGame : Game
{
    private InputVector2Action move = null!, look = null!;
    private InputAction fire = null!, quit = null!;
    private Mesh sphere = null!;
    private Material bulletMaterial = null!;
    private readonly List<(RenderObject Body, float Life)> shots = [];
    private readonly HashSet<Guid> ballIds = [], cubeIds = [];
    private PhysicsScene physics = null!;
    private AudioSystem? audio;
    private readonly List<AudioSource> hitSounds = [];
    private int nextHitSound;

    public SphereShooterGame()
    {
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 120);
    }

    protected override void Load()
    {
        physics = new PhysicsScene(PhysicsMode.Simulation, Scene);
        physics.Contact += OnContact;
        try { audio = new AudioSystem(); }
        catch (InvalidOperationException exception)
        {
            Console.Error.WriteLine($"Audio unavailable: {exception.Message}");
        }
        if (audio != null)
        {
            // PCM16 copy of the supplied float WAV, which the audio loader cannot decode.
            var clip = audio.LoadWav(Path.Combine(AppContext.BaseDirectory, "Assets", "hit-sound.pcm16.wav"));
            for (int i = 0; i < 16; i++)
                hitSounds.Add(audio.CreateSource(clip, spatial: clip.Channels == 1));
        }
        Camera.Position = new Vector3(0, 1.7f, 6);
        move = Input.CreateVector2Action("Move");
        move.AddBinding(new(new InputBinding(InputKey.A), new InputBinding(InputKey.D),
            new InputBinding(InputKey.S), new InputBinding(InputKey.W)));
        look = Input.CreateVector2Action("Look", mode: InputValueMode.Delta);
        look.AddBinding(InputVector2Binding.MouseMotion(scale: 0.002f));
        fire = Input.CreateAction("Fire");
        fire.AddBinding(new InputBinding(MouseButton.Left));
        quit = Input.CreateAction("Quit");
        quit.AddBinding(new InputBinding(InputKey.Escape));
        Input.SetCursorMode(InputCursorMode.Captured);

        Scene.Add(new SceneLight
        {
            Type = SceneLightType.Directional,
            Direction = new Vector3(-0.5f, -1, -1).Normalized(),
            Color = Vector3.One, Intensity = 3
        });

        using var floorMesh = GraphicsDevice.CreateMesh(
            new VertexPositionNormalTexture[]
            {
                new(new(-20, 0, -20), Vector3.UnitY, new(0, 0)),
                new(new(-20, 0,  20), Vector3.UnitY, new(0, 1)),
                new(new( 20, 0,  20), Vector3.UnitY, new(1, 1)),
                new(new( 20, 0, -20), Vector3.UnitY, new(1, 0))
            }, [0u, 1u, 2u, 0u, 2u, 3u]);
        using var floorMaterial = GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(0.25f, 0.3f, 0.35f, 1),
            MetallicFactor = 0, RoughnessFactor = 0.9f
        });
        var floor = new RenderObject(floorMesh, floorMaterial);
        Scene.Add(floor);
        physics.Register(floor.Id, [ColliderShape.Box(new Vector3(40, 1, 40))],
            pose: new PhysicsPose(new Vector3(0, -.5f, 0)));

        sphere = CreateSphere();
        bulletMaterial = GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(1, 0.35f, 0.05f, 1),
            MetallicFactor = 0, RoughnessFactor = 0.5f,
            EmissiveFactor = new Vector3(1, 0.2f, 0), EmissiveStrength = 2
        });
        using var cube = CreateCube();
        // Small stacks can topple and scatter when struck by a ball.
        for (int x = -3; x <= 3; x += 3)
        for (int y = 0; y < 3; y++)
        {
            var target = new RenderObject(cube, floorMaterial)
            {
                Position = new Vector3(x, .5f + y * 1.01f, -4)
            };
            Scene.Add(target);
            cubeIds.Add(target.Id);
            physics.Register(target.Id, [ColliderShape.Box(Vector3.One)],
                node: target.Node, entity: target,
                body: new BodySettings { Kind = BodyKind.Dynamic, Mass = 1, Friction = .7f });
        }
    }

    protected override void Update(GameTime time)
    {
        if (quit.WasPressed) { Exit(); return; }
        float dt = (float)time.ElapsedGameTime.TotalSeconds;
        var camera = (FirstPersonCamera)Camera;
        // Mouse delta is already a displacement: do not multiply it by dt.
        camera.RotateYawPitch(-look.Value.X, -look.Value.Y);
        camera.Pitch = System.Math.Clamp(camera.Pitch, -1.5f, 1.5f);
        var forward = new Vector3(camera.Forward.X, 0, camera.Forward.Z).Normalized();
        var movement = camera.Right * move.Value.X + forward * move.Value.Y;
        if (movement.LengthSquared() > 1) movement = movement.Normalized();
        camera.Position += movement * (5 * dt); // Five scene units per second.

        if (fire.WasPressed)
        {
            var body = new RenderObject(sphere, bulletMaterial)
            {
                // Offset the muzzle slightly so the sphere is visible as it leaves.
                Position = camera.Position + camera.Forward * 0.6f
                    + camera.Right * 0.18f - camera.Up * 0.12f,
                Scale = new Vector3(0.12f)
            };
            Scene.Add(body);
            var collider = physics.Register(body.Id, [ColliderShape.Sphere(1)],
                node: body.Node, entity: body,
                body: new BodySettings { Kind = BodyKind.Dynamic, Mass = .5f,
                    Friction = .5f, Restitution = .35f });
            physics.SetVelocity(collider, camera.Forward * 18);
            ballIds.Add(body.Id);
            shots.Add((body, 8));
        }
        audio?.SetListener(camera.Position, camera.Forward, camera.Up);
        audio?.Update((float)time.UnscaledElapsedGameTime.TotalSeconds);
        base.Update(time);
    }

    protected override void FixedUpdate(GameTime time)
    {
        base.FixedUpdate(time);
        float dt = (float)time.ElapsedGameTime.TotalSeconds;
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var shot = shots[i];
            shot.Life -= dt;
            if (shot.Life <= 0)
            {
                ballIds.Remove(shot.Body.Id);
                Scene.Remove(shot.Body); // Removes its physics registration as well.
                shots.RemoveAt(i);
            }
            else shots[i] = shot;
        }
        physics.Step(dt);
    }

    private void OnContact(PhysicsContact contact)
    {
        if (!contact.Began || hitSounds.Count == 0) return;
        bool ballIsA = ballIds.Contains(contact.OwnerA) && cubeIds.Contains(contact.OwnerB);
        bool ballIsB = ballIds.Contains(contact.OwnerB) && cubeIds.Contains(contact.OwnerA);
        if (!ballIsA && !ballIsB) return;
        var sound = hitSounds.FirstOrDefault(source => source.State != AudioPlaybackState.Playing)
            ?? hitSounds[nextHitSound++ % hitSounds.Count];
        sound.Position = physics.GetPose(ballIsA ? contact.A : contact.B).Position;
        sound.Play();
    }

    private Mesh CreateCube()
    {
        Vector3[] corners = [new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f),
            new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f), new(-.5f,-.5f,.5f),
            new(.5f,-.5f,.5f), new(.5f,.5f,.5f), new(-.5f,.5f,.5f)];
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<uint>();
        void Face(int a, int b, int c, int d, Vector3 normal)
        {
            uint start = (uint)vertices.Count;
            vertices.Add(new(corners[a], normal, new(0, 0)));
            vertices.Add(new(corners[b], normal, new(1, 0)));
            vertices.Add(new(corners[c], normal, new(1, 1)));
            vertices.Add(new(corners[d], normal, new(0, 1)));
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        Face(0, 3, 2, 1, -Vector3.UnitZ); Face(4, 5, 6, 7, Vector3.UnitZ);
        Face(0, 1, 5, 4, -Vector3.UnitY); Face(3, 7, 6, 2, Vector3.UnitY);
        Face(0, 4, 7, 3, -Vector3.UnitX); Face(1, 2, 6, 5, Vector3.UnitX);
        return GraphicsDevice.CreateMesh(vertices.ToArray(), indices.ToArray());
    }

    // Unit-radius UV sphere, shared by every projectile.
    private Mesh CreateSphere()
    {
        const int rings = 10, slices = 16;
        var vertices = new List<VertexPositionNormalTexture>();
        var indices = new List<uint>();
        for (int y = 0; y <= rings; y++)
        for (int x = 0; x <= slices; x++)
        {
            float v = (float)y / rings, u = (float)x / slices;
            float latitude = v * MathF.PI, longitude = u * MathF.Tau;
            var normal = new Vector3(MathF.Sin(latitude) * MathF.Cos(longitude),
                MathF.Cos(latitude), MathF.Sin(latitude) * MathF.Sin(longitude));
            vertices.Add(new(normal, normal, new Vector2(u, v)));
        }
        for (int y = 0; y < rings; y++)
        for (int x = 0; x < slices; x++)
        {
            uint a = (uint)(y * (slices + 1) + x), b = a + slices + 1;
            if (y > 0) indices.AddRange([a, a + 1, b]);
            if (y < rings - 1) indices.AddRange([a + 1, b + 1, b]);
        }
        return GraphicsDevice.CreateMesh(vertices.ToArray(), indices.ToArray());
    }

    protected override void Unload()
    {
        Input.SetCursorMode(InputCursorMode.Normal);
        physics?.Dispose();
        audio?.Dispose();
        sphere?.Dispose();
        bulletMaterial?.Dispose();
        // Game disposes Scene; each object retains its own mesh/material references.
    }
}
