# Optional audio

Reference `Njulf.Audio/Njulf.Audio.csproj` explicitly. It depends on Njulf.Core and
Silk.NET OpenAL 2.23.0, with OpenAL Soft 1.23.1 native runtime assets. The framework
does not initialize audio automatically. Audio has no graphics or physics dependency.

## Playback and ownership

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

`Play` starts or restarts a source and resumes a paused source. `Pause` retains its
playback position. `Stop` retains the clip for replay. `State` reports Initial,
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
Doppler is disabled. Pause and time scale do not change playback automatically:
call source `Pause`/`Play` and change `Pitch` explicitly when desired.

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

For ordinary projects, copy WAVs with `CopyToOutputDirectory` and
`CopyToPublishDirectory`. Games using `build/Njulf.Game.targets` instead declare
runtime sidecars, since WAV files do not go through the graphics cooker:

```xml
<ItemGroup>
  <ProjectReference Include="$(NjulfEngineRoot)/Njulf.Audio/Njulf.Audio.csproj" />
  <NjulfRuntimeContent Include="Assets/Audio/*.wav" />
  <!-- Game targets suppress transitive content copying; retain audio notices explicitly. -->
  <NjulfRuntimeContent Include="$(NjulfEngineRoot)/Njulf.Audio/ThirdParty/*.txt"
                       Link="ThirdParty/Audio/%(Filename)%(Extension)" />
</ItemGroup>
```

NuGet supplies native runtime libraries; no system OpenAL install is required on
Windows x64. Preserve native assets and `ThirdParty/Audio` when distributing.
Other platforms, trimmed/AOT builds, and device hot-swap are not qualified in v1.

```powershell
dotnet run --project Njulf.ApiExamples -c Development -- --example audio
dotnet run --project Njulf.ApiExamples -c Development -- --example audio --frames 120 --validation
dotnet test Njulf.Tests -c Development --filter FullyQualifiedName~AudioTests
dotnet publish Njulf.ApiExamples -c Development --no-restore -o artifacts/audio-publish
./artifacts/audio-publish/Njulf.ApiExamples.exe --example audio --frames 120 --validation
```

WASD moves, Left/Right turns, Space toggles the wall, O toggles occlusion, and P
pauses/resumes. The orange box is the source. The finite validation run opens the
wall halfway through and requires both blocked and clear observations.

The checked-in `occlusion-loop.wav` is an original generated one-second 24 kHz mono
PCM16 tone: 330 Hz plus 0.6 times 3300 Hz, multiplied by
`6500 * (0.8 + 0.2 * cos(2*pi*3*t))`. Integer frequencies make it loop continuously.

Seven focused test cases cover WAV parsing, spatial panning and listener rotation,
distance attenuation, wall/door ray filtering, actual EFX spectral attenuation and
restoration, volume fallback, smoothing, and native lifetime. Loopback renders into
memory without speakers; it does not replace a subjective headphone listening check.
