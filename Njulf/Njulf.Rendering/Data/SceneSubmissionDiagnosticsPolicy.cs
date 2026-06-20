using System;
using System.Collections.Generic;
using Njulf.Rendering.Pipeline;

namespace Njulf.Rendering.Data
{
    internal readonly record struct SceneSubmissionValidationCommandKey(
        uint MeshletIndex,
        uint InstanceId,
        uint MeshIndex,
        uint MaterialIndex) : IComparable<SceneSubmissionValidationCommandKey>
    {
        public int CompareTo(SceneSubmissionValidationCommandKey other)
        {
            int compare = MeshletIndex.CompareTo(other.MeshletIndex);
            if (compare != 0)
                return compare;
            compare = InstanceId.CompareTo(other.InstanceId);
            if (compare != 0)
                return compare;
            compare = MeshIndex.CompareTo(other.MeshIndex);
            if (compare != 0)
                return compare;
            return MaterialIndex.CompareTo(other.MaterialIndex);
        }

        public override string ToString()
        {
            return $"obj={InstanceId}, mesh={MeshIndex}, meshlet={MeshletIndex}, mat={MaterialIndex}";
        }
    }

    internal static class SceneSubmissionDiagnosticsPolicy
    {
        public static string BuildFallbackReason(
            SceneRenderingData sceneData,
            SceneSubmissionCounterSnapshot counters,
            SceneSubmissionValidationSnapshot validation)
        {
            if (!sceneData.SceneSubmissionGpuCompactionEnabled)
                return "GPU compaction disabled";

            if (sceneData.SceneSubmissionValidationCompareCpuGpuLists &&
                validation.Valid != 0 &&
                validation.MismatchCount > 0 &&
                IsActionableValidationMismatch(validation))
            {
                string detail = string.IsNullOrWhiteSpace(validation.FirstMismatch)
                    ? validation.Status
                    : validation.FirstMismatch;
                return string.IsNullOrWhiteSpace(detail)
                    ? "previous CPU/GPU validation mismatch"
                    : $"previous CPU/GPU validation mismatch: {detail}";
            }

            if (counters.OverflowCount > 0)
            {
                return "previous GPU opaque compaction overflow: " +
                    $"emitted={counters.EmittedCount}, overflow={counters.OverflowCount}";
            }

            uint depthOverflow = counters.SolidDepthOverflowCount + counters.MaskedDepthOverflowCount;
            if (depthOverflow > 0)
            {
                return "previous GPU depth compaction overflow: " +
                    $"solid={counters.SolidDepthOverflowCount}, masked={counters.MaskedDepthOverflowCount}";
            }

            ulong directionalShadowOverflow =
                Sum(counters.DirectionalStaticShadowOverflowCounts) +
                Sum(counters.DirectionalDynamicShadowOverflowCounts);
            if (directionalShadowOverflow > 0)
                return $"previous GPU directional shadow compaction overflow: overflow={directionalShadowOverflow}";

            if (!HasEligibleGpuSubmission(sceneData))
                return "no eligible opaque/depth/shadow meshlets for GPU scene submission";

            return string.Empty;
        }

        public static SceneSubmissionMode ResolveMode(SceneRenderingData sceneData)
        {
            if (sceneData.SceneSubmissionGpuCompactionActive && sceneData.SceneSubmissionFallbackReason.Length == 0)
            {
                return sceneData.SceneSubmissionIndirectMeshletDispatchEnabled
                    ? SceneSubmissionMode.GpuCompactedIndirect
                    : SceneSubmissionMode.GpuCompactedDirect;
            }

            if (!sceneData.SceneSubmissionGpuCompactionEnabled)
                return SceneSubmissionMode.Cpu;

            if (sceneData.SceneSubmissionFallbackReason.StartsWith("no eligible ", StringComparison.Ordinal))
                return SceneSubmissionMode.Cpu;

            if (sceneData.SceneSubmissionFallbackReason.Length > 0)
                return SceneSubmissionMode.CpuFallback;

            return SceneSubmissionMode.Cpu;
        }

