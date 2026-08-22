# Production C5/SSGI v2 Implementation Plan

## Summary

Complete C5 as a production, single-bounce diffuse screen-space residual for opaque, alpha-masked, and qualified foliage surfaces.

The finished path will:

- Correct Hi-Z tracing, TAA history, moving-hit validation, reconstruction, B3 frequency separation, foliage coverage, tile compaction, timing, resizing, controls, diagnostics, and qualification.
- Preserve the 0.75 ms P95 RTX 3060-class budget, 96 MiB steady memory, and 192 MiB one-generation hot-swap ceiling.
- Make `AutoQualified` the fresh-install default; missing or mismatched evidence falls back to canonical DDGI+B3.
- Migrate existing settings but reject all existing C5 evidence.
- Qualify per device across RTX 3060-class, current NVIDIA, AMD, and Intel Vulkan hardware.

## Public Contracts and Versioning

- Preserve existing numeric `SimpleDdgiNearFieldResidualMode` values and the legacy experiment alias.
- Add `SimpleDdgiNearFieldResidualQualityPreset` with `Performance`, `Balanced`, and `Quality`; default to `Balanced`.
- Add persisted advanced controls:
  - `SimpleDdgiNearFieldResidualAdvancedOverridesEnabled`
  - `MaximumTraceDistanceMeters`, clamped to 2–16 m
  - `RaysPerPixel`, clamped to 1–4
  - `FilterIterationCount`, clamped to 0–4
  - `Intensity`, clamped to 0–2
- Advance settings serialization to v14. Preserve authored modes during migration, initialize missing quality fields to Balanced, and clear stale C5 qualification IDs.
- Change fresh defaults and production sample profiles to `AutoQualified`; deterministic validation/CLI profiles may explicitly select `HiZAdaptive`.
- Advance:
  - C5 GPU ABI to V12 (`0x4335000C`)
  - Forward source semantic version to 4
  - C5 evidence ABI to V6 (`0x43350106`)
  - Diagnostics contract to v3
  - Runtime evidence bundle to schema 2
- Replace the single evidence document with a bounded list of device/profile/tier qualifications. Schema-1 bundles remain readable only as a clear “obsolete evidence” diagnostic and are never admitted.

Quality profiles are fixed as follows:

| Preset | Full/max distance | Steps | Max rays | Near filters | Allowed scales |
| --- | ---: | ---: | ---: | ---: | --- |
| Performance | 3/6 m | 48 | 1 | 1 | 1/8, 1/4 |
| Balanced | 4/8 m | 64 | 2 | 2 | 1/8, 1/4, 1/2 |
| Quality | 6/12 m | 96 | 4 | 3 | 1/8, 1/4, 1/2 |

All profiles use four mip-zero refinements. Bias and thickness become view-space values scaled by receiver pixel footprint, with 1 mm and 2 cm respective minimums.

## Implementation Changes

### 1. Source ownership, surface identity, and foliage

- Keep one full-resolution `R16G16B16A16_SFLOAT` source:
  - RGB: shadowed direct diffuse plus emissive scene-linear radiance.
  - Alpha: actual local B3/base footprint radius, `0.25 × selected probe spacing`; zero means invalid.
- Redefine the existing 128-bit receiver payload:
  - Geometric and shading normals remain octahedrally encoded.
  - A 16-bit frame-local surface token references a C5 surface table.
  - Per-pixel directional diffuse base uses RGB9E5.
  - Per-pixel dielectric F0 uses RGB565.
- Build a 65,534-entry, frame-buffered surface table containing stable object/material IDs, 16-bit object/material revisions, and coverage/motion flags. Revision wrap advances the global scene generation.
- Remove scene-wide object/material-count and foliage-count disable conditions. Table overflow or an unsupported surface makes only those pixels C5-invalid and increments telemetry.
- Add matching source variants for opaque, masked, authored foliage, and procedural grass. Foliage uses the same alpha test as depth, publishes stable instance/prototype identity, and participates when its motion-vector contract is valid.
- Combined B1/C4/C5 pipeline variants preserve existing attachment locations; foliage writes an invalid C4 payload when only C5 foliage participation is qualified.
- Keep transparent and particle passes outside the source contract.

