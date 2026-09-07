# Nsight GPU trace optimization candidates — 2026-09-05

The largest target is Bistro automatic planar capture: **93.93 ms, 71.8% of its captured frame**. Main forward shading is second and dominates Sponza. The strongest recent regression signal is the shared temporal surface-validity producer and its added motion-vector writes.

This is an analysis, with no renderer changes or new benchmark runs. Existing working changes were preserved.

## Capture scope and interpretation

Only these two GPU traces directly in `C:\Users\njaal\Documents\NVIDIA Nsight Graphics` contribute capture measurements. No trace in a child directory is used in this report.

| Capture filename | Scene, verified from embedded screenshot | Recorded frame | Main render extent | VRAM committed / budget | Demoted VRAM |
| --- | --- | ---: | --- | --- | ---: |
| `NjulfHelloGame_2026_09_05_10_21_53.ngfx-gputrace` | Sponza | 43.761 ms | 1600×900 | 2541 / 5226 MiB | 0 MiB |
| `NjulfHelloGame_2026_09_05_10_25_22.ngfx-gputrace` | Bistro | 130.765 ms | 1600×900 | 2879 / 5226 MiB | 0 MiB |

Both use Nsight Graphics 2026.3.1, RTX 3060 Laptop/GA106, driver 610.62, GPU clocks locked to base, and the `ProfileSymbols/net10.0/NjulfHelloGame.exe` executable. Both identify process 33944. V-sync is forced off; real-time shader profiling is enabled; multi-pass metrics and Time Every Action are disabled.

These are different scenes, **not a before/after pair**. Their frame-time difference cannot establish a code regression. Each contains one selected presented frame. Pass values below are timestamp spans for that frame, not steady-state averages. Existing repository benchmarks are identified separately and are not numerically compared against these 900p, base-clock traces.

## Ranked candidates

Times are measured work budgets, not promised savings. Nested markers are included in their owning pass and are not counted twice.

| Rank | Candidate | Sponza ms | Bistro ms | Recommended next change or check |
| ---: | --- | ---: | ---: | --- |
| 1 | Automatic planar reflection capture | Not executed | **93.928** | Repair reflected-view depth/culling and auxiliary pipeline selection; remove per-fragment object exclusion work where it can be decided per object/meshlet. |
| 2 | Main opaque forward shading | **26.267** | **18.967** | Concentrate on the Full and SimpleFullInput hybrid fragment programs, remaining DDGI diffuse/visibility loads, and live register pressure. |
| 3 | Motion vectors plus directional temporal/shared validity | **4.844** | **4.199** | Rework or gate the shared-validity path until its producer and extra motion writes pay for themselves. |
| 4 | Post-forward hybrid reflection stack | **4.442** | **4.602** | Start with DDGI cohort production, then resolve/temporal bandwidth. Ray-query traversal is negligible in these frames. |
| 5 | GTAO | **2.596** | **2.605** | Optimize raw AO and spatial filtering; temporal AO is already small. Retain the final measured spatial improvement. |
| 6 | DDGI transport audit | **2.065** | Not executed | Check audit lifetime and invalidation; avoid unnecessary restarts and amortize work while preserving certification. |
| 7 | Scene opaque compaction | **1.713** | **1.688** | Inspect instance expansion and synchronization, with context-switch effects separated from kernel execution. |
| 8 | Bistro transparency | **0.055** | **1.175** | Further specialize remaining ThinGlass work only after the much larger targets; the new main-view ThinGlass variant is active. |

### 1. Planar capture: investigate structure before small shader edits

The Bistro trace has one planar rendering scope at **800×450**. Its opaque draw issues **83,725 meshlet groups** and takes **93.059 ms**, or **99.1% of the planar pass**. The transparent capture draw issues 1,628 groups and takes 0.435 ms; sky, depth processing, mip generation, and remaining work together account for approximately 0.433 ms. Mip generation is not the primary problem.

