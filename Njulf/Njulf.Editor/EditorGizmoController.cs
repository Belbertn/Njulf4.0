using Hexa.NET.ImGui;
using Njulf.Core.Camera;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using N2 = System.Numerics.Vector2;
using N3 = System.Numerics.Vector3;
using NQ = System.Numerics.Quaternion;

namespace Njulf.Editor;

public enum GizmoMode { Move, Rotate, Scale }
public enum GizmoSpace { World, Local }

internal readonly record struct GizmoTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    internal static GizmoTransform Read(RenderObject target) => new(target.Position, target.Rotation, target.Scale);
}

/// <summary>Single-object, CPU-picked transform handles. Input and transform calculation do not require an ImGui context.</summary>
public sealed class EditorGizmoController
{
    public GizmoMode Mode { get; set; }
    public GizmoSpace Space { get; set; }
    public bool IsDragging => _target != null;
    public bool ConsumesPointer => IsDragging || _hover >= 0;
    private readonly List<(N2 A, N2 B, int Axis)> _segments = new();
    private readonly N3[] _axes = new N3[3];
    private RenderObject? _target;
    private GizmoTransform _original, _current;
    private GizmoMode _dragMode;
    private N3 _axis, _startDirection;
    private float _startAxis, _worldSize, _dragSize;
    private N2 _startMouse;
    private int _hover = -1, _handle;
    private bool _wasDown;

    public void Update(EditorController editor, FirstPersonCamera camera, Vector2 viewportSize, Vector2 mouse,
        bool mouseDown, bool pointerAvailable, bool focused, bool cancel)
    {
        if (!editor.Enabled || !focused || cancel || (IsDragging && (!editor.TryGetSelectedObject(out var selected) || selected != _target)))
        {
            Cancel(editor); _segments.Clear(); _hover = -1; _wasDown = mouseDown; return;
        }
        if (!editor.TryGetSelectedObject(out RenderObject? target) || target == null || target.HasNonTrsMatrix)
        { _segments.Clear(); _hover = -1; _wasDown = mouseDown; return; }
        BuildHandles(target, camera, viewportSize);
        N2 pointer = new(mouse.X, mouse.Y);
        _hover = pointerAvailable ? HitTest(pointer) : -1;
        if (!IsDragging && mouseDown && !_wasDown && _hover >= 0)
            Start(target, camera.ScreenPointToRay(mouse, viewportSize), pointer, _hover);
        if (IsDragging)
        {
            if (mouseDown)
            {
                Ray ray = camera.ScreenPointToRay(mouse, viewportSize);
                if (TryDrag(ray, pointer, out var value))
                {
                    _current = value;
                    editor.ApplyGizmoTransform(_target!, value, false);
                }
            }
            else
            {
                editor.ApplyGizmoTransform(_target!, _current, _current != _original);
                _target = null;
            }
        }
        _wasDown = mouseDown;
    }

    public void Cancel(EditorController editor)
    {
        if (_target != null && ReferenceEquals(editor.Scene.FindById(_target.Id), _target)) editor.ApplyGizmoTransform(_target, _original, false);
        _target = null; _hover = -1;
    }

    private void BuildHandles(RenderObject target, FirstPersonCamera camera, Vector2 viewport)
    {
        _segments.Clear();
        var rect = new SpriteRectangle(0, 0, viewport.X, viewport.Y);
        if (!ScreenProjection.TryProject(camera, target.Position, rect, out Vector2 screen)) return;
        N3 center = ToN(target.Position);
        float distance = N3.Dot(center - ToN(camera.Position), ToN(camera.Forward));
        _worldSize = 160 * distance * MathF.Tan(camera.FieldOfView * .5f) / viewport.Y;
        if (!float.IsFinite(_worldSize) || _worldSize <= 1e-6f) return;
        NQ rotation = ToN(target.Rotation);
        for (int axis = 0; axis < 3; axis++)
        {
            N3 unit = axis == 0 ? N3.UnitX : axis == 1 ? N3.UnitY : N3.UnitZ;
            _axes[axis] = Mode == GizmoMode.Scale || Space == GizmoSpace.Local ? N3.Transform(unit, rotation) : unit;
            if (Mode == GizmoMode.Rotate)
            {
                N3 tangent = N3.Normalize(N3.Cross(_axes[axis], MathF.Abs(_axes[axis].Y) < .9f ? N3.UnitY : N3.UnitX));
                N3 bitangent = N3.Cross(_axes[axis], tangent);
                for (int segment = 0; segment < 48; segment++)
                {
                    float a = segment * MathF.Tau / 48, b = (segment + 1) * MathF.Tau / 48;
                    Add(center + (tangent * MathF.Cos(a) + bitangent * MathF.Sin(a)) * _worldSize,
                        center + (tangent * MathF.Cos(b) + bitangent * MathF.Sin(b)) * _worldSize, axis);
                }
            }
            else Add(center + _axes[axis] * _worldSize * .15f, center + _axes[axis] * _worldSize, axis);
        }
        if (Mode == GizmoMode.Scale)
        {
            N2 p = new(screen.X, screen.Y);
            _segments.Add((p - new N2(5, 5), p + new N2(5, -5), 3));
            _segments.Add((p + new N2(5, -5), p + new N2(5, 5), 3));
            _segments.Add((p + new N2(5, 5), p + new N2(-5, 5), 3));
            _segments.Add((p + new N2(-5, 5), p - new N2(5, 5), 3));
        }
        void Add(N3 a, N3 b, int axis)
        {
            if (ScreenProjection.TryProject(camera, ToCore(a), rect, out var p) && ScreenProjection.TryProject(camera, ToCore(b), rect, out var q) &&
                (p - q).LengthSquared() > 1e-4f)
                _segments.Add((new(p.X, p.Y), new(q.X, q.Y), axis));
        }
    }