### 2. Prepare, compaction, and corrected short-ray tracing

Use this pass order:

1. Clear/reset
2. Receiver prepare and tile compaction
3. Optional source-luminance hierarchy
4. Hi-Z trace
5. Temporal reconstruction
6. Near à-trous filters
7. B3 frequency separation
8. Guided full-resolution composite

The prepare pass will:

- Depth-aware downsample each trace footprint by selecting the nearest valid receiver and its matching payload/motion.
- Produce trace-resolution linear depth+B3 footprint, payload, and motion images.
- Clear the current history-write bank and transient outputs.
- Append non-empty 8×8 tiles to an active-tile buffer and write Vulkan indirect-dispatch arguments.
- Build a trace-resolution luminance level for source-guided sampling when the active tier has at least two rays.

Replace the current projected linear march with perspective-correct view-space Hi-Z DDA:

- Reconstruct receiver view position and trace a bounded view-space segment.
- Clip against the near plane and viewport.
- Traverse screen cells using homogeneous reciprocal-W interpolation.
- Compare positive linear view depths, independent of forward/reversed-Z storage.
- Select mips from projected footprint, ascend through empty cells, descend on possible crossings, then refine at mip zero.
- Count each depth test against `MaximumTraceSteps`; remove the separate mip-visit rejection that currently terminates a 64-step trace after eight samples.
- Validate thickness, front/back ordering, depth discontinuity, hit normal, and exact screen bounds in view-space units.
- A miss contributes zero and lowers confidence; it never removes canonical DDGI/B3.

Sampling will use an Owen-scrambled Sobol sequence with an 8×8 blue-noise rank, keyed by C5 sequence index, ray ordinal, stable surface identity, and pixel. TAA jitter and wall-clock time are excluded from the stochastic identity.

For tiers with multiple rays:

- Launch one cosine-weighted ray and alternate remaining rays between cosine and source-guided proposals.
- Build the guided proposal from the screen-source luminance hierarchy.
- Convert texel probability to solid-angle PDF using reconstructed target position, normal, projected pixel area, and distance.
- Evaluate the complete mixture PDF for both cosine and guided samples; reject invalid or occluded target proposals.
- Average launched samples with misses as zero and use valid-ray coverage in confidence.

### 3. Temporal history and dynamic sources

- Change hit metadata to a 48-byte format stored only in the two history banks; trace writes directly into the current write bank, eliminating the separate third metadata allocation.
- Store linear receiver/hit depth, flags, packed hit normal, receiver B3 footprint, receiver/hit object and material IDs, packed object/material revisions, half-precision hit UV, and packed hit-source radiance.
- Split history identity into:
  - Structural projection revision, excluding TAA jitter.
  - Current/previous jittered matrices used for reprojection.
  - Source semantic/layout revision.
  - Scene resource generation and origin-rebase revision.
  - Per-surface and per-hit content revisions.
- Reproject receivers through current motion vectors and gather valid history bilinearly, rejecting individual taps before renormalization.
- Reconstruct the previous hit world position from its UV/linear depth and previous camera constants, project it into the current source, then validate current depth, hit normal, object/material identity, and revisions.
- Treat receiver transform motion as reusable when motion vectors are valid; hit pose changes always reject hit history.
- When `SourceLightingEpoch` changes, compare stored and current hit-source radiance:
  - Up to 5% relative change retains history.
  - 5–25% reduces history weight linearly.
  - Above 25% rejects history.
- Compare current and stored local B3 footprints and reset on incompatible B3 ownership/profile changes.
- Keep variance-aware 3×3 clipping, first/second luminance moments, and a 64-frame maximum history.
- A newly valid or disoccluded sample composites immediately at 25% of trace confidence, rising to full confidence over at most eight stable frames. Composite validity no longer requires `HistoryAccepted`.

