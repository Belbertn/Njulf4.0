using Njulf.Core.Math;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data;

/// <summary>Capture-pixel error policy. It never consumes main-view LOD or visibility.</summary>
internal static class SecondaryViewLodPolicy
{
    internal const float HysteresisFraction = 0.15f;

    internal static int Select(in MeshInfo mesh, Matrix4x4 world, BoundingBox bounds,
        Vector3 camera, Matrix4x4 projection, uint width, uint height, float budget,
        int previous = -1, bool forceFullDetail = false)
    {
        if (forceFullDetail || mesh.IsSkinned || width == 0 || height == 0 ||
            !float.IsFinite(budget) || budget <= 0 || !Finite(bounds.Min) || !Finite(bounds.Max) ||
            !Finite(camera) || bounds.Min.X > bounds.Max.X || bounds.Min.Y > bounds.Max.Y ||
            bounds.Min.Z > bounds.Max.Z)
            return 0;

        float scale = ConservativeScale(world);
        float projectionScale = 0.5f * MathF.Max(width * MathF.Abs(projection.M11),
            height * MathF.Abs(projection.M22));
        if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(projectionScale) || projectionScale <= 0)
            return 0;
        float factor = scale * projectionScale;
        if (projection.M44 == 0 && MathF.Abs(projection.M34) == 1)
        {
            float distance = (camera - bounds.Center).Length() - (bounds.Max - bounds.Min).Length() * 0.5f;
            if (!float.IsFinite(distance) || distance <= 1e-4f) return 0;
            factor /= distance;
        }
        else if (projection.M44 != 1 || projection.M34 != 0)
            return 0;

        if (!float.IsFinite(factor)) return 0;

        float error1 = Error(mesh.MeshletLod1SimplificationError, factor);
        float error2 = Error(mesh.MeshletLod2SimplificationError, factor);
        int selected = ForThreshold(mesh, error1, error2, budget);
        if (previous is >= 0 and <= 2 && selected != previous)
            selected = ForThreshold(mesh, error1, error2,
                budget * (selected > previous ? 1 - HysteresisFraction : 1 + HysteresisFraction));
        return selected;
    }

    // Both bounds dominate the largest singular value, including shear and reflections.
    // The induced-norm bound is exact for diagonal scales; Frobenius limits rotation overestimation.
    internal static float ConservativeScale(Matrix4x4 m)
    {
        float row = MathF.Max(MathF.Abs(m.M11) + MathF.Abs(m.M12) + MathF.Abs(m.M13),
            MathF.Max(MathF.Abs(m.M21) + MathF.Abs(m.M22) + MathF.Abs(m.M23),
                MathF.Abs(m.M31) + MathF.Abs(m.M32) + MathF.Abs(m.M33)));
        float column = MathF.Max(MathF.Abs(m.M11) + MathF.Abs(m.M21) + MathF.Abs(m.M31),
            MathF.Max(MathF.Abs(m.M12) + MathF.Abs(m.M22) + MathF.Abs(m.M32),
                MathF.Abs(m.M13) + MathF.Abs(m.M23) + MathF.Abs(m.M33)));
        float sum = m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13 +
                    m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23 +
                    m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33;
        return MathF.Sqrt(MathF.Min(sum, row * column));
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static float Error(float error, float factor) => float.IsFinite(error) && error >= 0
        ? error * factor
        : float.PositiveInfinity;

    private static int ForThreshold(in MeshInfo mesh, float error1, float error2, float threshold) =>
        mesh.MeshletLod2Count > 0 && error2 <= threshold ? 2 :
        mesh.MeshletLod1Count > 0 && error1 <= threshold ? 1 : 0;
}

internal readonly record struct SecondaryLodInstanceKey(Guid Entity, int Ordinal, MeshHandle Mesh);

