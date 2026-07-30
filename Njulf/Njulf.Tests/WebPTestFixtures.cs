namespace Njulf.Tests;

internal static class WebPTestFixtures
{
    internal const string LosslessBase64 =
        "UklGRkQAAABXRUJQVlA4TDcAAAAvAkAAAC9AkG2zod/sZtMgyLYp0vytTnAyaZtWwpzO/9P5DzTySLZ5B4VsI8Cp3UNwahfR/xgvAA==";

    internal const string LossyBase64 =
        "UklGRkQAAABXRUJQVlA4IDgAAADQAQCdASoDAAIAAgA0JagCdAEO+Kf2AAD89kf/0vRT7OXmtUhh8pJ8mAGev+h+XLygQf8/6DwAAA==";

    internal const string AlphaBase64 =
        "UklGRk4AAABXRUJQVlA4TEEAAAAvAkAAEC9AkG1TndncX+YaBNk2m8P8ye5ygmybzWv+IBc5kQAJt2ja/H/IADg/L021bUIh2whw6F6gU7iI/sfwAgA=";

    internal static byte[] Lossless => Convert.FromBase64String(LosslessBase64);
    internal static byte[] Lossy => Convert.FromBase64String(LossyBase64);
    internal static byte[] Alpha => Convert.FromBase64String(AlphaBase64);

    internal static readonly byte[] LosslessPixels =
    [
        255, 0, 0, 255,
        0, 255, 0, 255,
        0, 0, 255, 255,
        255, 255, 255, 255,
        32, 64, 128, 255,
        240, 128, 16, 255
    ];

    internal static readonly byte[] LossySourcePixels =
    [
        20, 30, 40, 255,
        90, 100, 110, 255,
        180, 190, 200, 255,
        250, 220, 170, 255,
        40, 160, 220, 255,
        220, 60, 100, 255
    ];

    internal static readonly byte[] AlphaPixels =
    [
        255, 0, 0, 255,
        0, 255, 0, 128,
        0, 0, 255, 0,
        255, 255, 255, 64,
        20, 40, 60, 200,
        240, 120, 30, 1
    ];
}
