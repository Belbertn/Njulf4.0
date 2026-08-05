using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Njulf.Rendering.Resources;

public readonly record struct SimpleDdgiResidencyCoverageFrame(
    int PredictedPageCount,
    int ActualPageCount,
    int FalseNegativePageCount,
    int FalsePositivePageCount,
    double FalseNegativeRate,
    double InflationRatio,
    int MaximumConsecutiveMissFrames);

public readonly record struct SimpleDdgiResidencyCoverageSummary(
    int FrameCount,
    double FalseNegativeRateP95,
    double InflationRatioP95,
    int MaximumConsecutiveMissFrames,
    bool MeetsInitialQualificationGate);

/// <summary>
/// Separate surface-page predictor oracle.  Geometric receiver coverage keeps
/// its existing semantics; this validator only compares page demand to exact
/// gather touches.
/// </summary>
public sealed class SimpleDdgiResidencyCoverageValidator
{
    public const double InitialFalseNegativeP95Gate = 0.005;
    public const double InitialInflationP95Gate = 1.5;
    public const int InitialMaximumConsecutiveMissGate = 2;

    private readonly byte[] _predicted;
    private readonly byte[] _actual;
    private readonly int[] _consecutiveMisses;
    private readonly double[] _falseNegativeRates;
    private readonly double[] _inflationRatios;
    private int _frameCount;
    private int _maximumConsecutiveMissFrames;

    public SimpleDdgiResidencyCoverageValidator(
        int virtualPageCount,
        int maximumRecordedFrames = 16_384)
    {
        if (virtualPageCount < 0)
            throw new ArgumentOutOfRangeException(nameof(virtualPageCount));
        if (maximumRecordedFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordedFrames));
        _predicted = new byte[virtualPageCount];
        _actual = new byte[virtualPageCount];
        _consecutiveMisses = new int[virtualPageCount];
        _falseNegativeRates = new double[maximumRecordedFrames];
        _inflationRatios = new double[maximumRecordedFrames];
    }

    public int VirtualPageCount => _predicted.Length;
    public int FrameCount => _frameCount;

    public SimpleDdgiResidencyCoverageFrame ObserveFrame(
        ReadOnlySpan<int> predictedPages,
        ReadOnlySpan<int> actualPages)
    {
        if (_frameCount >= _falseNegativeRates.Length)
        {
            throw new InvalidOperationException(
                "The predeclared residency coverage trajectory capacity was exceeded.");
        }

        Array.Clear(_predicted);
        Array.Clear(_actual);
        for (int index = 0; index < predictedPages.Length; index++)
            _predicted[ValidatePage(predictedPages[index])] = 1;
        for (int index = 0; index < actualPages.Length; index++)
            _actual[ValidatePage(actualPages[index])] = 1;

        int predictedCount = 0;
        int actualCount = 0;
        int falseNegatives = 0;
        int falsePositives = 0;
        for (int page = 0; page < _predicted.Length; page++)
        {
            bool predicted = _predicted[page] != 0;
            bool actual = _actual[page] != 0;
            if (predicted)
                predictedCount++;
            if (actual)
                actualCount++;
            if (actual && !predicted)
            {
                falseNegatives++;
                _consecutiveMisses[page] = checked(_consecutiveMisses[page] + 1);
                _maximumConsecutiveMissFrames = Math.Max(
                    _maximumConsecutiveMissFrames,
                    _consecutiveMisses[page]);
            }
            else
            {
                _consecutiveMisses[page] = 0;
            }
            if (predicted && !actual)
                falsePositives++;
        }

        double falseNegativeRate = actualCount > 0
            ? (double)falseNegatives / actualCount
            : 0.0;
        double inflationRatio = actualCount > 0
            ? (double)predictedCount / actualCount
            : predictedCount == 0 ? 1.0 : double.PositiveInfinity;
        _falseNegativeRates[_frameCount] = falseNegativeRate;
        _inflationRatios[_frameCount] = inflationRatio;
        _frameCount++;

        return new SimpleDdgiResidencyCoverageFrame(
            predictedCount,
            actualCount,
            falseNegatives,
            falsePositives,
            falseNegativeRate,
            inflationRatio,
            _maximumConsecutiveMissFrames);
    }

    public SimpleDdgiResidencyCoverageSummary GetSummary()
    {
        if (_frameCount == 0)
            return new SimpleDdgiResidencyCoverageSummary(0, 0.0, 1.0, 0, true);

        // Copying into fixed, preallocated temporary storage would double the
        // trajectory allocation.  The validator is offline/qualification-only;
        // sort the populated prefix in place after collection is complete.
        Array.Sort(_falseNegativeRates, 0, _frameCount);
        Array.Sort(_inflationRatios, 0, _frameCount);
        double falseNegativeP95 = _falseNegativeRates[PercentileIndex(_frameCount, 0.95)];
        double inflationP95 = _inflationRatios[PercentileIndex(_frameCount, 0.95)];
        bool meetsGate = falseNegativeP95 <= InitialFalseNegativeP95Gate &&
            inflationP95 <= InitialInflationP95Gate &&
            _maximumConsecutiveMissFrames <= InitialMaximumConsecutiveMissGate;
        return new SimpleDdgiResidencyCoverageSummary(
            _frameCount,
            falseNegativeP95,
            inflationP95,
            _maximumConsecutiveMissFrames,
            meetsGate);
    }

    private int ValidatePage(int page)
    {
        if ((uint)page >= (uint)_predicted.Length)
            throw new ArgumentOutOfRangeException(nameof(page));
        return page;
    }

    private static int PercentileIndex(int count, double percentile) =>
        Math.Clamp((int)Math.Ceiling(count * percentile) - 1, 0, count - 1);
}

