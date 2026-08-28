using System;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Fence-complete validation of the versioned C5 tile stream. Timings remain a
/// separate timestamp-query domain, so this witness proves counters but does
/// not by itself qualify a device/profile.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualCompletionWitness(
    ulong CompletedFrameSerial,
    SimpleDdgiNearFieldResidualTraceTelemetry Trace,
    SimpleDdgiNearFieldResidualHistoryTelemetry History,
    SimpleDdgiNearFieldResidualEnergyTelemetry ResidualEnergy,
    SimpleDdgiNearFieldResidualTileTelemetry Tiles,
    ulong TotalTraceSteps,
    ulong TotalMipVisits,
    ulong TotalRefinementVisits,
    float MaximumTraceDistance,
    uint MaximumTraceStepCount,
    uint MaximumMipVisitCount)
{
    public static SimpleDdgiNearFieldResidualCompletionWitness Empty { get; } =
        default;
}

public static class SimpleDdgiNearFieldResidualCompletionValidator
{
    private const int HeaderWordCount =
        (int)SimpleDdgiNearFieldResidualGpuAbi.TelemetryHeaderWordCount;
    private const int TileWordCount =
        (int)SimpleDdgiNearFieldResidualGpuAbi.TileRecordWordCount;
    private const int TileWidth = 8;
    private const int TileHeight = 8;

