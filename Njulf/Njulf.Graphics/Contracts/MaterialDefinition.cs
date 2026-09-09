using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    /// <summary>
    /// Immutable authored source of truth used by runtime and editor material
    /// changes. Derived GPU and GI fields are deliberately absent.
    /// </summary>
    public sealed record MaterialDefinition
    {
        /// <summary>Shared immutable default: white, non-metallic, fully rough, opaque PBR material with no textures.</summary>
        public static MaterialDefinition Default { get; } = new();

        /// <summary>Authored diagnostic name; defaults to DefaultMaterial.</summary>
        public string Name { get; init; } = "DefaultMaterial";
        /// <summary>Linear RGBA base color multiplier; defaults to white with full opacity.</summary>
        public Vector4 BaseColorFactor { get; init; } = Vector4.One;
        /// <summary>Emission color in the selected photometric unit; defaults to zero.</summary>
        public Vector3 EmissiveFactor { get; init; } = Vector3.Zero;
        /// <summary>Emission strength multiplier; defaults to one.</summary>
        public float EmissiveStrength { get; init; } = 1f;
        /// <summary>Emission unit; defaults to scene-linear radiance.</summary>
        public EmissivePhotometricUnit EmissiveUnit { get; init; } =
            EmissivePhotometricUnit.SceneLinearRadiance;
        /// <summary>
        /// Deliberate non-physical multiplier applied after unit conversion. Its
        /// energy effect is exposed separately by diagnostics and the editor.
        /// </summary>
        public float EmissiveArtisticMultiplier { get; init; } = 1f;
        /// <summary>Metallic fraction in [0, 1]; defaults to zero.</summary>
        public float MetallicFactor { get; init; }
        /// <summary>Perceptual roughness in [0, 1]; defaults to one.</summary>
        public float RoughnessFactor { get; init; } = 1f;
        /// <summary>Occlusion texture strength in [0, 1]; defaults to one.</summary>
        public float OcclusionStrength { get; init; } = 1f;
        /// <summary>Normal texture XY multiplier; defaults to one.</summary>
        public float NormalScale { get; init; } = 1f;

        public MaterialTextureBinding BaseColor { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding Normal { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding MetallicRoughness { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding Occlusion { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding Emissive { get; init; } = MaterialTextureBinding.Missing;

        /// <summary>Alpha interpretation; defaults to opaque.</summary>
        public MaterialAlphaMode AlphaMode { get; init; }
        /// <summary>Masked alpha cutoff; defaults to 0.5.</summary>
        public float AlphaCutoff { get; init; } = 0.5f;
        /// <summary>Whether both sides are rendered; defaults to false.</summary>
        public bool DoubleSided { get; init; }
        /// <summary>Whether this material receives shadows; defaults to true.</summary>
        public bool ReceivesShadows { get; init; } = true;
        /// <summary>
        /// Allows rigid planar surfaces using this material to compete for the
        /// automatic planar-capture budget. Explicit authoring is required;
        /// non-water, non-mirror materials are treated as wet-ground surfaces.
        /// </summary>
        public bool AutomaticPlanarReflectionEnabled { get; init; }
        /// <summary>
        /// Optional renderer-specific blend policy. When unset, the compiler
        /// derives the conventional blend mode from <see cref="AlphaMode"/> and
        /// enabled material features.
        /// </summary>
        public MaterialBlendMode? RenderBlendModeOverride { get; init; }
        /// <summary>Shading model; defaults to PBR.</summary>
        public MaterialShadingModel ShadingModel { get; init; } = MaterialShadingModel.Pbr;
        public MaterialFeatureFlags FeatureFlags { get; init; }
        /// <summary>Immutable optional material extensions; defaults to none.</summary>
        public MaterialExtensionDefinition Extensions { get; init; } = MaterialExtensionDefinition.None;

        public GiParticipationOverride DiffuseGiParticipation { get; init; } = GiParticipationOverride.Default;
        public GiParticipationOverride EmissionGiParticipation { get; init; } = GiParticipationOverride.Default;
        public bool IsGeometryDecal { get; init; }
        public int DecalLayer { get; init; }
        public float DecalDepthBias { get; init; }

        public bool ReceivesIndirectDiffuse =>
            DiffuseGiParticipation != GiParticipationOverride.Disabled &&
            ShadingModel != MaterialShadingModel.Unlit &&
            (ShadingModel != MaterialShadingModel.Decal || IsGeometryDecal);

        public bool ReflectsIndirectDiffuse =>
            DiffuseGiParticipation == GiParticipationOverride.Enabled ||
            DiffuseGiParticipation == GiParticipationOverride.Default &&
            (ShadingModel is MaterialShadingModel.Pbr or
                 MaterialShadingModel.Foliage or
                 MaterialShadingModel.SubsurfaceApproximation or
                 MaterialShadingModel.ThinGlass ||
             ShadingModel == MaterialShadingModel.Decal && IsGeometryDecal);

        public bool EmitsIntoGi =>
            EmissionGiParticipation == GiParticipationOverride.Enabled ||
            EmissionGiParticipation == GiParticipationOverride.Default &&
            ShadingModel != MaterialShadingModel.Unlit;
    }
