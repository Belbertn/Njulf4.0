using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Core.Vfx;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Silk.NET.Vulkan;
using static Njulf.Rendering.RenderingConstants;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace Njulf.Rendering.Resources
{
    public sealed class ParticleSystemManager : IDisposable
    {
        private const float MaxDeltaSeconds = 1.0f / 15.0f;
        private const uint InitialParticleCapacity = 1024;
        private const uint InitialBatchCapacity = 128;

        private static readonly ulong ParticleStride = (ulong)Marshal.SizeOf<GPUParticleInstance>();
        private static readonly ulong BatchStride = (ulong)Marshal.SizeOf<GPUParticleBatch>();

        private readonly Dictionary<ParticleEffectInstance, InstanceState> _instances = new();
        private readonly Dictionary<string, int> _textureIndexCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ParticleEffectInstance> _removeScratch = new();
        private readonly List<GPUParticleInstance> _gpuInstanceScratch = new();
        private readonly List<GPUParticleBatch> _gpuBatchScratch = new();
        private readonly ParticleSimulationFrame _frame = new();
        private readonly object _lock = new();
        private readonly VulkanContext? _context;
        private readonly BufferManager? _bufferManager;
        private readonly StagingRing? _stagingRing;
        private ParticleBuffer[] _instanceBuffers = [];
        private ParticleBuffer[] _batchBuffers = [];
        private BindlessHeap? _registeredBindlessHeap;

        public ParticleSimulationFrame LastFrame => _frame;
        public bool SimulationPaused { get; set; }

        public ParticleSystemManager()
        {
        }

        public ParticleSystemManager(VulkanContext context, BufferManager bufferManager, StagingRing stagingRing)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _bufferManager = bufferManager ?? throw new ArgumentNullException(nameof(bufferManager));
            _stagingRing = stagingRing ?? throw new ArgumentNullException(nameof(stagingRing));
            _instanceBuffers = new ParticleBuffer[FramesInFlight];
            _batchBuffers = new ParticleBuffer[FramesInFlight];

            for (int i = 0; i < FramesInFlight; i++)
            {
                _instanceBuffers[i] = CreateBuffer(InitialParticleCapacity, ParticleStride, $"Particle.InstanceBuffer.Frame{i}");
                _batchBuffers[i] = CreateBuffer(InitialBatchCapacity, BatchStride, $"Particle.BatchBuffer.Frame{i}");
            }
        }

        public ParticleSimulationFrame Update(
            Scene scene,
            ParticleSettings settings,
            Vector3 cameraPosition,
            float deltaSeconds)
        {
            if (scene == null)
                throw new ArgumentNullException(nameof(scene));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            _frame.Clear();
            if (!settings.Enabled || settings.MaxParticles == 0 || settings.MaxEmitters == 0)
            {
                RemoveDeadSceneInstances(scene);
                return _frame;
            }

            long simulationStart = Stopwatch.GetTimestamp();
            float clampedDelta = Math.Clamp(deltaSeconds, 0.0f, MaxDeltaSeconds);
            int globalLiveBudget = settings.MaxParticles;
            int liveParticles = 0;
            int simulatedParticles = 0;
            int renderedParticles = 0;
            int culledParticles = 0;
            int emitterCount = 0;
            int visibleEffectCount = 0;
            int softParticles = 0;
            int flipbookParticles = 0;
            int alphaParticles = 0;
            int additiveParticles = 0;
            int budgetExceeded = 0;
            int trailCount = 0;
            int trailSegmentCount = 0;
            int beamCount = 0;
            int beamSegmentCount = 0;

            RemoveDeadSceneInstances(scene);

            for (int effectIndex = 0; effectIndex < scene.ParticleEffects.Count; effectIndex++)
            {
                ParticleEffectInstance instance = scene.ParticleEffects[effectIndex];
                if (!_instances.TryGetValue(instance, out InstanceState? state))
                {
                    state = new InstanceState(instance);
                    _instances.Add(instance, state);
                }

                if (state.Seed != instance.RandomSeed || instance.ClearRequested)
                    state.Reset(instance);

                if (instance.ClearRequested)
                    instance.ConsumeClearRequest();

                if (!instance.Visible)
                {
                    culledParticles += state.LiveParticleCount;
                    continue;
                }

                visibleEffectCount++;

                ParticleEmitterDefinition[] emitters = state.Emitters;
                int activeEmitterLimit = Math.Min(emitters.Length, settings.MaxEmitters - emitterCount);
                for (int emitterIndex = 0; emitterIndex < activeEmitterLimit; emitterIndex++)
                {
                    ParticleEmitterDefinition definition = emitters[emitterIndex];
                    EmitterState emitter = state.EmitterStates[emitterIndex];
                    emitterCount++;

                    bool canSimulate = !SimulationPaused && !instance.Paused;
                    if (canSimulate)
                    {
                        SimulateEmitter(
                            instance,
                            state,
                            emitter,
                            definition,
                            settings,
                            clampedDelta,
                            ref globalLiveBudget,
                            ref budgetExceeded,
                            ref simulatedParticles);
                    }

                    liveParticles += emitter.Particles.Count;
                    BuildRenderInstances(
                        instance,
                        definition,
                        emitter,
                        settings,
                        cameraPosition,
                        effectIndex,
                        emitterIndex,
                        ref renderedParticles,
                        ref culledParticles,
                        ref alphaParticles,
                        ref additiveParticles,
                        ref softParticles,
                        ref flipbookParticles);
                }

                BuildTrailAndBeamRenderInstances(
                    instance,
                    state,
                    settings,
                    cameraPosition,
                    clampedDelta,
                    effectIndex,
                    ref trailCount,
                    ref trailSegmentCount,
                    ref beamCount,
                    ref beamSegmentCount,
                    ref budgetExceeded);
            }

            long simulationMicroseconds = ElapsedMicroseconds(simulationStart);
            long buildStart = Stopwatch.GetTimestamp();
            SortAndBatch();
            long buildMicroseconds = ElapsedMicroseconds(buildStart);

            _frame.Stats = new ParticleSystemFrameStats
            {
                Effects = visibleEffectCount,
                Emitters = emitterCount,
                LiveParticles = liveParticles,
                SimulatedParticles = simulatedParticles,
                CulledParticles = culledParticles,
                RenderedParticles = renderedParticles,
                Batches = _frame.Batches.Count,
                AlphaParticles = alphaParticles,
                AdditiveParticles = additiveParticles,
                SoftParticles = softParticles,
                FlipbookParticles = flipbookParticles,
                Trails = trailCount,
                TrailSegments = trailSegmentCount,
                Beams = beamCount,
                ParticleBudgetExceeded = budgetExceeded,
                UploadBudgetExceeded = (ulong)_frame.Instances.Count * ParticleStride > settings.MaxUploadBytesPerFrame ? 1 : 0,
                InstanceUploadBytes = (ulong)_frame.Instances.Count * ParticleStride,
                TrailBeamUploadBytes = (ulong)(trailSegmentCount + beamSegmentCount) * ParticleStride,
                SimulationMicroseconds = simulationMicroseconds,
                BuildMicroseconds = buildMicroseconds
            };

            return _frame;
        }

        public static void PopulateSceneData(
            SceneRenderingData sceneData,
            ParticleSettings settings,
            ParticleSimulationFrame frame)
        {
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            ParticleSystemFrameStats stats = frame.Stats;
            sceneData.ParticlesEnabled = settings.Enabled;
            sceneData.ParticleSimulationMode = settings.SimulationMode;
            sceneData.ParticleDebugView = settings.DebugView;
            sceneData.ParticleEffectCount = stats.Effects;
            sceneData.ParticleEmitterCount = stats.Emitters;
            sceneData.LiveParticleCount = stats.LiveParticles;
            sceneData.SimulatedParticleCount = stats.SimulatedParticles;
            sceneData.CulledParticleCount = stats.CulledParticles;
            sceneData.RenderedParticleCount = stats.RenderedParticles;
            sceneData.ParticleBatchCount = stats.Batches;
            sceneData.AlphaParticleCount = stats.AlphaParticles;
            sceneData.AdditiveParticleCount = stats.AdditiveParticles;
            sceneData.SoftParticleCount = stats.SoftParticles;
            sceneData.FlipbookParticleCount = stats.FlipbookParticles;
            sceneData.TrailCount = stats.Trails;
            sceneData.TrailSegmentCount = stats.TrailSegments;
            sceneData.BeamCount = stats.Beams;
            sceneData.ParticleBudgetExceeded = stats.ParticleBudgetExceeded;
            sceneData.ParticleUploadBudgetExceeded = stats.UploadBudgetExceeded;
            sceneData.ParticleInstanceUploadBytes = stats.InstanceUploadBytes;
            sceneData.TrailBeamUploadBytes = stats.TrailBeamUploadBytes;
            sceneData.CpuParticleSimulationMicroseconds = stats.SimulationMicroseconds;
            sceneData.CpuParticleBuildMicroseconds = stats.BuildMicroseconds;
        }

        public void RegisterBuffers(BindlessHeap bindlessHeap)
        {
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));
            if (_bufferManager == null)
                return;

            lock (_lock)
            {
                _registeredBindlessHeap = bindlessHeap;
                UpdateRegisteredBindlessBuffers();
            }
        }

        public void UploadFrame(
            ParticleSimulationFrame frame,
            ParticleSettings settings,
            TextureManager textureManager,
            CommandBuffer commandBuffer,
            SceneRenderingData sceneData)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (textureManager == null)
                throw new ArgumentNullException(nameof(textureManager));
            if (sceneData == null)
                throw new ArgumentNullException(nameof(sceneData));
            if (_context == null || _bufferManager == null || _stagingRing == null)
                return;
            if (commandBuffer.Handle == 0)
                throw new ArgumentException("A valid command buffer is required for particle uploads.", nameof(commandBuffer));

            lock (_lock)
            {
                int frameIndex = _stagingRing.CurrentFrameIndex;
                BuildGpuScratch(frame, settings, textureManager);
                EnsureCapacity(ref _instanceBuffers[frameIndex], CheckedCount(_gpuInstanceScratch.Count), ParticleStride, $"Particle.InstanceBuffer.Frame{frameIndex}");
                EnsureCapacity(ref _batchBuffers[frameIndex], CheckedCount(_gpuBatchScratch.Count), BatchStride, $"Particle.BatchBuffer.Frame{frameIndex}");
                UpdateRegisteredBindlessBuffers();

                ulong uploadBytes = 0;
                uploadBytes += UploadSpan(CollectionsMarshal.AsSpan(_gpuInstanceScratch), _instanceBuffers[frameIndex].Handle, commandBuffer);
                uploadBytes += UploadSpan(CollectionsMarshal.AsSpan(_gpuBatchScratch), _batchBuffers[frameIndex].Handle, commandBuffer);
                RecordUploadToGraphicsBarrier(commandBuffer, frameIndex);

                sceneData.ParticleInstanceBuffer = _instanceBuffers[frameIndex].Handle;
                sceneData.ParticleBatchBuffer = _batchBuffers[frameIndex].Handle;
                sceneData.ParticleInstanceBufferSize = _instanceBuffers[frameIndex].ByteSize;
                sceneData.ParticleBatchBufferSize = _batchBuffers[frameIndex].ByteSize;
                sceneData.ParticleInstanceUploadBytes = uploadBytes;
                sceneData.ParticleDrawCallCount = _gpuBatchScratch.Count;
                sceneData.ParticleBatches.Clear();
                sceneData.ParticleBatches.AddRange(_gpuBatchScratch);
                if (uploadBytes > settings.MaxUploadBytesPerFrame)
                    sceneData.ParticleUploadBudgetExceeded = 1;
            }
        }

        private void SimulateEmitter(
            ParticleEffectInstance instance,
            InstanceState instanceState,
            EmitterState emitter,
            ParticleEmitterDefinition definition,
            ParticleSettings settings,
            float deltaSeconds,
            ref int globalLiveBudget,
            ref int budgetExceeded,
            ref int simulatedParticles)
        {
            emitter.TimeSeconds += deltaSeconds;
            float localTime = emitter.TimeSeconds - Math.Max(0.0f, definition.StartDelaySeconds);

            for (int i = emitter.Particles.Count - 1; i >= 0; i--)
            {
                ParticleState particle = emitter.Particles[i];
                particle.AgeSeconds += deltaSeconds;
                if (particle.AgeSeconds >= particle.LifetimeSeconds)
                {
                    RemoveParticleAtSwapBack(emitter.Particles, i);
                    continue;
                }

                float drag = Math.Clamp(definition.Drag, 0.0f, 100.0f);
                float dragFactor = MathF.Max(0.0f, 1.0f - drag * deltaSeconds);
                particle.Velocity = (particle.Velocity + definition.Acceleration * deltaSeconds) * dragFactor;
                particle.Position += particle.Velocity * deltaSeconds;
                particle.RotationRadians += particle.AngularVelocityRadiansPerSecond * deltaSeconds;
                emitter.Particles[i] = particle;
                simulatedParticles++;
            }

            if (!instance.Playing || localTime < 0.0f)
                return;

            bool activeDuration = definition.Looping ||
                definition.DurationSeconds <= 0.0f ||
                localTime <= definition.DurationSeconds;
            if (!activeDuration)
                return;

            if (definition.BurstCount > 0 && !emitter.BurstFired && localTime >= Math.Max(0.0f, definition.BurstTimeSeconds))
            {
                SpawnParticles(instance, instanceState, emitter, definition, definition.BurstCount, settings, ref globalLiveBudget, ref budgetExceeded);
                emitter.BurstFired = true;
            }

            float spawnRate = Math.Max(0.0f, definition.SpawnRatePerSecond * settings.GlobalSpawnRateScale);
            emitter.SpawnAccumulator += spawnRate * deltaSeconds;
            int spawnCount = (int)MathF.Floor(emitter.SpawnAccumulator);
            if (spawnCount > 0)
            {
                emitter.SpawnAccumulator -= spawnCount;
                SpawnParticles(instance, instanceState, emitter, definition, spawnCount, settings, ref globalLiveBudget, ref budgetExceeded);
            }

            if (definition.Looping && definition.DurationSeconds > 0.0f && localTime > definition.DurationSeconds)
            {
                emitter.TimeSeconds = definition.StartDelaySeconds;
                emitter.BurstFired = false;
            }
        }

        private static void SpawnParticles(
            ParticleEffectInstance instance,
            InstanceState instanceState,
            EmitterState emitter,
            ParticleEmitterDefinition definition,
            int spawnCount,
            ParticleSettings settings,
            ref int globalLiveBudget,
            ref int budgetExceeded)
        {
            int emitterBudget = Math.Clamp(definition.MaxParticles, 0, settings.MaxParticles);
            for (int i = 0; i < spawnCount; i++)
            {
                if (globalLiveBudget <= 0 || emitter.Particles.Count >= emitterBudget)
                {
                    budgetExceeded = 1;
                    return;
                }

                SampleSpawn(definition.SpawnShape, ref emitter.Random, out Vector3 localPosition, out Vector3 localDirection);
                Vector3 initialVelocity = new(
                    emitter.Random.NextFloat(definition.InitialVelocityMin.X, definition.InitialVelocityMax.X),
                    emitter.Random.NextFloat(definition.InitialVelocityMin.Y, definition.InitialVelocityMax.Y),
                    emitter.Random.NextFloat(definition.InitialVelocityMin.Z, definition.InitialVelocityMax.Z));
                initialVelocity *= settings.GlobalVelocityScale;

                Vector3 position = definition.LocalSpace ? localPosition : TransformPoint(instance.WorldMatrix, localPosition);
                Vector3 directionVelocity = localDirection * initialVelocity.Length();
                Vector3 velocity = initialVelocity.LengthSquared() > 0.000001f ? initialVelocity : directionVelocity;
                if (!definition.LocalSpace)
                    velocity = TransformDirection(instance.WorldMatrix, velocity);

                float lifetime = Math.Max(0.001f, definition.LifetimeSeconds.Sample(emitter.Random.NextFloat()));
                ParticleFlipbook? flipbook = definition.Material.Flipbook;
                int startFrame = flipbook?.RandomStartFrame == true
                    ? emitter.Random.NextInt(0, flipbook.FrameCount)
                    : 0;

                emitter.Particles.Add(new ParticleState
                {
                    Position = position,
                    Velocity = velocity,
                    LifetimeSeconds = lifetime,
                    BaseSize = Math.Max(0.0f, definition.Size.Sample(0.0f)),
                    RotationRadians = definition.RotationRadians.Sample(0.0f),
                    AngularVelocityRadiansPerSecond = definition.AngularVelocityRadiansPerSecond.Sample(emitter.Random.NextFloat()),
                    FlipbookStartFrame = startFrame,
                    StableId = instanceState.NextParticleId++
                });
                globalLiveBudget--;
            }
        }

        private void BuildRenderInstances(
            ParticleEffectInstance instance,
            ParticleEmitterDefinition definition,
            EmitterState emitter,
            ParticleSettings settings,
            Vector3 cameraPosition,
            int effectId,
            int emitterId,
            ref int renderedParticles,
            ref int culledParticles,
            ref int alphaParticles,
            ref int additiveParticles,
            ref int softParticles,
            ref int flipbookParticles)
        {
            float maxDrawDistance = Math.Max(0.0f, definition.MaxDrawDistance * settings.DistanceCullMultiplier);
            float maxDrawDistanceSquared = maxDrawDistance * maxDrawDistance;
            ParticleMaterialDefinition material = definition.Material;

            for (int i = 0; i < emitter.Particles.Count; i++)
            {
                ParticleState particle = emitter.Particles[i];
                float distanceSquared = Vector3.DistanceSquared(cameraPosition, particle.Position);
                if (distanceSquared > maxDrawDistanceSquared)
                {
                    culledParticles++;
                    continue;
                }

                float normalizedLifetime = Math.Clamp(particle.AgeSeconds / particle.LifetimeSeconds, 0.0f, 1.0f);
                float size = Math.Max(0.0f, definition.Size.Sample(normalizedLifetime));
                if (size <= 0.0f)
                {
                    culledParticles++;
                    continue;
                }

                Color color = definition.ColorOverLife.Sample(normalizedLifetime);
                float emissive = definition.EmissiveOverLife.Sample(normalizedLifetime) * settings.GlobalEmissiveScale;
                int frame = material.Flipbook?.GetFrame(particle.AgeSeconds, particle.LifetimeSeconds, particle.FlipbookStartFrame) ?? 0;

                _frame.Instances.Add(new ParticleRenderInstance(
                    particle.Position,
                    particle.Velocity,
                    color,
                    size,
                    particle.RotationRadians,
                    emissive,
                    normalizedLifetime,
                    frame,
                    effectId,
                    emitterId,
                    material.BillboardMode,
                    material,
                    distanceSquared));

                renderedParticles++;
                if (IsAlphaSorted(material.BlendMode))
                    alphaParticles++;
                else if (IsAdditive(material.BlendMode))
                    additiveParticles++;
                if (settings.SoftParticlesEnabled && material.SoftParticles)
                    softParticles++;
                if (material.Flipbook != null)
                    flipbookParticles++;
            }
        }

        private void BuildTrailAndBeamRenderInstances(
            ParticleEffectInstance instance,
            InstanceState state,
            ParticleSettings settings,
            Vector3 cameraPosition,
            float deltaSeconds,
            int effectId,
            ref int trailCount,
            ref int trailSegmentCount,
            ref int beamCount,
            ref int beamSegmentCount,
            ref int budgetExceeded)
        {
            bool canSimulate = !SimulationPaused && !instance.Paused && instance.Playing && !instance.Stopped;
            if (canSimulate)
                state.EffectTimeSeconds += deltaSeconds;

            BuildTrailRenderInstances(
                instance,
                state,
                settings,
                cameraPosition,
                deltaSeconds,
                canSimulate,
                effectId,
                ref trailCount,
                ref trailSegmentCount,
                ref budgetExceeded);

            BuildBeamRenderInstances(
                instance,
                state,
                settings,
                cameraPosition,
                effectId,
                trailSegmentCount,
                ref beamCount,
                ref beamSegmentCount,
                ref budgetExceeded);
        }

        private void BuildTrailRenderInstances(
            ParticleEffectInstance instance,
            InstanceState state,
            ParticleSettings settings,
            Vector3 cameraPosition,
            float deltaSeconds,
            bool canSimulate,
            int effectId,
            ref int trailCount,
            ref int trailSegmentCount,
            ref int budgetExceeded)
        {
            IReadOnlyList<TrailDefinition> trails = state.Effect.Trails;
            for (int trailIndex = 0; trailIndex < trails.Count; trailIndex++)
            {
                TrailDefinition trail = trails[trailIndex];
                TrailState trailState = state.TrailStates[trailIndex];
                float lifetime = Math.Max(0.001f, trail.LifetimeSeconds);

                if (canSimulate)
                {
                    AgeTrailSamples(trailState.Samples, deltaSeconds, lifetime);
                    Vector3 position = TransformPoint(instance.WorldMatrix, trail.LocalOffset);
                    AddTrailSample(trailState.Samples, position, trail, lifetime);
                }

                if (trailState.Samples.Count < 2)
                    continue;
                if (trailCount >= settings.MaxTrails || trailSegmentCount >= settings.MaxTrailSegments)
                {
                    budgetExceeded = 1;
                    continue;
                }

                trailCount++;
                int maxSegments = Math.Min(trail.MaxSegments, trailState.Samples.Count - 1);
                int remainingSegments = settings.MaxTrailSegments - trailSegmentCount;
                int segmentsToDraw = Math.Min(maxSegments, remainingSegments);
                int firstSegment = Math.Max(1, trailState.Samples.Count - segmentsToDraw);

                for (int sampleIndex = firstSegment; sampleIndex < trailState.Samples.Count; sampleIndex++)
                {
                    TrailSample start = trailState.Samples[sampleIndex - 1];
                    TrailSample end = trailState.Samples[sampleIndex];
                    float normalizedAge = Math.Clamp((start.AgeSeconds + end.AgeSeconds) * 0.5f / lifetime, 0.0f, 1.0f);
                    float normalizedLife = 1.0f - normalizedAge;
                    EmitRibbonSegment(
                        (start.Position + end.Position) * 0.5f,
                        end.Position - start.Position,
                        trail.Width.Sample(normalizedLife),
                        trail.ColorOverLife.Sample(normalizedLife),
                        trail.Material,
                        effectId,
                        0x4000 + trailIndex,
                        normalizedLife,
                        cameraPosition);
                    trailSegmentCount++;
                }
            }
        }

        private void BuildBeamRenderInstances(
            ParticleEffectInstance instance,
            InstanceState state,
            ParticleSettings settings,
            Vector3 cameraPosition,
            int effectId,
            int trailSegmentCount,
            ref int beamCount,
            ref int beamSegmentCount,
            ref int budgetExceeded)
        {
            IReadOnlyList<BeamDefinition> beams = state.Effect.Beams;
            for (int beamIndex = 0; beamIndex < beams.Count; beamIndex++)
            {
                BeamDefinition beam = beams[beamIndex];
                int segmentCount = Math.Clamp(beam.SegmentCount, 1, 256);
                int remainingSegments = settings.MaxTrailSegments - trailSegmentCount - beamSegmentCount;
                if (remainingSegments <= 0)
                {
                    budgetExceeded = 1;
                    continue;
                }

                int segmentsToDraw = Math.Min(segmentCount, remainingSegments);
                Vector3 start = TransformPoint(instance.WorldMatrix, beam.LocalStart);
                Vector3 end = TransformPoint(instance.WorldMatrix, beam.LocalEnd);
                Vector3 axis = end - start;
                Vector3 normal = GetStableBeamNormal(axis);

                beamCount++;
                for (int segmentIndex = 0; segmentIndex < segmentsToDraw; segmentIndex++)
                {
                    float t0 = segmentIndex / (float)segmentCount;
                    float t1 = (segmentIndex + 1) / (float)segmentCount;
                    Vector3 p0 = Vector3.Lerp(start, end, t0) + ComputeBeamNoise(normal, beam.NoiseAmplitude, state.EffectTimeSeconds, beamIndex, segmentIndex, t0);
                    Vector3 p1 = Vector3.Lerp(start, end, t1) + ComputeBeamNoise(normal, beam.NoiseAmplitude, state.EffectTimeSeconds, beamIndex, segmentIndex + 1, t1);
                    float t = (t0 + t1) * 0.5f;
                    EmitRibbonSegment(
                        (p0 + p1) * 0.5f,
                        p1 - p0,
                        beam.Width.Sample(t),
                        beam.Color.Sample(t),
                        beam.Material,
                        effectId,
                        0x5000 + beamIndex,
                        t,
                        cameraPosition);
                    beamSegmentCount++;
                }
            }
        }

        private void EmitRibbonSegment(
            Vector3 center,
            Vector3 axis,
            float width,
            Color color,
            ParticleMaterialDefinition material,
            int effectId,
            int emitterId,
            float normalizedLifetime,
            Vector3 cameraPosition)
        {
            if (axis.LengthSquared() <= 0.000001f || width <= 0.0f || color.A <= 0.0f)
                return;

            float distanceSquared = Vector3.DistanceSquared(cameraPosition, center);
            _frame.Instances.Add(new ParticleRenderInstance(
                center,
                axis,
                color,
                width,
                0.0f,
                1.0f,
                Math.Clamp(normalizedLifetime, 0.0f, 1.0f),
                0,
                effectId,
                emitterId,
                ParticleBillboardMode.RibbonSegment,
                material,
                distanceSquared));
        }

        private static void AgeTrailSamples(List<TrailSample> samples, float deltaSeconds, float lifetime)
        {
            for (int i = samples.Count - 1; i >= 0; i--)
            {
                TrailSample sample = samples[i];
                sample.AgeSeconds += deltaSeconds;
                if (sample.AgeSeconds > lifetime)
                    samples.RemoveAt(i);
                else
                    samples[i] = sample;
            }
        }

        private static void AddTrailSample(List<TrailSample> samples, Vector3 position, TrailDefinition trail, float lifetime)
        {
            int maxSamples = Math.Clamp(trail.MaxSegments + 1, 2, 4097);
            float spacing = Math.Max(0.01f, trail.Width.Sample(0.0f) * 0.5f);
            if (samples.Count == 0 || Vector3.DistanceSquared(samples[^1].Position, position) >= spacing * spacing)
                samples.Add(new TrailSample(position));
            else
                samples[^1] = new TrailSample(position);

            while (samples.Count > maxSamples)
                samples.RemoveAt(0);

            for (int i = samples.Count - 1; i >= 0; i--)
            {
                if (samples[i].AgeSeconds > lifetime)
                    samples.RemoveAt(i);
            }
        }

        private static Vector3 GetStableBeamNormal(Vector3 axis)
        {
            Vector3 direction = axis.LengthSquared() > 0.000001f ? axis.Normalized() : Vector3.UnitZ;
            Vector3 reference = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 normal = Vector3.Cross(direction, reference);
            return normal.LengthSquared() > 0.000001f ? normal.Normalized() : Vector3.UnitX;
        }

        private static Vector3 ComputeBeamNoise(Vector3 normal, float amplitude, float timeSeconds, int beamIndex, int segmentIndex, float t)
        {
            if (amplitude <= 0.0f)
                return Vector3.Zero;

            float phase = timeSeconds * 7.0f + beamIndex * 1.618f + segmentIndex * 0.73f + t * MathF.PI * 2.0f;
            return normal * (MathF.Sin(phase) * amplitude);
        }

        private void SortAndBatch()
        {
            _frame.Instances.Sort(static (a, b) =>
            {
                int bucket = GetBlendBucket(a.Material.BlendMode).CompareTo(GetBlendBucket(b.Material.BlendMode));
                if (bucket != 0)
                    return bucket;

                if (IsAlphaSorted(a.Material.BlendMode))
                {
                    int distance = b.SortDistanceSquared.CompareTo(a.SortDistanceSquared);
                    if (distance != 0)
                        return distance;
                }

                int material = StringComparer.Ordinal.Compare(a.Material.Name, b.Material.Name);
                if (material != 0)
                    return material;

                int emitter = a.EmitterId.CompareTo(b.EmitterId);
                if (emitter != 0)
                    return emitter;

                return a.FlipbookFrame.CompareTo(b.FlipbookFrame);
            });

            _frame.Batches.Clear();
            if (_frame.Instances.Count == 0)
                return;

            int batchStart = 0;
            ParticleRenderInstance first = _frame.Instances[0];
            int batchId = 0;
            for (int i = 1; i <= _frame.Instances.Count; i++)
            {
                if (i < _frame.Instances.Count && CanBatch(first, _frame.Instances[i]))
                    continue;

                _frame.Batches.Add(new ParticleBatch(
                    batchStart,
                    i - batchStart,
                    first.Material,
                    first.Material.BlendMode,
                    first.Material.TexturePath?.GetHashCode(StringComparison.Ordinal) ?? 0,
                    batchId++));

                if (i < _frame.Instances.Count)
                {
                    batchStart = i;
                    first = _frame.Instances[i];
                }
            }
        }

        private void BuildGpuScratch(ParticleSimulationFrame frame, ParticleSettings settings, TextureManager textureManager)
        {
            _gpuInstanceScratch.Clear();
            _gpuBatchScratch.Clear();

            for (int i = 0; i < frame.Instances.Count; i++)
            {
                ParticleRenderInstance instance = frame.Instances[i];
                ParticleFlipbook? flipbook = instance.Material.Flipbook;
                int textureIndex = ResolveTextureIndex(instance.Material.TexturePath, textureManager);
                float softDistance = settings.SoftParticlesEnabled && instance.Material.SoftParticles
                    ? settings.SoftParticleDistance
                    : 0.0f;

                _gpuInstanceScratch.Add(new GPUParticleInstance
                {
                    PositionSize = new Vector4(instance.Position, instance.Size),
                    VelocityRotation = new Vector4(instance.Velocity, instance.RotationRadians),
                    Color = instance.Color.ToVector4(),
                    EmissiveLifetimeSoftClip = new Vector4(
                        instance.EmissiveIntensity,
                        instance.NormalizedLifetime,
                        softDistance,
                        instance.Material.AlphaClipThreshold),
                    TextureIndex = checked((uint)textureIndex),
                    FlipbookFrame = checked((uint)Math.Max(0, instance.FlipbookFrame)),
                    FlipbookColumns = checked((uint)(flipbook?.Columns ?? 1)),
                    FlipbookRows = checked((uint)(flipbook?.Rows ?? 1)),
                    BlendMode = (uint)instance.Material.BlendMode,
                    BillboardMode = (uint)instance.BillboardMode,
                    DebugId = checked((uint)(((instance.EffectId & 0xFFFF) << 16) | (instance.EmitterId & 0xFFFF))),
                    Padding0 = 0
                });
            }

            for (int i = 0; i < frame.Batches.Count; i++)
            {
                ParticleBatch batch = frame.Batches[i];
                _gpuBatchScratch.Add(new GPUParticleBatch
                {
                    Start = checked((uint)batch.Start),
                    Count = checked((uint)batch.Count),
                    BlendMode = (uint)batch.BlendMode,
                    Padding0 = 0
                });
            }
        }

        private int ResolveTextureIndex(string? texturePath, TextureManager textureManager)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
                return BindlessIndex.DefaultWhiteTexture;

            string fullPath = System.IO.Path.GetFullPath(texturePath);
            if (_textureIndexCache.TryGetValue(fullPath, out int cachedIndex))
                return cachedIndex;

            TextureHandle texture = textureManager.LoadOptionalTextureFromFile(
                fullPath,
                textureManager.DefaultWhiteTexture,
                generateMipmaps: true,
                srgb: true);
            int index = textureManager.GetBindlessTextureIndex(texture);
            if (index < 0)
                index = BindlessIndex.DefaultWhiteTexture;

            _textureIndexCache[fullPath] = index;
            return index;
        }

        private ParticleBuffer CreateBuffer(uint elementCapacity, ulong stride, string debugName)
        {
            if (_context == null || _bufferManager == null)
                return default;

            uint capacity = Math.Max(1u, elementCapacity);
            ulong byteSize = checked(capacity * stride);
            BufferHandle handle = _bufferManager.CreateDeviceBuffer(
                byteSize,
                BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
                requireDeviceAddress: false,
                MemoryBudgetCategory.ObjectAndInstanceBuffers,
                debugName);
            _context.SetDebugName(_bufferManager.GetBuffer(handle).Handle, ObjectType.Buffer, debugName);
            return new ParticleBuffer(handle, capacity, byteSize);
        }

        private void EnsureCapacity(ref ParticleBuffer buffer, uint requiredElements, ulong stride, string debugName)
        {
            if (_bufferManager == null)
                return;

            if (!buffer.Handle.IsValid)
            {
                buffer = CreateBuffer(Math.Max(1u, requiredElements), stride, debugName);
                return;
            }

            if (requiredElements <= buffer.ElementCapacity)
                return;

            uint newCapacity = buffer.ElementCapacity;
            do
            {
                newCapacity = checked(newCapacity * 2);
            }
            while (newCapacity < requiredElements);

            DestroyIfValid(buffer.Handle);
            buffer = CreateBuffer(newCapacity, stride, debugName);
        }

        private unsafe ulong UploadSpan<T>(ReadOnlySpan<T> data, BufferHandle destination, CommandBuffer commandBuffer)
            where T : unmanaged
        {
            if (data.IsEmpty || _context == null || _bufferManager == null || _stagingRing == null)
                return 0;

            return GpuBufferUploader.UploadSpanToBuffer(
                _context,
                _bufferManager,
                _stagingRing,
                commandBuffer,
                destination,
                data).ByteCount;
        }

        private unsafe void RecordUploadToGraphicsBarrier(CommandBuffer commandBuffer, int frameIndex)
        {
            if (_context == null || _bufferManager == null)
                return;

            BufferMemoryBarrier2* barriers = stackalloc BufferMemoryBarrier2[2];
            uint barrierCount = 0;

            if (_gpuInstanceScratch.Count > 0)
                barriers[barrierCount++] = CreateTransferToGraphicsReadBarrier(_instanceBuffers[frameIndex].Handle);
            if (_gpuBatchScratch.Count > 0)
                barriers[barrierCount++] = CreateTransferToGraphicsReadBarrier(_batchBuffers[frameIndex].Handle);
            if (barrierCount == 0)
                return;

            var dependencyInfo = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                BufferMemoryBarrierCount = barrierCount,
                PBufferMemoryBarriers = barriers
            };

            _context.Api.CmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        }

        private BufferMemoryBarrier2 CreateTransferToGraphicsReadBarrier(BufferHandle handle)
        {
            return new BufferMemoryBarrier2
            {
                SType = StructureType.BufferMemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.TransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit,
                DstAccessMask = AccessFlags2.ShaderStorageReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = _bufferManager!.GetBuffer(handle),
                Offset = 0,
                Size = Vk.WholeSize
            };
        }

        private void UpdateRegisteredBindlessBuffers()
        {
            if (_registeredBindlessHeap == null || _bufferManager == null || _instanceBuffers.Length == 0)
                return;

            RegisterStorageBuffer(BindlessIndex.ParticleInstanceBufferBase, _instanceBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.ParticleInstanceBufferFrame1, _instanceBuffers[1].Handle);
            RegisterStorageBuffer(BindlessIndex.ParticleBatchBufferBase, _batchBuffers[0].Handle);
            RegisterStorageBuffer(BindlessIndex.ParticleBatchBufferFrame1, _batchBuffers[1].Handle);
        }

        private void RegisterStorageBuffer(int bindlessIndex, BufferHandle handle)
        {
            if (_bufferManager == null || !handle.IsValid)
                return;

            VkBuffer buffer = _bufferManager.GetBuffer(handle);
            _registeredBindlessHeap!.RegisterStorageBuffer(bindlessIndex, buffer, 0, Vk.WholeSize);
        }

        private void DestroyIfValid(BufferHandle handle)
        {
            if (_bufferManager != null && handle.IsValid)
                _bufferManager.DestroyBuffer(handle);
        }

        private void RemoveDeadSceneInstances(Scene scene)
        {
            _removeScratch.Clear();
            foreach (ParticleEffectInstance instance in _instances.Keys)
            {
                if (!Contains(scene.ParticleEffects, instance))
                    _removeScratch.Add(instance);
            }

            for (int i = 0; i < _removeScratch.Count; i++)
                _instances.Remove(_removeScratch[i]);
        }

        private static bool Contains(IReadOnlyList<ParticleEffectInstance> instances, ParticleEffectInstance instance)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                if (ReferenceEquals(instances[i], instance))
                    return true;
            }

            return false;
        }

        private static bool CanBatch(ParticleRenderInstance a, ParticleRenderInstance b)
        {
            return ReferenceEquals(a.Material, b.Material) &&
                   a.Material.BlendMode == b.Material.BlendMode &&
                   a.Material.BillboardMode == b.Material.BillboardMode &&
                   a.Material.LightingMode == b.Material.LightingMode &&
                   string.Equals(a.Material.TexturePath, b.Material.TexturePath, StringComparison.Ordinal);
        }

        private static int GetBlendBucket(ParticleBlendMode mode)
        {
            return IsAlphaSorted(mode) ? 0 : 1;
        }

        private static bool IsAlphaSorted(ParticleBlendMode mode)
        {
            return mode is ParticleBlendMode.AlphaBlend or ParticleBlendMode.PremultipliedAlpha or ParticleBlendMode.AlphaClip;
        }

        private static bool IsAdditive(ParticleBlendMode mode)
        {
            return mode is ParticleBlendMode.Additive or ParticleBlendMode.SoftAdditive;
        }

        private static void SampleSpawn(
            ParticleSpawnShape shape,
            ref ParticleRandom random,
            out Vector3 position,
            out Vector3 direction)
        {
            switch (shape.Kind)
            {
                case ParticleSpawnShapeKind.Sphere:
                    direction = RandomUnitVector(ref random);
                    position = direction * (shape.Radius * MathF.Pow(random.NextFloat(), 1.0f / 3.0f));
                    break;
                case ParticleSpawnShapeKind.SphereShell:
                    direction = RandomUnitVector(ref random);
                    position = direction * shape.Radius;
                    break;
                case ParticleSpawnShapeKind.Box:
                    position = new Vector3(
                        random.NextFloat(-shape.Extents.X, shape.Extents.X),
                        random.NextFloat(-shape.Extents.Y, shape.Extents.Y),
                        random.NextFloat(-shape.Extents.Z, shape.Extents.Z));
                    direction = position.LengthSquared() > 0.000001f ? position.Normalized() : Vector3.UnitY;
                    break;
                case ParticleSpawnShapeKind.Cone:
                    {
                        float angle = random.NextFloat(0.0f, MathF.PI * 2.0f);
                        float radius = shape.Radius * MathF.Sqrt(random.NextFloat());
                        position = new Vector3(MathF.Cos(angle) * radius, 0.0f, MathF.Sin(angle) * radius);
                        float cone = MathF.Tan(shape.AngleRadians) * shape.Length;
                        direction = new Vector3(position.X + cone, shape.Length, position.Z + cone).Normalized();
                        break;
                    }
                case ParticleSpawnShapeKind.Ring:
                    {
                        float angle = random.NextFloat(0.0f, MathF.PI * 2.0f);
                        float inner = Math.Min(shape.InnerRadius, shape.Radius);
                        float radius = MathF.Sqrt(random.NextFloat(inner * inner, shape.Radius * shape.Radius));
                        position = new Vector3(MathF.Cos(angle) * radius, 0.0f, MathF.Sin(angle) * radius);
                        direction = position.LengthSquared() > 0.000001f ? position.Normalized() : Vector3.UnitY;
                        break;
                    }
                case ParticleSpawnShapeKind.Line:
                    position = new Vector3(random.NextFloat(-shape.Length * 0.5f, shape.Length * 0.5f), 0.0f, 0.0f);
                    direction = Vector3.UnitY;
                    break;
                default:
                    position = Vector3.Zero;
                    direction = Vector3.UnitY;
                    break;
            }
        }

        private static Vector3 RandomUnitVector(ref ParticleRandom random)
        {
            float z = random.NextFloat(-1.0f, 1.0f);
            float angle = random.NextFloat(0.0f, MathF.PI * 2.0f);
            float radius = MathF.Sqrt(MathF.Max(0.0f, 1.0f - z * z));
            return new Vector3(MathF.Cos(angle) * radius, z, MathF.Sin(angle) * radius);
        }

        private static Vector3 TransformPoint(Matrix4x4 matrix, Vector3 point)
        {
            return point * matrix;
        }

        private static Vector3 TransformDirection(Matrix4x4 matrix, Vector3 direction)
        {
            return new Vector3(
                direction.X * matrix.M11 + direction.Y * matrix.M21 + direction.Z * matrix.M31,
                direction.X * matrix.M12 + direction.Y * matrix.M22 + direction.Z * matrix.M32,
                direction.X * matrix.M13 + direction.Y * matrix.M23 + direction.Z * matrix.M33);
        }

        private static void RemoveParticleAtSwapBack(List<ParticleState> particles, int index)
        {
            int last = particles.Count - 1;
            particles[index] = particles[last];
            particles.RemoveAt(last);
        }

        private static int CountTrails(Scene scene)
        {
            int count = 0;
            for (int i = 0; i < scene.ParticleEffects.Count; i++)
                count += scene.ParticleEffects[i].Effect.Trails.Count;
            return count;
        }

        private static int CountBeams(Scene scene)
        {
            int count = 0;
            for (int i = 0; i < scene.ParticleEffects.Count; i++)
                count += scene.ParticleEffects[i].Effect.Beams.Count;
            return count;
        }

        private static long ElapsedMicroseconds(long startTimestamp)
        {
            return Stopwatch.GetElapsedTime(startTimestamp).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
        }

        private static uint CheckedCount(int count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count));

            return (uint)count;
        }

        public void Dispose()
        {
            if (_bufferManager == null)
                return;

            lock (_lock)
            {
                for (int i = 0; i < _instanceBuffers.Length; i++)
                {
                    DestroyIfValid(_instanceBuffers[i].Handle);
                    DestroyIfValid(_batchBuffers[i].Handle);
                }

                _gpuInstanceScratch.Clear();
                _gpuBatchScratch.Clear();
                _textureIndexCache.Clear();
            }
        }

        private readonly struct ParticleBuffer
        {
            public ParticleBuffer(BufferHandle handle, uint elementCapacity, ulong byteSize)
            {
                Handle = handle;
                ElementCapacity = elementCapacity;
                ByteSize = byteSize;
            }

            public BufferHandle Handle { get; }
            public uint ElementCapacity { get; }
            public ulong ByteSize { get; }
        }

        private sealed class InstanceState
        {
            public InstanceState(ParticleEffectInstance instance)
            {
                Effect = instance.Effect;
                Seed = instance.RandomSeed;
                Emitters = new ParticleEmitterDefinition[Effect.Emitters.Count];
                EmitterStates = new EmitterState[Effect.Emitters.Count];
                TrailStates = new TrailState[Effect.Trails.Count];
                for (int i = 0; i < Effect.Emitters.Count; i++)
                {
                    Emitters[i] = Effect.Emitters[i];
                    EmitterStates[i] = new EmitterState(CombineSeed(Seed, i));
                }

                for (int i = 0; i < TrailStates.Length; i++)
                    TrailStates[i] = new TrailState();
            }

            public ParticleEffect Effect { get; }
            public uint Seed { get; private set; }
            public ParticleEmitterDefinition[] Emitters { get; }
            public EmitterState[] EmitterStates { get; }
            public TrailState[] TrailStates { get; }
            public uint NextParticleId { get; set; } = 1;
            public float EffectTimeSeconds { get; set; }

            public int LiveParticleCount
            {
                get
                {
                    int count = 0;
                    for (int i = 0; i < EmitterStates.Length; i++)
                        count += EmitterStates[i].Particles.Count;
                    return count;
                }
            }

            public void Reset(ParticleEffectInstance instance)
            {
                Seed = instance.RandomSeed;
                NextParticleId = 1;
                EffectTimeSeconds = 0.0f;
                for (int i = 0; i < EmitterStates.Length; i++)
                    EmitterStates[i].Reset(CombineSeed(Seed, i));
                for (int i = 0; i < TrailStates.Length; i++)
                    TrailStates[i].Samples.Clear();
            }

            private static uint CombineSeed(uint seed, int emitterIndex)
            {
                unchecked
                {
                    uint value = seed == 0 ? 1u : seed;
                    value ^= (uint)(emitterIndex + 1) * 0x9E3779B9u;
                    value ^= value >> 16;
                    value *= 0x85EBCA6Bu;
                    value ^= value >> 13;
                    return value == 0 ? 1u : value;
                }
            }
        }

        private sealed class EmitterState
        {
            public EmitterState(uint seed)
            {
                Random = new ParticleRandom(seed);
            }

            public List<ParticleState> Particles { get; } = new();
            public ParticleRandom Random;
            public float TimeSeconds;
            public float SpawnAccumulator;
            public bool BurstFired;

            public void Reset(uint seed)
            {
                Particles.Clear();
                Random = new ParticleRandom(seed);
                TimeSeconds = 0.0f;
                SpawnAccumulator = 0.0f;
                BurstFired = false;
            }
        }

        private struct ParticleState
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float AgeSeconds;
            public float LifetimeSeconds;
            public float BaseSize;
            public float RotationRadians;
            public float AngularVelocityRadiansPerSecond;
            public int FlipbookStartFrame;
            public uint StableId;
        }

        private sealed class TrailState
        {
            public List<TrailSample> Samples { get; } = new();
        }

        private struct TrailSample
        {
            public TrailSample(Vector3 position)
            {
                Position = position;
                AgeSeconds = 0.0f;
            }

            public Vector3 Position;
            public float AgeSeconds;
        }
    }
}
