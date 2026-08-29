# Complete Adaptive Reflection System Implementation Plan

Status: Proposed  
Date: 2026-08-29

## Goal and acceptance criteria

Complete the reflection system's remaining quality and performance work without
adding any manually placed probe, planar-surface, capture-volume, or test-scene
helper. Existing local probes remain a compatibility fallback, but receive no
new authoring, capture, baking, clustering, cubemap-path, or qualification work.

Replace the current full-screen hybrid chain with an adaptive implementation
while retaining the current implementation as a staged rollback. For matching
receivers, the complete source order is:

1. automatic planar reflection;
2. screen-space reflection;
3. ray-query recovery;
4. directional DDGI;
5. existing local probes;
6. the global environment.

`StaticProbesAndPlanar` uses automatic planar capture followed by the existing
probe/environment fallback. `HybridRayQuery` admits the complete source chain.
The other reflection modes retain their current source semantics.

The authoritative 1920x1080 High-tier gate on the current benchmark host is:

- total reflection-owned GPU P95 no greater than 3.25 ms over 720 frames;
- total reflection-owned GPU P99 no greater than 4.25 ms;
- peak reflection-owned memory no greater than 160 MiB, including buffers,
  active planar resources, and any allocated legacy probe array;
- no task overflow, Vulkan validation error, non-finite output, hidden
  transparent work, or unaccounted active pass;
- aggregate HDR-FLIP mean and P95 in targeted reflection ROIs at least 10%
  better than the legacy path, with no individual ROI regressing by more than
  2%.

## Public contracts and compatibility

### Implementation selection and serialization

Add:

```csharp
public enum ReflectionImplementationMode : uint
{
    Auto = 0,
    Legacy = 1,
    Adaptive = 2
}
```

Add `ReflectionSettings.ImplementationMode`, defaulting migrated settings to
`Auto`. Preserve every existing `ReflectionMode`, debug-view, and fallback
numeric value; append new values only.

During opt-in rollout, `Auto` resolves to `Legacy`. After qualification it
resolves to `Adaptive` only on certified devices. Explicit `Adaptive` is usable
before promotion and falls safely to `Legacy` with a diagnostic reason when
required resources or capabilities are unavailable.

Append implementation, automatic-planar-capability, and memory-budget fallback
reasons. Diagnostics must expose:

- requested and effective implementation plus fallback reason;
- receiver tile rates and indirect work counts;
- base and secondary-lobe source counts;
- source-scoped history invalidations;
- ray/task reservations, admissions, overflows, hits, and misses;
- planar candidates, selections, updates, reprojections, ages, and rejection
  reasons;
- sorted and weighted transparent reflection reservations and timings;
- exact owned bytes per reflection image and buffer;
- timings for every stage included in the total reflection cost.

Bump the following versions together:

- render-settings serialization 23 to 24;
- hybrid receiver payload ABI 4 to 5;
- forward reflection attachment semantic version 1 to 2;
- primitive transport profile schema 5 to 6 and algorithm 6 to 7;
- cooked model package 1.5 to 1.6;
- Bistro reflection capture and qualification contracts to their next
  versions.

Old settings load as `Auto`. Old cooked and runtime-generated meshes run the
same deterministic runtime planar analyzer from their retained positions and
indices, so recooking improves startup work but is not required for correctness.

### Receiver and lobe ABI

Receiver ABI v5 keeps the current four-word payload and assigns the last word
as follows:

- receiver identity: 22 bits;
- specular occlusion: 6 bits;
- lobe flags: 3 bits (`Transmissive`, `Anisotropic`, `Clearcoat`);
- validity: 1 bit.

Add an `RG32UI` forward lobe-extension attachment:

- word 0 stores the oct16 clearcoat normal;
- word 1 stores clearcoat factor, clearcoat roughness, anisotropy strength, and
  canonical-tangent azimuth as four normalized bytes.

The producer writes a defined zero extension for every receiver without an
extended lobe. The same image becomes the raw metadata target after reflection
classification: word X describes the base lobe and word Y describes clearcoat.
Identity is read from the receiver payload instead of being duplicated in raw
metadata.

Add a per-material reflection revision to the forward material record and
include it in the receiver identity. Increment it only for changes that affect
F0, roughness, normals, clearcoat, anisotropy, transmission, or their textures.
This replaces the current global material-history reset with per-receiver
invalidation.