The opaque draw binds `First-Frame Universal Compacted Opaque Forward Pipeline`. Its active fragment program uses **168 registers/thread** and accounts for **87.7% of raw PC sample records in the planar interval**. The shared mesh program accounts for approximately 9.8%. Sample shares describe the sampled interval and are not exclusive duration percentages.

Three concrete code checks follow from this evidence:

1. **Reflection depth and visibility.** `RecordAutomaticPlanarCapture` clears a separate reflection depth attachment, sets `DepthPrePassEnabled=true`, disables occlusion/Hi-Z, and proceeds through sky/opaque/transparent drawing without a reflected-view depth prepass. The bound bootstrap opaque pipeline is created with `depthWriteEnable=false`; `DrawForwardBucket` selects `GreaterOrEqual` and no culling. This combination warrants an immediate depth correctness and overdraw check. A reflected-view depth prepass or appropriate depth-writing capture pipeline is a stronger candidate than reducing reflection resolution further. The construction and capture setup date to August 29–30, so this is a pre-existing concern, not evidence that the September 4 specialization introduced it.
2. **Prepare or select the correct auxiliary family.** Capture recording requests `Simple`/`SimpleFullInput`/`Full`, while fast startup prepares the active taskless family bank. The missing-handle branch added in `db8e8828` explicitly falls back to `CompactedFull`. The observed universal pipeline is consistent with this route. Use the matching taskless capture family, with a valid reflected-view draw stream, or prepare the required auxiliary sibling. The label persists for the lifetime of that pipeline; it does not establish that the frame was still in startup or that the whole specialized bank failed.
3. **Move object exclusion out of fragment shading where possible.** `AutomaticPlanarExcludedObjectContains` still has an exact linear-list fallback, and `AutomaticPlanarMetadataEncoder.ResolveMode` defaults to `SortedList`. Decide whole-object exclusion before rasterization, retaining the precise fragment plane clip. The trace does not expose the selected exclusion encoding or list length, so the proportion of cost attributable to that loop is unmeasured. Do not blindly promote the bitset experiment merely because it is theoretically cheaper.

Source anchors: [capture recording](../Njulf.Rendering/Pipeline/ForwardPlusPass.cs) at 956, 1010, 1051, and 1080; [draw recording](../Njulf.Rendering/Pipeline/ForwardPlusPass.cs) at 2600 and 2690; [auxiliary fallback and pipeline depth state](../Njulf.Rendering/Pipeline/PipelineObjects/MeshPipeline.cs) at 1778, 2067, 3372, and 5371; [fragment exclusion](../Njulf.Shaders/automatic_planar_reflection.glsl) at 141 and 155; [encoding default](../Njulf.Rendering/Resources/AutomaticPlanarMetadataEncoder.cs) at 50.

### 2. Main forward: expensive fragments, especially Sponza Full

Sponza spends **60.0%** of its frame in `ForwardPlusPass`. The dominant draws are:

| Scene | Pipeline | Draw span | Active fragment registers/thread |
| --- | --- | ---: | ---: |
| Sponza | `Hybrid Reflection Forward Pipeline L0 C0 F1 Sparse Lobe` — CompactedFull | 14.953 ms | **255** |
| Sponza | `Hybrid Reflection Forward Pipeline L0 C0 F5 Sparse Lobe` — CompactedSimpleFullInput | 10.999 ms | **168** |
| Bistro | `Hybrid Reflection Forward Pipeline L0 C0 F5 Sparse Lobe`, first draw | 17.496 ms | **168** |
| Bistro | Same family, second draw | 1.414 ms | **168** |

Fragments account for approximately **96.4%** of Sponza forward PC records and **93.1%** of Bistro forward records. This makes fragment specialization and remaining gather/material work a higher priority than increasing meshlet size alone. The register counts are recorded native-program metadata, not estimates from GLSL or SPIR-V size. They motivate reducing live values and expensive branches; they do not prove spills or a specific occupancy limit.

