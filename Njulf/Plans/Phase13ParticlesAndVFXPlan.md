# Phase 13: Particles And VFX Detailed Implementation Plan

Goal: provide production-ready gameplay VFX for fire, smoke, dust, sparks, impacts, rain, magic, pickups, trails, beams, and world-space UI accents. Phase 13 should add authorable particle effects, deterministic CPU simulation, efficient GPU rendering, flipbook animation, soft particles, emissive HDR output, bloom integration, diagnostics, sample controls, and a clear path to GPU simulation for high particle counts.

## Recommendation

Implement Phase 13 as a vertical slice that is useful for real game content before adding large-scale GPU simulation.

Recommended order:
1. add authorable CPU-side particle effect data and runtime emitters.
2. add deterministic CPU simulation, pooling, culling, sorting, and budgets.
3. add a dedicated instanced billboard particle render pass that writes into HDR scene color.
4. add flipbook texture support, soft-particle depth fade, emissive intensity, and blend modes.
5. add diagnostics, debug views, sample input controls, and validation scenes.
6. add trails and beams as separate ribbon/beam primitives after billboards are stable.
7. add GPU particle simulation only when CPU simulation and instanced rendering hit measured limits.

Do not begin with a fully GPU-driven particle system. The renderer first needs a robust authoring model, stable sorting, correct depth interaction, predictable memory budgets, and diagnostics. GPU simulation should accelerate an already-defined contract, not define the contract.

## Current Baseline

Relevant current behavior:
- `Njulf.Rendering/VulkanRenderer.cs` owns the pass sequence and exposes sample toggles for existing renderer features.
- `SceneDataBuilder.cs` builds opaque and transparent meshlet draw buffers and sorts transparent meshlets back-to-front.
- `TransparentForwardPass.cs` renders transparent meshlet geometry into `RenderTargetManager.SceneColor`, depth-tested against the swapchain depth image, with blending enabled.
- `BloomPass.cs`, `FogPass.cs`, `ToneMapCompositePass.cs`, and `AntiAliasingPass.cs` run after the main forward path, so emissive particles can feed bloom if particles are drawn into HDR scene color before bloom extraction.
- `RenderSettings.cs` already groups feature settings for shadows, bloom, environment, reflections, AO, anti-aliasing, and fog.
- `RendererDiagnostics.cs` and `SceneRenderingData.cs` already carry per-pass CPU/GPU timing and upload-byte fields.
- `BindlessIndexTable.cs` provides fixed slots for storage buffers and textures.
- `GPUStructs.cs` holds C# structs that must match `Njulf.Shaders/common.glsl`.
- `SampleInputController.cs` and `SampleDiagnosticsReporter.cs` are the sample application's current debug and reporting surfaces.
- `SampleReflectionTestSpheres.cs` creates glossy and metallic spheres that are useful for verifying HDR, bloom, soft depth fades, and transparent VFX composition over reflective content.

There is no existing particle system, emitter model, particle asset format, particle render pass, trail/beam renderer, or VFX diagnostics. Phase 13 is a new subsystem that should integrate with the existing HDR/transparent/fog/bloom pipeline without disturbing static mesh rendering.

## Target Outcome

Phase 13 is complete when:
- VFX can be authored as data without changing renderer code.
- `Scene` can own one or more particle effect instances.
- emitters can spawn, simulate, cull, sort, and render billboards deterministically.
- particles support alpha blend, premultiplied alpha, additive, and alpha-clipped modes where appropriate.
- flipbook animation supports rows, columns, frame rate, start frame randomization, interpolation, and looping.
- soft particles fade against scene depth without hard intersections.
- emissive particles write HDR color and feed bloom.
- renderer diagnostics report particle counts, emitter counts, batches, uploads, CPU time, GPU time, culled particles, budget pressure, and active blend modes.
- `NjulfHelloGame` can toggle a representative VFX sample scene and inspect it over the existing reflection-test spheres.
- CPU tests and shader build tests cover the new contracts.

## Non-Goals

Phase 13 does not include:
- full editor UI for particle authoring.
- node-graph VFX authoring.
- fluid simulation.
- volumetric clouds or voxel fog.
- physically correct participating media.
- collision against arbitrary triangle meshes as the first slice.
- GPU simulation as a prerequisite for acceptance.
- ray-traced particles.
- transparent object order perfection across every possible mesh and particle overlap.
- reflection-probe recapture for dynamic particles.
- gameplay scripting for every particle event.

## Core Design Decisions

1. Use a dedicated particle renderer instead of forcing billboards through the meshlet path. Instanced quads are simpler, cheaper to author, easier to sort, and easier to debug.
2. Simulate particles on CPU first. This makes determinism, tests, editor-style authoring, and diagnostics straightforward.
3. Store particle effect definitions as data assets. Runtime code should not hard-code fire, smoke, rain, or sparks.
4. Render particles into HDR scene color before fog, bloom, tone mapping, and anti-aliasing.
5. Use read-only scene depth for soft-particle fade and depth testing. Default alpha-blended particles do not write depth.
6. Keep additive and alpha-blended particles in separate sorted batches. Do not let additive effects destabilize alpha sorting.
7. Batch by material, texture, blend mode, lighting mode, and soft-particle mode after sorting constraints are satisfied.
8. Enforce budgets explicitly. If an effect exceeds the configured particle count or upload budget, diagnostics must report it.
9. Prefer premultiplied alpha for authored smoke/fire flipbooks where possible. Straight alpha remains supported for compatibility.
10. Trails and beams share authoring and diagnostics with particles but use dedicated ribbon/beam instance buffers and pipelines.

## Target Frame Shape

