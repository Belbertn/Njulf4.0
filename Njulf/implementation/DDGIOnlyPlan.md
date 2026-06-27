# Recommendation

Create a true **`DdgiHigh` production profile** as the default:

* DDGI for diffuse global illumination.
* GTAO for contact-scale occlusion.
* Reflection probes/SSR for specular indirect light.
* No SSGI passes, attachments, histories, or allocations.
* Four lightweight camera-relative clipmaps plus streamed, high-density authored volumes for rooms.
* A stable frame-(N) cache used for shading while a bounded update builds data for frame (N+1).

This matches established DDGI production structure: trace probe rays, update probes, relocate, classify, optionally measure variability, then query irradiance. NVIDIA’s integration guidance also recommends using the geometric normal and selecting overlapping volumes by density, proximity, screen coverage, and artist priority. ([GitHub][1])

DDGI is inherently a low-frequency diffuse signal, so removing SSGI should **not** mean removing AO or the separate reflection system. NVIDIA explicitly recommends complementary AO for the missing high-frequency detail. ([GitHub][2])

Do not delete SSGI immediately. Keep it as a non-default diagnostic or compatibility backend until the DDGI-only validation suite passes, then exclude it from production builds if desired.

---

# Current `Simplified` branch assessment

The branch already has a strong foundation:

* Camera-relative, ring-buffered clipmaps with scroll, fast-movement and teleport handling.
* Authored volumes, camera clipmaps, dirty regions, view-priority information and overlap blending.
* Scheduling for new cells, dirty bounds, visible probes, safety refresh and age refresh.
* Ray-query material resolution including albedo and emissive textures, direct lighting, visibility and recursive cached diffuse.
* Probe classification, relocation, visibility moments, debug overlays and several deterministic layout/scheduler tests.
* SSGI pass execution is already gated by `EffectiveUseSsgi`, making a clean DDGI mode feasible.

However, the current implementation has several production blockers.

## Highest-priority blockers

### 1. Per-cascade ray counts are currently ineffective

`BuildVolumes()` aggregates the maximum rays-per-probe across all volumes. `DdgiUpdatePass` uploads that single value, and `ddgi_update.comp` uses it for every scheduled probe. Consequently, a far cascade configured for 32 or 48 rays can still trace the maximum used by cascade 0.

The sample profile permits 4,096 probe updates and 128 rays in cascade 0. On the first frame, the adaptive scheduler has no previous timing and returns the full hard maximum. That permits **524,288 primary probe rays before hit-lighting rays**.

### 2. Hit lighting has potentially multiplicative ray cost

Every primary DDGI hit iterates as many as 64 lights. Each applicable light launches another visibility ray. Cost can therefore behave approximately like:

[
\text{probe updates}\times\text{probe rays}\times\text{relevant lights}
]

This is unsuitable as the default hit-shading model for a laptop RTX 3060.

### 3. Ray distance is forced to the volume diagonal

Camera clipmaps set `MaxRayDistance` to their diagonal, and `EffectiveMaxRayDistance()` takes the maximum of the authored distance and volume length. This prevents using deliberately short rays in large, coarse cascades and makes far-cascade traversal unnecessarily expensive.

### 4. Recursive GI copies the entire cache before every update

`DdgiUpdatePass` snapshots all probe state, irradiance and visibility buffers before dispatch. The manager therefore maintains full current and recursive copies and performs full-buffer copies even when only a few hundred probes change.

The atlas budget also counts only one atlas set, while the manager allocates two. At the configured maximum of 49,152 probes, those two atlas sets occupy approximately **144 MiB before probe state, update queues and acceleration structures**, despite the nominal 128 MiB atlas budget.

### 5. DDGI-only still allocates the entire SSGI target set

Render-target creation is controlled by `GlobalIllumination.Enabled`, rather than `EffectiveUseSsgi`. Thus a DDGI-only profile still creates the SSGI source, raw result, filtered result, hit distance, paired color/depth/normal/moment/history-length histories, and full-resolution final buffer.

### 6. The TLAS is rebuilt and re-uploaded every frame

