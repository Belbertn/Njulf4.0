using System;
using System.Numerics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

/// <summary>
/// Immutable description of the equal-area direction domain used by C3.  The
/// resolution is deliberately restricted to qualification candidates; changing
/// it changes the persistent distribution ABI and must not be an incidental
/// quality-slider value.
/// </summary>
public readonly record struct SimpleDdgiGuidingDistributionConfiguration(
    int LeafResolution)
{
    public static SimpleDdgiGuidingDistributionConfiguration FourByFour { get; } =
        new(4);
    public static SimpleDdgiGuidingDistributionConfiguration EightByEight { get; } =
        new(8);
    public static SimpleDdgiGuidingDistributionConfiguration SixteenBySixteen { get; } =
        new(16);

    public int LeafCount
    {
        get
        {
            Validate();
            return checked(LeafResolution * LeafResolution);
        }
    }

    /// <summary>Number of quadtree levels below the root.</summary>
    public int QuadtreeDepth
    {
        get
        {
            Validate();
            int depth = 0;
            for (int side = LeafResolution; side > 1; side >>= 1)
                depth++;
            return depth;
        }
    }

    /// <summary>
    /// Total leaf-first hierarchy entries: 21 for 4x4, 85 for 8x8, and 341
    /// for 16x16.
    /// </summary>
    public int HierarchyWeightCount
    {
        get
        {
            Validate();
            int count = 0;
            for (int side = LeafResolution; side >= 1; side >>= 1)
                count = checked(count + checked(side * side));
            return count;
        }
    }

    public double LeafSolidAngle => 4.0d * Math.PI / LeafCount;

    public void Validate()
    {
        if (LeafResolution is not 4 and not 8 and not 16)
        {
            throw new ArgumentOutOfRangeException(nameof(LeafResolution),
                "C3 supports only 4x4, 8x8, or 16x16 equal-area leaves.");
        }
    }

    /// <summary>
    /// Returns the leaf-first offset for a square hierarchy level. For an 8x8
    /// hierarchy, the offsets are 0 (8x8 leaves), 64 (4x4), 80 (2x2), and 84
    /// (root).
    /// </summary>
    public int GetLevelOffset(int sideLength)
    {
        Validate();
        ValidateSideLength(sideLength);

        int offset = 0;
        for (int current = LeafResolution; current > sideLength; current >>= 1)
            offset = checked(offset + checked(current * current));
        return offset;
    }

    public int GetNodeIndex(int sideLength, int x, int y)
    {
        ValidateSideLength(sideLength);
        if ((uint)x >= (uint)sideLength || (uint)y >= (uint)sideLength)
            throw new ArgumentOutOfRangeException(nameof(x));
        return checked(GetLevelOffset(sideLength) + checked(y * sideLength) + x);
    }

    public int GetLeafIndex(int x, int y) => GetNodeIndex(LeafResolution, x, y);

    public (int X, int Y) GetLeafCoordinates(int leafIndex)
    {
        if ((uint)leafIndex >= (uint)LeafCount)
            throw new ArgumentOutOfRangeException(nameof(leafIndex));
        return (leafIndex % LeafResolution, leafIndex / LeafResolution);
    }

    private void ValidateSideLength(int sideLength)
    {
        if (sideLength < 1 || sideLength > LeafResolution ||
            (sideLength & (sideLength - 1)) != 0 ||
            LeafResolution % sideLength != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sideLength));
        }
    }
}

public enum SimpleDdgiGuidingHierarchyValidationFailure : byte
{
    None = 0,
    IncorrectWeightCount = 1,
    NonFiniteWeight = 2,
    NegativeWeight = 3,
    WeightAboveOne = 4,
    InvalidRoot = 5,
    ParentChildMismatch = 6
}

public readonly record struct SimpleDdgiGuidingHierarchyValidation(
    bool IsValid,
    SimpleDdgiGuidingHierarchyValidationFailure Failure,
    string Reason)
{
    public static SimpleDdgiGuidingHierarchyValidation Valid { get; } =
        new(true, SimpleDdgiGuidingHierarchyValidationFailure.None, "valid");
}

public enum SimpleDdgiGuidingHierarchyBuildFailure : byte
{
    None = 0,
    InvalidLeafEnergy = 1,
    ZeroFiniteEnergy = 2,
    QuantizedValidationFailed = 3
}

/// <summary>Fail-closed result of building a persistent quantized hierarchy.</summary>
public readonly record struct SimpleDdgiGuidingHierarchyBuildResult(
    SimpleDdgiGuidingQuantizedHierarchy Hierarchy,
    bool UsedUniformFallback,
    SimpleDdgiGuidingHierarchyBuildFailure Failure,
    float TotalIncidentEnergy,
    bool TotalIncidentEnergyClamped);

