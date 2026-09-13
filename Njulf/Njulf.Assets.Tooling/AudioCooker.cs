using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Njulf.Assets.Tooling;

/// <summary>Build-only conversion to the small PCM WAV format consumed by Njulf.Audio.</summary>
public static class AudioCooker
{
    private sealed record Stamp(long SourceLength, long SourceTicks, bool Mono, string FFmpeg, int Version = 1);

    /// <summary>Converts the first audio stream. Returns false for an unchanged input/settings/output.
    /// FFmpeg is provided by the build machine, never bundled with the game.</summary>
    public static bool Cook(string source, string output, bool mono = false, string ffmpeg = "ffmpeg")
    {
        source = Path.GetFullPath(source); output = Path.GetFullPath(output);
        if (string.Equals(source, output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Audio output must differ from its source.");
        if (!new[] { ".wav", ".mp3", ".ogg", ".flac" }.Contains(Path.GetExtension(source).ToLowerInvariant()))
            throw new NotSupportedException("Audio inputs must be WAV, MP3, Ogg Vorbis, or FLAC.");
        string inputFormat = Path.GetExtension(source)[1..].ToLowerInvariant();
        if (!Path.GetExtension(output).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Audio output must have a .wav extension.");
        var input = new FileInfo(source);
        if (!input.Exists) throw new FileNotFoundException("Audio input does not exist.", source);
        string stampPath = output + ".cook.json";
        string stamp = JsonSerializer.Serialize(new Stamp(input.Length, input.LastWriteTimeUtc.Ticks, mono, ffmpeg));
        if (File.Exists(output) && File.GetLastWriteTimeUtc(output) >= input.LastWriteTimeUtc &&
            File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string normalized = temporary + ".pcm";
        try
        {
            var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            // Select the declared container: short periodic PCM tones can confuse automatic format probing.
            foreach (string arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", inputFormat, "-i", source,
                         "-map", "0:a:0", "-vn", "-map_metadata", "-1", "-c:a", "pcm_s16le" }) start.ArgumentList.Add(arg);
            if (mono) { start.ArgumentList.Add("-ac"); start.ArgumentList.Add("1"); }
            start.ArgumentList.Add("-f"); start.ArgumentList.Add("wav"); start.ArgumentList.Add(temporary);
            try
            {
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidDataException($"FFmpeg failed converting '{source}': {error.Trim()}");
            }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException("Audio cooking requires FFmpeg on PATH or an explicit --ffmpeg / NjulfFFmpeg path.", error);
            }
            // FFmpeg can emit extensible WAV even for PCM. Write a canonical PCM header
            // so runtime decoding stays unchanged, including for high sample rates.
            NormalizeWave(temporary, normalized);
            File.Move(normalized, output, overwrite: true);
            File.WriteAllText(stampPath, stamp);
            return true;
        }
        finally
        {
            File.Delete(temporary);
            File.Delete(normalized);
        }
    }

    private static void NormalizeWave(string input, string output)
    {
        using var stream = File.OpenRead(input);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (reader.ReadUInt32() != 0x46464952) throw new InvalidDataException("Expected RIFF WAV from FFmpeg.");
        long end = 8L + reader.ReadUInt32();
        if (end > stream.Length || reader.ReadUInt32() != 0x45564157) throw new InvalidDataException("Invalid FFmpeg WAV output.");
        byte[]? format = null;
        long dataOffset = 0;
        uint dataLength = 0;
        while (stream.Position + 8 <= end)
        {
            uint id = reader.ReadUInt32(), length = reader.ReadUInt32();
            long next = stream.Position + length + (length & 1);
            if (next > end) throw new InvalidDataException("Truncated FFmpeg WAV output.");
            if (id == 0x20746d66 && length is >= 16 and <= 64) format = reader.ReadBytes((int)length);
            if (id == 0x61746164) { dataOffset = stream.Position; dataLength = length; }
            stream.Position = next;
        }
        if (format == null || dataOffset == 0 || dataLength == 0) throw new InvalidDataException("FFmpeg produced no audio samples.");
        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        if (tag != 1 && !(tag == 0xfffe && format.Length >= 40 &&
            format.AsSpan(24, 16).SequenceEqual(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray())))
            throw new InvalidDataException("FFmpeg did not produce PCM audio.");
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2));
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4));
        if (channels is not (1 or 2)) throw new NotSupportedException("Audio requires mono/stereo; use --mono to downmix multichannel inputs.");
        if (rate == 0 || rate > int.MaxValue || BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14)) != 16 || dataLength % (channels * 2) != 0)
            throw new InvalidDataException("Invalid PCM16 audio output.");
        using var target = File.Create(output);
        using var writer = new BinaryWriter(target, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(checked(36u + dataLength)); writer.Write("WAVEfmt "u8);
        writer.Write(16u); writer.Write((ushort)1); writer.Write(channels); writer.Write(rate);
        writer.Write(checked(rate * channels * 2)); writer.Write((ushort)(channels * 2)); writer.Write((ushort)16);
        writer.Write("data"u8); writer.Write(dataLength);
        stream.Position = dataOffset;
        byte[] buffer = new byte[81920];
        for (long remaining = dataLength; remaining > 0;)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException();
            target.Write(buffer, 0, read); remaining -= read;
        }
    }
}
