using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Graphics;

namespace Njulf.Rendering.Pipeline;

public sealed record OverlayDrawData(Vector2 DisplayPosition, Vector2 DisplaySize, Vector2 FramebufferScale,
    OverlayVertex[] Vertices, ushort[] Indices, OverlayDrawCommand[] Commands)
{
    public int VertexCount { get; init; } = Vertices.Length;
    public int IndexCount { get; init; } = Indices.Length;
    public int CommandCount { get; init; } = Commands.Length;
    public bool IsEmpty => DisplaySize.X <= 0f || DisplaySize.Y <= 0f || VertexCount == 0 || IndexCount == 0 || CommandCount == 0;

    internal static OverlayDrawData Compose(IReadOnlyList<OverlayDrawData> sprites, OverlayDrawData? editor, uint width, uint height)
    {
        var builder = new OverlayFrameBuilder();
        foreach (var data in sprites) builder.Append(data);
        if (editor != null) builder.Append(editor);
        return builder.Build(width, height);
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct OverlayVertex(Vector2 Position, Vector2 Uv, uint Color);
public readonly record struct OverlayDrawCommand(uint ElementCount, uint IndexOffset, int VertexOffset, Vector4 ClipRectangle, int TextureIndex)
{
    public bool LinearColor { get; init; }
}
internal sealed class OverlayDrawDataSource { public OverlayDrawData? Current { get; private set; } public void Set(OverlayDrawData? value) => Current = value; }

// CPU storage survives frames. UploadSpanToBuffer copies it before the next frame can reuse it.
internal sealed class OverlayFrameBuilder
{
    private OverlayVertex[] _vertices = [];
    private ushort[] _indices = [];
    private OverlayDrawCommand[] _commands = [];
    private int _vc, _ic, _cc;
    internal int Count => _ic;
    internal void Clear() { _vc = _ic = _cc = 0; }
    private void Reserve(int vertices, int indices, int commands)
    {
        Grow(ref _vertices, checked(_vc + vertices)); Grow(ref _indices, checked(_ic + indices)); Grow(ref _commands, checked(_cc + commands));
        static void Grow<T>(ref T[] storage, int required)
        { if (storage.Length < required) Array.Resize(ref storage, Math.Max(required, Math.Max(64, checked(storage.Length * 2)))); }
    }
    internal void Append(SpriteDrawData data, uint width, uint height)
    {
        Reserve(data.Vertices.Length, data.Indices.Length, data.Commands.Length);
        Vector2 scale = new(width / data.DisplaySize.X, height / data.DisplaySize.Y);
        foreach (var v in data.Vertices) _vertices[_vc++] = new(v.Position * scale, v.Uv, v.Color);
        int baseVertex = _vc - data.Vertices.Length;
        data.Indices.CopyTo(_indices.AsSpan(_ic));
        foreach (var c in data.Commands)
            _commands[_cc++] = new(c.ElementCount, checked(c.IndexOffset + (uint)_ic), checked(c.VertexOffset + baseVertex),
                new(c.Clip.X * scale.X, c.Clip.Y * scale.Y, c.Clip.Right * scale.X, c.Clip.Bottom * scale.Y), c.TextureIndex) { LinearColor = true };
        _ic += data.Indices.Length;
    }
    internal void Append(OverlayDrawData data)
    {
        Reserve(data.VertexCount, data.IndexCount, data.CommandCount);
        for (int i = 0; i < data.VertexCount; i++)
        {
            var v = data.Vertices[i];
            _vertices[_vc + i] = v with { Position = (v.Position - data.DisplayPosition) * data.FramebufferScale };
        }
        data.Indices.AsSpan(0, data.IndexCount).CopyTo(_indices.AsSpan(_ic));
        foreach (var c in data.Commands.AsSpan(0, data.CommandCount))
        {
            Vector2 min = (new Vector2(c.ClipRectangle.X, c.ClipRectangle.Y) - data.DisplayPosition) * data.FramebufferScale;
            Vector2 max = (new Vector2(c.ClipRectangle.Z, c.ClipRectangle.W) - data.DisplayPosition) * data.FramebufferScale;
            _commands[_cc++] = c with { IndexOffset = checked(c.IndexOffset + (uint)_ic), VertexOffset = checked(c.VertexOffset + _vc), ClipRectangle = new(min.X, min.Y, max.X, max.Y) };
        }
        _vc += data.VertexCount; _ic += data.IndexCount;
    }
    internal OverlayDrawData Build(uint width, uint height) => new(default, new(width, height), Vector2.One, _vertices, _indices, _commands)
    { VertexCount = _vc, IndexCount = _ic, CommandCount = _cc };
}