/// <summary>A sampled equal-area direction and its exact quantized-guide PDF.</summary>
public readonly record struct SimpleDdgiGuidingSample(
    Vector3 Direction,
    int LeafIndex,
    double GuidedPdf,
    double SquareU,
    double SquareV);

/// <summary>
/// CPU reference representation of the FP16 persistent hierarchy. All PDF
/// evaluation and sampling reads the same quantized child weights. It never
/// consults the FP32 training histogram after publication.
/// </summary>
public sealed class SimpleDdgiGuidingQuantizedHierarchy
{
    // A binary16 value is rounded by less than 0.0005 near one. Four children
    // and independently quantized parents require a slightly larger bound.
    private const double ParentChildAbsoluteTolerance = 0.003d;
    private const double ParentChildRelativeTolerance = 0.003d;
    private const double RootTolerance = 0.003d;

    private readonly Half[] _weights;

    public SimpleDdgiGuidingQuantizedHierarchy(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        ReadOnlySpan<Half> weights)
    {
        configuration.Validate();
        if (weights.Length != configuration.HierarchyWeightCount)
        {
            throw new ArgumentException(
                "The quantized hierarchy does not match its configured resolution.",
                nameof(weights));
        }

        Configuration = configuration;
        _weights = weights.ToArray();
    }

    public SimpleDdgiGuidingDistributionConfiguration Configuration { get; }

    /// <summary>
    /// Read-only access to leaf-first hierarchy weights. Callers that need to
    /// alter a test vector must use <see cref="CopyWeights"/> and construct a
    /// new hierarchy, preserving publication immutability.
    /// </summary>
    public ReadOnlySpan<Half> Weights => _weights;

    public static SimpleDdgiGuidingQuantizedHierarchy CreateUniform(
        SimpleDdgiGuidingDistributionConfiguration configuration)
    {
        configuration.Validate();
        double[] leafEnergy = new double[configuration.LeafCount];
        Array.Fill(leafEnergy, 1.0d);
        SimpleDdgiGuidingHierarchyBuildResult result =
            BuildFromLeafEnergies(configuration, leafEnergy);
        return result.Hierarchy;
    }

    /// <summary>
    /// Builds hierarchy probabilities from per-leaf incident-energy estimates.
    /// Non-finite/negative input and zero energy are published as a uniform
    /// guide, never as a partially valid stale guide.
    /// </summary>
    public static SimpleDdgiGuidingHierarchyBuildResult BuildFromLeafEnergies(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        ReadOnlySpan<double> leafEnergy)
    {
        configuration.Validate();
        if (leafEnergy.Length != configuration.LeafCount)
        {
            throw new ArgumentException(
                "Leaf energy must contain exactly one value per configured leaf.",
                nameof(leafEnergy));
        }

        double maximumEnergy = 0.0d;
        for (int index = 0; index < leafEnergy.Length; index++)
        {
            double energy = leafEnergy[index];
            if (!double.IsFinite(energy) || energy < 0.0d)
            {
                return UniformFallback(configuration,
                    SimpleDdgiGuidingHierarchyBuildFailure.InvalidLeafEnergy);
            }
            maximumEnergy = Math.Max(maximumEnergy, energy);
        }

        if (maximumEnergy <= 0.0d)
        {
            return UniformFallback(configuration,
                SimpleDdgiGuidingHierarchyBuildFailure.ZeroFiniteEnergy);
        }

        // Scaling by the largest finite value avoids overflow for HDR training
        // accumulators while preserving every normalized leaf probability.
        double scaledTotal = 0.0d;
        for (int index = 0; index < leafEnergy.Length; index++)
            scaledTotal += leafEnergy[index] / maximumEnergy;
        if (!double.IsFinite(scaledTotal) || scaledTotal <= 0.0d)
        {
            return UniformFallback(configuration,
                SimpleDdgiGuidingHierarchyBuildFailure.InvalidLeafEnergy);
        }

        double[] hierarchy = new double[configuration.HierarchyWeightCount];
        for (int leaf = 0; leaf < configuration.LeafCount; leaf++)
            hierarchy[leaf] = leafEnergy[leaf] / maximumEnergy / scaledTotal;

        BuildParentSums(configuration, hierarchy);
        // The root is mathematically one. Making that fact explicit avoids a
        // platform-specific last-bit accumulation disagreement in the ABI.
        hierarchy[configuration.GetNodeIndex(1, 0, 0)] = 1.0d;

        Half[] quantized = new Half[hierarchy.Length];
        for (int index = 0; index < hierarchy.Length; index++)
            quantized[index] = (Half)Math.Clamp(hierarchy[index], 0.0d, 1.0d);

        var result = new SimpleDdgiGuidingQuantizedHierarchy(configuration, quantized);
        SimpleDdgiGuidingHierarchyValidation validation = result.Validate();
        if (!validation.IsValid)
        {
            return UniformFallback(configuration,
                SimpleDdgiGuidingHierarchyBuildFailure.QuantizedValidationFailed);
        }

        double totalIncidentEnergy = maximumEnergy * scaledTotal;
        bool clamped = !double.IsFinite(totalIncidentEnergy) ||
            totalIncidentEnergy > float.MaxValue;
        float finiteScale = clamped
            ? float.MaxValue
            : checked((float)totalIncidentEnergy);
        return new SimpleDdgiGuidingHierarchyBuildResult(
            result,
            UsedUniformFallback: false,
            SimpleDdgiGuidingHierarchyBuildFailure.None,
            finiteScale,
            clamped);
    }