public sealed record SimpleDdgiResidencyWorkingSetFrame(
    int FrameIndex,
    int PredictedPageCount,
    int ActualPageCount,
    int CurrentDemandPageCount,
    int AlternativeCurrentDemandPageCount,
    int FalseNegativePageCount,
    int FalsePositivePageCount,
    IReadOnlyList<int> RetainedPageCounts);

public readonly record struct SimpleDdgiResidencyRetentionSummary(
    int RetentionFrames,
    int RequiredPoolP50,
    int RequiredPoolP95,
    int RequiredPoolP99,
    int RequiredPoolMaximum);

public readonly record struct SimpleDdgiResidencyCapacitySimulation(
    int RetentionFrames,
    int PhysicalPageCapacity,
    int AdmissionCount,
    int EvictionCount,
    int FailedAdmissionCount,
    int PressureFrameCount,
    int MaximumConsecutivePressureFrames,
    int MaximumResidentPageCount);

public readonly record struct SimpleDdgiResidencyPageGeometrySummary(
    string Name,
    int PageDimensionX,
    int PageDimensionY,
    int PageDimensionZ,
    int VirtualPageCount,
    int CurrentDemandP50,
    int CurrentDemandP95,
    int CurrentDemandP99,
    int CurrentDemandMaximum,
    double CurrentDemandUtilizationP95);

public readonly record struct SimpleDdgiResidencyMemoryCandidate(
    string Name,
    int RetentionFrames,
    int PhysicalPageCapacity,
    SimpleDdgiMemoryPlan SparsePlan,
    ulong DenseEquivalentLiveBytes);

public readonly record struct SimpleDdgiResidencyMemoryProjection(
    string Name,
    int RetentionFrames,
    int PhysicalPageCapacity,
    int PhysicalProbeCapacity,
    int SampledAtlasPhysicalProbeCapacity,
    int SparsePagePaddingProbeCount,
    int SampledAtlasPaddingProbeCount,
    ulong SampledAtlasPaddingBytes,
    ulong PhysicalPayloadBytes,
    ulong ResidencyArenaBytes,
    ulong AllocatedLiveBytes,
    ulong DenseEquivalentLiveBytes,
    ulong AvoidedBytes);