The acceleration-structure manager recollects static opaque instances, uploads all instance metadata and performs a full TLAS build each enabled frame. It currently includes only opaque, non-skinned, non-decal meshes. Alpha-masked foliage, transparent geometry and skinned objects are absent from DDGI visibility.

### 7. Large-world local-volume streaming would cause cache churn

Authored volumes are currently accepted in scene order before camera clipmaps consume the remaining probe budget. This can starve clipmaps. Changes in the volume layout also participate in the persistent-resource signature and can trigger clearing of the entire probe cache.

### 8. Foliage and VFX are not ready for a DDGI-only renderer

The foliage shader uses hard-coded directional lighting and writes a contribution for SSGI, but it does not receive DDGI. The particle shader is effectively unlit/emissive and does not receive cached ambient lighting.

### 9. Existing performance evidence is not sufficient

The committed benchmark captures only one measured frame and has no valid GPU timestamp result. The earlier baseline has the same GPU-timing limitation. No present DDGI timing should be treated as representative of the Legion 5 target.

---

# Target production architecture

```text
Frame N shading
 ├─ GTAO / bent-normal contact occlusion
 ├─ select at most:
 │    1 authored local volume
 │    2 neighbouring clipmap cascades
 │    environment fallback
 ├─ query stable DDGI atlas using geometric normal
 └─ direct + DDGI diffuse + reflection system + emissive

Frame N cache update for use by frame N+1
 ├─ collect dirty events
 ├─ build a ray-cost-bounded update list
 ├─ trace compact probe ray data
 ├─ blend only updated probe tiles
 ├─ relocate/classify updated probes
 ├─ update variance, age and confidence
 └─ publish completed cache
```

Using the previous stable cache during tracing and committing updates only after all recursive reads have completed eliminates the need for a full recursive snapshot. A one-frame lighting latency is acceptable for the base architecture and substantially simplifies synchronization.

---

# Step-by-step implementation plan

## Step 0 — Establish a trustworthy baseline

**Files**

* `SampleBenchmarkRunner.cs`
* `SampleBenchmarkReport.cs`
* `PerformanceSnapshotWriter.cs`
* `SampleGlobalIlluminationValidation.cs`
* `RendererDiagnostics.cs`

**Work**

1. Add a dedicated `ddgi-production` benchmark profile at 1920×1080, VSync off.
2. Warm up for at least 300 frames and record 600–1,000 frames.
3. Require valid GPU timestamp samples before accepting a report.
4. Record separate values for:

   * TLAS build/update.
   * DDGI trace.
   * Probe blend/update.
   * Relocation/classification.
   * DDGI gather contribution inside forward shading.
   * CPU scheduler.
   * Primary ray count and visibility-ray count.
   * Updated probes by cascade and reason.
   * Active probe count and resident bytes.
   * Cache copies and transferred bytes.
   * Resource recreation and `WaitIdle` counts.
5. Capture stationary, walking, sprinting, teleport, moving-light and streaming cases.
6. Run a sustained thermal test on the actual Legion 5 rather than using desktop-GPU results.

**Gate**

No optimization result is accepted without valid multi-frame p50, p95 and maximum GPU timings.

---

## Step 1 — Add a real `DdgiHigh` mode

**Files**

* `RenderSettings.cs`
* `SamplePlazaGlobalIllumination.cs`
* `SampleGlobalIlluminationValidation.cs`
* `GlobalIlluminationPassExecutionPolicy.cs`
* `VulkanRenderer.cs`

**Work**

Add a profile rather than scattering values through samples:

```csharp
GlobalIlluminationPreset.DdgiHigh
```

Its mandatory configuration should be:

```text
Enabled                       true
Mode                          Ddgi
UseSsgi                       false
UseDdgi                       true
UseRayQueryBackend            true
DdgiProbeClassification       true
DdgiProbeRelocation           true
DdgiCameraRelative            true
DdgiAsyncCompute              false initially
IndirectIntensity             1.0
```

Enforce configuration validity:

