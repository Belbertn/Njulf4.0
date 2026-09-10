using Njulf.Core.Interfaces;
using Njulf.Core.Math;

namespace Njulf.Graphics;

public enum DiagnosticDepthMode { DepthTested, AlwaysVisible, XRay }
public enum DiagnosticLifetime { OneFrame, Persistent }

/// <summary>Optional world-space diagnostics. Submit before DrawScene; one-frame shapes are consumed by scene drawing.</summary>
public abstract class DiagnosticDrawing
{
    public abstract bool Enabled { get; set; }
    public abstract int DroppedLineCount { get; }
    public abstract void Line(Vector3 a, Vector3 b, Color color, DiagnosticDepthMode depth = DiagnosticDepthMode.DepthTested, DiagnosticLifetime lifetime = DiagnosticLifetime.OneFrame);
    public abstract void Box(BoundingBox bounds, Color color, DiagnosticDepthMode depth = DiagnosticDepthMode.DepthTested, DiagnosticLifetime lifetime = DiagnosticLifetime.OneFrame);
    public abstract void OrientedBox(Matrix4x4 transform, Vector3 extents, Color color, DiagnosticDepthMode depth = DiagnosticDepthMode.DepthTested, DiagnosticLifetime lifetime = DiagnosticLifetime.OneFrame);
    public abstract void Sphere(Vector3 center, float radius, Color color, int segments = 24, DiagnosticDepthMode depth = DiagnosticDepthMode.DepthTested, DiagnosticLifetime lifetime = DiagnosticLifetime.OneFrame);
    public abstract void Frustum(Matrix4x4 viewProjection, Color color, DiagnosticDepthMode depth = DiagnosticDepthMode.DepthTested, DiagnosticLifetime lifetime = DiagnosticLifetime.OneFrame);
    public abstract void ClearPersistent();
}

public abstract partial class GraphicsDevice
{
    public virtual DiagnosticDrawing DebugDraw => throw new NotSupportedException("Diagnostic drawing is unavailable on this device.");
}

public static class ScreenProjection
{
    /// <summary>Projects using the camera's unjittered Vulkan projection into a logical top-left viewport. Offscreen XY is allowed for clipping.</summary>
    public static bool TryProject(ICamera camera, Vector3 point, SpriteRectangle viewport, out Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return TryProject(camera.ViewProjectionMatrix, point, viewport, out position);
    }
    public static bool TryProject(Matrix4x4 matrix, Vector3 point, SpriteRectangle viewport, out Vector2 position)
    {
        position = default;
        if (!viewport.IsValid || viewport.Width <= 0 || viewport.Height <= 0) return false;
        float x = point.X * matrix.M11 + point.Y * matrix.M21 + point.Z * matrix.M31 + matrix.M41;
        float y = point.X * matrix.M12 + point.Y * matrix.M22 + point.Z * matrix.M32 + matrix.M42;
        float z = point.X * matrix.M13 + point.Y * matrix.M23 + point.Z * matrix.M33 + matrix.M43;
        float w = point.X * matrix.M14 + point.Y * matrix.M24 + point.Z * matrix.M34 + matrix.M44;
        if (!float.IsFinite(w) || w <= 1e-6f || !float.IsFinite(z) || z < 0 || z > w) return false;
        position = new(viewport.X + (x / w + 1) * .5f * viewport.Width, viewport.Y + (y / w + 1) * .5f * viewport.Height);
        return float.IsFinite(position.X) && float.IsFinite(position.Y);
    }
}
