using System.Buffers.Binary;
using Silk.NET.OpenAL;

namespace Njulf.Audio;

internal sealed record WaveData(byte[] Samples, int Channels, int BitsPerSample, int SampleRate)
{
    public BufferFormat Format => (Channels, BitsPerSample) switch
    {
        (1, 8) => BufferFormat.Mono8,
        (1, 16) => BufferFormat.Mono16,
        (2, 8) => BufferFormat.Stereo8,
        _ => BufferFormat.Stereo16
    };

    public static WaveData Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Expected a RIFF WAVE file.");
        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(file[4..]);
        if (end < 12 || end > file.Length) throw new InvalidDataException("Truncated or invalid RIFF size.");

        ReadOnlySpan<byte> format = default, samples = default;
        bool foundFormat = false, foundData = false;
        for (long offset = 12; offset < end;)
        {
            if (end - offset < 8) throw new InvalidDataException("Truncated WAV chunk header.");
            var header = file.Slice((int)offset, 8);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long next = offset + 8 + size + (size & 1);
            if (next > end) throw new InvalidDataException("Truncated WAV chunk or padding.");
            var data = file.Slice((int)offset + 8, checked((int)size));
            if (header[..4].SequenceEqual("fmt "u8))
            {
                if (foundFormat) throw new InvalidDataException("Duplicate WAV format chunk.");
                format = data; foundFormat = true;
            }
            else if (header[..4].SequenceEqual("data"u8))
            {
                if (foundData) throw new InvalidDataException("Multiple WAV data chunks are unsupported.");
                samples = data; foundData = true;
            }
            offset = next;
        }
        if (!foundFormat || format.Length < 16 || !foundData || samples.IsEmpty)
            throw new InvalidDataException("WAV requires a format chunk and nonempty sample data.");
        int encoding = BinaryPrimitives.ReadUInt16LittleEndian(format);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..]);
        uint byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..]);
        int alignment = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        if (encoding != 1 || channels is not (1 or 2) || bits is not (8 or 16))
            throw new NotSupportedException("Audio supports PCM WAV with 8/16-bit mono or stereo samples.");
        if (rate == 0 || rate > int.MaxValue || alignment != channels * (bits / 8) ||
            byteRate != (long)rate * alignment || samples.Length % alignment != 0)
            throw new InvalidDataException("Invalid WAV sample rate, byte rate, or sample alignment.");
        byte[] pcm = samples.ToArray();
        if (bits == 16 && !BitConverter.IsLittleEndian)
            for (int i = 0; i < pcm.Length; i += 2) (pcm[i], pcm[i + 1]) = (pcm[i + 1], pcm[i]);
        return new(pcm, channels, bits, (int)rate);
    }
}
