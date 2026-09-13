using Njulf.Core.Math;
using static Njulf.Physics.PhysicsMath;

namespace Njulf.Physics;

/// <summary>Y-up capsule movement settings. Distances use scene units; slope angles use degrees.</summary>
public sealed record CharacterControllerSettings
{
    /// <summary>Positive capsule radius, default 0.35 scene units.</summary>
    public float Radius { get; init; } = .35f;
    /// <summary>Straight capsule segment; total height is Length + 2 * Radius.</summary>
    public float Length { get; init; } = 1.1f;
    /// <summary>Collision separation margin, default 0.02 scene units.</summary>
    public float SkinWidth { get; init; } = .02f;
    /// <summary>Maximum walkable slope in degrees, default 45.</summary>
    public float MaxSlopeAngle { get; init; } = 45;
    /// <summary>Maximum step-up distance, default 0.3 scene units.</summary>
    public float StepHeight { get; init; } = .3f;
    /// <summary>Downward ground attachment distance, default 0.1 scene units.</summary>
    public float GroundSnapDistance { get; init; } = .1f;
    /// <summary>Downward acceleration magnitude, default 9.81 units/second squared.</summary>
    public float Gravity { get; init; } = 9.81f;
    /// <summary>Initial upward jump speed, default 5 units/second.</summary>
    public float JumpSpeed { get; init; } = 5;
    public uint Layer { get; init; } = CollisionLayers.Default;
    public uint Mask { get; init; } = CollisionLayers.All;
}

/// <summary>Y-up kinematic capsule. Move once per fixed update, after platform targets and before PhysicsScene.Step.</summary>
/// <remarks>Position is the capsule center. Owns its collider; disposing the scene also invalidates the controller.</remarks>
public sealed class CharacterController : IDisposable
{
    private const int MaxIterations = 6;
    private readonly PhysicsScene _world;
    private readonly float _walkableY;
    private float _verticalSpeed;
    private Vector3 _airVelocity, _supportLocalPoint;
    private PhysicsPose _supportPose;
    private long _supportTeleportVersion;
    private bool _disposed;

    /// <summary>Settings captured at construction.</summary>
    public CharacterControllerSettings Settings { get; }
    /// <summary>Borrowed capsule registration, valid until controller or world disposal.</summary>
    public ColliderHandle Collider { get; }
    /// <summary>Current world-space capsule-center pose.</summary>
    public PhysicsPose Pose => _world.GetPose(Collider);
    /// <summary>Resulting velocity from the last move in scene units per second.</summary>
    public Vector3 Velocity { get; private set; }
    /// <summary>Whether the last move found walkable support.</summary>
    public bool IsGrounded { get; private set; }
    /// <summary>Last walkable support normal in world space.</summary>
    public Vector3 GroundNormal { get; private set; }
    /// <summary>Current support registration, or a default handle when unsupported.</summary>
    public ColliderHandle SupportingCollider { get; private set; }
    /// <summary>True when bounded overlap recovery could not find a clear pose; that move is stopped.</summary>
    public bool RecoveryFailed { get; private set; }

    public CharacterController(PhysicsScene world, Guid ownerId, Vector3 position, CharacterControllerSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(world); Finite(position);
        if (world.Mode != PhysicsMode.Simulation) throw new ArgumentException("Characters require Simulation mode.", nameof(world));
        Settings = settings ?? new();
        Positive(Settings.Radius); Positive(Settings.SkinWidth);
        Nonnegative(Settings.Length); Nonnegative(Settings.StepHeight); Nonnegative(Settings.GroundSnapDistance);
        Nonnegative(Settings.Gravity); Nonnegative(Settings.JumpSpeed);
        if (!float.IsFinite(Settings.MaxSlopeAngle) || Settings.MaxSlopeAngle < 0 || Settings.MaxSlopeAngle >= 90)
            throw new ArgumentOutOfRangeException(nameof(settings), "Slope angle must be in [0, 90) degrees.");
        if (Settings.SkinWidth >= Settings.Radius) throw new ArgumentException("Skin width must be smaller than the radius.", nameof(settings));
        _walkableY = MathF.Cos(Settings.MaxSlopeAngle * MathF.PI / 180);
        _world = world;
        Collider = world.Register(ownerId, [ColliderShape.Capsule(Settings.Radius, Settings.Length)], new PhysicsPose(position),
            new BodySettings { Kind = BodyKind.Kinematic, AffectedByGravity = false, Friction = 0 }, Settings.Layer, Settings.Mask);
    }

