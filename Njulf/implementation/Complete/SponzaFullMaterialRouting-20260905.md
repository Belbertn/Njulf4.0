# Sponza Full material routing

## Finding and implementation

The cooked main Sponza model has no extension-bearing materials. The curtain
addendum has three: `curtain_01`, `curtain_03`, and `curtain_02`, used by 27
objects. Their flags are `0x800200` (BC5 normals and transmission). They have
GI ThinSurface transmission 0.35 and tint 0.9, with no other extension lobe.

`forward.frag` explicitly disables visible raster transmission for PBR
ThinSurface materials. Its IOR also remains at 1.5 unless raster transmission
or the separate IOR feature is active. Nevertheless, the CPU classifier
previously required Full for every transmission extension.

The classifier now ignores transmission and transmission-texture requirements
for that GI-only raster case. It checks both the metadata and GPU transport
flags, retains the material's GI payload and feature flags, and continues to
require Full for IOR, clearcoat, and every other visible extension. ThinGlass,
volume transmission, and blending retain their existing classification.
Normal maps, transformed UVs, masking, and vertex colors still select the
appropriate full-input interface through the existing material/mesh routing.

This moves all 30,172 Full-class opaque candidate meshlets into the existing
simple families; the complete Sponza scene now has 92,477 simple candidate
meshlets and zero Full opaque candidate meshlets. These are candidate counts,
not visible fragment counts. No additional shader family, ABI, public setting,
or material-data migration is introduced. There is no remaining Sponza Full
opaque workload to justify a further Full shader body rewrite in this change.
The completed removal of hybrid directional DDGI gathering is unchanged.

## Shader evidence

The motivating 1600 x 900 ProfileSymbols trace is
`NjulfHelloGame_2026_09_05_10_21_53.ngfx-gputrace`, SHA-256
`6aac2853431f5e44b92ab786a5f71d4a4cd630cd53f319f0df3877b671f983b3`.
It is independent of the new Release benchmarks.

The matching Nsight 2026.3.1 UI opens this trace offline. Its Full draw occupies
21.493216–36.445984 ms (14.952768 ms). The fragment shader hash is
`0xe04682c0811ddfc2`, with 255 registers, 253 live registers reported, and seven
theoretical warps. SimpleFullInput also reports seven theoretical warps at
168 registers. Lower register count alone therefore does not establish an
occupancy improvement for these recorded programs.

The Full draw interval has 6.5 average pixel warps (13.6% occupancy), 26.6
active threads per warp (83.2% coherence), and 27.6% SM throughput. Full's
leading sampled stalls are WAIT (37.34%) and long scoreboard (17.47%). The
draw interval's instruction dependency view attributes 264,566 samples to
global-memory loads and 115,362 to local-memory loads. Local-memory activity
is observed; a specific spilling source or register-limited occupancy is not
established by these figures.

Source hotspots include aligned storage reads, receiver-address modulo,
near-visibility confidence branches, atlas support checks, and atlas-address
division. These identify useful future DDGI experiments if a measured Full
workload remains in another scene. This change removes Sponza's unnecessary
Full routing before pursuing those broader changes.

NVIDIA distinguishes theoretical occupancy limits, sampled stall reasons,
and instruction dependencies in its [Shader Profiler documentation](https://docs.nvidia.com/nsight-graphics/UserGuide/shader-profiler.html).
A new CLI trace attempt failed with "GPU Performance Counters unavailable";
the shader evidence above comes from the supplied trace, not a candidate
hardware-counter capture. UI exports and a screenshot of the selected Full
interval are saved under `.perf-loop-runs/sponza-full-20260905/`.

## Verification

The production Release/Compile build succeeds. Shader bytecode is unchanged.
All 18 material classifier tests pass, including six new parameterized cases
for GI-only transmission, BC5 normal input, masking, transmission textures,
and visible IOR/clearcoat extensions. Existing ThinGlass and ordinary
extension tests also pass. No new GPU infrastructure is added.

The initial `sponza-high` pair is settled and image-correct, but inconclusive
for performance: forward 9.2748 → 9.3073 ms, GPU frame 21.8468 → 21.9289 ms.
HDR relative RMSE is 0.00006161 and FLIP p95 is 0.004493. Removing the Full
candidate bucket does not establish a speedup at that camera. A corrected
pair uses the existing curtain/masonry incident bookmark to exercise the
affected materials directly.

### Matched curtain-facing result: keep

Both runs use Release/Compile, RTX 3060 Laptop, driver 610.62, 1920 x 1080,
DdgiHigh, Normal scenario, `sponza-receiver-cache-incident`, async compute
disabled, V-sync off, and unlocked clocks. Each has 120 warmup frames,
416 additional settling frames, and 120 measured frames; neither times out.
The shader bundle hash is identical. Both submit 18,510 opaque meshlets.

| Metric | Baseline | Candidate | Improvement |
| --- | ---: | ---: | ---: |
| Forward mean (ms) | 15.9410 | 12.7255 | 20.2% |
| Forward p95 (ms) | 16.292 | 12.945 | 20.5% |
| GPU frame mean (ms) | 29.4176 | 26.3599 | 10.4% |

HDR comparison passes: relative RMSE `6.647e-8`, maximum absolute difference
`1.526e-5`, and FLIP p95 zero. These results exceed the quick-loop 5% target
without a frame regression. The measured benefit is reduced forward shading
cost from material routing; no candidate register-count or occupancy gain is
claimed. The historical 14.95 ms Full draw is not the comparison denominator.

Artifacts: `curtains-baseline.json`, `curtains-candidate.json`, matching HDR
files, command arguments, and shader/rendering binary hashes in
`.perf-loop-runs/sponza-full-20260905/`. The candidate Rendering binary hash
also matches the one used for the passing classifier test build. The saved
historical CSV exports have malformed headings around some compound columns;
stall entries were read as explicit label/value pairs, rather than by blindly
zipping those columns. The selected interval's occupancy and instruction
dependencies are also preserved in the UI extract and screenshot.
