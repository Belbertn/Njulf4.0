# Production pipeline ownership

`ProductionPipelineOwner` is an internal assembly and lifetime boundary. It constructs
production pipeline objects and passes, prepares pipeline variants, and recreates them
when the renderer has reached its existing safe boundary. Dependencies are borrowed
through `ProductionPipelineDependencies`; the owner does not dispose those services.

`VulkanRenderer` retains frame ordering, submission, completion/idle waits, startup worker
scheduling and readiness publication. Render targets, shared pipeline caches and subsystem
coordinators retain their existing owners. Pass properties exposed by the pipeline owner
are borrowed references for recording and diagnostics, not a second disposal path.

Construction retains each graph pass until `RenderGraph.AddPass` succeeds. The graph then
owns cleanup, including cleanup after partial initialization. Standalone reflection passes
and auxiliary pipeline objects remain owned by the pipeline owner. A failed assembly never
publishes readiness or silently overwrites its resources on a repeated initialization call;
it retains them for renderer shutdown after worker draining and GPU completion.

The renderer's existing named disposal stages and dependency edges remain authoritative.
The owner supplies pipeline release callbacks to those stages. Graph and pending-pass
cleanup remember successful releases and retry failed ones; graph-owned targets remain
alive until all graph passes finish cleanup. Cleanup implementations must tolerate retry
after a partial release. Constructors that acquire native resources before throwing unwind
their own incomplete object because ownership cannot transfer until construction returns.

Recreation methods do not wait for the GPU themselves. Call them only at the existing
renderer completion/idle boundaries. Keep graph resource declarations, shader layouts,
settings objects and telemetry contracts unchanged when moving additional responsibilities.
