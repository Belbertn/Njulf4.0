# Renderer Architecture 06 - Async Compute Plan - 2026-06-14

Target outcome: async compute is used only where the render graph can prove useful overlap and correct synchronization. Skinning, culling, particles, light culling, Hi-Z, bloom, exposure, and other compute work may run on compute queues when it improves measured frame time.

Async compute is not a goal by itself. Incorrect async scheduling can reduce performance. This plan requires measured wins, precise barriers, queue ownership management, and clean fallbacks.

## Non-Negotiable Requirements

1. The render graph owns queue scheduling and synchronization.
2. Async compute is enabled per device capability and per workload.
3. No pass manually submits async work outside graph control.
4. Queue ownership transfers are explicit when required.
5. No same-frame CPU readback is introduced.
6. Async paths must produce identical rendering output to graphics-queue paths.
7. Old manual or duplicate scheduling paths are removed once graph scheduling is authoritative.

## Phase 0 - Device Capability Model

1. Query queue families:
   - graphics.
   - compute.
   - transfer.
   - present.
2. Detect whether async compute is truly separate from graphics.
3. Record queue counts and supported timestamp behavior.
4. Add device profile flags:
   - async compute unavailable.
   - async compute shared queue only.
   - async compute dedicated queue.
   - queue ownership transfer required.
5. Add settings:
   - disabled.
   - conservative.
   - aggressive.
   - per-pass overrides for debugging.

Acceptance criteria:
- Renderer diagnostics explain why async compute is enabled or disabled.

## Phase 1 - Graph Queue Annotations

1. Extend pass declarations with:
   - supported queue classes.
   - preferred queue class.
   - async eligibility.
   - expected workload size.
   - dependency urgency.
2. Mark candidate passes:
   - GPU skinning.
   - GPU scene uploads/copies where transfer queue is useful.
   - object/meshlet culling.
   - particle simulation.
   - light culling.
   - Hi-Z build.
   - AO.
   - bloom.
   - auto exposure.
3. Mark non-candidates:
   - passes writing swapchain.
   - passes requiring dynamic rendering graphics pipeline.
   - tiny compute passes where submit overhead dominates.
4. Add graph validation for unsupported queue use.

Acceptance criteria:
- The graph can compile the same frame for all-graphics or async scheduling.

## Phase 2 - Timeline Synchronization

1. Prefer timeline semaphores where available.
2. Add per-queue command pools and command buffers.
3. Track pass completion values.
4. Generate waits/signals between queues from graph dependencies.
5. Add fallback to binary semaphores if timeline semaphores are unavailable.
6. Ensure frame fences include all submitted queues.
7. Add diagnostics for queue wait durations and queue idle gaps.

Acceptance criteria:
- Multi-queue frames retire correctly without device idle waits.

## Phase 3 - Queue Ownership And Barriers

1. Extend resource state tracking with queue family ownership.
2. Generate release/acquire barriers for images and buffers when queue families differ.
3. Keep resources in concurrent sharing mode only when measured to be better or required.
4. Validate external/imported resources such as swapchain images are not used on unsupported queues.
5. Add RenderDoc validation captures for queue transfers.

Acceptance criteria:
- Vulkan validation reports no queue ownership hazards.

## Phase 4 - First Async Candidate: GPU Skinning

1. Move GPU skinning pass declaration to compute eligible.
2. Schedule skinning before visibility and graphics consumers.
3. Add barriers from skinning writes to culling/render reads.
4. Measure overlap with early frame CPU/graphics work.
5. Keep all-graphics fallback.
6. Delete any manual skinning synchronization once graph scheduling owns it.

Acceptance criteria:
- Skinned scenes render identically.
- Async skinning improves or matches frame time on capable devices.

## Phase 5 - GPU Culling And Compaction

1. Mark culling and compaction passes compute eligible.
2. Schedule after GPU scene uploads and skinning.
3. Overlap with shadow setup or other graphics work only when dependencies permit.
4. Add barriers from compacted list writes to graphics reads.
5. Measure GPU queue occupancy and graphics bubbles.

Acceptance criteria:
- GPU-driven visibility works on graphics-only and async compute paths.

## Phase 6 - Particles

1. Run particle simulation on compute queue.
2. Include spawn, kill, compaction, sorting, and emitter culling dependencies.
3. Synchronize particle render buffers before graphics particle draw.
4. Avoid CPU readback of live particle counts.
5. Measure overlap with opaque rendering or shadow rendering.

Acceptance criteria:
- Large particle scenes benefit from async simulation without synchronization stalls.

## Phase 7 - Light Culling, Hi-Z, AO, Bloom, Exposure

1. Evaluate each compute pass individually:
   - work size.
   - dependency position.
   - bandwidth pressure.
   - overlap opportunity.