    public static SimpleDdgiGuidingHierarchyBuildResult BuildFromLeafEnergies(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        ReadOnlySpan<float> leafEnergy)
    {
        double[] converted = new double[leafEnergy.Length];
        for (int index = 0; index < leafEnergy.Length; index++)
            converted[index] = leafEnergy[index];
        return BuildFromLeafEnergies(configuration, converted);
    }

    public Half[] CopyWeights() => _weights.ToArray();

    public SimpleDdgiGuidingHierarchyValidation Validate()
    {
        if (_weights.Length != Configuration.HierarchyWeightCount)
        {
            return Invalid(
                SimpleDdgiGuidingHierarchyValidationFailure.IncorrectWeightCount,
                "weight-count-does-not-match-configuration");
        }

        for (int index = 0; index < _weights.Length; index++)
        {
            float weight = (float)_weights[index];
            if (!float.IsFinite(weight))
            {
                return Invalid(
                    SimpleDdgiGuidingHierarchyValidationFailure.NonFiniteWeight,
                    $"weight-{index}-is-not-finite");
            }
            if (weight < 0.0f)
            {
                return Invalid(
                    SimpleDdgiGuidingHierarchyValidationFailure.NegativeWeight,
                    $"weight-{index}-is-negative");
            }
            if (weight > 1.0f)
            {
                return Invalid(
                    SimpleDdgiGuidingHierarchyValidationFailure.WeightAboveOne,
                    $"weight-{index}-exceeds-one");
            }
        }

        double root = WeightAt(Configuration.GetNodeIndex(1, 0, 0));
        if (Math.Abs(root - 1.0d) > RootTolerance)
        {
            return Invalid(
                SimpleDdgiGuidingHierarchyValidationFailure.InvalidRoot,
                "quantized-root-is-not-normalized");
        }

        for (int parentSide = 1;
             parentSide < Configuration.LeafResolution;
             parentSide <<= 1)
        {
            int childSide = parentSide << 1;
            for (int y = 0; y < parentSide; y++)
            for (int x = 0; x < parentSide; x++)
            {
                double parent = WeightAt(Configuration.GetNodeIndex(parentSide, x, y));
                double children = SumChildren(childSide, x, y);
                double tolerance = ParentChildAbsoluteTolerance +
                    ParentChildRelativeTolerance * Math.Max(parent, children);
                if (Math.Abs(parent - children) > tolerance)
                {
                    return Invalid(
                        SimpleDdgiGuidingHierarchyValidationFailure.ParentChildMismatch,
                        $"parent-{parentSide}-{x}-{y}-does-not-match-children");
                }
            }
        }

        return SimpleDdgiGuidingHierarchyValidation.Valid;
    }

    /// <summary>
    /// Returns the exact probability mass selected by the hierarchy after FP16
    /// quantization. This is intentionally calculated from child comparisons,
    /// not by reading a newer parent/leaf histogram.
    /// </summary>
    public double EvaluateGuidedLeafProbability(int leafIndex)
    {
        EnsureValid();
        (int leafX, int leafY) = Configuration.GetLeafCoordinates(leafIndex);
        return EvaluateLeafProbabilityUnchecked(leafX, leafY);
    }

    public double EvaluateGuidedPdf(Vector3 direction)
    {
        EnsureValid();
        int leafIndex = GetLeafIndex(direction);
        (int leafX, int leafY) = Configuration.GetLeafCoordinates(leafIndex);
        return EvaluateLeafProbabilityUnchecked(leafX, leafY) /
            Configuration.LeafSolidAngle;
    }

