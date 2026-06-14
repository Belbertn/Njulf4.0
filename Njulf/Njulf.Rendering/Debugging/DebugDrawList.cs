using Njulf.Core.Math;

namespace Njulf.Rendering.Debug
{
    public sealed class DebugDrawList
    {
        public const int DefaultMaxLineSegments = 65_536;
        private const int MinSphereSegments = 8;
        private const int MaxSphereSegments = 128;

        private readonly object _lock = new();
        private readonly List<DebugDrawCommand> _frameLines = new();
        private readonly List<DebugDrawCommand> _persistentLines = new();
        private int _droppedLineCount;
        private int _maxLineSegments = DefaultMaxLineSegments;

        public bool Enabled { get; set; }

        public int MaxLineSegments
        {
            get => _maxLineSegments;
            set => _maxLineSegments = Math.Clamp(value, 0, 1_000_000);
        }

        public int DroppedLineCount
        {
            get
            {
                lock (_lock)
                    return _droppedLineCount;
            }
        }

        public void Line(
            Vector3 a,
            Vector3 b,
            Vector4 color,
            DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested,
            DebugDrawLifetime lifetime = DebugDrawLifetime.OneFrame)
        {
            AddLine(new DebugDrawCommand(new DebugLine(a, b, color), depthMode), lifetime);
        }

        public void Box(
            BoundingBox bounds,
            Vector4 color,
            DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested,
            DebugDrawLifetime lifetime = DebugDrawLifetime.OneFrame)
        {
            Vector3 min = bounds.Min;
            Vector3 max = bounds.Max;
            Span<Vector3> corners = stackalloc Vector3[8]
            {
                new(min.X, min.Y, min.Z),
                new(max.X, min.Y, min.Z),
                new(max.X, max.Y, min.Z),
                new(min.X, max.Y, min.Z),
                new(min.X, min.Y, max.Z),
                new(max.X, min.Y, max.Z),
                new(max.X, max.Y, max.Z),
                new(min.X, max.Y, max.Z)
            };

            AddBoxEdges(corners, color, depthMode, lifetime);
        }

        public void OrientedBox(
            Matrix4x4 transform,
            Vector3 extents,
            Vector4 color,
            DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested,
            DebugDrawLifetime lifetime = DebugDrawLifetime.OneFrame)
        {
            Vector3 e = new(MathF.Abs(extents.X), MathF.Abs(extents.Y), MathF.Abs(extents.Z));
            Span<Vector3> corners = stackalloc Vector3[8]
            {
                TransformPoint(new Vector3(-e.X, -e.Y, -e.Z), transform),
                TransformPoint(new Vector3(e.X, -e.Y, -e.Z), transform),
                TransformPoint(new Vector3(e.X, e.Y, -e.Z), transform),
                TransformPoint(new Vector3(-e.X, e.Y, -e.Z), transform),
                TransformPoint(new Vector3(-e.X, -e.Y, e.Z), transform),
                TransformPoint(new Vector3(e.X, -e.Y, e.Z), transform),
                TransformPoint(new Vector3(e.X, e.Y, e.Z), transform),
                TransformPoint(new Vector3(-e.X, e.Y, e.Z), transform)
            };

            AddBoxEdges(corners, color, depthMode, lifetime);
        }

        public void Sphere(
            Vector3 center,
            float radius,
            Vector4 color,
            int segments = 24,
            DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested,
            DebugDrawLifetime lifetime = DebugDrawLifetime.OneFrame)
        {
            if (radius <= 0.0f || float.IsNaN(radius) || float.IsInfinity(radius))
                return;

            int clampedSegments = Math.Clamp(segments, MinSphereSegments, MaxSphereSegments);
            float step = MathF.Tau / clampedSegments;
            for (int i = 0; i < clampedSegments; i++)
            {
                float a0 = i * step;
                float a1 = (i + 1) * step;
                float c0 = MathF.Cos(a0);
                float s0 = MathF.Sin(a0);
                float c1 = MathF.Cos(a1);
                float s1 = MathF.Sin(a1);

                Line(
                    center + new Vector3(c0 * radius, s0 * radius, 0.0f),
                    center + new Vector3(c1 * radius, s1 * radius, 0.0f),
                    color,
                    depthMode,
                    lifetime);
                Line(
                    center + new Vector3(c0 * radius, 0.0f, s0 * radius),
                    center + new Vector3(c1 * radius, 0.0f, s1 * radius),
                    color,
                    depthMode,
                    lifetime);
                Line(
                    center + new Vector3(0.0f, c0 * radius, s0 * radius),
                    center + new Vector3(0.0f, c1 * radius, s1 * radius),
                    color,
                    depthMode,
                    lifetime);
            }
        }

