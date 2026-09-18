# TAA history round-trip fix — Njulf4.0 `Simplified-SDF`

## Context

`46fdef4` + `9b440c5` implemented the previous plan correctly (verified line by line), yet
TAA got progressively louder rather than quieter. The debug captures show why: the resolve
maths is now right, but **the history texture the shader samples is not the one the previous
frame wrote**, so nothing ever accumulates. Each fix we landed removed something that had
been accidentally damping the raw jittered frame, which is why the symptom escalated.

## What the evidence proves

From `performance-20260918-115602` / `-115652` (both `AntiAliasingMode = Taa`,
`ValidationErrorMessageCount = 0`, `ValidationMode = Standard`):

- `MotionVectorsEnabled = 1`, `JitterEnabled = 1`, `JitterX/Y` ≈ 0.3–0.4 px at 1600×900 —
  jitter amplitude is correctly sub-pixel, and the validity chain's hardest input is on.
- `AntiAliasingWidth/Height = 1600×900` matches the swapchain (`AntiAliasingRenderTargetBytes
  = 40320000` = 1600·900·28 B), so there is **no** render-scale/extent mismatch.
- `TemporalSampleIndex = 4510` — thousands of frames of settling time.

From debug view 5 (MotionVectors): the frame is uniformly `(0.5, 0.5, 1.0)`.
RG = 0.5 → **velocity is exactly zero everywhere** (the jitter-free motion-vector work is
correct). Blue = 1 → **`historyValid` is true for every pixel** (`taa_resolve.frag:244`).

From debug view 8 (TaaHistoryLength): near-black over the whole frame, **no red anywhere** →
`disocclusion == 0` (the min-over-neighbourhood fix is correct), and `currentLength` is
pinned at the bottom of its range.

Those three cannot coexist. With `historyValid == true` and `disocclusion == 0`:

```glsl
previousLength = clamp(historyAlpha, 1.0, 32.0);        // >= 1 by construction
currentLength  = min(previousLength * 1.0 + 1.0, 32.0); // must ramp 2,3,4 ... 32
```

`currentLength` is mathematically unable to stay low — unless `historyAlpha` is not the
`currentLength` written last frame. The write side is sound: blending is disabled and the
write mask is full RGBA on both attachments (`AntiAliasingPass.cs:598-604`), and
`R16G16B16A16_SFLOAT` holds 32 exactly. So the **read** is wrong.

`currentLength` sits at exactly 2 (`2/32 = 0.0625` → near-black), which is what you get when
`historyAlpha == 1.0` — the `ClearValue(0,0,0,1)` the history attachment is cleared to at
render-pass begin (`AntiAliasingPass.cs:388-390`). The shader is sampling the bank it is
rendering into. `historyColor` then reads `(0,0,0)`, gets dragged to the neighbourhood floor
by `ClipToAabb`, and `feedback = min(0.95, 1 − 1/2) = 0.5`, so every frame outputs
`mix(current, ~black, 0.5)` — half the raw jittered frame, zero accumulation. That is the
"jitters like crazy".

## Root cause

`RenderTaa` mutates a **shared, immediately-updated bindless descriptor slot once per frame**:

- `BindlessHeap` owns a single `_textureSamplerSet` (`BindlessHeap.cs:354`).
- `RegisterTextureLocked` issues `vkUpdateDescriptorSets` inline (`BindlessHeap.cs:543`).
- `AntiAliasingPass.cs:321-325` re-points `BindlessIndex.TaaHistoryTexture` at the *other*
  bank every frame, at **record** time.
- `RenderingConstants.cs:7` → `FramesInFlight = 2`, `SwapchainImageCount = 3`,
  `PresentModeFifoKhr`.

Frame N records its TAA draw with the slot pointing at bank B, then frame N+1 records and
re-points the same slot at bank A — which is the bank frame N is still writing. Binding flags
are `UpdateAfterBindBit | PartiallyBoundBit` with **no** `UpdateUnusedWhilePendingBit`
(`BindlessHeap.cs:57-59`); `UPDATE_AFTER_BIND` deliberately stops the layers from tracking
per-descriptor use by pending submissions, which is exactly why
`ValidationErrorMessageCount = 0` does not exonerate this.

`VulkanRenderer.RegisterAntiAliasingTextures` (`:12722-12726`) additionally hard-codes the
slot to `TaaHistoryA`; it only runs on init/resize (`RegisterSceneBuffers` at `:1649/1654`,
`:12039`, `:12299`), so it is not the per-frame offender, but it is a second writer to the
same slot and must go the same way.

The fix is the standard bindless rule we are currently violating: **never mutate a descriptor
slot per frame — register every resource once and pass the index.**

## Changes

### 1. Two permanent history slots, index passed by push constant

**`Njulf/Njulf.Rendering/Descriptors/BindlessIndexTable.cs`** — append a new index at the
**end** of the chain, after `GtaoDebugTexture` (`:880`):

```csharp
/// <summary>Second bank of the TAA history double buffer.</summary>
public const int TaaHistoryTextureB = GtaoDebugTexture + 1;
```

Do **not** insert it next to `TaaHistoryTexture` — the table is a `+ 1` chain mirrored as
hard-coded integers in `common.glsl:468-500`, so an insertion shifts every later slot.
Add the matching `const int TAA_HISTORY_TEXTURE_B_INDEX = <n>;` to `common.glsl` and check
whether any test mirrors the two tables before relying on the value.

