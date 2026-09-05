using System;
using System.Collections.Generic;
using Njulf.Rendering.Descriptors;

namespace Njulf.Rendering.Data
{
    public enum TransparentMaterialClass : byte
    {
        GeometryDecal = 0,
        OrdinaryBlend = 1,
        ThickTransmission = 2,
        ThinGlass = 3
    }

    public readonly record struct TransparentDrawClassification(
        TransparentMaterialClass MaterialClass,
        bool ReceivesSceneReflections,
        bool ReceivesShadows);

    public readonly record struct TransparentPipelineKey(
        TransparentMaterialClass MaterialClass,
        TransparencyMode CompositionMode,
        bool RaySceneRequired,
        bool ExactReceiverFeedbackRequired,
        bool DecalReceiverCacheRequired)
    {
        public const int MaterialClassCount = 4;
        public const int CompositionModeCount = 2;
        public const int CacheEntryCount =
            MaterialClassCount * CompositionModeCount * 2 * 2 * 2;

        public int CacheIndex
        {
            get
            {
                int materialClass = (int)MaterialClass;
                int compositionMode = (int)CompositionMode;
                if ((uint)materialClass >= MaterialClassCount ||
                    (uint)compositionMode >= CompositionModeCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(TransparentPipelineKey),
                        "Transparent pipeline key contains an unknown class or composition mode.");
                }

                int flags = (RaySceneRequired ? 1 : 0) |
                    (ExactReceiverFeedbackRequired ? 2 : 0) |
                    (DecalReceiverCacheRequired ? 4 : 0);
                return materialClass + MaterialClassCount *
                    (compositionMode + CompositionModeCount * flags);
            }
        }
    }

    /// <summary>
    /// A contiguous range whose material-derived properties are stable. The
    /// renderer resolves frame-local pipeline requirements from this record.
    /// </summary>
    public readonly record struct TransparentMaterialRun(
        int FirstDraw,
        int DrawCount,
        TransparentDrawClassification Classification);

    public readonly record struct TransparentDrawRun(
        int FirstDraw,
        int DrawCount,
        TransparentPipelineKey PipelineKey);

    public readonly record struct TransparentRunPlanningOptions(
        TransparencyMode CompositionMode,
        bool TransparentLayeredRaySceneRequired,
        bool DecalLayeredRaySceneRequired,
        bool ThickTransmissionRayQueryEnabled,
        bool ReflectionRayQueryEnabled,
        bool ExactReceiverFeedbackRequired,
        bool DecalReceiverCacheAvailable,
        int MaximumRunCount = TransparentDrawRunPlanner.DefaultMaximumRunCount,
        int MinimumSpecializedRunLength =
            TransparentDrawRunPlanner.DefaultMinimumSpecializedRunLength);

    public static class TransparentMaterialClassifier
    {
        public static TransparentMaterialClass Classify(
            MaterialForwardClass forwardClass,
            bool isGeometryDecal,
            GiTransmissionPolicy transmissionPolicy)
        {
            if (isGeometryDecal)
                return TransparentMaterialClass.GeometryDecal;

            if (forwardClass == MaterialForwardClass.ThickTransmission ||
                transmissionPolicy == GiTransmissionPolicy.Volume)
            {
                return TransparentMaterialClass.ThickTransmission;
            }

            return forwardClass == MaterialForwardClass.ThinGlass
                ? TransparentMaterialClass.ThinGlass
                : TransparentMaterialClass.OrdinaryBlend;
        }
    }

    public static class TransparentDrawRunPlanner
    {
        public const int DefaultMaximumRunCount = 256;
        public const int DefaultMinimumSpecializedRunLength = 8;

        public static TransparentPipelineKey CreatePipelineKey(
            TransparentDrawClassification classification,
            in TransparentRunPlanningOptions options)
        {
            bool raySceneRequired =
                classification.ReceivesShadows &&
                (classification.MaterialClass ==
                    TransparentMaterialClass.GeometryDecal
                        ? options.DecalLayeredRaySceneRequired
                        : options.TransparentLayeredRaySceneRequired) ||
                (classification.MaterialClass ==
                    TransparentMaterialClass.ThickTransmission &&
                 options.ThickTransmissionRayQueryEnabled) ||
                (classification.ReceivesSceneReflections &&
                 options.ReflectionRayQueryEnabled);
            bool decalReceiverCacheRequired =
                classification.MaterialClass ==
                    TransparentMaterialClass.GeometryDecal &&
                options.DecalReceiverCacheAvailable &&
                !options.ExactReceiverFeedbackRequired &&
                !raySceneRequired;
            return new TransparentPipelineKey(
                classification.MaterialClass,
                options.CompositionMode,
                raySceneRequired,
                options.ExactReceiverFeedbackRequired,
                decalReceiverCacheRequired);
        }

        public static bool TryBuildRuns(
            IReadOnlyList<TransparentMaterialRun> materialRuns,
            int totalDrawCount,
            in TransparentRunPlanningOptions options,
            Span<TransparentDrawRun> destination,
            out int runCount,
            out string fallbackReason)
        {
            ArgumentNullException.ThrowIfNull(materialRuns);
            runCount = 0;
            fallbackReason = string.Empty;

            if (totalDrawCount <= 0)
            {
                fallbackReason = "transparent-run-empty";
                return false;
            }
            if (options.CompositionMode is not
                (TransparencyMode.SortedAlphaBlend or
                 TransparencyMode.WeightedBlendedOit))
            {
                fallbackReason = "transparent-run-composition-mode-invalid";
                return false;
            }
            if (options.MaximumRunCount <= 0 ||
                options.MaximumRunCount > DefaultMaximumRunCount ||
                destination.Length < options.MaximumRunCount)
            {
                fallbackReason = "transparent-run-capacity-invalid";
                return false;
            }
            if (options.MinimumSpecializedRunLength < 1)
            {
                fallbackReason = "transparent-run-governor-invalid";
                return false;
            }
            if (options.CompositionMode ==
                    TransparencyMode.WeightedBlendedOit &&
                options.ExactReceiverFeedbackRequired)
            {
                fallbackReason =
                    "transparent-run-exact-feedback-requires-canonical-order";
                return false;
            }

            int expectedFirstDraw = 0;
            for (int index = 0; index < materialRuns.Count; index++)
            {
                TransparentMaterialRun materialRun = materialRuns[index];
                if (materialRun.FirstDraw != expectedFirstDraw ||
                    materialRun.DrawCount <= 0 ||
                    materialRun.FirstDraw < 0 ||
                    materialRun.FirstDraw >
                        GPUForwardPushConstants.MaximumTransparentFirstDraw ||
                    materialRun.DrawCount > totalDrawCount -
                        materialRun.FirstDraw)
                {
                    fallbackReason = "transparent-run-range-invalid";
                    return false;
                }

                TransparentPipelineKey key = CreatePipelineKey(
                    materialRun.Classification,
                    options);
                if (runCount > 0 &&
                    destination[runCount - 1].PipelineKey == key)
                {
                    TransparentDrawRun previous = destination[runCount - 1];
                    destination[runCount - 1] = previous with
                    {
                        DrawCount = checked(
                            previous.DrawCount + materialRun.DrawCount)
                    };
                }
                else
                {
                    if (runCount >= options.MaximumRunCount)
                    {
                        fallbackReason = "transparent-run-count-governor";
                        return false;
                    }

                    destination[runCount++] = new TransparentDrawRun(
                        materialRun.FirstDraw,
                        materialRun.DrawCount,
                        key);
                }

                expectedFirstDraw = checked(
                    materialRun.FirstDraw + materialRun.DrawCount);
            }

            if (expectedFirstDraw != totalDrawCount || runCount == 0)
            {
                runCount = 0;
                fallbackReason = "transparent-run-coverage-invalid";
                return false;
            }

            if (runCount > 1)
            {
                for (int index = 0; index < runCount; index++)
                {
                    if (destination[index].DrawCount <
                        options.MinimumSpecializedRunLength)
                    {
                        runCount = 0;
                        fallbackReason =
                            "transparent-run-minimum-length-governor";
                        return false;
                    }
                }
            }

            for (int index = 0; index < runCount; index++)
            {
                TransparentDrawRun run = destination[index];
                if (!GPUForwardPushConstants.TryPackTransparentDrawRange(
                        BindlessIndex.TransparentMeshletDrawBufferBase,
                        checked((uint)run.FirstDraw),
                        out _))
                {
                    runCount = 0;
                    fallbackReason =
                        "transparent-run-range-not-representable";
                    return false;
                }
            }

            return true;
        }
    }
}