    /// <summary>
    /// Samples the hierarchy using one progressively remapped branch variate and
    /// two independent intra-leaf variates. All inputs are required to be
    /// strictly inside [0,1), exactly matching stable hash contracts.
    /// </summary>
    public SimpleDdgiGuidingSample SampleGuided(
        double branchVariate,
        double intraLeafU,
        double intraLeafV)
    {
        EnsureValid();
        ValidateUnitOpen(branchVariate, nameof(branchVariate));
        ValidateUnitOpen(intraLeafU, nameof(intraLeafU));
        ValidateUnitOpen(intraLeafV, nameof(intraLeafV));

        int side = 1;
        int x = 0;
        int y = 0;
        double remaining = branchVariate;
        double leafProbability = 1.0d;
        Span<double> children = stackalloc double[4];

        while (side < Configuration.LeafResolution)
        {
            int childSide = side << 1;
            ReadChildWeights(childSide, x, y, children);
            double childSum = children[0] + children[1] + children[2] + children[3];
            int selected;
            double conditionalProbability;
            if (childSum <= 0.0d)
            {
                double scaled = remaining * 4.0d;
                selected = Math.Min(3, (int)scaled);
                remaining = ClampUnitOpen(scaled - selected);
                conditionalProbability = 0.25d;
            }
            else
            {
                double target = remaining * childSum;
                double cumulative = 0.0d;
                selected = 3;
                for (int candidate = 0; candidate < 4; candidate++)
                {
                    double next = cumulative + children[candidate];
                    if (target < next || candidate == 3)
                    {
                        selected = candidate;
                        break;
                    }
                    cumulative = next;
                }

                double selectedWeight = children[selected];
                // A zero-weight final child can only be selected through a
                // round-off endpoint. It has zero probability, so retrying the
                // last positive child keeps the sample/PDF pair self-consistent.
                if (selectedWeight <= 0.0d)
                {
                    for (int candidate = 2; candidate >= 0; candidate--)
                    {
                        if (children[candidate] > 0.0d)
                        {
                            selected = candidate;
                            selectedWeight = children[candidate];
                            break;
                        }
                    }
                }

                conditionalProbability = selectedWeight / childSum;
                remaining = ClampUnitOpen((target - cumulative) / selectedWeight);
            }

            leafProbability *= conditionalProbability;
            x = checked(x * 2 + (selected & 1));
            y = checked(y * 2 + (selected >> 1));
            side = childSide;
        }

        int leafIndex = Configuration.GetLeafIndex(x, y);
        double u = (x + intraLeafU) / Configuration.LeafResolution;
        double v = (y + intraLeafV) / Configuration.LeafResolution;
        return new SimpleDdgiGuidingSample(
            DirectionFromSquare(u, v),
            leafIndex,
            leafProbability / Configuration.LeafSolidAngle,
            u,
            v);
    }

    public int GetLeafIndex(Vector3 direction)
    {
        if (!TryDirectionToSquare(direction, out double u, out double v))
            throw new ArgumentOutOfRangeException(nameof(direction));
        int x = Math.Min(Configuration.LeafResolution - 1,
            (int)Math.Floor(u * Configuration.LeafResolution));
        int y = Math.Min(Configuration.LeafResolution - 1,
            (int)Math.Floor(v * Configuration.LeafResolution));
        return Configuration.GetLeafIndex(x, y);
    }

    public Vector3 DirectionFromLeaf(
        int leafIndex,
        double intraLeafU,
        double intraLeafV)
        => DirectionFromLeaf(Configuration, leafIndex, intraLeafU, intraLeafV);

    /// <summary>
    /// Reconstructs the canonical equal-area direction represented by a leaf
    /// and the packed intra-leaf sample. This intentionally has no dependency
    /// on a learned hierarchy, allowing source-cache identity validation after
    /// a guide has changed.
    /// </summary>
    public static Vector3 DirectionFromLeaf(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        int leafIndex,
        double intraLeafU,
        double intraLeafV)
    {
        configuration.Validate();
        ValidateUnitOpen(intraLeafU, nameof(intraLeafU));
        ValidateUnitOpen(intraLeafV, nameof(intraLeafV));
        (int x, int y) = configuration.GetLeafCoordinates(leafIndex);
        return DirectionFromSquare(
            (x + intraLeafU) / configuration.LeafResolution,
            (y + intraLeafV) / configuration.LeafResolution);
    }