Target frame order after Phase 13:
1. `DirectionalShadowPass`
2. `SpotShadowPass`
3. `PointShadowPass`
4. `DepthPrePass`
5. `HiZBuildPass`
6. `AmbientOcclusionPass`
7. `AmbientOcclusionBlurPass`
8. `TiledLightCullingPass`
9. `ForwardPlusPass`
10. `SkyboxPass`
11. `TransparentForwardPass`
12. `ParticlePass`
13. `TrailBeamPass`
14. `FogPass`
15. `BloomPass`
16. `ToneMapCompositePass`
17. `AntiAliasingPass`
18. Present

Notes:
- `ParticlePass` and `TrailBeamPass` should load `RenderTargetManager.SceneColor` and the depth buffer.
- Particles are included in fog, bloom, tone mapping, and anti-aliasing because they are rendered before those passes.
- Static reflection probes do not reflect dynamic particles unless probe recapture is explicitly requested later. Do not fake particle reflections in Phase 13.
- If sorted transparency interaction between mesh transparent geometry and particles becomes visually unacceptable, add a later `TransparentRenderQueue` that can interleave meshlet transparent draws and particle draws. The first production slice can render transparent meshlets first, then particles, with this limitation documented in diagnostics and tests.

## Scope

Phase 13 includes:
- particle effect asset model.
- runtime particle effect instances and emitters.
- CPU simulation.
- deterministic random streams.
- world-space and local-space emitters.
- spawn shapes: point, sphere, box, cone, circle/ring, line.
- lifetime, color, size, rotation, velocity, acceleration, drag, gravity, turbulence/noise, and emissive curves.
- flipbook animation.
- billboard orientation modes.
- depth-tested alpha blend, premultiplied alpha, additive, and alpha-clipped particles.
- soft-particle depth fade.
- CPU culling using emitter bounds and conservative particle bounds.
- CPU sorting by camera distance for alpha-blended particles.
- material batching.
- GPU instance buffer upload.
- dedicated particle graphics pipeline.
- diagnostics and debug views.
- sample app controls and validation content.
- trails and beams as a second slice.
- GPU simulation design and extension points.

Phase 13 should not change:
- the existing static mesh path.
- material import behavior for normal glTF geometry.
- reflection probe capture behavior.
- shadow passes, except for future optional particle shadows.

## Settings

Add `ParticleSettings` under `RenderSettings`.

Suggested API:

```csharp
public enum ParticleSimulationMode : uint
{
    Cpu = 0,
    Gpu = 1
}

public enum ParticleDebugView : uint
{
    None = 0,
    Bounds = 1,
    Overdraw = 2,
    SoftParticleFade = 3,
    FlipbookFrame = 4,
    SortOrder = 5,
    Lifetime = 6,
    Velocity = 7,
    EmitterId = 8,
    BatchId = 9,
    BudgetHeatmap = 10
}

public sealed class ParticleSettings
{
    public bool Enabled { get; set; } = true;
    public ParticleSimulationMode SimulationMode { get; set; } = ParticleSimulationMode.Cpu;
    public ParticleDebugView DebugView { get; set; } = ParticleDebugView.None;

    public int MaxParticles { get; set; } = 65536;
    public int MaxEmitters { get; set; } = 1024;
    public int MaxBatches { get; set; } = 4096;
    public int MaxTrails { get; set; } = 4096;
    public int MaxTrailSegments { get; set; } = 65536;

    public bool SoftParticlesEnabled { get; set; } = true;
    public float SoftParticleDistance { get; set; } = 0.35f;
    public bool DepthTestEnabled { get; set; } = true;
    public bool ReceiveFog { get; set; } = true;
    public bool UsePremultipliedAlphaByDefault { get; set; } = true;

    public float GlobalSpawnRateScale { get; set; } = 1.0f;
    public float GlobalVelocityScale { get; set; } = 1.0f;
    public float GlobalEmissiveScale { get; set; } = 1.0f;
    public float DistanceCullMultiplier { get; set; } = 1.0f;
    public ulong MaxUploadBytesPerFrame { get; set; } = 8 * 1024 * 1024;
}
```

Recommended clamps:
- `MaxParticles`: `0` to `1_000_000`.
- `MaxEmitters`: `0` to `65535`.
- `MaxBatches`: `0` to `65535`.
- `SoftParticleDistance`: `0.0f` to `10.0f`.
- `GlobalSpawnRateScale`: `0.0f` to `10.0f`.
- `GlobalEmissiveScale`: `0.0f` to `64.0f`.
- `MaxUploadBytesPerFrame`: `0` to a documented platform budget.

Acceptance criteria:
- disabled particles produce zero particle draw calls and zero particle uploads.
- settings clamp invalid values and are covered by tests.
- diagnostics expose effective settings, not only requested values.

## Authoring Data Model

Add runtime authoring types in `Njulf.Core.Scene` or a new `Njulf.Core.Vfx` namespace. Prefer a new `Njulf.Core.Vfx` namespace for reusable VFX data and keep scene ownership in `Njulf.Core.Scene`.

Suggested files:
- `Njulf.Core/Vfx/ParticleEffect.cs`
- `Njulf.Core/Vfx/ParticleEmitterDefinition.cs`
- `Njulf.Core/Vfx/ParticleMaterialDefinition.cs`
- `Njulf.Core/Vfx/ParticleCurve.cs`
- `Njulf.Core/Vfx/ParticleGradient.cs`
- `Njulf.Core/Vfx/ParticleSpawnShape.cs`
- `Njulf.Core/Vfx/ParticleBlendMode.cs`
- `Njulf.Core/Vfx/ParticleBillboardMode.cs`
- `Njulf.Core/Vfx/ParticleLightingMode.cs`
- `Njulf.Core/Vfx/ParticleFlipbook.cs`
- `Njulf.Core/Vfx/TrailDefinition.cs`
- `Njulf.Core/Vfx/BeamDefinition.cs`
- `Njulf.Core/Scene/ParticleEffectInstance.cs`

