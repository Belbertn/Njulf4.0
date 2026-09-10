using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Debug;

namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    private DiagnosticDrawing? _diagnosticDrawing;
    public override DiagnosticDrawing DebugDraw => _diagnosticDrawing ??= new Diagnostics(_spriteRenderer);
    private sealed class Diagnostics(VulkanRenderer renderer) : DiagnosticDrawing
    {
        public override bool Enabled { get => renderer.Settings.Debug.Enabled; set { renderer.Settings.Debug.Enabled = value; renderer.DebugDraw.Enabled = value; } }
        public override int DroppedLineCount => renderer.DebugDraw.DroppedLineCount;
        public override void Line(Vector3 a, Vector3 b, Color color, DiagnosticDepthMode depth, DiagnosticLifetime lifetime)
            => renderer.DebugDraw.Line(a, b, color.ToVector4(), Depth(depth), Lifetime(lifetime));
        public override void Box(BoundingBox bounds, Color color, DiagnosticDepthMode depth, DiagnosticLifetime lifetime)
            => renderer.DebugDraw.Box(bounds, color.ToVector4(), Depth(depth), Lifetime(lifetime));
        public override void OrientedBox(Matrix4x4 transform, Vector3 extents, Color color, DiagnosticDepthMode depth, DiagnosticLifetime lifetime)
            => renderer.DebugDraw.OrientedBox(transform, extents, color.ToVector4(), Depth(depth), Lifetime(lifetime));
        public override void Sphere(Vector3 center, float radius, Color color, int segments, DiagnosticDepthMode depth, DiagnosticLifetime lifetime)
            => renderer.DebugDraw.Sphere(center, radius, color.ToVector4(), segments, Depth(depth), Lifetime(lifetime));
        public override void Frustum(Matrix4x4 viewProjection, Color color, DiagnosticDepthMode depth, DiagnosticLifetime lifetime)
            => renderer.DebugDraw.Frustum(viewProjection, color.ToVector4(), Depth(depth), Lifetime(lifetime));
        public override void ClearPersistent() => renderer.DebugDraw.ClearPersistent();
        private static DebugDrawDepthMode Depth(DiagnosticDepthMode value) => Enum.IsDefined(value) ? (DebugDrawDepthMode)value : throw new ArgumentOutOfRangeException(nameof(value));
        private static DebugDrawLifetime Lifetime(DiagnosticLifetime value) => Enum.IsDefined(value) ? (DebugDrawLifetime)value : throw new ArgumentOutOfRangeException(nameof(value));
    }
}
