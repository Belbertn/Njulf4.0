# Simulation timing and pause

`Game` defaults to variable simulation, a time scale of one, and continued simulation
when unfocused. `MaximumFramesPerSecond` and VSync still control presentation pacing.
Timing starts after `LoadAsync` completes.

## Optional fixed simulation

Set `IsFixedTimeStep = true`. `Update(GameTime)` still runs once per host update,
after one input publication. It is followed by zero or more `FixedUpdate(GameTime)`
calls. Default `Update` advances the scene only in variable mode; default
`FixedUpdate` advances it only in fixed mode. Call the appropriate base method to
retain scene updates. Draw remains independent of the simulation cadence.

| Control | Default | Contract |
| --- | --- | --- |
| `IsFixedTimeStep` | `false` | Enables `FixedUpdate` |
| `TargetElapsedTime` | `TimeSpan.FromSeconds(1.0 / 60)` | Positive fixed interval |
| `MaxCatchUpSteps` | `5` | Positive maximum fixed calls per host update |
| `TimeScale` | `1.0` | Finite nonnegative multiplier; zero pauses |
| `IsPaused` | `false` | Explicit application pause |
| `PauseWhenInactive` | `false` | Opt-in focus pause |
| `IsActive` | Window focus | Read-only |
| `IsSimulationPaused` | Combined pause reasons | Read-only |
| `InterpolationAlpha` | `1` outside active fixed interpolation | Read-only |

Scaled wall elapsed accumulates until a full fixed step is available. Each fixed
callback receives exactly `TargetElapsedTime`, even during slow motion. After the
catch-up limit, excess whole steps are discarded and only the fractional remainder
is retained. Discarded steps do not advance the fixed simulation total.

Keep previous/current simulation state in application fields. In `Draw`, interpolate
their presentation using `InterpolationAlpha`; do not feed interpolated values back
into simulation. The fraction is in `[0, 1)` after a fixed step. It is `1` in variable
mode, during pause, and after a timing reset until another fixed step completes.
The framework does not automatically interpolate scene transforms.

Read input edges and mouse deltas in `Update`. Retain pending commands across host
ticks without fixed steps, consume each command once in `FixedUpdate`, and discard
pending gameplay commands when paused. Reading `WasPressed` in every catch-up
callback would repeat the same input snapshot.

## Scaled and unscaled time

| Callback | `TotalGameTime` | `ElapsedGameTime` |
| --- | --- | --- |
| `Update` / `Draw` | Integrated scaled time, excluding pauses | Scaled interval for this callback stream; first interval zero |
| `FixedUpdate` | Sum of executed fixed steps | Exactly `TargetElapsedTime` |

`UnscaledTotalGameTime` is monotonic wall time since content loading completed.
`UnscaledElapsedGameTime` is the wall interval since the previous invocation of the
same callback stream, initially zero. Both include pauses and dropped catch-up time.
Fixed callbacks in one batch share a timestamp: after its first callback, their
unscaled elapsed intervals are zero. Use the scaled fixed interval for simulation;
use unscaled frame time in `Update` for menus and UI animation.

The existing two-argument `GameTime(total, elapsed)` constructor and deconstruction
remain available. That constructor initializes unscaled values to the supplied values.

## Pause and focus

Effective pause is `IsPaused || TimeScale == 0 || (PauseWhenInactive && !IsActive)`.
Input, `Update`, `Draw`, uploads and async continuations keep running. Scaled elapsed
is zero while paused; no fixed callbacks or default scene updates run. Application
code that mutates gameplay in `Update` must honor `IsSimulationPaused` itself.

Timing controls run on the game thread. Changes cannot modify an already-delivered
`GameTime` value. Pause stops remaining fixed callbacks in the current batch.
Pause/resume and fixed-mode/interval changes discard backlog without resetting totals.
After resume, the first Update/Draw scaled interval is zero and fixed simulation waits
for a fresh full interval. Changing a positive scale affects future elapsed time.
Changing an unused fixed interval in variable mode does not reset frame timing.

Focus return removes only the focus pause reason. Explicit pause and zero scale
remain in force. Existing input behavior still releases cursor capture, clears
transients and requires held actions to return to neutral. With focus pause disabled,
simulation continues; a platform stall is handled by the usual bounded catch-up.

## Renderer effects

The host supplies `IRendererGameTime.SetGameTime` after a successful `BeginFrame`
and before `Draw`. Vulkan particles, trails/beams, foliage wind and animated time of
day honor host pause and scaling. Effects remain render-step; fixed gameplay does
not add GPU simulation substeps. Repeated scene draws reuse the frame time without
advancing effects again. Frozen particles still produce render instances.

`Particles.FixedSimulationDeltaSeconds` remains a particle-only override for
captures: it is multiplied by host scale, and pause always takes precedence.
Existing particle stability limits remain in effect. Foliage, its shadows, motion
vectors and DDGI proxies share scaled scene time. Day/night animation additionally
applies its own authored time scale. Profiling, upload budgets, exposure adaptation
and GI scheduling continue independently of simulation pause.

Custom renderers can implement the optional timing interface. Standalone renderer
callers that never supply host time keep the existing clock fallback.

## Example and validation

```powershell
dotnet run --project Njulf.ApiExamples -c Development -- --example timing
```

The example uses 30 Hz simulation and a 90 FPS render limit, with interpolated
movement, particles and wind. P toggles pause; T toggles half speed; F toggles focus
pause; Space queues a direction reversal for the next fixed update. Its title shows
unscaled time continuing during pause. Focus pause is explicitly enabled by this
example; it is not the framework default.

Focused tests are `GameTimingTests`, `GameTimingLifecycleTests`,
`FrameworkContractTests` and `ParticleEmitterSimulationTests`. The lifecycle fixture
reads GPU particle state and counters across paused and resumed presented frames.
