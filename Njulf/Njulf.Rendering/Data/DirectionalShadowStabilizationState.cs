using System;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Rendering.Data;

public enum DirectionalShadowStabilizationResetReason : uint
{
    None = 0,
    FirstUse = 1,
    LightIdentityChanged = 2,
    ConfigurationChanged = 3,
    InvalidLightDirection = 4,
    DirectionDiscontinuity = 5
}

public readonly record struct DirectionalShadowCascadeFitDiagnostics(
    int CascadeIndex,
    CoreVector3 LightDirection,
    CoreVector3 BasisRight,
    CoreVector3 BasisUp,
    float RawCenterX,
    float RawCenterY,
    float SnappedCenterX,
    float SnappedCenterY,
    float StableDiameter,
    float WorldTexelSize,
    float GuardTexels,
    float RawDepthMinimum,
    float RawDepthMaximum,
    float StableDepthMinimum,
    float StableDepthMaximum,
    DirectionalShadowStabilizationResetReason ResetReason);

/// <summary>
/// Renderer-owned continuity state for directional cascades. It deliberately
/// contains no Vulkan resources and is directly testable.
/// </summary>
public sealed class DirectionalShadowStabilizationState
{
    private const double DirectionEpsilonSquared = 1.0e-20;
    private const double AntiparallelThreshold = -0.999999;
    private const double ComparableDepthDirectionThreshold = 0.999999;
    private const double DepthContractionRate = 0.05;

    private readonly DepthInterval[] _depthIntervals =
        new DepthInterval[ShadowSettings.MaxDirectionalCascades];
    private readonly DirectionalShadowCascadeFitDiagnostics[] _diagnostics =
        new DirectionalShadowCascadeFitDiagnostics[ShadowSettings.MaxDirectionalCascades];
    private bool _initialized;
    private ulong _lightIdentity;
    private ulong _configurationSignature;
    private Double3 _direction;
    private Double3 _up;
    private bool _depthDirectionComparable;

    public ReadOnlySpan<DirectionalShadowCascadeFitDiagnostics> Diagnostics => _diagnostics;

    internal BasisFrame BeginFrame(
        ulong lightIdentity,
        CoreVector3 authoredDirection,
        ulong configurationSignature)
    {
        Double3 requested = Double3.From(authoredDirection);
        bool invalidDirection = !requested.IsFinite ||
            requested.LengthSquared <= DirectionEpsilonSquared;
        if (invalidDirection)
            requested = new Double3(0.0, -1.0, 0.0);
        requested = requested.Normalized();

        DirectionalShadowStabilizationResetReason reset =
            DirectionalShadowStabilizationResetReason.None;
        bool resetState = !_initialized;
        if (!_initialized)
            reset = DirectionalShadowStabilizationResetReason.FirstUse;
        else if (_lightIdentity != lightIdentity)
        {
            resetState = true;
            reset = DirectionalShadowStabilizationResetReason.LightIdentityChanged;
        }
        else if (_configurationSignature != configurationSignature)
        {
            resetState = true;
            reset = DirectionalShadowStabilizationResetReason.ConfigurationChanged;
        }
        else if (invalidDirection)
        {
            resetState = true;
            reset = DirectionalShadowStabilizationResetReason.InvalidLightDirection;
        }

        double directionDot = _initialized
            ? Math.Clamp(Double3.Dot(_direction, requested), -1.0, 1.0)
            : 1.0;
        _depthDirectionComparable = !resetState &&
            directionDot >= ComparableDepthDirectionThreshold;

        if (resetState)
        {
            _direction = requested;
            _up = CreateInitialUp(requested);
            Array.Clear(_depthIntervals, 0, _depthIntervals.Length);
        }
        else if (directionDot <= AntiparallelThreshold)
        {
            // There is no unique shortest rotation at 180 degrees. Keeping the
            // previous tangent is deterministic and remains perpendicular after
            // projection; the authored light itself is discontinuous.
            _direction = requested;
            _up = OrthonormalizeUp(_up, requested);
            Array.Clear(_depthIntervals, 0, _depthIntervals.Length);
            _depthDirectionComparable = false;
            reset = DirectionalShadowStabilizationResetReason.DirectionDiscontinuity;
        }
        else
        {
            Double3 transportedUp = TransportShortestArc(
                _up,
                _direction,
                requested,
                directionDot);
            _direction = requested;
            _up = OrthonormalizeUp(transportedUp, requested);
        }

        _lightIdentity = lightIdentity;
        _configurationSignature = configurationSignature;
        _initialized = true;

        Double3 viewZ = -_direction;
        Double3 right = Double3.Cross(_up, viewZ).Normalized();
        _up = Double3.Cross(viewZ, right).Normalized();
        return new BasisFrame(
            _direction.ToCore(),
            right.ToCore(),
            _up.ToCore(),
            reset);
    }