    /// <summary>Desired X/Z velocity in world units/second; Y is ignored. Jump is consumed only while grounded.</summary>
    public void Move(Vector3 desiredVelocity, bool jump, float seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); Finite(desiredVelocity); Positive(seconds);
        Vector3 start = Pose.Position, position = start;
        Vector3 supportVelocity = default;
        if (SupportingCollider != default)
        {
            if (_world.TryGetSupport(SupportingCollider, out var supportPose, out long version) && version == _supportTeleportVersion)
            {
                Vector3 carry = _supportLocalPoint * supportPose.ToMatrix() - _supportLocalPoint * _supportPose.ToMatrix();
                supportVelocity = carry / seconds;
                Slide(ref position, carry, false, SupportingCollider);
            }
            else ClearGround(); // Teleports and invalid supports must never launch or carry the character.
        }

        RecoveryFailed = !Recover(ref position);
        if (RecoveryFailed)
        {
            Velocity = _airVelocity = default; _verticalSpeed = 0; ClearGround();
            _world.SetKinematicTarget(Collider, new(start)); return;
        }

        bool hadGround = IsGrounded;
        if (_verticalSpeed + _airVelocity.Y <= 0 && ProbeGround(position, out var initialGround))
        {
            position -= Vector3.UnitY * MathF.Max(0, initialGround.Distance - Settings.SkinWidth);
            SetGround(initialGround); _verticalSpeed = 0; _airVelocity = default;
        }
        else
        {
            ClearGround();
            if (hadGround) _airVelocity = supportVelocity;
        }

        bool groundedAtStart = IsGrounded;
        if (jump && IsGrounded)
        {
            _verticalSpeed = Settings.JumpSpeed; _airVelocity = supportVelocity; ClearGround();
        }
        if (!IsGrounded) _verticalSpeed -= Settings.Gravity * seconds;

        Vector3 horizontal = new(desiredVelocity.X + _airVelocity.X, 0, desiredVelocity.Z + _airVelocity.Z);
        if (IsGrounded) horizontal -= GroundNormal * Vector3.Dot(horizontal, GroundNormal);
        Slide(ref position, horizontal * seconds, IsGrounded);
        Slide(ref position, Vector3.UnitY * ((_verticalSpeed + _airVelocity.Y) * seconds), false);

