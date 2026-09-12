namespace Njulf.Audio;

/// <summary>An immutable OpenAL buffer shared by sources from the same audio system.</summary>
public sealed class AudioClip : IDisposable
{
    internal AudioSystem Owner { get; }
    internal uint Buffer { get; private set; }
    internal int Attachments { get; set; }
    public int Channels { get; }
    public int BitsPerSample { get; }
    public int SampleRate { get; }
    public TimeSpan Duration { get; }

    internal unsafe AudioClip(AudioSystem owner, WaveData wave)
    {
        Owner = owner;
        Channels = wave.Channels; BitsPerSample = wave.BitsPerSample; SampleRate = wave.SampleRate;
        Duration = TimeSpan.FromSeconds((double)wave.Samples.Length / (Channels * (BitsPerSample / 8)) / SampleRate);
        try
        {
            Buffer = owner.Al.GenBuffer();
            fixed (byte* data = wave.Samples) owner.Al.BufferData(Buffer, wave.Format, data, wave.Samples.Length, SampleRate);
            owner.CheckError();
        }
        catch { if (Buffer != 0) owner.Al.DeleteBuffer(Buffer); Buffer = 0; throw; }
    }

    internal void Check() { Owner.Check(); ObjectDisposedException.ThrowIf(Buffer == 0, this); }

    /// <summary>Dispose attached sources first, including stopped sources. System disposal handles this order automatically.</summary>
    public void Dispose()
    {
        if (Buffer == 0) return;
        Owner.Check();
        if (Attachments != 0) throw new InvalidOperationException("Dispose all sources using this clip before disposing the clip.");
        Owner.Al.DeleteBuffer(Buffer); Buffer = 0;
        Owner.Remove(this);
    }
}
