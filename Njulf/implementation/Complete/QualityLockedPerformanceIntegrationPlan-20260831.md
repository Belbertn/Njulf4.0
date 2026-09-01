# Quality-Locked Performance Integration Plan

## Objective

- Integrate the retained performance work from `perf/quality-locked-1080p60-20260830` into `Simplified-SDF`, then implement the additional quality-preserving opportunities identified during review.
- Enable the complete qualified optimization set by default, including the async-compute work. Provide one master switch, per-feature switches, and the existing independent async-compute switch so every risky path can be isolated in an A/B run.
- Preserve the current `Simplified-SDF` renderer architecture. This is a behavioral port, not a branch merge: the branches diverged after merge base `59cbcc63550b09ed7331adf888f29db00584e11c`, and a raw merge would roll back newer resolved-meshlet, DDGI scheduler, and frame-pacing work.
- Treat correctness, validation, provenance, and telemetry repairs as unconditional. Feature switches may select a baseline performance path, but must not restore a known correctness defect.

Source snapshots at planning time:

- Integration base: `Simplified-SDF` at `e7b01acc`.
- Donor worktree: `perf/quality-locked-1080p60-20260830` at `5a67aa4e`.
- Record both hashes in the final evidence report and preserve donor commit IDs in integration commit messages.

## Success Contract

- Do not intentionally reduce render resolution, render scale, texture quality, material fidelity, shadow quality, GI budgets, ray/sample counts, probe density, geometry detail, or scene content.
- Judge stochastic rendering changes against an identical-build A/A repeatability envelope. A candidate passes when its temporal and perceptual differences remain within that noise envelope and it introduces no stable visible artifact.
- Retain the campaign's hard perceptual limits: FLIP p95 `<= 0.02`; ROI luminance delta `<= 0.02` mean and `<= 0.03` p95. Keep relative HDR RMSE `<= 0.005` as the deterministic target and diagnostic signal, not as an absolute rejection threshold when identical-build captures already exceed it.
- Retain an optimization only when three-cycle ABBA measurements show either at least `1%` and `0.10 ms` improvement to the whole frame, or at least `5%` and `0.05 ms` improvement to the targeted pass, with 95% confidence and no repeated secondary-pass regression above `1%`.
- Final qualification targets native 1920x1080 on the RTX 3060 laptop: GPU p95 `<= 10 ms`, renderer CPU p95 `<= 6 ms`, and frame p99 `<= 16.67 ms` across the campaign workloads. Failure to reach the headline target does not justify a quality reduction; preserve the best fully qualified stack and report the remaining gap.
- Maintain tracked GPU memory below 80% of the 2 GiB budget and produce no Vulkan validation, shader validation, device-loss, or shutdown-lifetime errors.

## Controls and Public Interfaces

- Add `RenderSettings.PerformanceOptimizations` with:
  - `Enabled`, default `true`.
  - `EnabledFeatures`, a `PerformanceOptimizationFeature : ulong` flags enum, default `All`.
- Define independently selectable flags for:
  - meshlet working-set admission;
  - resolved meshlet addressing;
  - stable DDGI refinement admission;
  - hybrid ownership projection elision;
  - screen-local receiver admission;
  - split hybrid forward programs;
  - row-major/spatial DDGI gather;
  - shared DDGI resolve staging;
  - static shader specialization;
  - directional-lattice load sharing;
  - DDGI publication-generation reuse;
  - asymmetric sided draw streams;
  - compact masked feedback;
  - sparse hybrid-lobe payload;
  - async GI/far-field execution.
- Bump renderer-settings serialization to version 27. Older settings files deserialize with `Enabled = true` and `EnabledFeatures = All`; explicitly saved values round-trip without changing unrelated settings.
- Add startup overrides:
  - `--performance-optimizations enabled|disabled`
  - `--performance-optimization-mask all|none|<comma-separated feature expression>`
  - `NJULF_RENDERER_PERFORMANCE_OPTIMIZATIONS`
  - `NJULF_RENDERER_PERFORMANCE_OPTIMIZATION_MASK`
