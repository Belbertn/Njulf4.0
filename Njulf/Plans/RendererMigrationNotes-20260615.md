# Renderer Migration Notes - 2026-06-15

## Current invariants

- Production rendering requires GPU-driven visibility. `ForwardPlusPass` consumes indirect draw counts from `GpuVisibilityPass`; the legacy direct `CmdDrawMeshTask` path is no longer a production fallback.
- GPU scene transform history is advanced on the GPU by copying the current transform buffer into the previous-transform buffer at the end of the frame. CPU history is still retained for scene-data/debug lookups, but successful-frame advancement must not dirty and re-upload every previous transform.
- GPU visibility output buffers are append-only for the current frame. The pass clears counters every frame; draw buffers are not full-capacity cleared. Sorted transparency prepares only the padded sort region it needs.
- Staging-ring overflow buffers have frame/fence lifetime. Renderer-created staging rings queue oversize upload buffers against the frame fence instead of retaining them until shutdown.

## Compatibility still present

- `SceneDataBuilder` still owns CPU object payload construction because legacy shaders and debug tooling consume the object-data buffer contract.
- CPU meshlet metadata is diagnostics-only. Release builds skip the duplicate CPU meshlet cache by default; enable `NJULF_CPU_MESHLET_CACHE=1` before loading meshes when meshlet-bound overlays need CPU meshlet spheres.
- `MeshManager.CompactStaticBuffers()` remains an explicit startup/maintenance operation. It must not be called from the render hot path until mesh uploads have a fully deferred residency API.
- Texture uploads still keep blocking helper methods for startup/default texture initialization. Runtime streaming should use the command-buffer upload overload so multiple texture uploads can be batched into the frame graph or an upload queue.

## Follow-up migration targets

- Move the remaining object-data shader readers to GPU scene buffers so the older per-frame object buffer can become diagnostics-only.
- Add a mesh residency/upload queue API for runtime asset streaming; current `RegisterMeshes` returns ready-to-draw handles and therefore still uses a synchronous completion point.
- Replace CPU-driven transparent sort dispatch sizing with a GPU indirect sort/compaction path once sort-key buffers are introduced.