    private int HitTest(N2 pointer)
    {
        float closest = 8; int handle = -1;
        foreach (var s in _segments)
        {
            N2 direction = s.B - s.A;
            float squared = direction.LengthSquared();
            if (squared < 1e-4f) continue;
            float t = Math.Clamp(N2.Dot(pointer - s.A, direction) / squared, 0, 1);
            float distance = N2.Distance(pointer, s.A + direction * t);
            if (distance < closest) { closest = distance; handle = s.Axis; }
        }
        return handle;
    }

    private void Start(RenderObject target, Ray ray, N2 mouse, int handle)
    {
        _original = _current = GizmoTransform.Read(target);
        _handle = handle; _axis = handle < 3 ? _axes[handle] : N3.Zero; _dragMode = Mode;
        _dragSize = _worldSize; _startMouse = mouse;
        if (Mode == GizmoMode.Rotate)
        {
            if (!GizmoMath.TryRingDirection(ray, ToN(target.Position), _axis, out _startDirection)) return;
        }
        else if (handle < 3 && !GizmoMath.TryAxisDistance(ray, ToN(target.Position), _axis, out _startAxis)) return;
        _target = target;
    }

    private bool TryDrag(Ray ray, N2 mouse, out GizmoTransform value)
    {
        value = _original;
        if (_dragMode == GizmoMode.Rotate)
        {
            if (!GizmoMath.TryRingDirection(ray, ToN(_original.Position), _axis, out N3 direction)) return false;
            float angle = MathF.Atan2(N3.Dot(_axis, N3.Cross(_startDirection, direction)), N3.Dot(_startDirection, direction));
            NQ rotation = NQ.Normalize(NQ.CreateFromAxisAngle(_axis, angle) * ToN(_original.Rotation));
            value = value with { Rotation = new(rotation.X, rotation.Y, rotation.Z, rotation.W) };
        }
        else
        {
            float delta;
            if (_handle == 3) delta = ((mouse.X - _startMouse.X) - (mouse.Y - _startMouse.Y)) / 100;
            else
            {
                if (!GizmoMath.TryAxisDistance(ray, ToN(_original.Position), _axis, out float distance)) return false;
                delta = distance - _startAxis;
            }
            if (_dragMode == GizmoMode.Move) value = value with { Position = ToCore(ToN(_original.Position) + _axis * delta) };
            else value = value with { Scale = GizmoMath.Scale(_original.Scale, _handle, _handle == 3 ? delta : delta / _dragSize) };
        }
        return true;
    }

    public void Render()
    {
        ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
        foreach (var s in _segments)
        {
            uint color = (IsDragging ? _handle : _hover) == s.Axis ? 0xff40ffffu : s.Axis switch { 0 => 0xff5555eeu, 1 => 0xff55dd55u, 2 => 0xffee9955u, _ => 0xffffffffu };
            draw.AddLine(s.A, s.B, color, 2.5f);
            if (Mode != GizmoMode.Rotate && s.Axis < 3)
            {
                if (Mode == GizmoMode.Scale) draw.AddRectFilled(s.B - new N2(4), s.B + new N2(4), color);
                else
                {
                    N2 d = N2.Normalize(s.B - s.A), n = new(-d.Y, d.X);
                    draw.AddTriangleFilled(s.B, s.B - d * 10 + n * 5, s.B - d * 10 - n * 5, color);
                }
            }
        }
    }

    private static N3 ToN(Vector3 p) => new(p.X, p.Y, p.Z);
    private static NQ ToN(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
    private static Vector3 ToCore(N3 p) => new(p.X, p.Y, p.Z);
}

internal static class GizmoMath
{
    internal static bool TryAxisDistance(Ray ray, N3 center, N3 axis, out float distance)
    {
        N3 direction = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z);
        N3 offset = new N3(ray.Position.X, ray.Position.Y, ray.Position.Z) - center;
        float b = N3.Dot(direction, axis), denominator = 1 - b * b;
        distance = 0;
        if (denominator < 1e-4f) return false;
        distance = (N3.Dot(axis, offset) - b * N3.Dot(direction, offset)) / denominator;
        return float.IsFinite(distance);
    }
    internal static bool TryRingDirection(Ray ray, N3 center, N3 normal, out N3 direction)
    {
        direction = default;
        N3 d = new(ray.Direction.X, ray.Direction.Y, ray.Direction.Z), origin = new(ray.Position.X, ray.Position.Y, ray.Position.Z);
        float denominator = N3.Dot(d, normal);
        if (MathF.Abs(denominator) < 1e-4f) return false;
        float t = N3.Dot(center - origin, normal) / denominator;
        N3 radial = origin + d * t - center;
        if (!float.IsFinite(t) || t < 0 || radial.LengthSquared() < 1e-8f) return false;
        direction = N3.Normalize(radial); return true;
    }
    internal static Vector3 Scale(Vector3 original, int axis, float delta)
    {
        float factor = MathF.Exp(Math.Clamp(delta, -8, 8));
        static float Apply(float value, float factor) => MathF.CopySign(Math.Clamp(MathF.Abs(value) * factor, 1e-4f, float.MaxValue), value);
        return new(axis is 0 or 3 ? Apply(original.X, factor) : original.X,
            axis is 1 or 3 ? Apply(original.Y, factor) : original.Y,
            axis is 2 or 3 ? Apply(original.Z, factor) : original.Z);
    }
}