        if (_verticalSpeed + _airVelocity.Y <= 0 && ProbeGround(position, out var ground))
        {
            position -= Vector3.UnitY * MathF.Max(0, ground.Distance - Settings.SkinWidth);
            SetGround(ground); _verticalSpeed = 0; _airVelocity = default;
        }
        else
        {
            ClearGround();
            if (groundedAtStart && !jump) _airVelocity = supportVelocity;
        }
        Velocity = (position - start) / seconds;
        _world.SetKinematicTarget(Collider, new(position));
    }

    public void Teleport(Vector3 position)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _world.Teleport(Collider, new(position));
        Velocity = _airVelocity = default; _verticalSpeed = 0; RecoveryFailed = false; ClearGround();
    }

    private bool Recover(ref Vector3 position)
    {
        for (int i = 0; i < MaxIterations; i++)
        {
            if (!_world.CharacterPenetration(Collider, position, Settings.Radius, Settings.Length, out var normal, out float depth)) return true;
            position += normal * (depth + Settings.SkinWidth);
        }
        return !_world.CharacterPenetration(Collider, position, Settings.Radius, Settings.Length, out _, out _);
    }

    private bool Sweep(Vector3 position, Vector3 direction, float distance, out RaycastHit hit, ColliderHandle ignore = default) =>
        _world.SweepCharacter(Collider, position, Settings.Radius, Settings.Length, direction, distance, ignore, out hit);

    private void Slide(ref Vector3 position, Vector3 remaining, bool canStep, ColliderHandle ignore = default)
    {
        for (int i = 0; i < MaxIterations && remaining.LengthSquared() > 1e-10f; i++)
        {
            float distance = remaining.Length(); var direction = remaining / distance;
            if (!Sweep(position, direction, distance + Settings.SkinWidth, out var hit, ignore)) { position += remaining; return; }
            float travel = System.Math.Clamp(hit.Distance - Settings.SkinWidth, 0, distance);
            position += direction * travel; remaining -= direction * travel;
            if (hit.Normal.LengthSquared() < .5f) return;
            if (canStep && hit.Normal.Y < _walkableY && TryStep(ref position, remaining)) return;
            var normal = hit.Normal;
            if (normal.Y > 0 && normal.Y < _walkableY && remaining.Y >= 0)
                normal = new Vector3(normal.X, 0, normal.Z).Normalized(); // Steep surfaces cannot turn horizontal input into ascent.
            if (normal.Y < -.01f && remaining.Y > 0)
            { _verticalSpeed = MathF.Min(_verticalSpeed, 0); _airVelocity = new(_airVelocity.X, MathF.Min(_airVelocity.Y, 0), _airVelocity.Z); }
            float into = Vector3.Dot(remaining, normal);
            if (into >= -1e-6f) return;
            remaining -= normal * into;
        }
    }

    private bool TryStep(ref Vector3 position, Vector3 remaining)
    {
        if (Settings.StepHeight <= 0) return false;
        Vector3 forward = new(remaining.X, 0, remaining.Z);
        float distance = forward.Length(); if (distance < 1e-6f) return false;
        if (Sweep(position, Vector3.UnitY, Settings.StepHeight + Settings.SkinWidth, out _)) return false;
        Vector3 raised = position + Vector3.UnitY * Settings.StepHeight;
        if (Sweep(raised, forward / distance, distance + Settings.SkinWidth, out _)) return false;
        raised += forward;
        if (!Sweep(raised, -Vector3.UnitY, Settings.StepHeight + Settings.GroundSnapDistance + Settings.SkinWidth, out var landing)) return false;
        landing = landing with { Normal = _world.SupportSurfaceNormal(landing, raised, Settings.SkinWidth) };
        if (landing.Normal.Y < _walkableY) return false;
        Vector3 candidate = raised - Vector3.UnitY * MathF.Max(0, landing.Distance - Settings.SkinWidth);
        if (candidate.Y <= position.Y + 1e-4f || candidate.Y > position.Y + Settings.StepHeight + 1e-4f) return false;
        position = candidate; return true;
    }

    private bool ProbeGround(Vector3 position, out RaycastHit hit)
    {
        if (!Sweep(position, -Vector3.UnitY, Settings.GroundSnapDistance + Settings.SkinWidth, out hit)) return false;
        hit = hit with { Normal = _world.SupportSurfaceNormal(hit, position, Settings.SkinWidth) };
        return hit.Normal.Y >= _walkableY;
    }

    private void SetGround(RaycastHit hit)
    {
        IsGrounded = true; GroundNormal = hit.Normal; SupportingCollider = hit.Collider;
        _world.TryGetSupport(hit.Collider, out _supportPose, out _supportTeleportVersion);
        _supportLocalPoint = hit.Point * _supportPose.ToMatrix().Invert();
    }

    private void ClearGround() { IsGrounded = false; GroundNormal = default; SupportingCollider = default; }
    private static void Nonnegative(float value)
    {
        if (!float.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_world.IsValid(Collider)) _world.Remove(Collider);
        _disposed = true; ClearGround();
    }
}
