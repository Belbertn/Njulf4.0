# Hybrid DDGI Cache Performance Unlock — Results

## Implementation

- Hybrid cache consumers are prepared after the opaque family bank and admitted only when every required handle exists.
- Unavailable or unsafe hybrid consumers select exact Forward before cache dispatch; independent B1 feedback production remains intact.
- Surface-aware hybrid cache production has a diffuse/visibility-only compute artifact. The 80-byte gather stride, canonical words, surface sidecar, descriptors, and public settings are unchanged.
- The production benchmark accepts SurfaceAware mode without requiring the TemporalAdaptive generation counter.
- The compact producer is selected only for an eligible hybrid SurfaceAware path without exact feedback, diagnostics, or cache debug view; canonical production remains the fallback.

## Verification

- Release shader/build verification: 488 shader artifacts current; receiver-cache contract and SPIR-V validation passed; focused tests passed 2/2.
- Device/workload: NVIDIA RTX 3060 Laptop, Vulkan 1.4.341, 1920x1080 Bistro Presentation, DDGI High, Stress Unlimited, validation off.
- Combined cache lane capture: GPU frame 178.44 ms average / 190.91 ms p95; Forward 156.95 ms average; cache producer 4.98 ms. Cache was consumed. This is a severe regression.
- Final split-admission capture: GPU frame 38.99 ms average / 40.40 ms p95; Forward 16.97 ms average; validation errors 0. Bistro's 10,494 masked meshlets select exact Forward with an explicit `PipelineUnavailable` detail instead of entering the combined cache lane.
- The remaining 5.24 ms receiver-gather pass is the independently required B1 feedback producer and is intentionally retained.

The compact cache path remains implemented for views eligible for the low-pressure split consumers. The measured combined hybrid/cache program is not admitted.