    /// <summary>Maps a finite nonzero direction onto the periodic equal-area square.</summary>
    public static bool TryDirectionToSquare(
        Vector3 direction,
        out double u,
        out double v)
    {
        u = 0.0d;
        v = 0.0d;
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) ||
            !float.IsFinite(direction.Z))
        {
            return false;
        }

        double lengthSquared = (double)direction.X * direction.X +
            (double)direction.Y * direction.Y +
            (double)direction.Z * direction.Z;
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0.0d)
            return false;

        double inverseLength = 1.0d / Math.Sqrt(lengthSquared);
        double x = direction.X * inverseLength;
        double y = direction.Y * inverseLength;
        double z = Math.Clamp(direction.Z * inverseLength, -1.0d, 1.0d);
        u = Math.Atan2(y, x) / (2.0d * Math.PI);
        if (u < 0.0d)
            u += 1.0d;
        // atan2 nominally returns [-pi, pi], but this canonicalizes any rare
        // platform endpoint to the periodic seam as well.
        if (u >= 1.0d)
            u = 0.0d;
        v = z * 0.5d + 0.5d;
        return true;
    }

    /// <summary>Inverse equal-area mapping. u is periodic; v includes the poles.</summary>
    public static Vector3 DirectionFromSquare(double u, double v)
    {
        ValidateUnitOpen(u, nameof(u));
        if (!double.IsFinite(v) || v < 0.0d || v > 1.0d)
            throw new ArgumentOutOfRangeException(nameof(v));

        double phi = 2.0d * Math.PI * u;
        double z = 2.0d * v - 1.0d;
        double radius = Math.Sqrt(Math.Max(0.0d, 1.0d - z * z));
        return Vector3.Normalize(new Vector3(
            checked((float)(Math.Cos(phi) * radius)),
            checked((float)(Math.Sin(phi) * radius)),
            checked((float)z)));
    }

    private static SimpleDdgiGuidingHierarchyBuildResult UniformFallback(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        SimpleDdgiGuidingHierarchyBuildFailure failure)
    {
        int count = configuration.HierarchyWeightCount;
        double[] values = new double[count];
        double leafWeight = 1.0d / configuration.LeafCount;
        for (int leaf = 0; leaf < configuration.LeafCount; leaf++)
            values[leaf] = leafWeight;
        BuildParentSums(configuration, values);
        values[configuration.GetNodeIndex(1, 0, 0)] = 1.0d;

        Half[] quantized = new Half[count];
        for (int index = 0; index < values.Length; index++)
            quantized[index] = (Half)values[index];
        return new SimpleDdgiGuidingHierarchyBuildResult(
            new SimpleDdgiGuidingQuantizedHierarchy(configuration, quantized),
            UsedUniformFallback: true,
            failure,
            TotalIncidentEnergy: 0.0f,
            TotalIncidentEnergyClamped: false);
    }

    private static void BuildParentSums(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        Span<double> hierarchy)
    {
        for (int parentSide = configuration.LeafResolution >> 1;
             parentSide >= 1;
             parentSide >>= 1)
        {
            int childSide = parentSide << 1;
            for (int y = 0; y < parentSide; y++)
            for (int x = 0; x < parentSide; x++)
            {
                int parent = configuration.GetNodeIndex(parentSide, x, y);
                int childX = x << 1;
                int childY = y << 1;
                hierarchy[parent] = hierarchy[configuration.GetNodeIndex(childSide,
                    childX, childY)] +
                    hierarchy[configuration.GetNodeIndex(childSide,
                        childX + 1, childY)] +
                    hierarchy[configuration.GetNodeIndex(childSide,
                        childX, childY + 1)] +
                    hierarchy[configuration.GetNodeIndex(childSide,
                        childX + 1, childY + 1)];
            }
        }
    }

    private double EvaluateLeafProbabilityUnchecked(int leafX, int leafY)
    {
        double probability = 1.0d;
        for (int parentSide = 1;
             parentSide < Configuration.LeafResolution;
             parentSide <<= 1)
        {
            int childSide = parentSide << 1;
            int childX = leafX / (Configuration.LeafResolution / childSide);
            int childY = leafY / (Configuration.LeafResolution / childSide);
            int parentX = childX >> 1;
            int parentY = childY >> 1;
            double childSum = SumChildren(childSide, parentX, parentY);
            if (childSum <= 0.0d)
            {
                probability *= 0.25d;
            }
            else
            {
                probability *= WeightAt(Configuration.GetNodeIndex(
                    childSide, childX, childY)) / childSum;
            }
        }
        return probability;
    }

    private double SumChildren(int childSide, int parentX, int parentY)
    {
        int childX = parentX << 1;
        int childY = parentY << 1;
        return WeightAt(Configuration.GetNodeIndex(childSide, childX, childY)) +
            WeightAt(Configuration.GetNodeIndex(childSide, childX + 1, childY)) +
            WeightAt(Configuration.GetNodeIndex(childSide, childX, childY + 1)) +
            WeightAt(Configuration.GetNodeIndex(childSide, childX + 1, childY + 1));
    }

    private void ReadChildWeights(int childSide, int parentX, int parentY,
        Span<double> destination)
    {
        int childX = parentX << 1;
        int childY = parentY << 1;
        destination[0] = WeightAt(Configuration.GetNodeIndex(childSide, childX, childY));
        destination[1] = WeightAt(Configuration.GetNodeIndex(childSide, childX + 1, childY));
        destination[2] = WeightAt(Configuration.GetNodeIndex(childSide, childX, childY + 1));
        destination[3] = WeightAt(Configuration.GetNodeIndex(childSide, childX + 1, childY + 1));
    }

    private double WeightAt(int index) => (float)_weights[index];

    private void EnsureValid()
    {
        SimpleDdgiGuidingHierarchyValidation validation = Validate();
        if (!validation.IsValid)
            throw new InvalidOperationException(
                $"Cannot sample an invalid guiding hierarchy: {validation.Reason}.");
    }

    private static SimpleDdgiGuidingHierarchyValidation Invalid(
        SimpleDdgiGuidingHierarchyValidationFailure failure,
        string reason) => new(false, failure, reason);

    private static void ValidateUnitOpen(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0d || value >= 1.0d)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static double ClampUnitOpen(double value)
    {
        if (value <= 0.0d)
            return 0.0d;
        return value >= 1.0d ? Math.BitDecrement(1.0d) : value;
    }
}

