using Njulf.Core.Math;

namespace Njulf.Physics;

public enum PhysicsMode { Disabled, QueryOnly, Simulation }
public enum BodyKind { Static, Kinematic, Dynamic }

/// <summary>A registration, including all compound shapes. Handles belong to exactly one scene.</summary>
public readonly record struct ColliderHandle(Guid SceneId, long Id);

public readonly record struct PhysicsPose(Vector3 Position, Quaternion Rotation)
{
    public static PhysicsPose Identity => new(Vector3.Zero, Quaternion.Identity);
    public PhysicsPose(Vector3 position) : this(position, Quaternion.Identity) { }
    public Matrix4x4 ToMatrix() => Rotation.ToMatrix4x4() * Matrix4x4.CreateTranslation(Position);
}

public sealed record BodySettings
{
    public BodyKind Kind { get; init; } = BodyKind.Static;
    public float Mass { get; init; } = 1;
    public float Friction { get; init; } = .5f;
    public float Restitution { get; init; }
    public bool AffectedByGravity { get; init; } = true;
}

/// <summary>Query layer mask and optional owner exclusion. Null filters include every layer.</summary>
public readonly record struct QueryFilter(uint LayerMask, Guid? IgnoreOwner = null);
public readonly record struct RaycastHit(ColliderHandle Collider, Guid OwnerId, Vector3 Point, Vector3 Normal, float Distance);
public readonly record struct OverlapHit(ColliderHandle Collider, Guid OwnerId);
/// <summary>Written entries are unique registrations in unspecified order. Total may exceed the buffer length.</summary>
public readonly record struct OverlapResult(int Written, int Total)
{
    public bool Overflowed => Total > Written;
}
/// <summary>Begin/end at body-pair granularity, delivered after pose publication. Handles may have since been removed.</summary>
public readonly record struct PhysicsContact(ColliderHandle A, Guid OwnerA, ColliderHandle B, Guid OwnerB, bool Began);
