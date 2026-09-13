# Optional audio

Reference `Njulf.Audio/Njulf.Audio.csproj` explicitly. It depends on Njulf.Core and
Silk.NET OpenAL 2.23.0, with OpenAL Soft 1.23.1 native runtime assets. The framework
does not initialize audio automatically. Audio has no graphics or physics dependency.

## Playback and ownership

For automatic integration, register `new AudioHostModule(audio)` once during initial loading.
The host owns its disposal, updates the camera listener and calls unscaled audio maintenance
after gameplay/physics. Remove manual per-frame listener/`Update` calls and device disposal.

Create `audio.CreateScope()` and register it with a `GameLevel` for scene-local audio, or with
`Game` for host-lifetime audio that follows pause. Use scope `LoadWav`, `CreateSource` and
`PlayOneShot` to own local clips/voices. An optional `CreateSource(..., position: () => node.Position)`
provider updates each frame after physics, including pause; supply a world-space position.
Direct sources can use `PositionProvider`, or the grouped `CreateSource` overload's
`position` argument. Bindings also run before playback and are released on disposal.
Scopes start inactive, so candidate-level sounds wait for activation. SFX follows
`IsSimulationPaused` by default; Music/UI ignore simulation pause but still wait for scope
activation. Only automatically paused voices resume, and explicit source `Pause`/`Stop` wins.
Play requests made while suspended wait for resume. Each scope has its own bounded one-shot
pool using `AudioSystem.OneShotCapacity`. Disposal releases voices before locally loaded clips.
Clips passed from outside a scope are borrowed and must outlive their local sources.

