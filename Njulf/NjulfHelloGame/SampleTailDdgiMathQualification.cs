using System;
using System.Collections.Generic;
using System.Linq;

namespace NjulfHelloGame;

public enum SampleTailDdgiSolverKind
{
    TailJacobi = 0,
    TailAccelerated = 1
}

public sealed record SampleTailDdgiMathSolveResult(
    SampleTailDdgiSolverKind Solver,
    int SolveEpochs,
    int CachedSweeps,
    double FixedPointDefect,
    double ReportedTailBound,
    double MeasuredAnalyticError,
    double Tolerance,
    bool Converged,
    bool ErrorWithinReportedBound);

public sealed record SampleTailDdgiMathCaseResult(
    string Name,
    int ProbeCount,
    double ContractionBound,
    SampleTailDdgiMathSolveResult Jacobi,
    SampleTailDdgiMathSolveResult Accelerated)
{
    public bool Passed =>
        Jacobi.Converged &&
        Accelerated.Converged &&
        Jacobi.ErrorWithinReportedBound &&
        Accelerated.ErrorWithinReportedBound;
}

public sealed record SampleTailDdgiMathQualificationReport(
    IReadOnlyList<SampleTailDdgiMathCaseResult> Cases,
    int JacobiSolveEpochs,
    int AcceleratedSolveEpochs,
    double SolveEpochReduction,
    bool AccuracyPassed,
    bool AccelerationPassed)
{
    public bool Passed => AccuracyPassed && AccelerationPassed;
}

/// <summary>
/// Deterministic positive-linear transport fixtures. Every system is of the
/// form L = S + A*L with ||A||_infinity &lt; 1, so
/// ||L*-L||_infinity &lt;= ||T(L)-L||_infinity/(1-q) is an analytic theorem,
/// not a comparison against another iterative renderer.
/// </summary>
public static class SampleTailDdgiMathQualification
{
    public const double RelativeTolerance = 0.001;
    public const double AbsoluteTolerance = 0.0001;
    public const int AcceleratedSweepsPerEpoch = 2;
    public const double RequiredAccelerationReduction = 0.30;
    private const int MaximumSolveEpochs = 20_000;
    private const double DoubleUnitRoundoff = 2.2204460492503131e-16;

    public static SampleTailDdgiMathQualificationReport Run()
    {
        TransportFixture[] fixtures =
        [
            CreateWhiteEnclosure("q = 0.95 white enclosure", 0.95),
            CreateWhiteEnclosure("q = 0.99 white enclosure", 0.99),
            CreateChain("2-probe chain", 2, 0.95),
            CreateChain("20-probe chain", 20, 0.95),
            CreateChain("128-probe chain", 128, 0.95),
            CreateThinSheet(),
            CreateChromaticEnclosure()
        ];

        SampleTailDdgiMathCaseResult[] results = fixtures
            .Select(Evaluate)
            .ToArray();
        int jacobiEpochs = results.Sum(static result => result.Jacobi.SolveEpochs);
        int acceleratedEpochs = results.Sum(
            static result => result.Accelerated.SolveEpochs);
        double reduction = jacobiEpochs > 0
            ? 1.0 - acceleratedEpochs / (double)jacobiEpochs
            : 0.0;
        bool accuracyPassed = results.Length == 7 &&
            results.All(static result => result.Passed);
        bool accelerationPassed = acceleratedEpochs < jacobiEpochs &&
            reduction >= RequiredAccelerationReduction;
        return new SampleTailDdgiMathQualificationReport(
            Array.AsReadOnly(results),
            jacobiEpochs,
            acceleratedEpochs,
            reduction,
            accuracyPassed,
            accelerationPassed);
    }

    private static SampleTailDdgiMathCaseResult Evaluate(TransportFixture fixture)
    {
        double[,] exact = SolveExact(fixture);
        SampleTailDdgiMathSolveResult jacobi = Iterate(
            fixture,
            exact,
            SampleTailDdgiSolverKind.TailJacobi);
        SampleTailDdgiMathSolveResult accelerated = Iterate(
            fixture,
            exact,
            SampleTailDdgiSolverKind.TailAccelerated);
        return new SampleTailDdgiMathCaseResult(
            fixture.Name,
            fixture.ProbeCount,
            fixture.ContractionBound,
            jacobi,
            accelerated);
    }