        public static SceneSubmissionValidationSnapshot CompareValidationSamples(
            IReadOnlyList<SceneSubmissionValidationCommandKey> expectedKeys,
            IReadOnlyList<SceneSubmissionValidationCommandKey> gpuKeys,
            int cpuCount,
            int gpuCount,
            uint overflowCount,
            int sampleLimit,
            bool hiZEnabled,
            bool gpuLodSelectionEnabled,
            bool compareFullSample)
        {
            var sortedExpected = new SceneSubmissionValidationCommandKey[compareFullSample ? expectedKeys.Count : 0];
            for (int i = 0; i < sortedExpected.Length; i++)
                sortedExpected[i] = expectedKeys[i];

            var sortedGpu = new SceneSubmissionValidationCommandKey[compareFullSample ? gpuKeys.Count : 0];
            for (int i = 0; i < sortedGpu.Length; i++)
                sortedGpu[i] = gpuKeys[i];

            Array.Sort(sortedExpected);
            Array.Sort(sortedGpu);

            int compared = Math.Min(sortedExpected.Length, sortedGpu.Length);
            int mismatches = cpuCount == gpuCount ? 0 : 1;
            string firstMismatch = cpuCount == gpuCount
                ? string.Empty
                : $"count cpu={cpuCount} gpu={gpuCount}";

            for (int i = 0; i < compared; i++)
            {
                if (sortedExpected[i].Equals(sortedGpu[i]))
                    continue;

                mismatches++;
                if (firstMismatch.Length == 0)
                    firstMismatch = $"sample[{i}] cpu={sortedExpected[i]} gpu={sortedGpu[i]}";
            }

            int sampleDelta = Math.Abs(sortedExpected.Length - sortedGpu.Length);
            if (sampleDelta > 0)
            {
                mismatches += sampleDelta;
                if (firstMismatch.Length == 0)
                    firstMismatch = $"sample-count cpu={sortedExpected.Length} gpu={sortedGpu.Length}";
            }

            if (overflowCount > 0 && firstMismatch.Length == 0)
                firstMismatch = $"overflow={overflowCount}";

            string status;
            if (overflowCount > 0)
                status = "overflow";
            else if (!compareFullSample && mismatches == 0)
                status = hiZEnabled
                    ? "count matched; sample over limit; Hi-Z not included"
                    : "count matched; sample over limit";
            else if (!compareFullSample)
                status = hiZEnabled
                    ? "count mismatch; sample over limit; Hi-Z not included"
                    : "count mismatch; sample over limit";
            else if (mismatches == 0 && cpuCount > sampleLimit)
                status = hiZEnabled ? "sample matched; Hi-Z not included" : "sample matched";
            else if (mismatches == 0)
                status = hiZEnabled ? "matched; Hi-Z not included" : "matched";
            else
                status = hiZEnabled ? "mismatch; Hi-Z not included" : "mismatch";

            if (gpuLodSelectionEnabled)
                status += "; GPU LOD active";

            return new SceneSubmissionValidationSnapshot(
                1,
                status,
                cpuCount,
                gpuCount,
                compared,
                mismatches,
                sampleLimit,
                firstMismatch);
        }

        private static bool IsActionableValidationMismatch(SceneSubmissionValidationSnapshot validation)
        {
            return !validation.Status.Contains("Hi-Z not included", StringComparison.Ordinal);
        }

        private static bool HasEligibleGpuSubmission(SceneRenderingData sceneData)
        {
            return sceneData.OpaqueMeshletCount > 0 ||
                (sceneData.DepthPrePassEnabled &&
                 (sceneData.SolidMeshletCount > 0 || sceneData.MaskedMeshletCount > 0)) ||
                (sceneData.SceneSubmissionGpuShadowCompactionEnabled &&
                 sceneData.DirectionalShadowPassEnabled &&
                 (sceneData.DirectionalStaticShadowMeshletCount > 0 ||
                  sceneData.DirectionalDynamicShadowMeshletCount > 0));
        }

        private static ulong Sum(uint[] values)
        {
            ulong sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return sum;
        }
    }
}
