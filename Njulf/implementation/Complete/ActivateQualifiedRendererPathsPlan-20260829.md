# Activate the Remaining Renderer Paths

## Summary

Promote the listed features into the normal production request for `Medium`,
`High`, `DdgiHigh`, and `Ultra` across `Debug`, `Development`, `Release`,
`ShippingPerformance`, `ProfileSymbols`, and `DetailedInvestigation`. `Low`
remains disabled.

Qualification files become optional validation evidence, not activation
authority. Explicit `Off` settings and hard capability, memory, resource,
ownership, device-loss, and sustained-performance fallbacks remain
authoritative.

## Implementation Changes

### GI and Shadow Activation

- Add `RecursiveGlossyTransport` to the DDGI production rollout and request
  `L2` directional radiance plus `RecursiveCertified` glossy transport for
  every non-Low DDGI tier.
- Provision Medium with the required resources: a 32 MiB directional-radiance
  budget, one refinement brick, and a 32 MiB near-visibility budget. Retain
  High/DdgiHigh at two bricks/64 MiB and Ultra at four bricks/96 MiB, with
  64/128 MiB directional budgets respectively.
- Preserve the recursive fallback order: an incomplete sidecar or failed
  transport audit falls back to `OneBounce`, then receiver-only/off if its
  prerequisites also fail.
- Append `DirectionalCsmTemporalMode.Enabled = 3`; select it for every non-Low
  preset and `Disabled` for Low. `Enabled` bypasses the manifest but retains
  history allocation, camera-cut, resize, cascade-change, invalid-resource,
  and device-loss resets.
- Remove the C4-versus-hybrid-reflection rejection. Complete and validate the
  combined forward MRT/pipeline variants, appending the hybrid-reflection
  receiver output after the existing C4/C5 outputs.

### Advanced GI Production Promotion

- Normalize `AutoQualified` requests to ordinary production implementations
  instead of requiring manifests:

  | Requested feature | Effective production mode |
  | --- | --- |
  | B1 receiver feedback | `ExactCompacted` |
  | C1 opacity micromaps | `ExtFourStateExperiment` |
  | C3 directional guiding | `PerProbeHistogramExperiment` |
  | C4 tagged caustics | `WorldCacheExperiment` |
  | C5 near-field residual | `HiZAdaptive` |

- Keep the persisted `AutoQualified` numeric values for compatibility and
  preserve `RequestedMode=AutoQualified` in diagnostics, but report the
  explicit effective mode and never claim that missing evidence was qualified.
- Replace prerequisite-manifest authority with actual runtime facts:
  Vulkan/device support, cooked C1 payload availability, active B3 refinement,
  valid source ABI, content tags, compiled layout, memory admission, and
  complete resources.
- Request all five modes in every non-Low preset. Ensure the normal cooker emits
  C1 sidecars for eligible masked geometry when the pinned OMM bridge is
  available; ordinary BLAS remains the unsupported/content fallback.
- Give C4 and C5 source-controlled bounded production profiles so neither needs
  a candidate/evidence file to construct its graph resources.
- Enable B3/C5 on Medium. Use C5 `Performance` on Medium, `Balanced` on
  High/DdgiHigh, and `Quality` on Ultra.
- Allow `HiZAdaptive` to compile eighth-, quarter-, and half-resolution
  generations without evidence. Start at quarter, promote to half after four
  consecutive 120-sample windows at or below 0.45 ms P95, and demote above
  0.75 ms or on allocation/recovery failure.

### Meshlet Clustering Profiles

- Make baseline, cone-0.25, and cone-0.50 selectable through an AssetTool
  `--meshlet-build-profile <id>` option and store the selected ID and parameters
  in cooked mesh metadata and compatibility hashes.
- Add a deterministic qualification command that cooks the same pinned corpus
  with all three profiles and consumes matching `ShippingPerformance` captures.
