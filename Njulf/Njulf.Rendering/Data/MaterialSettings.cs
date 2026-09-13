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
    public enum TransparencyMode : uint
    {
        SortedAlphaBlend = 0,
        WeightedBlendedOit = 1
    }

    public enum TransparencyDebugView : uint
    {
        None = 0,
        AlphaMode = 1,
        AlphaValue = 2,
        AlphaCutoff = 3,
        TransparentSortOrder = 4,
        Overdraw = 5,
        WeightedOitAccumulation = 6,
        WeightedOitRevealage = 7
    }

    public enum ThickTransmissionMode : uint
    {
        Off = 0,
        Approximation = 1,
        RayQuery = 2
    }

    public enum DispersionMode : uint
    {
        Off = 0,
        RgbTriplet = 1
    }

    public enum DecalDebugView : uint
    {
        None = 0,
        GeometryDecalMask = 1,
        DecalLayer = 2,
        DecalDepthBias = 3,
        ProjectedDecalVolume = 4,
        ProjectedDecalAtlas = 5
    }

    public enum MaterialDebugView : uint
    {
        None = 0,
        FeatureFlags = 32,
        BaseColor = 33,
        Metallic = 34,
        Roughness = 35,
        NormalStrength = 36,
        WorldNormal = 37,
        EmissiveIntensity = 38,
        ClearcoatFactor = 39,
        ClearcoatRoughness = 40,
        SheenColor = 41,
        SheenRoughness = 42,
        AnisotropyStrength = 43,
        AnisotropyDirection = 44,
        Transmission = 45,
        Ior = 46,
        VolumeThickness = 47,
        AttenuationColor = 48,
        SubsurfaceStrength = 49,
        SpecularFactor = 50,
        SpecularColor = 51,
        IridescenceFactor = 52,
        IridescenceThickness = 53,
        Dispersion = 54,
        MaterialOcclusion = 55,
        CanonicalDiffuseReflectance = 56,
        CompiledEmission = 57,
        GeometricNormal = 58,
        Opacity = 59,
        Sidedness = 60,
        ShadingModel = 61,
        TransportProfile = 62,
        MaterialRevisions = 63,
        /// <summary>
        /// Capture-only linear direct-diffuse signal. This value intentionally
        /// sits outside the interactive material-inspection range.
        /// </summary>
        CaptureLinearDirectDiffuse = 71,
        /// <summary>
        /// Capture-only direct specular, evaluated as aggregate direct lighting
        /// minus the exact diffuse term accumulated by the same shader.
        /// </summary>
        CaptureLinearDirectSpecular = 72
    }

    public static class MaterialDebugViewPolicy
    {
        public static bool IsLinearDirectCapture(MaterialDebugView view) =>
            view is MaterialDebugView.CaptureLinearDirectDiffuse or
                MaterialDebugView.CaptureLinearDirectSpecular;
    }

    public enum SpecularAntialiasingMode : uint
    {
        Off = 0,
        GeometricVariance = 1
    }

    public sealed class MaterialSettings
    {
        private SpecularAntialiasingMode _specularAntialiasingMode =
            SpecularAntialiasingMode.GeometricVariance;

        public MaterialDebugView DebugView { get; set; } = MaterialDebugView.None;

        public SpecularAntialiasingMode SpecularAntialiasingMode
        {
            get => _specularAntialiasingMode;
            set => _specularAntialiasingMode = Enum.IsDefined(value)
                ? value
                : SpecularAntialiasingMode.GeometricVariance;
        }
    }

    public sealed class TransparencySettings
    {
        public const int MaximumSceneReflectionRayTaskBudget = 4_194_304;
        public const int MaximumSceneReflectionSsrSampleBudget = 16_777_216;

        private int _maxTransparentMeshlets = 262144;
        private float _alphaDiscardThreshold = 0.001f;
        private int _sceneReflectionRayTaskBudget = 65_536;
        private int _sceneReflectionSsrSampleBudget = 4_194_304;
        private int _thickTransmissionRayTaskBudget = 262_144;
        private int _thickTransmissionMaximumInterfaces =
            BoundedDielectricMediaStack.MaximumInterfaces;
        private int _thickTransmissionMaximumMediaDepth =
            BoundedDielectricMediaStack.MaximumDepth;
        private int _thickTransmissionMaximumCandidatesPerInterface =
            BoundedDielectricMediaStack.MaximumCandidatesPerInterface;
        private float _thickTransmissionMaximumDistance = 100f;
        private ulong _thickTransmissionMemoryBudgetBytes =
            128UL * 1024UL * 1024UL;

        public bool Enabled { get; set; } = true;
        public TransparencyMode Mode { get; set; } = TransparencyMode.SortedAlphaBlend;
        public TransparencyDebugView DebugView { get; set; } = TransparencyDebugView.None;
        public bool ReceiveShadows { get; set; } = true;
        public bool ReceiveGlobalIllumination { get; set; } = true;
        public bool SampleReflections { get; set; } = true;
        public bool SortPerMeshlet { get; set; } = true;
        /// <summary>
        /// Qualification candidate for bounded transparent material runs. The
        /// universal one-list draw remains the default and rollback path.
        /// </summary>
        public bool PipelinePartitioningEnabled { get; set; }
        public ThickTransmissionMode ThickTransmissionMode { get; set; } =
            ThickTransmissionMode.RayQuery;
        public DispersionMode DispersionMode { get; set; } = DispersionMode.Off;

        /// <summary>
        /// Maximum transparent fragments admitted to the scene-reflection
        /// ray-query recovery path in one frame. SSR and analytic fallbacks
        /// remain available when this bounded budget is zero or exhausted.
        /// </summary>
        public int SceneReflectionRayTaskBudget
        {
            get => _sceneReflectionRayTaskBudget;
            set => _sceneReflectionRayTaskBudget = Math.Clamp(
                value,
                0,
                MaximumSceneReflectionRayTaskBudget);
        }

        /// <summary>
        /// Maximum Hi-Z depth samples reserved by transparent scene-reflection
        /// SSR in one frame. Each admitted fragment reserves its complete
        /// worst-case march before issuing its first depth sample.
        /// </summary>
        public int SceneReflectionSsrSampleBudget
        {
            get => _sceneReflectionSsrSampleBudget;
            set => _sceneReflectionSsrSampleBudget = Math.Clamp(
                value,
                0,
                MaximumSceneReflectionSsrSampleBudget);
        }

        public int ThickTransmissionRayTaskBudget
        {
            get => _thickTransmissionRayTaskBudget;
            set => _thickTransmissionRayTaskBudget =
                Math.Clamp(
                    value,
                    0,
                    GPUForwardPushConstants.MaximumThickTransmissionRayTaskBudget);
        }

        public int ThickTransmissionMaximumInterfaces
        {
            get => _thickTransmissionMaximumInterfaces;
            set => _thickTransmissionMaximumInterfaces = Math.Clamp(
                value, 1, BoundedDielectricMediaStack.MaximumInterfaces);
        }

        public int ThickTransmissionMaximumMediaDepth
        {
            get => _thickTransmissionMaximumMediaDepth;
            set => _thickTransmissionMaximumMediaDepth = Math.Clamp(
                value, 1, BoundedDielectricMediaStack.MaximumDepth);
        }

        public int ThickTransmissionMaximumCandidatesPerInterface
        {
            get => _thickTransmissionMaximumCandidatesPerInterface;
            set => _thickTransmissionMaximumCandidatesPerInterface = Math.Clamp(
                value, 1,
                BoundedDielectricMediaStack.MaximumCandidatesPerInterface);
        }

        public float ThickTransmissionMaximumDistance
        {
            get => _thickTransmissionMaximumDistance;
            set => _thickTransmissionMaximumDistance = Clamp(value, 0.1f, 10_000f);
        }

        public ulong ThickTransmissionMemoryBudgetBytes
        {
            get => _thickTransmissionMemoryBudgetBytes;
            set => _thickTransmissionMemoryBudgetBytes = Math.Clamp(
                value, 16UL * 1024UL * 1024UL,
                2UL * 1024UL * 1024UL * 1024UL);
        }

        public int MaxTransparentMeshlets
        {
            get => _maxTransparentMeshlets;
            set => _maxTransparentMeshlets = value < 0 ? 0 : value;
        }

        public float AlphaDiscardThreshold
        {
            get => _alphaDiscardThreshold;
            set => _alphaDiscardThreshold = Clamp(value, 0.0f, 0.05f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }
    }

    public sealed class DecalSettings
    {
        private float _geometryDepthBias = 0.0005f;
        private float _geometrySlopeScaledDepthBias;
        private int _maxProjectedDecals = 256;
        private int _maxProjectedDecalsPerTile = 64;
        private int _maxProjectedDecalsPerPixel = 8;

        public bool GeometryDecalsEnabled { get; set; } = true;
        public bool ProjectedDecalsEnabled { get; set; }
        /// <summary>
        /// Controls shadow sampling for geometry-decal receivers independently
        /// from ordinary transparent materials. Geometry decals share the
        /// transparent draw path, so folding this into TransparencySettings
        /// makes a controlled decal-only capture impossible.
        /// </summary>
        public bool ReceiveShadows { get; set; } = true;
        public bool ReceiveGlobalIllumination { get; set; } = true;
        /// <summary>
        /// Optional material-index isolation used by deterministic performance
        /// captures. A negative value renders every decal material.
        /// </summary>
        public int IsolatedMaterialIndex { get; set; } = -1;
        public DecalDebugView DebugView { get; set; } = DecalDebugView.None;

        public float GeometryDepthBias
        {
            get => _geometryDepthBias;
            set => _geometryDepthBias = Clamp(value, 0.0f, 0.01f);
        }

        public float GeometrySlopeScaledDepthBias
        {
            get => _geometrySlopeScaledDepthBias;
            set => _geometrySlopeScaledDepthBias = Clamp(value, 0.0f, 4.0f);
        }

        public int MaxProjectedDecals
        {
            get => _maxProjectedDecals;
            set => _maxProjectedDecals = Clamp(value, 0, 4096);
        }

        public int MaxProjectedDecalsPerTile
        {
            get => _maxProjectedDecalsPerTile;
            set => _maxProjectedDecalsPerTile = Clamp(value, 0, 256);
        }

        public int MaxProjectedDecalsPerPixel
        {
            get => _maxProjectedDecalsPerPixel;
            set => _maxProjectedDecalsPerPixel = Clamp(value, 0, 32);
        }

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