public sealed record SimpleDdgiResidencyWorkingSetReport(
    int FrameCount,
    IReadOnlyList<int> RetentionIntervals,
    SimpleDdgiResidencyCoverageSummary Coverage,
    IReadOnlyList<SimpleDdgiResidencyWorkingSetFrame> Frames,
    IReadOnlyList<SimpleDdgiResidencyPageGeometrySummary> PageGeometries,
    IReadOnlyList<SimpleDdgiResidencyRetentionSummary> RetentionSummaries,
    IReadOnlyList<SimpleDdgiResidencyCapacitySimulation> CapacitySimulations,
    IReadOnlyList<SimpleDdgiResidencyMemoryProjection> MemoryProjections);

/// <summary>
/// Offline Shadow-mode trajectory calculator. It keeps exact page-set history,
/// compares the depth predictor with instrumented receiver touches, evaluates
/// candidate retention/capacity policies through the deterministic reference
/// allocator, and emits a JSON-safe report. It is intentionally separate from
/// shipping telemetry and never participates in frame scheduling.
/// </summary>
public sealed class SimpleDdgiResidencyWorkingSetAnalyzer
{
    private sealed class CapacityCandidate
    {
        private readonly SimpleDdgiProbePageReferenceModel _model;
        private readonly SimpleDdgiPageReferenceSettings _settings;
        private readonly SimpleDdgiPageTransition[] _transitions;

        public CapacityCandidate(
            int virtualPageCount,
            int retentionFrames,
            int physicalPageCapacity)
        {
            RetentionFrames = retentionFrames;
            PhysicalPageCapacity = physicalPageCapacity;
            _model = new SimpleDdgiProbePageReferenceModel(
                virtualPageCount,
                physicalPageCapacity);
            _settings = new SimpleDdgiPageReferenceSettings(
                (ulong)retentionFrames,
                physicalPageCapacity,
                physicalPageCapacity,
                EmptySuppressionConfirmationCount: 2,
                SuppressedRetryFrames: 300UL);
            _transitions = new SimpleDdgiPageTransition[
                checked(physicalPageCapacity * 2)];
        }

        public int RetentionFrames { get; }
        public int PhysicalPageCapacity { get; }
        public int AdmissionCount { get; private set; }
        public int EvictionCount { get; private set; }
        public int FailedAdmissionCount { get; private set; }
        public int PressureFrameCount { get; private set; }
        public int MaximumConsecutivePressureFrames { get; private set; }
        public int MaximumResidentPageCount { get; private set; }

        public void Observe(
            ulong frameSerial,
            ReadOnlySpan<SimpleDdgiPageDemand> demands)
        {
            Array.Clear(_transitions);
            SimpleDdgiPageReconcileSummary summary = _model.Reconcile(
                frameSerial,
                demands,
                _settings,
                _transitions);
            int transitionCount = checked(
                summary.AdmissionCount + summary.EvictionCount);
            for (int index = 0; index < transitionCount; index++)
            {
                SimpleDdgiPageTransition transition = _transitions[index];
                if (transition.Kind == SimpleDdgiPageTransitionKind.Admit)
                {
                    _model.MarkPublished(
                        transition.VirtualPageIndex,
                        frameSerial);
                }
            }

            AdmissionCount = checked(AdmissionCount + summary.AdmissionCount);
            EvictionCount = checked(EvictionCount + summary.EvictionCount);
            FailedAdmissionCount = checked(
                FailedAdmissionCount + summary.FailedAdmissionCount);
            if (summary.PoolPressure)
                PressureFrameCount = checked(PressureFrameCount + 1);
            MaximumConsecutivePressureFrames = Math.Max(
                MaximumConsecutivePressureFrames,
                summary.ConsecutivePressureFrames);
            MaximumResidentPageCount = Math.Max(
                MaximumResidentPageCount,
                summary.ResidentPageCount);
        }

