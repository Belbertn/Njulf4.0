namespace Njulf.Rendering.Debug
{
    public enum DebugOverlayMode : uint
    {
        None = 0,
        LightTiles = 1,
        DirectionalShadowCascades = 2,
        ReflectionProbeVolumes = 3,
        DecalVolumes = 4,
        ObjectBounds = 5,
        MeshletBounds = 6,
        SelectedObject = 7,
        MaterialInspection = 8,
        PassTimings = 9,
        GpuMemory = 10
    }
}
