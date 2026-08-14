# Njulf Editor Mode Plan (Part 2 of 2)

Companion to Part 1 (Cooked Asset Pipeline). Goal: press a shortcut → an ImGui overlay opens in the running game → add/move/edit objects, lights, and materials → save the scene to disk. Not a full editor.

Dependency on Part 1: soft. The editor loads assets through `ContentManager` and benefits from fast cooked loads, and its scene files reference assets the same way Part 1 names packages — but every phase here works against the source-import fallback too, so Part 2 can start once Part 1's Phase 7 (content routing) exists, or even before it on the pure source path.

## Current Baseline (verified against the code)

1. **No scene file format exists.** Scenes are built imperatively (`NjulfHelloGame/SampleSceneLoader.cs`, `SampleAssetManifest.cs` with hardcoded glTF paths). Nothing to save/load.
2. **No ImGui or any UI/text rendering library** in any csproj. `Plans/Complete/Phase18EditorDebugToolingHooksPlan.md` built "editor-ready" hooks but deliberately deferred a UI framework.
3. **Reusable editor foundations already exist** in `Njulf.Rendering/Debugging/`:
   - `IRendererDebugTools` (implemented by `VulkanRenderer`): `SelectedObject { get; set; }`, `TryInspectObject(int, out SelectedObjectInspection)`, `TryFindObjectByName`, `RequestScreenshot`, `RequestRenderDocCapture`.
   - `DebugDrawList`: world-space lines/boxes/oriented-boxes/spheres/frusta with depth-tested/always-visible/x-ray modes, rendered by `Pipeline/DebugDrawPass.cs` — the pattern to copy for an ImGui pass, and the highlight mechanism (`VulkanRenderer.DrawSelectedObjectOverlay`, ~line 1899, draws selected-object bounds).
   - `ObjectDebugSnapshot` (per-object index, name, handles, world matrix, bounds — populated when `Settings.Debug.CpuSnapshotsEnabled`), `SelectedObjectInspection`, `MaterialInspectionResult.FromGpuMaterial` (decodes `GPUMaterialData` for display).
4. **Input**: `Njulf.Input/InputManager` — named actions + bindings, `IsPhysicalKeyDown`, mouse position/delta/scroll, `OnActionPressed` events; updated once per frame from `Game.OnWindowUpdate` (`Njulf.Core/Game.cs:208`). `SampleInputController` (~2380 lines) shows the chord-shortcut pattern (`WasChordPressed`) and already binds ~90 actions — CapsLock toggles debug tooling today.
5. **Scene mutation is already live**: `SceneDataBuilder` re-reads and content-hashes `Scene` collections every `DrawScene`, so transform/visibility/add/remove edits appear next frame with no extra plumbing. Probes, particles, GI volumes are likewise re-uploaded from scene state.
6. **Identity gaps**:
   - Scene entities have only non-unique `Name` strings; no GUIDs.
   - `RenderObject` stores a raw `Matrix4x4 WorldMatrix`; `Position`/`Scale` poke matrix elements and **rotation is not representable as a property** — no TRS storage.
   - Lights are anonymous structs owned by `LightManager` (not `Scene`), addressed by int index with **swap-remove** on `RemoveLight` (indices unstable). Full CRUD exists (`AddLight`/`UpdateLight`/`RemoveLight`, thread-safe, revision-tracked).
   - `MaterialManager` is content-addressed + ref-counted with **no update-in-place API** (`_gpuUploadDirty` is only set inside registration).
7. **Picking gap**: `Njulf.Core/Math/Ray.cs` has `Intersects(BoundingBox, out float)` and `Intersects(BoundingSphere, out float)`, but no screen-ray/unproject helper exists and `Ray` is unused for selection today. `CameraBase` exposes view/projection matrices; `FirstPersonCamera` is the sample's fly camera.

## Decisions already made (with the user)

1. ImGui binding: **Hexa.NET.ImGui** (docking branch; maintained; ImGuizmo/ImPlot companions for later).
2. Editor v1 interaction: **panels + mouse picking** — click to select in the viewport, edit via drag fields. Gizmos are explicitly post-v1.
3. Ordering: this plan runs **after** Part 1 (pipeline first).

