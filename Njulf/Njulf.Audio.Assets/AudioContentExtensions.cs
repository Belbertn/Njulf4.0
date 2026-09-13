using Njulf.Assets;

namespace Njulf.Audio.Assets;

/// <summary>Opt-in audio loading through the ordinary content cache and scope lifetimes.</summary>
public static class AudioContentExtensions
{
    /// <summary>Call once on the audio thread. Cached clips are borrowed; dispose their sources
    /// before releasing content, and keep the audio system alive through content cleanup.</summary>
    public static void RegisterAudio(this IContentManager content, AudioSystem audio)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(audio);
        if (content is not ContentManager manager)
            throw new NotSupportedException("Audio registration requires the root ContentManager.");
        // Validate the device thread even when no clips have been requested yet.
        audio.Check();
        manager.RegisterLoader<AudioClip>(audio.LoadWav);
    }
}
