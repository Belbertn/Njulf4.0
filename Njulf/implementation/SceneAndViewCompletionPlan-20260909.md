# Scene and view completion plan

Planning reference: `10440367`, 2026-09-09, with uncommitted production pipeline ownership work. Preserve that work and recheck the working tree before implementation. This plan closes the scene/view remainder of items 5–6 in [FrameworkApiRemaining](../docs/FrameworkApiRemaining.md).

Deliver multiple cameras, scene-to-texture rendering, viewport composition, a minimap and an editor preview through the ordinary API. Each view must retain independent rendering history. Complete authored-light and model-placement ownership migration in the editor and sample.

Keep one device/window and the existing renderer, managers, production shaders and resource retirement system. No new backend, generic rendering abstraction, editor redesign, prefab system, shader optimization campaign or new settings serialization. Before source edits, collect the single-view image/timing reference required by step 7. Keep secondary public views unavailable until their state is isolated.

## Reviewed execution order and contract amendments (2026-09-10)

These amendments supersede the original numbering below. Implement baseline capture, authored scene lights and complete model ownership first (original steps 4–5); isolate renderer state while retaining one view (step 2); complete orthographic projection correctness; then expose and schedule views/targets (steps 1 and 3), integrate the example/preview (step 6), and close the evidence gates (step 7).

- Orthographic support includes shadow frusta, tiled-light prisms, view directions, sky rays, fog origins/distances, AO/reflection reconstruction and CPU/GPU LOD. A camera class alone is insufficient. Preserve orientation across camera changes and do not disable effects silently.
- Scene identity is reference identity, not an editable scene GUID. Scene state includes scene acceleration structures, reflection-probe scheduling and effective environment/imported-shadow policy. View state includes automatic planar reflections and camera-dependent shadows. Release GPU scene state after its last view without disposing the borrowed scene. Legacy manager registrations belong to the default/main scene.
- Allocate descriptor banks per view and in-flight slot, preserving binding indices and shared asset IDs. Never overwrite banks used by recorded work. Publish changed bindings safely, allocate lazily, unwind failures, and share immutable pipelines. The eight reflection capture slots are not public views.
- MainView anchors its scene's world GI. The oldest live view anchors another scene, independently of batch order or skipped frames. Only the anchor recenters/provides feedback; an anchor handoff invalidates consumers. Scheduling anchor-only work must not advance an unscheduled view's history.
- Expand dependencies over scene GPU work, view phases, auxiliary captures and native declared reads. Freeze each CPU scene snapshot once. GI follows anchor shadows where necessary. Compute target reads from effective materials after exclusions; apply exclusions to raster, ray and auxiliary rendering without changing shared Scene visibility.
- The monitor is an ordinary opaque unlit material. Its consuming camera may expose/tone-map it again. Avoid unnecessary shared-GI texture evaluation for an unlit surface that neither reflects nor emits GI, preserving its occlusion. Reject genuine GI feedback cycles rather than using stale content.
- Track initialization, layout, queue access and retirement by physical allocation using the shared TextureGraphImage across graphs, sampler aliases, UI, blits and capture. One producer per target; scheduled producers supply current-frame data, otherwise only initialized prior data is valid.
- Validate/freeze the entire batch before recording. Reject duplicate views and a second batch within a successful BeginFrame/EndFrame; allow an empty batch for blits. Viewports use positive, in-bounds integer framebuffer rectangles. Reject disposed/foreign wrappers before retaining ownership; retained bindings survive wrapper disposal.
- Record all work before submission. Commit histories only after successful submission, including successful submission followed by failed presentation. Recording failure commits nothing; partial submission retains resources through recovery and invalidates affected histories. Reset on cut/replacement, projection, extent, destination and resumed skipped views, plus relevant scene/global changes.
- Render at viewport extent. Clear the backbuffer once and compose in caller order; later rectangles overwrite overlaps. Fully clear a produced offscreen target so pixels outside its viewport are defined. Preserve the direct full-window single-view path.
- RGBA16F targets contain display-linear output after exposure/tone-mapping/AA. Blits scale/overwrite without another tone-map and reject physical aliasing. Apply the display transfer once for presentation or PNG. Final overlay and captures occur in EndFrame after blits. Legacy custom stages run once when MainView is included; diagnostics remain available after the batch.
- Capture requests queued before the batch observe final target contents that frame; later requests apply next frame. Decode RGBA16F to linear values, convert to sRGB RGBA8, then use the atomic PNG writer. Cancellation must not release GPU resources early; shutdown resolves every request.
- Provide a disposable retained ImGui ITexture binding and defer its retirement by actual last use. Preview resize creates/rebinds/releases resources without DeviceWaitIdle; use DPI framebuffer dimensions, local picking coordinates, explicit scene updates, collapsed-panel skips and close cleanup.
- Migrate every ModelLightRuntimeController attachment origin: Attach returns an existing controller and does not rebind its store. Preserve suspended suns, imported overrides, IES references, explicit shadow disables/limits, schema 12 and legacy loading.
- Extend evidence with orthographic lighting/shadow/sky/fog/depth renders and zoom/aspect/clip/orientation/picking; separate-scene anchor behavior; initialized/outside/overlap rectangle colors; expected additional monitor tone-mapping; capture cancellation/errors and async resource replacement. Report actual renderer CPU/GPU timing, not presentation intervals. GPU evidence skipped or unavailable remains an incomplete gate.

