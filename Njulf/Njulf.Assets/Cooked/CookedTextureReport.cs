using System.Text.Json.Serialization;
using Njulf.Graphics;
using System;
using Njulf.Core.Math;

namespace Njulf.Assets.Cooked;



public sealed record CookedTextureReport(
    string SourceIdentity,
    int OriginalWidth,
    int OriginalHeight,
    int CookedWidth,
    int CookedHeight,
    uint VulkanFormat,
    int MipCount,
    long SourceBytes,
    long CookedBytes,
    bool PassedThrough)
{
    public TextureTransportStatistics TransportStatistics { get; init; } =
        TextureTransportStatistics.Invalid(
            TextureTransportStatisticsStatus.InvalidData,
            "Texture cooker did not publish transport statistics.",
            0,
            TextureSemantic.Data,
            TextureColorSpace.Linear);
    public bool AlphaCoveragePreserved { get; init; }
    public float AlphaCutoff { get; init; }

    /// <summary>
    /// Compatibility projection retained for existing cook-report JSON and V1
    /// callers. New code should consume <see cref="TransportStatistics"/>.
    /// </summary>
    public Njulf.Core.Math.Vector4? LinearAverageColor { get; init; }

    [JsonIgnore]
    internal TextureTransportImage? SourceTransportImage { get; init; }
}
