# Fix the Remaining Camera-Motion DDGI Refresh

## Confirmed root causes

- The scroll planner calculates arbitrary ray counts from `budget / exposedProbes`. For the current High near ring, an X-cell scroll exposes 345 probes and requests **94 rays** ([`SimpleDdgiScrollPlanner.cs`](../Njulf.Rendering/Resources/SimpleDdgiScrollPlanner.cs#L265)).
- The GPU scheduler only supports `{128, 64, 32, 16, 8}` that frame. A 94-ray update matches no bucket ([`ddgi_simple_schedule_emit_classify.comp`](../Njulf.Shaders/ddgi_simple_schedule_emit_classify.comp#L18)).
- Unmatched updates are silently excluded from tracing and queue scattering, while blend/publish still dispatch over the full accepted count. This consumes stale queue entries, leaves the entering probe slab unrepaired, and causes it to fill progressively over subsequent frames—the observed “refresh.”
- Existing tests missed the integration contract and even assert an unsupported 17-ray scroll cardinality. All 33 relevant tests currently pass despite the defect.
- The deterministic Sponza run recorded six compatible scrolls, zero camera cuts and zero emergency rebases, but moved along Z, where the calculated 64-ray cardinality happens to be valid. It therefore missed the lateral X failure.
- Separately, generic Hi-Z camera cuts—5 m frame displacement or rotation past the 0.5 forward-dot threshold—force a direct DDGI rebase with `mandatoryRepair=false` and zero reserved rays ([`SimpleDdgiVolumeManager.cs`](../Njulf.Rendering/Resources/SimpleDdgiVolumeManager.cs#L14278)). This causes another refresh during fast movement or rotation.

## Implementation changes

- Build the six frame ray buckets once through a shared CPU policy and use that identical table for scroll planning and GPU frame upload.
- Change incremental planning to select the highest available bucket within the ring’s maintenance/full bounds and remaining ray budget. Never manufacture an arbitrary cardinality.
  - High near-ring X scroll: select 64 rays instead of 94.
  - The existing 17-ray test case becomes 16 rays.
  - Defer movement without changing origin, offsets, or generations when no supported bucket fits.
- Make bucket emission fail closed:
  - Count accepted updates without a matching bucket.
  - Require `acceptedCount == sum(rayBucketCounts)`.
  - On mismatch, suppress trace, blend, publish, and commit commands; allow only feedback export.
  - Add the missing counter barrier between bucket classification and emission.
  - Report a specific unbucketed-update fallback reason instead of silently using stale queue records.
- Make mandatory scrolling a real transaction:
  - Add a bounded GPU cohort-validation stage after producer completion and before commit.
  - Require exactly the expected exposed probes, the correct transaction serial/cardinality, complete trace/transport/publication outcomes, and zero producer failures.
  - Commit no exposed probe if the complete cohort fails; overlapping probes remain untouched and receivers use coarser valid support.
- Decouple temporal camera cuts from DDGI:
  - Hi-Z/TAA/receiver histories may reset on a camera cut, but overlapping world-space DDGI rings continue through normal incremental toroidal scrolling.
  - Only actual no-overlap movement is a DDGI rebase.
  - During a true teleport, suppress the affected ring, use the next coarser ring/environment fallback, rebuild the ring completely, then fade it in over eight frames. Never reveal a progressively repaired lattice.

## Diagnostics and interfaces

- Add frame buckets, selected scroll cardinality, expected/accepted/traced/committed scroll counts, unbucketed count, cohort failure reason, and rebase state to `RendererDiagnostics` and capture telemetry.
- Preserve the existing six-bucket ABI, two-cascade-per-frame limit, and 32,768-ray spatial-recovery ceiling.
- Extend moving-camera capture output so scroll events remain visible in temporal reports instead of being lost when the final sampled frame is not a recenter frame.

## Verification

- Unit-test every quality preset and customized ray policy: every planned scroll cardinality must occur in the uploaded frame buckets.
- Cover positive/negative X, Y, Z and multi-axis scrolls, insufficient budgets, two simultaneous cascades, and no-overlap teleports.
- Add shader-contract tests for unmatched-bucket failure, contiguous queue emission, `accepted == bucket sum`, cohort atomicity, and zero stale-tail consumption.
- Add deterministic Sponza world-X and world-Z routes plus:
  - a 6 m overlapping movement that trips Hi-Z cut detection;
  - a rotation beyond 60° without translation;
  - a true no-overlap teleport.
- Ordinary-scroll acceptance:
  - expected = accepted = traced = committed in the same frame;
  - unbucketed count and cohort failures remain zero;
  - no atlas clear or scheduler fallback;
  - overlapping probe state and atlas payload remain bit-identical;
  - newly exposed probe age does not extend into later frames.
- RenderDoc the first lateral recenter and verify the selected 64-ray policy, matching indirect bucket dispatch, complete cohort publication, preserved overlap, and no stale queue records.
- Run the existing DDGI tests, shader build/validation suite, moving Bistro/Sponza quality captures, and performance benchmark. The added cohort validation must remain a bounded GPU-only scan with no per-probe CPU readback or additional ray-bucket dispatches.

## Assumptions

- “No refresh” applies to ordinary and fast overlapping camera motion.
- True teleports use stable fallback and controlled fade-in because an entirely new near field cannot be generated instantaneously within the production ray budget.
- Scene, material, and lighting invalidations retain their existing update behavior.
