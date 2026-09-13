using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using Njulf.Assets.Tooling;
using Njulf.Audio;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AudioCookerTests
{
    private string _directory = null!;
    private string _ffmpeg = null!;

    [SetUp]
    public void SetUp()
    {
        _ffmpeg = Environment.GetEnvironmentVariable("NJULF_TEST_FFMPEG") ?? "ffmpeg";
        try { RunFFmpeg("-version"); }
        catch (Win32Exception) { Assert.Ignore("Set NJULF_TEST_FFMPEG or install FFmpeg to exercise build-time conversion."); }
        _directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "audio-cooking", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (_directory != null && Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [TestCase(".wav", false, 1, 48000)]
    [TestCase(".wav", true, 2, 96000)]
    [TestCase(".mp3", false, 2, 48000)]
    [TestCase(".ogg", false, 1, 48000)]
    [TestCase(".flac", false, 2, 48000)]
    public void CommonInputsCookToReadablePcmWithPreservedSignalAndSkipUnchanged(string extension, bool floating, int channels, int rate)
    {
        string wave = Path.Combine(_directory, "original.wav");
        WriteTone(wave, floating, channels, rate);
        if (!floating) Assert.That(WaveData.Read(File.ReadAllBytes(wave)).SampleRate, Is.EqualTo(rate));
        string source = wave;
        if (extension != ".wav")
        {
            source = Path.Combine(_directory, "encoded" + extension);
            RunFFmpeg("-nostdin", "-v", "error", "-f", "wav", "-i", wave, source);
        }
        string output = Path.Combine(_directory, "runtime.wav");
        Assert.That(AudioCooker.Cook(source, output, ffmpeg: _ffmpeg), Is.True);
        WaveData pcm = WaveData.Read(File.ReadAllBytes(output));
        Assert.That((pcm.Channels, pcm.BitsPerSample, pcm.SampleRate), Is.EqualTo((channels, 16, rate)));
        Assert.That((double)pcm.Samples.Length / (channels * 2 * rate), Is.EqualTo(.2).Within(.035));
        double energy = 0;
        for (int i = 0; i < pcm.Samples.Length; i += 2)
        {
            double sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.Samples.AsSpan(i)) / 32768.0;
            energy += sample * sample;
        }
        Assert.That(Math.Sqrt(energy / (pcm.Samples.Length / 2)), Is.EqualTo(.25 / Math.Sqrt(2)).Within(.025));
        DateTime timestamp = File.GetLastWriteTimeUtc(output);
        Assert.That(AudioCooker.Cook(source, output, ffmpeg: _ffmpeg), Is.False);
        Assert.That(File.GetLastWriteTimeUtc(output), Is.EqualTo(timestamp));
        Assert.That(AudioCooker.Cook(source, output, mono: true, ffmpeg: _ffmpeg), Is.True);
        Assert.That(WaveData.Read(File.ReadAllBytes(output)).Channels, Is.EqualTo(1));
        // Changed input invalidates the stamp even when conversion settings are unchanged.
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(2));
        Assert.That(AudioCooker.Cook(source, output, mono: true, ffmpeg: _ffmpeg), Is.True);
    }

    [Test]
    public void FailedConversionsPreserveLastGoodOutputAndMultichannelRequiresExplicitMono()
    {
        string source = Path.Combine(_directory, "source.wav"), output = Path.Combine(_directory, "runtime.wav");
        WriteTone(source, false, 3, 48000);
        AudioCooker.Cook(source, output, mono: true, ffmpeg: _ffmpeg);
        byte[] good = File.ReadAllBytes(output);
        Assert.Throws<NotSupportedException>(() => AudioCooker.Cook(source, output, ffmpeg: _ffmpeg));
        Assert.That(File.ReadAllBytes(output), Is.EqualTo(good));
        File.WriteAllText(source, "not audio");
        Assert.Throws<InvalidDataException>(() => AudioCooker.Cook(source, output, ffmpeg: _ffmpeg));
        Assert.That(File.ReadAllBytes(output), Is.EqualTo(good));
        var missing = Assert.Throws<InvalidOperationException>(() => AudioCooker.Cook(source, output, ffmpeg: Path.Combine(_directory, "missing-ffmpeg.exe")));
        Assert.That(missing!.Message, Does.Contain("requires FFmpeg"));
    }

    private static void WriteTone(string path, bool floating, int channels, int rate)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int bytes = floating ? 4 : 2, frames = rate / 5;
        writer.Write("RIFF"u8); writer.Write(36 + frames * channels * bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)(floating ? 3 : 1)); writer.Write((ushort)channels);
        writer.Write(rate); writer.Write(rate * channels * bytes); writer.Write((ushort)(channels * bytes)); writer.Write((ushort)(bytes * 8));
        writer.Write("data"u8); writer.Write(frames * channels * bytes);
        for (int i = 0; i < frames; i++)
            for (int channel = 0; channel < channels; channel++)
            {
                float sample = .25f * MathF.Sin(2 * MathF.PI * 1000 * i / rate);
                if (floating) writer.Write(sample); else writer.Write((short)(sample * 32767));
            }
    }

    private void RunFFmpeg(params string[] arguments)
    {
        var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync(), output = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(error, output);
        Assert.That(process.ExitCode, Is.Zero, error.Result);
    }
}