    public static bool TryValidate(
        ReadOnlySpan<uint> words,
        in SimpleDdgiNearFieldResidualLayout layout,
        ulong completedFrameSerial,
        out SimpleDdgiNearFieldResidualCompletionWitness witness,
        out string reason)
    {
        witness = default;
        if (!layout.IsValid || layout.TraceWidth <= 0 || layout.TraceHeight <= 0 ||
            layout.TileBuffersBytes == 0UL || completedFrameSerial == 0UL)
        {
            reason = "near-field-completion-layout-or-frame-invalid";
            return false;
        }

        try
        {
            int tileCountX = checked((layout.TraceWidth + TileWidth - 1) / TileWidth);
            int tileCountY = checked((layout.TraceHeight + TileHeight - 1) / TileHeight);
            int tileCount = checked(tileCountX * tileCountY);
            int requiredWords = checked(HeaderWordCount + tileCount * TileWordCount);
            if (words.Length < requiredWords)
            {
                reason = "near-field-completion-readback-truncated";
                return false;
            }

            ulong coveredSum = 0UL;
            ulong validSum = 0UL;
            ulong invalidSum = 0UL;
            ulong raysLaunched = 0UL;
            ulong rayHitSum = 0UL;
            ulong proposalSampleCount = 0UL;
            ulong guidedProposalSampleCount = 0UL;
            ulong guidedValidSampleCount = 0UL;
            ulong guidedZeroContributionSampleCount = 0UL;
            ulong cosineProposalSampleCount = 0UL;
            ulong validSampleCount = 0UL;
            ulong screenExits = 0UL;
            ulong invalidReceivers = 0UL;
            ulong invalidRays = 0UL;
            ulong stepBudgetRejects = 0UL;
            ulong mipBudgetRejects = 0UL;
            ulong depthRejects = 0UL;
            ulong normalRejects = 0UL;
            ulong sourceRejects = 0UL;
            ulong nonFiniteRejects = 0UL;
            ulong totalTraceSteps = 0UL;
            ulong totalMipVisits = 0UL;
            ulong totalRefinementVisits = 0UL;
            ulong historyCandidates = 0UL;
            ulong historyAccepted = 0UL;
            ulong historyRejected = 0UL;
            ulong invalidCurrentHistory = 0UL;
            ulong historyEpochRejects = 0UL;
            ulong identityRejects = 0UL;
            ulong reprojectionRejects = 0UL;
            ulong historyDepthRejects = 0UL;
            ulong historyNormalRejects = 0UL;
            ulong sourceRevisionRejects = 0UL;
            ulong receiverIdentityRejects = 0UL;
            ulong receiverRevisionRejects = 0UL;
            ulong hitIdentityRejects = 0UL;
            ulong hitRevisionRejects = 0UL;
            ulong sourceReactiveRejects = 0UL;
            ulong varianceClipped = 0UL;
            double varianceSum = 0.0;
            double maximumVariance = 0.0;
            double signedEnergy = 0.0;
            double absoluteEnergy = 0.0;
            double squaredEnergy = 0.0;
            double maximumAbsoluteEnergy = 0.0;
            ulong nonFiniteEnergy = 0UL;
            double lowFrequencyLeakage = 0.0;
            float maximumTraceDistance = 0.0f;
            uint maximumTraceStepCount = 0U;
            uint maximumMipVisitCount = 0U;
            uint recordedTiles = 0U;
            uint candidateTiles = words[16];
            uint activeTiles = words[17];
            uint compactedTiles = words[18];
            uint emptyTiles = words[19];
            uint overflowTiles = words[20];
            uint invalidSurfacePixels = words[21];
            uint requiredCompletionMask = compactedTiles == 0U
                ? 0U
                : SimpleDdgiNearFieldResidualGpuAbi
                    .TelemetryRequiredCompletionMask;
            uint overflowFlags = words[15];
            if (overflowTiles != 0U)
            {
                reason = "near-field-completion-tile-list-overflow";
                return false;
            }
            if (overflowFlags != 0U)
            {
                reason = DescribeOverflowFlags(
                    overflowFlags,
                    words,
                    tileCount,
                    compactedTiles);
                return false;
            }
            if (words[0] != SimpleDdgiNearFieldResidualGpuAbi.TelemetryMagic ||
                words[1] != SimpleDdgiNearFieldResidualGpuAbi.Version ||
                (((ulong)words[3] << 32) | words[2]) != completedFrameSerial ||
                (compactedTiles != 0U &&
                    (words[4] != (uint)layout.TraceWidth ||
                     words[5] != (uint)layout.TraceHeight)) ||
                words[6] != (uint)tileCount ||
                (words[7] & SimpleDdgiNearFieldResidualGpuAbi
                    .TelemetryRequiredCompletionMask) !=
                    requiredCompletionMask ||
                candidateTiles != (uint)tileCount ||
                activeTiles > candidateTiles ||
                compactedTiles > activeTiles ||
                emptyTiles > candidateTiles ||
                activeTiles + emptyTiles != candidateTiles ||
                compactedTiles + overflowTiles != activeTiles ||
                words[28] != 0U || words[29] != 0U ||
                words[30] != 0U || words[31] != 0U)
            {
                reason = "near-field-completion-header-identity-invalid";
                return false;
            }

            for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
            {
                int word = checked(HeaderWordCount + tileIndex * TileWordCount);
                uint recordCompletion = words[word + 15] >> 16;
                if ((recordCompletion &
                        SimpleDdgiNearFieldResidualGpuAbi
                            .TelemetryTraceCompleteBit) == 0U)
                {
                    for (int recordWord = 0;
                         recordWord < TileWordCount;
                         recordWord++)
                    {
                        if (words[word + recordWord] != 0U)
                        {
                            reason =
                                "near-field-completion-inactive-tile-record-not-zero";
                            return false;
                        }
                    }
                    continue;
                }
                if (words[word] != (uint)tileIndex)
                {
                    reason = "near-field-completion-tile-index-mismatch";
                    return false;
                }

                uint traceCounts0 = words[word + 1];
                uint traceCounts1 = words[word + 2];
                uint traceCounts2 = words[word + 3];
                uint historyCounts0 = words[word + 4];
                uint historyCounts1 = words[word + 5];
                uint historyCounts2 = words[word + 6];
                uint covered = traceCounts0 & 0x7fU;
                uint valid = (traceCounts0 >> 7) & 0x7fU;
                uint invalid = (traceCounts0 >> 14) & 0x7fU;
                uint rays = (traceCounts0 >> 21) & 0x1ffU;
                uint tileScreenExits = UnpackTileCount(traceCounts1, 0);
                uint tileInvalidReceivers = UnpackTileCount(traceCounts1, 1);
                uint tileInvalidRays = UnpackTileCount(traceCounts1, 2);
                uint tileStepBudgetRejects = UnpackTileCount(traceCounts1, 3);
                uint tileMipBudgetRejects = UnpackTileCount(traceCounts2, 0);
                uint tileDepthRejects = UnpackTileCount(traceCounts2, 1);
                uint tileNormalRejects = UnpackTileCount(traceCounts2, 2);
                uint tileSourceRejects = UnpackTileCount(traceCounts2, 3);
                uint tileNonFiniteRejects = UnpackTileCount(historyCounts0, 0);
                uint tileHistoryCandidates = UnpackTileCount(historyCounts0, 1);
                uint tileHistoryAccepted = UnpackTileCount(historyCounts0, 2);
                uint tileInvalidCurrent = UnpackTileCount(historyCounts0, 3);
                uint tileEpochRejects = UnpackTileCount(historyCounts1, 0);
                uint tileIdentityRejects = UnpackTileCount(historyCounts1, 1);
                uint tileReprojectionRejects = UnpackTileCount(historyCounts1, 2);
                uint tileHistoryDepthRejects = UnpackTileCount(historyCounts1, 3);
                uint tileHistoryNormalRejects = UnpackTileCount(historyCounts2, 0);
                uint tileSourceRevisionRejects = UnpackTileCount(historyCounts2, 1);
                uint tileVarianceClipped = UnpackTileCount(historyCounts2, 2);
                uint tileNonFiniteEnergy = UnpackTileCount(historyCounts2, 3);
                uint detailedHistory0 = words[word + 16];
                uint detailedHistory1 = words[word + 17];
                uint detailedHistory2 = words[word + 18];
                uint tileReceiverIdentityRejects =
                    UnpackTileCount(detailedHistory0, 0);
                uint tileReceiverRevisionRejects =
                    UnpackTileCount(detailedHistory0, 1);
                uint tileHitIdentityRejects =
                    UnpackTileCount(detailedHistory0, 2);
                uint tileHitRevisionRejects =
                    UnpackTileCount(detailedHistory0, 3);
                uint tileSourceReactiveRejects =
                    UnpackTileCount(detailedHistory1, 0);
                uint proposalCounts = words[word + 19];
                uint guidedAndTraversalCounts = words[word + 20];
                uint hitAndValidSampleCounts = words[word + 21];
                uint tileProposalCount = UnpackNineBitCount(
                    proposalCounts, 0);
                uint tileGuidedProposalCount = UnpackNineBitCount(
                    proposalCounts, 1);
                uint tileGuidedValidCount = UnpackNineBitCount(
                    proposalCounts, 2);
                uint tileGuidedZeroCount = UnpackNineBitCount(
                    guidedAndTraversalCounts, 0);
                uint tileCosineProposalCount = UnpackNineBitCount(
                    guidedAndTraversalCounts, 1);
                uint tileTracedRayCount = UnpackNineBitCount(
                    guidedAndTraversalCounts, 2);
                uint tileRayHitCount = UnpackNineBitCount(
                    hitAndValidSampleCounts, 0);
                uint tileValidSampleCount = UnpackNineBitCount(
                    hitAndValidSampleCounts, 1);
                uint visitTotals = words[word + 7];
                uint peakAndRefinement = words[word + 8];
                uint tileTotalTraceSteps = visitTotals & 0xffffU;
                uint tileTotalMipVisits = visitTotals >> 16;
                uint tileTotalRefinementVisits = peakAndRefinement & 0xffffU;
                uint tileMaximumTraceSteps = (peakAndRefinement >> 16) & 0x1ffU;
                uint tileMaximumMipVisits = (peakAndRefinement >> 25) & 0x3fU;
                uint completionAndDistance = words[word + 15];
                uint completionMask = completionAndDistance >> 16;
                int tileX = tileIndex % tileCountX;
                int tileY = tileIndex / tileCountX;
                uint expectedCovered = checked((uint)(
                    Math.Min(TileWidth, layout.TraceWidth - tileX * TileWidth) *
                    Math.Min(TileHeight, layout.TraceHeight - tileY * TileHeight)));
                if (covered != expectedCovered || valid > covered ||
                    invalid > covered || valid + invalid != covered ||
                    rays > covered * 4U ||
                    tileProposalCount > covered * 4U ||
                    tileTracedRayCount != rays ||
                    tileGuidedProposalCount + tileCosineProposalCount !=
                        tileProposalCount ||
                    tileGuidedValidCount > tileGuidedProposalCount ||
                    tileGuidedZeroCount > tileGuidedValidCount ||
                    tileValidSampleCount > tileProposalCount ||
                    tileTracedRayCount > tileProposalCount ||
                    tileRayHitCount > tileTracedRayCount ||
                    tileHistoryCandidates > valid ||
                    tileHistoryAccepted > tileHistoryCandidates ||
                    tileScreenExits > covered || tileInvalidReceivers > covered ||
                    tileInvalidRays > covered || tileStepBudgetRejects > covered ||
                    tileMipBudgetRejects > covered || tileDepthRejects > covered ||
                    tileNormalRejects > covered || tileSourceRejects > covered ||
                    tileNonFiniteRejects > covered || tileInvalidCurrent > covered ||
                    tileEpochRejects > covered || tileIdentityRejects > covered ||
                    tileReprojectionRejects > covered ||
                    tileHistoryDepthRejects > covered ||
                    tileHistoryNormalRejects > covered ||
                    tileSourceRevisionRejects > covered ||
                    tileVarianceClipped > covered || tileNonFiniteEnergy > covered ||
                    tileReceiverIdentityRejects > covered ||
                    tileReceiverRevisionRejects > covered ||
                    tileHitIdentityRejects > covered ||
                    tileHitRevisionRejects > covered ||
                    tileSourceReactiveRejects > covered ||
                    UnpackTileCount(detailedHistory1, 1) !=
                        tileReprojectionRejects ||
                    UnpackTileCount(detailedHistory1, 2) !=
                        tileHistoryDepthRejects ||
                    UnpackTileCount(detailedHistory1, 3) !=
                        tileHistoryNormalRejects ||
                    UnpackTileCount(detailedHistory2, 0) !=
                        tileVarianceClipped ||
                    UnpackTileCount(detailedHistory2, 1) !=
                        tileSourceRevisionRejects ||
                    UnpackTileCount(detailedHistory2, 2) !=
                        tileEpochRejects ||
                    UnpackTileCount(detailedHistory2, 3) !=
                        tileInvalidCurrent ||
                    words[word + 22] != 0U || words[word + 23] != 0U ||
                    tileNonFiniteEnergy > tileHistoryCandidates ||
                    tileTotalTraceSteps >
                        rays * SimpleDdgiNearFieldResidualGpuAbi.MaximumTraceSteps ||
                    tileTotalMipVisits >
                        rays * SimpleDdgiNearFieldResidualGpuAbi.MaximumTraceSteps ||
                    tileTotalRefinementVisits >
                        rays * SimpleDdgiNearFieldResidualGpuAbi
                            .MaximumBinaryRefinementSteps ||
                    (completionMask & SimpleDdgiNearFieldResidualGpuAbi
                        .TelemetryRequiredCompletionMask) !=
                        SimpleDdgiNearFieldResidualGpuAbi
                            .TelemetryRequiredCompletionMask)
                {
                    reason = "near-field-completion-tile-count-out-of-range";
                    return false;
                }

                float variance = BitConverter.UInt32BitsToSingle(words[word + 9]);
                float tileMaximumVariance =
                    BitConverter.UInt32BitsToSingle(words[word + 10]);
                float tileSignedEnergy =
                    BitConverter.UInt32BitsToSingle(words[word + 11]);
                float tileAbsoluteEnergy =
                    BitConverter.UInt32BitsToSingle(words[word + 12]);
                float tileSquaredEnergy =
                    BitConverter.UInt32BitsToSingle(words[word + 13]);
                float tileMaximumAbsoluteEnergy =
                    BitConverter.UInt32BitsToSingle(words[word + 14]);
                float tileMaximumTraceDistance = (float)
                    BitConverter.UInt16BitsToHalf(
                        checked((ushort)(completionAndDistance & 0xffffU)));
                uint finiteEnergyCount = tileHistoryCandidates - tileNonFiniteEnergy;
                double tileLeakage = finiteEnergyCount == 0U
                    ? 0.0
                    : Math.Abs(tileSignedEnergy / finiteEnergyCount);
                if (!float.IsFinite(variance) || variance < 0.0f ||
                    !float.IsFinite(tileMaximumVariance) ||
                    tileMaximumVariance < 0.0f ||
                    !float.IsFinite(tileSignedEnergy) ||
                    !float.IsFinite(tileAbsoluteEnergy) ||
                    tileAbsoluteEnergy < MathF.Abs(tileSignedEnergy) - 1.0e-4f ||
                    !float.IsFinite(tileSquaredEnergy) || tileSquaredEnergy < 0.0f ||
                    !float.IsFinite(tileMaximumAbsoluteEnergy) ||
                    tileMaximumAbsoluteEnergy < 0.0f ||
                    !float.IsFinite(tileMaximumTraceDistance) ||
                    tileMaximumTraceDistance < 0.0f ||
                    tileMaximumTraceSteps >
                        SimpleDdgiNearFieldResidualGpuAbi.MaximumTraceSteps ||
                    tileMaximumMipVisits >
                        SimpleDdgiNearFieldResidualGpuAbi
                            .MaximumTelemetryMipVisits)
                {
                    reason = "near-field-completion-tile-statistic-invalid";
                    return false;
                }

                coveredSum = checked(coveredSum + covered);
                validSum = checked(validSum + valid);
                invalidSum = checked(invalidSum + invalid);
                raysLaunched = checked(raysLaunched + rays);
                rayHitSum = checked(rayHitSum + tileRayHitCount);
                proposalSampleCount = checked(
                    proposalSampleCount + tileProposalCount);
                guidedProposalSampleCount = checked(
                    guidedProposalSampleCount + tileGuidedProposalCount);
                guidedValidSampleCount = checked(
                    guidedValidSampleCount + tileGuidedValidCount);
                guidedZeroContributionSampleCount = checked(
                    guidedZeroContributionSampleCount + tileGuidedZeroCount);
                cosineProposalSampleCount = checked(
                    cosineProposalSampleCount + tileCosineProposalCount);
                validSampleCount = checked(
                    validSampleCount + tileValidSampleCount);
                screenExits = checked(screenExits + tileScreenExits);
                invalidReceivers = checked(invalidReceivers + tileInvalidReceivers);
                invalidRays = checked(invalidRays + tileInvalidRays);
                stepBudgetRejects = checked(stepBudgetRejects + tileStepBudgetRejects);
                mipBudgetRejects = checked(mipBudgetRejects + tileMipBudgetRejects);
                depthRejects = checked(depthRejects + tileDepthRejects);
                normalRejects = checked(normalRejects + tileNormalRejects);
                sourceRejects = checked(sourceRejects + tileSourceRejects);
                nonFiniteRejects = checked(nonFiniteRejects + tileNonFiniteRejects);
                totalTraceSteps = checked(totalTraceSteps + tileTotalTraceSteps);
                totalMipVisits = checked(totalMipVisits + tileTotalMipVisits);
                totalRefinementVisits = checked(
                    totalRefinementVisits + tileTotalRefinementVisits);
                historyCandidates = checked(historyCandidates + tileHistoryCandidates);
                historyAccepted = checked(historyAccepted + tileHistoryAccepted);
                historyRejected = checked(
                    historyRejected + tileHistoryCandidates - tileHistoryAccepted);
                invalidCurrentHistory = checked(
                    invalidCurrentHistory + tileInvalidCurrent);
                historyEpochRejects = checked(historyEpochRejects + tileEpochRejects);
                identityRejects = checked(identityRejects + tileIdentityRejects);
                reprojectionRejects = checked(
                    reprojectionRejects + tileReprojectionRejects);
                historyDepthRejects = checked(
                    historyDepthRejects + tileHistoryDepthRejects);
                historyNormalRejects = checked(
                    historyNormalRejects + tileHistoryNormalRejects);
                sourceRevisionRejects = checked(
                    sourceRevisionRejects + tileSourceRevisionRejects);
                receiverIdentityRejects = checked(
                    receiverIdentityRejects + tileReceiverIdentityRejects);
                receiverRevisionRejects = checked(
                    receiverRevisionRejects + tileReceiverRevisionRejects);
                hitIdentityRejects = checked(
                    hitIdentityRejects + tileHitIdentityRejects);
                hitRevisionRejects = checked(
                    hitRevisionRejects + tileHitRevisionRejects);
                sourceReactiveRejects = checked(
                    sourceReactiveRejects + tileSourceReactiveRejects);
                varianceClipped = checked(varianceClipped + tileVarianceClipped);
                varianceSum += variance;
                maximumVariance = Math.Max(maximumVariance, tileMaximumVariance);
                signedEnergy += tileSignedEnergy;
                absoluteEnergy += tileAbsoluteEnergy;
                squaredEnergy += tileSquaredEnergy;
                maximumAbsoluteEnergy = Math.Max(
                    maximumAbsoluteEnergy, tileMaximumAbsoluteEnergy);
                nonFiniteEnergy = checked(nonFiniteEnergy + tileNonFiniteEnergy);
                lowFrequencyLeakage += tileLeakage;
                maximumTraceDistance = Math.Max(
                    maximumTraceDistance, tileMaximumTraceDistance);
                maximumTraceStepCount = Math.Max(
                    maximumTraceStepCount, tileMaximumTraceSteps);
                maximumMipVisitCount = Math.Max(
                    maximumMipVisitCount, tileMaximumMipVisits);
                recordedTiles++;
            }

            ulong rayHits = words[10];
            ulong rayMisses = words[11];
            ulong maximumCandidateReceivers = checked(
                (ulong)layout.TraceWidth * (ulong)layout.TraceHeight);
            if (coveredSum > maximumCandidateReceivers)
                return SummaryMismatch("covered-capacity",
                    maximumCandidateReceivers, coveredSum, out reason);
            if (recordedTiles != compactedTiles)
                return SummaryMismatch("recorded-tiles",
                    compactedTiles, recordedTiles, out reason);
            if (validSum + invalidSum != coveredSum)
                return SummaryMismatch("receiver-validity",
                    coveredSum, validSum + invalidSum, out reason);
            if (words[8] != coveredSum)
                return SummaryMismatch("candidate-receivers",
                    words[8], coveredSum, out reason);
            if (words[9] != raysLaunched)
                return SummaryMismatch("traced-rays",
                    words[9], raysLaunched, out reason);
            if (rayHits != rayHitSum)
                return SummaryMismatch("ray-hits",
                    rayHits, rayHitSum, out reason);
            if (rayHits > raysLaunched)
                return SummaryMismatch("ray-hit-upper-bound",
                    raysLaunched, rayHits, out reason);
            if (rayMisses != raysLaunched - rayHits)
                return SummaryMismatch("ray-misses",
                    rayMisses, raysLaunched - rayHits, out reason);
            if (words[12] != invalidReceivers)
                return SummaryMismatch("invalid-receivers",
                    words[12], invalidReceivers, out reason);
            if (words[13] != invalidRays)
                return SummaryMismatch("invalid-rays",
                    words[13], invalidRays, out reason);
            if (words[14] != invalidSum)
                return SummaryMismatch("invalid-candidates",
                    words[14], invalidSum, out reason);
            if (words[22] != proposalSampleCount)
                return SummaryMismatch("proposal-samples",
                    words[22], proposalSampleCount, out reason);
            if (words[23] != guidedProposalSampleCount)
                return SummaryMismatch("guided-proposals",
                    words[23], guidedProposalSampleCount, out reason);
            if (words[24] != guidedValidSampleCount)
                return SummaryMismatch("guided-valid-samples",
                    words[24], guidedValidSampleCount, out reason);
            if (words[25] != guidedZeroContributionSampleCount)
                return SummaryMismatch("guided-zero-samples",
                    words[25], guidedZeroContributionSampleCount, out reason);
            if (words[26] != cosineProposalSampleCount)
                return SummaryMismatch("cosine-proposals",
                    words[26], cosineProposalSampleCount, out reason);
            if (words[27] != validSampleCount)
                return SummaryMismatch("valid-samples",
                    words[27], validSampleCount, out reason);

            witness = new SimpleDdgiNearFieldResidualCompletionWitness(
                completedFrameSerial,
                new SimpleDdgiNearFieldResidualTraceTelemetry(
                    coveredSum,
                    raysLaunched,
                    rayHits,
                    rayMisses,
                    screenExits,
                    invalidReceivers,
                    invalidRays,
                    stepBudgetRejects,
                    mipBudgetRejects,
                    depthRejects,
                    normalRejects,
                    sourceRejects,
                    nonFiniteRejects)
                {
                    ProposalSampleCount = proposalSampleCount,
                    GuidedProposalSampleCount = guidedProposalSampleCount,
                    GuidedValidSampleCount = guidedValidSampleCount,
                    GuidedZeroContributionSampleCount =
                        guidedZeroContributionSampleCount,
                    CosineProposalSampleCount = cosineProposalSampleCount,
                    ValidSampleCount = validSampleCount
                },
                new SimpleDdgiNearFieldResidualHistoryTelemetry(
                    historyCandidates,
                    historyAccepted,
                    historyRejected,
                    invalidCurrentHistory,
                    historyEpochRejects,
                    hitIdentityRejects,
                    reprojectionRejects,
                    historyDepthRejects,
                    historyNormalRejects,
                    sourceRevisionRejects,
                    varianceClipped,
                    historyCandidates == nonFiniteEnergy
                        ? 0.0
                        : varianceSum / (historyCandidates - nonFiniteEnergy),
                    maximumVariance)
                {
                    ReceiverIdentityRejectedCount = receiverIdentityRejects,
                    ReceiverRevisionRejectedCount = receiverRevisionRejects,
                    HitRevisionRejectedCount = hitRevisionRejects,
                    SourceReactiveRejectedCount = sourceReactiveRejects
                },
                new SimpleDdgiNearFieldResidualEnergyTelemetry(
                    historyCandidates >= nonFiniteEnergy
                        ? historyCandidates - nonFiniteEnergy
                        : 0UL,
                    signedEnergy,
                    absoluteEnergy,
                    squaredEnergy,
                    maximumAbsoluteEnergy,
                    lowFrequencyLeakage,
                    nonFiniteEnergy),
                new SimpleDdgiNearFieldResidualTileTelemetry(
                    (uint)tileCount,
                    candidateTiles,
                    compactedTiles,
                    emptyTiles,
                    overflowTiles,
                    layout.TileBuffersBytes)
                {
                    ActiveTileCount = activeTiles,
                    InvalidSurfacePixelCount = invalidSurfacePixels
                },
                totalTraceSteps,
                totalMipVisits,
                totalRefinementVisits,
                maximumTraceDistance,
                maximumTraceStepCount,
                maximumMipVisitCount);
            reason = "valid";
            return true;
        }
        catch (OverflowException)
        {
            reason = "near-field-completion-validation-overflow";
            return false;
        }
    }