### 4. Reconstruction, frequency separation, and upscale

- Replace repeated same-radius bilateral filtering with variance-guided à-trous passes using strides 1, 2, and 4 according to the selected tier.
- Every tap must validate linear depth, both normals, stable surface identity, material revision, validity, and temporal variance.
- Construct:
  - `nearEstimate` from the temporal result and small-support à-trous passes.
  - `lowEstimate` with a fixed-tap bilateral kernel whose outer stride is derived from the projected per-pixel B3 footprint.
  - `bandResidual = nearEstimate - lowEstimate`.
- Within each active 8×8 tile, subtract a confidence-weighted residual mean computed only from taps sharing receiver identity and compatible depth/normal. This prevents truncated kernels from accumulating low-frequency energy across material edges.
- Alias transient raw/filter resources after temporal completion so low estimate and final band do not require permanent extra images.
- Replace nearest-neighbour composite with a 3×3 joint bilateral gather at each full-resolution receiver. Require matching surface/material identity and weight by tent position, linear depth, normals, B3 footprint, confidence, and validity.
- If no compatible low-resolution sample exists, contribute exactly zero.
- Preserve signed brightening/darkening, the existing 20% safety clamp, finite checks, and non-negative final scene colour. C5 never multiplies or occludes canonical DDGI/B3.

### 5. Indirect execution, timing governor, and hot resize

- Dispatch trace, temporal, every filter, frequency separation, and composite with `CmdDispatchIndirect` over the GPU-produced active-tile list.
- Use transfer clears/fills for inactive outputs and arguments so skipped tiles cannot expose stale history.
- Rename diagnostics so candidate, active, compacted, empty, and overflow tile counts reflect actual GPU values. Tile-list overflow invalidates the entire C5 frame and preserves canonical output.
- Include prepare/compaction and frequency-separation timings.
- Measure source-MRT cost through deterministic paired Forward+ C5-source-on/off captures. Evidence stores a conservative source-cost upper bound by extent; runtime totals combine that calibrated cost with live exclusive compute timestamps.
- `AutoQualified` refuses any tier without authoritative source-cost evidence. Explicit developer modes report source cost as non-authoritative and cannot promote beyond their starting tier.
- Select the highest qualified startup tier whose archived total P95 is at most 0.60 ms. Retain the existing 120-sample window and 30-frame evaluation cadence:
  - Demote when live P95 exceeds 0.75 ms.
  - Promote after four windows at or below 0.45 ms.
  - If the lowest tier exceeds budget for two evaluations, suspend C5 for 300 frames before retrying.
- Replace extent freezing and “disable until restart” with a resource-generation transaction:
  - Compile and validate a replacement layout without mutating the active generation.
  - Allocate images, buffers, descriptor sets, and concrete graph bindings for one pending generation.
  - Switch atomically at a frame boundary, advance C5 history/layout epochs, and enqueue the old generation against the greatest referencing frame-fence value.
  - Poll the existing completion-retirement queue and destroy resources only after completion.
  - Allow one active plus one pending/retired generation: 96 MiB steady and 192 MiB peak.
  - Coalesce repeated resize requests to the newest extent. If overlap admission fails, run canonical DDGI+B3 until retirement frees space.
- Evidence binds a tested extent envelope, aspect range, preset, tier, formats, shader hashes, B3 identity, and maximum layout bytes. A live layout is accepted only when it lies wholly inside that envelope; otherwise `AutoQualified` remains off.

### 6. Debugging, evidence, and rollout

- Append C5 debug views without changing existing enum values:
  - Source radiance
  - Raw candidate
  - Near estimate
  - Low estimate
  - Signed residual
  - Final contribution
  - Confidence
  - History length
  - History rejection reason
  - Trace distance/hit validity
  - Tile activity
  - B3 footprint