**`Njulf/Njulf.Rendering/VulkanRenderer.cs:12722-12726`** — register both banks once, instead
of hard-coding `TaaHistoryA`:

```csharp
_bindlessHeap.RegisterTexture(BindlessIndex.TaaHistoryTexture,  _renderTargets.TaaHistoryA.View, _bindlessHeap.ScreenSampler, ImageLayout.ShaderReadOnlyOptimal);
_bindlessHeap.RegisterTexture(BindlessIndex.TaaHistoryTextureB, _renderTargets.TaaHistoryB.View, _bindlessHeap.ScreenSampler, ImageLayout.ShaderReadOnlyOptimal);
```

Both descriptors now record `ShaderReadOnlyOptimal` permanently while one bank sits in
`ColorAttachmentOptimal` each frame. That is legal and is normal bindless practice: Vulkan
only requires the recorded layout to match for descriptors the shader actually **accesses**,
and the shader only ever samples the read bank. `PartiallyBoundBit` is already set.

**`Njulf/Njulf.Rendering/Pipeline/AntiAliasingPass.cs:321-325`** — delete the per-frame
`RegisterTexture` call outright. Keep the two barriers at `:319-320`; they are per-frame and
correct. Then pass the read bank's index through the push block, using the same predicate
that already picks `historyRead`:

```csharp
TaaHistoryTextureIndex = (uint)(_taaWriteHistoryA
    ? BindlessIndex.TaaHistoryTextureB
    : BindlessIndex.TaaHistoryTexture),
```

### 2. Push ABI: 124 → 128 bytes

Append `public uint TaaHistoryTextureIndex;` after `TaaSharpness` in
`GPUStructs.cs` (`GPUAntiAliasingPushConstants`) and mirror it in
`anti_aliasing_push.glsl`. It lands at offset 124; the two `vec2`s stay 8-aligned at 104/112
and the total becomes 128 (a multiple of 4, well inside the push-constant budget —
`GPUFogPushConstants` is already 224 B).

Update `common.glsl:1916` `SIZEOF_GPU_ANTI_ALIASING_PUSH_CONSTANTS` 124 → 128,
`GPUStructLayoutTests.cs:215` 124 → 128, and `AntiAliasingRepairTests.cs:139` 124 → 128 plus
a new `OffsetOf(TaaHistoryTextureIndex) == 124` assertion.

### 3. `taa_resolve.frag` — read through the passed index

Replace all four `TAA_HISTORY_TEXTURE_INDEX` uses (the four taps in
`SampleHistoryCatmullRom`, the `textureSize` call, and the `historyAlpha` tap) with
`nonuniformEXT(int(pc.TaaHistoryTextureIndex))`. No other shader logic changes — the resolve
maths is already correct.

### 4. Stop the clear from disguising a mis-bind

`AntiAliasingPass.cs:388` — the history attachment's `LoadOp.Clear` → `AttachmentLoadOp.DontCare`.
The shader writes every pixel of the render area, so the clear is pure bandwidth, and it is
what made a self-read return a *plausible* black instead of obvious garbage. Cleanup, not the
fix, but it makes the next regression of this class visible immediately.

### 5. Make this provable next time

Add `TaaHistoryValid` and the resolved read-bank index to the diagnostics payload alongside
the existing `MotionVectorsEnabled` / `AntiAliasingMode` fields. Two lines; this failure took
three rounds to isolate because nothing in the capture described the history binding.

## Verification

1. `dotnet build Njulf/Njulf.sln` — a C#/GLSL push mismatch fails here.
2. `dotnet test Njulf/Njulf.Tests` — `AntiAliasingRepairTests`, `GPUStructLayoutTests`,
   `ShaderEffectGpuTests` (TAA smoke, zero validation errors).
3. **The pass/fail gate**, static camera, `AntiAliasing.Mode = Taa`:
   - **Debug view 8** must ramp from black to **uniform white within ~32 frames**. This is the
     whole fix — today it is pinned near-black forever. Any red means disocclusion is firing.
   - **Debug view 5** must stay flat `(0.5, 0.5)` with full blue (unchanged; it is already
     correct, and must not regress).
   - Normal view: edges hold still, then pan slowly — no ghost trails, no smear on trailing
     silhouettes.
4. Re-capture the performance JSON and confirm `ValidationErrorMessageCount == 0` still.

**If view 8 still refuses to ramp after this**, the remaining suspect is the render-graph
history chain: `TaaHistory` is declared `OwnedImageChainResource` with
`ReadWrite(RenderGraphResourceId.TaaHistory)`
(`ProductionRenderPipelineDeclaration.cs:1084`, `:1664-1665`) and carries
`RenderGraphHistoryBindingSelection.Current`/`.Previous` barriers, while `AntiAliasingPass`
independently flips `_taaWriteHistoryA` and issues its own transitions. If the graph's notion
of current/previous disagrees with the pass's, the layouts are wrong. Check that next, not
before — the descriptor mutation is the concrete defect and has to go regardless.

## Out of scope (flagged, not fixed)

Both captures also report `GtaoHistoryValid = 0` and `VolumetricFogHistoryValid = 0`. Those
may be the same class of bug — other passes that ping-pong a shared bindless slot per frame —
or independent. Worth a sweep for `RegisterTexture` calls inside per-frame `Record` paths once
TAA is green, but not part of this change.
