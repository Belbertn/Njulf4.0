namespace Njulf.Core.Enums
{
    /// <summary>Selects a memory placement and CPU access policy.</summary>
    public enum MemoryUsage
    {
        GPUOnly,
        CPUToGPU,
        GPUToCPU,
        CPUOnly,
        Automatic
    }
}
