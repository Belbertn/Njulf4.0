using Njulf.Core.Math;

namespace Njulf.Core.Vfx
{
    public enum ParticleSpawnShapeKind : uint
    {
        Point = 0,
        Sphere = 1,
        SphereShell = 2,
        Box = 3,
        Cone = 4,
        Ring = 5,
        Line = 6
    }

    public readonly record struct ParticleSpawnShape(
        ParticleSpawnShapeKind Kind,
        Vector3 Extents,
        float Radius,
        float InnerRadius,
        float AngleRadians,
        float Length)
    {
        public static ParticleSpawnShape Point() => new(
            ParticleSpawnShapeKind.Point,
            Vector3.Zero,
            0.0f,
            0.0f,
            0.0f,
            0.0f);

        public static ParticleSpawnShape Sphere(float radius) => new(
            ParticleSpawnShapeKind.Sphere,
            Vector3.Zero,
            ClampMin(radius, 0.0f),
            0.0f,
            0.0f,
            0.0f);

        public static ParticleSpawnShape SphereShell(float radius) => new(
            ParticleSpawnShapeKind.SphereShell,
            Vector3.Zero,
            ClampMin(radius, 0.0f),
            0.0f,
            0.0f,
            0.0f);

        public static ParticleSpawnShape Box(Vector3 extents) => new(
            ParticleSpawnShapeKind.Box,
            Vector3.Max(extents, Vector3.Zero),
            0.0f,
            0.0f,
            0.0f,
            0.0f);

        public static ParticleSpawnShape Cone(float radius, float angleRadians, float length) => new(
            ParticleSpawnShapeKind.Cone,
            Vector3.Zero,
            ClampMin(radius, 0.0f),
            0.0f,
            ClampMin(angleRadians, 0.0f),
            ClampMin(length, 0.0f));

        public static ParticleSpawnShape Ring(float innerRadius, float outerRadius) => new(
            ParticleSpawnShapeKind.Ring,
            Vector3.Zero,
            ClampMin(outerRadius, 0.0f),
            ClampMin(innerRadius, 0.0f),
            0.0f,
            0.0f);

        public static ParticleSpawnShape Line(float length) => new(
            ParticleSpawnShapeKind.Line,
            Vector3.Zero,
            0.0f,
            0.0f,
            0.0f,
            ClampMin(length, 0.0f));

        private static float ClampMin(float value, float min)
        {
            return value < min ? min : value;
        }
    }
}