/// <summary>
/// Double-precision estimator/reference helpers shared by C3 CPU tests and
/// future shader-conformance vectors.
/// </summary>
public static class SimpleDdgiGuidingReference
{
    public const double UniformSpherePdf = 1.0d / (4.0d * Math.PI);
    public const double MinimumUniformFraction = 0.10d;

    /// <summary>
    /// Resolves the effective mixture alpha. Callers must record this value (or
    /// the resulting PDF) with a ray; silently re-clamping a later setting is
    /// not cache-safe.
    /// </summary>
    public static double ResolveUniformFraction(double requestedUniformFraction)
    {
        if (!double.IsFinite(requestedUniformFraction))
            throw new ArgumentOutOfRangeException(nameof(requestedUniformFraction));
        return Math.Clamp(requestedUniformFraction, MinimumUniformFraction, 1.0d);
    }

    public static double EvaluateMixturePdf(
        double guidedPdf,
        double requestedUniformFraction)
    {
        ValidateNonNegativeFinite(guidedPdf, nameof(guidedPdf));
        double alpha = ResolveUniformFraction(requestedUniformFraction);
        double result = alpha * UniformSpherePdf + (1.0d - alpha) * guidedPdf;
        if (!double.IsFinite(result) || result <= 0.0d)
            throw new ArgumentOutOfRangeException(nameof(guidedPdf));
        return result;
    }

    public static SimpleDdgiDirectionMixtureBranch SelectMixtureBranch(
        double branchVariate,
        double requestedUniformFraction)
    {
        if (!double.IsFinite(branchVariate) || branchVariate < 0.0d ||
            branchVariate >= 1.0d)
        {
            throw new ArgumentOutOfRangeException(nameof(branchVariate));
        }
        return branchVariate < ResolveUniformFraction(requestedUniformFraction)
            ? SimpleDdgiDirectionMixtureBranch.Uniform
            : SimpleDdgiDirectionMixtureBranch.Guided;
    }

    /// <summary>
    /// Validates the cross-fields that make an identity independently
    /// reconstructible. The check uses its recorded leaf/intra-leaf bits and
    /// packed direction only; it intentionally never reads a current guide or
    /// recomputes the generation-time PDF.
    /// </summary>
    public static SimpleDdgiDirectionIdentityValidation ValidateIdentity(
        in SimpleDdgiDirectionSampleIdentity identity,
        SimpleDdgiGuidingDistributionConfiguration configuration)
    {
        configuration.Validate();
        SimpleDdgiDirectionIdentityValidation basic = identity.Validate(
            configuration.LeafCount);
        if (!basic.IsValid)
            return basic;

        (double u, double v) =
            SimpleDdgiDirectionSampleIdentity.UnpackIntraLeafSample(
                identity.IntraLeafSampleBits);
        Vector3 expected = SimpleDdgiGuidingQuantizedHierarchy.DirectionFromLeaf(
            configuration,
            checked((int)identity.LeafIndex),
            u,
            v);
        Vector3 actual = identity.DecodePackedDirection();
        // Both sides have independent 16-bit encodings. This leaves generous
        // headroom for octahedral quantization while still catching a leaf,
        // intra-leaf, or direction ABI mismatch decisively.
        if (Vector3.Dot(expected, actual) < 0.99999f)
        {
            return new SimpleDdgiDirectionIdentityValidation(
                false,
                SimpleDdgiDirectionIdentityValidationFailure
                    .DirectionDoesNotMatchLeafSample,
                "packed-direction-does-not-match-leaf-and-intra-leaf-sample");
        }
        return SimpleDdgiDirectionIdentityValidation.Valid;
    }