The current source already compiles directional DDGI gathering out of hybrid opaque shaders through `FORWARD_DDGI_DIRECTIONAL_GATHER=0`. Do not repeat that completed proposal as a new optimization. Focus on the remaining exact diffuse/visibility gather, Full-material routing, and ensuring the intended specialization reaches the actual capture artifacts. Preserve authored sidedness, normal maps, alpha coverage, and extended-material behavior when narrowing a family.

Source anchors: [hybrid gather ownership](../Njulf.Shaders/forward.frag) at 202; [gather implementation](../Njulf.Shaders/forward_ddgi_receiver_gather.glsl); [variant classification](../Njulf.Rendering/Pipeline/ForwardPlusPass.cs) at 2432.

### 3. Shared temporal validity: strongest recent regression signal

The captures show motion vectors taking **3.514 / 2.849 ms** and the directional temporal marker taking **1.330 / 1.350 ms**, respectively. The latter contains CSM resolve, shared surface validation, and the final directional temporal filter. The middle shared-validity dispatch costs **0.299 / 0.294 ms**. Its cost must be evaluated together with the seed writes in motion vectors.

The motion fragment now writes four extra words per pixel, including identity, current/previous depth, and geometric normal, before a later compute pass reads the history and evaluates four candidate reprojection taps. The two full-resolution 16-byte banks require **43.95 MiB at 1600×900**, or **63.28 MiB at 1920×1080**, in addition to the existing shadow history.

The stored Plan 9/12 comparison at 1920×1080 records:

| Metric | Before mean | After mean | Difference |
| --- | ---: | ---: | ---: |
| Motion vectors | 1.157 ms | 1.876 ms | +0.718 ms |
| Directional temporal marker, including producer | 0.703 ms | 1.043 ms | +0.340 ms |
| Affected aggregate | 1.861 ms | 2.919 ms | **+1.058 ms, +56.9%** |
| Whole GPU frame | 35.396 ms | 36.469 ms | **+1.074 ms, +3.03%** |
| Whole GPU frame p95 | 36.750 ms | 38.011 ms | +1.261 ms, +3.43% |

This is the clearest measured slowdown associated with the recent work. However, **both existing captures timed out after 256 additional settling frames and recorded only 30 measured frames**. Their formal capture contracts mark them non-comparable because transport was not settled and the production sample minimum was not reached. The matching settings fingerprint, nearly identical cost increase in the affected passes and whole frame, and extra bandwidth support a strong engineering suspicion; this is not a clean settled regression proof.

Recommended first A/B: the complete shared producer/seed-writing path versus its retained legacy directional validation path, with transport settled and the same workload. Optimize or gate the whole producer/consumer contract rather than benchmarking only the final temporal filter.

Implementation follow-up: [surface-input reuse and local qualification](SurfaceInputReuse-20260906.md) disables the costly shared producer, measures opt-in depth/motion fusion including identity storage/copy costs, and rejects CSM normal reuse below the benefit threshold. Effect-specific temporal rejection remains separate.

Evidence: [before](../.perf-loop-runs/temporal-validity-plan9-12-20260904/baseline.json), [after](../.perf-loop-runs/temporal-validity-plan9-12-20260904/candidate.json), [implementation record](Complete/OrderedPerformancePlansEvidence-20260904.md). Source: [motion seed writer](../Njulf.Shaders/motion_vector.frag), [four-word codec](../Njulf.Shaders/temporal_surface_validity.glsl), [directional consumer](../Njulf.Rendering/Pipeline/DirectionalShadowScreenPasses.cs).

### 4–8. Smaller targets and useful exclusions