    private static string DescribeOverflowFlags(
        uint flags,
        ReadOnlySpan<uint> words,
        int tileCount,
        uint compactedTiles)
    {
        if ((flags & SimpleDdgiNearFieldResidualGpuAbi
                .TelemetryFinalizeRecordInvalidBit) != 0U)
        {
            uint recordedTiles = 0U;
            for (int tileIndex = 0; tileIndex < tileCount; tileIndex++)
            {
                int record = checked(HeaderWordCount + tileIndex * TileWordCount);
                uint completion = words[record + 15] >> 16;
                if (completion == 0U)
                    continue;
                recordedTiles++;
                if ((completion & SimpleDdgiNearFieldResidualGpuAbi
                        .TelemetryRequiredCompletionMask) !=
                    SimpleDdgiNearFieldResidualGpuAbi
                        .TelemetryRequiredCompletionMask)
                {
                    return $"near-field-completion-finalize-record-completion-" +
                           $"invalid:tile={tileIndex};mask=0x{completion:X4};" +
                           $"flags=0x{flags:X8}";
                }
                if (words[record] != (uint)tileIndex)
                {
                    return $"near-field-completion-finalize-record-index-" +
                           $"invalid:tile={tileIndex};record={words[record]};" +
                           $"flags=0x{flags:X8}";
                }
                if (words[record + 22] != 0U || words[record + 23] != 0U)
                {
                    return $"near-field-completion-finalize-record-reserved-" +
                           $"invalid:tile={tileIndex};flags=0x{flags:X8}";
                }

                uint traceCounts = words[record + 1];
                uint tracedRays = (traceCounts >> 21) & 0x1ffU;
                uint proposals = UnpackNineBitCount(words[record + 19], 0);
                uint guided = UnpackNineBitCount(words[record + 19], 1);
                uint guidedValid = UnpackNineBitCount(words[record + 19], 2);
                uint guidedZero = UnpackNineBitCount(words[record + 20], 0);
                uint cosine = UnpackNineBitCount(words[record + 20], 1);
                uint independentlyPackedTraced =
                    UnpackNineBitCount(words[record + 20], 2);
                uint hits = UnpackNineBitCount(words[record + 21], 0);
                uint validSamples = UnpackNineBitCount(words[record + 21], 1);
                if (independentlyPackedTraced != tracedRays ||
                    guided + cosine != proposals || guidedValid > guided ||
                    guidedZero > guidedValid || validSamples > proposals ||
                    tracedRays > proposals || hits > tracedRays)
                {
                    return $"near-field-completion-finalize-record-counts-" +
                           $"invalid:tile={tileIndex};proposals={proposals};" +
                           $"guided={guided};guidedValid={guidedValid};" +
                           $"guidedZero={guidedZero};cosine={cosine};" +
                           $"traced={tracedRays};packedTraced=" +
                           $"{independentlyPackedTraced};hits={hits};" +
                           $"valid={validSamples};flags=0x{flags:X8}";
                }
            }
            if (recordedTiles != compactedTiles)
            {
                return $"near-field-completion-finalize-record-count-" +
                       $"mismatch:recorded={recordedTiles};" +
                       $"compacted={compactedTiles};flags=0x{flags:X8}";
            }
        }

        string stage = (flags & SimpleDdgiNearFieldResidualGpuAbi
                .TelemetryTemporalRecordIdentityMismatchBit) != 0U
            ? "temporal-record-identity-mismatch"
            : (flags & SimpleDdgiNearFieldResidualGpuAbi
                    .TelemetryTemporalRecordOverflowBit) != 0U
                ? "temporal-record-overflow"
                : (flags & SimpleDdgiNearFieldResidualGpuAbi
                        .TelemetryTraceRecordOverflowBit) != 0U
                    ? "trace-record-overflow"
                    : (flags & SimpleDdgiNearFieldResidualGpuAbi
                            .TelemetryFinalizeRecordInvalidBit) != 0U
                        ? "finalize-record-invalid"
                        : "unknown";
        uint unknown = flags & ~SimpleDdgiNearFieldResidualGpuAbi
            .TelemetryKnownOverflowFlags;
        return $"near-field-completion-{stage}:flags=0x{flags:X8};" +
               $"unknown=0x{unknown:X8}";
    }