    private static SampleTailDdgiMathSolveResult Iterate(
        TransportFixture fixture,
        double[,] exact,
        SampleTailDdgiSolverKind solver)
    {
        var current = new double[fixture.ProbeCount, 3];
        var candidate = new double[fixture.ProbeCount, 3];
        int sweepsPerEpoch = solver == SampleTailDdgiSolverKind.TailAccelerated
            ? AcceleratedSweepsPerEpoch
            : 1;
        double defect = double.PositiveInfinity;
        double bound = double.PositiveInfinity;
        double error = double.PositiveInfinity;
        double tolerance = double.NaN;

        for (int epoch = 1; epoch <= MaximumSolveEpochs; epoch++)
        {
            if (solver == SampleTailDdgiSolverKind.TailJacobi)
            {
                ApplyOperator(fixture, current, candidate);
                (current, candidate) = (candidate, current);
            }
            else
            {
                for (int sweep = 0; sweep < sweepsPerEpoch; sweep++)
                    ApplyRedBlackSweep(fixture, current);
            }

            ApplyOperator(fixture, current, candidate);
            defect = InfinityDistance(candidate, current);
            double magnitude = InfinityMagnitude(current);
            // Enclose both operator evaluation and the direct-solve oracle.
            // The geometric term is exact in real arithmetic; this outward
            // roundoff term prevents a sub-ULP host subtraction from making
            // the measured double-precision oracle appear larger than the
            // mathematically certified bound.
            double geometricBound =
                defect / (1.0 - fixture.ContractionBound);
            double arithmeticRoundoffBound =
                32.0 * DoubleUnitRoundoff * Math.Max(1.0, magnitude) /
                (1.0 - fixture.ContractionBound);
            bound = Math.BitIncrement(
                geometricBound + arithmeticRoundoffBound);
            error = InfinityDistance(exact, current);
            tolerance = Math.Max(AbsoluteTolerance, RelativeTolerance * magnitude);
            if (bound <= tolerance)
            {
                return new SampleTailDdgiMathSolveResult(
                    solver,
                    epoch,
                    checked(epoch * sweepsPerEpoch),
                    defect,
                    bound,
                    error,
                    tolerance,
                    Converged: true,
                    ErrorWithinReportedBound: IsWithinOutwardRoundedBound(
                        error,
                        bound));
            }
        }

        return new SampleTailDdgiMathSolveResult(
            solver,
            MaximumSolveEpochs,
            checked(MaximumSolveEpochs * sweepsPerEpoch),
            defect,
            bound,
            error,
            tolerance,
            Converged: false,
            ErrorWithinReportedBound: IsWithinOutwardRoundedBound(error, bound));
    }

