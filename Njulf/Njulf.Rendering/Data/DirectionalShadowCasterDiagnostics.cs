using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
    public enum DirectionalShadowCasterClass : uint
    {
        Unknown = 0u,
        Static = 1u,
        Dynamic = 2u,
        Foliage = 3u
    }

    /// <summary>
    /// One bounded GPU attribution record.  ObjectId currently aliases the
    /// scene instance/object-table identity because that is the canonical ID
    /// present in the meshlet draw command.
    /// </summary>
    public readonly record struct DirectionalShadowCasterAttribution(
        uint ObjectId,
        uint InstanceId,
        uint MeshletId,
        uint SelectedLod,
        DirectionalShadowCasterClass CasterClass,
        uint CascadeIndex,
        uint CandidateIndex,
        uint EligibilityFlags,
        ulong MatrixHash,
        int Accepted,
        int FirstRejectingPlane,
        float FirstRejectingSignedDistance,
        Vector3 WorldCenter,
        float WorldRadius,
        Vector4 ClipCenter,
        float[] SignedPlaneDistances)
    {
        public bool HasExactBoundaryEvidence => SignedPlaneDistances is { Length: 6 };

        /// <summary>Frame serial captured with the exact matrix bank.</summary>
        public ulong FrameSerial { get; init; }
        /// <summary>Directional shadow image-pair generation for this record.</summary>
        public uint ResourceGeneration { get; init; }
        /// <summary>Whether the GPU bank ownership stamp matched the retained frame slot.</summary>
        public int FrameGenerationMatchesCapturedSlot { get; init; }
        /// <summary>Whether an exact CPU matrix/bounds comparison was available.</summary>
        public int CpuReferenceAvailable { get; init; }
        /// <summary>Independent direct-clip CPU result for the same record.</summary>
        public DirectionalShadowClipReferenceResult CpuReference { get; init; }
        /// <summary>Whether the GPU record hashes the retained matrix bytes.</summary>
        public int MatrixMatchesCapturedBytes { get; init; }
        /// <summary>Whether the CPU and GPU accepted/rejected the same sphere.</summary>
        public int CpuGpuDecisionMatches { get; init; }
        /// <summary>Whether direct CPU and GPU clip coordinates agree within float tolerance.</summary>
        public int ClipCoordinatesMatch { get; init; }
    }

    /// <summary>
    /// Fixed-capacity readback from the diagnostic compaction pipeline.  The
    /// sampled count is intentionally allowed to exceed <see cref="Records"/>
    /// when deterministic sampling selects more candidates than the bounded
    /// capture bank can retain.
    /// </summary>
    public readonly record struct DirectionalShadowCasterDiagnostics(
        int ReadbackValid,
        uint SampledCandidateCount,
        uint DroppedRecordCount,
        DirectionalShadowCasterAttribution[] Records)
    {
        public static DirectionalShadowCasterDiagnostics Empty { get; } = new(
            ReadbackValid: 0,
            SampledCandidateCount: 0u,
            DroppedRecordCount: 0u,
            Records: Array.Empty<DirectionalShadowCasterAttribution>());

        /// <summary>Frame serial stamped into the same GPU diagnostic bank.</summary>
        public ulong GpuFrameSerial { get; init; }
        /// <summary>Shadow-resource generation stamped into the same GPU diagnostic bank.</summary>
        public uint GpuResourceGeneration { get; init; }
        /// <summary>Whether the completed bank carried an explicit frame ownership stamp.</summary>
        public int FrameMetadataValid { get; init; }
    }

    /// <summary>
    /// Frame-slot capture paired with the bounded delayed diagnostic readback.
    /// It preserves the raw GPUShadowData bytes instead of consulting mutable
    /// current-frame shadow state after a fence completes.
    /// </summary>
    public readonly record struct DirectionalShadowCasterFrameCapture(
        int Valid,
        ulong FrameSerial,
        uint ResourceGeneration,
        int CascadeCount,
        Vector3 CameraPosition,
        Vector3 LightDirection,
        GPUShadowData ShadowData,
        byte[] ShadowDataBytes)
    {
        public static DirectionalShadowCasterFrameCapture Empty { get; } = new(
            Valid: 0,
            FrameSerial: 0UL,
            ResourceGeneration: 0u,
            CascadeCount: 0,
            CameraPosition: Vector3.Zero,
            LightDirection: Vector3.Zero,
            ShadowData: default,
            ShadowDataBytes: Array.Empty<byte>());

        public static DirectionalShadowCasterFrameCapture Create(
            ulong frameSerial,
            uint resourceGeneration,
            int cascadeCount,
            Vector3 cameraPosition,
            Vector3 lightDirection,
            in GPUShadowData shadowData)
        {
            GPUShadowData copy = shadowData;
            byte[] bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)).ToArray();
            return new DirectionalShadowCasterFrameCapture(
                Valid: 1,
                FrameSerial: frameSerial,
                ResourceGeneration: resourceGeneration,
                CascadeCount: Math.Clamp(cascadeCount, 0, ShadowSettings.MaxDirectionalCascades),
                CameraPosition: cameraPosition,
                LightDirection: lightDirection,
                ShadowData: copy,
                ShadowDataBytes: bytes);
        }
    }

    /// <summary>
    /// Joins a completed bounded GPU capture with its same-frame matrix bank.
    /// No shader decision is reused here: acceptance is recomputed by the
    /// independent direct-clip CPU oracle.
    /// </summary>
    public static class DirectionalShadowCasterDiagnosticsEvaluator
    {
        public static DirectionalShadowCasterDiagnostics AttachCpuReference(
            in DirectionalShadowCasterDiagnostics diagnostics,
            in DirectionalShadowCasterFrameCapture capture)
        {
            if (diagnostics.ReadbackValid == 0 || capture.Valid == 0 || diagnostics.Records.Length == 0)
                return diagnostics;

            bool sameFrameGeneration = diagnostics.FrameMetadataValid != 0 &&
                diagnostics.GpuFrameSerial == capture.FrameSerial &&
                diagnostics.GpuResourceGeneration == capture.ResourceGeneration;
            var records = new DirectionalShadowCasterAttribution[diagnostics.Records.Length];
            for (int index = 0; index < records.Length; index++)
            {
                DirectionalShadowCasterAttribution record = diagnostics.Records[index];
                if (!sameFrameGeneration || record.CascadeIndex >= (uint)capture.CascadeCount)
                {
                    records[index] = record with
                    {
                        FrameSerial = diagnostics.GpuFrameSerial,
                        ResourceGeneration = diagnostics.GpuResourceGeneration,
                        FrameGenerationMatchesCapturedSlot = 0,
                        CpuReferenceAvailable = 0,
                        MatrixMatchesCapturedBytes = 0,
                        CpuGpuDecisionMatches = 0,
                        ClipCoordinatesMatch = 0
                    };
                    continue;
                }

                Matrix4x4 matrix = DirectionalShadowClipReference.GetCascadeMatrix(
                    capture.ShadowData,
                    checked((int)record.CascadeIndex));
                DirectionalShadowClipReferenceResult reference =
                    DirectionalShadowClipReference.EvaluateSphere(record.WorldCenter, record.WorldRadius, matrix);
                // Verify the retained raw byte image before using its decoded
                // struct. This keeps the matrix comparison tied to the exact
                // same GPUShadowData payload captured for the completed slot.
                bool matrixMatches = CaptureBytesMatch(capture) &&
                    unchecked((uint)record.MatrixHash) ==
                    DirectionalShadowClipReference.ComputeMatrixHash32(matrix);
                bool clipMatches = NearlyEqual(reference.ClipCenter.X, record.ClipCenter.X) &&
                    NearlyEqual(reference.ClipCenter.Y, record.ClipCenter.Y) &&
                    NearlyEqual(reference.ClipCenter.Z, record.ClipCenter.Z) &&
                    NearlyEqual(reference.ClipCenter.W, record.ClipCenter.W);
                records[index] = record with
                {
                    FrameSerial = diagnostics.GpuFrameSerial,
                    ResourceGeneration = diagnostics.GpuResourceGeneration,
                    FrameGenerationMatchesCapturedSlot = 1,
                    CpuReferenceAvailable = 1,
                    CpuReference = reference,
                    MatrixMatchesCapturedBytes = matrixMatches ? 1 : 0,
                    CpuGpuDecisionMatches = matrixMatches && clipMatches &&
                        reference.Accepted == (record.Accepted != 0)
                        ? 1
                        : 0,
                    ClipCoordinatesMatch = clipMatches ? 1 : 0
                };
            }

            return diagnostics with { Records = records };
        }

        private static bool NearlyEqual(float left, float right)
        {
            if (!float.IsFinite(left) || !float.IsFinite(right))
                return left.Equals(right);
            float tolerance = 2.0e-4f * MathF.Max(1.0f, MathF.Max(MathF.Abs(left), MathF.Abs(right)));
            return MathF.Abs(left - right) <= tolerance;
        }

        private static bool CaptureBytesMatch(in DirectionalShadowCasterFrameCapture capture)
        {
            GPUShadowData shadowData = capture.ShadowData;
            ReadOnlySpan<byte> retainedBytes = MemoryMarshal.AsBytes(
                MemoryMarshal.CreateReadOnlySpan(ref shadowData, 1));
            return capture.ShadowDataBytes.Length == retainedBytes.Length &&
                capture.ShadowDataBytes.AsSpan().SequenceEqual(retainedBytes);
        }
    }
}
