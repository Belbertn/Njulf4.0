namespace Njulf.Rendering.Debug
{
    public enum DebugOverlayMode : uint
    {
        None = 0,
        LightTiles = 1,
        DirectionalShadowCascades = 2,
        ReflectionProbeVolumes = 3,
        DdgiProbeVolumes = 4,
        DecalVolumes = 5,
        ObjectBounds = 6,
        MeshletBounds = 7,
        SelectedObject = 8,
        MaterialInspection = 9,
        PassTimings = 10,
        GpuMemory = 11,
        DdgiProbeActivity = 12,
        DdgiUpdatedProbes = 13,
        DdgiProbeRelocation = 14
    }
}