### Temporal and memory ABI

Compact each temporal metadata history to `RG32UI` with this 64-bit layout:

- identity: 22 bits;
- FP16 reverse-Z: 16 bits;
- oct8 x 2 history normal: 16 bits;
- source: 3 bits, where zero means invalid;
- age: 5 bits, saturating at 31;
- sparse state: 2 bits for none, resolution cadence, ray budget, or reserved.

Keep temporal confidence in the FP16 history radiance alpha channel. Replace
each `RG16F` moment history with one `R16F` variance history and derive the first
moment from history luminance.

Remove the dedicated full-resolution filter-scratch image. Its lifetime is
replaced as follows:

1. before temporal accumulation, the current-history image is transient
   clearcoat radiance storage;
2. temporal accumulation overwrites current history with the authoritative
   unfiltered result;
3. the previous-history image is then dead for the frame and becomes spatial
   scratch;
4. after composite consumes the filtered result, that same image becomes the
   immutable opaque snapshot for transparent reflections;
5. on the next frame it is the temporal destination and is overwritten again.

The adaptive full-resolution core is therefore 68 bytes per pixel:

- receiver payload: 16 bytes;
- raw radiance: 8 bytes;
- transient/raw metadata: 8 bytes;
- two radiance histories: 16 bytes;
- two variance histories: 4 bytes;
- two compact metadata histories: 16 bytes.

Add an exact reflection memory planner that uses Vulkan allocation sizes, not
format estimates. At High it additionally budgets:

- one HDR mip tail beginning at half resolution;
- no more than 4 MiB of compact DDGI cohort storage;
- no more than 4 MiB of shared tile/task/indirect/counter storage;
- planar color, depth, and mips at the largest scale from 0.5, 0.375, and 0.25
  that keeps the complete reflection allocation at or below 160 MiB.

If the minimum planar allocation does not fit, reject that capture with an
observable memory reason and continue through the normal reflection fallback
chain. Never require a manually placed replacement.

## Implementation sequence

### 1. Establish the adaptive scheduler and scoped invalidation

Extract the reusable mechanics from the current adaptive DDGI receiver cache:

- 8x8 tile binning;
- compact append buffers;
- the compact receiver-surface ABI;
- indirect-dispatch argument generation;
- bounded overflow behavior;
- asynchronous diagnostic readback.

Keep reflection classification policy separate. It classifies tiles as Full,
Half, Quarter, or Reuse from receiver validity, physical and scheduling
roughness, F0, lobe flags, motion, depth/normal edges, prior source/confidence,
and current dirty state.

Generate indirect lists for:

- DDGI base and clearcoat cohort gathering;
- automatic-planar and SSR tracing;
- base and clearcoat ray recovery;
- temporal reprojection;
- static carry-forward;
- zero, one, or two spatial iterations;
- reflection composition.

A Reuse tile is legal only when every receiver is unchanged, its maximum
motion is below 0.25 pixel, and no source invalidation applies. A cheap indirect
carry pass copies its valid temporal state. Camera motion above that threshold
always schedules normal reprojection.

Restrict full history resets to extent changes, reflection mode or
implementation changes, payload/history ABI changes, and camera cuts. Use a
source-bit invalidation mask for all other changes:

- environment generation invalidates environment-sourced receivers;
- DDGI topology/publication changes invalidate DDGI-sourced receivers;
- existing probe revision changes invalidate local-probe-sourced receivers;
- planar selection/capture generation changes invalidate planar receivers;
- ray-scene changes invalidate geometric sources;
- material reflection revisions are rejected by receiver identity.

Keep invalidation bits live until every affected tile has been processed. Rare
global source changes may refresh every tile of that source, but must not erase
unrelated history.

Replace the full-resolution DDGI cohort image with compact variable records.
Invalid cache state, dirty history, or insufficient representative confidence
must schedule exact reconstruction. Cohort/task overflow resolves through the
next analytic source, increments an exact counter, and fails qualification.

### 2. Replace SSR and add rough-lobe tracing

Create one reusable HDR color mip tail whose level zero corresponds to
half-resolution SceneColor:

1. after the sky pass, build it from opaque SceneColor for opaque SSR;
2. after reflection composite, copy SceneColor into the dead previous-history
   image and rebuild the same tail for transparent reflections.

Shaders treat SceneColor or the opaque snapshot as logical mip zero, then
manually blend to and within the separate mip tail.

