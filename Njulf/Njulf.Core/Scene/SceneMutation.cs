using System;
using Njulf.Core.Math;

namespace Njulf.Core.Scene;

/// <summary>
/// Producer-side scene changes that may invalidate spatial consumers such as
/// acceleration structures, shadows, and dynamic global illumination.
/// </summary>
[Flags]
public enum SceneMutationKind : uint
{
    None = 0,
    Added = 1u << 0,
    Removed = 1u << 1,
    Transform = 1u << 2,
    Geometry = 1u << 3,
    Material = 1u << 4,
    Visibility = 1u << 5,
    Animation = 1u << 6,
    ParticleState = 1u << 7,
    StaticInstances = 1u << 8,
    Foliage = 1u << 9,
    Content = 1u << 10,
    Emission = 1u << 11,
    Global = 1u << 31
}

/// <summary>
/// Allocation-free mutation payload published by <see cref="Scene"/>. Bounds
/// are optional because some producers need renderer-owned mesh metadata to
/// resolve them; consumers must conservatively fall back when neither bound is
/// present.
/// </summary>
public readonly record struct SceneMutation(
    ulong Serial,
    Guid ProducerId,
    IIdentifiedSceneEntity Producer,
    SceneMutationKind Kind,
    BoundingBox? OldWorldBounds,
    BoundingBox? NewWorldBounds,
    ulong ContentRevision = 0);

/// <summary>Detailed change notification emitted by a render object.</summary>
public readonly record struct RenderObjectMutation(
    RenderObject Source,
    SceneMutationKind Kind,
    BoundingBox? OldWorldBounds,
    BoundingBox? NewWorldBounds,
    object? OldResource,
    object? NewResource,
    ulong Revision);