## Target Architecture

- **`Njulf.Editor`** — new project (references `Njulf.Core`, `Njulf.Rendering`, `Njulf.Assets`, `Njulf.Input`, Hexa.NET.ImGui). All panels, picking, editor state. Shipping games simply don't reference it.
- **`.njscene.json`** — human-readable JSON *source* scene document; written by the editor, loaded by the runtime in development. Part 1's Phase 10 later compiles it to a cooked binary `.njscene`; the editor only ever writes JSON.
- **ImGui render pass** — lives in `Njulf.Rendering` (it needs Vulkan internals), modeled on `DebugDrawPass`, fed by ImGui draw data each frame; inert unless an editor/UI service is registered.
- **Identity layer** — GUIDs on scene entities, TRS on `RenderObject`, generation-checked `LightHandle`s. This is runtime/core work (`Njulf.Core`), not editor work, and is the only part the rest of the engine must absorb.

## Implementation Phases

### Phase 1: Stable Identity And Transform Groundwork (`Njulf.Core`)

Prerequisite for both serialization and selection; no UI yet.

1. **GUIDs**: add `Guid Id { get; init; }` (default `Guid.NewGuid()`) to `RenderObject`, `StaticInstanceBatch`, `FoliagePrototype`, `FoliagePatch`, `ReflectionProbe`, `GlobalIlluminationProbeVolume`, `ParticleEffectInstance`. Add `Scene.FindById(Guid)` over the collections.
2. **TRS on `RenderObject`**: store `Position` (Vector3), `Rotation` (Quaternion), `Scale` (Vector3) as the source of truth; compose `_worldMatrix` lazily (existing `_dirty` flag). Keep `WorldMatrix` get/set for compatibility — the setter runs `Matrix4x4.Decompose` and falls back to storing the raw matrix (with a `HasNonTrsMatrix` flag) if decomposition fails (shear). Audit existing writers: `SampleSceneLoader` sets `WorldMatrix` from `manifest.CreateModelWorld` — must keep working. `SkinnedRenderObject` inherits unchanged.
3. **Light handles**: add a `LightHandle` (readonly struct: `int Slot`, `int Generation`) issued by `LightManager.AddLight`. Internally keep a slot table (`slot → array index`, `array index → slot`) maintained across the existing swap-remove, and bump generation on free — the packed GPU array and `GetFrameSnapshot` stay exactly as they are. Add `UpdateLight(LightHandle, in Light)`, `RemoveLight(LightHandle)`, `TryGetLight(LightHandle, out Light)`, plus an optional `string? Name` per slot. Keep the old int-index API delegating to slots during migration, then remove it.
4. **Tests** (`Njulf.Tests`): TRS compose/decompose round-trip (incl. negative scale and non-decomposable matrix fallback), handle validity across interleaved add/remove (generation catches stale handles), `Scene.FindById`.

Acceptance: engine builds and the sample behaves identically; every scene entity and light is addressable by a stable id; a stale `LightHandle` is detected, not silently pointing at another light.

### Phase 2: Scene Source Format `.njscene.json` (`Njulf.Assets`)

1. **Schema** (versioned, `"schemaVersion": 1`):
   - Scene: id, name, ambient light.
   - `objects[]`: id, name, model reference (`{ "path": "...", "subObject": "<name|index|*>", "contentHash": "<optional>" }`), position/rotation(quaternion)/scale, visible, isStatic, optional material overrides.
   - `lights[]`: id, name, all `Light` fields (type, position, direction, color, intensity, range, spotAngle, shadow settings).
   - `reflectionProbes[]`, `giProbeVolumes[]`: all fields of the existing classes.
   - `instanceBatches[]`: id, name, model reference, instance matrices (or TRS array).
   - `foliagePrototypes[]` / `foliagePatches[]`: prototype settings + patch bounds/density/seed, prototype referenced by id.
   - `particleEffects[]`: effect reference + instance transform/seed.
   - `dependencies[]`: distinct asset paths (+ cooked content hashes when available) — lets the loader prefetch and Part 1's tooling validate.