* Dynamic `Ddgi` mode requires ray-query support and an active RT scene.
* Alternatively, it requires a loaded static probe-cache asset.
* Do not silently run with an uninitialized DDGI cache.
* Unsupported hardware falls back to baked probe data or IBL + GTAO, not automatically to SSGI.

The current default is still hybrid, SSGI-enabled and ray-query-disabled, so this needs to be an explicit production profile rather than a small settings change.

**Gate**

The default sample starts with DDGI-only and receives nonzero dynamic irradiance without command-line overrides.

---

## Step 2 — Remove SSGI’s runtime and memory footprint

**Files**

* `RenderTargetManager.cs`
* `VulkanRenderer.cs`
* `ProductionRenderPipelineDeclaration.cs`
* `ForwardPlusPass.cs`
* `PipelineObjects/MeshPipeline.cs`
* `PipelineObjects/FoliagePipeline.cs`
* `forward.frag`
* `foliage_forward.frag`

**Work**

1. Introduce:

```csharp
bool needsSsgiResources = settings.GlobalIllumination.EffectiveUseSsgi;
```

2. Use it for render-target creation instead of `GlobalIllumination.Enabled`.
3. In DDGI-only mode, do not create:

   * `SsgiTraceSource`
   * `SsgiRaw`
   * `SsgiHitDistance`
   * `SsgiFiltered`
   * all SSGI histories
   * `GiFinalDiffuse`
4. Compile opaque and foliage pipeline variants with only one color output when SSGI is disabled.
5. Remove the `SsgiTraceSource` MRT write from the DDGI-only forward path.
6. Keep motion vectors only when TAA or another enabled feature needs them.
7. Have the production render graph omit inactive SSGI resources entirely rather than registering full-sized unused resources.

**Gate**

A DDGI-only capture contains no SSGI passes, descriptors, barriers or full-sized SSGI images. GI render-target memory should fall close to zero because DDGI is gathered directly in forward shading.

---

## Step 3 — Fix per-volume ray work and ray distances

**Files**

* `GlobalIlluminationProbeVolumeData.cs`
* `DdgiUpdatePass.cs`
* `ddgi_update.comp`
* `DdgiProbeUpdateScheduler.cs`
* `DdgiFrameLayout.cs`

**Work**

1. Resolve rays per probe from each volume’s `RayAndUpdateParams.x` in the shader.
2. Keep the push-constant ray value only as a maximum allocation bound.
3. Track actual scheduled rays:

[
W = \sum_{\text{updated probe }p}\text{raysPerProbe}(p)
]

4. Budget scheduler work in ray units, not probe units.
5. Add a separate maximum visibility-ray budget.
6. Replace the no-history scheduler behavior. The first frame must use a conservative cold-start budget, not the hard maximum.
7. Add explicit per-cascade settings:

```csharp
DdgiCascadeProfile
{
    ProbeCount,
    Spacing,
    RaysPerProbe,
    MaxRayDistance,
    TargetRefreshFrames,
    Priority
}
```

8. Stop forcing `MaxRayDistance` to the volume diagonal.
9. Use a ray distance appropriate to transport scale, independent of clipmap extent.

**Gate**

GPU counters show different actual ray counts for near and far cascades. Changing cascade 3 from 16 to 32 rays doubles only cascade 3’s ray work.

---

## Step 4 — Split tracing from probe-cache updates

**Files**

* Replace `DdgiUpdatePass.cs` with:

  * `DdgiTracePass.cs`
  * `DdgiBlendPass.cs`
  * `DdgiRelocateClassifyPass.cs`
  * optional `DdgiVariabilityPass.cs`
* Replace/split `ddgi_update.comp`
* `DdgiProbeVolumeManager.cs`
* `ProductionRenderPipelineDeclaration.cs`
* `GPUStructs.cs`
* `BindlessIndexTable.cs`

**Work**

1. Allocate a compact probe-ray scratch buffer sized to the maximum scheduled work, not all active probes.
2. `DdgiTracePass` writes:

   * radiance.
   * hit distance and squared distance.
   * front/backface state.
   * relocation evidence.
