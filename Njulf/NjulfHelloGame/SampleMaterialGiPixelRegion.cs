namespace NjulfHelloGame;

/// <summary>
/// Top-left-origin integer pixel region shared by semantic and approved HDR
/// evidence. Coordinates use the decoded image's logical origin rather than
/// its encoded row order.
/// </summary>
public sealed record SampleMaterialGiPixelRegion(
    int X,
    int Y,
    int Width,
    int Height);
