using K4os.Compression.LZ4;
using ZstdSharp;

namespace Njulf.Assets.Cooked;

internal static class CookedCompressionCodec
{
    public static byte[] Compress(ReadOnlySpan<byte> source, CookedCompression compression) => compression switch
    {
        CookedCompression.None => source.ToArray(),
        CookedCompression.Lz4 => CompressLz4(source),
        CookedCompression.Zstd => CompressZstd(source),
        _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "This codec requires typed meshoptimizer metadata.")
    };

    public static void Decompress(ReadOnlySpan<byte> source, Span<byte> destination, CookedCompression compression)
    {
        int written = compression switch
        {
            CookedCompression.None => Copy(source, destination),
            CookedCompression.Lz4 => LZ4Codec.Decode(source, destination),
            CookedCompression.Zstd => DecompressZstd(source, destination),
            _ => throw new ArgumentOutOfRangeException(nameof(compression), compression, "This codec requires typed meshoptimizer metadata.")
        };
        if (written != destination.Length)
            throw new InvalidDataException($"{compression} decompression produced {written} bytes, expected {destination.Length}.");
    }

    private static byte[] CompressLz4(ReadOnlySpan<byte> source)
    {
        var destination = GC.AllocateUninitializedArray<byte>(LZ4Codec.MaximumOutputSize(source.Length));
        int written = LZ4Codec.Encode(source, destination, LZ4Level.L12_MAX);
        if (written <= 0)
            throw new InvalidOperationException("LZ4 compression failed.");
        Array.Resize(ref destination, written);
        return destination;
    }

    private static byte[] CompressZstd(ReadOnlySpan<byte> source)
    {
        using var compressor = new Compressor(9);
        return compressor.Wrap(source).ToArray();
    }

    private static int DecompressZstd(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(source, destination);
    }

    private static int Copy(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        if (source.Length != destination.Length)
            return -1;
        source.CopyTo(destination);
        return source.Length;
    }
}