- **Hybrid reflections:** DDGI base takes 2.222 / 2.319 ms, about half the post-forward stack. Cohort production alone takes **1.938 / 1.894 ms**, with 168 registers/thread in the active compute program. Reconstruction is only 0.273 / 0.278 ms. Resolve and temporal add approximately 1.20 / 1.26 ms. Ray query is **0.005 ms in both traces**; expanding ray task records or optimizing traversal is low priority for these particular frames. Source: [DDGI cohort/reconstruction template](../Njulf.Shaders/hybrid_reflection_ddgi_base.comp).
- **GTAO:** raw AO is 1.420 / 1.416 ms, spatial is 1.038 / 1.050 ms, and temporal is only 0.138 / 0.139 ms. Spatial neighborhood loads and raw AO sampling are the useful targets. The final spatial optimization already improved its saved target timing; see the disposition table below.
- **Transport audit:** Sponza's 2.065 ms audit includes two approximately 0.969 / 0.947 ms dispatches of the same 168-register audit program. This is recurring certification work, not the main DDGI ray-trace pass. The new camera-motion gate only defers starting a new audit; `TryBeginTransportTailAudit` immediately accepts an already `AuditFrozen` audit. Seeing an audit here does not prove that the motion-deferral change failed. Establish whether this was an existing frozen audit, a stationary frame, or a genuine unnecessary restart before changing its scheduling. Source: [audit admission](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs) at 3667.
- **Scene compaction:** its main dispatch spans 1.611 / 1.587 ms and uses a 96-register program. Both intervals include GPU context switching to other contexts in the same process; therefore the full timestamp span is not clean exclusive kernel execution. Recheck without that interference before claiming a kernel regression. Production aggregate-output duplication has already been removed.
- **Transparency:** the specialized `ThinGlass + SortedAlphaBlend + RaySceneRequired` program is active in Bistro and uses 168 registers/thread. The main transparent draw is 1.163 ms. The planar transparent draw still uses the generic transparent pipeline, but costs only 0.435 ms. Narrow further only where material and ray-query work remains necessary.
- **Lower priorities here:** ordinary depth is 0.746 / 0.925 ms; bloom is about 0.182 ms; tone mapping and the remaining small post passes are much smaller than forward and planar capture. VRAM demotion is zero, so the captures do not support a paging diagnosis or a claim that the increased DDGI memory budget caused the main slowdown.

## Did recent optimizations make performance worse?

| Change | Evidence and disposition |
| --- | --- |
| Shared surface validity + directional consumer, in `db8e8828` | Strongest slowdown signal: +1.058 ms affected aggregate and +1.074 ms whole frame in the existing short comparison. Both runs failed settling; prioritize a settled repeat and rework/gate if confirmed. It is active in both supplied traces. |
| Pipeline specialization promotion `0fd9945f`, then auxiliary fallback in `db8e8828` | Main view reaches specialized hybrid families, while the planar draw still uses the full bootstrap program. The source exposes a missing auxiliary-family fallback. This is a major missed optimization, but the two new traces do not prove a before/after slowdown. Earlier fast-startup code also routed through a universal program. |
| Forward interface experiments | Original world-position reconstruction worsened forward mean **18.910→19.289 ms**; packed `uvec3` IDs worsened it **18.910→19.251 ms**. Whole-frame means also increased about 1.6–1.9%; those experiments were rejected. The retained follow-up uses separate component-qualified `perprimitiveEXT` IDs and has no new timing pair. It is a different implementation and remains unproven; do not call it a measured win or attribute the rejected experiments' deltas to it. |
| Opaque visibility compute experiment in the dirty worktree | Same-build forward control **27.909 ms mean / 29.547 p95**, compute **28.184 / 29.762**: approximately **1.0% worse mean, 0.73% worse p95**. It remains opt-in. The supplied traces execute mesh-draw forward shading, so this backend is not their current bottleneck. |
| GTAO spatial optimization | The first `candidate.json` regressed spatial **1.426→2.206 ms** and frame **24.786→25.558 ms**. The final `candidate-fast.json` fixed that: spatial **1.426→1.297 ms**, frame **24.786→24.693 ms**. Use the final artifact when assessing the retained implementation. |
| ThinGlass specialization | Saved benchmark improves transparent aggregate **1.314→0.686 ms** and frame **26.966→26.511 ms**. The new family is present in the Bistro trace. No evidence here to undo it. |
| Scene-compaction output trimming | Saved compaction mean **0.817→0.765 ms**; frame **26.532→26.556 ms**, effectively neutral. A local improvement, not an established whole-frame speedup. |
| Motion-vector sided raster | Earlier local comparison improved the pass **1.088→0.979 ms**; its whole-frame result was disturbed by reflection-resolve spikes. The subsequent shared-validity seed writes add a separate cost. Do not conflate the two changes. |
| Meshlet cone-0.50 candidate | Saved forward mean was essentially unchanged (**12.251→12.209 ms**), while AO had large excursions and frame time worsened. The global production profile was not promoted. The report does not isolate a meshlet-caused whole-frame regression. |
| DDGI budget increase `d43da490` | Default/high budget grew **192→288 MiB**. That can admit more work, but the two traces neither identify a matched budget comparison nor show VRAM demotion. It is not the leading explanation for the 93 ms planar draw. |

