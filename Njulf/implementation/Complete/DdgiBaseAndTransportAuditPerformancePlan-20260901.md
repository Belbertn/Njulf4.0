# Reduce DDGI Base and Audit Frame Costs

## Summary

- Implement two separately measured candidates: promote the existing exact reflection-base path, then reduce transport-audit batching to one chunk per frame.
- Preserve rendering quality, full tail certification, shader/storage ABI, and the current grouped path as an immediate rollback.
- Retain each candidate only after isolated profiling and the full Bistro/Sponza quality and performance gates pass.

## Implementation Changes

- Add default-on `PerformanceOptimizationFeature.ExactHybridDdgiBase` at bit 15, expose the stable mask token `exact-hybrid-ddgi-base`, and expand `All` accordingly.
- Select exact DDGI-base evaluation when that feature or `DdgiReflectionFullResolutionOracle` is enabled. Exact mode uses scale 1, skips the cohort-producer dispatch, and forces per-pixel reconstruction, avoiding the shared-memory barrier and `maximumGroups` scans. Disabling the feature with the oracle off restores the current grouped path unchanged.
- Keep the existing pipeline, descriptor layout, push constants, cohort resource, and shader artifact so rollout adds no shader variant, pipeline-creation, ABI, or memory risk.
- Change `MaximumChunksPerSubmittedFrame` from two to one. Continue auditing only during `AuditFrozen`, preserve all ray/reduce work and certificate coverage, and let the existing conservative readback deadline cover the longer dispatch span.
- Update lifecycle evidence and documentation so a three-chunk audit spans three submission frames. Do not alter the packed SH decoder or audit shader in this candidate.

## Interfaces and Tests

- Treat the new enum value and performance-mask token as the only public interface addition; keep the existing oracle setting as a force-exact diagnostic override.
- Add selector tests covering feature on/off, oracle override, history validity, and grouped quality-tier scales.
- Update performance-mask parsing/formatting tests and renderer diagnostics expectations.
- Update audit cardinality and transient-evidence tests to require one submitted chunk per frame, distinct frame serials, unchanged chunk/texel totals, exact certificate acceptance, and no execution while certified or otherwise idle.
- Run focused hybrid-reflection, performance-settings, DDGI-tail, transient-evidence, shader-build, and Vulkan validation tests, followed by the full test suite.

## Performance and Acceptance

- Record the exact build, shader-bundle, GPU/driver, cache, settings, dispatch dimensions, and raw profiler identities before comparison; preserve the current dirty worktree through an isolated candidate.
- Compare feature-off grouped versus feature-on exact with matched A/A and three-cycle ABBA captures in Bistro and Sponza. Keep the feature default-on only if the named pass clears the live 5%/0.05 ms threshold, whole-frame timing clears 1%/0.1 ms, and no admitted workload regresses beyond 1%.
- Verify profiler samples no longer reach the reconstruction barrier or group scans in exact mode. Confirm the affected SPIR-V hashes, pipeline count, and measured-frame pipeline creation remain unchanged.
- Compare two-chunk and one-chunk audits across identical Sponza invalidation/certification transactions. Require lower audit-active-frame p95/max, exactly one chunk per frame, unchanged total audit work and certificate digest, and no readback timeout, recovery, or convergence-deadline regression.
- Run HDR relative-error, FLIP, ROI luminance, temporal-repeatability, artifact review, validation, memory, shutdown, and CPU/GPU tail-latency gates across 1080p plus the supported higher-resolution quality profiles.
- Reject rather than default-enable the exact-path candidate if any supported workload or quality gate fails; retain the one-chunk audit only if its reduced spike outweighs the bounded certification-latency increase.
