using System.Numerics;
using System.Runtime.InteropServices;

namespace Njulf.Rendering.Pipeline;

public sealed record OverlayDrawData(Vector2 DisplayPosition, Vector2 DisplaySize, Vector2 FramebufferScale,
    OverlayVertex[] Vertices, ushort[] Indices, OverlayDrawCommand[] Commands)
{
    public bool IsEmpty => DisplaySize.X <= 0f || DisplaySize.Y <= 0f || Vertices.Length == 0 || Indices.Length == 0 || Commands.Length == 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct OverlayVertex(Vector2 Position, Vector2 Uv, uint Color);
public readonly record struct OverlayDrawCommand(uint ElementCount, uint IndexOffset, int VertexOffset, Vector4 ClipRectangle, int TextureIndex);
internal sealed class OverlayDrawDataSource { public OverlayDrawData? Current { get; private set; } public void Set(OverlayDrawData? value) => Current = value; }
