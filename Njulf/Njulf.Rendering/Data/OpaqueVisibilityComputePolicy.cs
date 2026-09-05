using System;

namespace Njulf.Rendering.Data;

/// <summary>Experimental exact backend; does not change receiver-cache mode values or presets.</summary>
internal static class OpaqueVisibilityComputePolicy
{
    internal static bool Requested { get; } =
        Environment.GetEnvironmentVariable("NJULF_OPAQUE_VISIBILITY_COMPUTE") == "1";

    internal const uint FamilyCount = 4;
    internal const ulong JobBytes = 16;
    internal const ulong IndexBytes = 4;
    internal const ulong ControlBytes = 128;
    internal const uint IndirectWord = 16;

    internal static ulong PixelCapacity(uint width, uint height) => checked((ulong)width * height);
}
