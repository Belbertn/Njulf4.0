# Reverted Performance Plan Implementations

As of commit `124c86f5`, the following plan candidates were implemented,
built, tested, measured, and then reverted because they did not pass their
performance or quality acceptance gates.

## Forward-Pass Register and Occupancy Remediation

Plan: [ForwardPassRegisterOccupancyRemediationPlan-20260901.md](ForwardPassRegisterOccupancyRemediationPlan-20260901.md)

All three forward remediation candidates were reverted:

- DDGI address reuse.
- Visibility-footprint and validity-work reuse.
- The normal-only forward shader module and its selection path.

The combined candidate changed whole-GPU-frame p95 from `32.061 ms` to
`32.510 ms`, a `0.449 ms` regression. `ForwardPlusPass` p95 improved only
`0.164 ms`, from `18.256 ms` to `18.092 ms`, which was below the required
gain and accompanied by worse median timing.

Evidence: [candidate.json](../.perf-loop-runs/forward-stationary-remediated-20260903-002330/candidate.json)

## DDGI Base and Transport Audit

Plan: [DdgiBaseAndTransportAuditPerformancePlan-20260901.md](DdgiBaseAndTransportAuditPerformancePlan-20260901.md)

Both independent DDGI candidates were reverted.

### Exact hybrid DDGI base

The default-on feature bit, mask token, selector, and exact reconstruction
path were removed. Whole-GPU-frame p95 regressed from `32.061 ms` to
`32.776 ms`. `HybridReflectionDdgiBase` p95 regressed from `3.761 ms` to
`4.738 ms`.

Evidence: [exact-on.json](../.perf-loop-runs/ddgi-exact-quick-20260903-011509/exact-on.json)

### One transport-audit chunk per frame

The one-chunk cardinality change and its lifecycle/test updates were removed.
It reduced audit-active-frame average time from `1.318 ms` to `0.669 ms`, but
whole-GPU-frame p95 regressed from `47.473 ms` to `48.088 ms`, and the relight
image delta was worse than the matched two-chunk control.

Evidence:

- [one-chunk candidate](../.perf-loop-runs/ddgi-audit-onechunk-20260903-012237/candidate.json)
- [two-chunk control](../.perf-loop-runs/ddgi-audit-twochunk-control-20260903-012526/control.json)

## Bistro Transparent Ray-Traversal Optimization

Plan: [BistroTransparencyRayQueryOptimizationPlan-20260901.md](BistroTransparencyRayQueryOptimizationPlan-20260901.md)

The `0x04` transparent-reflection TLAS mask, independent eligibility rules,
GLSL mask constant, ordinary-ray query change, and associated tests were
removed. `TransparentPasses` p95 improved only `0.005 ms`, from `1.395 ms`
to `1.390 ms`, while whole-GPU-frame p95 regressed from `32.061 ms` to
`32.146 ms`.

Evidence: [candidate.json](../.perf-loop-runs/transparency-raymask-quick-20260903-015547/candidate.json)

## Not Reverted

The automatic planar-reflection material opt-in, exact metadata encoding,
telemetry, fallback, and internal bitset override remain implemented. The
bitset representation remains non-default because its measured improvement
did not meet the requested retention threshold.

Plan: [AutomaticPlanarReflectionOptInAndCaptureOptimizationPlan-20260901.md](AutomaticPlanarReflectionOptInAndCaptureOptimizationPlan-20260901.md)

Evidence: [planar comparison](../.perf-loop-runs/planar-quick-20260902-225212/)
