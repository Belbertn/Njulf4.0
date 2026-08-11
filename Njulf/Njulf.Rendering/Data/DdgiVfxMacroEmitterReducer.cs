using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;

namespace Njulf.Rendering.Data;

public enum DdgiVfxMacroShape : uint
{
    Sphere = 1,
    Capsule = 2,
    Cone = 3,
    Line = 4,
    Disk = 5,
    BoundedVolume = 6
}

public readonly record struct DdgiVfxMacroEmitter(
    ulong StableSourceId,
    uint Revision,
    DdgiVfxMacroShape Shape,
    Vector3 Center,
    Vector3 PrincipalAxis,
    Vector3 Extents,
    Vector3 IntegratedPower,
    BoundingBox CurrentBounds,
    BoundingBox SweptBounds,
    bool AuthoredPower);

public readonly record struct DdgiVfxMacroReductionResult(
    int SourceCount,
    int EligibleEmitterCount,
    int RejectedTransientCount,
    int OverflowCount,
    int AuthoredPowerCount,
    int AutoPowerCount,
    ulong Revision,
    ulong RefitCount);

/// <summary>
/// Reduces sustained particle and beam definitions into one analytic source per
/// emitter. Integrated authored power is never multiplied by live particle
/// count, so tessellation and capacity changes cannot change the GI mean.
/// The stateful deadband controls hierarchy refits only; published source power
/// is never temporally blurred in the lighting result.
/// </summary>
public sealed class DdgiVfxMacroEmitterReducer
{
    public const int DefaultMaximumSourceCount = 256;
    private const float MinimumAutomaticAdmissionSeconds = 0.1f;
    private const float MaximumIntegratedPower = 1.0e9f;
    private const int EnvelopeSampleCount = 8;

    private readonly int _capacity;
    private readonly Dictionary<ulong, SourceState> _states = new();
    private readonly List<PendingSource> _pending = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _removals = new();
    private ulong _revision;
    private ulong _refitCount;

    public DdgiVfxMacroEmitterReducer(int capacity = DefaultMaximumSourceCount)
    {
        if (capacity <= 0 || capacity > DdgiEmissiveTriangleTable.MaximumAliasEntryCount)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _pending.Capacity = capacity;
        _removals.Capacity = capacity;
    }