        public void Frustum(
            Matrix4x4 viewProjection,
            Vector4 color,
            DebugDrawDepthMode depthMode = DebugDrawDepthMode.DepthTested,
            DebugDrawLifetime lifetime = DebugDrawLifetime.OneFrame)
        {
            Matrix4x4 inverse;
            try
            {
                inverse = viewProjection.Invert();
            }
            catch (InvalidOperationException)
            {
                return;
            }

            Span<Vector3> corners = stackalloc Vector3[8]
            {
                TransformClipCorner(-1.0f, -1.0f, 0.0f, inverse),
                TransformClipCorner(1.0f, -1.0f, 0.0f, inverse),
                TransformClipCorner(1.0f, 1.0f, 0.0f, inverse),
                TransformClipCorner(-1.0f, 1.0f, 0.0f, inverse),
                TransformClipCorner(-1.0f, -1.0f, 1.0f, inverse),
                TransformClipCorner(1.0f, -1.0f, 1.0f, inverse),
                TransformClipCorner(1.0f, 1.0f, 1.0f, inverse),
                TransformClipCorner(-1.0f, 1.0f, 1.0f, inverse)
            };

            AddBoxEdges(corners, color, depthMode, lifetime);
        }

        public DebugDrawFrameSnapshot Snapshot()
        {
            lock (_lock)
            {
                if (!Enabled)
                    return new DebugDrawFrameSnapshot(
                        Array.Empty<DebugDrawCommand>(),
                        LineCount: 0,
                        PersistentLineCount: _persistentLines.Count,
                        DroppedLineCount: _droppedLineCount);

                int count = _persistentLines.Count + _frameLines.Count;
                var lines = new DebugDrawCommand[count];
                _persistentLines.CopyTo(lines, 0);
                _frameLines.CopyTo(lines, _persistentLines.Count);
                return new DebugDrawFrameSnapshot(lines, count, _persistentLines.Count, _droppedLineCount);
            }
        }

        public void ClearFrame()
        {
            lock (_lock)
            {
                _frameLines.Clear();
                _droppedLineCount = 0;
            }
        }

        public void ClearPersistent()
        {
            lock (_lock)
                _persistentLines.Clear();
        }

        private void AddBoxEdges(
            Span<Vector3> corners,
            Vector4 color,
            DebugDrawDepthMode depthMode,
            DebugDrawLifetime lifetime)
        {
            Line(corners[0], corners[1], color, depthMode, lifetime);
            Line(corners[1], corners[2], color, depthMode, lifetime);
            Line(corners[2], corners[3], color, depthMode, lifetime);
            Line(corners[3], corners[0], color, depthMode, lifetime);
            Line(corners[4], corners[5], color, depthMode, lifetime);
            Line(corners[5], corners[6], color, depthMode, lifetime);
            Line(corners[6], corners[7], color, depthMode, lifetime);
            Line(corners[7], corners[4], color, depthMode, lifetime);
            Line(corners[0], corners[4], color, depthMode, lifetime);
            Line(corners[1], corners[5], color, depthMode, lifetime);
            Line(corners[2], corners[6], color, depthMode, lifetime);
            Line(corners[3], corners[7], color, depthMode, lifetime);
        }

        private void AddLine(DebugDrawCommand command, DebugDrawLifetime lifetime)
        {
            lock (_lock)
            {
                if (_frameLines.Count + _persistentLines.Count >= _maxLineSegments)
                {
                    _droppedLineCount++;
                    return;
                }

                if (lifetime == DebugDrawLifetime.Persistent)
                    _persistentLines.Add(command);
                else
                    _frameLines.Add(command);
            }
        }

        private static Vector3 TransformPoint(Vector3 point, Matrix4x4 matrix)
        {
            return TransformHomogeneous(point.X, point.Y, point.Z, 1.0f, matrix);
        }

        private static Vector3 TransformClipCorner(float x, float y, float z, Matrix4x4 inverseViewProjection)
        {
            return TransformHomogeneous(x, y, z, 1.0f, inverseViewProjection);
        }

        private static Vector3 TransformHomogeneous(float x, float y, float z, float w, Matrix4x4 matrix)
        {
            float tx = x * matrix.M11 + y * matrix.M21 + z * matrix.M31 + w * matrix.M41;
            float ty = x * matrix.M12 + y * matrix.M22 + z * matrix.M32 + w * matrix.M42;
            float tz = x * matrix.M13 + y * matrix.M23 + z * matrix.M33 + w * matrix.M43;
            float tw = x * matrix.M14 + y * matrix.M24 + z * matrix.M34 + w * matrix.M44;
            return tw == 0.0f
                ? new Vector3(tx, ty, tz)
                : new Vector3(tx / tw, ty / tw, tz / tw);
        }
    }
}
