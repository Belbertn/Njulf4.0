# SphereShooter: a compact gameplay example

Run the existing project from the repository root:

```powershell
dotnet run --project SphereShooter -c Development
```

See [setup prerequisites](GettingStarted.md) and the complete [game source](../SphereShooter/Program.cs).
Geometry is procedural; the supplied impact sound is a prepared mono PCM16 WAV copied
into the output. No model import or audio conversion is needed to run this example.

| Control | Behavior |
| --- | --- |
| WASD / mouse | Walk at eye height / aim. Mouse displacement is not multiplied by frame time. |
| Left click | Fire a physics sphere at the cube stacks. Shots expire after eight simulation seconds. |
| P | Pause/resume gameplay and local sound effects; release/recapture the cursor. |
| R | Replace the level with a fresh playground, including while paused. |
| Escape | Exit and release the host's resources. |

The camera is a simple walking viewpoint; player collision and jumping are demonstrated
separately by `--example physics-simulation` in `Njulf.ApiExamples`.

## Follow the ownership and update flow

`SphereShooterGame.Load` creates cached input actions and registers one `AudioHostModule`.
The host updates the camera listener and audio device automatically. If an output device
is unavailable, the example reports it and continues silently; invalid/missing content still fails.

`LoadAsync` awaits `LoadLevelAsync`. Its loader registers a small `Playground` module,
a `PhysicsHostModule`, and an `AudioScope` with the candidate level. It populates
`level.Scene`, so the active level stays intact until the candidate commits. Registering
resources as they are created also makes partial-load cleanup automatic.

`Update` handles quit, pause, and replacement even while simulation is paused. Ordinary
movement and firing stop during pause. Replacement tasks are observed without blocking
the game thread; another R press is ignored while a replacement is pending. A replacement
resets the camera and playground while preserving pause.

`Playground.FixedUpdate` expires shots before the registered physics module steps the world.
Removing a scene object also removes its collider. A ball/cube begin-contact plays one
bounded spatial one-shot at the copied contact position. The level's audio scope follows
simulation pause; unloading stops its voices before releasing their clips.

The level owns scene objects and registered modules. The playground releases its shared
mesh/material handles; scene objects retain their own references. The host-owned audio
device survives replacement and is disposed at shutdown. There are no manual physics
steps, audio updates, or duplicate disposals of registered modules.

The existing lighting balance is retained: atmosphere intensity 0.02, directional light
intensity 1, and fixed exposure 1. This scales sky illumination for background, reflections,
and indirect lighting together.

For variations, see [input](GameplayInput.md), [physics](Physics.md), [audio](Audio.md),
[managed levels](FrameworkApi.md#managed-levels), and [window configuration](GettingStarted.md#configure-the-startup-window).