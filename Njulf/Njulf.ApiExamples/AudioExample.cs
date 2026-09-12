using Njulf.Audio;
using Njulf.Core;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Input;
using Njulf.Physics;

namespace Njulf.ApiExamples;

internal sealed class AudioExample(ExampleOptions options) : ExampleGame(options)
{
    private const uint SoundBlockerLayer = 2;
    private AudioSystem? _audio;
    private AudioSource? _source;
    private PhysicsScene _physics = null!;
    private RenderObject _wall = null!;
    private ColliderHandle _wallCollider;
    private InputAction _toggleWall = null!, _toggleOcclusion = null!, _pause = null!;
    private InputAction _forward = null!, _back = null!, _left = null!, _right = null!, _turnLeft = null!, _turnRight = null!;
    private bool _occlusionEnabled = true;
    private float _queryCountdown;
    private bool _observedBlocked, _observedClear;

    protected override void Load()
    {
        base.Load();
        MaximumFramesPerSecond = 90;
        Camera.Position = new(0, 1.5f, 6);
        _physics = new(PhysicsMode.QueryOnly, Scene);
        AddBox(new(0, -.25f, 0), new(16, .5f, 16), new(.3f, .35f, .4f, 1));
        AddBox(new(0, 1.5f, -2), new(.5f, .5f, .5f), new(.9f, .5f, .1f, 1));
        _wall = AddBox(new(0, 1.5f, 1), new(3, 3, .25f), new(.2f, .5f, .7f, 1));
        _wallCollider = _physics.Register(_wall.Id, [ColliderShape.Box(new(3, 3, .25f))],
            layer: SoundBlockerLayer, node: _wall.Node, entity: _wall);
        _toggleWall = Bind("Toggle audio wall", InputKey.Space);
        _toggleOcclusion = Bind("Toggle occlusion", InputKey.O);
        _pause = Bind("Pause audio", InputKey.P);
        _forward = Bind("Walk forward", InputKey.W); _back = Bind("Walk back", InputKey.S);
        _left = Bind("Walk left", InputKey.A); _right = Bind("Walk right", InputKey.D);
        _turnLeft = Bind("Turn left", InputKey.Left); _turnRight = Bind("Turn right", InputKey.Right);
        try { _audio = new AudioSystem(); }
        catch (InvalidOperationException error)
        {
            if (Options.Validation) throw;
            Console.Error.WriteLine($"Continuing without audio: {error.Message}");
        }
        if (_audio != null)
        {
            _audio.MasterGain = .5f;
            var clip = _audio.LoadWav(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "occlusion-loop.wav"));
            _source = _audio.CreateSource(clip);
            _source.Position = new(0, 1.5f, -2);
            _source.ReferenceDistance = 3;
            _source.Looping = true;
            _audio.SetListener(Camera.Position, Camera.Forward, Camera.Up);
            QueryOcclusion(); // Before Play: no initial burst of unoccluded sound.
            _source.Play();
            Console.WriteLine($"OpenAL ready; EFX={_audio.SupportsEfx} (otherwise volume-only occlusion).");
        }
        Console.WriteLine("WASD: move. Left/Right: turn. Space: wall. O: occlusion. P: pause/resume sound. Orange box is the source.");
    }

    private InputAction Bind(string name, InputKey key)
    {
        var action = Input.CreateAction(name); action.AddBinding(new InputBinding(key)); return action;
    }

    private RenderObject AddBox(Vector3 position, Vector3 size, Vector4 color)
    {
        var vertices = new VertexPositionNormalTextureTangent[24];
        var indices = new uint[36];
        Vector3[] normals = [Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Forward, Vector3.Backward];
        for (int face = 0; face < 6; face++)
        {
            Vector3 n = normals[face];
            Vector3 u = Vector3.Cross(MathF.Abs(n.Y) > .5f ? Vector3.UnitZ : Vector3.Up, n);
            Vector3 v = Vector3.Cross(n, u);
            Vector2[] uv = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
            for (int corner = 0; corner < 4; corner++)
                vertices[face * 4 + corner] = new((n + u * (uv[corner].X * 2 - 1) + v * (uv[corner].Y * 2 - 1)) * size * .5f,
                    n, uv[corner], new(u, 1));
            uint start = (uint)(face * 4);
            uint[] quad = [start, start + 1, start + 2, start, start + 2, start + 3];
            quad.CopyTo(indices, face * 6);
        }
        using var mesh = Graphics.CreateMesh(vertices, indices);
        using var material = Graphics.CreateMaterial(MaterialDefinition.Default with { BaseColorFactor = color, RoughnessFactor = .7f });
        var visual = Graphics.CreateRenderObject(mesh, material);
        visual.Position = position; Scene.Add(visual); return visual;
    }

    private void QueryOcclusion()
    {
        if (_source == null) return;
        Vector3 delta = _source.Position - Camera.Position;
        float distance = delta.Length();
        bool blocked = _occlusionEnabled && distance > .001f &&
            _physics.Raycast(Camera.Position, delta, distance, out _, new QueryFilter(SoundBlockerLayer));
        _source.Occlusion = blocked ? 1 : 0;
        _observedBlocked |= blocked; _observedClear |= !blocked;
        _queryCountdown = .1f;
    }

    protected override void Update(GameTime time)
    {
        base.Update(time);
        float dt = (float)time.UnscaledElapsedGameTime.TotalSeconds;
        var camera = (FirstPersonCamera)Camera;
        camera.Yaw += ((_turnLeft.IsDown ? 1 : 0) - (_turnRight.IsDown ? 1 : 0)) * dt;
        Camera.Position += (Camera.Forward * ((_forward.IsDown ? 1 : 0) - (_back.IsDown ? 1 : 0)) +
            Camera.Right * ((_right.IsDown ? 1 : 0) - (_left.IsDown ? 1 : 0))) * (dt * 3);
        // A finite validation run also exercises opening the wall.
        if (_toggleWall.WasPressed || (Options.Validation && Options.Frames > 0 && QualityFrames >= Options.Frames / 2 && _wall.Visible))
        {
            _wall.Visible = !_wall.Visible;
            _physics.SetEnabled(_wallCollider, _wall.Visible);
            _queryCountdown = 0;
        }
        if (_toggleOcclusion.WasPressed) { _occlusionEnabled = !_occlusionEnabled; _queryCountdown = 0; }
        if (_audio != null && _source != null)
        {
            _audio.SetListener(Camera.Position, Camera.Forward, Camera.Up);
            if (_pause.WasPressed)
            {
                if (_source.State == AudioPlaybackState.Playing) _source.Pause();
                else { QueryOcclusion(); _source.Play(); }
            }
            _queryCountdown -= dt;
            if (_source.State == AudioPlaybackState.Playing && _queryCountdown <= 0) QueryOcclusion();
            _audio.Update(dt);
        }
        Window.Title = $"Audio | wall={_wall.Visible} | occlusion={_occlusionEnabled} | blocked={_source?.Occlusion:F0} | EFX={_audio?.SupportsEfx}";
    }

    protected override void Unload()
    {
        try
        {
            if (Options.Validation && Options.Frames > 0 && _source != null && (!_observedBlocked || !_observedClear))
                throw new InvalidOperationException("Audio example did not observe both blocked and clear paths.");
        }
        finally
        {
            _audio?.Dispose(); // Sources and clips released before the output device.
            _physics?.Dispose();
            base.Unload();
        }
    }
}