Supporting existing evidence: [interface results](Complete/ForwardFragmentInterfaceReductionEvidence-20260904.md), [compute experiment](OpaqueVisibilityComputeExperiment-20260905.md), [ordered plan results](Complete/OrderedPerformancePlansEvidence-20260904.md), [GTAO final capture](../.perf-loop-runs/gtao-spatial-plan5-20260904/candidate-fast.json), [ThinGlass capture](../.perf-loop-runs/transparency-plan6-20260904/candidate.json), [compaction capture](../.perf-loop-runs/scene-compaction-plan8-20260904/candidate.json).

## Extraction and limits

The analysis decoded the trace's compressed metadata using the message schemas embedded in the installed matching Nsight version. GPU pass/draw spans come from `PbTimedAPIStream` timestamps and debug-label ranges, clipped to the selected presented frame. Native register counts and shader address ranges come from `ShaderProfilerReport`; timestamped per-SM PC records identify which native programs actually ran. Other stored driver variants were not substituted for the sampled programs.

The selected-frame top-level markers account for 43.470 of 43.761 ms in Sponza and 130.462 of 130.765 ms in Bistro. Small unlabelled gaps remain. A timestamp span can include waits and descheduling; PC records are samples, not exclusive elapsed time. These distinctions matter particularly for scene compaction. The raw PM counter sections were not evaluated, so no measured occupancy, bandwidth, spill rate, or Nsight projected speedup is claimed. NVIDIA documents the [GPU Trace event and metric views](https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-ui.html) and [capture controls](https://docs.nvidia.com/nsight-graphics/UserGuide/gpu-trace-overview.html).

Source review used HEAD `db8e882865370af4dbb99b5da95c8516865be278` plus the existing dirty worktree. The traces do not embed a repository commit or complete application settings fingerprint, so source-based causes remain candidates unless independently supported by a matched experiment.

Reproducible extracts, embedded screenshots, hashes, pass/action JSON, PC-sample summaries, and extraction scripts are under [`.tmp/nsight-analysis-20260905`](../.tmp/nsight-analysis-20260905). [Input manifest](../.tmp/nsight-analysis-20260905/manifest.json).

- Sponza SHA-256: `6aac2853431f5e44b92ab786a5f71d4a4cd630cd53f319f0df3877b671f983b3`.
- Bistro SHA-256: `3ad330d9019a545a595bdb52ae706c6245f764ad5de4b9f26104520a604b9c67`.

The first implementation priorities are planar capture depth/family selection, the shared-validity cost check, and Sponza's 255-register Full forward fragment program. Each should be measured independently.