    public DdgiVfxMacroReductionResult Reduce(
        Scene scene,
        float deltaSeconds,
        Span<DdgiVfxMacroEmitter> destination)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0.0f)
            deltaSeconds = 0.0f;

        int outputCapacity = Math.Min(destination.Length, _capacity);
        _pending.Clear();
        _seen.Clear();
        int eligibleCount = 0;
        int rejectedTransientCount = 0;
        int authoredPowerCount = 0;
        int autoPowerCount = 0;

        for (int instanceIndex = 0; instanceIndex < scene.ParticleEffects.Count; instanceIndex++)
        {
            ParticleEffectInstance instance = scene.ParticleEffects[instanceIndex];
            if (!instance.Visible || !instance.Playing || instance.Stopped)
                continue;

            IReadOnlyList<ParticleEmitterDefinition> emitters = instance.Effect.Emitters;
            for (int emitterIndex = 0; emitterIndex < emitters.Count; emitterIndex++)
            {
                ParticleEmitterDefinition emitter = emitters[emitterIndex];
                if (!TryCreateParticleSource(
                        instance,
                        emitter,
                        emitterIndex,
                        out PendingSource pending,
                        out bool rejectedTransient))
                {
                    if (rejectedTransient)
                        rejectedTransientCount++;
                    continue;
                }

                eligibleCount++;
                if (pending.AuthoredPower)
                    authoredPowerCount++;
                else
                    autoPowerCount++;
                AdmitOrUpdate(pending, deltaSeconds);
            }

            IReadOnlyList<BeamDefinition> beams = instance.Effect.Beams;
            for (int beamIndex = 0; beamIndex < beams.Count; beamIndex++)
            {
                if (!TryCreateBeamSource(
                        instance,
                        beams[beamIndex],
                        beamIndex,
                        out PendingSource pending))
                {
                    continue;
                }

                eligibleCount++;
                authoredPowerCount++;
                AdmitOrUpdate(pending, deltaSeconds);
            }
        }

        if (_states.Count > _seen.Count)
        {
            _removals.Clear();
            foreach (KeyValuePair<ulong, SourceState> entry in _states)
            {
                if (!_seen.Contains(entry.Key))
                    _removals.Add(entry.Key);
            }
            foreach (ulong stableId in _removals)
            {
                _states.Remove(stableId);
                AdvanceRevision();
            }
        }

        _pending.Sort(static (left, right) =>
        {
            int importance = right.Importance.CompareTo(left.Importance);
            return importance != 0
                ? importance
                : left.StableId.CompareTo(right.StableId);
        });

        int count = Math.Min(outputCapacity, _pending.Count);
        for (int index = 0; index < count; index++)
        {
            PendingSource pending = _pending[index];
            SourceState state = _states[pending.StableId];
            destination[index] = new DdgiVfxMacroEmitter(
                pending.StableId,
                state.Revision,
                pending.Shape,
                pending.Center,
                pending.Axis,
                pending.Extents,
                state.PublishedPower,
                pending.Bounds,
                Union(state.PreviousBounds, pending.Bounds),
                pending.AuthoredPower);
            state.PreviousBounds = pending.Bounds;
            _states[pending.StableId] = state;
        }

        return new DdgiVfxMacroReductionResult(
            count,
            eligibleCount,
            rejectedTransientCount,
            Math.Max(_pending.Count - outputCapacity, 0),
            authoredPowerCount,
            autoPowerCount,
            _revision,
            _refitCount);
    }

    public static GPUDdgiEmissiveSource PackSource(DdgiVfxMacroEmitter source)
    {
        Vector3 axis = SafeNormalize(source.PrincipalAxis, Vector3.UnitY);
        float radius = Math.Max(source.Extents.X, 1e-4f);
        uint flags = (uint)(DdgiEmissiveSourceFlags.MacroEmitter |
                            DdgiEmissiveSourceFlags.SpatialHierarchy) |
                     ((uint)source.Shape << 8);
        uint packedAliasFlags = flags << DdgiEmissiveTriangleTable.FlagsShift;
        return new GPUDdgiEmissiveSource
        {
            Vertex0Area = new Vector4(source.Center, radius),
            Edge1AliasProbability = new Vector4(axis, 1.0f),
            Edge2AliasFlags = new Vector4(
                source.Extents.Y,
                source.Extents.Z,
                0.0f,
                BitConverter.UInt32BitsToSingle(packedAliasFlags)),
            RadianceSelectionProbability = new Vector4(
                source.IntegratedPower.X,
                source.IntegratedPower.Y,
                source.IntegratedPower.Z,
                0.0f)
        };
    }

    public static double MeasureImportance(DdgiVfxMacroEmitter source)
    {
        double luminance =
            0.2126 * Math.Max(source.IntegratedPower.X, 0.0f) +
            0.7152 * Math.Max(source.IntegratedPower.Y, 0.0f) +
            0.0722 * Math.Max(source.IntegratedPower.Z, 0.0f);
        // Triangle importance is radiance * area * sidedness. Dividing radiant
        // power by 2PI places isotropic macro power in the same proposal units.
        return luminance / (2.0 * Math.PI);
    }

    private void AdmitOrUpdate(PendingSource pending, float deltaSeconds)
    {
        _seen.Add(pending.StableId);
        if (!_states.TryGetValue(pending.StableId, out SourceState state))
        {
            state = new SourceState
            {
                PreviousBounds = pending.Bounds,
                CandidateSeconds = pending.ForceAdmission
                    ? MinimumAutomaticAdmissionSeconds
                    : deltaSeconds
            };
        }
        else
        {
            state.CandidateSeconds = Math.Min(
                state.CandidateSeconds + deltaSeconds,
                MinimumAutomaticAdmissionSeconds);
        }

        bool admitted = pending.ForceAdmission ||
                        state.CandidateSeconds >= MinimumAutomaticAdmissionSeconds;
        if (!admitted)
        {
            _states[pending.StableId] = state;
            return;
        }

        bool sourceChanged = false;
        if (!state.Admitted)
        {
            state.Admitted = true;
            state.PublishedPower = pending.Power;
            sourceChanged = true;
        }
        else if (PowerChangedBeyondDeadband(
                     state.PublishedPower,
                     pending.Power,
                     pending.EnergyHysteresis))
        {
            state.PublishedPower = pending.Power;
            sourceChanged = true;
        }

        if (state.Shape != pending.Shape ||
            !state.Center.Equals(pending.Center) ||
            !state.Axis.Equals(pending.Axis) ||
            !state.Extents.Equals(pending.Extents))
        {
            sourceChanged = true;
        }
        state.Shape = pending.Shape;
        state.Center = pending.Center;
        state.Axis = pending.Axis;
        state.Extents = pending.Extents;
        if (sourceChanged)
        {
            if (state.Revision != 0)
                _refitCount++;
            state.Revision = NextSourceRevision(state.Revision);
            AdvanceRevision();
        }

        _states[pending.StableId] = state;
        _pending.Add(pending with
        {
            Power = state.PublishedPower,
            Importance = MeasurePowerImportance(state.PublishedPower)
        });
    }

    private static bool TryCreateParticleSource(
        ParticleEffectInstance instance,
        ParticleEmitterDefinition emitter,
        int emitterIndex,
        out PendingSource pending,
        out bool rejectedTransient)
    {
        pending = default;
        rejectedTransient = false;
        if (emitter.GlobalIlluminationEmission == ParticleGiEmissionMode.Disabled)
            return false;

        bool force = emitter.GlobalIlluminationEmission == ParticleGiEmissionMode.Force;
        float maximumEmissive = SampleMaximum(emitter.EmissiveOverLife);
        bool transient = !emitter.Looping &&
                         emitter.DurationSeconds < 1.0f &&
                         emitter.SpawnRatePerSecond < 2.0f;
        bool sustained = emitter.Looping ||
                         emitter.DurationSeconds >= 1.0f ||
                         emitter.SpawnRatePerSecond >= 2.0f;
        if (!force && (!sustained || transient || maximumEmissive < 1.25f))
        {
            rejectedTransient = transient && maximumEmissive > 0.0f;
            return false;
        }

        bool authoredPower = HasPositiveFinitePower(emitter.GlobalIlluminationPower);
        Vector3 power = authoredPower
            ? SanitizePower(emitter.GlobalIlluminationPower)
            : EstimateParticlePower(emitter);
        if (!HasPositiveFinitePower(power))
            return false;

        DdgiVfxMacroShape shape = ResolveShape(
            emitter.GlobalIlluminationSourceShape,
            emitter.SpawnShape.Kind);
        Vector3 localAxis = emitter.SpawnShape.Kind == ParticleSpawnShapeKind.Line
            ? Vector3.UnitX
            : Vector3.UnitY;
        Vector3 axis = TransformDirection(localAxis, instance.WorldMatrix);
        Vector3 center = new(
            instance.WorldMatrix.M41,
            instance.WorldMatrix.M42,
            instance.WorldMatrix.M43);

        float meanLifetime = Math.Max(
            (emitter.LifetimeSeconds.Sample(0.0f) + emitter.LifetimeSeconds.Sample(1.0f)) * 0.5f,
            0.001f);
        Vector3 meanVelocity = (emitter.InitialVelocityMin + emitter.InitialVelocityMax) * 0.5f;
        if (!emitter.LocalSpace)
            meanVelocity = TransformDirection(meanVelocity, instance.WorldMatrix);
        float meanAge = meanLifetime * 0.5f;
        center += meanVelocity * meanAge + emitter.Acceleration * (0.5f * meanAge * meanAge);

        Vector3 extents = EstimateShapeExtents(emitter, shape, meanLifetime);
        BoundingBox bounds = CreateBounds(center, axis, extents, shape);
        ulong stableId = StableId(instance.Id, emitterIndex, 0x45504d4954544552UL);
        pending = new PendingSource(
            stableId,
            shape,
            center,
            axis,
            extents,
            power,
            bounds,
            authoredPower,
            force,
            Math.Clamp(emitter.GlobalIlluminationEnergyHysteresis, 0.0f, 0.5f),
            MeasurePowerImportance(power));
        return true;
    }

    private static bool TryCreateBeamSource(
        ParticleEffectInstance instance,
        BeamDefinition beam,
        int beamIndex,
        out PendingSource pending)
    {
        pending = default;
        if (beam.GlobalIlluminationEmission == ParticleGiEmissionMode.Disabled ||
            !HasPositiveFinitePower(beam.GlobalIlluminationPower))
        {
            return false;
        }

        Vector3 start = beam.LocalStart * instance.WorldMatrix;
        Vector3 end = beam.LocalEnd * instance.WorldMatrix;
        Vector3 delta = end - start;
        float length = delta.Length();
        Vector3 axis = length > 1e-5f ? delta / length : Vector3.UnitZ;
        Vector3 center = (start + end) * 0.5f;
        float radius = Math.Max(
            Math.Max(beam.Width.Sample(0.0f), beam.Width.Sample(1.0f)) * 0.5f,
            0.001f);
        Vector3 extents = new(radius, length * 0.5f, radius + Math.Max(beam.NoiseAmplitude, 0.0f));
        Vector3 power = SanitizePower(beam.GlobalIlluminationPower);
        BoundingBox bounds = CreateBounds(center, axis, extents, DdgiVfxMacroShape.Line);
        ulong stableId = StableId(instance.Id, beamIndex, 0x4245414d534f5552UL);
        pending = new PendingSource(
            stableId,
            DdgiVfxMacroShape.Line,
            center,
            axis,
            extents,
            power,
            bounds,
            AuthoredPower: true,
            ForceAdmission: beam.GlobalIlluminationEmission == ParticleGiEmissionMode.Force,
            Math.Clamp(beam.GlobalIlluminationEnergyHysteresis, 0.0f, 0.5f),
            MeasurePowerImportance(power));
        return true;
    }

    private static Vector3 EstimateParticlePower(ParticleEmitterDefinition emitter)
    {
        Vector3 accumulated = Vector3.Zero;
        float accumulatedArea = 0.0f;
        for (int sampleIndex = 0; sampleIndex < EnvelopeSampleCount; sampleIndex++)
        {
            float t = (sampleIndex + 0.5f) / EnvelopeSampleCount;
            Color color = emitter.ColorOverLife.Sample(t);
            float emission = Math.Max(emitter.EmissiveOverLife.Sample(t), 0.0f);
            float size = Math.Max(emitter.Size.Sample(t), 0.0f);
            accumulated += new Vector3(color.R, color.G, color.B) * emission;
            accumulatedArea += size * size;
        }

        Vector3 meanRadiance = accumulated / EnvelopeSampleCount;
        float meanArea = Math.Max(accumulatedArea / EnvelopeSampleCount, 1e-6f);
        float meanLifetime = Math.Max(
            (emitter.LifetimeSeconds.Sample(0.0f) + emitter.LifetimeSeconds.Sample(1.0f)) * 0.5f,
            0.0f);
        float expectedParticles = Math.Clamp(
            emitter.SpawnRatePerSecond * meanLifetime + Math.Max(emitter.BurstCount, 0),
            0.0f,
            Math.Max(emitter.MaxParticles, 0));
        return SanitizePower(meanRadiance * (MathF.PI * meanArea * expectedParticles));
    }

    private static Vector3 EstimateShapeExtents(
        ParticleEmitterDefinition emitter,
        DdgiVfxMacroShape shape,
        float lifetime)
    {
        ParticleSpawnShape spawn = emitter.SpawnShape;
        float maximumSpeed = Math.Max(
            emitter.InitialVelocityMin.Length(),
            emitter.InitialVelocityMax.Length());
        float motionRadius = maximumSpeed * lifetime +
                             0.5f * emitter.Acceleration.Length() * lifetime * lifetime;
        float particleRadius = Math.Max(
            emitter.Size.Sample(0.0f),
            emitter.Size.Sample(1.0f)) * 0.5f;
        float padding = motionRadius + particleRadius;
        return shape switch
        {
            DdgiVfxMacroShape.Line => new Vector3(
                Math.Max(particleRadius, 0.001f),
                Math.Max(spawn.Length * 0.5f, 0.001f),
                padding),
            DdgiVfxMacroShape.Capsule => new Vector3(
                Math.Max(spawn.Radius + particleRadius, 0.001f),
                Math.Max(spawn.Length * 0.5f, 0.001f),
                padding),
            DdgiVfxMacroShape.Cone => new Vector3(
                Math.Max(spawn.Radius + MathF.Tan(spawn.AngleRadians) * spawn.Length, 0.001f) + padding,
                Math.Max(spawn.Length * 0.5f, 0.001f),
                padding),
            DdgiVfxMacroShape.Disk => new Vector3(
                Math.Max(spawn.Radius + padding, 0.001f),
                Math.Max(particleRadius, 0.001f),
                padding),
            DdgiVfxMacroShape.BoundedVolume => new Vector3(
                Math.Max(spawn.Extents.X + padding, 0.001f),
                Math.Max(spawn.Extents.Y + padding, 0.001f),
                Math.Max(spawn.Extents.Z + padding, 0.001f)),
            _ => new Vector3(Math.Max(spawn.Radius + padding, 0.001f))
        };
    }

    private static BoundingBox CreateBounds(
        Vector3 center,
        Vector3 axis,
        Vector3 extents,
        DdgiVfxMacroShape shape)
    {
        if (shape is DdgiVfxMacroShape.Line or DdgiVfxMacroShape.Capsule or DdgiVfxMacroShape.Cone)
        {
            Vector3 endpoint = axis * extents.Y;
            Vector3 radius = new(Math.Max(extents.X, extents.Z));
            return new BoundingBox(
                Vector3.Min(center - endpoint, center + endpoint) - radius,
                Vector3.Max(center - endpoint, center + endpoint) + radius);
        }

        Vector3 boundsExtent = shape == DdgiVfxMacroShape.BoundedVolume
            ? extents
            : new Vector3(Math.Max(extents.X, Math.Max(extents.Y, extents.Z)));
        return new BoundingBox(center - boundsExtent, center + boundsExtent);
    }

    private static DdgiVfxMacroShape ResolveShape(
        ParticleGiSourceShape authored,
        ParticleSpawnShapeKind spawn) => authored switch
    {
        ParticleGiSourceShape.Sphere => DdgiVfxMacroShape.Sphere,
        ParticleGiSourceShape.Capsule => DdgiVfxMacroShape.Capsule,
        ParticleGiSourceShape.Cone => DdgiVfxMacroShape.Cone,
        ParticleGiSourceShape.Line => DdgiVfxMacroShape.Line,
        ParticleGiSourceShape.Disk => DdgiVfxMacroShape.Disk,
        ParticleGiSourceShape.BoundedVolume => DdgiVfxMacroShape.BoundedVolume,
        _ => spawn switch
        {
            ParticleSpawnShapeKind.Box => DdgiVfxMacroShape.BoundedVolume,
            ParticleSpawnShapeKind.Cone => DdgiVfxMacroShape.Cone,
            ParticleSpawnShapeKind.Ring => DdgiVfxMacroShape.Disk,
            ParticleSpawnShapeKind.Line => DdgiVfxMacroShape.Line,
            _ => DdgiVfxMacroShape.Sphere
        }
    };

    private static float SampleMaximum(ParticleCurve curve) => Math.Max(
        curve.Sample(0.0f),
        Math.Max(curve.Sample(0.5f), curve.Sample(1.0f)));

    private static Vector3 TransformDirection(Vector3 direction, Matrix4x4 matrix) =>
        SafeNormalize(new Vector3(
            direction.X * matrix.M11 + direction.Y * matrix.M21 + direction.Z * matrix.M31,
            direction.X * matrix.M12 + direction.Y * matrix.M22 + direction.Z * matrix.M32,
            direction.X * matrix.M13 + direction.Y * matrix.M23 + direction.Z * matrix.M33),
            Vector3.UnitY);

    private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-20f && float.IsFinite(lengthSquared)
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static Vector3 SanitizePower(Vector3 value) => new(
        float.IsFinite(value.X) ? Math.Clamp(value.X, 0.0f, MaximumIntegratedPower) : 0.0f,
        float.IsFinite(value.Y) ? Math.Clamp(value.Y, 0.0f, MaximumIntegratedPower) : 0.0f,
        float.IsFinite(value.Z) ? Math.Clamp(value.Z, 0.0f, MaximumIntegratedPower) : 0.0f);

    private static bool HasPositiveFinitePower(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        (value.X > 0.0f || value.Y > 0.0f || value.Z > 0.0f);

    private static bool PowerChangedBeyondDeadband(
        Vector3 previous,
        Vector3 current,
        float relativeDeadband)
    {
        float scale = Math.Max(
            Math.Max(previous.X, Math.Max(previous.Y, previous.Z)),
            1e-4f);
        float delta = Math.Max(
            MathF.Abs(current.X - previous.X),
            Math.Max(MathF.Abs(current.Y - previous.Y), MathF.Abs(current.Z - previous.Z)));
        return delta > scale * relativeDeadband;
    }

    private static double MeasurePowerImportance(Vector3 power) =>
        (0.2126 * power.X + 0.7152 * power.Y + 0.0722 * power.Z) /
        (2.0 * Math.PI);

    private static ulong StableId(Guid instanceId, int localIndex, ulong domain)
    {
        Span<byte> bytes = stackalloc byte[16];
        instanceId.TryWriteBytes(bytes);
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset ^ domain;
        for (int index = 0; index < bytes.Length; index++)
        {
            hash ^= bytes[index];
            hash *= prime;
        }
        hash ^= checked((uint)localIndex);
        hash *= prime;
        return hash == 0 ? 1UL : hash;
    }

    private static BoundingBox Union(BoundingBox left, BoundingBox right) => new(
        Vector3.Min(left.Min, right.Min),
        Vector3.Max(left.Max, right.Max));

    private static uint NextSourceRevision(uint revision) =>
        revision == uint.MaxValue ? 1u : revision + 1u;

    private void AdvanceRevision()
    {
        _revision = _revision == ulong.MaxValue ? 1UL : _revision + 1UL;
    }

    private readonly record struct PendingSource(
        ulong StableId,
        DdgiVfxMacroShape Shape,
        Vector3 Center,
        Vector3 Axis,
        Vector3 Extents,
        Vector3 Power,
        BoundingBox Bounds,
        bool AuthoredPower,
        bool ForceAdmission,
        float EnergyHysteresis,
        double Importance);

    private struct SourceState
    {
        public bool Admitted;
        public float CandidateSeconds;
        public Vector3 PublishedPower;
        public BoundingBox PreviousBounds;
        public uint Revision;
        public DdgiVfxMacroShape Shape;
        public Vector3 Center;
        public Vector3 Axis;
        public Vector3 Extents;
    }
}