- Allow mask expressions such as `all,-async-gi,-generation-reuse`, so disabling one candidate does not require listing every other flag.
- Preserve the independent async kill switches `--async-compute-mode disabled` and `NJULF_RENDERER_ASYNC_COMPUTE_MODE=disabled`. They override the async feature bit and force the affected work back to graphics without disabling the other optimizations.
- Resolve and log the effective master setting, feature mask, async mode, hardware fallbacks, and quarantine state at startup and in every performance-capture manifest.
- Select CPU algorithms and statically compiled pipeline variants from the effective mask. Do not add dynamic feature branches to shader hot loops. A runtime settings change must use the renderer's normal safe resource/pipeline reconfiguration path.
- Master-off selects the preserved baseline algorithms and pipeline variants and forces campaign-owned async segments to graphics. Correctness fixes, fail-closed validation, and telemetry remain active.

## Integration Strategy

### 1. Establish a Current-Branch Baseline

- Build and test the unmodified `Simplified-SDF` base before porting any donor behavior.
- Recreate the fixed-camera Bistro and Sponza stationary, traversal, and relighting baselines under the current renderer. Capture at least two identical-build runs per workload to establish the A/A noise distribution.
- Reconcile the donor campaign harness with the current `FramePacer` instead of copying its older readiness/pacing implementation. Port only missing behavior and tests.
- Adapt the donor's missing prerequisite and evidence changes where the current branch has no equivalent:
  - split receiver-cache classify/gather/resolve timing (`596292f3`);
  - comparative evidence support (`09459619`);
  - transport-contraction ULP guard (`18fefb95`);
  - urgent, far-field, foliage, and complete-frame timing (`fab4f755`, `21713e57`, `9d682e56`);
  - source-cohort correctness (`5742dafe`);
  - source-publication provenance (`8f84ae8a`);
  - budget gating (`ed92f016`).
- Review the donor readiness/pacing commits (`b896e1bf`, `66e107cc`, `ba5f91a7`) against the current implementation and port only assertions or tests that are not already superseded.
- Make telemetry schema additions backward-compatible and keep reference images, capture manifests, hashes, and the candidate ledger under the existing campaign evidence layout.

### 2. Replay the Retained Donor Stack

Implement each retained candidate as a narrow integration commit in the following dependency order. Port behavior into current types and resource ownership; do not cherry-pick architectural deletions or old generated artifacts.

1. **MR-001 — complete meshlet working-set admission (`b40b536c`)**
   - Price and admit the complete frame working set, not only pinned pages plus the largest range.
   - Reserve all required ranges atomically for the frame, use deterministic eviction order, and retain fail-closed fallback when the set cannot fit.
   - Track admitted, evicted, late-published, unresolved, and fallback meshlets separately. This addresses the observed invalid-mapping storm and coarse-LOD fallback at its source.
2. **MR-002 — stable DDGI refinement admission lanes (`32fd1b42`)**
   - Preserve stable capacity and lane ownership across frames, avoid needless registration transitions, and reset deterministically on dependency/topology changes.
3. **MR-003 — redundant hybrid projection elision (`d83363f5`)**
   - Compile out the unnecessary hybrid DDGI projection work when receiver-cache ownership makes its result unreachable.
   - Preserve the canonical projection path for baseline and unsupported variants.
4. **MR-005 — screen-local receiver-cache admission (`bb654389`)**
   - Reuse the donor's proven screen-local admission rules and retain exact fallback for rejected or missing entries.
5. **MR-006 — split hybrid accepted/exact-fallback forward programs (`de36f6f6`)**
   - Retain two complementary programs selected by the existing per-fragment predicate/discard contract: a cache-accepted program without exact-gather register pressure and an exact-fallback program.
   - Keep depth, visibility, blend, material, and feedback ownership identical. Do not add a new meshlet-to-screen-tile binning system.
6. **MR-008 — row-major adaptive gather (`ab93570f`)**
   - Port the spatially coherent work ordering and prove identical candidate and write ownership.
7. **MR-009 — shared 6x6 resolve staging (`c629ec38`)**
   - Stage the shared neighborhood once per workgroup with bounds-safe halo loading and unchanged resolve math.
8. **MR-011 — impossible debug-branch removal (`e8d66c72`)**
   - Eliminate branches only when the selected production pipeline statically proves them unreachable. Keep diagnostics variants intact.
