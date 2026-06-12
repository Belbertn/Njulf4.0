using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public readonly struct AnimationTransform
    {
        public static readonly AnimationTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

        public AnimationTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Translation = translation;
            Rotation = rotation.Normalized();
            Scale = scale;
        }

        public Vector3 Translation { get; }
        public Quaternion Rotation { get; }
        public Vector3 Scale { get; }

        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.CreateScale(Scale) * Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(Translation);
        }

        public static AnimationTransform Lerp(AnimationTransform a, AnimationTransform b, float amount)
        {
            amount = System.Math.Clamp(amount, 0f, 1f);
            return new AnimationTransform(
                Vector3.Lerp(a.Translation, b.Translation, amount),
                Quaternion.Slerp(a.Rotation, b.Rotation, amount).Normalized(),
                Vector3.Lerp(a.Scale, b.Scale, amount));
        }
    }
}
