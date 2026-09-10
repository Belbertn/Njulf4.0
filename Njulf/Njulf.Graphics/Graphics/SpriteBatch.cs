using Njulf.Core.Math;
using System.Runtime.InteropServices;
using System.Text;

namespace Njulf.Graphics;

/// <summary>A rectangle in logical screen pixels (or atlas pixels for a source rectangle).</summary>
public readonly record struct SpriteRectangle(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    internal bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Right) && float.IsFinite(Bottom) && Width >= 0 && Height >= 0;
    internal SpriteRectangle Intersect(SpriteRectangle other)
    {
        float x = MathF.Max(X, other.X), y = MathF.Max(Y, other.Y);
        return new(x, y, MathF.Max(0, MathF.Min(Right, other.Right) - x), MathF.Max(0, MathF.Min(Bottom, other.Bottom) - y));
    }
}

[Flags]
public enum SpriteFlip { None = 0, Horizontal = 1, Vertical = 2 }
public enum SpriteSampler { LinearClamp, PointClamp }

/// <summary>Deferred, ordered screen drawing. Begin/End must be inside a renderer frame. Colors are linear, straight alpha.</summary>
public sealed class SpriteBatch : IDisposable
{
    private readonly ISpriteBatchSink _sink;
    private readonly List<SpriteVertex> _vertices = new();
    private readonly List<ushort> _indices = new();
    private readonly List<SpriteCommand> _commands = new();
    private readonly Stack<SpriteRectangle> _clips = new();
    private readonly Dictionary<Texture, SpriteTextureLease> _textures = new();
    private SpriteSampler _sampler;
    private Vector2 _display;
    private bool _active, _disposed;
    private int _baseVertex;

    internal SpriteBatch(ISpriteBatchSink sink) => _sink = sink;