Suggested high-level types:

```csharp
public sealed class ParticleEffect
{
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<ParticleEmitterDefinition> Emitters { get; init; } = Array.Empty<ParticleEmitterDefinition>();
    public IReadOnlyList<TrailDefinition> Trails { get; init; } = Array.Empty<TrailDefinition>();
    public IReadOnlyList<BeamDefinition> Beams { get; init; } = Array.Empty<BeamDefinition>();
}

public sealed class ParticleEmitterDefinition
{
    public string Name { get; init; } = string.Empty;
    public ParticleMaterialDefinition Material { get; init; } = new();
    public ParticleSpawnShape SpawnShape { get; init; } = ParticleSpawnShape.Point();
    public bool Looping { get; init; } = true;
    public float DurationSeconds { get; init; } = 1.0f;
    public float StartDelaySeconds { get; init; }
    public float SpawnRatePerSecond { get; init; } = 10.0f;
    public int BurstCount { get; init; }
    public float BurstTimeSeconds { get; init; }
    public ParticleCurve LifetimeSeconds { get; init; } = ParticleCurve.Constant(1.0f);
    public ParticleCurve Size { get; init; } = ParticleCurve.Constant(1.0f);
    public ParticleGradient ColorOverLife { get; init; } = ParticleGradient.White;
    public ParticleCurve EmissiveOverLife { get; init; } = ParticleCurve.Constant(0.0f);
    public ParticleCurve RotationRadians { get; init; } = ParticleCurve.Constant(0.0f);
    public ParticleCurve AngularVelocityRadiansPerSecond { get; init; } = ParticleCurve.Constant(0.0f);
    public Vector3 InitialVelocityMin { get; init; }
    public Vector3 InitialVelocityMax { get; init; }
    public Vector3 Acceleration { get; init; }
    public float Drag { get; init; }
    public bool LocalSpace { get; init; }
    public int MaxParticles { get; init; } = 1024;
}
```

Keep the first version intentionally serializable with simple public properties. Avoid delegates, reflection-heavy authoring, or complex runtime callbacks in the asset model.

Acceptance criteria:
- VFX definitions can be built in code for samples.
- VFX definitions can be serialized to and from a simple content file later without changing runtime semantics.
- invalid definitions fail with effect name, emitter name, and offending field.

## Scene Integration

Extend `Scene` with particle effect instance ownership:

```csharp
public sealed class ParticleEffectInstance
{
    public string Name { get; set; } = string.Empty;
    public ParticleEffect Effect { get; }
    public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
    public bool Visible { get; set; } = true;
    public bool Playing { get; private set; } = true;
    public uint RandomSeed { get; set; }

    public void Play();
    public void Pause();
    public void Stop(bool clearParticles);
    public void Restart(uint? seed = null);
}
```

Add to `Scene`:
- `IReadOnlyList<ParticleEffectInstance> ParticleEffects`.
- `Add(ParticleEffectInstance effect)`.
- `Remove(ParticleEffectInstance effect)`.
- clear behavior in `Scene.Clear`.

Ownership rules:
- `ParticleEffect` is shared immutable authoring data.
- `ParticleEffectInstance` is per-scene mutable playback state.
- renderer-side simulation state should be stored in `ParticleSystemManager`, keyed by instance identity or stable instance id.

Acceptance criteria:
- adding/removing particle effects is safe across frames.
- stopping an effect can either clear existing particles or let them die naturally.
- `Scene.Clear` releases renderer-side state on the next update.

## CPU Simulation

Add `ParticleSystemManager` in `Njulf.Rendering.Resources`.

Suggested responsibilities:
- register scene particle instances.
- allocate per-emitter particle pools.
- simulate particles each frame.
- spawn continuous and burst particles.
- maintain deterministic random streams per effect instance and emitter.
- compact dead particles.
- compute per-emitter and per-effect bounds.
- cull emitters by camera frustum and distance budget.
- produce sorted render instances and batch metadata.
- upload GPU instance buffers.
- populate diagnostics.

Suggested files:
- `Njulf.Rendering/Resources/ParticleSystemManager.cs`
- `Njulf.Rendering/Data/ParticleSimulationData.cs`
- `Njulf.Rendering/Data/ParticleBatchBuilder.cs`
- `Njulf.Rendering/Data/ParticleSortKey.cs`
- `Njulf.Rendering/Data/ParticleBounds.cs`

Simulation rules:
1. Use `deltaSeconds` from frame timing, clamped to a maximum such as `1.0f / 15.0f` to avoid huge catch-up bursts after a hitch.
2. Support optional fixed-step simulation for deterministic tests.
3. Accumulate fractional spawns per emitter to avoid frame-rate-dependent spawn rates.
4. Use seeded deterministic random generation per emitter.
5. Update particle:
   - age.
   - normalized lifetime.
   - velocity.
   - position.
   - rotation.
   - size.
   - color.
   - emissive intensity.
   - flipbook frame.
6. Kill particles when age exceeds lifetime or size reaches zero if the effect opts into that policy.
7. Do not allocate per-frame managed objects in hot simulation paths.
8. Use arrays or pooled lists, not LINQ, in the particle hot path.

Acceptance criteria:
- a fixed-step emitter produces the same particle positions and colors for the same seed.
- spawn rates are stable across 30, 60, and 144 FPS simulation tests.
- no per-frame allocations occur in the common simulation path under a representative smoke/spark sample.

## Curves And Gradients

Use simple sampled curves first.

Recommended implementation:
- `ParticleCurve`: constant, two-key linear, or sampled keyframes.
- `ParticleGradient`: color/alpha keyframes sampled by normalized lifetime.
- clamp key times to `0.0f` to `1.0f`.
- sort keyframes on asset validation.
- bake curves and gradients into compact runtime arrays if needed.