For persistent music, create clips/sources directly on the host-owned `AudioSystem`.
They survive level unload and ignore simulation pause. Time scaling never changes pitch.
See [managed levels](FrameworkApi.md#managed-levels). The manual API below remains available.

Create one game-owned `AudioSystem` in `Game.Load`. Set up sources there and reuse
them; OpenAL mixes buffered playback independently of the game's frame rate.
All native operations, loading, and disposal run on the creating game thread.

```csharp
// Fields: AudioSystem? _audio; AudioSource? _sound;
// In Load:
_audio = new AudioSystem();
AudioClip clip = _audio.LoadWav(Path.Combine(AppContext.BaseDirectory, "Assets", "Audio", "engine.wav"));
_sound = _audio.CreateSource(clip); // spatial by default; requires mono
_sound.Position = new Vector3(0, 1, -5);
_sound.Looping = true;
_sound.Gain = .7f;
_audio.SetListener(Camera.Position, Camera.Forward, Camera.Up);
// Evaluate any initial occlusion here, before starting playback.
_sound.Play();

// In Update, after gameplay/camera movement:
_audio.SetListener(Camera.Position, Camera.Forward, Camera.Up);
_audio.Update((float)time.UnscaledElapsedGameTime.TotalSeconds);

// In Unload:
_audio?.Dispose();
```

Clips hold immutable native buffers and can be shared by several sources from the
same system. `LoadWav(Stream)` leaves the stream open; neither overload caches files.
PCM WAV supports 8/16-bit mono/stereo, arbitrary chunk order and padded metadata
chunks. Compressed, float, extensible WAV, and multiple data chunks are rejected.
Loading is synchronous and reads the file into memory; use short sound effects.

`Play` retains its original behavior: starts/restarts a source and resumes a paused source.
Prefer `Resume()` to continue a paused voice (other states are unchanged), and `Restart()`
to begin at sample zero. Restart during suspension queues that start. `Pause` retains its
playback position. `Stop` cancels pending playback and retains the clip for replay. `State` reports Initial,
Playing, Paused, or Stopped. Nonlooping sources remain reusable after completion;
dispose them when no longer needed. Native source exhaustion throws an OpenAL error.

Dispose sources before their clips, including stopped sources. Disposing an attached
clip throws. Disposing the audio system releases every remaining source, filter,
and clip before closing its context/device. Disposal is idempotent. On scene changes,
explicitly stop/dispose that scene's sources; the audio module has no scene subscriptions.

`new AudioSystem()` throws `InvalidOperationException` if initialization fails.
Games that allow silent operation can catch that exception and leave audio disabled;
the example does this except during validation. `SupportsEfx == false` means
occlusion will reduce volume without applying a low-pass filter.

## One-call sound effects

```csharp
var audio = new AudioSystem(oneShotCapacity: 32); // Default pool limit.
bool played = audio.PlayOneShot(impactClip, impactPosition, volume: .7f);
// Keep calling audio.Update(unscaledSeconds) as usual.
```

One-shots are spatial, nonlooping mono clips from the same audio system, with volume in [0,1].
The system lazily creates at most `OneShotCapacity` pooled sources and reuses them across clips.
If all voices are busy, the new sound is dropped and `PlayOneShot` returns false; existing
sounds continue. Invalid input and native OpenAL failures still throw. Ordinary `CreateSource`
voices are separate from this pool limit.

Completed voices are reclaimed during `Update`, before the next one-shot, and when disposing
a clip. Idle pooled sources detach their clips, allowing disposal; a clip still playing or
attached to an ordinary source cannot be disposed. System disposal stops and releases the pool.
Reused voices reset to pitch 1, no occlusion, reference distance 1, maximum distance 100, and
rolloff 1. The pool is game-owned with the audio system; it is not automatically cleared on
scene changes. The audio example uses F to play a one-shot at the camera.

## Spatial audio and controls

Positions use scene units with +Y up and -Z forward. Pass the camera's position,
forward, and up for a camera-bound listener. Any other listener transform is also
valid. Forward/up are normalized and orthogonalized; degenerate orientations throw.

Spatial sources use inverse-distance-clamped attenuation: reference distance 1,
maximum distance 100, and rolloff 1 by default. Attenuation starts at reference
distance and stops decreasing at maximum distance; maximum distance is **not** a
hard audible cutoff. Keep distances in the same units as your scene.

Use `CreateSource(clip, spatial: false)` for centered UI/background audio. Mono and
stereo are accepted; source position, distance attenuation, and occlusion are ignored.
`MasterGain` and source `Gain` accept [0, 1]; `Pitch` must be finite and positive.
Doppler is disabled. Time scale never changes pitch; change `Pitch` explicitly if desired.
Registered scopes apply the simulation-pause policy described below, while directly owned
sources remain independent of simulation pause.

## Groups, pause, and fades

Every source belongs to `audio.SFX` by default. Select `audio.Music` or `audio.UI`
with `CreateSource(clip, audio.Music, spatial: false)`, the corresponding scope overload,
or by assigning `source.Group`. Groups belong to one audio system. Grouped one-shots use
`PlayOneShot(clip, audio.SFX, position, volume)`; these retain the existing mono/spatial behavior.

```csharp
audio.Music.Volume = .6f;
audio.SFX.Muted = true;           // Silence without pausing or changing Volume.
audio.UI.Pause();
audio.UI.Resume();
audio.Music.FadeTo(0, 1.5f);     // Linear fade, in unscaled seconds.
audio.Music.PausePolicy = AudioPausePolicy.FollowScope;
```

Volume is in [0,1] and multiplies source gain, master gain, occlusion, and attenuation.
Fades continue during pause/mute; a new fade replaces the previous one. Setting Volume
cancels a fade, and a zero-duration fade applies immediately. Duration must be finite and nonnegative.

Group pause, explicit source pause, and scope suspension are independent: clearing one
never overrides another. Play/resume/restart requests made while suspended wait until all
applicable suspension reasons clear. Stop cancels that request. SFX defaults to `FollowScope`;
Music/UI default to `IgnoreSimulationPause`. Neither policy allows an inactive scope to play.
All groups default to volume 1, unmuted and unpaused. Audio maintenance uses unscaled time.

`AudioHostModule.ListenerProvider` optionally supplies an `AudioListenerPose` with world-space
position/forward/up; null follows the camera. It is sampled after physics, including while paused.
Set a binding to null to restore manual emitter positioning or the camera listener.

## Optional content integration

Reference `Njulf.Audio.Assets` and call `Content.RegisterAudio(audio)` once during loading
on the audio thread. This registers a synchronous `AudioClip` loader with the root
`ContentManager`; the audio project itself still has no asset/graphics dependency.

```csharp
using Njulf.Audio.Assets;

Content.RegisterAudio(audio);
// During a managed level's loader:
var local = level.RegisterModule(audio.CreateScope());
AudioClip clip = level.Content.Load<AudioClip>("Audio/hit.wav");
var sound = local.CreateSource(clip, audio.SFX, position: () => emitter.Position);
sound.Play();                    // Waits for level activation.
```

Paths are relative to the content root (absolute paths also work). Equivalent normalized
paths share one immutable clip across content scopes. Returned clips are borrowed: release
sources before their content lifetime, and keep the audio device alive through cleanup.
Managed levels already release modules before content. For manually managed lifetimes,
dispose audio scopes before content scopes; an attached clip prevents final content unload
and ownership is retained so cleanup can be retried. See the audio example for host-owned
content cleanup. Registering the type twice throws. Registered loaders support synchronous
`Load` only; `LoadAsync`/preload do not move OpenAL work onto worker threads.

## Ray-based occlusion

Gameplay owns queries. Reference `Njulf.Physics` when using its `PhysicsScene`;
`QueryOnly` is sufficient. Register walls/doors on an explicit sound-blocker layer,
excluding listener/emitter colliders. Visual meshes do not block sound unless you
register corresponding collision geometry. This simple model detects obstruction,
not diffraction around corners or transmission through different materials.

```csharp
Vector3 delta = _sound.Position - Camera.Position;
float distance = delta.Length();
bool blocked = distance > .001f &&
    physics.Raycast(Camera.Position, delta, distance, out _,
        new QueryFilter(LayerMask: soundBlockerLayer));
_sound.Occlusion = blocked ? 1 : 0;
```

The second raycast argument is a **direction**, not the source position. The finite
distance prevents a wall behind the source from blocking it. The zero-distance
guard avoids invalid ray directions. Physics synchronizes bound collider transforms
before querying, so moving doors work without a simulation step.

Query enabled, playing spatial sources before initial playback and then at 10 Hz;
the example keeps a countdown and performs at most one query per frame. Reset
`Occlusion` to zero when disabling it. The audio module never casts rays itself.

`Occlusion` accepts [0, 1]. `AudioSystem.Update` smooths it with an exponential
time constant (`OcclusionSmoothingSeconds`, default .1 seconds; zero is immediate).
The source exposes `SmoothedOcclusion` for inspection. Initial/restarted playback
applies the target immediately to avoid a burst of clear audio through a wall.

Fully blocked defaults are `BlockedGain = .35` and
`BlockedHighFrequencyGain = .1`; both are configurable on `AudioSystem`.
Occlusion multiplies source gain without overwriting the authored value; master gain
and distance attenuation still apply. EFX uses a per-source low-pass direct filter,
keeps its overall gain at 1, and reattaches it after parameter changes. Clear sources
detach the filter; it remains reusable until source disposal. There are no effect
slots, reverb, streaming queues, or audio worker threads in this module.

## Assets, example, and checks

The optional build workflow accepts WAV (including float/extensible WAV), MP3,
Ogg Vorbis, and FLAC. On 64-bit Windows, builds use FFmpeg on PATH or automatically
download the pinned Gyan 9.0.1 essentials build into `artifacts/tools/ffmpeg-9.0.1`.
The archive is SHA-256 checked; subsequent builds reuse the local tool offline.
Upstream licenses and documentation stay with the cached executable. FFmpeg is
never copied into the game. Set `-p:NjulfFFmpeg=D:/Tools/ffmpeg.exe` to override
resolution, or `-p:NjulfDownloadFFmpeg=false` to require PATH without downloading.
Other platforms require PATH or an explicit executable. The standalone AssetTool CLI
also requires PATH or `--ffmpeg`; automatic acquisition belongs to the MSBuild workflow.
The pinned download and hash are maintained in `tools/resolve-ffmpeg.ps1`;
source: https://github.com/GyanD/codexffmpeg/releases/tag/9.0.1.
Ordinary projects import
`build/Njulf.Audio.targets`; `build/Njulf.Game.targets` already imports it.

```xml
<ItemGroup>
  <ProjectReference Include="$(NjulfEngineRoot)/Njulf.Audio.Assets/Njulf.Audio.Assets.csproj" />
  <NjulfAudioContent Include="Assets/Audio/hit.wav" Link="Audio/hit.wav" Mono="true" />
  <NjulfAudioContent Include="Assets/Audio/menu.ogg" Link="Audio/menu.wav" />
  <!-- Game targets suppress transitive content copying; retain audio notices explicitly. -->
  <NjulfRuntimeContent Include="$(NjulfEngineRoot)/Njulf.Audio/ThirdParty/*.txt"
                       Link="ThirdParty/Audio/%(Filename)%(Extension)" />
</ItemGroup>
```

Each item requires a unique, content-root-relative `.wav` Link. Outputs are PCM16 WAV,
preserving sample rate and mono/stereo channels. Set `Mono="true"` (CLI `--mono`) to
downmix for spatial playback; multichannel input otherwise fails. The cooker normalizes
the WAV header, so runtime decoding remains the existing PCM-only implementation.
Changed inputs/settings recook; unchanged files are skipped using timestamps, length,
and a settings stamp. Failed conversion preserves the previous output and fails the build.
Only converted WAVs are copied to build/publish output; conversion stamps stay intermediate.
Projects without audio items require no FFmpeg. Direct PCM WAV sidecar copying remains valid.

```powershell
dotnet run --project Njulf.AssetTool -c Development -- cook audio Assets/Audio/hit.wav --out Cooked/Audio/hit.wav --mono
# Optional: --ffmpeg D:/Tools/ffmpeg.exe, or -p:NjulfFFmpeg=D:/Tools/ffmpeg.exe on the game build.
```

NuGet supplies native runtime libraries; no system OpenAL install is required on
Windows x64. Preserve native assets and `ThirdParty/Audio` when distributing.
Other platforms, trimmed/AOT builds, and device hot-swap are not qualified in v1.

```powershell
dotnet run --project Njulf.ApiExamples -c Development -- --example audio
dotnet run --project Njulf.ApiExamples -c Development -- --example audio --frames 120 --validation
dotnet test Njulf.Tests -c Development --filter 'FullyQualifiedName~AudioTests|FullyQualifiedName~AudioCookerTests|FullyQualifiedName~ContentScopeTests'
dotnet publish Njulf.ApiExamples -c Development --no-restore -o artifacts/audio-publish
./artifacts/audio-publish/Njulf.ApiExamples.exe --example audio --frames 120 --validation
```

WASD moves, Left/Right turns, Space toggles the wall, O toggles occlusion, and P
pauses/resumes. M mutes SFX, G fades SFX, R restarts, and F plays a one-shot.
The orange box is the source. The finite validation run opens the
wall halfway through and requires both blocked and clear observations.

The checked-in `occlusion-loop.wav` is an original generated one-second 24 kHz mono
PCM16 tone: 330 Hz plus 0.6 times 3300 Hz, multiplied by
`6500 * (0.8 + 0.2 * cos(2*pi*3*t))`. Integer frequencies make it loop continuously.

Focused tests cover WAV parsing, spatial panning and listener rotation,
distance attenuation, wall/door ray filtering, actual EFX spectral attenuation and
restoration, volume fallback, smoothing, and native lifetime. Loopback renders into
memory without speakers; it does not replace a subjective headphone listening check.
Content tests cover normalized cache reuse and shared clip ownership. Additional loopback
checks cover groups, fades, independent pause reasons, resume/restart sample positions, and
moving bindings. Cooker tests cover the supported formats and failure preservation; set
`NJULF_TEST_FFMPEG` to an executable path if it is not on PATH (otherwise those tests skip).

## Streaming music follow-up

Music currently uses buffered clips, just like SFX/UI. A separate follow-up will add
bounded-memory decoding and queued-buffer refill for long tracks, using the same Music
group, playback controls, pause policy, and disposal ordering. This change adds no streaming
threads, queues, seeking API, or runtime compressed-audio decoder.