- Run three clean-worktree runs of at least 1,000 frames per profile on the RTX
  3060, plus AMD-iGPU correctness coverage. Require visual/culling correctness,
  no system regression beyond the existing 2% gate, and at least a 3% GPU P95
  improvement over baseline.
- Select the fastest passing candidate, tie-breaking by profile ID. If neither
  passes, retain baseline. Update `RendererMeshletBuildProfiles.Production` to
  the selected result and recook production assets; the two candidates remain
  selectable for later testing but are not mixed within one cooked mesh.

### Far-Field and Simple-DDGI Async Offload

- Fix the mesh/skybox descriptor-layout validation failure blocking Far-field
  testing, then complete exact buffer-range release/acquire ownership for
  Far-field production and its first DDGI consumer.
- Extend the atomic Simple-DDGI async segment to include the sampled atlas,
  directional/recursive sidecars, scheduler/publication buffers, and every
  enabled advanced-GI resource it reads or writes. Remove the current
  `SimpleDdgiSampledAtlasActive == 0` exclusion only after that resource plan
  validates.
- Certify both paths after forced graphics-versus-compute equivalence runs with
  zero Vulkan validation, ownership, generation, or lifecycle failures.
- Add a persisted preferred-path mask, defaulting to
  `SimpleDdgiUpdate | FarFieldClipmapBake` for non-Low presets. Existing path
  booleans and `AsyncComputeMode.Disabled` remain explicit opt-outs.
- Schedule preferred paths on compute immediately when a compatible compute
  queue and complete resource plan exist. Merge adjacent Far-field and
  Simple-DDGI work into one ordered compute segment when both are pending.
- Demote a path to graphics after three consecutive 30-sample windows show a
  regression of at least both 0.25 ms and 3%; retry after the existing
  180-frame cooldown. Capability, validation, allocation, ownership, or
  device-loss failures fall back immediately.

## Interfaces and Persistence

- Advance the in-progress settings schema from v24 to v25.
- Persist `DirectionalCsmTemporalMode.Enabled` and the async preferred-path
  mask.
- Migrate prior CSM `Auto` selections to `Enabled`; preserve `Disabled`.
- Migrate prior Advanced-GI `AutoQualified` selections to the production
  mappings above; preserve explicit `Off`.
- Increment cooked-mesh compatibility metadata so assets without the selected
  meshlet profile identity are rejected for recooking rather than silently
  mixed.
- Update editor/CLI labels and diagnostics to distinguish `requested`,
  `effective`, `evidence present`, `scheduled queue`, and `fallback reason`.

## Test and Acceptance Plan

- Add preset and persistence tests proving every non-Low preset requests all
  features, Low requests none, old files migrate correctly, and explicit
  saved/CLI opt-outs still win.
- Add unit tests for recursive sidecar admission/fallback, CSM `Enabled`,
  AutoQualified normalization, C4+C5+hybrid attachment layouts, C5 half
  promotion/demotion, meshlet selection, and async preferred-path hysteresis.
- Run shader ABI/layout tests and C1/C4/C5 resource-completeness tests, including
  injected allocation, stale-generation, camera-cut, resize, reload, and
  device-loss failures.
- Run GPU smoke tests for all four non-Low presets in all six build
  configurations. Diagnostics must show the effective paths executing when
  their workload exists, with no build-configuration-specific quarantine.
- On RTX 3060, validate recursive transport, CSM history, all Advanced-GI paths,
  C5 reaching half under a controlled low-cost workload, and both async paths
  submitting to compute. On AMD, verify supported paths and deterministic hard
  fallbacks for unsupported C1/queue capabilities.
- Complete the meshlet qualification procedure before changing the production
  profile; archive the report and resulting cooked-profile identity.

## Assumptions

- "Active" means requested by default and executed whenever its real hardware,
  content, workload, and resource prerequisites exist; it does not fabricate C4
  work without tagged heroes, Far-field work without pending pages, or C1 work
  on unsupported hardware.
- Existing uncommitted GI and meshlet work will be preserved and completed
  rather than replaced.
- External Ada/Intel qualification is no longer required for activation.