    private static uint UnpackTileCount(uint packed, int lane) =>
        (packed >> checked(lane * 8)) & 0xffU;

    private static uint UnpackNineBitCount(uint packed, int lane) =>
        (packed >> checked(lane * 9)) & 0x1ffU;

    private static bool SummaryMismatch(
        string invariant,
        ulong headerValue,
        ulong tileValue,
        out string reason)
    {
        reason = $"near-field-completion-{invariant}-mismatch:" +
            $"header={headerValue},tiles={tileValue}";
        return false;
    }
}

/// <summary>
/// Availability of the C5 telemetry payload.  A requested or admitted C5 mode
/// is not evidence that the GPU counters have been read back.  Consumers must
/// use <see cref="SimpleDdgiNearFieldResidualReadbackStatus.IsAuthoritative"/>
/// before treating any counters or timings as measured work.
/// </summary>
public enum SimpleDdgiNearFieldResidualReadbackState
{
    Disabled = 0,
    PendingRendererIntegration = 1,
    PendingGpuReadback = 2,
    Available = 3,
    Faulted = 4
}

/// <summary>
/// Immutable provenance for one C5 telemetry sample.  Both timestamp and
/// counter readback must be complete for a sample to be authoritative; this
/// avoids reporting a CPU-side plan or a submission as measured GPU work.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualReadbackStatus(
    SimpleDdgiNearFieldResidualReadbackState State,
    bool CounterReadbackValid,
    bool TimingReadbackValid,
    ulong CompletedFrameSerial,
    uint AgeFrames,
    string Reason)
{
    [JsonIgnore]
    public bool IsAuthoritative =>
        State == SimpleDdgiNearFieldResidualReadbackState.Available &&
        CounterReadbackValid &&
        TimingReadbackValid &&
        CompletedFrameSerial != 0UL;

    public static SimpleDdgiNearFieldResidualReadbackStatus Disabled(
        string reason = "C5 near-field residual telemetry is disabled.") => new(
        SimpleDdgiNearFieldResidualReadbackState.Disabled,
        false,
        false,
        0UL,
        0U,
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason));

    public static SimpleDdgiNearFieldResidualReadbackStatus PendingRendererIntegration(
        string reason = "C5 renderer integration or GPU readback was not supplied.") => new(
        SimpleDdgiNearFieldResidualReadbackState.PendingRendererIntegration,
        false,
        false,
        0UL,
        0U,
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason));

    public static SimpleDdgiNearFieldResidualReadbackStatus PendingGpuReadback(
        string reason = "C5 GPU readback has not completed.") => new(
        SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback,
        false,
        false,
        0UL,
        0U,
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason));

    public static SimpleDdgiNearFieldResidualReadbackStatus CounterReadbackPending(
        ulong completedFrameSerial,
        uint ageFrames = 0U,
        string reason = "C5 counters are valid; exclusive GPU timings are pending.") => new(
        SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback,
        true,
        false,
        completedFrameSerial,
        ageFrames,
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason));

    public static SimpleDdgiNearFieldResidualReadbackStatus Available(
        ulong completedFrameSerial,
        uint ageFrames = 0U,
        string reason = "completed GPU readback") => new(
        SimpleDdgiNearFieldResidualReadbackState.Available,
        true,
        true,
        completedFrameSerial,
        ageFrames,
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason));

    public SimpleDdgiNearFieldResidualReadbackStatus Normalize()
    {
        string reason = SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(Reason);
        return State switch
        {
            SimpleDdgiNearFieldResidualReadbackState.Disabled => Disabled(reason),
            SimpleDdgiNearFieldResidualReadbackState.PendingRendererIntegration =>
                PendingRendererIntegration(reason),
            SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback
                when CounterReadbackValid && !TimingReadbackValid &&
                     CompletedFrameSerial != 0UL => this with { Reason = reason },
            SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback
                when !CounterReadbackValid && !TimingReadbackValid &&
                     CompletedFrameSerial == 0UL => PendingGpuReadback(reason),
            SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback =>
                PendingGpuReadback(
                    "C5 pending readback provenance was internally inconsistent."),
            SimpleDdgiNearFieldResidualReadbackState.Faulted => new(
                SimpleDdgiNearFieldResidualReadbackState.Faulted,
                false,
                false,
                0UL,
                0U,
                reason),
            SimpleDdgiNearFieldResidualReadbackState.Available
                when CounterReadbackValid && TimingReadbackValid &&
                     CompletedFrameSerial != 0UL => this with { Reason = reason },
            SimpleDdgiNearFieldResidualReadbackState.Available =>
                PendingGpuReadback("C5 readback was incomplete and was not admitted as telemetry."),
            _ => Disabled("C5 telemetry has an unknown readback state.")
        };
    }
}

