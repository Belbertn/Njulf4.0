# Hi-Z CPU Optimization Plan

Goal: keep the new Hi-Z consumers enabled while reducing CPU recording overhead enough for production use.

Current diagnostic signal:

```text
hiz=1, occlusion=1
forwardHiZ=22625/1326/0.059
forwardHiZ=60735/4357/0.072
hizUs=37-40us
hizRecordUs=8976-9186us
hizBreakdownUs=10/786/79/186/7794
```

Hi-Z is now doing real GPU work, but CPU recording cost dominates. The first production target is to reduce steady-state `hizRecordUs` from roughly 9 ms to below 1 ms.

## 1. Name the Hi-Z CPU Buckets

Replace the positional breakdown:

```text
hizBreakdownUs=10/786/79/186/7794
```

with named diagnostics:

```text
depthTransitionUs
pyramidTransitionUs
descriptorBindUs
pushDispatchUs
finalBarrierUs
```

Acceptance criteria:

- Diagnostics identify which bucket owns the high cost.
- Existing compact one-line diagnostics remain easy to scan.
- `RendererDiagnosticsTests` covers the named fields.

## 2. Attack the Final Barrier Cost First

The largest observed bucket is the last one:

```text
finalBarrierUs ~= 7.8ms
```

Inspect `HiZBuildPass` barrier recording:

```csharp
AddMipWriteToNextReadDependency(...)
TransitionPyramidToShaderRead(cmd)
```

Optimize by:

- Precomputing static `ImageSubresourceRange` values.
- Precomputing barrier templates that only patch the image/layout fields that can change.
- Reducing per-mip barrier setup allocations and struct construction.
- Verifying whether all per-mip barriers are necessary at their current scope.

Acceptance criteria:

```text
CpuHiZFinalBarrierMicroseconds < 500us steady-state
```

## 3. Reduce Per-Mip Descriptor Work

The current path binds a descriptor set for each mip:

```csharp
CmdBindDescriptorSets
CmdPushConstants
CmdDispatch
```

Production options:

- Use one descriptor set containing all Hi-Z mip views.
- Use bindless descriptors for source and destination mips.
- Use descriptor indexing/storage image arrays if supported cleanly by the existing descriptor system.

Acceptance criteria:

```text
CpuHiZDescriptorBindMicroseconds < 100us steady-state
```

## 4. Precompute Static Per-Mip Metadata

Keep per-frame Hi-Z recording as close as possible to issuing commands only.

Cache on creation or resize:

- push constants per mip
- dispatch dimensions
- descriptor set arrays
- image subresource ranges
- barrier templates

Rebuild only when the pyramid or scene depth extent changes.

Important correctness constraint:

```csharp
mip == 0 ? _renderTargets.SceneDepth.Extent : _pyramid.GetMipExtent(mip - 1)
```

Mip 0 must use full scene-depth dimensions as its source, even though the Hi-Z pyramid is half-resolution.

## 5. Consider a Single-Dispatch Build Variant

If command recording remains expensive after barrier and descriptor cleanup, prototype a single-dispatch or fewer-dispatch pyramid build.

Options:

- One compute dispatch that builds multiple mips using groupshared memory for early levels.
- A two-stage build: full depth to first few mips, then recursive mips.
- A specialized first downsample pass from full depth to half-res Hi-Z mip 0.

Acceptance criteria:

- Preserves reverse-Z min-depth semantics.
- Does not introduce over-culling artifacts.
- Beats the optimized multi-dispatch path in CPU and total frame time.

## 6. Make Adaptive Hi-Z CPU-Aware

The current adaptive estimate is GPU-centered:

```text
hizEstimatedUs=512/143/369
```

Extend the policy to include CPU recording cost:

```text
netUs = estimatedGpuSavedUs - gpuHiZUs - cpuHiZRecordUs
```

If CPU recording cost exceeds expected GPU savings, adaptive policy should suppress or probe even when cull rate is nonzero.

Acceptance criteria:

- Open or low-occlusion views suppress automatically.
- Occlusion-heavy views stay active.
- Diagnostics explain CPU-driven suppression clearly.

## 7. Enable Async Compute After CPU Cost Is Fixed

Async compute can hide GPU work, but it will not solve high CPU recording cost.

Only evaluate async once steady-state CPU recording is acceptable.

Acceptance criteria:

```text
asyncEnabled=1
hizUs hidden or overlapped
no queue-transfer regression
no added frame pacing spikes
```

## 8. Production Thresholds

Hi-Z is production-ready when typical steady-state captures meet:

```text
hizRecordUs < 1000us
hizUs < 150us
hizCullRate >= 0.03 in occlusion-heavy views
hizEstimatedNetUs > 0 after CPU cost is included
no grey/over-cull frames
no repeated warmup invalidation
```

## 9. Validation Scenes

Run fixed camera paths across:

- Sponza open view: should suppress if cull rate is low.
- Sponza interior or corridor: should stay active and cull meaningfully.
- Fast camera movement: previous-frame path should suppress safely.
- Teleport/camera cut: previous pyramid should invalidate and warm up.

Required metrics:

```text
frameMs avg/p95
cpuDrawMs avg/p95
hizRecordUs avg/p95
hizUs avg/p95
forwardUs avg/p95
forwardHiZ tested/culled/rate
previousHiZSkip
hizPolicy
hizAdaptiveStatus
```

## Priority Order

1. Name the buckets.
2. Optimize the final barrier bucket.
3. Reduce per-mip descriptor binding.
4. Precompute static metadata.
5. Add CPU-aware adaptive suppression.
6. Evaluate async compute.
7. Validate against fixed camera paths.
