using Njulf.Core.Math;

namespace Njulf.Rendering.Debug
{
    public enum DebugDrawDepthMode
    {
        DepthTested,
        AlwaysVisible,
        XRay
    }

    public enum DebugDrawLifetime
    {
        OneFrame,
        Persistent
    }

    public readonly record struct DebugLine(Vector3 A, Vector3 B, Vector4 Color);

    public readonly record struct DebugDrawCommand(DebugLine Line, DebugDrawDepthMode DepthMode);

    public sealed record DebugDrawFrameSnapshot(
        IReadOnlyList<DebugDrawCommand> Lines,
        int LineCount,
        int PersistentLineCount,
        int DroppedLineCount)
    {
        public static DebugDrawFrameSnapshot Empty { get; } = new(
            Array.Empty<DebugDrawCommand>(),
            LineCount: 0,
            PersistentLineCount: 0,
            DroppedLineCount: 0);
    }
}
