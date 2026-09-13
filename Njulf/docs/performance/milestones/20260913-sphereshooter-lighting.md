# Sphereshooter atmosphere brightness — 2026-09-13

Source: `09be7c7d`, initially dirty in `SphereShooter/Program.cs` with an existing
sun-intensity change from 3 to 1 and mouse-direction correction. Both incoming
changes were preserved. This fix adds an authored scene atmosphere intensity of
0.02 and updates the tutorial lighting example.

The default Hosek sky uses HDR radiances independently of the scene directional
light's intensity. At atmosphere intensity 1 and fixed exposure 1, it overwhelms
this scene's unit-intensity sun. GI and environment fallback multipliers are both
1. The forward shader composes DDGI and environment fallback with complementary
coverage weights; source inspection did not identify duplicate full-strength sky
addition. Reducing only environment diffuse intensity would leave GI probe sky
transport bright. Scale the atmosphere source so background, reflected sky and
indirect sky transport agree. Keep this calibration in the sample; this is not a
change to the engine's HDR scene defaults or a universal photometric calibration.

Validation used the actual sample source in a temporary capture host, at its
initial camera `(0, 1.7, 6)`, 1280×720, Development, DdgiHigh, 60 FPS host pacing,
VSync on, fixed exposure 1, ACES fitted, bloom intensity 0.08. Mouse/fire input
was disabled and the window hidden. Capture was requested after 360 full-quality
frames and the process exited at 390. Hardware: Ryzen 5 5600H, NVIDIA RTX 3060
Laptop GPU, driver 32.0.16.1062, Windows. Vulkan validation reported zero errors
in all three runs.

| Observation | Incoming baseline | Trial 0.02 | Final source 0.02 |
| --- | ---: | ---: | ---: |
| Display luminance mean, 0–255 | 249.65 | 116.53 | 116.60 |
| Pixels with display luminance ≥250 | 53.68% | 0% | 0% |
| Sky region mean linear HDR luminance | 10.0331 | 0.2007 | 0.2007 |
| Floor region mean linear HDR luminance | 1.7334 | 0.0877 | 0.0877 |
| Cube front mean linear HDR luminance | 2.3590 | 0.1131 | 0.1131 |
| Presented frame median, ms | 16.63 | 16.65 | 16.63 |
| Presented frame p95, ms | 19.44 | 18.09 | 18.27 |
| Presented frame p99, ms | 94.50 | 48.25 | 50.16 |

[Compact measurements and region bounds](20260913-sphereshooter-lighting.json).
HDR captures contained finite, nonnegative values. Visual inspection confirms a
blue sky and readable grey objects with face/shadow contrast. The final sample
build passes with zero warnings/errors; `git diff --check` passes.

Decision: accept the scene atmosphere correction. Timings cover 180 presented
frame intervals after warmup and include pacing, validation and runtime cache
activity; these single runs do not establish a performance improvement. The
capture covers the initial view and does not certify every camera position or
projectile interaction. No shader or GI algorithm changed.

Reproduce interactively:

```powershell
dotnet run --project SphereShooter -c Development
```

Local automated reproducer and analysis sources are retained under
`.codex-tmp/sphere-lighting/`. `run.ps1 -Mode baseline -Build` omits the new
atmosphere assignment; `run.ps1 -Mode final -Build` uses the fixed sample source.
The original trial is reproduced by `run.ps1 -Mode candidate`; `run.ps1 -Analyze`
compares all three captures. Runtime caches, temp files and outputs stay on D:
inside the workspace. Raw captures and the isolated host build are disposable
within two days; this record and the compact JSON preserve the results.

Storage review: `tools/prune-local-artifacts.ps1` reported 0.22 GiB eligible,
including copied assemblies in the active capture host. No broad deletion was
applied while the host was in use. Approximately 257 GiB remained free on D:.
