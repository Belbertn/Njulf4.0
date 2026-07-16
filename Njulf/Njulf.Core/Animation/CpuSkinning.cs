using System;
using Njulf.Core.Math;

namespace Njulf.Core.Animation
{
    public static class CpuSkinning
    {
        public static Vector3 SkinPosition(
            Vector3 bindPosition,
            VertexJointIndices jointIndices,
            VertexJointWeights jointWeights,
            ReadOnlySpan<Matrix4x4> skinMatrices)
        {
            Vector3 result = Vector3.Zero;
            VertexJointWeights weights = jointWeights.Normalized();

            for (int i = 0; i < 4; i++)
            {
                float weight = weights[i];
                if (weight <= 0f)
                    continue;

                int joint = jointIndices[i];
                if (joint < 0 || joint >= skinMatrices.Length)
                    throw new ArgumentOutOfRangeException(nameof(jointIndices), $"Vertex references skin matrix {joint}, but only {skinMatrices.Length} matrices are available.");

                result += (bindPosition * skinMatrices[joint]) * weight;
            }

            return result;
        }

        public static Vector3 SkinDirection(
            Vector3 bindDirection,
            VertexJointIndices jointIndices,
            VertexJointWeights jointWeights,
            ReadOnlySpan<Matrix4x4> skinMatrices)
        {
            Vector3 result = Vector3.Zero;
            VertexJointWeights weights = jointWeights.Normalized();

            for (int i = 0; i < 4; i++)
            {
                float weight = weights[i];
                if (weight <= 0f)
                    continue;

                int joint = jointIndices[i];
                if (joint < 0 || joint >= skinMatrices.Length)
                    throw new ArgumentOutOfRangeException(nameof(jointIndices), $"Vertex references skin matrix {joint}, but only {skinMatrices.Length} matrices are available.");

                result += TransformDirection(bindDirection, skinMatrices[joint]) * weight;
            }

            return result.Normalized();
        }

        private static Vector3 TransformDirection(Vector3 direction, Matrix4x4 transform)
        {
            return new Vector3(
                direction.X * transform.M11 + direction.Y * transform.M21 + direction.Z * transform.M31,
                direction.X * transform.M12 + direction.Y * transform.M22 + direction.Z * transform.M32,
                direction.X * transform.M13 + direction.Y * transform.M23 + direction.Z * transform.M33);
        }
    }
}
