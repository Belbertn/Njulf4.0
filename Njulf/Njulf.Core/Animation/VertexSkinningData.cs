using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public readonly struct VertexJointIndices
    {
        public VertexJointIndices(ushort x, ushort y, ushort z, ushort w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public ushort X { get; }
        public ushort Y { get; }
        public ushort Z { get; }
        public ushort W { get; }

        public ushort this[int index] => index switch
        {
            0 => X,
            1 => Y,
            2 => Z,
            3 => W,
            _ => throw new System.ArgumentOutOfRangeException(nameof(index))
        };
    }

    public readonly struct VertexJointWeights
    {
        public VertexJointWeights(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float W { get; }

        public float this[int index] => index switch
        {
            0 => X,
            1 => Y,
            2 => Z,
            3 => W,
            _ => throw new System.ArgumentOutOfRangeException(nameof(index))
        };

        public float Sum => X + Y + Z + W;

        public VertexJointWeights Normalized()
        {
            float sum = Sum;
            return sum > float.Epsilon
                ? new VertexJointWeights(X / sum, Y / sum, Z / sum, W / sum)
                : new VertexJointWeights(1f, 0f, 0f, 0f);
        }
    }
}