2. **Serializer**: `SceneDocument` DTO tree + `SceneDocumentJson` (System.Text.Json, same conventions as `AssetValidationJson`). Deterministic output for round-trip stability: fixed property order, invariant culture, `"R"`-style float formatting, arrays sorted by id where order is not semantic, indented.
3. **Loader** `SceneDocumentLoader` (in `Njulf.Assets`, renderer-agnostic): for each object record, `ContentManager.Load<Model>(path)` (cooked-first via Part 1 routing, source fallback otherwise) → `Model.CreateInstance()` → select sub-objects → apply id/name/TRS/flags → `scene.Add`. Lights via `LightManager.AddLight` keeping ids ↔ handles in a scene-side map. Probes/foliage/particles constructed directly from the document.
4. **Writer** `SceneDocumentWriter`: builds a `SceneDocument` from a live `Scene` + the light table; atomic write (temp file + `File.Move` overwrite).
5. **Sample conversion**: one-off export — run the current code-built sample, call the writer, commit `NjulfHelloGame/Scenes/SampleScene.njscene.json`; then switch `SampleSceneLoader` to load it. Keep the code-built path behind `NJULF_SAMPLE_CODE_SCENE=true` during migration.
6. **Tests**: load→save→save is byte-identical; load→save→load produces an equal document; missing model path fails with the offending record's id/name in the message; unknown `schemaVersion` rejected; unknown *fields* ignored with a warning (forward compatibility).

Acceptance: `NjulfHelloGame` loads the sample from `.njscene.json` (over cooked assets when present); round-trips are byte-stable; the manifest/hardcoded-path route is fallback-only.

### Phase 3: ImGui Integration (Hexa.NET.ImGui + Vulkan pass)

1. **Packages**: `Hexa.NET.ImGui` (+ `Hexa.NET.ImGui.Docking` variant if split) referenced by `Njulf.Editor`; `Njulf.Rendering` gets the minimal reference needed for draw-data access (see Open Decisions for the alternative of an abstraction layer).
2. **Render pass** `ImGuiRenderPass` in `Njulf.Rendering/Pipeline/`, cloned structurally from `DebugDrawPass`:
   - Own pipeline + shaders (`Njulf.Shaders/imgui.vert/.frag`: pos2/uv2/col4 vertex layout, scissor rects, ortho projection from display size; render into the final swapchain target after post-processing so UI is never tonemapped).
   - Font atlas uploaded once as a texture (reuse `TextureManager` upload utilities or a dedicated image; bindless not required — one descriptor set is fine).
   - Per-frame-in-flight growable vertex/index buffers (same staging/`FenceBasedDeleter` utilities `MeshManager` uses).
   - Handles `ImDrawData` command lists: vertex/index copy, clip rect → scissor, texture id → descriptor.
3. **Frame lifecycle** in `Game`/`VulkanRenderer`: `ImGui.NewFrame` (delta time, display size, framebuffer scale) at the start of update; UI code runs during update; `ImGui.Render` before `DrawScene` records the pass. All behind an `IImGuiHost` service — when it's not registered (headless, benchmarks, shipping), nothing initializes.
4. **Input bridge** in `Njulf.Editor`: pump `InputManager`/Silk.NET events into ImGui IO (mouse pos/buttons/wheel, keys, text input via keyboard char events). When `io.WantCaptureMouse` / `WantCaptureKeyboard`, suppress game input — add a `bool SuppressGameInput`-style gate that `SampleInputController` and camera movement respect. Cursor: editor mode shows the OS cursor (Silk.NET `CursorMode.Normal`), game mode restores raw/hidden mode.
5. **Sanity check**: render `ImGui.ShowDemoWindow()` over the sample scene.

Acceptance: demo window renders and is fully interactive at negligible cost when hidden; typing in a text field does not move the camera; clicking a window does not pick/shoot in-game; headless/benchmark runs are byte-for-byte unaffected.

### Phase 4: Editor Shell, Hierarchy, And Mouse Picking (`Njulf.Editor`)

