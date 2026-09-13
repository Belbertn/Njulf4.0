using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Physics;

namespace Njulf.ApiExamples;

internal sealed class PhysicsExample : ExampleGame
{
    private PhysicsScene _physics = null!;
    private ColliderHandle _platform;
    private readonly List<ColliderHandle> _boxes = [];
    private InputAction _pause = null!, _slow = null!, _interact = null!, _debug = null!;
    private readonly Guid _player = Guid.NewGuid();
    private readonly bool _simulation;
    private RaycastHit _hit;
    private bool _hasHit;
    private int _queryHits;
    private bool _stackToppled;
    private double _simulationSeconds;
    private CharacterController? _character;
    private InputVector2Action _move = null!;
    private InputAction _jump = null!, _resetCharacter = null!;
    private int _checkpointEntries;
    private bool _autoJumped;

    public PhysicsExample(ExampleOptions options) : base(options)
    {
        _simulation = options.Example == "physics-simulation";
        IsFixedTimeStep = _simulation;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 60);
        MaximumFramesPerSecond = 90;
    }
    protected override void Load()
    {
        base.Load();
        _physics = new(_simulation ? PhysicsMode.Simulation : PhysicsMode.QueryOnly, Scene);
        RegisterModule(new PhysicsHostModule(_physics));
        Camera.Position = new(8, 6, 12);
        var camera = (Njulf.Core.Camera.FirstPersonCamera)Camera;
        camera.Yaw = -MathF.Atan2(8, 12); camera.Pitch = MathF.Atan2(5, MathF.Sqrt(208));
        AddBox(new(0, -.5f, 0), new(20, 1, 14), new(.3f, .35f, .4f, 1), BodyKind.Static);
        if (_simulation)
        {
            for (int i = 0; i < 4; i++) _boxes.Add(AddBox(new(-2, 1 + i * 1.1f, 0), Vector3.One, new(.9f, .4f, .1f, 1), BodyKind.Dynamic));
            _platform = AddBox(new(2, .4f, 0), new(3, .5f, 3), new(.1f, .5f, .9f, 1), BodyKind.Kinematic);
            _boxes.Add(AddBox(new(2, 1.6f, 0), Vector3.One, new(.8f, .6f, .1f, 1), BodyKind.Dynamic));
            AddBox(new(-3, .1f, 3), new(1, .2f, 2), new(.5f, .5f, .5f, 1), BodyKind.Static);
            AddBox(new(-2, .2f, 3), new(1, .4f, 2), new(.5f, .5f, .5f, 1), BodyKind.Static);
            AddBox(new(1, .015f, 3), new(2, .03f, 2), new(.1f, .8f, .3f, 1), BodyKind.Static);
            var checkpoint = _physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(2, 2, 2))], new PhysicsPose(new(1, 1, 3)),
                new BodySettings { IsTrigger = true });
            _character = new(_physics, _player, new(-5, .92f, 3));
            var capsule = PrimitiveMesh.Capsule(_character.Settings.Radius, _character.Settings.Length, tangents: true);
            var vertices = capsule.Vertices.Select((p, i) => new VertexPositionNormalTextureTangent(p, capsule.Normals[i], capsule.TexCoords[i],
                new(capsule.Tangents[i], Vector3.Dot(Vector3.Cross(capsule.Normals[i], capsule.Tangents[i]), capsule.Bitangents[i]) < 0 ? -1 : 1))).ToArray();
            using var mesh = Graphics.CreateMesh(vertices, capsule.Indices);
            using var material = Graphics.CreateMaterial(MaterialDefinition.Default with { BaseColorFactor = new(.7f, .2f, .8f, 1) });
            var visual = Graphics.CreateRenderObject(mesh, material); Scene.Add(visual);
            _physics.BindPresentation(_character.Collider, visual.Node);
            _physics.Trigger += e =>
            {
                if (e.Entered && (e.A == checkpoint && e.B == _character.Collider || e.B == checkpoint && e.A == _character.Collider))
                { _checkpointEntries++; Console.WriteLine("Character entered checkpoint."); }
            };
            _physics.Contact += e =>
            {
                if (e.Details is { ClosingSpeed: > 1.5f } impact)
                    Console.WriteLine($"Impact at {impact.Position}, normal {impact.Normal}, closing speed {impact.ClosingSpeed:F2}m/s.");
            };
            _move = Input.CreateVector2Action("Character move");
            _move.AddBinding(new(new InputBinding(InputKey.Left), new InputBinding(InputKey.Right), new InputBinding(InputKey.Down), new InputBinding(InputKey.Up)));
            _jump = Input.CreateButton("Character jump", InputKey.Enter, bufferPresses: true);
            _resetCharacter = Input.CreateButton("Reset character", InputKey.R, bufferPresses: true);
        }
        else
        {
            AddBox(new(-2, 1, 0), new(1, 2, 1), new(.9f, .4f, .1f, 1), BodyKind.Static);
            AddBox(new(0, 1, 0), new(1, 2, 1), new(.2f, .8f, .4f, 1), BodyKind.Static);
            AddBox(new(2, 1, 0), new(1, 2, 1), new(.1f, .5f, .9f, 1), BodyKind.Static);
        }
        _pause = Bind("Pause physics", InputKey.P); _slow = Bind("Half speed", InputKey.T);
        _interact = Bind("Interact / impulse", InputKey.Space); _debug = Bind("Physics bounds", InputKey.B);
        Console.WriteLine("Aim with the camera. Space: inspect hit / push dynamic body. P: pause. T: half speed. B: debug bounds and ray.");
        if (_simulation) Console.WriteLine("Arrows: move purple capsule. Enter: jump. R: reset. Green pad: non-blocking checkpoint. Finite runs drive the character automatically.");
    }
    private InputAction Bind(string name, InputKey key)
    {
        var action = Input.CreateAction(name); action.AddBinding(new InputBinding(key)); return action;
    }
    private ColliderHandle AddBox(Vector3 position, Vector3 size, Vector4 color, BodyKind kind)
    {
        var primitive = PrimitiveMesh.Box(size, tangents: true);
        var vertices = new VertexPositionNormalTextureTangent[primitive.Vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            float handedness = Vector3.Dot(Vector3.Cross(primitive.Normals[i], primitive.Tangents[i]), primitive.Bitangents[i]) < 0 ? -1 : 1;
            vertices[i] = new(primitive.Vertices[i], primitive.Normals[i], primitive.TexCoords[i], new(primitive.Tangents[i], handedness));
        }
        using var mesh = Graphics.CreateMesh(vertices, primitive.Indices);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default with { BaseColorFactor = color, RoughnessFactor = .7f });
        var visual = Graphics.CreateRenderObject(mesh, material); visual.Position = position; Scene.Add(visual);
        var shape = ColliderShape.Box(size);
        var settings = new BodySettings { Friction = .8f };
        if (kind == BodyKind.Static) return _physics.RegisterStatic(visual, shape, settings);
        // Keep the authoritative node separate from the interpolated visual.
        var body = _physics.Register(visual.Id, [shape], body: settings with { Kind = kind },
            node: new SceneNode { Position = position }, entity: visual);
        _physics.BindPresentation(body, visual.Node);
        return body;
    }
    protected override void Update(GameTime time)
    {
        if (_pause.WasPressed) IsPaused = !IsPaused;
        if (_slow.WasPressed) TimeScale = TimeScale == 1 ? .5 : 1;
        if (_debug.WasPressed) DebugDraw.Enabled = !DebugDraw.Enabled;
        base.Update(time);
        _hasHit = _physics.Raycast(Camera.Position, Camera.Forward, 30, out _hit, new(1, _player));
        if (_hasHit)
        {
            _queryHits++;
            if (_interact.WasPressed)
            {
                Console.WriteLine($"Owner {_hit.OwnerId}, distance {_hit.Distance:F2}, point {_hit.Point}");
                if (_boxes.Contains(_hit.Collider)) _physics.ApplyImpulse(_hit.Collider, Camera.Forward * 4 + Vector3.UnitY * 3);
            }
        }
        Window.Title = $"{Options.Example} | {(_hasHit ? $"hit {_hit.Distance:F2}m" : "no hit")} | {(IsSimulationPaused ? "paused" : "running")} | scale {TimeScale:F1}";
    }
    protected override void FixedUpdate(GameTime time)
    {
        base.FixedUpdate(time);
        _simulationSeconds = time.TotalGameTime.TotalSeconds;
        // Let the boxes settle, then strike the stack's base after about two seconds.
        float phase = MathF.Max(0, (float)_simulationSeconds - 1) * 1.5f;
        _physics.SetKinematicTarget(_platform, new(new(2 * MathF.Cos(phase), .4f, 0)));
        if (_character != null)
        {
            if (_resetCharacter.ConsumePressed()) _character.Teleport(new(-5, .92f, 3));
            Vector2 move = _move.Value;
            bool jump = _jump.ConsumePressed();
            if (Options.Frames > 0)
            {
                move = _character.Pose.Position.X < 4 ? new(1, 0) : Vector2.Zero;
                if (!_autoJumped && _character.Pose.Position.X > 2) { jump = true; _autoJumped = true; }
            }
            _character.Move(new Vector3(move.X, 0, -move.Y) * 2.5f, jump, (float)time.ElapsedGameTime.TotalSeconds);
        }
        // The registered module steps and delivers contacts after this callback returns.
        if (!_stackToppled)
        {
            for (int i = 0; i < 4; i++)
            {
                var rotation = _physics.GetPose(_boxes[i]).Rotation.ToMatrix4x4();
                if ((Vector3.UnitY * rotation).Y < .8f)
                {
                    _stackToppled = true;
                    Console.WriteLine($"Platform toppled the stack at {_simulationSeconds:F2}s.");
                    break;
                }
            }
        }
    }
    protected override void Draw(GameTime time)
    {
        if (DebugDraw.Enabled)
        {
            Span<BoundingBox> bounds = stackalloc BoundingBox[32];
            int count = _physics.GetDebugBounds(bounds);
            for (int i = 0; i < System.Math.Min(count, bounds.Length); i++) DebugDraw.Box(bounds[i], Color.Yellow);
            DebugDraw.Line(Camera.Position, _hasHit ? _hit.Point : Camera.Position + Camera.Forward * 30, Color.Cyan);
            if (_hasHit) DebugDraw.Sphere(_hit.Point, .08f, Color.Red);
        }
        base.Draw(time);
    }
    public void ValidatePhysicsCompletion()
    {
        if (_queryHits == 0) throw new InvalidOperationException("Interaction query never hit the example geometry.");
        if (_simulation && _physics.CompletedSteps == 0) throw new InvalidOperationException("Physics did not step.");
        if (_simulation && _simulationSeconds >= 8 && !_stackToppled)
            throw new InvalidOperationException("The platform did not topple the stack during the example.");
        foreach (var body in _boxes)
            if (_physics.GetPose(body).Position.Y < -.5f) throw new InvalidOperationException("A body fell through the floor.");
        if (_character != null)
        {
            if (_character.Pose.Position.Y < .5f) throw new InvalidOperationException("The character fell through the floor.");
            if (Options.Frames > 0 && _simulationSeconds >= 5 && _checkpointEntries == 0)
                throw new InvalidOperationException("The character did not reach the checkpoint.");
            Console.WriteLine($"Character position={_character.Pose.Position}, grounded={_character.IsGrounded}, checkpoint entries={_checkpointEntries}.");
        }
        Console.WriteLine($"Physics queries hit on {_queryHits} updates; completed steps={_physics.CompletedSteps}.");
    }
    protected override void Unload()
    {
        if (_physics != null)
        {
            ValidatePhysicsCompletion();
            _character?.Dispose();
        }
        base.Unload();
    }
}