9. **MR-012 — bent-normal-off specialization (`fc29ff8f`)**
   - Compile a static variant for the common `BentNormals = Off` configuration while preserving the existing enabled path.

Run targeted correctness, quality, and performance gates after each candidate. A failed candidate is disabled by default and documented; it must not block later independent candidates.

### 3. Complete the Resolved-Meshlet Fast Path

- Keep the existing subgroup aggregation in `IncrementMeshletInvalidMapping`; add a shader invariant/test showing one elected atomic add per subgroup and preservation of the exact counter total.
- Use MR-001 to eliminate eviction thrash and late publication rather than merely making its counter cheaper.
- Extend the current `MeshletResolvedMappingTable` path so both GPU `SelectMeshletRange` and CPU `MeshletFrameResidencyResolver.Create` emit an encoded resolved bank/offset whenever the admitted range is ready. Compaction then consumes the resolved record directly instead of repeating mapping, page-table, and page-header reads.
- Resolve an unavailable virtual address at most once per surviving meshlet per frame, cache that decision in the compacted record, and make every depth, masked depth, motion, shadow, and forward consumer use the same result.
- Retain authenticated bounds, magic, version, and offset validation on the unresolved virtual path. Do not make all page validation diagnostics-only: the resolved path removes that recurring cost without weakening the fail-closed contract.
- Preserve coarse-LOD fallback only for genuine capacity, corruption, or publication failure. Expose the reason in counters and assert that an admitted authenticated range never falls back because of a missing publication.

### 4. Finish Receiver-Cache and Forward-Pass Unique Work

#### Directional-lattice load sharing

- Add a hardware-qualified fragment-quad/subgroup variant for the remaining directional-lattice consumers not compiled out by MR-003.
- Broadcast the loaded entry payload only when participating lanes prove the same candidate address and validity generation. Keep each fragment's surface reconstruction, support tests, weights, and accumulation order unchanged.
- Fall back to the existing scalar load path when quad operations are unavailable, helper-lane behavior is unsuitable, addresses differ, or validation cannot prove equivalence.
- Verify generated SPIR-V and target-hardware occupancy/bandwidth. Retain only if final color is bit-identical in deterministic captures and the performance gate passes.

#### Publication-generation reuse

- Replace the lattice entry's frame-index freshness test with a per-region publication generation representing every input that can affect the entry: probe/atlas publication, directional and recursive sidecars, material/content/source cohorts, volume topology, projection mode, resource generation, and relevant history discontinuities.
- Use a monotonic generation, not a hash. On 32-bit wrap, clear the dependent cache and restart at generation 1 so equality cannot accept stale data within a cache lifetime.
- Publish the generation and changed-region bitset with the same synchronization as the associated atlas/sidecars. Never expose a new generation before its data is visible.
- Reuse an unchanged entry only after the existing motion, depth, normal, disocclusion, and history checks pass. Gather and resolve dirty regions only; static regions retain their exact prior data.
- Add counters for generation hits, dirty invalidations, wrap resets, skipped tiles, and exact fallbacks.

#### Asymmetric sided-stream capacity

- Audit material metadata and command generation so only genuinely double-sided meshlets enter the cull-disabled partition.
- Track exact one-sided and double-sided candidate counts separately. Compute each stream's bound from its count plus the exact maximum LOD-dither expansion required by the command-generation contract.
- Publish an asymmetric layout with explicit one-sided capacity, double-sided base, and double-sided capacity rather than using full logical capacity for both ranges.
- Retain the current symmetric full-capacity layout when counts are unavailable or an invariant fails. Add canaries/assertions proving no overlap or overflow under curtain, foliage, traversal, and LOD-transition stress.

#### Masked-feedback compaction

- Remove the alpha-mask always-exact gather only for masked fragments already accepted by the same receiver-cache predicate used for shading.
- Preserve exact B1 attribution by compacting surviving masked feedback into a post-forward exact-feedback compute pass with identical source IDs and weights.
- Size the compact list from measured high-water marks with a documented safety margin. Detect overflow before closing the producer epoch and rerun/fall back to the existing dense exact path for that frame; never silently drop feedback.
- Keep rejected, missing, disoccluded, or otherwise ineligible masked fragments on the existing exact path.