## Detailed work areas

Implementation checkpoint (2026-09-10): the baseline and authored scene-light/environment/model-placement migration are implemented and have focused evidence in [the ownership milestone](../docs/performance/milestones/20260910-scene-ownership.md). The timing tail gate remains open. Renderer isolation and the public multi-view/projection/composition/capture/example/preview stages below are still pending; this plan is not complete.

1. **Define the small public contract and route the existing main view through it.**

   Work in `Njulf.Graphics/Graphics`, `Njulf.Graphics/Interfaces/IRenderer.cs`, `Njulf.Rendering/Graphics` and `Njulf.Framework/Game.cs`.

   - Add `IRenderView`, `IRenderDestination` and the device-created, disposable `RenderView`, following the existing abstract Graphics contract/internal Vulkan implementation pattern. A view has stable identity, borrowed `Scene`/`ICamera`, retained destination, optional pixel `Viewport`, stable scene-object GUID exclusions and `ResetHistory()`.
   - Add `GraphicsDevice.CreateRenderView(scene, camera, destination)` and borrowed `GraphicsDevice.BackBuffer`. Make the existing `RenderTarget2D` implement `IRenderDestination`; TextureManager remains its image/descriptor owner.
   - Add `Game.MainView`, `IRenderer.DrawView(view)` and `DrawViews(ReadOnlySpan<IRenderView>)`. Submit one complete view batch between the existing BeginFrame/EndFrame. Reject a second batch; callers pass all cameras together. Keep `DrawScene(scene, camera)` using the persistent main-view state, with no temporary view allocation.
   - Default Game.Draw draws MainView. Preserve Game.Scene/Camera, startup, and ExchangeScene ownership; exchanging the scene rebinds MainView and invalidates its history. Game owns/releases MainView before Scene and renderer shutdown. Dispose additional views in Unload; they borrow their scenes rather than disposing them. Renderer shutdown drains remaining view GPU resources.
   - Freeze camera matrices for rendering; do not change a shared camera's aspect ratio while recording. Game continues resizing its default camera. Additional view owners update their cameras when their viewport size changes. Add a small `OrthographicCamera` using the existing reverse-Z `Matrix4x4.CreateOrthographic` for minimaps.

   Specify device-thread access, disposed/foreign-resource failures and viewport bounds in XML comments. Null viewport means the full destination; rectangles use top-left framebuffer pixels. Minimized windows suspend frames. Additional per-view quality presets are outside this change; existing renderer quality settings apply to every view.

