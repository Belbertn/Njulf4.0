using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Debug;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Resources;

namespace Njulf.Rendering.Data
{
    public enum AnimationSkinningMode : uint
    {
        Disabled = 0,
        CpuDebug = 1,
        GpuCompute = 2
    }

    public enum AnimationDebugView : uint
    {
        None = 0,
        SkinnedObjects = 64,
        JointWeights = 65,
        JointIndex = 66,
        SkinningError = 67,
        Skeleton = 68,
        AnimatedBounds = 69,
        ClipTime = 70
    }

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

    public sealed class AnimationSettings
    {
        private int _maxJointsPerSkeleton = 256;
        private int _maxAnimatedInstances = 1024;
        private float _boundsPadding = 0.25f;

        public bool Enabled { get; set; } = true;
        public AnimationSkinningMode SkinningMode { get; set; } = AnimationSkinningMode.GpuCompute;
        public AnimationDebugView DebugView { get; set; } = AnimationDebugView.None;

        public int MaxJointsPerSkeleton
        {
            get => _maxJointsPerSkeleton;
            set => _maxJointsPerSkeleton = value < 1 ? 1 : value > 1024 ? 1024 : value;
        }

        public int MaxAnimatedInstances
        {
            get => _maxAnimatedInstances;
            set => _maxAnimatedInstances = value < 0 ? 0 : value;
        }

        public bool UpdateWhenOffscreen { get; set; } = true;
        public bool UseConservativeBounds { get; set; } = true;

        public float BoundsPadding
        {
            get => _boundsPadding;
            set => _boundsPadding = Clamp(value, 0.0f, 10.0f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class ParticleSettings
    {
        private int _maxParticles = 65536;
        private int _maxEmitters = 1024;
        private int _maxBatches = 4096;
        private int _maxTrails = 4096;
        private int _maxTrailSegments = 65536;
        private float _softParticleDistance = 0.35f;
        private float _globalSpawnRateScale = 1.0f;
        private float _globalVelocityScale = 1.0f;
        private float _globalEmissiveScale = 1.0f;
        private float _distanceCullMultiplier = 1.0f;
        private float _fixedSimulationDeltaSeconds;

        public bool Enabled { get; set; } = true;
        public ParticleSimulationMode SimulationMode { get; set; } = ParticleSimulationMode.Cpu;
        public ParticleDebugView DebugView { get; set; } = ParticleDebugView.None;

        /// <summary>
        /// Optional fixed simulation timestep used by deterministic captures and benchmarks.
        /// A value of zero keeps the normal wall-clock timestep.
        /// </summary>
        public float FixedSimulationDeltaSeconds
        {
            get => _fixedSimulationDeltaSeconds;
            set => _fixedSimulationDeltaSeconds = float.IsFinite(value)
                ? Clamp(value, 0.0f, 1.0f / 15.0f)
                : 0.0f;
        }

        public int MaxParticles
        {
            get => _maxParticles;
            set => _maxParticles = Clamp(value, 0, 1_000_000);
        }

        public int MaxEmitters
        {
            get => _maxEmitters;
            set => _maxEmitters = Clamp(value, 0, 65535);
        }

        public int MaxBatches
        {
            get => _maxBatches;
            set => _maxBatches = Clamp(value, 0, 65535);
        }

        public int MaxTrails
        {
            get => _maxTrails;
            set => _maxTrails = Clamp(value, 0, 65535);
        }

        public int MaxTrailSegments
        {
            get => _maxTrailSegments;
            set => _maxTrailSegments = Clamp(value, 0, 1_000_000);
        }

        public bool SoftParticlesEnabled { get; set; } = true;

        public float SoftParticleDistance
        {
            get => _softParticleDistance;
            set => _softParticleDistance = Clamp(value, 0.0f, 10.0f);
        }

        public bool DepthTestEnabled { get; set; } = true;
        public bool ReceiveFog { get; set; } = true;
        public bool UsePremultipliedAlphaByDefault { get; set; } = true;

        public float GlobalSpawnRateScale
        {
            get => _globalSpawnRateScale;
            set => _globalSpawnRateScale = Clamp(value, 0.0f, 10.0f);
        }

        public float GlobalVelocityScale
        {
            get => _globalVelocityScale;
            set => _globalVelocityScale = Clamp(value, 0.0f, 10.0f);
        }

        public float GlobalEmissiveScale
        {
            get => _globalEmissiveScale;
            set => _globalEmissiveScale = Clamp(value, 0.0f, 64.0f);
        }

        public float DistanceCullMultiplier
        {
            get => _distanceCullMultiplier;
            set => _distanceCullMultiplier = Clamp(value, 0.0f, 100.0f);
        }

        public ulong MaxUploadBytesPerFrame { get; set; } = 8 * 1024 * 1024;

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }
}
