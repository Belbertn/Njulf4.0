using System;

namespace Njulf.Rendering.GpuScene;

public readonly record struct GpuObjectId(int Index, uint Generation)
{
    public static GpuObjectId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
    public override string ToString() => IsValid ? $"GpuObjectId({Index}:{Generation})" : "GpuObjectId.Invalid";
}

public readonly record struct GpuInstanceId(int Index, uint Generation)
{
    public static GpuInstanceId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
    public override string ToString() => IsValid ? $"GpuInstanceId({Index}:{Generation})" : "GpuInstanceId.Invalid";
}

public readonly record struct GpuMaterialId(int Index, uint Generation)
{
    public static GpuMaterialId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
}

public readonly record struct GpuMeshId(int Index, uint Generation)
{
    public static GpuMeshId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
}

public readonly record struct GpuLightId(int Index, uint Generation)
{
    public static GpuLightId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
}

public readonly record struct GpuDecalId(int Index, uint Generation)
{
    public static GpuDecalId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
}

public readonly record struct GpuParticleEmitterId(int Index, uint Generation)
{
    public static GpuParticleEmitterId Invalid { get; } = new(-1, 0);
    public bool IsValid => Index >= 0 && Generation > 0;
}

[Flags]
public enum GpuSceneObjectFlags : uint
{
    None = 0,
    Static = 1u << 0,
    Skinned = 1u << 1,
    Transparent = 1u << 2,
    AlphaTested = 1u << 3,
    Decal = 1u << 4,
    Foliage = 1u << 5,
    ImpostorCapable = 1u << 6,
    CastsShadows = 1u << 7,
    ReceivesShadows = 1u << 8,
    Visible = 1u << 9,
    TeleportHistoryReset = 1u << 10
}

[Flags]
public enum GpuSceneDirtyFlags : uint
{
    None = 0,
    Object = 1u << 0,
    Instance = 1u << 1,
    Transform = 1u << 2,
    PreviousTransform = 1u << 3,
    Material = 1u << 4,
    Mesh = 1u << 5,
    Bounds = 1u << 6,
    Visibility = 1u << 7,
    Association = 1u << 8
}