Replace fixed parabolic SSR stepping with a shared CPU/GLSL reverse-Z
hierarchical traversal:

- clip the ray segment against the view frustum and screen bounds;
- traverse Hi-Z cells from a footprint-selected mip;
- descend conservatively on a possible intersection and advance/ascend on an
  empty cell;
- terminate rays behind the camera or outside the screen;
- refine a mip-zero candidate with four secant iterations;
- calculate view-space thickness from the ray's projected pixel footprint.

Choose the sampled color LOD from GGX cone footprint, hit distance, and
projected pixel size. The CPU mirror implementation is normative for reverse-Z
inequalities, boundary behavior, and refinement tests.

For admitted rough ray queries, use a temporally rotated Heitz GGX VNDF sample
seeded by receiver identity, pixel, temporal sample index, and lobe ID.
Roughness at or below 0.06 remains a deterministic mirror direction.

For anisotropy:

- convert the rotated world tangent into an azimuth around a canonical frame
  constructed from the shading normal;
- decode that azimuth in the deferred pass;
- calculate `alphaX` and `alphaY` using the glTF anisotropy convention;
- use the oriented anisotropic VNDF and cone footprint for SSR, ray queries,
  DDGI direction selection, and environment fallback.

Trace clearcoat as a second lobe with its own normal, roughness, source,
confidence, SSR result, and optional ray task. Store base radiance in raw
radiance and clearcoat radiance transiently in current history. Ray task records
carry their lobe ID and packed extension parameters, allowing at most one writer
per pixel and lobe.

Gather compact DDGI base and clearcoat cohorts before geometric tracing. The
active-tile combine stage performs source fallback, BRDF evaluation, and
energy-conserving layer composition. Remove the Adaptive path's existing
environment-only clearcoat addition from forward shading. Leave Legacy
unchanged. Sheen remains an analytic broad lobe because it is not a geometric
mirror direction.

### 3. Improve ray-hit lighting

At a committed reflection hit, evaluate:

- hit-material emissive radiance;
- every directional light;
- two local-light-tree samples at High and four at Ultra;
- exact visibility for each selected light;
- diffuse DDGI;
- bounded environment or directional-DDGI specular.

Use the existing DDGI light-tree sampler and its PDF compensation. If its state
does not match the current light buffer, scan only the same bounded number of
stable local-light entries and publish a fallback reason; never scan all local
lights merely to select a bounded subset.

Do not recurse into SSR, ray queries, planar captures, or local-probe sampling
at the hit. The hit shader returns a bounded one-bounce outgoing radiance.

### 4. Make temporal and spatial work adaptive

The active-tile combine and temporal pass must:

- validate four bilinear history taps independently;
- decode compact identity, depth, normal, source, age, and sparse state;
- consult source-scoped invalidation bits;
- derive the previous first moment from history luminance and read variance
  from `R16F`;
- retain the existing cadence/ray-budget sparse-history limits;
- write combined base-plus-clearcoat temporal radiance and confidence.

Use variance and receiver roughness to schedule zero, one, or two edge-aware
atrous spatial iterations at High. The first writes the dead previous-history
image; the second writes raw radiance. Composite selects temporal, one-pass, or
two-pass output per tile. Never write a filtered value back into authoritative
temporal history.

### 5. Implement automatic planar reflections

Add deterministic planar evidence to cooked and runtime primitive profiles.
For every non-degenerate triangle, accumulate an area-weighted local plane and
projected two-dimensional bounds. Reject evidence when:

- the mesh is skinned or otherwise deforming;
- no positive-area triangle remains;
- any triangle normal has `abs(dot)` below 0.9995 against the fitted plane;
- any contributing vertex is farther from the plane than
  `max(0.0005 m, local AABB diagonal * 1e-4)`.

Orient the stored normal deterministically by making its largest absolute
component positive. Store local plane, tangent basis, bounds, surface area,
maximum deviation, and evidence validity.

At scene-build time, automatically consider rigid visible instances with valid
evidence when their resolved material is:

- `WaterSurface`;
- explicitly `Mirror`; or
- a generic material with known physical/cooked mean roughness no greater than
  0.18 and maximum F0 at least 0.02.

When texture statistics are missing or incomplete, only water and mirror
semantics qualify. Require projected coverage equivalent to 4096 pixels at
1080p, scaled with output resolution.