Acceptance criteria:
- curve sampling is covered by tests.
- gradient color and alpha interpolation are covered by tests.
- invalid key ordering is corrected or reported deterministically.

## Spawn Shapes

Required first-slice shapes:
- point.
- sphere volume.
- sphere shell.
- box volume.
- cone.
- circle/ring.
- line segment.

Each shape should produce:
- spawn position in emitter local space.
- initial direction.
- optional normal for velocity alignment.

Acceptance criteria:
- shape sampling respects min/max ranges.
- seeded shape sampling is deterministic.
- generated bounds are conservative.

## Particle Materials

Particle materials are separate from `GPUMaterialData` unless the renderer later unifies material authoring. Mesh PBR materials and VFX particle materials have different needs.

Suggested API:

```csharp
public enum ParticleBlendMode : uint
{
    AlphaBlend = 0,
    PremultipliedAlpha = 1,
    Additive = 2,
    SoftAdditive = 3,
    AlphaClip = 4
}

public enum ParticleBillboardMode : uint
{
    ViewFacing = 0,
    VelocityAligned = 1,
    Horizontal = 2,
    WorldAligned = 3,
    StretchedVelocity = 4
}

public enum ParticleLightingMode : uint
{
    Unlit = 0,
    AmbientOnly = 1,
    DirectionalOnly = 2,
    ForwardPlus = 3
}

public sealed class ParticleMaterialDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? TexturePath { get; init; }
    public ParticleBlendMode BlendMode { get; init; } = ParticleBlendMode.PremultipliedAlpha;
    public ParticleBillboardMode BillboardMode { get; init; } = ParticleBillboardMode.ViewFacing;
    public ParticleLightingMode LightingMode { get; init; } = ParticleLightingMode.Unlit;
    public ParticleFlipbook? Flipbook { get; init; }
    public bool SoftParticles { get; init; } = true;
    public bool DepthTest { get; init; } = true;
    public bool DepthWrite { get; init; }
    public bool ReceiveFog { get; init; } = true;
    public float AlphaClipThreshold { get; init; } = 0.5f;
}
```

Material constraints:
- additive and soft-additive are not depth writing.
- alpha-blended and premultiplied particles should sort back-to-front.
- additive particles can sort coarse front-to-back or by material after alpha particles if visual order is acceptable.
- alpha-clipped particles may write depth only in a later masked-particle path. First slice should avoid depth writes unless required by a validation scene.

Acceptance criteria:
- blend mode is visible in diagnostics and RenderDoc pipeline names.
- premultiplied alpha assets render without dark fringes.
- additive emissive particles can exceed `1.0` HDR color and trigger bloom.

## Flipbook Support

Add `ParticleFlipbook`:

```csharp
public sealed class ParticleFlipbook
{
    public int Columns { get; init; } = 1;
    public int Rows { get; init; } = 1;
    public int FrameCount { get; init; } = 1;
    public float FramesPerSecond { get; init; } = 0.0f;
    public bool Loop { get; init; } = true;
    public bool RandomStartFrame { get; init; }
    public bool InterpolateFrames { get; init; }
}
```

Shader behavior:
- compute frame index from particle age, start frame, and FPS.
- derive UV scale and offset from columns/rows.
- if interpolation is enabled, sample current and next frame and blend.

Acceptance criteria:
- non-square atlases work.
- frame count can be less than `Rows * Columns`.
- random start frame is deterministic by seed.
- debug view can show frame id as color.

## Soft Particles

Soft particles should fade where particle fragments intersect opaque scene depth.

Implementation:
1. Ensure `ParticlePass` can sample the depth image.
2. Register depth or a depth-readable target through bindless texture slots if not already available for shaders.
3. In `particle.frag`, reconstruct view-space or linear depth for the particle fragment and scene depth.
4. Compute fade:

```text
fade = saturate((sceneDepthLinear - particleDepthLinear) / softParticleDistance)
```

5. Multiply alpha and emissive by fade.
6. Clamp behavior for sky/no-depth pixels so particles do not disappear against the sky.

Acceptance criteria:
- smoke and magic quads do not hard-clip into floors or walls.
- soft-particle fade can be disabled globally and per material.
- debug view visualizes fade factor.
- reversed-Z or current depth convention is covered by tests or shader comments.

## GPU Data Layout

Add GPU structs in `GPUStructs.cs` and matching GLSL structs in `common.glsl`.