    public static double CalculateBalanceWeight(
        int thisTechniqueSampleCount,
        double thisTechniquePdf,
        int otherTechniqueSampleCount,
        double otherTechniquePdf)
    {
        ValidateTechniqueCount(thisTechniqueSampleCount, nameof(thisTechniqueSampleCount));
        ValidateTechniqueCount(otherTechniqueSampleCount, nameof(otherTechniqueSampleCount));
        ValidateNonNegativeFinite(thisTechniquePdf, nameof(thisTechniquePdf));
        ValidateNonNegativeFinite(otherTechniquePdf, nameof(otherTechniquePdf));

        double numerator = thisTechniqueSampleCount * thisTechniquePdf;
        double denominator = numerator + otherTechniqueSampleCount * otherTechniquePdf;
        if (!double.IsFinite(denominator))
            throw new ArgumentOutOfRangeException(nameof(thisTechniquePdf));
        return denominator > 0.0d ? numerator / denominator : 0.0d;
    }

    /// <summary>
    /// One-sample balance-heuristic contribution. A sample from either
    /// radiometric technique contributes F/(nU*pUniform + nM*pMix), not F over
    /// its branch PDF. An absent technique contributes zero explicitly.
    /// </summary>
    public static double EvaluateMultiTechniqueContribution(
        double integrand,
        int uniformMaintenanceSampleCount,
        int mixtureSampleCount,
        SimpleDdgiDirectionSamplingTechnique technique,
        double guidedPdf,
        double requestedUniformFraction)
    {
        if (!double.IsFinite(integrand))
            throw new ArgumentOutOfRangeException(nameof(integrand));
        ValidateTechniqueCount(uniformMaintenanceSampleCount,
            nameof(uniformMaintenanceSampleCount));
        ValidateTechniqueCount(mixtureSampleCount, nameof(mixtureSampleCount));
        if (technique is not SimpleDdgiDirectionSamplingTechnique.UniformMaintenance and
            not SimpleDdgiDirectionSamplingTechnique.Mixture)
        {
            throw new ArgumentOutOfRangeException(nameof(technique));
        }

        int thisTechniqueCount = technique ==
            SimpleDdgiDirectionSamplingTechnique.UniformMaintenance
            ? uniformMaintenanceSampleCount
            : mixtureSampleCount;
        if (thisTechniqueCount == 0)
            return 0.0d;

        double mixturePdf = EvaluateMixturePdf(guidedPdf,
            requestedUniformFraction);
        double denominator = uniformMaintenanceSampleCount * UniformSpherePdf +
            mixtureSampleCount * mixturePdf;
        if (!double.IsFinite(denominator) || denominator <= 0.0d)
            throw new ArgumentOutOfRangeException(nameof(guidedPdf));
        return integrand / denominator;
    }

    /// <summary>One-sample estimator for a mixture-only transport set.</summary>
    public static double EvaluateMixtureContribution(
        double integrand,
        double guidedPdf,
        double requestedUniformFraction)
    {
        if (!double.IsFinite(integrand))
            throw new ArgumentOutOfRangeException(nameof(integrand));
        return integrand / EvaluateMixturePdf(guidedPdf,
            requestedUniformFraction);
    }

    private static void ValidateTechniqueCount(int value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateNonNegativeFinite(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0d)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}

[Flags]
public enum SimpleDdgiGuidingDistributionFlags : uint
{
    None = 0,
    UniformFallback = 1u << 0,
    ValidationReference = 1u << 1
}

/// <summary>
/// Logical GPU header. The fixed 32-byte size includes five uint fields, a
/// float energy scale, flags, and one reserved uint for future ABI growth.
/// </summary>
public readonly record struct SimpleDdgiGuidingDistributionHeader(
    uint VirtualProbeId,
    uint PageGeneration,
    uint DistributionGeneration,
    uint DirectionProposalEpoch,
    uint SampleCountAndAge,
    float TotalIncidentEnergy,
    SimpleDdgiGuidingDistributionFlags Flags)
{
    public const ulong ByteSize = 32UL;
}

/// <summary>One immutable bank as seen by a C3 reader or publication validator.</summary>
public sealed record SimpleDdgiGuidingDistributionBank(
    SimpleDdgiGuidingDistributionHeader Header,
    SimpleDdgiGuidingQuantizedHierarchy Hierarchy);

public enum SimpleDdgiGuidingDoubleBufferValidationFailure : byte
{
    None = 0,
    InvalidBankIndex = 1,
    MissingBank = 2,
    VirtualProbeMismatch = 3,
    PageGenerationMismatch = 4,
    DistributionGenerationMissing = 5,
    ProposalEpochMissing = 6,
    InvalidEnergyScale = 7,
    ConfigurationMismatch = 8,
    InvalidHierarchy = 9,
    CandidateGenerationNotNewer = 10,
    CandidateProposalEpochOlder = 11
}

public readonly record struct SimpleDdgiGuidingDoubleBufferValidation(
    bool IsValid,
    SimpleDdgiGuidingDoubleBufferValidationFailure Failure,
    string Reason)
{
    public static SimpleDdgiGuidingDoubleBufferValidation Valid { get; } =
        new(true, SimpleDdgiGuidingDoubleBufferValidationFailure.None, "valid");
}

/// <summary>
/// A pending two-bank publication. Sampling reads <see cref="SamplingBank"/>,
/// while training/build writes the other bank. This type is a validator only;
/// it cannot accidentally publish a partially built hierarchy.
/// </summary>
public readonly record struct SimpleDdgiGuidingDoubleBuffer(
    int SamplingBankIndex,
    SimpleDdgiGuidingDistributionBank SamplingBank,
    int BuildBankIndex,
    SimpleDdgiGuidingDistributionBank BuildBank)
{
    public SimpleDdgiGuidingDoubleBufferValidation ValidatePublication(
        SimpleDdgiGuidingDistributionConfiguration configuration,
        uint expectedVirtualProbeId,
        uint expectedPageGeneration)
    {
        configuration.Validate();
        if (SamplingBankIndex is < 0 or > 1 || BuildBankIndex is < 0 or > 1 ||
            BuildBankIndex == SamplingBankIndex)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.InvalidBankIndex,
                "sampling-and-build-banks-must-be-distinct-binary-indices");
        }