    internal void StabilizeDepth(
        int cascade,
        double rawMinimum,
        double rawMaximum,
        double depthQuantum,
        out float stableMinimum,
        out float stableMaximum)
    {
        if ((uint)cascade >= ShadowSettings.MaxDirectionalCascades)
            throw new ArgumentOutOfRangeException(nameof(cascade));

        ref DepthInterval interval = ref _depthIntervals[cascade];
        if (!interval.Valid || !_depthDirectionComparable)
        {
            interval.Minimum = rawMinimum;
            interval.Maximum = rawMaximum;
            interval.Valid = true;
        }
        else
        {
            interval.Minimum = rawMinimum < interval.Minimum
                ? rawMinimum
                : Lerp(interval.Minimum, rawMinimum, DepthContractionRate);
            interval.Maximum = rawMaximum > interval.Maximum
                ? rawMaximum
                : Lerp(interval.Maximum, rawMaximum, DepthContractionRate);
        }

        double quantum = Math.Max(depthQuantum, 0.001);
        // Keep the hysteresis accumulator unquantized. Feeding the outward-
        // rounded result back into the next contraction step can pin an
        // interval forever at the same quantum boundary.
        double quantizedMinimum =
            Math.Floor(interval.Minimum / quantum) * quantum;
        double quantizedMaximum =
            Math.Ceiling(interval.Maximum / quantum) * quantum;
        if (quantizedMaximum <= quantizedMinimum)
            quantizedMaximum = quantizedMinimum + quantum;

        stableMinimum = (float)quantizedMinimum;
        stableMaximum = (float)quantizedMaximum;
    }

    internal void RecordDiagnostics(
        int cascade,
        in DirectionalShadowCascadeFitDiagnostics diagnostics) =>
        _diagnostics[cascade] = diagnostics;

    public void Reset()
    {
        _initialized = false;
        _lightIdentity = 0UL;
        _configurationSignature = 0UL;
        _direction = default;
        _up = default;
        _depthDirectionComparable = false;
        Array.Clear(_depthIntervals, 0, _depthIntervals.Length);
        Array.Clear(_diagnostics, 0, _diagnostics.Length);
    }

    private static Double3 CreateInitialUp(Double3 direction)
    {
        Double3 axis = Math.Abs(direction.X) <= Math.Abs(direction.Y) &&
                       Math.Abs(direction.X) <= Math.Abs(direction.Z)
            ? Double3.UnitX
            : (Math.Abs(direction.Y) <= Math.Abs(direction.Z)
                ? Double3.UnitY
                : Double3.UnitZ);
        return OrthonormalizeUp(axis, direction);
    }

    private static Double3 OrthonormalizeUp(Double3 up, Double3 direction)
    {
        Double3 projected = up - direction * Double3.Dot(up, direction);
        if (!projected.IsFinite || projected.LengthSquared <= DirectionEpsilonSquared)
            projected = CreateFallbackProjectedAxis(direction);
        return projected.Normalized();
    }

    private static Double3 CreateFallbackProjectedAxis(Double3 direction)
    {
        Double3 axis = Math.Abs(direction.X) < 0.8
            ? Double3.UnitX
            : (Math.Abs(direction.Y) < 0.8 ? Double3.UnitY : Double3.UnitZ);
        return axis - direction * Double3.Dot(axis, direction);
    }

    private static Double3 TransportShortestArc(
        Double3 value,
        Double3 from,
        Double3 to,
        double directionDot)
    {
        Double3 cross = Double3.Cross(from, to);
        double crossLengthSquared = cross.LengthSquared;
        if (crossLengthSquared <= DirectionEpsilonSquared)
            return value;

        // Rodrigues rotation without acos: R(v) = v + k×v +
        // k×(k×v)/(1+dot), where k = from×to.
        return value + Double3.Cross(cross, value) +
            Double3.Cross(cross, Double3.Cross(cross, value)) /
            (1.0 + directionDot);
    }

    private static double Lerp(double from, double to, double amount) =>
        from + (to - from) * amount;

    internal readonly record struct BasisFrame(
        CoreVector3 Direction,
        CoreVector3 Right,
        CoreVector3 Up,
        DirectionalShadowStabilizationResetReason ResetReason);

    private struct DepthInterval
    {
        public bool Valid;
        public double Minimum;
        public double Maximum;
    }

    private readonly record struct Double3(double X, double Y, double Z)
    {
        public static Double3 UnitX => new(1.0, 0.0, 0.0);
        public static Double3 UnitY => new(0.0, 1.0, 0.0);
        public static Double3 UnitZ => new(0.0, 0.0, 1.0);
        public bool IsFinite =>
            double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;
        public Double3 Normalized()
        {
            double inverseLength = 1.0 / Math.Sqrt(LengthSquared);
            return this * inverseLength;
        }

        public CoreVector3 ToCore() => new((float)X, (float)Y, (float)Z);
        public static Double3 From(CoreVector3 value) => new(value.X, value.Y, value.Z);
        public static double Dot(Double3 left, Double3 right) =>
            left.X * right.X + left.Y * right.Y + left.Z * right.Z;
        public static Double3 Cross(Double3 left, Double3 right) => new(
            left.Y * right.Z - left.Z * right.Y,
            left.Z * right.X - left.X * right.Z,
            left.X * right.Y - left.Y * right.X);
        public static Double3 operator +(Double3 left, Double3 right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Double3 operator -(Double3 left, Double3 right) =>
            new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
        public static Double3 operator -(Double3 value) =>
            new(-value.X, -value.Y, -value.Z);
        public static Double3 operator *(Double3 value, double scalar) =>
            new(value.X * scalar, value.Y * scalar, value.Z * scalar);
        public static Double3 operator /(Double3 value, double scalar) =>
            new(value.X / scalar, value.Y / scalar, value.Z / scalar);
    }
}
