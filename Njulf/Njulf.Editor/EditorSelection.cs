using System;
using Njulf.Rendering.Resources;

namespace Njulf.Editor;

public enum EditorSelectionKind
{
    None,
    Object,
    Light,
    ReflectionProbe,
    FoliagePrototype,
    FoliagePatch,
    ParticleEffect,
    InstanceBatch
}

/// <summary>Stable selection that never relies on renderer packed-array indices.</summary>
public readonly record struct EditorSelection(EditorSelectionKind Kind, Guid Id, LightHandle LightHandle)
{
    public static EditorSelection None { get; } = new(EditorSelectionKind.None, Guid.Empty, default);
    public bool IsEmpty => Kind == EditorSelectionKind.None;
    public static EditorSelection ForEntity(EditorSelectionKind kind, Guid id) => new(kind, id, default);
    public static EditorSelection ForLight(Guid id, LightHandle handle) => new(EditorSelectionKind.Light, id, handle);
}