internal readonly record struct SecondaryLodHistoryContract(
    bool Probe,
    uint Width,
    uint Height,
    Matrix4x4 Projection,
    bool Enabled,
    float PixelError,
    uint ResourceGeneration,
    ulong CameraCutSerial);

/// <summary>Recoverable failure: the scratch cubemap must be retried, never partly published.</summary>
internal sealed class SecondaryViewLodUnavailableException(string message) : Exception(message);

/// <summary>One reflector/probe's history, independent of GPU frame slots and face scheduling.</summary>
internal sealed class SecondaryViewLodHistory
{
    private readonly Dictionary<SecondaryLodInstanceKey, int> _previous = [];
    private readonly Dictionary<SecondaryLodInstanceKey, Choice> _choices = [];
    private readonly HashSet<SecondaryLodInstanceKey> _seen = [];
    private SecondaryLodHistoryContract _contract;
    private ulong _serial;
    private bool _initialized;
    private bool _snapshotReady;
    internal int TransitionCount { get; private set; }
    internal int Count => _previous.Count;

    internal void Begin(SecondaryLodHistoryContract contract, ulong serial)
    {
        // Ignore perspective subpixel jitter, which is not a projection-density change.
        Matrix4x4 projection = contract.Projection;
        if (projection.M44 == 0)
        {
            projection.M31 = 0;
            projection.M32 = 0;
        }

        contract = contract with { Projection = projection };
        bool changed = !_initialized || contract != _contract;
        if (changed && _initialized && contract.Probe && _snapshotReady && serial == _serial)
            throw new SecondaryViewLodUnavailableException("Probe LOD policy changed during a capture ticket.");
        if (changed) _previous.Clear();
        if (changed || !contract.Probe || serial != _serial)
        {
            _choices.Clear();
            _snapshotReady = false;
        }

        _contract = contract;
        _serial = serial;
        _initialized = true;
        _seen.Clear();
        TransitionCount = 0;
    }

    internal int Select(SecondaryLodInstanceKey key, in MeshInfo mesh, Matrix4x4 world,
        BoundingBox bounds, Vector3 camera, bool forceFullDetail)
    {
        if (!_seen.Add(key)) throw new InvalidOperationException("Duplicate secondary-view instance identity.");
        if (_contract.Probe && _snapshotReady)
        {
            if (!_choices.TryGetValue(key, out Choice frozen))
                throw new SecondaryViewLodUnavailableException("Probe geometry changed during a capture ticket.");
            return frozen.Requested;
        }

        int previous = _previous.GetValueOrDefault(key, -1);
        int requested = SecondaryViewLodPolicy.Select(mesh, world, bounds, camera,
            _contract.Projection, _contract.Width, _contract.Height, _contract.PixelError, previous,
            forceFullDetail || !_contract.Enabled);
        if (previous >= 0 && previous != requested) TransitionCount++;
        _choices[key] = new Choice(requested, -1);
        return requested;
    }

    internal int ResolveRequest(SecondaryLodInstanceKey key) =>
        _contract.Probe && _snapshotReady ? _choices[key].Effective : _choices[key].Requested;

    internal void ObserveEffective(SecondaryLodInstanceKey key, int effective, uint meshletCount)
    {
        Choice choice = _choices[key];
        if (_contract.Probe && (meshletCount == 0 || (_snapshotReady && effective != choice.Effective)))
            throw new SecondaryViewLodUnavailableException("A probe's frozen LOD range is no longer resident.");
        _choices[key] = choice with { Effective = effective };
    }

    internal void SealSnapshot()
    {
        if (_contract.Probe && _seen.Count != _choices.Count)
            throw new SecondaryViewLodUnavailableException(
                "Probe instance membership changed during a capture ticket.");
        _snapshotReady = true;
    }

    internal void Commit()
    {
        _previous.Clear();
        foreach (var (key, choice) in _choices) _previous.Add(key, choice.Requested);
    }

    private readonly record struct Choice(int Requested, int Effective);
}