Transform candidates into world planes, then cluster planes whose normals have
`abs(dot) >= 0.9995` and whose signed offsets differ by no more than the greater
of 1 cm or 0.1% of their world diagonal. Rank clusters by projected pixels,
view Fresnel, gloss, semantic priority, distance, and stable identity. Water is
ranked before an otherwise equal mirror, and explicit mirrors before generic
glossy planes.

Quality defaults are:

| Tier | Maximum captures | Preferred linear scale |
| --- | ---: | ---: |
| Low | 1 | 0.25 |
| Medium | 1 | 0.25 |
| High | 1 | 0.50 |
| Ultra | 2 | 0.50 |

The memory planner may reduce scale through 0.375 and 0.25, but never exceed
the tier's capture count.

For each selected cluster, reflect the main camera across the world plane and
render after current DDGI publication with a reverse-Z oblique clip plane.
Render opaque, masked, sky, direct, emissive, DDGI/environment lighting, sorted
transparency, and weighted OIT. Exclude every receiver identity in the selected
plane cluster and disable SSR, ray-query, planar, and local-probe reflection
recursion inside the capture.

Store:

- an FP16 color-array layer and reflected depth;
- GGX-prefiltered color mips;
- current and previous reflected view-projection matrices;
- plane basis, receiver bounds and identities;
- capture generation, age, and confidence.

Render immediately after selection, camera cuts, candidate/material/transform
changes, or a scene-dirty region intersecting the reflected frustum. On skipped
frames, depth-reproject the previous capture into the current reflected camera.
Stable captures may be reused for at most four frames; a dynamic or dirty
capture for at most one frame. Fill reprojection holes from DDGI/environment and
lower confidence.

A receiver matches only when identity, plane distance, normal, and projected
bounds agree. Fade the outer two capture texels. Missing, rejected, stale, or
memory-denied planar data continues to SSR or the relevant analytic fallback.

### 6. Optimize transparent reflections without changing layering

Share the new planar lookup, hierarchical SSR, cone color sampling, ray-hit
shading, clearcoat, and anisotropy helpers with transparent shader variants.
Keep the current sorted-alpha and weighted-OIT composition order exactly.

Replace per-fragment global reservations and diagnostic atomics with subgroup
allocation:

1. calculate each lane's requested count;
2. calculate subgroup sum and exclusive prefix;
3. let one elected lane reserve the subgroup total with one atomic;
4. broadcast the base index;
5. determine each lane's exact admission from base plus prefix;
6. aggregate admitted, rejected, reserved, actual, hit, and miss counters in
   the same manner.

Use prior completed request counts to choose a deterministic hash threshold for
the current frame. This distributes bounded SSR and ray work across the whole
image instead of allowing early draw order to consume the budget. Exact
counter identities must continue to satisfy requested equals admitted plus
rejected and actual samples never exceed reservations.

Add nested GPU timestamp scopes around reflection-enabled sorted-transparent
and weighted-OIT draw partitions. Include them, the post-composite snapshot,
and the transparent mip rebuild in the reflection-total diagnostic.

### 7. Add a measured quality controller and optional async compute

Add a `HybridReflectionBudgetController` driven only by completed GPU timing
snapshots. Use:

- EWMA alpha 0.1;
- an 8% dead band around the 3.25 ms High target;
- at most one quality-step change every eight frames;
- at least 32 consecutive under-budget frames before increasing quality;
- immediate one-step reduction after a verified task overflow.

The controller may adjust, in order:

1. low-importance ray admission;
2. the variance threshold for a second spatial iteration;
3. broad, low-F0 tile cadence;
4. low-priority planar resolution and update cadence.

Sharp mirrors, thin transmission, and the selected visible planar receiver keep
protected minimum work. The environment fallback is never disabled. Reset the
controller to a declared state at the start of deterministic capture sequences
and record every requested/effective decision in the manifest.

After compaction is complete, audit queue ownership for classification, compact
DDGI gathering, ray queries, temporal work, and spatial filtering. Enable the
compute queue per device only when a 720-frame graphics-versus-async A/B:

- improves reflection P95 by at least 3%;
- does not regress reflection P99 by more than 1%;
- produces no ownership, semaphore, validation, or publication error.

Otherwise Adaptive stays on the graphics queue and reports the measured async
rejection reason.

## Test and qualification plan

### Automated unit and contract tests

