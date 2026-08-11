using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
    /// <summary>
    /// The six Vulkan homogeneous clip boundaries used by directional-shadow
    /// caster diagnostics.  The order is deliberately the direct clip order,
    /// not the order of the shader's extracted-plane helper.
    /// </summary>
    public enum DirectionalShadowClipBoundary
    {
        Left = 0,
        Right = 1,
        Bottom = 2,
        Top = 3,
        Near = 4,
        Far = 5
    }

    /// <summary>
    /// Result of an independent, row-vector Vulkan clip-space sphere test.
    /// Signed distances are in world-space units after the homogeneous
    /// boundary gradient has been normalized; a sphere is accepted when every
    /// distance is at least <c>-Radius</c>.
    /// </summary>
    public readonly record struct DirectionalShadowClipReferenceResult(
        Vector4 ClipCenter,
        float Radius,
        float LeftSignedDistance,
        float RightSignedDistance,
        float BottomSignedDistance,
        float TopSignedDistance,
        float NearSignedDistance,
        float FarSignedDistance,
        DirectionalShadowClipBoundary? FirstRejectingBoundary,
        bool Accepted)
    {
        public float GetSignedDistance(DirectionalShadowClipBoundary boundary) => boundary switch
        {
            DirectionalShadowClipBoundary.Left => LeftSignedDistance,
            DirectionalShadowClipBoundary.Right => RightSignedDistance,
            DirectionalShadowClipBoundary.Bottom => BottomSignedDistance,
            DirectionalShadowClipBoundary.Top => TopSignedDistance,
            DirectionalShadowClipBoundary.Near => NearSignedDistance,
            DirectionalShadowClipBoundary.Far => FarSignedDistance,
            _ => throw new ArgumentOutOfRangeException(nameof(boundary))
        };
    }

    /// <summary>
    /// Immutable evidence retained by deterministic directional-shadow
    /// captures.  Matrix bytes are kept separately so a later GPU readback can
    /// prove it used the exact matrix that the CPU reference evaluated.
    /// </summary>
    public sealed record DirectionalShadowCullingFixture(
        byte[] ShadowDataBytes,
        int CascadeIndex,
        Vector3 CameraPosition,
        Vector3 LightDirection,
        float CascadeNearDistance,
        float CascadeFarDistance,
        Vector4 FittedLightSpaceExtents,
        Vector3 WorldCenter,
        float WorldRadius,
        Matrix4x4 Matrix,
        DirectionalShadowClipReferenceResult CpuReference)
    {
        public ulong MatrixHash => DirectionalShadowClipReference.ComputeMatrixHash(Matrix);
        public uint DiagnosticMatrixHash => DirectionalShadowClipReference.ComputeMatrixHash32(Matrix);
    }

    /// <summary>
    /// Independent CPU oracle for the directional-shadow caster test.
    ///
    /// This intentionally starts with the Vulkan clip inequalities instead of
    /// reproducing <c>SphereIntersectsRowMajorFrustum</c>'s extracted-plane
    /// implementation.  For each boundary it evaluates the linear
    /// homogeneous expression at the sphere centre and expands that expression
    /// by the norm of its world-space gradient.
    /// </summary>
    public static class DirectionalShadowClipReference
    {
        // A small scale-aware slack preserves conservative boundary behaviour
        // when CPU and GPU evaluate the same float matrix with different fused
        // multiply/add choices.  It is only used for classification, never for
        // the reported signed distances.
        private const float BoundaryRelativeEpsilon = 1.0e-5f;

        public static DirectionalShadowClipReferenceResult EvaluateSphere(
            Vector3 worldCenter,
            float worldRadius,
            Matrix4x4 rowVectorMatrix)
        {
            if (!float.IsFinite(worldRadius) || worldRadius < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(worldRadius));

            Vector4 clipCenter = TransformRowVectorPoint(worldCenter, rowVectorMatrix);
            Span<float> signedDistances = stackalloc float[6];
            DirectionalShadowClipBoundary? firstRejected = null;

            // Direct Vulkan clip inequalities for a row-vector transform:
            // -w <= x <= w, -w <= y <= w, 0 <= z <= w.
            EvaluateBoundary(
                clipCenter.X + clipCenter.W,
                rowVectorMatrix.M11 + rowVectorMatrix.M14,
                rowVectorMatrix.M21 + rowVectorMatrix.M24,
                rowVectorMatrix.M31 + rowVectorMatrix.M34,
                worldRadius,
                DirectionalShadowClipBoundary.Left,
                signedDistances,
                ref firstRejected);
            EvaluateBoundary(
                clipCenter.W - clipCenter.X,
                rowVectorMatrix.M14 - rowVectorMatrix.M11,
                rowVectorMatrix.M24 - rowVectorMatrix.M21,
                rowVectorMatrix.M34 - rowVectorMatrix.M31,
                worldRadius,
                DirectionalShadowClipBoundary.Right,
                signedDistances,
                ref firstRejected);
            EvaluateBoundary(
                clipCenter.Y + clipCenter.W,
                rowVectorMatrix.M12 + rowVectorMatrix.M14,
                rowVectorMatrix.M22 + rowVectorMatrix.M24,
                rowVectorMatrix.M32 + rowVectorMatrix.M34,
                worldRadius,
                DirectionalShadowClipBoundary.Bottom,
                signedDistances,
                ref firstRejected);
            EvaluateBoundary(
                clipCenter.W - clipCenter.Y,
                rowVectorMatrix.M14 - rowVectorMatrix.M12,
                rowVectorMatrix.M24 - rowVectorMatrix.M22,
                rowVectorMatrix.M34 - rowVectorMatrix.M32,
                worldRadius,
                DirectionalShadowClipBoundary.Top,
                signedDistances,
                ref firstRejected);
            EvaluateBoundary(
                clipCenter.Z,
                rowVectorMatrix.M13,
                rowVectorMatrix.M23,
                rowVectorMatrix.M33,
                worldRadius,
                DirectionalShadowClipBoundary.Near,
                signedDistances,
                ref firstRejected);
            EvaluateBoundary(
                clipCenter.W - clipCenter.Z,
                rowVectorMatrix.M14 - rowVectorMatrix.M13,
                rowVectorMatrix.M24 - rowVectorMatrix.M23,
                rowVectorMatrix.M34 - rowVectorMatrix.M33,
                worldRadius,
                DirectionalShadowClipBoundary.Far,
                signedDistances,
                ref firstRejected);

            return new DirectionalShadowClipReferenceResult(
                ClipCenter: clipCenter,
                Radius: worldRadius,
                LeftSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Left],
                RightSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Right],
                BottomSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Bottom],
                TopSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Top],
                NearSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Near],
                FarSignedDistance: signedDistances[(int)DirectionalShadowClipBoundary.Far],
                FirstRejectingBoundary: firstRejected,
                Accepted: firstRejected == null);
        }

        public static DirectionalShadowCullingFixture CreateFixture(
            in GPUShadowData shadowData,
            int cascadeIndex,
            Vector3 cameraPosition,
            Vector3 lightDirection,
            float cascadeNearDistance,
            float cascadeFarDistance,
            Vector4 fittedLightSpaceExtents,
            Vector3 worldCenter,
            float worldRadius)
        {
            Matrix4x4 matrix = GetCascadeMatrix(shadowData, cascadeIndex);
            GPUShadowData copiedData = shadowData;
            byte[] bytes = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref copiedData, 1)).ToArray();
            return new DirectionalShadowCullingFixture(
                ShadowDataBytes: bytes,
                CascadeIndex: cascadeIndex,
                CameraPosition: cameraPosition,
                LightDirection: lightDirection,
                CascadeNearDistance: cascadeNearDistance,
                CascadeFarDistance: cascadeFarDistance,
                FittedLightSpaceExtents: fittedLightSpaceExtents,
                WorldCenter: worldCenter,
                WorldRadius: worldRadius,
                Matrix: matrix,
                CpuReference: EvaluateSphere(worldCenter, worldRadius, matrix));
        }

        public static Matrix4x4 GetCascadeMatrix(in GPUShadowData shadowData, int cascadeIndex) => cascadeIndex switch
        {
            0 => shadowData.LightViewProjection0,
            1 => shadowData.LightViewProjection1,
            2 => shadowData.LightViewProjection2,
            3 => shadowData.LightViewProjection3,
            _ => throw new ArgumentOutOfRangeException(nameof(cascadeIndex))
        };

        public static ulong ComputeMatrixHash(in Matrix4x4 matrix)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    uint bits = BitConverter.SingleToUInt32Bits(matrix[row, column]);
                    hash ^= bits & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 8) & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 16) & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 24) & 0xffu;
                    hash *= prime;
                }
            }

            return hash;
        }

        /// <summary>
        /// The compact diagnostic shader's FNV-1a hash of the raw row-major
        /// matrix words. It is deliberately 32-bit so it is available on every
        /// Vulkan shader target without an int64 extension.
        /// </summary>
        public static uint ComputeMatrixHash32(in Matrix4x4 matrix)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    uint bits = BitConverter.SingleToUInt32Bits(matrix[row, column]);
                    hash ^= bits & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 8) & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 16) & 0xffu;
                    hash *= prime;
                    hash ^= (bits >> 24) & 0xffu;
                    hash *= prime;
                }
            }

            return hash;
        }

        /// <summary>
        /// Matches the conservative instance-scale contract used by directional
        /// meshlet compaction.  For the renderer's TRS instance transforms the
        /// longest transformed unit axis is the exact largest scale factor.
        /// </summary>
        public static float ComputeConservativeWorldRadius(
            float localRadius,
            in Matrix4x4 instanceTransform)
        {
            if (!float.IsFinite(localRadius) || localRadius < 0.0f)
                throw new ArgumentOutOfRangeException(nameof(localRadius));

            float xLength = MathF.Sqrt(
                instanceTransform.M11 * instanceTransform.M11 +
                instanceTransform.M12 * instanceTransform.M12 +
                instanceTransform.M13 * instanceTransform.M13);
            float yLength = MathF.Sqrt(
                instanceTransform.M21 * instanceTransform.M21 +
                instanceTransform.M22 * instanceTransform.M22 +
                instanceTransform.M23 * instanceTransform.M23);
            float zLength = MathF.Sqrt(
                instanceTransform.M31 * instanceTransform.M31 +
                instanceTransform.M32 * instanceTransform.M32 +
                instanceTransform.M33 * instanceTransform.M33);
            float maxScale = MathF.Max(xLength, MathF.Max(yLength, zLength));
            return localRadius * maxScale;
        }

        private static Vector4 TransformRowVectorPoint(Vector3 point, Matrix4x4 matrix) => new(
            point.X * matrix.M11 + point.Y * matrix.M21 + point.Z * matrix.M31 + matrix.M41,
            point.X * matrix.M12 + point.Y * matrix.M22 + point.Z * matrix.M32 + matrix.M42,
            point.X * matrix.M13 + point.Y * matrix.M23 + point.Z * matrix.M33 + matrix.M43,
            point.X * matrix.M14 + point.Y * matrix.M24 + point.Z * matrix.M34 + matrix.M44);

        private static void EvaluateBoundary(
            float centreValue,
            float gradientX,
            float gradientY,
            float gradientZ,
            float radius,
            DirectionalShadowClipBoundary boundary,
            Span<float> signedDistances,
            ref DirectionalShadowClipBoundary? firstRejected)
        {
            float gradientLength = MathF.Sqrt(
                gradientX * gradientX +
                gradientY * gradientY +
                gradientZ * gradientZ);
            float signedDistance;
            bool accepted;
            if (!float.IsFinite(centreValue) || !float.IsFinite(gradientLength))
            {
                signedDistance = float.NegativeInfinity;
                accepted = false;
            }
            else if (gradientLength <= float.Epsilon)
            {
                // Degenerate boundaries do not occur for supported fitted
                // cascades.  Treat a non-negative constant as satisfied and a
                // negative one as rejected instead of silently dividing by 0.
                signedDistance = centreValue >= 0.0f
                    ? float.PositiveInfinity
                    : float.NegativeInfinity;
                accepted = centreValue >= 0.0f;
            }
            else
            {
                signedDistance = centreValue / gradientLength;
                float epsilon = BoundaryRelativeEpsilon * MathF.Max(
                    1.0f,
                    MathF.Max(MathF.Abs(signedDistance), radius));
                accepted = signedDistance >= -radius - epsilon;
            }

            signedDistances[(int)boundary] = signedDistance;
            if (!accepted && firstRejected == null)
                firstRejected = boundary;
        }
    }
}