        SimpleDdgiGuidingDoubleBufferValidation sampled = ValidateBank(
            SamplingBank,
            configuration,
            expectedVirtualProbeId,
            expectedPageGeneration);
        if (!sampled.IsValid)
            return sampled;

        SimpleDdgiGuidingDoubleBufferValidation building = ValidateBank(
            BuildBank,
            configuration,
            expectedVirtualProbeId,
            expectedPageGeneration);
        if (!building.IsValid)
            return building;

        if (!IsStrictlyNewerGeneration(
                BuildBank.Header.DistributionGeneration,
                SamplingBank.Header.DistributionGeneration))
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.CandidateGenerationNotNewer,
                "build-bank-distribution-generation-is-not-newer");
        }
        if (!IsNewerOrEqualGeneration(
                BuildBank.Header.DirectionProposalEpoch,
                SamplingBank.Header.DirectionProposalEpoch))
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.CandidateProposalEpochOlder,
                "build-bank-proposal-epoch-is-older-than-sampled-bank");
        }

        return SimpleDdgiGuidingDoubleBufferValidation.Valid;
    }

    /// <summary>
    /// Serial-number comparison valid across a uint generation wrap. Generation
    /// zero remains reserved and is rejected by bank validation.
    /// </summary>
    public static bool IsStrictlyNewerGeneration(uint candidate, uint previous)
    {
        uint distance = unchecked(candidate - previous);
        return distance != 0u && distance < 0x8000_0000u;
    }

    private static bool IsNewerOrEqualGeneration(uint candidate, uint previous) =>
        candidate == previous || IsStrictlyNewerGeneration(candidate, previous);

    private static SimpleDdgiGuidingDoubleBufferValidation ValidateBank(
        SimpleDdgiGuidingDistributionBank? bank,
        SimpleDdgiGuidingDistributionConfiguration configuration,
        uint expectedVirtualProbeId,
        uint expectedPageGeneration)
    {
        if (bank is null || bank.Hierarchy is null)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.MissingBank,
                "guiding-bank-or-hierarchy-is-missing");
        }
        if (bank.Header.VirtualProbeId != expectedVirtualProbeId)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.VirtualProbeMismatch,
                "virtual-probe-id-does-not-match-physical-slot-owner");
        }
        if (bank.Header.PageGeneration != expectedPageGeneration)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.PageGenerationMismatch,
                "page-generation-does-not-match-physical-slot-owner");
        }
        if (bank.Header.DistributionGeneration == 0u)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.DistributionGenerationMissing,
                "distribution-generation-missing");
        }
        if (bank.Header.DirectionProposalEpoch == 0u)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.ProposalEpochMissing,
                "direction-proposal-epoch-missing");
        }
        if (!float.IsFinite(bank.Header.TotalIncidentEnergy) ||
            bank.Header.TotalIncidentEnergy < 0.0f)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.InvalidEnergyScale,
                "total-incident-energy-must-be-finite-and-nonnegative");
        }
        if (bank.Hierarchy.Configuration != configuration)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.ConfigurationMismatch,
                "bank-hierarchy-configuration-does-not-match-layout");
        }

        SimpleDdgiGuidingHierarchyValidation hierarchy = bank.Hierarchy.Validate();
        if (!hierarchy.IsValid)
        {
            return Invalid(
                SimpleDdgiGuidingDoubleBufferValidationFailure.InvalidHierarchy,
                hierarchy.Reason);
        }
        return SimpleDdgiGuidingDoubleBufferValidation.Valid;
    }

    private static SimpleDdgiGuidingDoubleBufferValidation Invalid(
        SimpleDdgiGuidingDoubleBufferValidationFailure failure,
        string reason) => new(false, failure, reason);
}
