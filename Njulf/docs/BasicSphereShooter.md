# Basic sphere shooter

Build a small first-person playground in Njulf: **WASD** walks, the **mouse** aims,
**left click** fires a little sphere, and **Escape** exits. The camera is the player;
movement stays at eye height. Projectiles fly straight and expire after three seconds.
This starter has no collision detection, gravity, or damage.

## 1. Create the project

Use the repository's working .NET 10 and Vulkan development setup (see
[build configurations](BuildConfigurations.md) and [shader build prerequisites](ShaderBuild.md)).
From the repository root, create a folder named `SphereShooter` containing
`SphereShooter.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Njulf.Framework\Njulf.Framework.csproj" />
  </ItemGroup>
</Project>
```

## 2. Add the game

Save the following as `SphereShooter/Program.cs`. Everything is procedural, so no
models or textures need importing. `Game` supplies the window, camera, input,
renderer, and scene; its default `Draw` renders the scene.

```csharp
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Input;

using var game = new SphereShooterGame();
game.Run();

sealed class SphereShooterGame : Game
{
    private InputVector2Action move = null!, look = null!;
    private InputAction fire = null!, quit = null!;
    private Mesh sphere = null!;
    private Material bulletMaterial = null!;
    private readonly List<(RenderObject Body, Vector3 Velocity, float Life)> shots = [];

    protected override void Load()
    {
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
        Scene.Add(new RenderObject(floorMesh, floorMaterial));

        sphere = CreateSphere();
        bulletMaterial = GraphicsDevice.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(1, 0.35f, 0.05f, 1),
            MetallicFactor = 0, RoughnessFactor = 0.5f,
            EmissiveFactor = new Vector3(1, 0.2f, 0), EmissiveStrength = 2
        });
        // A few stationary spheres give movement and aiming a visual reference.
        for (int x = -3; x <= 3; x += 3)
            Scene.Add(new RenderObject(sphere, floorMaterial)
            {
                Position = new Vector3(x, 0.6f, -4), Scale = new Vector3(0.6f)
            });
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

        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var shot = shots[i];
            shot.Life -= dt;
            if (shot.Life <= 0)
            {
                Scene.Remove(shot.Body); // Also disposes the scene-owned object.
                shots.RemoveAt(i);
                continue;
            }
            shot.Body.Position += shot.Velocity * dt;
            shots[i] = shot;
        }

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
            shots.Add((body, camera.Forward * 12, 3));
        }
        base.Update(time);
    }

    // Unit-radius UV sphere, shared by every projectile and landmark.
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
        sphere?.Dispose();
        bulletMaterial?.Dispose();
        // Game disposes Scene; each object retains its own mesh/material references.
    }
}
```

## 3. Run and experiment

From the repository root:

```powershell
dotnet run --project SphereShooter -c Development
```

Walk around the grey spheres, aim, and click to shoot orange spheres. Shots pass
through the landmarks in this version. Change `5` for walking speed, `12` for shot
speed, `0.12f` for shot radius, or `3` for lifetime. `WasPressed` fires once per
click; for automatic fire, use `IsDown` with a cooldown to limit the firing rate.

The essential loop is **read input → move the camera → advance/remove shots →
spawn a shot**. Meshes and materials are created once and shared; only projectile
objects are created when firing. Next, add segment-versus-sphere hit detection
between each shot's old and new positions so fast shots cannot skip targets.
See [gameplay input](GameplayInput.md) and [game timing](GameTiming.md) for more.