3. Recursive hit shading reads the stable atlas.
4. After all tracing finishes, `DdgiBlendPass` updates only scheduled probe tiles.
5. Relocate and classify after blending.
6. Store variability/luminance change per probe for adaptive scheduling.
7. Remove:

   * recursive probe-state buffer.
   * recursive irradiance atlas.
   * recursive visibility atlas.
   * full cache copies.
8. Initialize visibility data on the GPU or define uninitialized probes through state flags. Avoid generating and uploading a potentially tens-of-megabytes CPU initialization payload.
9. Consider converting the atlas buffers to image atlases with guard texels and hardware bilinear filtering. NVIDIA’s reference query path uses irradiance/distance atlas SRVs and a bilinear sampler. ([GitHub][1])
10. Separate **allocated capacity** from **active volume layout** so volume streaming cannot clear the entire pool.

**Gate**

* Zero full-atlas copies in a steady frame.
* Cache update bandwidth scales with updated probes.
* Changing active local volumes clears only their assigned ranges.
* Resident probe-cache memory accurately matches diagnostics.

---

## Step 5 — Bound hit-shading cost

**Files**

* `ddgi_trace.comp`
* `LightManager.cs`
* `GPUStructs.cs`
* `MaterialManager.cs`
* `SceneDataBuilder.cs`

**Work**

### Direct lights

Build a world-space light structure suitable for arbitrary ray hits:

* Deterministically evaluate the primary directional light.
* Select one local light using a spatial grid or alias table.
* Optionally select a second local light only for high-priority interior volumes.
* Trace at most one visibility ray per selected source.
* Use unbiased or energy-normalized importance weighting.
* Store light-selection diagnostics.

This changes hit lighting from “loop over every light” to bounded work.

### Material evaluation

Current ray hits sample albedo and emissive textures at LOD 0.

Add compact DDGI material metadata:

* Average diffuse albedo.
* Average emissive radiance.
* Emissive importance.
* Alpha policy.
* Optional coarse texture mip index.

Use the compact values for coarse cascades. Fine and interior volumes may use a ray-cone-derived mip rather than LOD 0.

### Environment misses

Replace the hard-coded blue gradient with the actual active environment cubemap or procedural sky evaluated in the ray direction. The current pass constructs a fixed approximate RGB value.

**Gate**

DDGI trace time grows approximately linearly with primary ray count and remains stable when scene light count grows from 8 to 128.

---

## Step 6 — Productionize acceleration structures

**Files**

* `AccelerationStructureManager.cs`
* `VulkanRenderer.cs`
* `MeshManager.cs`
* scene/world-streaming systems

**Work**

1. Split geometry into:

   * static world.
   * moving rigid instances.
   * optional deforming proxies.
2. Build static BLAS once and compact them.
3. Reuse BLAS for instanced meshes.
4. Use TLAS update/refit when transforms change but membership and capacity remain stable.
5. Rebuild only when instance membership or required capacity changes.
6. Double-buffer TLAS instance and metadata buffers to avoid CPU/GPU ownership stalls.
7. Partition large terrain and buildings into streaming chunks.
8. Keep RT chunks covering:

```text
camera clipmap coverage
+ maximum active DDGI ray distance
+ one streaming safety margin
```

9. Add ray masks:

   * static opaque.
   * dynamic rigid.
   * alpha-tested/proxy foliage.
   * optional character proxy.
10. For alpha-masked geometry, choose one production policy:

    * candidate-hit alpha testing.
    * simplified opaque proxy.
    * exclude very fine grass but retain trees and large leaves.
11. Do not add individual particles to the TLAS.

**Gate**

* A static scene performs no TLAS rebuild after warm-up.
* Moving one rigid object performs a TLAS update, not a full BLAS/TLAS rebuild.
* Streaming one chunk does not stall the device.
* TLAS cost is separately visible in diagnostics.

---

## Step 7 — Build the large-world volume system

**Files**

* `RenderSettings.cs`
* `GlobalIlluminationProbeVolume.cs`
* `DdgiFrameLayout.cs`
* `CameraRelativeDdgiClipmapController.cs`
* `DdgiProbeVolumeManager.cs`

