# Visibility Renderer Mesh Blinking Root Cause - 2026-06-16

## Context

After implementing `NsightVisibilityRendererOptimizationPlan-20260616.md`, meshes blink in and out intermittently. The failure is most consistent with an indirect visibility synchronization problem, not a mesh shader or rasterization issue.

The new staged visibility path moved meshlet list generation into multiple compute stages:

1. object visibility,
2. visibility count,
3. prefix scan,
4. meshlet expansion,
5. shadow list generation,
6. visibility finalize.

The final stage writes the indirect draw command counts that later graphics passes consume through `CmdDrawMeshTasksIndirect`.

## Primary Root Cause

`GpuVisibilityPass` writes a per-frame visibility counter buffer that also contains all mesh-task indirect draw commands, but that buffer is not modeled as a render-graph resource.

The render graph knows about the meshlet draw buffers:

- opaque meshlet draw buffer,
- solid depth meshlet draw buffer,
- masked depth meshlet draw buffer,
- transparent meshlet draw buffer,
- directional shadow meshlet draw buffer,
- local shadow meshlet draw buffer.

However, it does not know about the visibility counter / indirect command buffer.

This is the buffer returned by:

```csharp
_visibilityBuffers.GetCounterBuffer(frameIndex)
```

That buffer is used by draw passes as the indirect command source:

```csharp
_context.ExtMeshShader.CmdDrawMeshTasksIndirect(
    cmd,
    _bufferManager.GetBuffer(_visibilityBuffers.GetCounterBuffer((int)sceneData.CurrentFrameIndex)),
    _visibilityBuffers.GetIndirectCommandOffset(indirectList),
    1,
    (uint)Marshal.SizeOf<GPUMeshTaskIndirectCommand>());
```

This means the actual draw count is read from a buffer that the render graph does not see. The graph cannot insert correct barriers or queue-family ownership transfers for it.

## Why This Produces Blinking

The indirect buffer decides how many mesh tasks execute for each list.

If the indirect command buffer is stale or not visible to the graphics queue in a given frame, the renderer may see:

```text
count = 0      -> meshes disappear
count = old N  -> stale meshlet commands draw
count = random -> unstable flicker or blinking
```

The draw-command buffers may contain valid data, but the draw never executes if the indirect count is zero. Conversely, stale indirect counts can cause stale draw commands to be read.

This matches the observed symptom: meshes blink in and out after staged GPU visibility was introduced.

## Why The Plan Triggered It

Before the staged implementation, the monolithic visibility path appended draw commands and finalized indirect counts in one simpler shader path. After the plan, the renderer depends on several compute dispatches producing coherent data for subsequent graphics passes.

The new path writes indirect command counts in `gpu_visibility_finalize.comp`, but the render graph does not model the counter buffer as a producer/consumer resource.

`GpuVisibilityPass` does contain manual barriers, but those barriers do not fully solve the render-graph visibility problem because:

- render-graph scheduling is blind to the indirect buffer,
- async queue ownership transfer is blind to the indirect buffer,
- draw passes do not declare the indirect buffer as `IndirectCommandRead`,
- `TryGetGraphBuffer()` cannot resolve the counter buffer for graph-level barriers.

This is especially risky because async compute defaults to aggressive mode and `GpuVisibilityPass` is declared async eligible.

## Affected Files

Likely affected files:

```text
Njulf/Njulf.Rendering/Pipeline/GpuVisibilityPass.cs
Njulf/Njulf.Rendering/Pipeline/DepthPrePass.cs
Njulf/Njulf.Rendering/Pipeline/ForwardPlusPass.cs
Njulf/Njulf.Rendering/Pipeline/TransparentForwardPass.cs
Njulf/Njulf.Rendering/Pipeline/MotionVectorPass.cs
Njulf/Njulf.Rendering/Pipeline/DirectionalShadowPass.cs
Njulf/Njulf.Rendering/Pipeline/SpotShadowPass.cs
Njulf/Njulf.Rendering/Pipeline/PointShadowPass.cs
Njulf/Njulf.Rendering/Pipeline/ProductionRenderGraphResources.cs
Njulf/Njulf.Rendering/VulkanRenderer.cs
Njulf/Njulf.Rendering/GpuScene/GpuVisibilityBufferSet.cs
```

## Required Fix

Add the visibility counter / indirect buffer as a first-class render-graph resource.

### 1. Add a render-graph resource name

In `ProductionRenderGraphResources.cs`:

```csharp
public const string GpuVisibilityCounterBufferName = "GPU Visibility Counter Buffer";

public static RenderGraphResourceHandle GpuVisibilityCounterBuffer(RenderGraphResourceRegistry resources)
{
    return resources.GetOrCreateBuffer(new RenderGraphBufferDesc(
        GpuVisibilityCounterBufferName,
        RenderGraphResourcePersistence.External)
    {
        ByteSize = 1,
        Usage = BufferUsageFlags.StorageBufferBit |
                BufferUsageFlags.TransferSrcBit |
                BufferUsageFlags.TransferDstBit |
                BufferUsageFlags.IndirectBufferBit
    });
}
```

### 2. Declare it as written by `GpuVisibilityPass`

In `GpuVisibilityPass.DeclareResources()`:

```csharp
RenderGraphResourceHandle visibilityCounters =
    ProductionRenderGraphResources.GpuVisibilityCounterBuffer(resources);

pass
    .Write(visibilityCounters, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(opaqueDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(solidDepthDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(maskedDepthDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(transparentDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(directionalShadowDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit)
    .Write(localShadowDraws, RenderGraphResourceAccess.StorageWrite, PipelineStageFlags2.ComputeShaderBit);
```

