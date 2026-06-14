using System;

namespace Njulf.Rendering.Pipeline
{
    public enum RenderGraphResourceKind : byte
    {
        Image = 1,
        Buffer = 2
    }

    public readonly record struct RenderGraphResourceHandle(
        RenderGraphResourceKind Kind,
        int Index,
        int Generation)
    {
        public bool IsValid => Index >= 0 && Generation > 0;

        public static RenderGraphResourceHandle InvalidImage { get; } = new(RenderGraphResourceKind.Image, -1, 0);
        public static RenderGraphResourceHandle InvalidBuffer { get; } = new(RenderGraphResourceKind.Buffer, -1, 0);

        public void ThrowIfInvalid()
        {
            if (!IsValid)
                throw new InvalidOperationException($"Invalid render graph {Kind} handle.");
        }
    }
}
