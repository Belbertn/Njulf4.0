# Defer DDGI Transport Audits During Camera Motion

## Goal

Avoid starting Transport V2 certification audits that ongoing camera-ring movement is likely to invalidate, then certify normally after movement stops.

## Implementation

- Track submitted-frame camera translation for camera-relative ring volumes using the existing `1e-6` squared-distance tolerance.
- When a current live propagation boundary and generation exist and at least one camera-ring volume is active, make `TryBeginTransportTailAudit` return false while translation continues.
- Re-arm audit start after four consecutive translation-stable submitted upload frames.
- Do not cancel an already-frozen audit solely because the view camera moved. Actual ring remap, newly exposed probes, source/generation change, or other existing invalidation continues to cancel through the current paths.
- Reset the stability count on camera cuts, DDGI disable/re-enable, scene/topology or generation changes, and loss of the live boundary. Leave authored/static-only behavior unchanged.
- Add one diagnostic reason/counter for deferred audit starts. Do not change shaders, audit batching, certificates, public settings, or APIs.

## Verification

- Add focused manager tests for start deferral, four-frame re-arm, no cancellation from view motion alone, cancellation on real remap/invalidation, and unchanged static behavior.
- Build `Njulf.Rendering` and run only the focused transport/volume-manager tests.
- Take one fresh matched moving-Sponza baseline/candidate pair. Use the supplied ~2.14 ms timing only as prioritization evidence. Confirm recurring audit dispatches disappear without whole-frame regression.
- Stop the camera and run one short smoke through successful current-generation certification with no timeout or recovery error.