/// <summary>
/// Byte-exact C5 allocation lifecycle. Requested and admitted bytes describe
/// the frozen plan. Live allocation bytes are valid as soon as the renderer's
/// allocator has returned exact native requirements; they do not depend on a
/// shader-counter readback and therefore remain visible while GPU telemetry is
/// pending.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualMemoryTelemetry(
    ulong RequestedBytes,
    ulong AdmittedBytes,
    ulong AllocatedBytes,
    ulong PeakAllocatedBytes,
    ulong RetiredBytes)
{
    /// <summary>Two R16 validity banks plus two packed-R32 normal banks.</summary>
    public ulong PackedValidityAndNormalBytes { get; init; }

    /// <summary>
    /// Bytes avoided by aliasing RawCandidate as one filter ping-pong peer.
    /// This is a physical-plan saving and is not added to AllocatedBytes.
    /// </summary>
    public ulong AliasedFilterScratchBytes { get; init; }

    public int PhysicalFilterScratchImageCount { get; init; }

    public static SimpleDdgiNearFieldResidualMemoryTelemetry Empty { get; } = new(
        0UL, 0UL, 0UL, 0UL, 0UL);

    public SimpleDdgiNearFieldResidualMemoryTelemetry Normalize() =>
        PeakAllocatedBytes >= AllocatedBytes
            ? this
            : this with { PeakAllocatedBytes = AllocatedBytes };

    public SimpleDdgiNearFieldResidualMemoryTelemetry WithLayoutSavings(
        in SimpleDdgiNearFieldResidualLayout layout) => this with
        {
            PackedValidityAndNormalBytes = checked(
                layout.HistoryValidityBytes + layout.HistoryNormalBytes),
            AliasedFilterScratchBytes = layout.AliasedFilterScratchBytes,
            PhysicalFilterScratchImageCount =
                layout.PhysicalFilterScratchImageCount
        };

    public SimpleDdgiNearFieldResidualMemoryTelemetry WithoutLiveAllocation() =>
        this with
        {
            AllocatedBytes = 0UL,
            PeakAllocatedBytes = 0UL,
            RetiredBytes = 0UL
        };
}