#### Sparse hybrid-lobe payload

- Do not squeeze the existing hybrid-lobe `uvec2` into unrelated MRT spare bits; the current payloads have no proven lossless capacity.
- In the optimized hybrid pipeline, remove the full-frame `R32G32_UINT` lobe-extension color attachment and write the exact existing `uvec2` to a screen-linear storage buffer only for pixels whose lobe flags require it.
- Make consumers load the storage payload only under those same flags, with explicit clear/initialization and graphics-to-consumer barriers. Preserve the original attachment pipeline as the baseline/fallback variant.
- Compare attachment bandwidth, storage-write cost, compression behavior, and total frame time before retaining the change.

### 5. Complete and Enable Async Compute

- Keep `AsyncComputeMode.Auto` and the preferred mask for `SimpleDdgiUpdate | FarFieldClipmapBake` as the default when the async feature flag is enabled.
- Expand the atomic Simple DDGI async segment to include the complete dependency chain:
  - schedule;
  - guiding sample;
  - trace;
  - guiding train/build/validate;
  - relocate;
  - accelerated/transport/blend work;
  - directional-radiance publication;
  - publish and audit;
  - scheduler commit.
- Declare queue ownership and synchronization for every resource touched by all-on GI, including irradiance and visibility atlases, sampled atlas views, transport/source caches, directional and recursive sidecars, ray scratch/state, receiver probes, parity/update queues, relocation state, scheduler buffers, and guiding resources.
- Extend `SimpleDdgiGuidingGraphPass` and the activation catalog to support async only after its full resource footprint is represented. No pass may be silently omitted or independently migrated out of the atomic segment.
- Complete the equivalent resource/ownership plan for `FarFieldClipmapBake`.
- In Auto mode, demote safely to graphics when queue topology, resource ownership, validation, telemetry, or certification requirements are not met. Record the exact demotion reason.
- Retain the existing quarantine/fallback behavior for device or validation failures. A forced-graphics run must remain functional after any async failure.
- Certify forced-graphics versus forced-compute equivalence with fixed seeds/cameras, Vulkan validation, timestamp overlap, queue traces, and the full perceptual/temporal gate. The user-approved risk permits default activation once these safety checks pass; it does not permit missing barriers or known image divergence.

## Explicitly Excluded Donor Experiments

Do not reintroduce experiments that the donor branch itself reverted or abandoned:

| Candidate | Donor commits | Decision |
| --- | --- | --- |
| MR-004 | `3ab...` / `b80...` | No measurable gain. |
| MR-007 | `8be...` / `74a...` | Negligible result; do not use it as a new tile-binning prerequisite for MR-006. |
| MR-010 | `58f...` / `e83...` | Gather improved about 3.58%, but the whole frame improved only about 0.51%, below the campaign gate. |
| MR-013 | `3eb...` / `a684...` | Small compile shrink and no demonstrated runtime win. |
| MR-014 | `7f...`, `41...` / `5a67...` | Failed to activate or improve performance and caused severe quality failure. |

The final donor tree already removes these changes. They remain ledger evidence, not implementation work, unless new profiling identifies a materially different hypothesis.

## Verification Matrix

### Static and Automated Checks

- Build the affected configurations, including Development, Release, ShippingPerformance, and shader variants used by the campaign.
- Run the full unit/integration suite plus targeted tests for:
  - settings versioning, flag parsing, precedence, and round-trip serialization;
  - master-off and every individual feature-off path;
  - resolved-address encoding, bounds, fallback reasons, and frame lifetime;
  - working-set capacity, deterministic eviction, and late-publication races;
  - split-forward complementary coverage with no double shade or hole;
  - gather/resolve halo bounds and deterministic ownership;
  - publication-generation invalidation and wrap reset;
  - sided-stream non-overlap at maximum dither expansion;
  - masked-feedback overflow fallback and producer closure;
  - sparse-lobe initialization and synchronization;
  - async resource declarations, ownership transfers, demotion, quarantine, and shutdown.
