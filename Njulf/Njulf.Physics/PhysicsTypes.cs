using Njulf.Core.Math;

namespace Njulf.Physics;

/// <summary>Selects no physics, queries without a solver, or rigid-body simulation.</summary>
public enum PhysicsMode { Disabled, QueryOnly, Simulation }
/// <summary>Static bodies stay fixed, kinematic bodies follow game targets, and dynamic bodies respond to forces.</summary>
public enum BodyKind { Static, Kinematic, Dynamic }

/// <summary>A registration, including all compound shapes. Handles belong to exactly one scene.</summary>
public readonly record struct ColliderHandle(Guid SceneId, long Id);

/// <summary>World-space position in scene units and rotation quaternion; scale belongs to collider geometry.</summary>
public readonly record struct PhysicsPose(Vector3 Position, Quaternion Rotation)
{
    public static PhysicsPose Identity => new(Vector3.Zero, Quaternion.Identity);
    public PhysicsPose(Vector3 position) : this(position, Quaternion.Identity) { }
    public Matrix4x4 ToMatrix() => Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(Position);
}

/// <summary>Immutable registration settings. Defaults describe a static, gravity-enabled body with mass 1.</summary>
public sealed record BodySettings
{
    /// <summary>Body motion policy, default Static; RegisterDynamic/Static/Kinematic select their named policy.</summary>
    public BodyKind Kind { get; init; } = BodyKind.Static;
    /// <summary>Positive mass in consistent scene mass units; default 1.</summary>
    public float Mass { get; init; } = 1;
    /// <summary>Nonnegative friction coefficient, default 0.5.</summary>
    public float Friction { get; init; } = .5f;
    /// <summary>Bounce coefficient in [0,1], default 0.</summary>
    public float Restitution { get; init; }
    /// <summary>Whether a dynamic body receives world gravity; true by default.</summary>
    public bool AffectedByGravity { get; init; } = true;
    /// <summary>Queryable overlap volume with no solver response. Simulation emits Trigger enter/exit events.</summary>
    public bool IsTrigger { get; init; }
}

/// <summary>Query layer mask and optional owner exclusion. Null filters include every layer.</summary>
public readonly record struct QueryFilter(uint LayerMask, Guid? IgnoreOwner = null)
{
    /// <summary>Whether query results include trigger volumes; true by default.</summary>
    public bool IncludeTriggers { get; init; } = true;
    public static QueryFilter All => new(CollisionLayers.All);
    public static QueryFilter None => new(CollisionLayers.None);
}

/// <summary>Layer masks. Games can assign names with static readonly uint Player = CollisionLayers.Bit(1).</summary>
public static class CollisionLayers
{
    public const uint None = 0;
    public const uint Default = 1;
    public const uint All = uint.MaxValue;
    public static uint Bit(int index)
    {
        if ((uint)index >= 32) throw new ArgumentOutOfRangeException(nameof(index), "Layer indices range from 0 to 31.");
        return 1u << index;
    }
}
/// <summary>Closest query hit with world-space point and distance in scene units. The normal is unit length, or zero for initial overlap.</summary>
public readonly record struct RaycastHit(ColliderHandle Collider, Guid OwnerId, Vector3 Point, Vector3 Normal, float Distance);
/// <summary>An overlapping registration and its application owner identifier.</summary>
public readonly record struct OverlapHit(ColliderHandle Collider, Guid OwnerId);
/// <summary>Written entries are unique registrations in unspecified order. Total may exceed the buffer length.</summary>
public readonly record struct OverlapResult(int Written, int Total)
{
    public bool Overflowed => Total > Written;
}
/// <summary>Begin/end at body-pair granularity, delivered after pose publication. Handles may have since been removed.</summary>
public readonly record struct PhysicsContact(ColliderHandle A, Guid OwnerA, ColliderHandle B, Guid OwnerB, bool Began)
{
    /// <summary>Copied begin-contact data; absent for end events. Safe to retain after either collider is removed.</summary>
    public ContactDetails? Details { get; init; }
}

/// <summary>World-space midpoint and unit normal from A to B; nonnegative pre-solver closing speed in units/second.</summary>
public readonly record struct ContactDetails(Vector3 Position, Vector3 Normal, float ClosingSpeed);

/// <summary>Copied registration-pair transition. At least one collider is a trigger; handles may already be invalid.</summary>
public readonly record struct PhysicsTrigger(ColliderHandle A, Guid OwnerA, ColliderHandle B, Guid OwnerB, bool Entered);