/// <summary>
/// Exclusive GPU timestamp scopes for the optional C5 stages.  A zero value
/// means unavailable unless the parent readback status is authoritative.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualStageTimings(
    ulong SourceMicroseconds,
    ulong RawTraceMicroseconds,
    ulong TemporalMicroseconds,
    ulong FilterMicroseconds,
    ulong CompositeMicroseconds)
{
    /// <summary>Reset plus receiver preparation and GPU tile compaction.</summary>
    public ulong PrepareCompactionMicroseconds { get; init; }

    /// <summary>Post-temporal authoritative tile-to-header reduction.</summary>
    public ulong FinalizationMicroseconds { get; init; }

    /// <summary>B3-sized low estimate and signed band construction.</summary>
    public ulong FrequencySeparationMicroseconds { get; init; }

    /// <summary>
    /// True only when <see cref="SourceMicroseconds"/> came from an admitted,
    /// deterministic paired Forward+ source-on/off calibration for this
    /// extent envelope. Live compute timestamps alone cannot establish it.
    /// </summary>
    public bool SourceCostAuthoritative { get; init; }

    public static SimpleDdgiNearFieldResidualStageTimings Empty { get; } = new(
        0UL, 0UL, 0UL, 0UL, 0UL);

    [JsonIgnore]
    public ulong TotalMicroseconds => SaturatingAdd(
        SaturatingAdd(
            SaturatingAdd(SourceMicroseconds, PrepareCompactionMicroseconds),
            RawTraceMicroseconds),
        SaturatingAdd(
            TemporalMicroseconds,
            SaturatingAdd(
                FinalizationMicroseconds,
                SaturatingAdd(
                    FilterMicroseconds,
                    SaturatingAdd(
                        FrequencySeparationMicroseconds,
                        CompositeMicroseconds)))));

    [JsonIgnore]
    public ulong ExclusiveComputeMicroseconds => SaturatingAdd(
        PrepareCompactionMicroseconds,
        SaturatingAdd(
            RawTraceMicroseconds,
            SaturatingAdd(
                TemporalMicroseconds,
                SaturatingAdd(
                    FinalizationMicroseconds,
                    SaturatingAdd(
                        FilterMicroseconds,
                        SaturatingAdd(
                            FrequencySeparationMicroseconds,
                            CompositeMicroseconds))))));

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;
}

/// <summary>
/// Raw trace and rejection counters.  Rejection reason counters are not
/// mutually exclusive; the total rejected work is represented by the explicit
/// edge/invalid counters rather than a sum of diagnostic categories.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualTraceTelemetry(
    ulong CandidateReceiverCount,
    ulong RaysLaunched,
    ulong RayHitCount,
    ulong RayMissCount,
    ulong EdgeRejectedCount,
    ulong InvalidReceiverRejectedCount,
    ulong InvalidRayRejectedCount,
    ulong TraceStepBudgetRejectedCount,
    ulong MipVisitBudgetRejectedCount,
    ulong DepthRejectedCount,
    ulong NormalRejectedCount,
    ulong TraceSourceRejectedCount,
    ulong NonFiniteRejectedCount)
{
    public ulong ProposalSampleCount { get; init; }
    public ulong GuidedProposalSampleCount { get; init; }
    public ulong GuidedValidSampleCount { get; init; }
    public ulong GuidedZeroContributionSampleCount { get; init; }
    public ulong CosineProposalSampleCount { get; init; }
    public ulong ValidSampleCount { get; init; }

    public static SimpleDdgiNearFieldResidualTraceTelemetry Empty { get; } = new(
        0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL);
}

/// <summary>
/// History reuse evidence.  Individual rejection reasons are inclusive
/// diagnostics and must not be summed to infer the rejected history count.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualHistoryTelemetry(
    ulong CandidateHistoryCount,
    ulong AcceptedHistoryCount,
    ulong RejectedHistoryCount,
    ulong InvalidCurrentRejectedCount,
    ulong HistoryEpochRejectedCount,
    ulong HitIdentityRejectedCount,
    ulong ReprojectionRejectedCount,
    ulong DepthRejectedCount,
    ulong NormalRejectedCount,
    ulong TraceSourceRevisionRejectedCount,
    ulong VarianceClippedCount,
    double MeanVariance,
    double MaximumVariance)
{
    public ulong ReceiverIdentityRejectedCount { get; init; }
    public ulong ReceiverRevisionRejectedCount { get; init; }
    public ulong HitRevisionRejectedCount { get; init; }
    public ulong SourceReactiveRejectedCount { get; init; }

    public static SimpleDdgiNearFieldResidualHistoryTelemetry Empty { get; } = new(
        0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0UL, 0.0, 0.0);

    [JsonIgnore]
    public bool HasFiniteStatistics =>
        double.IsFinite(MeanVariance) && MeanVariance >= 0.0 &&
        double.IsFinite(MaximumVariance) && MaximumVariance >= 0.0;
}

/// <summary>
/// Signed and absolute scene-linear residual-energy aggregates.  Signed energy
/// is deliberately retained so a frequency-separation defect cannot hide
/// behind an absolute-only metric.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualEnergyTelemetry(
    ulong SampleCount,
    double SignedResidualEnergy,
    double AbsoluteResidualEnergy,
    double SquaredResidualEnergy,
    double MaximumAbsoluteResidualEnergy,
    double LowFrequencyLeakage,
    ulong NonFiniteRejectedCount)
{
    public static SimpleDdgiNearFieldResidualEnergyTelemetry Empty { get; } = new(
        0UL, 0.0, 0.0, 0.0, 0.0, 0.0, 0UL);

    [JsonIgnore]
    public bool HasFiniteStatistics =>
        double.IsFinite(SignedResidualEnergy) &&
        double.IsFinite(AbsoluteResidualEnergy) && AbsoluteResidualEnergy >= 0.0 &&
        double.IsFinite(SquaredResidualEnergy) && SquaredResidualEnergy >= 0.0 &&
        double.IsFinite(MaximumAbsoluteResidualEnergy) &&
        MaximumAbsoluteResidualEnergy >= 0.0 &&
        double.IsFinite(LowFrequencyLeakage) && LowFrequencyLeakage >= 0.0;
}

/// <summary>Compaction evidence for the bounded C5 tile work list.</summary>
public readonly record struct SimpleDdgiNearFieldResidualTileTelemetry(
    uint TileCapacity,
    uint CandidateTileCount,
    uint CompactedTileCount,
    uint EmptyTileCount,
    uint OverflowTileCount,
    ulong TileRecordBytes)
{
    public uint ActiveTileCount { get; init; }
    /// <summary>
    /// Trace-resolution receivers rejected individually because their token,
    /// stable identity, revision, or motion contract could not be published.
    /// This does not invalidate unrelated surfaces or the complete frame.
    /// </summary>
    public uint InvalidSurfacePixelCount { get; init; }

    public static SimpleDdgiNearFieldResidualTileTelemetry Empty { get; } = new(
        0U, 0U, 0U, 0U, 0U, 0UL);
}

/// <summary>
/// Optional stable IDs for a debug capture and its scene-linear reference.
/// They are empty unless an authoritative telemetry payload names the capture.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualCaptureIdentifiers(
    string DebugCaptureId,
    string ReferenceCaptureId)
{
    public static SimpleDdgiNearFieldResidualCaptureIdentifiers None { get; } = new(
        string.Empty,
        string.Empty);

    public SimpleDdgiNearFieldResidualCaptureIdentifiers Normalize() => new(
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeIdentifier(DebugCaptureId),
        SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeIdentifier(ReferenceCaptureId));
}

