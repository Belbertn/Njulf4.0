# Remaining framework architecture work

Roadmap items 1–4 now provide executable usage examples, automatic host services, typed resources,
documented ownership and game/graphics/Vulkan access levels on the existing renderer.
They do not complete the broader MonoGame-style framework redesign.

Items 5–6 are partially implemented: unified content loading and host pumping, async startup,
scene membership and model groups, renderer-neutral lights/environments, and schema 12 persistence.
The full approved implementation is not complete. Remaining work from that plan:

- **Views and destinations:** implement `IRenderView`, `IRenderDestination`, `RenderView`,
  `Game.MainView`, viewport composition, blitting and target capture. `RenderTarget2D` now exists
  for custom passes and material sampling; connecting it to a render view remains separate. Isolate
  camera buffers, visibility/LOD, render graphs/attachments, HiZ, temporal histories, exposure,
  and camera-dependent shadows before allowing a second view. Share one frozen scene and
  simulation/GI update per frame; present once. Keep TextureManager as offscreen image owner,
  retain material/view references, reject self-feedback, and support object exclusions.
- **Complete scene migration:** move the editor and the large sample's remaining manager-backed
  light stores to scene ownership, preserving the existing shadow-edit changes. Audit their
  model-placement ownership and migrate complete placements to scene-owned groups while
  preserving incremental single-object admission. The small API model example already uses
  `Scene.Add(ModelInstance)` and scene lights; the legacy light-store adapter remains available.
- **Remaining validation:** combined two-camera/offscreen-monitor example, view/history isolation,
  target resize/disposal under GPU use, same-frame dependencies, and comparable single-view
  performance/allocations. Existing single-view validation is not evidence for multi-view safety.

Later roadmap work:

Items 9–10 establish the Core/Graphics/Input/Assets/Rendering/Framework dependency boundaries
and separate Assets.Tooling from runtime loading. Public resources are abstract Graphics
contracts with internal Vulkan implementations. Typed button actions, namespace cleanup,
shared texture color space, XML output, architecture checks and local package-consumer
validation now support the ordinary API. Specialist API documentation can be improved as
those areas change; the assembly split does not complete the outstanding multi-view work above.

Items 7–8 now provide scoped Vulkan passes, custom targets/buffers, common settings receipts
and supported/active capabilities for the current single view. Multiple views, portable
shader/command abstractions, custom async-queue scheduling and exhaustive specialist-setting
classification were explicitly outside that implementation scope.

- **Resource extensions:** add richer vertex input, texture formats/mips and content conveniences only for demonstrated use cases. Preserve typed ownership and existing managers.
- **API consistency:** as each area changes, align naming, defaults, XML documentation,
  failure behavior and disposal with the runnable examples. Split assemblies only when
  dependency boundaries require it.

Avoid a generic RHI, new backend, scene rewrite, compatibility wrappers or broad manager
extractions as prerequisites. For each follow-up, update one real example and add focused
behavioral validation for the contract that changed.