- Run `spirv-val` on every affected artifact and inspect SPIR-V to confirm that specialized programs actually remove the intended exact-gather, projection, debug, and bent-normal code.
- Run Vulkan validation for forced graphics, forced async, Auto, master-off, and each risky feature isolated.

### Performance and Quality Runs

- Use fixed native 1080p cameras and settings for Bistro and Sponza stationary, traversal, and relighting workloads. Stabilize power, clocks, driver, thermals, cache state, and convergence.
- Measure each candidate against the retained stack with three-cycle ABBA runs and archive raw samples, capture manifests, effective masks, confidence calculations, and pass-level timestamps.
- Run the quality matrix after each shader/output-affecting candidate and the full matrix after the combined stack:
  - linear-HDR relative RMSE;
  - FLIP mean/p95 and heatmap;
  - ROI luminance mean/p95;
  - temporal delta/flicker and disocclusion checks;
  - fixed-seed deterministic comparisons where supported;
  - human inspection of thin curtains, foliage, alpha masks, glossy lobes, GI boundaries, traversal, and relighting.
- Compare every stochastic A/B delta with the current build's A/A envelope. Reject persistent bias, structured error, new flicker, fallback popping, light leaking, darkening, missing lobes, or geometry loss even when an aggregate metric passes.
- For async, show actual graphics/compute overlap and whole-frame improvement; queue migration without measurable overlap is not a retained performance win.

### Disable and Recovery Tests

- Verify one-command baseline runs:
  - all campaign optimizations off: `--performance-optimizations disabled`;
  - all optimizations on but async forced to graphics: `--async-compute-mode disabled`;
  - one suspect feature removed: `--performance-optimization-mask all,-<feature>`.
- Confirm capture manifests make these runs distinguishable and reproducible.
- Exercise feature transitions through renderer reconfiguration, device recreation, swapchain recreation, scene changes, and shutdown. No optimized resource may leak into a baseline variant, and no baseline resource may be destroyed while still referenced.

## Implementation Order and Checkpoints

1. Land controls, settings migration, capture-manifest fields, and baseline variants with no behavior change.
2. Land missing correctness/telemetry prerequisites and establish the current-branch A/A envelope.
3. Port MR-001 and complete resolved meshlet addressing; qualify invalid-mapping and geometry behavior.
4. Port MR-002, MR-003, MR-005, and MR-006; qualify the forward/cache split before adding smaller receiver optimizations.
5. Port MR-008, MR-009, MR-011, and MR-012 independently.
6. Implement directional-lattice sharing and publication-generation reuse.
7. Implement asymmetric sided streams, masked-feedback compaction, and sparse hybrid-lobe payload independently.
8. Complete the Simple DDGI and far-field async resource plans, enable Auto, and run equivalence/overlap certification.
9. Run the combined full qualification matrix, sustained thermal soak, memory audit, and master/per-feature rollback matrix.
10. Update the candidate ledger and renderer documentation, archive before/after evidence, and move this plan to `implementation/Complete` only after all retained default-on paths and their fallbacks pass.

Every checkpoint must leave the branch buildable and runnable with the new work disabled. If a candidate fails its gate, retain only generally useful instrumentation/correctness work, default the candidate off or remove it, record the evidence, and continue with independent items.

## Completion Deliverables

- Integrated code and tests with donor provenance in commit messages.
- Updated renderer-settings and command-line documentation, including copy-paste disable examples.
- Candidate ledger mapping every retained, rejected, superseded, and newly implemented item to evidence and its feature flag.
- Before/after performance distributions, pass timings, queue-overlap captures, quality reports, memory results, and validation logs for each campaign workload.
- A final default-on feature mask and a concise troubleshooting table mapping visible/performance symptoms to the smallest disable switch.

## Assumptions

- `Simplified-SDF` remains the destination and architectural source of truth; the performance worktree is evidence and behavior to adapt, not a tree to merge wholesale.
- All retained donor candidates and all viable unique items are initially attempted and default on only after their individual gates pass.
- Async risk is accepted, but graphics fallback, explicit synchronization, validation cleanliness, and the independent async disable switch are mandatory.
- The user's quality tolerance is perceptual repeatability/noise lock, not permission for deliberate quality reduction.
- Reverted donor experiments remain excluded unless a new profile and materially different design justify reopening them.