/// <summary>
/// Recovery state for rejected fence-complete telemetry frames. A transient
/// rejection does not invalidate the last authoritative witness or alter the
/// adaptive governor. Three consecutive failures request one generation-safe
/// rebuild; a rebuilt generation has a bounded validation window.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualRecoveryTelemetry(
    uint ConsecutiveInvalidTelemetryFrames,
    uint GenerationRebuildAttemptCount,
    bool GenerationRebuildPending,
    ulong ValidationDeadlineFrame,
    string LastFailureReason)
{
    public static SimpleDdgiNearFieldResidualRecoveryTelemetry Empty { get; } =
        new(0U, 0U, false, 0UL, string.Empty);

    public SimpleDdgiNearFieldResidualRecoveryTelemetry Normalize() => new(
        ConsecutiveInvalidTelemetryFrames,
        GenerationRebuildAttemptCount,
        GenerationRebuildPending,
        GenerationRebuildPending ? ValidationDeadlineFrame : 0UL,
        string.IsNullOrWhiteSpace(LastFailureReason)
            ? string.Empty
            : SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(
                LastFailureReason));
}

/// <summary>
/// CPU-owned resolution-governor state paired with the fence-complete timing
/// sample. SampledExtent names the dispatch measured by the current timings;
/// ActiveExtent names the next dispatch extent after applying that sample.
/// </summary>
public readonly record struct SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry(
    SimpleDdgiNearFieldResidualExecutionExtent SampledExtent,
    SimpleDdgiNearFieldResidualExecutionExtent ActiveExtent,
    SimpleDdgiNearFieldResidualExecutionScale MaximumScale,
    ulong LastP95Microseconds,
    ulong AuthoritativeTimingSampleCount,
    uint WindowSampleCount,
    uint PromotionWindowStreak,
    uint PromotionCount,
    uint DemotionCount,
    bool ResolutionChangedAfterSample)
{
    public uint LowestTierOverBudgetEvaluationStreak { get; init; }
    public uint SuspendedFramesRemaining { get; init; }
    public uint SuspensionCount { get; init; }
    public bool PromotionEnabled { get; init; }

    public static SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry Empty { get; } =
        default;

    [JsonIgnore]
    public bool IsValid =>
        ActiveExtent.IsValid &&
        Enum.IsDefined(ActiveExtent.Scale) &&
        Enum.IsDefined(MaximumScale) &&
        ActiveExtent.Scale <= MaximumScale &&
        (!SampledExtent.IsValid || Enum.IsDefined(SampledExtent.Scale));

    public SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry Normalize()
    {
        if (!IsValid)
            return Empty;

        return this with
        {
            WindowSampleCount = Math.Min(
                WindowSampleCount,
                (uint)SimpleDdgiNearFieldResidualAdaptiveResolution.SampleWindowSize),
            PromotionWindowStreak = Math.Min(
                PromotionWindowStreak,
                (uint)SimpleDdgiNearFieldResidualAdaptiveResolution.PromotionWindowCount),
            LowestTierOverBudgetEvaluationStreak = Math.Min(
                LowestTierOverBudgetEvaluationStreak,
                (uint)SimpleDdgiNearFieldResidualAdaptiveResolution
                    .LowestTierOverBudgetEvaluationCount),
            SuspendedFramesRemaining = Math.Min(
                SuspendedFramesRemaining,
                (uint)SimpleDdgiNearFieldResidualAdaptiveResolution
                    .SuspensionFrameCount),
            ResolutionChangedAfterSample =
                ResolutionChangedAfterSample && SampledExtent.IsValid &&
                SampledExtent != ActiveExtent
        };
    }
}

