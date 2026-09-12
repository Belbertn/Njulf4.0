using Jitter2.LinearMath;
using Njulf.Core.Math;

namespace Njulf.Physics;

internal static class PhysicsMath
{
    internal static JVector ToJ(Vector3 v) => new(v.X, v.Y, v.Z);
    internal static Vector3 FromJ(JVector v) => new(v.X, v.Y, v.Z);
    // Njulf applies its quaternion matrix to row vectors. Jitter applies column rotations.
    internal static JQuaternion ToJ(Quaternion q) => new(-q.X, -q.Y, -q.Z, q.W);
    internal static Quaternion FromJ(JQuaternion q) => new(-q.X, -q.Y, -q.Z, q.W);
    internal static void Finite(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            throw new ArgumentException("Vector components must be finite.");
    }
    internal static float Positive(float value)
    {
        if (!float.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value;
    }
    internal static PhysicsPose Validate(PhysicsPose pose)
    {
        Finite(pose.Position);
        float length = pose.Rotation.LengthSquared();
        if (!float.IsFinite(length) || length < 1e-12f) throw new ArgumentException("Rotation must be finite and nonzero.");
        return pose with { Rotation = pose.Rotation.Normalized() };
    }
    internal static (PhysicsPose Pose, Vector3 Scale) Decompose(Matrix4x4 m)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (!float.IsFinite(m[r, c])) throw new ArgumentException("Transform must be finite.");
        Vector3 x = new(m.M11, m.M12, m.M13), y = new(m.M21, m.M22, m.M23), z = new(m.M31, m.M32, m.M33);
        Vector3 scale = new(Positive(x.Length()), Positive(y.Length()), Positive(z.Length()));
        x /= scale.X; y /= scale.Y; z /= scale.Z;
        if (MathF.Abs(m.M14) > 1e-6f || MathF.Abs(m.M24) > 1e-6f || MathF.Abs(m.M34) > 1e-6f || MathF.Abs(m.M44 - 1) > 1e-6f ||
            MathF.Abs(Vector3.Dot(x, y)) > 1e-5f || MathF.Abs(Vector3.Dot(x, z)) > 1e-5f || MathF.Abs(Vector3.Dot(y, z)) > 1e-5f ||
            Vector3.Dot(Vector3.Cross(x, y), z) < .9999f)
            throw new ArgumentException("Physics transforms must be affine TRS with positive scale; shear and reflection are unsupported.");
        var rotation = Quaternion.FromMatrix4x4(new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1)).Normalized();
        return (new(m.Translation, rotation), scale);
    }
    internal static float UniformScale(Vector3 scale)
    {
        if (MathF.Abs(scale.X - scale.Y) > 1e-5f * scale.X || MathF.Abs(scale.X - scale.Z) > 1e-5f * scale.X)
            throw new ArgumentException("Bound nodes require positive uniform world scale. Bake other scale into shape geometry.");
        return scale.X;
    }
}