2. Do not schedule bandwidth-heavy passes asynchronously if they starve graphics.
3. Use profiling to choose default async candidates per quality profile.
4. Add pass-level counters:
   - queued async.
   - waited by graphics.
   - overlap time.
   - async benefit estimate.

Acceptance criteria:
- Async defaults are based on measured benefit, not assumption.

## Phase 8 - Scheduling Heuristics

1. Add a graph scheduler that can choose:
   - all graphics.
   - conservative async.
   - aggressive async.
2. Heuristics:
   - avoid async for tiny dispatches.
   - avoid async when graphics immediately waits.
   - prefer async when long compute can overlap independent graphics.
   - avoid concurrent bandwidth-heavy passes.
3. Add runtime adaptation only after stable static policies exist.
4. Store scheduling decision in diagnostics.

Acceptance criteria:
- Async compute can be toggled and compared without code changes.

## Phase 9 - Remove Old Architecture

Delete after graph scheduling is complete:

1. Manual per-pass async submission.
2. Pass-local semaphores or queue waits.
3. Duplicate all-graphics command recording paths outside graph scheduling.
4. Hidden device waits used to paper over queue hazards.
5. Unused command pools or synchronization objects from the old model.

Acceptance criteria:
- Queue scheduling has one owner: the render graph.

## Validation

1. Unit tests:
   - queue dependency graph.
   - semaphore wait/signal generation.
   - ownership transfer generation.
   - unsupported queue fallback.
2. Integration tests:
   - all-graphics frame.
   - async skinning.
   - async culling.
   - async particles.
   - async post.
   - resize while async is enabled.
3. Manual GPU validation:
   - Vulkan validation layers.
   - RenderDoc queue inspection.
   - vendor profiler queue lanes.
4. Performance validation:
   - async path must improve or match all-graphics on target workloads.
   - no increased frame latency unless explicitly accepted by profile.
   - no extra CPU submission overhead that exceeds GPU savings.

## Definition Of Done

1. Render graph schedules compute and graphics queues.
2. Async candidates are measured and controlled by settings/profile.
3. Synchronization and queue ownership are graph-generated.
4. Rendering output matches all-graphics path.
5. Old manual queue/sync paths are removed.

## Implementation Notes - 2026-06-14

- Phase 0 device capability modeling is implemented through `AsyncComputeDeviceProfile`.
- Phase 1/8 graph scheduling contracts are implemented through `AsyncPassSchedulingHint`, `AsyncComputeScheduler`, `ScheduledPass`, and `AsyncSchedulePlan`.
- Phase 2/3 planning-level synchronization and queue ownership edges are represented through `QueueSyncEdge`.
- Unit tests cover dedicated/shared compute queue classification, conservative/aggressive fallback behavior, and cross-queue sync-edge generation.
- Graph pass declarations now carry supported queues, preferred queue, async eligibility, workload score, bandwidth pressure, and dependency urgency.
- The render graph owns active async scheduling through `RenderGraph.ConfigureAsyncScheduling`, rebuilds queue-aware barrier plans from that schedule, and exposes schedule diagnostics for performance snapshots.
- Barrier planning now emits queue-family ownership metadata for image and buffer transitions when scheduled producer/consumer queues differ.
- GPU skinning is now a graph pass (`SkinningGraphPass`) instead of a manual renderer-side dispatch; conservative mode keeps it on graphics while its same-frame uploads remain recorded on the graphics command buffer.
- Runtime command infrastructure now includes per-frame dedicated compute command buffers and per-frame async compute synchronization primitives for binary fallback.
- Production graph execution now records compute-scheduled passes into the per-frame compute command buffer, records graphics-scheduled passes into the graphics command buffer, submits the compute queue, and makes graphics wait on compute completion.
- Async compute mode now defaults to aggressive, with per-pass mode overrides available through `RenderSettings.AsyncCompute.PassOverrides`.
- Timeline semaphores are created and used for compute-to-graphics waits when supported, with binary semaphore fallback retained.
- Dedicated transfer queue graph execution is wired for transfer-scheduled passes, including transfer-to-compute and transfer-to-graphics waits.
- Image queue-family ownership transfers are emitted as producer-side release and consumer-side acquire barriers for cross-queue graph edges.
- Particles now have a graph-visible compute `ParticleSimulationPass` before graphics `ParticlePass`, and the renderer defaults particle simulation mode to GPU.
- Queue-submit CPU overhead is measured for transfer, compute, and graphics submits through the runtime stall tracker.
- Unit tests cover graph queue-contract validation, metadata-driven async scheduling, cross-queue ownership transfer generation, shared-queue fallback, and the updated production pass order.
