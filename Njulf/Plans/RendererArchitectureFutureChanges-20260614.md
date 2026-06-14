# Renderer Architecture Future Changes - 2026-06-14

Purpose: track only future implementation, validation, migration, and cleanup work discovered while executing RendererArchitecture plans 01-07.

Rules:
- Use this file for deferred or cross-plan work from every renderer architecture plan.
- Do not record completed implementation status here.
- Move an item out of this file, or delete it, only when the replacement is implemented, verified, and any old path is safely removed.
- Keep plan documents as the phase source of truth; keep this file as the future-work queue.

## Plan 01 - Render Graph Ownership

- [ ] Make production graph declaration recompilation settings-aware when feature topology changes after initialization, including bloom mip count, AA mode, AO, fog, motion vectors, TAA, and shadow topology.
- [ ] Replace `RenderTargetManager` scene target allocation with graph-owned image allocation for migrated production resources.
- [ ] Import swapchain, shadow, environment, reflection, and other external Vulkan image/view handles into graph resource records.
- [ ] Feed compiler pass culling into runtime allocation and command recording so disabled features stop allocating placeholder full-size targets.
- [ ] Allocate persistent history images through the graph and keep them separate from transient aliasing.
- [ ] Wire `RenderGraphResolvedImageDesc.HistoryInvalidated` into live TAA/history resource reset.
- [ ] Convert planned image and buffer barriers into production `DependencyInfo` submissions.
- [ ] Replace manual `RenderTarget.TransitionTo...` calls and pass-owned image layout assumptions for graph-owned resources.
- [ ] Generate per-mip and per-layer production barriers for Hi-Z, bloom, cubemaps, and arrays.
- [ ] Add strict runtime validation for undeclared graph resource access during development.
- [ ] Enable transient image aliasing with live Vulkan memory requirement/alignment validation.
- [ ] Report unaliased bytes, aliased bytes, peak transient bytes, and alias group members in diagnostics.
- [ ] Add a debug setting that disables aliasing while preserving expected output.
- [ ] Centralize graph-owned image descriptor writes after graph compilation.
- [ ] Remove resize-only per-pass descriptor rebuild paths for graph targets.
- [ ] Generate viewport and scissor state from graph output extents where practical.
- [ ] Represent renderer-level scene clear, swapchain present, screenshot readback, and capture hooks as graph-declared work where appropriate.
- [ ] Delete old fixed scene-target ownership, manual layout tracking, duplicate descriptor registration, broad compatibility usage flags, and dead resize callbacks after graph ownership is authoritative and verified.
- [ ] Validate graph ownership with full build/tests, resize and dynamic-resolution tests, AO/AA/bloom/fog/shadow toggles, RenderDoc layout inspection, memory snapshots, timestamp comparison, and visual parity.

## Plan 02 - GPU Scene

- [ ] Track future GPU scene migration work here as phases are implemented.

## Plan 03 - GPU Driven Visibility

- [ ] Track future GPU-driven visibility migration work here as phases are implemented.

## Plan 04 - Offline Asset Pipeline

- [ ] Track future offline asset pipeline work here as phases are implemented.

## Plan 05 - Visibility First Frame

- [ ] Track future first-frame visibility work here as phases are implemented.

## Plan 06 - Async Compute

- [ ] Track future async compute migration work here as phases are implemented.

## Plan 07 - Diagnostics First Class

- [ ] Track future diagnostics and validation work here as phases are implemented.
