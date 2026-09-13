using System.Buffers.Binary;
using Njulf.Audio;
using Njulf.Audio.Assets;
using Njulf.Assets;
using Njulf.Core;
using Njulf.Core.Math;
using Njulf.Physics;
using NUnit.Framework;
using Silk.NET.OpenAL;

namespace Njulf.Tests;

[TestFixture, NonParallelizable]
public sealed class AudioTests
{
    [Test]
    public void ContentSharesClipsAcrossScopesAndRetainsFailedUnloadUntilSourcesRelease()
    {
        string directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "audio-content", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "tone.wav"), Wave());
            using var output = new Loopback();
            using var content = new ContentManager(directory);
            content.RegisterAudio(output.Audio);
            using var first = content.CreateScope();
            using var second = content.CreateScope();
            var clip = first.Load<AudioClip>("tone.wav");
            Assert.That(second.Load<AudioClip>("./tone.wav"), Is.SameAs(clip));
            Assert.Throws<InvalidOperationException>(() => content.RegisterAudio(output.Audio));
            first.Dispose();
            using var local = output.Audio.CreateScope();
            var source = local.CreateSource(clip);
            Assert.Throws<AggregateException>(second.Dispose);
            local.Dispose();
            second.Dispose();
            Assert.Throws<ObjectDisposedException>(() => output.Audio.CreateSource(clip));
            Assert.Throws<ObjectDisposedException>(source.Play);
            Assert.That(content.Load<AudioClip>("tone.wav"), Is.Not.SameAs(clip));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Test]
    public void GroupsMultiplyGainMuteAndFadeWithoutChangingSourceGain()
    {
        using var output = new Loopback();
        var audio = output.Audio;
        using var input = new MemoryStream(Wave());
        using var clip = audio.LoadWav(input);
        using var sound = audio.CreateSource(clip, audio.SFX, spatial: false);
        sound.Looping = true; sound.Gain = .8f; sound.Play();
        double baseline = Energy(output.SettleAndRender(), 0);
        audio.SFX.Volume = .5f;
        Assert.That(Energy(output.SettleAndRender(), 0) / baseline, Is.EqualTo(.25).Within(.015));
        audio.SFX.Muted = true;
        Assert.That(Energy(output.SettleAndRender(), 0), Is.LessThan(1e-9));
        Assert.That(sound.State, Is.EqualTo(AudioPlaybackState.Playing));
        audio.SFX.Muted = false; audio.SFX.Pause();
        audio.SFX.FadeTo(0, 1); audio.Update(.5f);
        Assert.That(audio.SFX.Volume, Is.EqualTo(.25f).Within(1e-6));
        audio.SFX.FadeTo(1, .5f); audio.Update(.25f);
        Assert.That(audio.SFX.Volume, Is.EqualTo(.625f).Within(1e-6));
        audio.Update(.25f); audio.SFX.Resume();
        Assert.That(Energy(output.SettleAndRender(), 0) / baseline, Is.EqualTo(1).Within(.02));
        audio.SFX.FadeTo(0, 0);
        Assert.That(audio.SFX.Volume, Is.Zero);
        sound.Group = audio.UI;
        Assert.That(Energy(output.SettleAndRender(), 0) / baseline, Is.EqualTo(1).Within(.02));
        Assert.That(sound.Gain, Is.EqualTo(.8f));
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.Music.FadeTo(1, float.NaN));
    }

    [Test]
    public void GroupScopeAndExplicitPausesRemainIndependentAndStoppedRequestsStayStopped()
    {
        using var output = new Loopback(oneShotCapacity: 1);
        var audio = output.Audio;
        using var input = new MemoryStream(Wave());
        using var clip = audio.LoadWav(input);
        using var local = audio.CreateScope();
        var sfx = local.CreateSource(clip);
        var music = local.CreateSource(clip, audio.Music, spatial: false);
        sfx.Looping = music.Looping = true;
        sfx.Play(); music.Play();
        Assert.That(music.State, Is.EqualTo(AudioPlaybackState.Paused), "Inactive scopes suspend even Music.");
        local.SetActive(true, true);
        Assert.That(music.State, Is.EqualTo(AudioPlaybackState.Playing));
        audio.SFX.Pause(); local.SetActive(true, false);
        Assert.That(sfx.State, Is.EqualTo(AudioPlaybackState.Paused));
        audio.SFX.Resume();
        Assert.That(sfx.State, Is.EqualTo(AudioPlaybackState.Playing));
        audio.SFX.Pause(); sfx.Pause(); audio.SFX.Resume();
        Assert.That(sfx.State, Is.EqualTo(AudioPlaybackState.Paused));
        sfx.Resume(); local.SetActive(true, true); audio.SFX.Pause(); audio.SFX.Resume();
        Assert.That(sfx.State, Is.EqualTo(AudioPlaybackState.Paused));
        sfx.Restart(); sfx.Stop(); local.SetActive(true, false); sfx.Resume();
        Assert.That(sfx.State, Is.EqualTo(AudioPlaybackState.Stopped));
        audio.UI.Pause();
        Assert.That(local.PlayOneShot(clip, audio.UI, Vector3.Zero), Is.True);
        audio.Update(.1f);
        Assert.That(local.PlayOneShot(clip, audio.UI, Vector3.Zero), Is.False, "Deferred voices must remain reserved.");
        audio.UI.Resume(); output.Render(6000); local.Update(new(new(TimeSpan.Zero, TimeSpan.Zero), false, Vector3.Zero, Vector3.Forward, Vector3.Up));
        Assert.That(local.PlayOneShot(clip, Vector3.Zero), Is.True);
        audio.Music.PausePolicy = AudioPausePolicy.FollowScope;
        local.SetActive(true, true);
        Assert.That(music.State, Is.EqualTo(AudioPlaybackState.Paused));
        Assert.That(sfx.Pitch, Is.EqualTo(1));
    }

    [Test]
    public void ResumeRetainsSamplePositionWhileRestartRewindsEvenDuringSuspension()
    {
        using var output = new Loopback();
        var audio = output.Audio;
        byte[] wave = Wave();
        // A rising ramp distinguishes positions without depending on private native handles.
        for (int i = 0; i < 4800; i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(54 + i * 2), (short)(i * 5));
        using var input = new MemoryStream(wave);
        using var clip = audio.LoadWav(input);
        using var source = audio.CreateSource(clip, spatial: false);
        source.Resume(); Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Initial));
        source.Play(); output.Render(2000);
        source.Pause(); output.Render(500); source.Resume();
        double resumed = Energy(output.Render(500), 0);
        source.Restart();
        double restarted = Energy(output.Render(500), 0);
        Assert.That(resumed, Is.GreaterThan(restarted * 5));
        output.Render(2000); audio.SFX.Pause(); source.Restart();
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Paused));
        output.Render(500); audio.SFX.Resume();
        Assert.That(Energy(output.Render(500), 0), Is.LessThan(resumed / 5));
    }

    [Test]
    public void WorldBindingsUpdateAfterMovementDuringPauseAndUnbindCleanly()
    {
        using var output = new Loopback();
        var audio = output.Audio;
        using var input = new MemoryStream(Wave());
        using var clip = audio.LoadWav(input);
        Vector3 emitter = new(-3, 0, -2);
        using var source = audio.CreateSource(clip, audio.SFX, position: () => emitter);
        var host = new AudioHostModule(audio) { ListenerProvider = () => new(Vector3.Zero, Vector3.Forward, Vector3.Up) };
        var frame = new GameModuleFrame(new(TimeSpan.Zero, TimeSpan.Zero) { UnscaledElapsedGameTime = TimeSpan.FromSeconds(.1) }, true,
            new(100, 0, 0), Vector3.Backward, Vector3.Up);
        source.Looping = true; source.Play(); host.Update(frame);
        var left = output.SettleAndRender();
        Assert.That(Energy(left, 0), Is.GreaterThan(Energy(left, 1) * 2));
        audio.SFX.Pause(); emitter = new(3, 0, -2); host.Update(frame);
        Assert.That(source.Position, Is.EqualTo(emitter));
        audio.SFX.Resume();
        var right = output.SettleAndRender();
        Assert.That(Energy(right, 1), Is.GreaterThan(Energy(right, 0) * 2));
        host.ListenerProvider = () => new(Vector3.Zero, Vector3.Backward, Vector3.Up);
        host.Update(frame);
        var rotated = output.SettleAndRender();
        Assert.That(Energy(rotated, 0), Is.GreaterThan(Energy(rotated, 1) * 2));
        source.PositionProvider = null; emitter = Vector3.Zero; host.Update(frame);
        Assert.That(source.Position, Is.EqualTo(new Vector3(3, 0, -2)));
        source.Dispose(); Assert.That((object?)source.PositionProvider, Is.Null);
        host.Dispose(); Assert.That((object?)host.ListenerProvider, Is.Null);
    }

    [Test]
    public void LocalAudioWaitsForActivationResumesOnlyHostPausedVoicesAndPreservesMusic()
    {
        using var output = new Loopback(oneShotCapacity: 1);
        var audio = output.Audio;
        using var input = new MemoryStream(Wave());
        using var musicClip = audio.LoadWav(input);
        using var music = audio.CreateSource(musicClip, spatial: false);
        music.Looping = true;
        music.Play();
        using var local = audio.CreateScope();
        using var localInput = new MemoryStream(Wave());
        var clip = local.LoadWav(localInput);
        var position = Vector3.One;
        var source = local.CreateSource(clip, position: () => position);
        source.Looping = true;
        source.Play();
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Paused), "Preparing levels must not play.");
        local.SetActive(true, false);
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Playing));
        var frame = new GameModuleFrame(new GameTime(TimeSpan.Zero, TimeSpan.Zero)
            { UnscaledElapsedGameTime = TimeSpan.FromSeconds(.1) }, true, Vector3.Zero, Vector3.Forward, Vector3.Up);
        local.Update(frame);
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Paused));
        Assert.That(music.State, Is.EqualTo(AudioPlaybackState.Playing));
        source.Pause(); // An explicit pause during host suspension must survive host resume.
        var stopped = local.CreateSource(clip);
        stopped.Play(); stopped.Stop();
        Assert.That(local.PlayOneShot(clip, Vector3.Zero), Is.True);
        position = new(3, 4, 5);
        local.Update(frame);
        Assert.That(source.Position, Is.EqualTo(position));
        Assert.That(local.PlayOneShot(clip, Vector3.Zero), Is.False, "Suspended one-shots must not be reclaimed.");
        local.Update(frame with { IsSimulationPaused = false });
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Paused));
        Assert.That(stopped.State, Is.Not.EqualTo(AudioPlaybackState.Playing));
        source.Play();
        local.Update(frame);
        local.Update(frame with { IsSimulationPaused = false });
        Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Playing));
        local.Dispose(); local.Dispose();
        Assert.Throws<ObjectDisposedException>(source.Play);
        Assert.That(music.State, Is.EqualTo(AudioPlaybackState.Playing));
        Assert.That(Energy(output.SettleAndRender(), 0), Is.GreaterThan(0));
    }

    [TestCase(1, 8)]
    [TestCase(1, 16)]
    [TestCase(2, 8)]
    [TestCase(2, 16)]
    public void WaveReadsPcmAndSkipsPaddedUnknownChunks(int channels, int bits)
    {
        byte[] file = Wave(channels, bits);
        var wave = WaveData.Read(file);
        Assert.That((wave.Channels, wave.BitsPerSample, wave.SampleRate), Is.EqualTo((channels, bits, 48000)));
        Assert.That(wave.Samples, Is.EqualTo(file.AsSpan(54).ToArray()));
        Assert.Throws<InvalidDataException>(() => WaveData.Read(file.AsSpan(0, file.Length - 1)));
        byte[] invalid = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(30), 3); // IEEE float, not PCM.
        Assert.Throws<NotSupportedException>(() => WaveData.Read(invalid));
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(invalid.AsSpan(42), 0); // Invalid block alignment.
        Assert.Throws<InvalidDataException>(() => WaveData.Read(invalid));
        Assert.Throws<InvalidDataException>(() => WaveData.Read("not a wave"u8));
    }

    [Test]
    public void LoopbackRendersSpatialAttenuationAndPhysicsDrivenLowpass()
    {
        using var output = new Loopback();
        AudioSystem audio = output.Audio;
        Assert.That(audio.SupportsEfx, Is.True, "Bundled OpenAL Soft must provide EFX.");
        audio.OcclusionSmoothingSeconds = 0;
        using var input = new MemoryStream(Wave());
        using var clip = audio.LoadWav(input);
        using var source = audio.CreateSource(clip);
        source.Looping = true; source.Position = new(-4, 0, -2); source.Play();
        float[] left = output.SettleAndRender();
        Assert.That(Energy(left, 0), Is.GreaterThan(Energy(left, 1) * 2), "A source to the listener's left must pan left.");
        source.Position = new(4, 0, -2);
        float[] right = output.SettleAndRender();
        Assert.That(Energy(right, 1), Is.GreaterThan(Energy(right, 0) * 2));
        audio.SetListener(Vector3.Zero, Vector3.Backward, Vector3.Up);
        float[] turned = output.SettleAndRender();
        Assert.That(Energy(turned, 0), Is.GreaterThan(Energy(turned, 1) * 2), "Turning the listener must reverse the pan.");
        audio.SetListener(Vector3.Zero, Vector3.Forward, Vector3.Up);
        source.Position = new(0, 0, -1);
        double near = Energy(output.SettleAndRender(), 0);
        source.Position = new(0, 0, -8);
        Assert.That(Energy(output.SettleAndRender(), 0), Is.LessThan(near / 16));

        using var physics = new PhysicsScene(PhysicsMode.QueryOnly);
        var door = physics.Register(Guid.NewGuid(), [ColliderShape.Box(new(3, 3, .2f))], new PhysicsPose(new(0, 0, -4)), layer: 2);
        // The listener/source colliders are on another layer and must not self-occlude.
        physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(.5f)], new PhysicsPose(Vector3.Zero), layer: 1);
        physics.Register(Guid.NewGuid(), [ColliderShape.Sphere(.5f)], new PhysicsPose(source.Position), layer: 1);
        bool Blocked()
        {
            Vector3 delta = source.Position;
            float distance = delta.Length();
            return distance > .001f && physics.Raycast(Vector3.Zero, delta, distance, out _, new QueryFilter(2));
        }
        float[] clear = output.SettleAndRender();
        Assert.That(Blocked(), Is.True);
        source.Occlusion = Blocked() ? 1 : 0; audio.Update(0);
        float[] blocked = output.SettleAndRender();
        Assert.That(Energy(blocked, 0), Is.LessThan(Energy(clear, 0) * .2));
        Assert.That(Spectrum(blocked, 4000) / Spectrum(blocked, 400),
            Is.LessThan(Spectrum(clear, 4000) / Spectrum(clear, 400) * .4), "EFX must remove high frequencies, not merely turn down the source.");
        physics.SetPose(door, new(new(5, 0, -4)));
        Assert.That(Blocked(), Is.False);
        source.Occlusion = 0; audio.Update(0);
        float[] restored = output.SettleAndRender();
        Assert.That(Energy(restored, 0) / Energy(clear, 0), Is.EqualTo(1).Within(.05));
        Assert.That(Spectrum(restored, 4000) / Spectrum(clear, 4000), Is.EqualTo(1).Within(.05));
        physics.SetPose(door, new(new(0, 0, -10)));
        Assert.That(Blocked(), Is.False, "A wall beyond the source is not an obstruction.");
        source.Position = Vector3.Zero;
        Assert.That(Blocked(), Is.False, "Coincident endpoints must not submit a zero-length direction.");
    }

    [Test]
    public void LoopbackVolumeFallbackAndSourceLifetime()
    {
        using var output = new Loopback(enableEfx: false);
        AudioSystem audio = output.Audio;
        Assert.That(audio.SupportsEfx, Is.False);
        using var input = new MemoryStream(Wave());
        var clip = audio.LoadWav(input);
        var source = audio.CreateSource(clip);
        source.Looping = true; source.Gain = .5f; source.Position = Vector3.Forward;
        source.Play();
        float[] clear = output.SettleAndRender();
        source.Stop(); source.Occlusion = 1; source.Play();
        Assert.That(source.SmoothedOcclusion, Is.EqualTo(1), "Initial playback must start obstructed, without a clear burst.");
        float[] blocked = output.SettleAndRender();
        Assert.That(Energy(blocked, 0) / Energy(clear, 0), Is.EqualTo(.35 * .35).Within(.01));
        Assert.That(Spectrum(blocked, 4000) / Spectrum(blocked, 400),
            Is.EqualTo(Spectrum(clear, 4000) / Spectrum(clear, 400)).Within(.01), "Fallback changes gain without filtering.");
        audio.MasterGain = .5f;
        Assert.That(Energy(output.SettleAndRender(), 0) / Energy(blocked, 0), Is.EqualTo(.25).Within(.01));
        source.Pause(); Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Paused));
        Assert.That(Energy(output.SettleAndRender(), 0), Is.LessThan(1e-10));
        source.Play(); Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Playing));
        source.Stop(); Assert.That(source.State, Is.EqualTo(AudioPlaybackState.Stopped));
        Assert.Throws<InvalidOperationException>(() => clip.Dispose());
        Assert.Throws<InvalidOperationException>(() => Task.Run(() => audio.Update(0)).GetAwaiter().GetResult());
        source.Dispose(); source.Dispose(); clip.Dispose(); clip.Dispose();
        Assert.Throws<ObjectDisposedException>(() => source.Play());

        using var stereoInput = new MemoryStream(Wave(2));
        var stereo = audio.LoadWav(stereoInput);
        Assert.Throws<ArgumentException>(() => audio.CreateSource(stereo));
        var nonSpatial = audio.CreateSource(stereo, spatial: false);
        nonSpatial.Looping = true; nonSpatial.Play();
        float[] centered = output.SettleAndRender();
        nonSpatial.Position = new(1000, 0, 1000); nonSpatial.Occlusion = 1;
        audio.SetListener(new(500, 500, 500), Vector3.Backward, Vector3.Up); audio.Update(1);
        float[] moved = output.SettleAndRender();
        Assert.That(Energy(moved, 0) / Energy(centered, 0), Is.EqualTo(1).Within(.01));
        audio.Dispose(); audio.Dispose();
        Assert.Throws<ObjectDisposedException>(() => nonSpatial.Play());
        stereo.Dispose(); nonSpatial.Dispose();
    }

    [Test]
    public void OcclusionSmoothingUsesUnscaledElapsedTime()
    {
        using var output = new Loopback();
        using var input = new MemoryStream(Wave());
        using var clip = output.Audio.LoadWav(input);
        using var source = output.Audio.CreateSource(clip);
        source.Occlusion = 1;
        for (int i = 0; i < 10; i++) output.Audio.Update(.01f);
        float smallSteps = source.SmoothedOcclusion;
        source.Occlusion = 0; source.Play(); source.Stop();
        source.Occlusion = 1; output.Audio.Update(.1f);
        Assert.That(source.SmoothedOcclusion, Is.EqualTo(smallSteps).Within(1e-5));
        Assert.That(smallSteps, Is.InRange(.632f, .633f));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Occlusion = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => output.Audio.Update(-1));
    }

    [Test]
    public void OneShotsDropNewestReuseVoicesAndReleaseCompletedClips()
    {
        using var output = new Loopback(oneShotCapacity: 1);
        var audio = output.Audio;
        using var firstData = new MemoryStream(Wave());
        using var secondData = new MemoryStream(Wave());
        var first = audio.LoadWav(firstData); var second = audio.LoadWav(secondData);
        Assert.That(audio.PlayOneShot(first, new(-1, 0, -1)), Is.True);
        Assert.That(audio.PlayOneShot(second, new(1, 0, -1)), Is.False);
        Assert.Throws<InvalidOperationException>(() => first.Dispose());
        float[] left = output.Render(2000);
        Assert.That(Energy(left, 0), Is.GreaterThan(1e-5));
        Assert.That(Energy(left, 0), Is.GreaterThan(Energy(left, 1) * 2));
        output.Render(6000);
        // Reclamation before PlayOneShot works even without an intervening Update.
        Assert.That(audio.PlayOneShot(second, new(1, 0, -1), .25f), Is.True);
        first.Dispose();
        float[] right = output.Render(2000);
        Assert.That(Energy(right, 1), Is.GreaterThan(Energy(right, 0) * 2));
        Assert.That(Energy(right, 1) / Energy(left, 0), Is.EqualTo(.0625).Within(.015));
        output.Render(6000); audio.Update(0); second.Dispose();
        Assert.Throws<ObjectDisposedException>(() => audio.PlayOneShot(second, Vector3.Zero));
        using var finalData = new MemoryStream(Wave());
        var final = audio.LoadWav(finalData);
        Assert.Throws<ArgumentOutOfRangeException>(() => audio.PlayOneShot(final, Vector3.Zero, 2));
        Assert.That(audio.PlayOneShot(final, Vector3.Zero), Is.True);
        audio.Dispose(); final.Dispose();
    }

    private static byte[] Wave(int channels = 1, int bits = 16)
    {
        const int sampleRate = 48000, frames = 4800;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(46 + frames * channels * (bits / 8)); writer.Write("WAVE"u8);
        writer.Write("JUNK"u8); writer.Write(1); writer.Write((byte)42); writer.Write((byte)0);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)channels);
        writer.Write(sampleRate); writer.Write(sampleRate * channels * (bits / 8));
        writer.Write((ushort)(channels * (bits / 8))); writer.Write((ushort)bits);
        writer.Write("data"u8); writer.Write(frames * channels * (bits / 8));
        for (int i = 0; i < frames; i++)
        {
            double sample = .2 * (System.Math.Sin(2 * System.Math.PI * 400 * i / sampleRate) + System.Math.Sin(2 * System.Math.PI * 4000 * i / sampleRate));
            for (int channel = 0; channel < channels; channel++)
                if (bits == 16) writer.Write((short)(sample * short.MaxValue));
                else writer.Write((byte)(128 + sample * 127));
        }
        return stream.ToArray();
    }

    private static double Energy(float[] samples, int channel)
    {
        double sum = 0;
        for (int i = channel; i < samples.Length; i += 2) sum += samples[i] * samples[i];
        return sum / (samples.Length / 2);
    }
    private static double Spectrum(float[] samples, int frequency)
    {
        double real = 0, imaginary = 0;
        for (int i = 0; i < samples.Length / 2; i++)
        {
            double angle = 2 * System.Math.PI * frequency * i / 48000;
            real += samples[i * 2] * System.Math.Cos(angle); imaginary += samples[i * 2] * System.Math.Sin(angle);
        }
        return System.Math.Sqrt(real * real + imaginary * imaginary);
    }

    private sealed unsafe class Loopback : IDisposable
    {
        private readonly Device* _device;
        private readonly delegate* unmanaged[Cdecl]<Device*, void*, int, void> _render;
        public AudioSystem Audio { get; }
        public Loopback(bool enableEfx = true, int oneShotCapacity = 32)
        {
            var alc = ALContext.GetApi(soft: true);
            if (!alc.IsExtensionPresent(null, "ALC_SOFT_loopback"))
            {
                alc.Dispose(); Assert.Ignore("OpenAL Soft loopback is unavailable.");
            }
            var open = (delegate* unmanaged[Cdecl]<byte*, Device*>)alc.GetProcAddress(null, "alcLoopbackOpenDeviceSOFT");
            _device = open(null);
            _render = (delegate* unmanaged[Cdecl]<Device*, void*, int, void>)alc.GetProcAddress(_device, "alcRenderSamplesSOFT");
            // ALC_FORMAT_CHANNELS_SOFT=stereo, ALC_FORMAT_TYPE_SOFT=float, ALC_FREQUENCY=48kHz.
            Audio = new AudioSystem(alc, _device, [0x1990, 0x1501, 0x1991, 0x1406, 0x1007, 48000, 0], enableEfx, oneShotCapacity);
        }
        public float[] Render(int frames)
        {
            var samples = new float[frames * 2];
            fixed (float* data = samples) _render(_device, data, frames);
            Audio.CheckError(); return samples;
        }
        public float[] SettleAndRender()
        {
            var samples = new float[9600];
            fixed (float* data = samples)
            {
                _render(_device, data, samples.Length / 2); // Settle mixer gain ramps and filter history.
                _render(_device, data, samples.Length / 2);
            }
            Audio.CheckError();
            return samples;
        }
        public void Dispose() => Audio.Dispose();
    }
}