1. **`EditorController`** (an `IUpdateable` added to the scene, or a service ticked by the game): owns editor state — `Enabled`, current `EditorSelection` (`enum kind Object/Light/Probe/GiVolume/Foliage/Particle/Batch` + `Guid`/`LightHandle`), dirty flag.
2. **Toggle**: `Ctrl+Keypad1` flips `Enabled`: shows/hides all panels, switches cursor mode, and pauses camera-look while a panel is hovered. Plain `F10` remains the meshlet-debug shortcut.
3. **Hierarchy panel**: collapsible sections per scene collection (`RenderObjects`, Lights, `ReflectionProbes`, `GlobalIlluminationProbeVolumes`, `FoliagePatches`, `ParticleEffects`, `StaticInstanceBatches`) with a filter text box; entries show name (+ id tooltip); click selects.
4. **Selection highlight**: on selection change, resolve the renderer object index (extend `ObjectDebugSnapshot` with the entity `Guid`, or match by name/index) and set `VulkanRenderer.SelectedObject` so the existing `DrawSelectedObjectOverlay` bounds-box highlight just works; non-object selections (lights/probes) draw via `DebugDrawList` (sphere at light position, box for probes) each frame while selected.
5. **Mouse picking**:
   - Add `CameraBase.ScreenPointToRay(Vector2 screenPos, Vector2 viewportSize)`: NDC → inverse(view·projection) unproject near/far → `Ray`.
   - On left-click with editor enabled and `!io.WantCaptureMouse`: test the ray against every visible `RenderObject`'s world AABB (`LocalMeshBounds` transformed by `WorldMatrix`; `Ray.Intersects(BoundingBox, out float)` exists) and against a small `BoundingSphere` per light position; select the nearest hit, or clear selection on miss.
   - AABB-only picking is accepted v1 precision (triangle-accurate picking is post-v1).
6. **Tests**: screen-ray unproject (center of screen → camera forward; corners hit frustum corners), picking chooses nearest of two overlapping boxes.

Acceptance: `Ctrl+Keypad1` opens the editor with a working cursor; clicking an object or light in the viewport selects and highlights it; hierarchy and viewport selection stay in sync; game input is fully suppressed while ImGui captures it.

### Phase 5: Inspector And Edit Operations

1. **Object inspector** (selection kind Object): name text box, `Visible`/`IsStatic` checkboxes, `DragFloat3` position, rotation displayed as Euler degrees (stored as the Phase 1 quaternion; conversion helpers with gimbal-safe readback), `DragFloat3` scale. Edits write straight to the `RenderObject` — `SceneDataBuilder` picks them up next frame.
2. **Light inspector**: type combo (Point/Directional/Spot), position/direction (`DragFloat3` + "set to camera" button), `ColorEdit3`, intensity/range/spot-angle drags, shadow toggles/strength — all through `LightManager.UpdateLight(LightHandle, in Light)` from Phase 1.
3. **Material inspector**: for the selected object's `MaterialHandle`, decode current values (`MaterialManager.GetMaterialData` + `MaterialInspectionResult`-style presentation, texture slot names via `GetMaterialTextures`/metadata). Editable v1 fields: albedo, emissive (+ strength), metallic, roughness, normal scale, alpha cutoff.
   - **New API — `MaterialManager.UpdateMaterial(MaterialHandle, in GPUMaterialData)`**: mutates the slot, sets `_gpuUploadDirty`, and **invalidates the content-address dedup entry** for that slot (remove its `MaterialRegistrationKey` from the dictionary, or mark the slot non-dedupable) so future `RegisterMaterial` calls can't alias an edited material. Ref-counting untouched. (Fallback if this proves invasive: editor-side re-register + swap `renderObject.Material`, releasing the old handle — see Open Decisions.)
4. **Add**: toolbar/menu —
   - *Add object*: pick a model from the scene's dependency list (or any loaded model), `Model.CreateInstance()`, place at camera position + forward offset, select it.
   - *Add light*: point/spot/directional at camera position with sane defaults, select it.
5. **Delete**: removes the selected entity (`scene.Remove(...)` overloads / `LightManager.RemoveLight(handle)`); disposal follows the scene's existing owned-disposable refcounting; selection cleared.
6. Every mutation sets the editor dirty flag (drives the "unsaved changes" marker in Phase 6).