- Permit C5-owned debug views while keeping unrelated GI debug outputs from contaminating the source. Use a debug-only capture target when transient aliasing would otherwise overwrite the selected intermediate.
- Populate capture identifiers from the benchmark capture identity and reference-manifest identity.
- Publish separate receiver identity, receiver revision, hit identity, hit revision, source-reactive, reprojection, depth, normal, and variance rejection counters.
- Derive runtime capability flags from actual resources, descriptors, pass registration, accepted evidence, and shader validation. Remove unconditional claims of hit validation or measured qualification.
- Update the evidence bundle with bounded per-device tier entries and source-MRT calibration. Old C5 ABI/evidence fingerprints fail closed.
- Keep `AutoQualified` disabled until the new cross-vendor evidence bundle is archived and installed.

## Test and Acceptance Plan

- Extend CPU/reference tests for perspective-correct DDA, full 64-step traversal, reversed-Z, near-plane clipping, thin gaps, depth steps, refinement, view-space bias/thickness, screen exits, Sobol determinism, guided PDFs, and multi-ray aggregation.
- Add source/payload tests proving:
  - DDGI/C4/C5/IBL changes cannot affect source RGB.
  - Actual B3 spacing controls source alpha.
  - Opaque, masked, and qualified foliage use identical coverage semantics.
  - Surface-table overflow invalidates only affected pixels.
- Add temporal sequence tests for TAA jitter, real projection changes, camera cuts, moving receivers, independently moving hits, skinned/foliage motion, light/emissive changes, material revisions, disocclusion, origin rebase, and resize.
- Add reconstruction tests for à-trous strides, variance weighting, B3-sized low support, per-identity mean preservation, signed residuals, guided upsampling, immediate first-frame contribution, and exact-zero invalid output.
- Add indirect-dispatch tests proving zero work for empty tiles, correct active counts/arguments, no stale outputs, and fail-closed overflow.
- Add lifecycle tests covering pending/active/retired generations, resize coalescing, fence ordering, allocation failure, 96/192 MiB enforcement, and no device-idle wait.
- Add settings-v14 migration, preset/override clamping, profile fingerprint, diagnostics-v3, evidence-schema-2, and obsolete-evidence rejection tests.
- Run the full Release solution build, managed/shader suite, and explicit Vulkan integration gate.

Qualification requires:

- At least three reference sequences, 120 frames per sequence, and three independent runs.
- At least 20% post-B3 near-field error reduction and better equal-cost improvement than additional B3 work.
- Absolute signed net residual energy no greater than 1% of absolute residual energy and low-frequency leakage no greater than 2%.
- No stale hit/light contribution beyond the first changed frame and no persistent edge darkening or ghosting.
- C5 total cost, including calibrated source MRT overhead, at or below 0.75 ms P95 and 1.0 ms P99 on every admitted device/tier.
- At most 96 MiB steady and 192 MiB during a single hot swap.
- A 60-minute traversal with stable memory, no retirement growth, no counter overflow, and no non-finite output.
- Evidence from the RTX 3060-class floor plus one current NVIDIA, AMD, and Intel Vulkan device; qualification remains device/driver/toolchain specific.
- Missing evidence, unsupported hardware, invalid content, failed allocation, empty work, or C5 Off produces no active source attachment, C5 dispatch, composite change, or persistent C5 allocation.

## Assumptions

- C5 remains diffuse-only, single-bounce, and screen-space.
- Opaque, masked, and qualified foliage are in scope.
- Off-screen ray-query fallback, glossy/specular GI, transparent receivers, particles, multiple screen-space bounces, ReSTIR reuse, and neural denoising remain explicitly deferred.
- The production source remains `R16G16B16A16_SFLOAT`; packed RGB-only formats cannot carry the required B3/validity metadata.
- C5 stays on the current graphics/compute scheduling path; async-compute ownership is unchanged unless later measurements demonstrate a real overlap benefit.
- The existing C5 runtime is replaced in place rather than retained as a second legacy execution path; CPU references remain for parity and regression testing.