2. **Separate frame, scene and view state before enabling multiple views.**

   Start at `VulkanRenderer.DrawScene`, `Data/SceneDataBuilder.cs`, `Data/SceneDataBuilder.SecondaryViews.cs`, `Pipeline/ProductionPipelineOwner.cs` and `ProductionPipelineDependencies.cs`. Use a short ownership checklist beside the implementation, not a repository-wide extraction project.

   | Owner | State and work |
   | --- | --- |
   | Renderer/device | Upload pump, asset allocations, immutable pipelines/caches, submission/completion, swapchain and presentation. |
   | `SceneRenderState` | Frozen object/material/light/environment data; skinning, particle simulation and world-space GI updates. Prepare once per referenced scene per frame and reuse across its views. |
   | `RenderViewState` | Camera/previous matrices, jitter/sample counter, visibility/LOD/HiZ, particle sorting, tiled-light and draw buffers, descriptors, graph/pass instances and attachments, motion/temporal surface validity, TAA/AO/SSGI/reflection/fog/screen-space GI histories, exposure, camera-dependent shadow selection/maps/history, and view diagnostics. |

   Move each mutable state's allocation, writers, invalidation and retirement together. Parameterize production assembly with scene/view dependencies; keep simulation/GI passes out of per-view replay. Share immutable pipelines where safe. Do not construct another VulkanRenderer or overwrite global camera/descriptors between recorded views. Every recorded view and in-flight frame needs storage valid until completion. Existing `SecondaryViewRenderer`/`SecondaryViewResources` supply useful capture patterns, but their reflection-only path and eight capture slots are not the public view implementation.

   Support a separate editor preview scene without replacing another scene's light/environment buffers or GI state. MainView anchors camera-relative world GI for its scene; for another scene use its first-created live view as a stable anchor. Other cameras consume that scene's GI result without advancing/recentering it. Keep screen-space GI state per view. Scene.Update remains the application's update responsibility; drawing never advances gameplay time.

   Advance a view's temporal state only after successful submission containing that view. Reset only the affected view on camera cut/replacement, scene replacement, projection/extent change, destination replacement or resuming after skipped frames. Scene-wide changes invalidate that scene's affected consumers; global settings changes invalidate affected views. Disposal/reuse must never resurrect old history. Preserve existing Vulkan completion, async-queue dependencies and partial-failure cleanup.

3. **Render the batch, connect targets, then compose and capture.**

   Keep frame ordering visible in VulkanRenderer: validate/freeze the batch, prepare each scene once, record dependent views, compose output, run the main overlay, submit and present once. Keep diagnostics available after DrawViews as they are after DrawScene today; preserve the existing single-view telemetry names.

   - Build a small stable producer/consumer order from effective material target reads and existing declared native-pass resources, including auxiliary rendering that samples targets. Apply view-local exclusions before deriving reads, without changing shared scene visibility. A target has at most one view producer per frame; multiple views may compose into BackBuffer in caller order. Reject self-feedback, dependency cycles and uninitialized reads before production recording. A scheduled producer supplies its current-frame image; otherwise use the last initialized contents with normal GPU dependencies. Never substitute stale contents to break a cycle.
   - Import retained target allocations into existing graph/barrier tracking. Track physical image/layout and last use across view graphs, material sampling, blits and readbacks. Do not give each graph an independent claim that the same external image starts undefined. Preserve safe scheduling for async producers/consumers.
   - Render each view with independent attachments at its viewport extent. Composite inside its destination rectangle, clearing the backbuffer once; later rectangles overwrite earlier overlaps. Preserve the current full-window single-view path without adding an obligatory copy. Add `IRenderer.Blit(source, destination, viewport)` for a retained texture copy/scale after the batch and before the final overlay. Reject source/destination aliasing. Existing unscoped native passes remain attached to MainView and run once.
   - Use existing RGBA16F targets for final display-linear view output, including that view's exposure/postprocessing. Blit does not tone-map again; presentation/PNG encoding performs the required display transfer conversion. Preserve the current single-view color result.
   - Add `GraphicsDevice.CaptureAsync(target, pngPath, cancellationToken)` using existing readback/encoding machinery. Queue requests before DrawViews for that frame; later requests apply next frame. Capture after the producer and complete after GPU readback/file writing. Retain the allocation through completion/cancellation and surface failures. No separate capture renderer or new image-format suite.
   - Resize a target by creating a replacement, rebinding the view and its material/UI consumers, then disposing the caller's old wrapper. Views, bindings and pending captures hold independent references. Apply changes at the existing safe boundary; defer native release by actual last use, including async work. No per-view/resource DeviceWaitIdle.

4. **Move editor/sample authored lights to their scenes.**

   Update `Njulf.Editor/EditorController.cs`, `EditorSelection.cs`, light panels, `NjulfHelloGame/SampleSceneLoader.cs`, `SampleLighting.cs` and Program's lighting/transition paths. Follow remaining direct light writes into input, stress rigs and capture fixtures; migrate authored light creation/edit/removal there too.

   Use `SceneLightStore(scene)` for persistence and `ModelLightRuntimeController`; rebind the store/controller on SetScene and scene handoff. Editor selection and commands use stable scene light IDs and `SceneLight`/documents, so adding/editing a light works before GPU mirroring. Resolve optional renderer shadow diagnostics separately from authored identity. SceneLightingCoordinator performs renderer mirroring; preserve the legacy manager adapter for advanced consumers without using it for ordinary editor/sample ownership.

   Preserve imported-light enablement, suspended suns, overrides, IES references, shadow edits, explicit renderer disables, allocation limits and save/reload. Keep authored environment values on Scene.Environment. Scene transitions/cancellation must not clear another scene's lights or leak preview environment/shadow-policy changes into the main scene. Keep schema 12 and existing legacy document loading.