### 3. Declare it as read by all indirect draw consumers

Every pass that calls `CmdDrawMeshTasksIndirect` with `_visibilityBuffers.GetCounterBuffer(...)` must declare:

```csharp
RenderGraphResourceHandle visibilityCounters =
    ProductionRenderGraphResources.GpuVisibilityCounterBuffer(resources);

pass.Read(
    visibilityCounters,
    RenderGraphResourceAccess.IndirectCommandRead,
    PipelineStageFlags2.DrawIndirectBit);
```

Apply this to:

```text
DepthPrePass
ForwardPlusPass
TransparentForwardPass
MotionVectorPass
DirectionalShadowPass
SpotShadowPass
PointShadowPass
```

### 4. Add the buffer to `TryGetGraphBuffer()`

In `VulkanRenderer.TryGetGraphBuffer()`:

```csharp
ProductionRenderGraphResources.GpuVisibilityCounterBufferName =>
    _gpuVisibilityBuffers.GetCounterBuffer(_currentFrame),
```

This lets the render graph resolve the real Vulkan buffer and emit correct barriers / queue ownership transfers.


## Secondary Correctness Issue: Draw Buffers Are Not Cleared

`GpuVisibilityPass.ClearOutputBuffers()` currently clears only the counter buffer:

```csharp
private void ClearOutputBuffers(CommandBuffer cmd, int frameIndex)
{
    Fill(cmd, _visibilityBuffers.GetCounterBuffer(frameIndex), 0u, _visibilityBuffers.CounterBufferBytes);
}
```

The meshlet draw buffers are not cleared to an invalid sentinel. That is safe only if the indirect counts are always exact and correctly synchronized.

Because task shaders skip commands whose `MeshletIndex == 0xFFFFFFFF`, stale draw-command ranges would be safer if unused draw slots were initialized to that sentinel. This is not the primary root cause, but it makes the indirect-buffer bug more visible.

Possible defensive patch:

```csharp
private void ClearOutputBuffers(CommandBuffer cmd, int frameIndex)
{
    Fill(cmd, _visibilityBuffers.GetCounterBuffer(frameIndex), 0u, _visibilityBuffers.CounterBufferBytes);

    Fill(cmd, _visibilityBuffers.GetOpaqueDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.OpaqueDrawBufferBytes);
    Fill(cmd, _visibilityBuffers.GetSolidDepthDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.SolidDepthDrawBufferBytes);
    Fill(cmd, _visibilityBuffers.GetMaskedDepthDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.MaskedDepthDrawBufferBytes);
    Fill(cmd, _visibilityBuffers.GetTransparentDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.TransparentDrawBufferBytes);
    Fill(cmd, _visibilityBuffers.GetDirectionalShadowDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.DirectionalShadowDrawBufferBytes);
    Fill(cmd, _visibilityBuffers.GetLocalShadowDrawBuffer(frameIndex), 0xFFFFFFFFu, _visibilityBuffers.LocalShadowDrawBufferBytes);
}
```

This is more expensive and should not be the final performance path, but it is useful for validation.

## Secondary Correctness Issue: Removed GPU Scene Objects May Remain Alive

`GpuSceneManager.RemoveObjectLocked()` removes CPU-side slots but does not clearly upload a default / invisible replacement record to the GPU object, bounds, visibility, instance, and transform buffers.

Since GPU visibility iterates the GPU scene high-water mark, removed object slots can remain renderable on the GPU if their old records are not overwritten.

Patch `RemoveObjectLocked()` so removed object slots are zeroed and marked dirty:

```csharp
private void RemoveObjectLocked(GpuObjectId objectId)
{
    ObjectSlot slot = GetObjectSlotLocked(objectId);
    int objectIndex = objectId.Index;
    int instanceIndex = slot.PrimaryInstance.Index;

    ReleaseInstanceSlot(slot.PrimaryInstance);

    slot.Active = false;
    slot.Object = default;
    slot.Bounds = default;
    slot.Visibility = default;
    slot.Generation = NextGeneration(slot.Generation);
    slot.DirtyFlags = GpuSceneDirtyFlags.Object |
                      GpuSceneDirtyFlags.Bounds |
                      GpuSceneDirtyFlags.Visibility;

    _objects[objectIndex] = slot;
    MarkObjectDirty(objectIndex, slot.DirtyFlags);

    _freeObjectSlots.Push(objectIndex);
}
```

Also make `ReleaseInstanceSlot()` clear and dirty the GPU-visible instance and transform records.

## Secondary Correctness Issue: BoundsIndex / VisibilityIndex Semantics

`CreateGpuObject()` sets:

```csharp
BoundsIndex = (uint)primaryInstanceIndex,
VisibilityIndex = (uint)primaryInstanceIndex,
```

But bounds and visibility snapshots are object-indexed, not instance-indexed. This happens to work while object indices and instance indices match, but it is wrong once slots are recycled or object/instance allocation diverges.

Change the call site to pass both indices:

```csharp
GPUSceneObject gpuObject = CreateGpuObject(desc, objectIndex, instanceIndex);
```

Then set:

```csharp
BoundsIndex = (uint)objectIndex,
VisibilityIndex = (uint)objectIndex,
FirstInstance = (uint)primaryInstanceIndex,
```

This prevents object bounds and visibility lookup from drifting after removals or static batch changes.
