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
        Camera.Position = new(8, 6, 12);
        var camera = (Njulf.Core.Camera.FirstPersonCamera)Camera;
        camera.Yaw = -MathF.Atan2(8, 12); camera.Pitch = MathF.Atan2(5, MathF.Sqrt(208));
        AddBox(new(0, -.5f, 0), new(20, 1, 14), new(.3f, .35f, .4f, 1), BodyKind.Static);
        if (_simulation)
        {
            for (int i = 0; i < 4; i++) _boxes.Add(AddBox(new(-2, 1 + i * 1.1f, 0), Vector3.One, new(.9f, .4f, .1f, 1), BodyKind.Dynamic));
            _platform = AddBox(new(2, .4f, 0), new(3, .5f, 3), new(.1f, .5f, .9f, 1), BodyKind.Kinematic);
            _boxes.Add(AddBox(new(2, 1.6f, 0), Vector3.One, new(.8f, .6f, .1f, 1), BodyKind.Dynamic));
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
    }
    private InputAction Bind(string name, InputKey key)
    {
        var action = Input.CreateAction(name); action.AddBinding(new InputBinding(key)); return action;
    }
    private ColliderHandle AddBox(Vector3 position, Vector3 size, Vector4 color, BodyKind kind)
    {
        Vector3 h = size * .5f;
        // A hard-edged box needs separate vertices per face, with flat outward normals.
        Vector3[] corners = [new(-h.X,-h.Y,-h.Z), new(h.X,-h.Y,-h.Z), new(h.X,h.Y,-h.Z), new(-h.X,h.Y,-h.Z),
            new(-h.X,-h.Y,h.Z), new(h.X,-h.Y,h.Z), new(h.X,h.Y,h.Z), new(-h.X,h.Y,h.Z)];
        var vertices = new VertexPositionNormalTextureTangent[24];
        var indices = new uint[36];
        int face = 0;
        void AddFace(int a, int b, int c, int d, Vector3 normal)
        {
            int start = face * 4, index = face * 6;
            var tangent = new Vector4((corners[b] - corners[a]).Normalized(), 1);
            vertices[start] = new(corners[a], normal, new(0, 0), tangent);
            vertices[start + 1] = new(corners[b], normal, new(1, 0), tangent);
            vertices[start + 2] = new(corners[c], normal, new(1, 1), tangent);
            vertices[start + 3] = new(corners[d], normal, new(0, 1), tangent);
            indices[index] = (uint)start; indices[index + 1] = (uint)(start + 1); indices[index + 2] = (uint)(start + 2);
            indices[index + 3] = (uint)start; indices[index + 4] = (uint)(start + 2); indices[index + 5] = (uint)(start + 3);
            face++;
        }
        AddFace(0, 3, 2, 1, -Vector3.UnitZ); AddFace(4, 5, 6, 7, Vector3.UnitZ);
        AddFace(0, 1, 5, 4, -Vector3.UnitY); AddFace(3, 7, 6, 2, Vector3.UnitY);
        AddFace(0, 4, 7, 3, -Vector3.UnitX); AddFace(1, 2, 6, 5, Vector3.UnitX);
        using var mesh = Graphics.CreateMesh(vertices, indices);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default with { BaseColorFactor = color, RoughnessFactor = .7f });
        var visual = Graphics.CreateRenderObject(mesh, material); visual.Position = position; Scene.Add(visual);
        return _physics.Register(visual.Id, [ColliderShape.Box(size)], body: new BodySettings { Kind = kind, Friction = .8f },
            node: visual.Node, entity: visual);
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
        _physics.Step((float)time.ElapsedGameTime.TotalSeconds);
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
        Console.WriteLine($"Physics queries hit on {_queryHits} updates; completed steps={_physics.CompletedSteps}.");
    }
    protected override void Unload()
    {
        if (_physics != null)
        {
            try { ValidatePhysicsCompletion(); }
            finally { _physics.Dispose(); }
        }
        base.Unload();
    }
}
