# Shared Temporal Surface Validity Producer

## Goal

Produce one conservative reprojection prefilter after motion vectors. This plan owns only the internal ABI and producer; plan 12 is the first consumer. Existing feature histories and local validation remain.

## ABI and Producer

- Add a versioned, lazily allocated, double-buffered full-resolution `TemporalSurfaceValidityHistory` with four uints per pixel:
  - word 0: stable 32-bit surface-identity hash;
  - word 1: current linear view-depth float bits for next-frame history;
  - word 2: previous-pose linear view-depth float bits;
  - word 3: octahedral current normal in 2x14 bits plus the four-tap mask in bits 28-31.
- Hash collision is possible, so identity success is only a prefilter. Consumers keep exact or feature-local checks where required. Identity zero is invalid.
- Motion fragments seed the current identity, depths, and normal. Cover opaque, alpha-tested, and foliage paths whenever at least one shared-validity consumer is registered.
- At the earliest dependency-safe point after motion, reproject each current pixel and test the four previous bilinear taps. Define mask order once in the shared codec: floor/floor, ceil/floor, floor/ceil, ceil/ceil.
- Shared rejection may cover bounds, invalid motion/history, definite identity mismatch, and depth/normal limits that are safe for every consumer. Consumers retain stricter thresholds and feature-specific rules.
- Let the render graph choose queue placement; do not force an extra graphics-queue serialization.

## Ownership and Fallback

- Keep the producer dormant until plan 12 registers the first consumer.
- Do not remove reflection `HybridMetadata`, GTAO geometry history, directional-shadow history, or any local fallback in this plan.
- Reset on camera cuts, discontinuities, resize, scene/resource regeneration, or ABI-version change. If shared validity is unavailable, consumers use their existing validation path.
- Add no public setting or serialized schema.

## Verification

- Add one focused CPU/GPU codec test, four-tap-order test, and render-graph dependency test.
- Compile affected shaders, run `spirv-val`, and exercise opaque, masked, and foliage writes once under Vulkan validation.
- Record allocation size and isolated producer cost. Plan 12 must measure producer plus first-consumer time together before the path is kept enabled.
