# Optimize the Post-Forward Hybrid-Reflection Stack

## Goal

Identify the dominant reflection subpass, then make one narrow change. This plan does not depend on the common-surface feasibility work.

## Profile First

- Record DDGI base, classify/compact, SSR, ray query, temporal, spatial, composite, and whole-stack time in one warmed Sponza capture.
- Distinguish the two sparse-lobe bucket/partition submissions in markers or counters; their shared `L0 C0 F1 Sparse Lobe` label does not by itself mean the complete pass ran twice.
- Continue only with the dominant measured subpass. Preserve ray counts, resolution, thresholds, adaptive budgets, and pass boundaries.

## Ray-Task Candidate

If ray query materially spends time rebuilding receiver state and the SSR ray, pass the already-computed ray through the task record:

- Expand the record from 8 to 12 words with this exact layout: words 0-3 existing `Primary`, words 4-5 existing lobe data, words 6-8 `floatBitsToUint(rayOrigin.xyz)`, and words 9-11 `floatBitsToUint(rayDirection.xyz)`.
- Ray query consumes that origin/direction directly and retains existing validation and fallback behavior.
- Update producer/consumer declarations, stride, capacity calculations, memory telemetry, and ABI tests together. Account for the 50% task-buffer traffic increase.
- Do not also introduce common-surface decoding or another reflection optimization in this comparison.

## Verification

- Fresh-build affected shaders, run `spirv-val`, and add one focused 12-word ABI/bounds test.
- Exercise compact dispatch and fallback once under Vulkan validation and compare one representative HDR frame.
- Take one matched Sponza candidate capture. Keep only a clear reduction in the dominant subpass and whole stack without a whole-frame, memory, or correctness regression.
- If another subpass dominates or the larger record loses, stop and write a separate narrow candidate rather than broadening this plan.
