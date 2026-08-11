namespace Njulf.Core.Vfx
{
    /// <summary>
    /// Controls whether a VFX emitter contributes an analytic macro source to
    /// dynamic GI. AutoSustained excludes brief flashes; Force allows an
    /// explicitly authored transient indirect flash.
    /// </summary>
    public enum ParticleGiEmissionMode
    {
        AutoSustained = 0,
        Disabled = 1,
        Force = 2
    }

    public enum ParticleGiSourceShape
    {
        Auto = 0,
        Sphere = 1,
        Capsule = 2,
        Cone = 3,
        Line = 4,
        Disk = 5,
        BoundedVolume = 6
    }
}