**Work**

### Fixed camera clipmaps

Reserve probe slots and volume slots for clipmaps before processing authored volumes. Never let local volumes starve the world cache.

Use anisotropic per-cascade layouts: far cascades need fewer vertical probes than the near cascade.

### Streamed authored volumes

Add production metadata:

```csharp
Priority
BlendDistance
StreamingCellId
Interior
QualityClass
MaxRayDistance
SteadyHysteresis
DirtyHysteresis
UpdatePriority
```

Select active authored volumes using:

1. Current camera containment.
2. Portal/room membership.
3. Distance.
4. Probe density.
5. Screen coverage.
6. Artist priority.

This follows the reference recommendation to sort and select overlapping volumes rather than blindly gathering all volumes. ([GitHub][1])

### Stable pool

* Preallocate the selected profile’s maximum probe pool at startup.
* Reserve stable ranges for the four clipmaps.
* Use a free-list for local-volume slots.
* Reassigning one local slot initializes only that range.
* Separate allocation signature from current world position and volume content.
* Keep world coordinates as large-world cells plus camera-relative float positions for rendering and ray tracing.

### Gather optimization

Do not loop over as many as 16 volumes per pixel. Build a per-tile or per-cluster list containing:

* zero or one local authored volume.
* current clipmap.
* adjacent clipmap for transition blending.

**Gate**

Travel several kilometres, cross streaming cells and enter/exit interiors with:

* zero whole-cache reinitializations.
* zero steady-state `WaitIdle`.
* bounded active probe count.
* no visible clipmap or room-volume seam.

---

## Step 8 — Add a small-room quality path

A small room should not force the global clipmap to use extremely dense spacing. It should activate a local authored volume.

**Recommended behavior**

* Fit the volume to room bounds plus a modest blend margin.
* Use approximately 0.4–0.75 m spacing.
* Limit ray distance to the room scale, commonly 8–15 m.
* Give it higher priority than camera clipmaps.
* Use 32 steady rays and up to 48–64 rays only while dirty.
* Blend to the fine clipmap at doors, windows and exterior boundaries.
* Reset or sharply lower hysteresis when the room volume is first activated.
* Use the **geometric normal** for cache querying and surface bias, not the normal-mapped shading normal. This is specifically recommended by NVIDIA’s integration guidance. ([GitHub][1])

Add an authoring validator that detects:

* probes initially inside geometry.
* insufficient free-space coverage.
* probes too close to thin walls.
* excessive overlap.
* invalid blend margins.
* room volumes exceeding their probe quota.

For thin walls:

* retain visibility moments.
* scale normal/view bias with probe spacing.
* enable relocation and classification.
* use conservative two-sided or thickened RT proxies where the rendered wall has effectively zero thickness.
* add a leak-clamp response based on low-confidence visibility.

**Gate**

Cornell-room and thin-wall validation must pass from multiple camera positions, including looking through a doorway into a bright exterior.

---

## Step 9 — Make dynamic changes production-safe

**Files**

* `VulkanRenderer.cs`
* `DdgiFrameLayout.cs`
* `DdgiProbeUpdateScheduler.cs`
* scene object/light/material managers

**Work**

Replace aggregate signatures with per-source tracking:

* Per-light previous and current bounds.
* Per-object previous and current bounds.
* Per-material revision and reverse map to using objects.
* World-chunk stream-in and stream-out events.
* Emissive-source revision.
* Destruction/building events.

Use explicit dirty reasons:

```text
NewCell
GeometryAdded
GeometryRemoved
TransformChanged
MaterialChanged
EmissiveChanged
LocalLightChanged
DirectionalLightChanged
StreamIn
StreamOut
Teleport
AgeRefresh
```

Apply reason-specific history handling:

| Reason                 | Suggested history behavior |
| ---------------------- | -------------------------- |
| New/teleport           | Reset                      |
| Geometry removed       | Reset affected probes      |
| Geometry added/moved   | Very low hysteresis        |
| Emissive/local light   | Medium hysteresis          |
| Directional sun change | Progressive cascade update |
| Age refresh            | Normal stable hysteresis   |

