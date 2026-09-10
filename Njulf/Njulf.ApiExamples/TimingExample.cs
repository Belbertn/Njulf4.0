using Njulf.Core;
using Njulf.Core.Foliage;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Rendering;

namespace Njulf.ApiExamples;

internal sealed class TimingExample : ExampleGame
{
    private RenderObject _object = null!;
    private InputAction _pause = null!, _slow = null!, _focusPause = null!, _reverse = null!;
    private Vector3 _previous, _current;
    private double _phase;
    private int _direction = 1;
    private bool _pendingReverse;

    public TimingExample(ExampleOptions options) : base(options)
    {
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(1.0 / 30);
        MaximumFramesPerSecond = 90;
        PauseWhenInactive = true; // Opt-in policy for this example.
    }

    protected override void Load()
    {
        base.Load();
        using var mesh = Graphics.CreateMesh(
            [new Vector3(0, .5f, 0), new Vector3(-.4f, -.4f, 0), new Vector3(.4f, -.4f, 0)], [0u, 1u, 2u]);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default with
        {
            BaseColorFactor = new Vector4(1, .4f, .1f, 1), RoughnessFactor = .7f
        });
        _object = Graphics.CreateRenderObject(mesh, material);
        Scene.Add(_object);

        var foliageOwner = Graphics.CreateRenderObject(mesh, material);
        var prototype = new FoliagePrototype
        {
            Name = "Timing grass", Mesh = foliageOwner.Mesh, Material = foliageOwner.Material,
            GeometryMode = FoliageGeometryMode.BillboardCards, CardHeight = .6f, CardWidth = .1f
        };
        prototype.AdoptResourceOwner(foliageOwner);
        prototype.Wind.Strength = .8f;
        Scene.Add(new FoliagePatch(prototype, new BoundingBox(new(-2, -1, -1), new(2, -1, 1))) { Density = 8 });
        Scene.Add(new ParticleEffectInstance(new ParticleEffect
        {
            Name = "Timing sparks",
            Emitters = [new ParticleEmitterDefinition
            {
                Name = "Sparks", SpawnRatePerSecond = 30, MaxParticles = 256,
                LifetimeSeconds = ParticleCurve.Constant(2), Size = ParticleCurve.Constant(.06f),
                InitialVelocityMin = new(-.3f, .5f, 0), InitialVelocityMax = new(.3f, 1, 0),
                ColorOverLife = ParticleGradient.Constant(Color.White),
                Material = new ParticleMaterialDefinition { Name = "White sparks" }
            }]
        }) { WorldMatrix = Matrix4x4.CreateTranslation(new Vector3(1.3f, -.5f, 0)), RandomSeed = 42 });
        ((VulkanRenderer)Renderer).Settings.Particles.Enabled = true;
        ((VulkanRenderer)Renderer).Settings.Foliage.Enabled = true;
        _pause = Bind("Pause", InputKey.P);
        _slow = Bind("Slow motion", InputKey.T);
        _focusPause = Bind("Focus pause", InputKey.F);
        _reverse = Bind("Reverse", InputKey.Space);
        Console.WriteLine("P: pause. T: half speed. F: toggle focus pause. Space: reverse on the next fixed step.");
    }

    private InputAction Bind(string name, InputKey key)
    {
        var action = Input.CreateAction(name);
        action.AddBinding(new InputBinding(key));
        return action;
    }

    protected override void Update(GameTime time)
    {
        if (_pause.WasPressed) IsPaused = !IsPaused;
        if (_slow.WasPressed) TimeScale = TimeScale == 1 ? .5 : 1;
        if (_focusPause.WasPressed) PauseWhenInactive = !PauseWhenInactive;
        if (IsSimulationPaused) _pendingReverse = false;
        else _pendingReverse |= _reverse.WasPressed; // Retain across host ticks with no fixed step.
        Window.Title = $"Timing | {(IsSimulationPaused ? "paused" : "running")} | scale {TimeScale:0.0} | focus pause {PauseWhenInactive} | wall {time.UnscaledTotalGameTime.TotalSeconds:0.0}s";
        base.Update(time);
    }

    protected override void FixedUpdate(GameTime time)
    {
        if (_pendingReverse) { _direction = -_direction; _pendingReverse = false; }
        _previous = _current;
        _phase += _direction * time.ElapsedGameTime.TotalSeconds;
        _current = new Vector3((float)System.Math.Sin(_phase), 0, 0);
        base.FixedUpdate(time);
    }

    protected override void Draw(GameTime time)
    {
        // Presentation never becomes the authoritative simulation state.
        _object.Position = Vector3.Lerp(_previous, _current, InterpolationAlpha);
        base.Draw(time);
    }
}