Preserve the current targeted baseline of 55 passing reflection,
mode-resolution, transparent-partitioning, and primitive-profile tests.

Add CPU/GLSL mirror tests for:

- receiver ABI v5 and lobe-extension packing;
- compact history packing, quantization, source validity, age saturation, and
  identity/depth/normal collision rejection;
- variance reconstruction and sparse-history behavior;
- reverse-Z hierarchy traversal, off-screen clipping, thickness, mip
  transitions, and secant refinement;
- GGX VNDF statistics, anisotropic axes, tangent rotation, and deterministic
  seeds;
- clearcoat/base energy conservation and white-furnace behavior;
- tile classification, indirect counts, bounded overflow, and carry-forward;
- source-scoped invalidation and event retention;
- subgroup reservation accounting;
- budget-controller hysteresis and quality floors;
- settings, profile, and cooked-package migration.

Planar evidence tests cover perfect planes, threshold boundaries, reversed
winding, degenerate triangles, disconnected coplanar objects, non-uniform
transforms, skinned rejection, clustering, ranking, clipping, reprojection,
candidate removal, and memory denial.

### Shader and Vulkan validation

Compile and reflect every Legacy/Adaptive, simple/full, C4/C5, transparent,
ray-enabled, and OIT permutation. Validate attachment counts, formats, usage
flags, descriptor layouts, push-constant limits, and indirect-record strides.

Run Vulkan validation with:

- startup and shutdown;
- resize and dynamic-resolution changes;
- camera cuts;
- hot material, texture, and environment reload;
- ray-scene rebuilds and incomplete ray scenes;
- DDGI topology/publication changes;
- zero and multiple planar candidates;
- task capacities forced to their minimum;
- graphics-only and admitted async execution.

### Visual qualification

Add automatically constructed, probe-free scenes for:

- dielectric and metallic roughness/F0 ramps;
- clearcoat-normal and roughness variation;
- rotated anisotropy;
- planar water and opaque mirrors;
- emissive ray hits and dense local lighting;
- foliage and alpha masking;
- thin glass under sorted transparency and weighted OIT;
- moving objects, camera disocclusion, rapid motion, and environment changes.

The harness must assert that none of these scenes contains a `ReflectionProbe`
or manually authored planar helper.

Import hash-addressed, approved path-traced HDR references with locked cameras,
settings, source assets, and reference manifests. Require:

- targeted reflection aggregate FLIP mean and P95 at least 10% better than
  Legacy;
- no targeted ROI more than 2% worse than Legacy;
- unchanged-control relative RMSE no greater than 0.005;
- unchanged-control FLIP P95 no greater than 0.02;
- unchanged-control mean shift no greater than 0.02 and P95 shift no greater
  than 0.03;
- white-furnace energy error no greater than 1%;
- disocclusion ghost residual no greater than 2% after two frames;
- 32-frame static temporal variance no worse than Legacy and a temporal mean
  closer to the reference.

### Performance and rollout qualification

Extend the Bistro 720-frame sequence so the reflection total includes:

- planar capture, reprojection, and prefiltering when active;
- both HDR mip builds;
- classification and compaction;
- SSR, ray queries, DDGI cohorts, combine, temporal, and spatial work;
- opaque composite and snapshot;
- sorted-transparent and weighted-OIT reflection scopes.

Reject a run for missing timings, counter inconsistency, overflow, invalid
readback, unmeasured active work, memory above 160 MiB, P95 above 3.25 ms, or
P99 above 4.25 ms.

Roll out in three stages:

1. land `Adaptive` behind explicit selection while `Auto` remains `Legacy`;
2. promote `Auto` only after approved NVIDIA, AMD, and Intel artifacts pass the
   same visual, performance, memory, and validation gates;
3. retain `Legacy` for one release, removing it only after production telemetry
   and repeat qualification reveal no unresolved regression or fallback.

## Implementation assumptions

- The current dirty worktree is authoritative. Preserve its deferred reflection
  pipeline initialization and C5 publication changes; do not reset or overwrite
  unrelated user changes.
- Existing local probes remain readable as fallback, but this plan performs no
  new probe authoring, capture, baking, clustering, cubemap-path, or probe-only
  qualification work.
- Exact transparent layering, staged rollback, and cross-vendor evidence are
  mandatory defaults.
- A capability or memory failure degrades automatically through the existing
  source chain and remains visible in diagnostics. It never asks the user to
  place a replacement helper.