The current shader resets only new-cell and teleport history; generic dirty probes otherwise retain a 0.985-style history and can react very slowly.

**Gate**

Moving, spawning and deleting an object affects both its old and new regions without dirtying the complete scene.

---

## Step 10 — Complete emissive, foliage and VFX integration

### Emissives

Ray-hit emission already works for opaque surfaces, but small emissive sources are unlikely to be hit by sparse probe rays.

Add an emissive-light table:

* Extract emissive triangles or artist-authored emissive proxies.
* Cluster them spatially.
* Sample one emissive source by importance during probe-hit shading.
* Mark nearby probes dirty when emissive intensity changes.
* Promote tiny, important emitters to analytic area/point lights.

### Foliage

Make foliage receive DDGI while retaining wrap/backlight:

* Sample DDGI once per foliage cluster, meshlet or vertex and interpolate.
* Avoid a full eight-probe query for every grass fragment.
* Use the bent foliage normal for appearance but a stable geometric/clump normal for DDGI lookup.
* Add trees and major leaf cards to RT visibility through proxies.
* Exclude individual grass blades from RT geometry.

### Particles and VFX

* Sample ambient DDGI once per emitter, beam segment or particle batch.
* Feed it as an interpolated ambient term.
* Represent flames, explosions and glowing particle groups with analytic proxy lights.
* Mark DDGI dirty only for sustained effects.
* Keep short sparks and muzzle flashes direct-only unless bounced light is a key visual.
* Fog and smoke should receive lighting from a coarse froxel/DDGI sample, not become probe-ray geometry.

**Gate**

A fire or glowing VFX emitter lights nearby geometry, while particle count has no material effect on TLAS or DDGI traversal cost.

---

## Step 11 — Replace the scheduler’s full scan and sort

The current frustum scheduler scans every active probe, allocates/rents candidate storage and sorts it. At tens of thousands of probes this is unnecessary CPU work.

**Work**

Maintain persistent queues per cascade and reason:

```text
Uninitialized
DirtyGeometry
DirtyLighting
VisibleNear
VisibleFar
Safety
AgeRefresh
```

Use a bounded top-(K) heap or fixed buckets instead of sorting every probe.

Score candidates using:

```text
dirty reason
volume priority
cascade
distance
frustum/guard-band membership
age
variance
camera velocity prediction
estimated ray cost
```

Reserve a small percentage for far-cascade and out-of-frustum refresh to prevent starvation.

Use an EWMA of GPU cost per ray category rather than scaling probe count from one previous total pass time.

**Gate**

CPU DDGI scheduling stays below roughly 0.25 ms p95 at the maximum supported pool size.

---

## Step 12 — Add variability and adaptive quality

Use per-probe luminance variance, update age and confidence:

* Stable probes receive fewer updates.
* New, dirty or high-variance probes receive more.
* Far cascades converge over longer periods.
* A minimum refresh interval prevents permanent starvation.
* Adapt update count first; never change the clipmap layout during gameplay.
* Layout/profile changes occur only during loading screens or explicit quality-setting changes.

The official DDGI integration exposes variability as the mechanism for deciding whether volumes need continued work. ([GitHub][1])

Do not enable “async compute” by default yet. The current renderer explicitly reports that it has no dedicated asynchronous compute queue and that queue transitions are diagnostic-only.

A later async implementation should use:

* a real compute-capable queue.
* timeline semaphores.
* explicit ownership transitions where needed.
* frame-(N+1) cache publication.
* profiling to verify overlap rather than contention.

---

# Initial `DdgiHigh` profile for the RTX 3060 laptop

These values are starting points for measurement, not claimed final performance.