Suggested structs:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUParticleInstance
{
    public Vector3 Position;
    public float RotationRadians;
    public Vector2 Size;
    public float AgeNormalized;
    public float SoftParticleDistance;
    public Vector4 Color;
    public Vector4 Emissive;
    public Vector4 VelocityAndStretch;
    public Vector4 UvScaleOffset;
    public uint TextureIndex;
    public uint MaterialFlags;
    public uint EmitterIndex;
    public uint SortKeyLow;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUParticleBatch
{
    public uint InstanceOffset;
    public uint InstanceCount;
    public uint TextureIndex;
    public uint MaterialFlags;
    public Vector4 MaterialParams;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GPUParticlePushConstants
{
    public Matrix4x4 ViewMatrix;
    public Matrix4x4 ProjectionMatrix;
    public Matrix4x4 ViewProjectionMatrix;
    public Matrix4x4 InverseProjectionMatrix;
    public Vector3 CameraPosition;
    public float Time;
    public Vector2 ScreenDimensions;
    public uint CurrentFrameIndex;
    public uint ParticleInstanceBufferBaseIndex;
    public uint DepthTextureIndex;
    public uint DebugView;
    public uint ReceiveFog;
}
```

Keep fields 16-byte friendly where practical. Add layout tests in `GPUStructLayoutTests.cs` before shader implementation is trusted.

Bindless index additions:
- `ParticleInstanceBufferBase`.
- `ParticleInstanceBufferFrame1`.
- `ParticleBatchBufferBase`.
- `ParticleBatchBufferFrame1`.
- optional `TrailVertexBufferBase`.
- optional `TrailVertexBufferFrame1`.

Acceptance criteria:
- C# struct sizes and offsets match GLSL declarations.
- bindless constants are covered by `BindlessIndexTests`.
- invalid or missing particle buffers bind safe empty buffers.

## Rendering Architecture

Add:
- `Njulf.Rendering/Pipeline/ParticlePass.cs`
- `Njulf.Rendering/Pipeline/TrailBeamPass.cs`
- `Njulf.Rendering/Pipeline/PipelineObjects/ParticlePipeline.cs`
- `Njulf.Shaders/particle.vert`
- `Njulf.Shaders/particle.frag`
- `Njulf.Shaders/trail_beam.vert`
- `Njulf.Shaders/trail_beam.frag`

Use a dedicated indexed quad draw:
- static quad vertex id or small immutable quad vertex/index buffer.
- one `GPUParticleInstance` per live rendered particle.
- one draw per batch, or multi-draw indirect later.

First-slice draw path:
1. Build sorted instance buffer on CPU.
2. Build batch list grouped by pipeline-affecting material state.
3. Upload instance data to per-frame storage buffer.
4. For each batch:
   - bind the correct blend pipeline.
   - push constants.
   - draw `6` indexed vertices with `batch.InstanceCount` instances.

Pipeline variants:
- alpha blend.
- premultiplied alpha.
- additive.
- soft additive.
- alpha clip.

Use clear debug names:
- `Particle Alpha Blend Pipeline`.
- `Particle Premultiplied Alpha Pipeline`.
- `Particle Additive Pipeline`.
- `Particle Instance Buffer Frame 0`.
- `Particle Batch Buffer Frame 0`.

Acceptance criteria:
- RenderDoc shows a named `ParticlePass`.
- particle buffers, pipelines, image views, and samplers have debug names.
- disabling particles removes the pass work without breaking barriers.
- resize remains validation-clean.

## Blend And Sort Policy

Sorting policy:
1. remove invisible and dead particles.
2. cull by emitter bounds and distance.
3. split into buckets:
   - alpha/premultiplied/soft-additive requiring back-to-front sorting.
   - additive effects that can be less strictly sorted.
   - alpha-clipped effects.
4. sort alpha bucket by camera distance descending.
5. preserve deterministic tie-breakers:
   - effect instance id.
   - emitter index.
   - particle birth sequence.
6. batch after sorting without changing order. If batching would reorder alpha particles incorrectly, prefer correctness over fewer draw calls.

Known first-slice limitation:
- transparent meshlets and particles are not globally interleaved. Particle effects render after transparent mesh geometry. Document this in diagnostics as `TransparentInterleaveMode=MeshThenParticles`.

Future optional improvement:
- introduce `TransparentRenderQueue` with queue items for meshlet ranges, particle ranges, trails, and beams. This should be driven by real visual need, not added preemptively.

Acceptance criteria:
- alpha particles do not flicker when distances tie.
- sort order debug view makes draw order visible.
- diagnostics report sorted particle count and additive particle count separately.

## Lighting And Shading

Required first slice:
- unlit tint.
- emissive HDR intensity.
- ambient-only multiplier from scene ambient/environment fallback.
- optional directional-light facing term for smoke/dust.

Deferred/future:
- full Forward+ local light sampling for particles.
- particle shadow casting.
- particle shadow receiving.
- volumetric self-shadowing.

Shader rules:
- sample base texture in sRGB or linear according to texture manager support. Color textures should be treated as sRGB where possible.
- multiply sampled color by particle color.
- add emissive contribution before tone mapping.
- apply soft-particle fade before blend.
- apply debug views late and clearly.
- avoid expensive PBR in the first particle fragment shader.

Acceptance criteria:
- fire/sparks bloom naturally.
- smoke can be art-directed without requiring lights.
- particles remain visible under exposure and tone mapping changes.

## Texture And Asset Pipeline

First slice can create sample particle materials in code and load textures through the existing `TextureManager`.

Add content asset support after the runtime path is stable:
- `.njvfx.json` or equivalent particle effect manifest.
- texture path.
- blend mode.
- flipbook layout.
- emitters.
- curves.
- gradients.
- spawn shapes.
- budgets.

Suggested manifest shape:

```json
{
  "name": "FireBurst",
  "emitters": [
    {
      "name": "Flame",
      "material": {
        "texture": "Assets/Vfx/fire_flipbook.png",
        "blendMode": "PremultipliedAlpha",
        "flipbook": { "columns": 8, "rows": 8, "frameCount": 48, "framesPerSecond": 24 }
      },
      "looping": false,
      "durationSeconds": 0.75,
      "burstCount": 64,
      "lifetimeSeconds": { "min": 0.4, "max": 1.0 },
      "size": { "keys": [[0, 0.2], [1, 1.2]] }
    }
  ]
}
```

Acceptance criteria:
- malformed manifests report asset path, emitter name, and field name.
- missing textures use an obvious fallback texture.
- particle textures participate in existing texture memory diagnostics.

## Trails And Beams

Add trails and beams after billboard particles pass validation.

Trail use cases:
- sword arcs.
- missile trails.
- spark streaks.
- smoke ribbons.
- magic wisps.

Beam use cases:
- lightning.
- lasers.
- targeting lines.
- healing links.

Trail implementation:
- maintain per-trail segment history.
- generate camera-facing ribbon vertices on CPU first.
- upload compact `GPUTrailVertex` data.
- render with alpha/additive blend.
- support width and color over normalized trail age.
- support texture tiling along length.

Beam implementation:
- define start/end in local or world space.
- support width, color, noise amplitude, segment count, scrolling UVs.
- CPU-generate beam polyline first.

Suggested files:
- `Njulf.Core/Vfx/TrailDefinition.cs`
- `Njulf.Core/Vfx/BeamDefinition.cs`
- `Njulf.Rendering/Resources/TrailBeamManager.cs`
- `Njulf.Rendering/Pipeline/TrailBeamPass.cs`

Acceptance criteria:
- trails do not explode when the camera crosses the ribbon plane.
- beam endpoints can be updated every frame.
- diagnostics report trail count, segment count, beam count, and upload bytes.

## GPU Simulation Extension

GPU simulation should be a later extension within Phase 13, not the first acceptance gate.

Add only after CPU path proves the data contract:
- `particle_simulate.comp`.
- `particle_emit.comp`.
- `particle_compact.comp`.
- optional GPU sort.
- indirect draw buffer.
- alive/dead lists.

GPU simulation requirements:
- preserve the same authoring data.
- share material/rendering path with CPU particles where possible.
- expose diagnostics for GPU simulation time, alive count, dead-list pressure, and indirect draw count.
- keep CPU simulation available for deterministic tests and low-count effects.

Acceptance criteria for optional GPU slice:
- 100k simple particles render within the target frame budget on the target GPU.
- GPU counters do not require CPU readback every frame.
- GPU path can be disabled at runtime and falls back to CPU for supported effects.

## Culling And Budgets

Emitter culling:
- frustum cull conservative emitter bounds.
- distance cull using effect-specific max draw distance.
- optionally reduce spawn rate by distance.
- optionally freeze simulation for far effects, but only if gameplay does not require particle state.

Budget policy:
- global `MaxParticles`.
- per-effect max particles.
- per-emitter max particles.
- per-frame spawn cap.
- per-frame upload-byte cap.
- optional priority field per effect/emitter.

If over budget:
1. keep high-priority effects.
2. reduce spawn counts for low-priority looping effects.
3. drop oldest particles for low-priority effects.
4. report budget pressure in diagnostics.

Acceptance criteria:
- exceeding budgets does not allocate unbounded memory.
- budget decisions are deterministic.
- diagnostics identify which effect or emitter was throttled.

## Diagnostics

Extend `RendererDiagnostics` and `SceneRenderingData` with particle fields.

Suggested fields:

```csharp
int ParticlesEnabled;
ParticleSimulationMode ParticleSimulationMode;
ParticleDebugView ParticleDebugView;
int ParticleEffectCount;
int ParticleEmitterCount;
int LiveParticleCount;
int SimulatedParticleCount;
int CulledParticleCount;
int RenderedParticleCount;
int ParticleBatchCount;
int AlphaParticleCount;
int AdditiveParticleCount;
int SoftParticleCount;
int FlipbookParticleCount;
int TrailCount;
int TrailSegmentCount;
int BeamCount;
int ParticleBudgetExceeded;
int ParticleUploadBudgetExceeded;
ulong ParticleInstanceUploadBytes;
ulong TrailBeamUploadBytes;
long CpuParticleSimulationMicroseconds;
long CpuParticleBuildMicroseconds;
long CpuParticleRecordMicroseconds;
long CpuTrailBeamRecordMicroseconds;
long GpuParticleMicroseconds;
long GpuTrailBeamMicroseconds;
```

`SampleDiagnosticsReporter.cs` should print a concise particle line:

```text
Frame diagnostics particles: enabled=1, mode=Cpu, effects=3, emitters=8, live=12043, rendered=9182, batches=42, alpha=7200, additive=1982, soft=6500, uploadMiB=1.4, simUs=..., buildUs=..., gpuUs=..., budgetExceeded=0.
```

Acceptance criteria:
- diagnostics reset to zero when particles are disabled.
- diagnostics distinguish simulated, culled, and rendered counts.
- pass timings are visible in the same reporting flow as existing passes.

## Debug Views And Sample Controls

Add sample controls in `SampleInputController.cs`:
- toggle particles.
- cycle particle debug view.
- pause/resume particle simulation.
- restart sample effects with the same seed.
- restart sample effects with a new seed.
- increase/decrease global spawn-rate scale.
- increase/decrease global emissive scale.
- toggle soft particles.

Suggested keys should avoid conflicting with existing controls. If the current sample already uses nearby function keys, prefer a small mode-based control printout rather than adding many unannounced bindings.

Debug views:
- emitter bounds.
- particle bounds.
- overdraw heat.
- soft-particle fade.
- flipbook frame.
- sort order.
- lifetime.
- velocity.
- emitter id.
- batch id.
- budget heatmap.

Acceptance criteria:
- debug views work without recompiling shaders.
- controls print the effective settings.
- debug views are disabled by default.

## Sample Content

Add a dedicated sample builder:
- `NjulfHelloGame/SampleVfxEffects.cs`
- optional `NjulfHelloGame/SampleParticleTextures.cs` if procedural fallback textures are generated.

Recommended sample effects:
1. `Vfx.FirePit`
   - looping flame flipbook.
   - additive sparks.
   - soft premultiplied smoke.
   - emissive HDR high enough to trigger bloom.
2. `Vfx.ImpactBurst`
   - one-shot burst.
   - sparks plus dust.
   - deterministic restart.
3. `Vfx.RainSheet`
   - many stretched velocity-aligned particles.
   - distance culling.
4. `Vfx.MagicOrb`
   - additive glow.
   - orbiting particles.
   - beam or trail extension later.

Use `SampleReflectionTestSpheres.cs` as a validation backdrop:
- place soft smoke and sparks in front of and behind the metallic spheres.
- verify depth test and soft fade against sphere geometry.
- verify emissive sparks bloom in HDR.
- verify particles composite over reflective materials without pretending that static reflection probes dynamically reflect them.

Acceptance criteria:
- sample scene can demonstrate alpha, premultiplied, additive, soft particles, and flipbook animation.
- sample scene remains validation-clean during restart, resize, and shutdown.
- sample content is deterministic with a fixed seed.

## RenderDoc And Validation Requirements

Every Phase 13 pass and resource must be inspectable:
- named `ParticlePass`.
- named `TrailBeamPass`.
- named particle pipelines.
- named particle instance buffers per frame.
- named particle textures or fallback textures.
- named samplers.
- named depth texture binding used by particles.

Validation runs:
1. startup with particles disabled.
2. startup with particles enabled.
3. toggle particles at runtime.
4. restart effects repeatedly.
5. resize window while particles are alive.
6. change bloom/exposure while emissive particles are visible.
7. switch particle debug views.
8. shutdown while effects are alive.

Acceptance criteria:
- no Vulkan validation errors.
- no descriptor use-after-free when particle textures or buffers are recreated.
- RenderDoc frame shows particle draw calls after transparent mesh rendering and before fog/bloom.

## Tests

Add CPU tests:
- `ParticleSettings` clamps.
- `ParticleCurve` sampling.
- `ParticleGradient` sampling.
- deterministic random stream.
- spawn shape sampling range.
- fixed-seed emitter simulation.
- spawn-rate frame-rate independence.
- budget throttling.
- alpha sort tie-breakers.
- particle bounds.
- scene add/remove/clear behavior.

Add renderer contract tests:
- `GPUStructLayoutTests` for particle structs.
- `BindlessIndexTests` for new particle buffer indices.
- `RendererDiagnosticsTests` for empty/default particle diagnostics.
- `ShaderBuildTests` for particle and trail/beam shaders.
- optional content import tests for `.njvfx.json` once asset manifests exist.

Potential test files:
- `Njulf.Tests/ParticleSettingsTests.cs`
- `Njulf.Tests/ParticleCurveTests.cs`
- `Njulf.Tests/ParticleEmitterSimulationTests.cs`
- `Njulf.Tests/ParticleBatchBuilderTests.cs`
- `Njulf.Tests/ParticleSceneTests.cs`

Acceptance criteria:
- `dotnet test Njulf.sln` passes.
- particle shader compilation is part of the existing shader build test path.
- tests prove deterministic simulation before GPU simulation is considered.

## Implementation Stages

### Stage 13.1: Baseline Contracts

Tasks:
1. Add `ParticleSettings`, `ParticleSimulationMode`, and `ParticleDebugView`.
2. Add empty particle diagnostics fields.
3. Add particle fields to `SceneRenderingData`.
4. Add settings clamp tests and diagnostics default tests.
5. Add `ParticleEffect`, `ParticleEmitterDefinition`, and `ParticleEffectInstance`.
6. Add `Scene` add/remove/clear support.

Acceptance criteria:
- code compiles with particles disabled by default or enabled with zero effects.
- diagnostics report zero particle work.
- scene ownership tests pass.

### Stage 13.2: CPU Simulation

Tasks:
1. Add `ParticleSystemManager`.
2. Add deterministic random stream.
3. Add per-emitter particle pools.
4. Implement point, sphere, box, cone, circle/ring, and line spawn shapes.
5. Implement lifetime, velocity, acceleration, drag, gravity, size, rotation, color, and emissive updates.
6. Implement looping, duration, delay, continuous spawn, and burst spawn.
7. Add conservative bounds.
8. Add CPU tests.

Acceptance criteria:
- fixed-seed simulations are reproducible.
- no common-path per-frame allocations in simulation.
- simulation can be paused and restarted.

### Stage 13.3: Particle GPU Data And Upload

Tasks:
1. Add `GPUParticleInstance`, `GPUParticleBatch`, and `GPUParticlePushConstants`.
2. Add matching GLSL structs/constants.
3. Add bindless particle buffer indices.
4. Add per-frame particle instance and batch buffers.
5. Add upload tracking and diagnostics.
6. Add empty safe buffers for zero-particle frames.

Acceptance criteria:
- struct layout tests pass.
- bindless index tests pass.
- zero-particle frames do not bind invalid buffers.

### Stage 13.4: Billboard Render Pass

Tasks:
1. Add `ParticlePipeline`.
2. Add `particle.vert` and `particle.frag`.
3. Add `ParticlePass`.
4. Add alpha blend, premultiplied alpha, additive, and soft-additive pipeline variants.
5. Insert `ParticlePass` after `TransparentForwardPass` and before `FogPass`.
6. Add barriers for scene color load/store and depth read.
7. Add RenderDoc debug names.

Acceptance criteria:
- simple colored untextured quads render in HDR scene color.
- alpha and additive modes blend correctly.
- pass is validation-clean through resize and shutdown.

### Stage 13.5: Textures And Flipbooks

Tasks:
1. Load particle textures through `TextureManager`.
2. Add fallback white/smoke/noise texture strategy.
3. Add flipbook UV packing.
4. Add frame interpolation.
5. Add flipbook debug view.
6. Add sample fire/smoke texture path or procedural placeholder.

Acceptance criteria:
- flipbook frames advance at stable rate.
- random start frame is deterministic.
- invalid texture path uses visible fallback and logs the asset path.

### Stage 13.6: Soft Particles

Tasks:
1. Expose depth texture to particle shader.
2. Implement linear-depth reconstruction.
3. Add material and global soft-particle toggles.
4. Add fade-distance controls.
5. Add soft-particle debug view.
6. Validate against floor, walls, Sponza columns, and reflection test spheres.

Acceptance criteria:
- soft particles fade at intersections.
- disabling soft particles restores hard intersections for comparison.
- fade behaves correctly at sky/no-depth pixels.

### Stage 13.7: Sorting, Batching, And Budgets

Tasks:
1. Implement alpha back-to-front sorting.
2. Add deterministic tie-breakers.
3. Add material batching that does not break alpha order.
4. Split additive and alpha buckets.
5. Add global/per-effect/per-emitter budgets.
6. Add budget throttling diagnostics.

Acceptance criteria:
- sorted particles are stable frame to frame.
- over-budget behavior is deterministic and reported.
- batching reduces draw calls without visible order regressions in validation scenes.

### Stage 13.8: Sample Scene And Controls

Tasks:
1. Add `SampleVfxEffects.cs`.
2. Add fire, smoke, sparks, impact burst, rain, and magic sample effects.
3. Add controls for toggle, debug view, pause, restart, spawn scale, emissive scale, and soft particles.
4. Extend `SampleDiagnosticsReporter.cs`.
5. Use `SampleReflectionTestSpheres.cs` as a depth/HDR/composition validation backdrop.

Acceptance criteria:
- sample scene demonstrates all first-slice features.
- controls print effective values.
- diagnostics are readable and not spammy.

### Stage 13.9: Trails And Beams

Tasks:
1. Add trail and beam definitions.
2. Add `TrailBeamManager`.
3. Add CPU ribbon/beam generation.
4. Add `TrailBeamPass`.
5. Add trail/beam shaders and pipelines.
6. Add sample sword arc, projectile trail, lightning beam, or magic link.
7. Add diagnostics and tests for trail segment budgets.

Acceptance criteria:
- trails and beams render correctly from normal gameplay camera angles.
- endpoints and trail histories can update every frame.
- trails and beams respect HDR, bloom, fog, and depth testing.

### Stage 13.10: Asset Manifests

Tasks:
1. Add `.njvfx.json` manifest reader.
2. Validate effect, emitter, material, curve, gradient, flipbook, and texture fields.
3. Add content diagnostics.
4. Add import tests.
5. Update sample content to load at least one effect from data.

Acceptance criteria:
- VFX can be authored without recompiling.
- manifest errors identify the asset path and field.
- runtime effect behavior matches code-authored equivalent tests.

### Stage 13.11: Optional GPU Simulation

Tasks:
1. Add compute simulation shaders.
2. Add alive/dead particle lists.
3. Add GPU emission and compaction.
4. Add indirect draw support.
5. Add GPU simulation diagnostics.
6. Add stress scene with 100k simple particles.

Acceptance criteria:
- GPU path accelerates high-count effects without changing authoring data.
- CPU path remains available and tested.
- GPU path avoids per-frame CPU readback.

## Performance Targets

Define initial desktop targets:
- 10k alpha/premultiplied particles under 1.0 ms GPU on a mid-range target GPU.
- 50k additive/simple particles under 2.0 ms GPU on a mid-range target GPU.
- CPU simulation and build under 1.0 ms for 10k simple particles.
- particle upload under 8 MiB per frame by default.
- no managed allocations in the common update/render path after warm-up.

These are starting targets. Replace them with concrete target hardware from Phase 19 when available.

## Risk Register

High-risk areas:
- transparent ordering between mesh transparent objects and particles.
- depth convention mistakes in soft particles.
- texture color-space errors causing dark fringes or incorrect emissive intensity.
- excessive CPU sort cost for many alpha particles.
- managed allocations in simulation and batching.
- too many pipeline variants if material features grow unchecked.
- budget throttling that changes gameplay-critical effects.

Mitigations:
- keep first-slice materials simple.
- test depth reconstruction explicitly.
- prefer premultiplied alpha for authored smoke/fire.
- expose sorting counts and costs in diagnostics.
- pool hot-path arrays.
- mark gameplay-critical effects with priority.
- add GPU simulation only after CPU profiling shows a real bottleneck.

## Industry-Standard Definition Of Done

Phase 13 is production-ready when:
1. Particle effects are data-authored and reusable.
2. Runtime emitters are deterministic and budgeted.
3. Billboard particles support flipbooks, soft depth fade, HDR emissive, and multiple blend modes.
4. Trails and beams are supported for common gameplay VFX.
5. The renderer batches and sorts particles predictably.
6. Expensive behavior is measurable through diagnostics.
7. Debug views make bounds, soft fade, overdraw, flipbook frames, and sort order inspectable.
8. The sample app demonstrates fire, smoke, sparks, rain, magic, impacts, trails, and beams.
9. Validation layers are clean during startup, rendering, resize, toggles, restart, and shutdown.
10. CPU tests cover authoring data, simulation determinism, curves, gradients, spawn shapes, budgets, sort keys, struct layout, and shader builds.
11. RenderDoc captures show clear pass/resource names and expected frame order.
12. The system degrades gracefully when budgets are exceeded instead of stalling, allocating unbounded memory, or silently dropping all VFX.

## Final Acceptance Checklist

Before marking Phase 13 complete:
1. Run `dotnet build Njulf.sln`.
2. Run `dotnet test Njulf.sln`.
3. Run `NjulfHelloGame` with Vulkan validation enabled.
4. Capture a RenderDoc frame with particle debug view disabled.
5. Capture a RenderDoc frame with soft-particle debug view enabled.
6. Verify `ParticlePass` runs after transparent mesh rendering and before fog/bloom.
7. Verify emissive particles contribute to bloom.
8. Verify smoke fades against scene depth and the reflection test spheres.
9. Verify particle toggles, pause, restart, and debug cycling work.
10. Verify diagnostics report particle counts, batches, upload bytes, CPU times, GPU times, and budget pressure.
11. Resize the window while particles are alive.
12. Restart effects repeatedly with fixed and random seeds.
13. Shut down while particles, trails, and beams are alive.

The phase is done when VFX artists or gameplay programmers can add a new fire, smoke, spark, rain, magic, trail, or beam effect through data and sample-level wiring, inspect it with diagnostics and RenderDoc, and tune it without editing renderer internals.