5. **Finish model placement ownership without breaking incremental loading.**

   Audit complete-instance creation in SampleSceneLoader, SampleAnimatedCharacter, SampleGiAllOnSceneRig, SampleKhronosMaterialGiRenderedScene and editor commands. Configure complete `ModelInstance` placements, then transfer them with `Scene.Add(instance)`; retain borrowed children only for transforms, selection and diagnostics. Release the unattached instance on failure. Group deletion/detachment uses the owning instance; add only a small Scene owner lookup if editor commands need it.

   Preserve `AdvancePreparedAssetAttachment` and `CreateRenderObjectInstance(index)` for progressive loading and explicit sub-object placement. EditorController.AddObject currently creates a whole instance to select one child: use a single-object clone with failure cleanup instead. Do not infer groups from matching asset paths/transforms, regroup streamed objects, or force foliage/static batches into ModelInstance. Existing per-object scene documents remain per-object records.

   Adjust `SampleScenePreparationWork` cancellation and transition/residency cleanup for grouped members; never remove a borrowed child through Scene.Remove. Preserve the existing incremental admission/release budgets and template/instance resource retention.

6. **Exercise the API in one sample and one small editor preview.**

   Add `--example views` in `Njulf.ApiExamples`: a main camera plus an orthographic camera rendering to a texture, an unlit in-scene monitor sampling that texture, and the same image blitted as a minimap inset. Exclude the monitor from its producer view. Reuse the finite-frame/validation/capture controls; demonstrate independent camera movement and target replacement without custom Vulkan code in the example.

   Add one preview panel using a separate scene/camera/RenderView and a retained target texture in the existing ImGui overlay. Reuse typed texture ownership for its UI binding; do not create unmanaged raw image ownership. Resize/rebind with panel dimensions and release when closed. Use viewport-relative coordinates for picking. Keep this to a working preview, without adding docking, thumbnail caches or an editor scene-management framework.

7. **Close with focused evidence and update the remaining-work notes.**

   Reuse existing NUnit and Vulkan harnesses. Add tests for observable behavior, not private field names/source strings.

   | Contract | Required observation |
   | --- | --- |
   | View isolation | Compare deterministic view A alone versus A with B; move/cut/resize/skip B and reverse independent-view order. A's image, exposure and history progression remain equivalent. Include objects visible only to B, temporal effects and camera-dependent shadows. Count one shared simulation/GI update. |
   | Dependencies/composition | A changing frame marker reaches the monitor/minimap in the same frame; check rectangle boundaries and colors. Feedback, cycles and uninitialized reads fail clearly. |
   | GPU lifetime | Replace/dispose target, view and UI/material references with frames in flight and a pending capture. Verify captured dimensions/content, retained old bindings and zero validation errors; cover recreation/failure cleanup and an existing async validation path. |
   | Scene migration | Two scenes retain separate lights/environments; editor commands work before mirroring. Save/reload imported overrides/IES/shadows. Complete-group removal, standalone placement and cancelled sample preparation release resources once. Extend the nearest scene/editor/transition tests. |
   | Single-view regression | Same workload/settings/resolution, 120 warmup + 480 measured frames, validation/capture off: compare CPU/GPU p50/p95/p99 and bytes allocated/frame. No new steady per-frame allocations. Investigate repeatable increases above 3% p50 or 5% p95/p99; repeat once if noisy. |

   Build `Njulf.Tests/Njulf.Tests.csproj` and `Njulf.ApiExamples/Njulf.ApiExamples.csproj` in Development; run only new view tests and affected scene/editor/imported-light/transition/lifecycle tests. Compile/validate affected shaders if changed. Run the combined example with `--example views --frames 600 --validation --capture <fresh-workspace-path>.png` and inspect the output. Use a focused rendered oracle for temporal isolation; a clean validation log alone does not establish correct histories. Unavailable GPU evidence remains an explicit incomplete gate.

   Update `docs/FrameworkApi.md` and `docs/FrameworkApiRemaining.md` only for verified behavior. Save one compact milestone with revision/dirty state, hardware/settings, commands, timings/tails, quality results, rejected candidates and limitations. Keep generated evidence on D: under this workspace and apply the AGENTS.md artifact-retention policy. Stop after these contracts pass; do not expand into full-suite, all-preset or broad performance qualification.