Acceptance: an object can be clicked, moved, rotated, scaled and the change is visible next frame; a light's color/intensity and a material's albedo/roughness can be edited live; objects and lights can be added and deleted without leaks (validation layers clean, ref counts balanced).

### Phase 6: Save To Disk And Round-Trip

1. **Save** (button + Ctrl+S while editor is open): `SceneDocumentWriter` (Part 2 Phase 2) serializes the live scene + light table to the `.njscene.json` the scene was loaded from; atomic write; previous file kept as `.njscene.json.bak` once per session. Title/menu shows `*` when dirty.
2. **Save As / path display** so scenes not loaded from a file (code-built) can still be exported.
3. **Reload** command: clear scene (`ClearAndDispose` + `ClearLights`) and re-run `SceneDocumentLoader` from disk — the fast way to verify persistence without restarting.
4. New entities get GUIDs at creation (Phase 1 defaults), so save→load→save stays byte-identical even after adds.
5. **End-to-end test** (headless-safe integration test): load sample document → programmatic edits via the same code paths the panels call (move object, edit light, add object) → save → load into a fresh scene → assert equality of documents.

Acceptance: edit → save → restart the game → the edited scene loads exactly; reload-in-place matches restart behavior; `.bak` protects against a bad save.

### Phase 7: Post-v1 Backlog (explicitly out of v1)

Transform gizmos (Hexa.NET.ImGuizmo), undo/redo command stack, multi-select, asset browser over Part 1's `.njassetdb` (spawn any cooked model, assign textures), creating new materials from scratch, triangle-accurate picking, foliage/probe/particle *editing* panels (v1 lists and selects them; editing can start read-only), prefab/instancing workflows, cooked binary `.njscene` (Part 1 Phase 10), cook-on-save hook.

## Suggested File Layout

```text
Njulf/
  Njulf.Editor/                      (new project)
    EditorController.cs, EditorInputBridge.cs
    Panels/ HierarchyPanel.cs, InspectorPanel.cs, MaterialInspector.cs, EditorMainMenu.cs
    Picking/ ScreenRayPicker.cs
  Njulf.Rendering/Pipeline/ImGuiRenderPass.cs
  Njulf.Shaders/imgui.vert, imgui.frag
  Njulf.Assets/Scenes/ SceneDocument.cs, SceneDocumentJson.cs, SceneDocumentLoader.cs, SceneDocumentWriter.cs
  NjulfHelloGame/Scenes/SampleScene.njscene.json
```

## Open Decisions

1. **Where the ImGui dependency sits**: `Njulf.Rendering` referencing Hexa.NET.ImGui directly (simple, recommended) vs an `IOverlayDrawData` abstraction keeping the renderer ImGui-free (cleaner layering, more code). Either way the pass code lives in `Njulf.Rendering`.
2. **Material edit mechanism**: `UpdateMaterial` with dedup-key invalidation (recommended) vs editor-side re-register + handle swap (zero `MaterialManager` changes, but leaks handle churn into the editor and any code holding the old handle).
3. **Light ownership**: keep lights in `LightManager` behind handles (recommended, minimal churn) vs moving light *definitions* into `Scene` with `LightManager` as GPU mirror (cleaner model, bigger refactor).
4. **Rotation UI**: Euler-degrees display with quaternion storage (recommended) vs raw quaternion fields.

## Definition Of Done (Part 2)

1. One shortcut toggles the editor overlay in the running sample, with correct cursor and input capture handover.
2. Objects, lights, and materials can be added (objects/lights), selected by viewport click or hierarchy, moved, and edited live via the inspector.
3. The scene saves to `.njscene.json` and reloads identically after a restart; the sample scene itself ships as a `.njscene.json` loaded through the standard path (cooked assets when present).
4. Headless, benchmark, and shipping configurations carry zero editor/ImGui cost — `Njulf.Editor` unreferenced means nothing initializes.
5. Engine-side additions (GUIDs, TRS, `LightHandle`, `UpdateMaterial`, screen-ray) are tested in `Njulf.Tests` independently of the UI.