| Volume             | Probe layout |    Spacing |            Rays/update | Max ray | Steady updates/frame |
| ------------------ | -----------: | ---------: | ---------------------: | ------: | -------------------: |
| Clipmap 0          |     24×10×24 |     0.75 m |                     32 | 10–12 m |                   96 |
| Clipmap 1          |      24×8×24 |      1.5 m |                     24 | 20–24 m |                   48 |
| Clipmap 2          |      24×6×24 |      3.0 m |                     16 | 36–48 m |                   24 |
| Clipmap 3          |      20×4×20 |      6.0 m |                     16 | 64–80 m |                   12 |
| Active room volume |  room-fitted | 0.4–0.75 m | 32 steady, 48–64 dirty |  8–15 m |      priority-driven |

The four clipmaps total 15,424 probes. With the current no-border payload of 1,536 bytes per probe, one atlas set is approximately 22.6 MiB. A fixed 24,576-probe pool would leave room for local volumes without approaching the current 49,152-probe default.

Recommended profile-wide limits:

```text
Maximum active probes           24,576
Maximum active volumes          8 normally, hard capacity 16
Steady primary ray target       ~5,000/frame
Burst primary ray ceiling       ~12,000/frame
Local/emissive samples per hit  1
Primary directional lights      1 deterministic
DDGI update target              ≤2.0 ms typical
DDGI update hard p95 target     ≤2.5 ms
Probe-cache target              ≤64–96 MiB
Async compute                   Off
Classification/relocation       On
GTAO                            On
Indirect intensity              1.0
```

The important limit is the measured GPU time and visibility-ray count, not a fixed number of probes.

---

# Production acceptance gates

## Visual

* Thin-wall leakage ≤3% relative luminance, retaining the existing proposed threshold.
* No visible room/clipmap transition greater than approximately 5% luminance.
* No NaN/Inf HDR pixels.
* No persistent bright-probe or black-probe contamination.
* Stable stationary luminance variation ≤2%.
* Bright exterior viewed from a dark room remains stable.
* Characters, foliage and transparent objects receive plausible ambient diffuse light.
* Small emissive lights visibly affect nearby probes.

## Responsiveness

* Near-volume moving light reaches 80% of its settled result within approximately six frames.
* Moving rigid geometry recovers within roughly eight frames near the camera.
* Teleport produces usable near-field lighting within eight frames and continues refining without a whole-cache clear.
* Far cascades may converge more slowly but must not retain obsolete lighting indefinitely.

## Performance on the actual Legion 5

* 1920×1080, sustained p95 GPU frame ≤16.6 ms for the 60 fps target.
* DDGI update p95 ≤2.5 ms.
* TLAS update p95 ≤1.0 ms in ordinary dynamic scenes.
* CPU scheduler p95 ≤0.25 ms.
* No device-idle waits, resource reallocations or staging overflow during normal traversal.
* No first-frame half-million-ray burst.
* DDGI cache memory remains within its real resident-byte budget.
* Acceleration-structure memory is reported separately.
* At least 1,000 valid measured frames after warm-up.
* Thirty-minute thermal-soak run without progressive frame-time degradation.

## Large-world stability

* Multi-kilometre traversal without resource-signature reinitialization.
* Repeated chunk stream-in/out without stale radiance.
* Camera sprint, vehicle speed, backtracking and teleport.
* Origin rebasing without clipmap address discontinuity.
* No old-world lighting after a streamed chunk is removed.

---

# Recommended delivery order

Implement this as twelve reviewable pull requests in the order above. The first four are mandatory before spending time tuning probe counts:

1. Trustworthy benchmark.
2. Real DDGI-only preset and SSGI resource removal.
3. Per-volume rays, bounded distances and cold-start cap.
4. Split trace/update pipeline and remove full-cache snapshots.

After those, optimize hit lighting and acceleration structures, then add large-world streaming, interior volumes, dynamic invalidation, foliage/VFX integration and adaptive scheduling.

The current branch has enough DDGI infrastructure to evolve into a production renderer. The largest risk is not DDGI quality itself; it is the present unbounded work multiplication, duplicated cache operations and unstable large-world resource model.

[1]: https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Integration.md "https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Integration.md"
[2]: https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Algorithms.md "https://github.com/NVIDIAGameWorks/RTXGI-DDGI/blob/main/docs/Algorithms.md"