/// <summary>
/// Persisted C5 observability contract. It is intentionally a data boundary:
/// the renderer publishes only common-frame, fence-complete pass/readback
/// evidence; a pre-integration plan remains explicitly pending rather than a
/// fabricated GPU measurement.
/// </summary>
public sealed record SimpleDdgiNearFieldResidualDiagnostics(
    uint ContractVersion,
    SimpleDdgiNearFieldResidualReadbackStatus Readback,
    SimpleDdgiNearFieldResidualMemoryTelemetry Memory,
    SimpleDdgiNearFieldResidualStageTimings Timings,
    SimpleDdgiNearFieldResidualTraceTelemetry Trace,
    SimpleDdgiNearFieldResidualHistoryTelemetry History,
    SimpleDdgiNearFieldResidualEnergyTelemetry ResidualEnergy,
    SimpleDdgiNearFieldResidualTileTelemetry Tiles,
    SimpleDdgiNearFieldResidualCaptureIdentifiers CaptureIdentifiers)
{
    public const uint CurrentContractVersion = 4U;

    public SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry
        AdaptiveResolution { get; init; } =
            SimpleDdgiNearFieldResidualAdaptiveResolutionTelemetry.Empty;

    public SimpleDdgiNearFieldResidualRecoveryTelemetry Recovery { get; init; } =
        SimpleDdgiNearFieldResidualRecoveryTelemetry.Empty;

    [JsonIgnore]
    public bool IsAuthoritativeReadback => Readback.IsAuthoritative;

    public static SimpleDdgiNearFieldResidualDiagnostics Disabled(
        string reason = "C5 near-field residual is disabled.") => new(
        CurrentContractVersion,
        SimpleDdgiNearFieldResidualReadbackStatus.Disabled(reason),
        SimpleDdgiNearFieldResidualMemoryTelemetry.Empty,
        SimpleDdgiNearFieldResidualStageTimings.Empty,
        SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
        SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
        SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
        SimpleDdgiNearFieldResidualTileTelemetry.Empty,
        SimpleDdgiNearFieldResidualCaptureIdentifiers.None);

    /// <summary>
    /// Exposes a CPU-side requested/admitted layout without claiming that any
    /// GPU resource, descriptor, pass, timestamp, or counter readback exists.
    /// </summary>
    public static SimpleDdgiNearFieldResidualDiagnostics PendingRendererIntegration(
        SimpleDdgiNearFieldResidualMemoryTelemetry memory,
        string reason = "C5 renderer integration or GPU readback was not supplied.") => new(
        CurrentContractVersion,
        SimpleDdgiNearFieldResidualReadbackStatus.PendingRendererIntegration(reason),
        memory.Normalize().WithoutLiveAllocation(),
        SimpleDdgiNearFieldResidualStageTimings.Empty,
        SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
        SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
        SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
        SimpleDdgiNearFieldResidualTileTelemetry.Empty,
        SimpleDdgiNearFieldResidualCaptureIdentifiers.None);

    /// <summary>
    /// Renderer resources and passes exist, but the complete counter/timestamp
    /// payload for a common fence-complete frame is not yet authoritative.
    /// Unlike pre-integration plans this preserves allocator-derived live
    /// bytes, which are independently measurable CPU-side facts.
    /// </summary>
    public static SimpleDdgiNearFieldResidualDiagnostics PendingGpuReadback(
        SimpleDdgiNearFieldResidualMemoryTelemetry memory,
        string reason = "C5 GPU telemetry is awaiting a fence-complete readback.") => new(
        CurrentContractVersion,
        SimpleDdgiNearFieldResidualReadbackStatus.PendingGpuReadback(reason),
        memory.Normalize(),
        SimpleDdgiNearFieldResidualStageTimings.Empty,
        SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
        SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
        SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
        SimpleDdgiNearFieldResidualTileTelemetry.Empty,
        SimpleDdgiNearFieldResidualCaptureIdentifiers.None);

    /// <summary>
    /// Publishes fence-complete counters while retaining the non-authoritative
    /// state until an exclusive timestamp sample for the same frame is also
    /// available. This keeps measured counters observable without presenting
    /// whole-pass Forward+ timing as C5-only cost.
    /// </summary>
    public static SimpleDdgiNearFieldResidualDiagnostics CreateCounterReadbackPending(
        in SimpleDdgiNearFieldResidualCompletionWitness witness,
        SimpleDdgiNearFieldResidualMemoryTelemetry memory,
        uint ageFrames = 0U,
        string reason = "C5 counters are valid; exclusive GPU timings are pending.") =>
        new SimpleDdgiNearFieldResidualDiagnostics(
            CurrentContractVersion,
            SimpleDdgiNearFieldResidualReadbackStatus.CounterReadbackPending(
                witness.CompletedFrameSerial,
                ageFrames,
                reason),
            memory,
            SimpleDdgiNearFieldResidualStageTimings.Empty,
            witness.Trace,
            witness.History,
            witness.ResidualEnergy,
            witness.Tiles,
            SimpleDdgiNearFieldResidualCaptureIdentifiers.None)
        .NormalizeForPersistence();

    public static SimpleDdgiNearFieldResidualDiagnostics Faulted(
        SimpleDdgiNearFieldResidualMemoryTelemetry memory,
        string reason) => new(
        CurrentContractVersion,
        new SimpleDdgiNearFieldResidualReadbackStatus(
            SimpleDdgiNearFieldResidualReadbackState.Faulted,
            false,
            false,
            0UL,
            0U,
            SimpleDdgiNearFieldResidualDiagnosticsText.NormalizeReason(reason)),
        memory.Normalize(),
        SimpleDdgiNearFieldResidualStageTimings.Empty,
        SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
        SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
        SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
        SimpleDdgiNearFieldResidualTileTelemetry.Empty,
        SimpleDdgiNearFieldResidualCaptureIdentifiers.None);

    /// <summary>
    /// Renderer integration must use this factory only after both timestamp
    /// and counter readbacks completed for the same frame serial.
    /// </summary>
    public static SimpleDdgiNearFieldResidualDiagnostics CreateAuthoritative(
        ulong completedFrameSerial,
        uint ageFrames,
        SimpleDdgiNearFieldResidualMemoryTelemetry memory,
        SimpleDdgiNearFieldResidualStageTimings timings,
        SimpleDdgiNearFieldResidualTraceTelemetry trace,
        SimpleDdgiNearFieldResidualHistoryTelemetry history,
        SimpleDdgiNearFieldResidualEnergyTelemetry residualEnergy,
        SimpleDdgiNearFieldResidualTileTelemetry tiles,
        SimpleDdgiNearFieldResidualCaptureIdentifiers captureIdentifiers) =>
        new SimpleDdgiNearFieldResidualDiagnostics(
        CurrentContractVersion,
        SimpleDdgiNearFieldResidualReadbackStatus.Available(completedFrameSerial, ageFrames),
        memory,
        timings,
        trace,
        history,
        residualEnergy,
        tiles,
        captureIdentifiers).NormalizeForPersistence();

    /// <summary>
    /// Normalizes untrusted deserialized data and converts incomplete or
    /// invalid samples into a non-authoritative state.  This is deliberately
    /// fail-closed: counters, timings, allocations, and capture IDs are erased
    /// unless their common readback status is authoritative.
    /// </summary>
    public SimpleDdgiNearFieldResidualDiagnostics NormalizeForPersistence()
    {
        if (ContractVersion != CurrentContractVersion)
        {
            return Disabled("C5 telemetry contract-version mismatch.");
        }

        SimpleDdgiNearFieldResidualReadbackStatus readback = Readback.Normalize();
        SimpleDdgiNearFieldResidualMemoryTelemetry memory = Memory.Normalize();
        bool countersAreValid = readback.CounterReadbackValid &&
            readback.CompletedFrameSerial != 0UL;
        if (countersAreValid &&
            (!History.HasFiniteStatistics || !ResidualEnergy.HasFiniteStatistics))
        {
            return new SimpleDdgiNearFieldResidualDiagnostics(
                CurrentContractVersion,
                new SimpleDdgiNearFieldResidualReadbackStatus(
                    SimpleDdgiNearFieldResidualReadbackState.Faulted,
                    false,
                    false,
                    0UL,
                    0U,
                    "C5 telemetry contained non-finite residual statistics."),
                memory,
                SimpleDdgiNearFieldResidualStageTimings.Empty,
                SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
                SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
                SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
                SimpleDdgiNearFieldResidualTileTelemetry.Empty,
                SimpleDdgiNearFieldResidualCaptureIdentifiers.None);
        }

        if (!readback.IsAuthoritative)
        {
            SimpleDdgiNearFieldResidualMemoryTelemetry nonAuthoritativeMemory =
                readback.State ==
                    SimpleDdgiNearFieldResidualReadbackState.PendingGpuReadback ||
                readback.State ==
                    SimpleDdgiNearFieldResidualReadbackState.Faulted
                    ? memory
                    : memory.WithoutLiveAllocation();
            return new SimpleDdgiNearFieldResidualDiagnostics(
                CurrentContractVersion,
                readback,
                nonAuthoritativeMemory,
                SimpleDdgiNearFieldResidualStageTimings.Empty,
                countersAreValid ? Trace : SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
                countersAreValid ? History : SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
                countersAreValid
                    ? ResidualEnergy
                    : SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
                countersAreValid ? Tiles : SimpleDdgiNearFieldResidualTileTelemetry.Empty,
                SimpleDdgiNearFieldResidualCaptureIdentifiers.None)
            {
                AdaptiveResolution = AdaptiveResolution.Normalize(),
                Recovery = Recovery.Normalize()
            };
        }

        if (!History.HasFiniteStatistics || !ResidualEnergy.HasFiniteStatistics)
        {
            return new SimpleDdgiNearFieldResidualDiagnostics(
                CurrentContractVersion,
                new SimpleDdgiNearFieldResidualReadbackStatus(
                    SimpleDdgiNearFieldResidualReadbackState.Faulted,
                    false,
                    false,
                    0UL,
                    0U,
                    "C5 telemetry contained non-finite residual statistics."),
                memory.WithoutLiveAllocation(),
                SimpleDdgiNearFieldResidualStageTimings.Empty,
                SimpleDdgiNearFieldResidualTraceTelemetry.Empty,
                SimpleDdgiNearFieldResidualHistoryTelemetry.Empty,
                SimpleDdgiNearFieldResidualEnergyTelemetry.Empty,
                SimpleDdgiNearFieldResidualTileTelemetry.Empty,
                SimpleDdgiNearFieldResidualCaptureIdentifiers.None);
        }

        return this with
        {
            ContractVersion = CurrentContractVersion,
            Readback = readback,
            Memory = memory,
            AdaptiveResolution = AdaptiveResolution.Normalize(),
            Recovery = Recovery.Normalize(),
            CaptureIdentifiers = CaptureIdentifiers.Normalize()
        };
    }
}

internal static class SimpleDdgiNearFieldResidualDiagnosticsText
{
    private const int MaximumReasonLength = 256;
    private const int MaximumIdentifierLength = 256;

    public static string NormalizeReason(string? value) =>
        Normalize(value, "C5 telemetry reason was unavailable.", MaximumReasonLength);

    public static string NormalizeIdentifier(string? value) =>
        Normalize(value, string.Empty, MaximumIdentifierLength);

    private static string Normalize(string? value, string fallback, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        string normalized = value.Trim();
        if (normalized.Length > maximumLength)
            return fallback;

        foreach (char character in normalized)
        {
            if (char.IsControl(character))
                return fallback;
        }

        return normalized;
    }
}
