# Forward-Pass Register and Occupancy Remediation Plan

## Summary

Optimize the production `forward_opaque_ddgi_hybrid_reflection_sparse_lobe.frag.spv` through three isolated candidates: DDGI address reuse, visibility-footprint reuse, and one normal-only shader variant. Preserve rendering quality, shader ABI, runtime settings, and the canonical shader as fallback.

Treat the current dirty worktree as the source baseline without overwriting unrelated changes. Rebuild and bind the supplied profile to the exact shader, pipeline, GPU/driver, specialization mask, and bundle hash before judging candidates; the currently inspected target SHA is `d7e12ab8…`, but it may be stale relative to the working tree.

## Implementation Changes

### 1. Establish a production-exact baseline

- Build Release for iteration and Release plus ShippingPerformance for final qualification using normal shader compilation.
- Capture A/A runs for `bistro-forward-gi-enabled` and `sponza-forward-gi-enabled` at 1920×1080, with pipeline creation completed before measured frames.
- Record ForwardPlusPass and whole-frame p50/p95/p99 plus native live registers, spills/local memory, occupancy, register-limited percentage, ISBE allocation stalls, dependency stalls, SM/VRAM throughput, and storage-load counts.
- Use symbol/profile builds only for attribution; compare performance using production-equivalent binaries.

### 2. Candidate A — reuse DDGI address derivation

- In the DDGI page/address helpers, compute the toroidally wrapped physical coordinate once per accepted corner and derive both local probe and virtual-page indices from it, eliminating the duplicate modulo path.
- Build a compact per-volume atlas-layout context before the eight-corner gather containing validated texel strides, physical range, mirror base/capacity, page-grid strides, and sampled-atlas limits.
- Leave only corner-dependent physical address, group, and layer calculations inside the loop. Keep temporaries short-lived so hoisting does not increase register pressure.
- Retain the existing uniform/aligned bindless helpers in `common.glsl`; reduce their call count at the DDGI call sites rather than changing global descriptor semantics.

### 3. Candidate B — fuse visibility footprint and validity work

- Compute the octahedral footprint for `probeToSurface` once per corner.
- Fuse visibility-moment sampling, Chebyshev evaluation, and optional near-visibility sidecar evaluation into a short-lived helper.
- When the sidecar is active, load the four canonical moment taps once and reuse them for both bilinear visibility and per-tap sidecar validation, eliminating the second octahedral wrap and repeated SSBO moment reads.
- Preserve the existing sampled-image fast path when the sidecar is inactive and keep irradiance sampling separate because it uses the surface normal rather than `probeToSurface`.

### 4. Candidate C — add one normal-only forward module

- Add `forward_opaque_ddgi_hybrid_reflection_normal_sparse_lobe.frag.spv`, compiled from `forward.frag` with `FORWARD_HYBRID_NORMAL_ONLY=1`.
- Compile out debug, provenance, capture, and diagnostic branches already forbidden by normal hybrid-receiver admission. Do not specialize material features, DDGI quality, AO, lights, shadows, far-field fallback, sampled-atlas behavior, or payload semantics.
- Select it only for opaque, sparse-lobe, exact-receiver normal rendering with no C4/C5 or receiver-cache lane and with `StaticShaderSpecialization` enabled. Any failed eligibility or pipeline creation falls back to the existing canonical module.
- Emit no simple-family, C4/C5, cache-lane, or diagnostic permutations. Prewarm the added pipeline and report module count, binary bytes, cache growth, and creation time.

### 5. Candidate discipline

- Implement and profile each candidate separately against the last accepted state. Reject and remove a candidate before testing the next if it misses its gates.
- Inspect native compiler output after every candidate; SPIR-V instruction counts are supporting evidence, not acceptance evidence.
- Do not alter the locked Bistro/Sponza campaign manifest, thresholds, reference images, or harness to admit the candidate. Reuse its workload definitions and gates in isolated candidate runs and retain raw evidence paths and source/binary hashes.

## Interfaces and Compatibility

- No external API, descriptor layout, push-constant, specialization-ID, render-graph attachment, DDGI storage ABI, or hybrid-reflection payload change.
- Keep the existing public shader resolver signature intact. Add an internal normal-exact resolver/eligibility helper used by pipeline selection and exposed to tests through existing internal test access.
- Reuse `StaticShaderSpecialization` as the rollback switch; disabling it restores the canonical artifact without a new public setting.

## Test and Acceptance Plan

- Add address-equivalence tests covering dense, sparse, and shadow residency; positive and wrap-boundary physical offsets; partial page grids; invalid metadata; and overflow guards.
- Add visibility tests covering octahedral edges/corners, interior versus seam sampling, sampled image on/off, near-sidecar on/off, invalid mappings, and parity of moments and final visibility.
- Add selector tests proving only the eligible normal exact path uses the new module and every debug, cache, producer, unsupported-family, disabled-feature, or missing-artifact case uses the canonical fallback.
- Compile all required Release and ShippingPerformance artifacts, run `spirv-val --target-env vulkan1.3`, verify fragment entry point/reflection and unchanged ABI, then create the affected Vulkan pipelines under standard validation. Run GPU-assisted validation separately.
- Run three-cycle ABBA comparisons in both scenes with the existing warmup/measurement trajectories. Keep a candidate only when both scenes satisfy:
  - ForwardPlusPass improvement of at least 5% and 0.05 ms.
  - Whole-frame improvement of at least 1% and 0.10 ms.
  - 95% bootstrap confidence, no tracked CPU/GPU/pass regression above 1%, and no memory-budget/headroom failure.
  - No spill/local-memory regression, plus either a lower native register allocation class or higher occupancy; register-limited and ISBE-stalled percentages must not regress.
- Pass the locked quality gates: relative HDR RMSE ≤ 0.005, FLIP p95 ≤ 0.02, ROI mean/p95 luminance shift ≤ 0.02/0.03, temporal residual within its A/A envelope and ≤ 0.005, no visual artifacts, validation errors, or shutdown failures.

## Assumptions

- The supplied timings describe steady-state ForwardPlusPass work; the `ForwardGiGatherPass` alias is not counted separately.
- Scene resolution, camera trajectories, materials, probe layout, rays, DDGI quality, and all visible features remain unchanged.
- Existing uncommitted changes belong to the current baseline and will be preserved; candidate diffs will be isolated from them.
- If the original captures cannot be matched to the rebuilt module and pipeline identity, recapture the baseline before optimization rather than comparing incompatible evidence.