    public void Begin(SpriteSampler sampler = SpriteSampler.LinearClamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active) throw new InvalidOperationException("End the current sprite batch first.");
        if (!Enum.IsDefined(sampler)) throw new ArgumentOutOfRangeException(nameof(sampler));
        _display = _sink.Begin();
        _sampler = sampler;
        _active = true;
        _clips.Push(new(0, 0, _display.X, _display.Y));
    }

    public void PushClip(SpriteRectangle rectangle)
    {
        RequireActive();
        if (!rectangle.IsValid) throw new ArgumentOutOfRangeException(nameof(rectangle));
        _clips.Push(_clips.Peek().Intersect(rectangle));
    }

    public void PopClip()
    {
        RequireActive();
        if (_clips.Count <= 1) throw new InvalidOperationException("No user clip to pop.");
        _clips.Pop();
    }

    /// <summary>Origin is measured in destination pixels, relative to the unrotated top-left. Rotation is clockwise radians.</summary>
    public void Draw(Texture texture, Vector2 position, Color color, Vector2? size = null,
        SpriteRectangle? source = null, float rotation = 0, Vector2 origin = default, SpriteFlip flip = SpriteFlip.None)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(texture);
        ObjectDisposedException.ThrowIf(texture.IsDisposed, texture);
        SpriteRectangle rect = source ?? new(0, 0, texture.Width, texture.Height);
        if (!rect.IsValid || rect.X < 0 || rect.Y < 0 || rect.Right > texture.Width || rect.Bottom > texture.Height)
            throw new ArgumentOutOfRangeException(nameof(source));
        if ((flip & ~(SpriteFlip.Horizontal | SpriteFlip.Vertical)) != 0) throw new ArgumentOutOfRangeException(nameof(flip));
        Vector2 extent = size ?? new(rect.Width, rect.Height);
        ValidateGeometry(position, extent, rotation, origin);
        if (!_textures.TryGetValue(texture, out SpriteTextureLease lease))
        {
            lease = _sink.Retain(texture, _sampler);
            _textures.Add(texture, lease);
        }
        float u0 = rect.X / texture.Width, v0 = rect.Y / texture.Height;
        float u1 = rect.Right / texture.Width, v1 = rect.Bottom / texture.Height;
        if ((flip & SpriteFlip.Horizontal) != 0) (u0, u1) = (u1, u0);
        if ((flip & SpriteFlip.Vertical) != 0) (v0, v1) = (v1, v0);
        Quad(lease.Index, position, extent, color, rotation, origin, u0, v0, u1, v1);
    }

    public void FillRectangle(SpriteRectangle rectangle, Color color)
    {
        RequireActive();
        if (!rectangle.IsValid) throw new ArgumentOutOfRangeException(nameof(rectangle));
        Quad(0, new(rectangle.X, rectangle.Y), new(rectangle.Width, rectangle.Height), color, 0, default, 0, 0, 1, 1);
    }

    public void DrawLine(Vector2 start, Vector2 end, Color color, float thickness = 1)
    {
        RequireActive();
        if (!float.IsFinite(thickness) || thickness <= 0) throw new ArgumentOutOfRangeException(nameof(thickness));
        Vector2 delta = end - start;
        float length = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        ValidateGeometry(start, new(length, thickness), 0, default);
        Quad(0, start, new(length, thickness), color, MathF.Atan2(delta.Y, delta.X), new(0, thickness * .5f), 0, 0, 1, 1);
    }

    public void DrawRectangle(SpriteRectangle rectangle, Color color, float thickness = 1)
    {
        if (!rectangle.IsValid) throw new ArgumentOutOfRangeException(nameof(rectangle));
        DrawLine(new(rectangle.X, rectangle.Y), new(rectangle.Right, rectangle.Y), color, thickness);
        DrawLine(new(rectangle.Right, rectangle.Y), new(rectangle.Right, rectangle.Bottom), color, thickness);
        DrawLine(new(rectangle.Right, rectangle.Bottom), new(rectangle.X, rectangle.Bottom), color, thickness);
        DrawLine(new(rectangle.X, rectangle.Bottom), new(rectangle.X, rectangle.Y), color, thickness);
    }

    public void DrawString(SpriteFont font, string text, Vector2 position, Color color, float scale = 1)
    {
        RequireActive();
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        font.EnsureUsable();
        if (!float.IsFinite(scale) || scale <= 0) throw new ArgumentOutOfRangeException(nameof(scale));
        float x = 0, y = 0;
        int previous = -1;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r') continue;
            if (rune.Value == '\n') { x = 0; y += font.LineHeight; previous = -1; continue; }
            SpriteGlyph glyph = font.GetGlyph(rune.Value);
            x += font.Kerning(previous, glyph.CodePoint);
            Draw(font.Atlas, position + new Vector2((x + glyph.Offset.X) * scale, (y + glyph.Offset.Y) * scale), color,
                new Vector2(glyph.Source.Width * scale, glyph.Source.Height * scale), glyph.Source);
            x += glyph.Advance;
            previous = glyph.CodePoint;
        }
    }

    public void End()
    {
        RequireActive();
        if (_clips.Count != 1) throw new InvalidOperationException("Pop every clip before End.");
        try
        {
            if (_indices.Count != 0)
                _sink.Submit(new(_display, CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices), CollectionsMarshal.AsSpan(_commands)));
        }
        finally { Reset(); }
    }

    private void Quad(int texture, Vector2 position, Vector2 size, Color color, float rotation, Vector2 origin,
        float u0, float v0, float u1, float v1)
    {
        SpriteRectangle clip = _clips.Peek();
        if (clip.Width == 0 || clip.Height == 0 || size.X == 0 || size.Y == 0) return;
        if (_vertices.Count - _baseVertex > ushort.MaxValue - 4) _baseVertex = _vertices.Count;
        int local = _vertices.Count - _baseVertex;
        uint packed = Pack(color);
        float c = MathF.Cos(rotation), s = MathF.Sin(rotation);
        Add(0, 0, u0, v0); Add(size.X, 0, u1, v0); Add(size.X, size.Y, u1, v1); Add(0, size.Y, u0, v1);
        uint offset = checked((uint)_indices.Count);
        _indices.Add((ushort)local); _indices.Add((ushort)(local + 1)); _indices.Add((ushort)(local + 2));
        _indices.Add((ushort)local); _indices.Add((ushort)(local + 2)); _indices.Add((ushort)(local + 3));
        if (_commands.Count > 0 && _commands[^1] is var last && last.TextureIndex == texture && last.Clip == clip && last.VertexOffset == _baseVertex)
            _commands[^1] = last with { ElementCount = last.ElementCount + 6 };
        else _commands.Add(new(6, offset, _baseVertex, clip, texture));
        void Add(float x, float y, float u, float v)
        {
            x -= origin.X; y -= origin.Y;
            _vertices.Add(new(new(position.X + x * c - y * s, position.Y + x * s + y * c), new(u, v), packed));
        }
    }

    private static uint Pack(Color color)
    {
        static uint Byte(float value) => (uint)Math.Clamp(float.IsFinite(value) ? MathF.Round(value * 255) : 0, 0, 255);
        return Byte(color.R) | Byte(color.G) << 8 | Byte(color.B) << 16 | Byte(color.A) << 24;
    }
    private static void ValidateGeometry(Vector2 p, Vector2 size, float rotation, Vector2 origin)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 0 || size.Y < 0 ||
            !float.IsFinite(rotation) || !float.IsFinite(origin.X) || !float.IsFinite(origin.Y)) throw new ArgumentOutOfRangeException(nameof(p));
    }
    private void RequireActive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_active) throw new InvalidOperationException("Call Begin before drawing.");
        try { _sink.ValidateFrame(); }
        catch { Reset(); throw; }
    }
    private void Reset()
    {
        foreach (SpriteTextureLease lease in _textures.Values) lease.Release();
        _textures.Clear(); _vertices.Clear(); _indices.Clear(); _commands.Clear(); _clips.Clear(); _baseVertex = 0; _active = false;
    }
    public void Dispose() { if (_disposed) return; Reset(); _disposed = true; }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct SpriteVertex(System.Numerics.Vector2 Position, System.Numerics.Vector2 Uv, uint Color);
internal readonly record struct SpriteCommand(uint ElementCount, uint IndexOffset, int VertexOffset, SpriteRectangle Clip, int TextureIndex);
internal readonly ref struct SpriteDrawData(Vector2 displaySize, ReadOnlySpan<SpriteVertex> vertices, ReadOnlySpan<ushort> indices, ReadOnlySpan<SpriteCommand> commands)
{
    public Vector2 DisplaySize { get; } = displaySize;
    public ReadOnlySpan<SpriteVertex> Vertices { get; } = vertices;
    public ReadOnlySpan<ushort> Indices { get; } = indices;
    public ReadOnlySpan<SpriteCommand> Commands { get; } = commands;
}
internal readonly record struct SpriteTextureLease(int Index, Action Release);
internal interface ISpriteBatchSink
{
    Vector2 Begin();
    void ValidateFrame();
    SpriteTextureLease Retain(Texture texture, SpriteSampler sampler);
    void Submit(SpriteDrawData data);
}
