# Scene and view ownership

Implementation checklist for [the scene/view completion plan](../implementation/SceneAndViewCompletionPlan-20260909.md). This records the intended boundary; it does not claim that multiple public views are implemented.

## Device lifetime

The renderer owns the Vulkan context, window/swapchain, submission and completion tracking, upload pump, asset managers, immutable pipeline caches and deferred destruction. Asset handles and shader binding indices stay stable across views. A descriptor bank is immutable with respect to recorded work until its in-flight slot completes. Mutable reserved bindings need separate banks per view and slot; asset registrations must publish to every applicable bank before use.

## Scene lifetime

SceneRenderState is keyed by the Scene reference. It owns the frozen scene snapshot, mirrored lights and effective environment, scene acceleration structures, skinning and particle simulation, reflection-probe scheduling and world-space GI. Prepare simulation and snapshot data once per referenced scene and frame. Drawing never calls Scene.Update. Legacy LightManager registrations belong to the default scene.

MainView anchors world GI for its scene. Otherwise the oldest live view is the anchor, regardless of scheduling order. Skipping a view does not select a new anchor. Only anchor work supplies camera-relative recentering and feedback; replacing an anchor invalidates affected consumers. Scene GPU work can require the anchor's shadow phase without advancing its screen-space histories.

Last-view release retires scene GPU resources against their actual queue use. It does not dispose the borrowed Scene. Shutdown drains pending work before destroying shared services.

## View lifetime

RenderViewState owns camera snapshots, previous object/camera transforms, jitter/sample counters, visibility/LOD/HiZ, particle sorting, camera-selected shadows, automatic planar reflections, per-view descriptors, graph/pass instances, attachments, screen-space effects, exposure and diagnostics. ProductionPipelineOwner remains responsible for pass creation, preparation and release; its dependencies must refer to the correct scene and view owners.

Creation validates the device, borrowed scene/camera and destination before retaining resources. Partial allocation failure unwinds everything created by that view. Rebinding retains the new allocation before releasing the old one. Target replacement does not invalidate other retained material/UI/capture bindings.

Freeze the batch and validate dependencies before recording. Histories commit on successful submission, even if presentation subsequently fails. Failed recording does not commit. Partial submission retains referenced resources through recovery and invalidates affected histories. Cut/replacement, projection, extent, destination and resuming a skipped view reset that view. Scene/global invalidation reaches only the affected consumers.

## Physical image lifetime

TextureManager owns each target allocation and its shared TextureGraphImage. All graph imports, sampler aliases, blits, UI bindings and readbacks refer to that physical identity. Track initialization, layout, queue ownership/access and last use together. Reject self-feedback, physical blit aliasing, uninitialized reads and dependency cycles before recording. A scheduled producer supplies this frame's contents; an unscheduled initialized target supplies its previous contents.

Capture requests retain the allocation until GPU completion even when cancelled. Requests made before a batch capture its final writes, including blits; later requests wait for the next frame. EndFrame records final overlay and readbacks. Shutdown completes or faults every request.

## Existing code that must cross the boundary

| Existing owner | Required lifecycle change |
| --- | --- |
| VulkanRenderer camera/history fields and SceneDataBuilder | Move allocation, build-time writes, commit/reset and release together into view ownership. |
| SceneLightingCoordinator | Stop replacing another scene's mirrors and mutating global environment when a second scene is prepared. |
| BindlessHeap reserved bindings | Bank by view and in-flight slot without changing shader indices or asset identity. |
| ProductionPipelineDependencies | Borrow the correct scene/view resources; retain device services as shared dependencies. |
| ProductionRenderPipelineDeclaration | Split scene GPU work from per-view phases and expand actual target dependencies, including GI and auxiliary captures. |
| Swapchain-dependent production passes | Render to view attachments at viewport extent; preserve the direct main-view presentation path. |
| SceneDataBuilder history writes | Stage changes until successful submission; roll back failed recording. |
| ScreenshotReadback / LinearHdrReadback | Reuse completion and atomic PNG machinery for retained RGBA16F target capture. |
| ImGui overlay texture IDs | Add typed retained bindings with fence-based retirement. |

Each row requires observable behavioral evidence from the plan before it is considered complete. Existing reflection-only secondary views are not a substitute for these lifetimes.