    private static void ApplyOperator(
        TransportFixture fixture,
        double[,] input,
        double[,] output)
    {
        for (int probe = 0; probe < fixture.ProbeCount; probe++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double value = fixture.Source[probe, channel];
                for (int sourceProbe = 0;
                     sourceProbe < fixture.ProbeCount;
                     sourceProbe++)
                {
                    value += fixture.Transfer[channel, probe, sourceProbe] *
                        input[sourceProbe, channel];
                }
                output[probe, channel] = value;
            }
        }
    }

    private static void ApplyRedBlackSweep(
        TransportFixture fixture,
        double[,] current)
    {
        for (int color = 0; color < 2; color++)
        {
            for (int probe = color;
                 probe < fixture.ProbeCount;
                 probe += 2)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    double value = fixture.Source[probe, channel];
                    for (int sourceProbe = 0;
                         sourceProbe < fixture.ProbeCount;
                         sourceProbe++)
                    {
                        value += fixture.Transfer[channel, probe, sourceProbe] *
                            current[sourceProbe, channel];
                    }
                    current[probe, channel] = value;
                }
            }
        }
    }

    private static double[,] SolveExact(TransportFixture fixture)
    {
        var result = new double[fixture.ProbeCount, 3];
        for (int channel = 0; channel < 3; channel++)
        {
            int count = fixture.ProbeCount;
            var augmented = new double[count, count + 1];
            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < count; column++)
                {
                    augmented[row, column] =
                        (row == column ? 1.0 : 0.0) -
                        fixture.Transfer[channel, row, column];
                }
                augmented[row, count] = fixture.Source[row, channel];
            }

            for (int pivot = 0; pivot < count; pivot++)
            {
                int best = pivot;
                for (int row = pivot + 1; row < count; row++)
                {
                    if (Math.Abs(augmented[row, pivot]) >
                        Math.Abs(augmented[best, pivot]))
                    {
                        best = row;
                    }
                }
                if (Math.Abs(augmented[best, pivot]) <= 1e-15)
                    throw new InvalidOperationException(
                        $"Tail fixture '{fixture.Name}' is singular.");
                if (best != pivot)
                    SwapRows(augmented, best, pivot, count + 1);

                double divisor = augmented[pivot, pivot];
                for (int column = pivot; column <= count; column++)
                    augmented[pivot, column] /= divisor;
                for (int row = 0; row < count; row++)
                {
                    if (row == pivot)
                        continue;
                    double factor = augmented[row, pivot];
                    if (factor == 0.0)
                        continue;
                    for (int column = pivot; column <= count; column++)
                    {
                        augmented[row, column] -=
                            factor * augmented[pivot, column];
                    }
                }
            }

            for (int probe = 0; probe < count; probe++)
                result[probe, channel] = augmented[probe, count];
        }
        return result;
    }

    private static void SwapRows(
        double[,] matrix,
        int left,
        int right,
        int width)
    {
        for (int column = 0; column < width; column++)
        {
            (matrix[left, column], matrix[right, column]) =
                (matrix[right, column], matrix[left, column]);
        }
    }

    private static double InfinityDistance(double[,] left, double[,] right)
    {
        double maximum = 0.0;
        for (int probe = 0; probe < left.GetLength(0); probe++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                maximum = Math.Max(
                    maximum,
                    Math.Abs(left[probe, channel] - right[probe, channel]));
            }
        }
        return maximum;
    }

    private static double InfinityMagnitude(double[,] values)
    {
        double maximum = 0.0;
        foreach (double value in values)
            maximum = Math.Max(maximum, Math.Abs(value));
        return maximum;
    }

    private static bool IsWithinOutwardRoundedBound(double error, double bound)
    {
        double outward = Math.BitIncrement(bound);
        return double.IsFinite(error) && double.IsFinite(outward) && error <= outward;
    }

    private static TransportFixture CreateWhiteEnclosure(string name, double q)
    {
        var source = new double[1, 3];
        var transfer = new double[3, 1, 1];
        for (int channel = 0; channel < 3; channel++)
        {
            source[0, channel] = 1.0;
            transfer[channel, 0, 0] = q;
        }
        return CreateFixture(name, source, transfer);
    }

    private static TransportFixture CreateChain(string name, int count, double q)
    {
        var source = new double[count, 3];
        var transfer = new double[3, count, count];
        source[0, 0] = 1.0;
        source[0, 1] = 0.8;
        source[0, 2] = 0.6;
        for (int probe = 1; probe < count; probe++)
        {
            for (int channel = 0; channel < 3; channel++)
                transfer[channel, probe, probe - 1] = q;
        }
        return CreateFixture(name, source, transfer);
    }

    private static TransportFixture CreateThinSheet()
    {
        const int count = 4;
        var source = new double[count, 3];
        var transfer = new double[3, count, count];
        source[0, 0] = 1.0;
        source[0, 1] = 0.7;
        source[0, 2] = 0.4;
        source[3, 0] = 0.1;
        source[3, 1] = 0.3;
        source[3, 2] = 0.8;
        for (int channel = 0; channel < 3; channel++)
        {
            // Probe 1 receives a reflected lobe from the front and a
            // transmitted lobe from behind. Their common row sum remains 0.95.
            transfer[channel, 1, 0] = 0.60;
            transfer[channel, 1, 2] = 0.35;
            transfer[channel, 0, 1] = 0.90;
            transfer[channel, 2, 1] = 0.55;
            transfer[channel, 2, 3] = 0.35;
            transfer[channel, 3, 2] = 0.90;
        }
        return CreateFixture(
            "reflected + transmitted thin sheet",
            source,
            transfer);
    }

    private static TransportFixture CreateChromaticEnclosure()
    {
        var source = new double[1, 3]
        {
            { 0.8, 0.35, 0.12 }
        };
        var transfer = new double[3, 1, 1];
        transfer[0, 0, 0] = 0.99;
        transfer[1, 0, 0] = 0.88;
        transfer[2, 0, 0] = 0.72;
        return CreateFixture("chromatic enclosure", source, transfer);
    }

    private static TransportFixture CreateFixture(
        string name,
        double[,] source,
        double[,,] transfer)
    {
        int probeCount = source.GetLength(0);
        double q = 0.0;
        for (int channel = 0; channel < 3; channel++)
        {
            for (int row = 0; row < probeCount; row++)
            {
                double rowSum = 0.0;
                for (int column = 0; column < probeCount; column++)
                {
                    double coefficient = transfer[channel, row, column];
                    if (!double.IsFinite(coefficient) || coefficient < 0.0)
                        throw new ArgumentException("Transport coefficients must be finite and non-negative.");
                    rowSum += coefficient;
                }
                q = Math.Max(q, rowSum);
            }
        }
        if (q >= 1.0)
            throw new ArgumentException("A qualification fixture must be contractive.");
        return new TransportFixture(name, source, transfer, q);
    }

    private sealed record TransportFixture(
        string Name,
        double[,] Source,
        double[,,] Transfer,
        double ContractionBound)
    {
        public int ProbeCount => Source.GetLength(0);
    }
}