        public SimpleDdgiResidencyCapacitySimulation Snapshot() => new(
            RetentionFrames,
            PhysicalPageCapacity,
            AdmissionCount,
            EvictionCount,
            FailedAdmissionCount,
            PressureFrameCount,
            MaximumConsecutivePressureFrames,
            MaximumResidentPageCount);
    }

    private readonly SimpleDdgiResidencyCoverageValidator _coverage;
    private readonly int[] _retentionIntervals;
    private readonly byte[] _predicted;
    private readonly byte[] _actual;
    private readonly byte[] _alternative;
    private readonly ulong[] _lastRelevantFrame;
    private readonly SimpleDdgiPageDemand[] _demandScratch;
    private readonly SimpleDdgiResidencyWorkingSetFrame[] _frames;
    private readonly int[][] _retainedPageCounts;
    private readonly int[] _currentDemandCounts;
    private readonly int[] _alternativeDemandCounts;
    private readonly CapacityCandidate[] _capacityCandidates;
    private int _frameCount;

    public SimpleDdgiResidencyWorkingSetAnalyzer(
        int virtualPageCount,
        int alternativeVirtualPageCount,
        IEnumerable<int> retentionIntervals,
        IEnumerable<int> physicalPageCapacities,
        int maximumRecordedFrames = 16_384)
    {
        if (virtualPageCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(virtualPageCount));
        if (alternativeVirtualPageCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(alternativeVirtualPageCount));
        }
        if (retentionIntervals == null)
            throw new ArgumentNullException(nameof(retentionIntervals));
        if (physicalPageCapacities == null)
            throw new ArgumentNullException(nameof(physicalPageCapacities));
        if (maximumRecordedFrames <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRecordedFrames));

        _retentionIntervals = retentionIntervals
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
        if (_retentionIntervals.Length == 0 ||
            _retentionIntervals.Any(static value => value < 1 || value > 3_600))
        {
            throw new ArgumentOutOfRangeException(nameof(retentionIntervals));
        }

        int[] capacities = physicalPageCapacities
            .Distinct()
            .OrderBy(static value => value)
            .ToArray();
        if (capacities.Length == 0 || capacities.Any(
                value => value < 0 || value > virtualPageCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(physicalPageCapacities));
        }

        _coverage = new SimpleDdgiResidencyCoverageValidator(
            virtualPageCount,
            maximumRecordedFrames);
        _predicted = new byte[virtualPageCount];
        _actual = new byte[virtualPageCount];
        _alternative = new byte[alternativeVirtualPageCount];
        _lastRelevantFrame = new ulong[virtualPageCount];
        _demandScratch = new SimpleDdgiPageDemand[virtualPageCount];
        _frames = new SimpleDdgiResidencyWorkingSetFrame[
            maximumRecordedFrames];
        _retainedPageCounts = new int[_retentionIntervals.Length][];
        for (int index = 0; index < _retainedPageCounts.Length; index++)
            _retainedPageCounts[index] = new int[maximumRecordedFrames];
        _currentDemandCounts = new int[maximumRecordedFrames];
        _alternativeDemandCounts = new int[maximumRecordedFrames];
        _capacityCandidates = new CapacityCandidate[checked(
            _retentionIntervals.Length * capacities.Length)];
        int candidateIndex = 0;
        foreach (int retention in _retentionIntervals)
        foreach (int capacity in capacities)
        {
            _capacityCandidates[candidateIndex++] = new CapacityCandidate(
                virtualPageCount,
                retention,
                capacity);
        }
    }

    public int FrameCount => _frameCount;

    public SimpleDdgiResidencyWorkingSetFrame ObserveFrame(
        ReadOnlySpan<int> predictedPages,
        ReadOnlySpan<int> actualPages) => ObserveFrame(
            predictedPages,
            actualPages,
            ReadOnlySpan<int>.Empty);

    public SimpleDdgiResidencyWorkingSetFrame ObserveFrame(
        ReadOnlySpan<int> predictedPages,
        ReadOnlySpan<int> actualPages,
        ReadOnlySpan<int> alternativeDemandPages)
    {
        if (_frameCount >= _frames.Length)
        {
            throw new InvalidOperationException(
                "The predeclared residency working-set trajectory capacity was exceeded.");
        }

        Array.Clear(_predicted);
        Array.Clear(_actual);
        Array.Clear(_alternative);
        MarkPages(predictedPages, _predicted, nameof(predictedPages));
        MarkPages(actualPages, _actual, nameof(actualPages));
        MarkPages(
            alternativeDemandPages,
            _alternative,
            nameof(alternativeDemandPages));

        ulong frameSerial = checked((ulong)_frameCount + 1UL);
        int demandCount = 0;
        for (int page = 0; page < _predicted.Length; page++)
        {
            bool predicted = _predicted[page] != 0;
            bool actual = _actual[page] != 0;
            if (!predicted && !actual)
                continue;
            _lastRelevantFrame[page] = frameSerial;
            _demandScratch[demandCount++] = new SimpleDdgiPageDemand(
                page,
                predicted
                    ? SimpleDdgiPageDemandClass.VisibleSurface
                    : SimpleDdgiPageDemandClass.ReceiverMiss,
                byte.MaxValue);
        }

        int[] retained = new int[_retentionIntervals.Length];
        for (int retentionIndex = 0;
            retentionIndex < _retentionIntervals.Length;
            retentionIndex++)
        {
            ulong retentionFrames = checked((ulong)
                _retentionIntervals[retentionIndex]);
            int retainedCount = 0;
            for (int page = 0; page < _lastRelevantFrame.Length; page++)
            {
                ulong lastRelevant = _lastRelevantFrame[page];
                if (lastRelevant != 0UL &&
                    frameSerial - lastRelevant < retentionFrames)
                {
                    retainedCount++;
                }
            }
            retained[retentionIndex] = retainedCount;
            _retainedPageCounts[retentionIndex][_frameCount] =
                retainedCount;
        }

        ReadOnlySpan<SimpleDdgiPageDemand> demands =
            _demandScratch.AsSpan(0, demandCount);
        for (int index = 0; index < _capacityCandidates.Length; index++)
            _capacityCandidates[index].Observe(frameSerial, demands);

        int alternativeDemandCount = CountMarked(_alternative);
        SimpleDdgiResidencyCoverageFrame coverage = _coverage.ObserveFrame(
            predictedPages,
            actualPages);
        var frame = new SimpleDdgiResidencyWorkingSetFrame(
            _frameCount,
            coverage.PredictedPageCount,
            coverage.ActualPageCount,
            demandCount,
            alternativeDemandCount,
            coverage.FalseNegativePageCount,
            coverage.FalsePositivePageCount,
            retained);
        _frames[_frameCount] = frame;
        _currentDemandCounts[_frameCount] = demandCount;
        _alternativeDemandCounts[_frameCount] = alternativeDemandCount;
        _frameCount++;
        return frame;
    }

    public SimpleDdgiResidencyWorkingSetReport CreateReport(
        IEnumerable<SimpleDdgiResidencyMemoryCandidate>? memoryCandidates = null)
    {
        var retention = new SimpleDdgiResidencyRetentionSummary[
            _retentionIntervals.Length];
        for (int index = 0; index < retention.Length; index++)
        {
            int[] values = SortedPrefix(
                _retainedPageCounts[index],
                _frameCount);
            retention[index] = new SimpleDdgiResidencyRetentionSummary(
                _retentionIntervals[index],
                Percentile(values, 0.50),
                Percentile(values, 0.95),
                Percentile(values, 0.99),
                values.Length == 0 ? 0 : values[^1]);
        }

        var geometry = new List<SimpleDdgiResidencyPageGeometrySummary>(2)
        {
            Geometry(
                "2x2x2",
                2,
                2,
                2,
                _predicted.Length,
                _currentDemandCounts)
        };
        if (_alternative.Length > 0)
        {
            geometry.Add(Geometry(
                "4x2x4",
                4,
                2,
                4,
                _alternative.Length,
                _alternativeDemandCounts));
        }

        SimpleDdgiResidencyMemoryProjection[] memory =
            (memoryCandidates ??
                Array.Empty<SimpleDdgiResidencyMemoryCandidate>())
            .Select(static candidate =>
            {
                SimpleDdgiMemoryPlan plan = candidate.SparsePlan;
                ulong avoided = candidate.DenseEquivalentLiveBytes >
                        plan.LiveBytes
                    ? candidate.DenseEquivalentLiveBytes - plan.LiveBytes
                    : 0UL;
                return new SimpleDdgiResidencyMemoryProjection(
                    candidate.Name,
                    candidate.RetentionFrames,
                    candidate.PhysicalPageCapacity,
                    plan.PhysicalProbeCapacity,
                    plan.SampledAtlasPhysicalProbeCapacity,
                    plan.SparsePagePaddingProbeCount,
                    plan.SampledAtlasPaddingProbeCount,
                    plan.SampledAtlasPaddingBytes,
                    plan.PhysicalPayloadBytes,
                    plan.ResidencyArenaBytes,
                    plan.LiveBytes,
                    candidate.DenseEquivalentLiveBytes,
                    avoided);
            })
            .ToArray();

        return new SimpleDdgiResidencyWorkingSetReport(
            _frameCount,
            _retentionIntervals.ToArray(),
            _coverage.GetSummary(),
            _frames.Take(_frameCount).ToArray(),
            geometry,
            retention,
            _capacityCandidates.Select(static candidate =>
                candidate.Snapshot()).ToArray(),
            memory);
    }

    public static void WriteJson(
        string path,
        SimpleDdgiResidencyWorkingSetReport report)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A report path is required.", nameof(path));
        if (report == null)
            throw new ArgumentNullException(nameof(report));

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") +
            ".tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    report,
                    new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void MarkPages(
        ReadOnlySpan<int> pages,
        byte[] destination,
        string parameterName)
    {
        for (int index = 0; index < pages.Length; index++)
        {
            int page = pages[index];
            if ((uint)page >= (uint)destination.Length)
                throw new ArgumentOutOfRangeException(parameterName);
            destination[page] = 1;
        }
    }

    private static int CountMarked(byte[] values)
    {
        int count = 0;
        for (int index = 0; index < values.Length; index++)
            count += values[index] != 0 ? 1 : 0;
        return count;
    }

    private SimpleDdgiResidencyPageGeometrySummary Geometry(
        string name,
        int x,
        int y,
        int z,
        int virtualPageCount,
        int[] source)
    {
        int[] values = SortedPrefix(source, _frameCount);
        int p50 = Percentile(values, 0.50);
        int p95 = Percentile(values, 0.95);
        int p99 = Percentile(values, 0.99);
        return new SimpleDdgiResidencyPageGeometrySummary(
            name,
            x,
            y,
            z,
            virtualPageCount,
            p50,
            p95,
            p99,
            values.Length == 0 ? 0 : values[^1],
            virtualPageCount > 0
                ? (double)p95 / virtualPageCount
                : 0.0);
    }

    private static int[] SortedPrefix(int[] source, int count)
    {
        int[] values = new int[count];
        Array.Copy(source, values, count);
        Array.Sort(values);
        return values;
    }

    private static int Percentile(int[] sorted, double percentile) =>
        sorted.Length == 0
            ? 0
            : sorted[Math.Clamp(
                (int)Math.Ceiling(sorted.Length * percentile) - 1,
                0,
                sorted.Length - 1)];
}
