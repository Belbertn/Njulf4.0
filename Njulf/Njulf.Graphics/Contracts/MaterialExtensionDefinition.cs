using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    /// <summary>
    /// Authored extension values. Texture bindings stay independent even when they
    /// happen to reference the same physical image.
    /// </summary>
    public sealed record MaterialExtensionDefinition
    {
        public static MaterialExtensionDefinition None { get; } = new();

        public float ClearcoatFactor { get; init; }
        public float ClearcoatRoughness { get; init; }
        public float ClearcoatNormalScale { get; init; } = 1f;
        public MaterialTextureBinding Clearcoat { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding ClearcoatRoughnessTexture { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding ClearcoatNormal { get; init; } = MaterialTextureBinding.Missing;

        public Vector3 SheenColorFactor { get; init; } = Vector3.Zero;
        public float SheenRoughness { get; init; }
        public MaterialTextureBinding SheenColor { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding SheenRoughnessTexture { get; init; } = MaterialTextureBinding.Missing;

        public float AnisotropyStrength { get; init; }
        public float AnisotropyRotation { get; init; }
        public MaterialTextureBinding Anisotropy { get; init; } = MaterialTextureBinding.Missing;

        public float TransmissionFactor { get; init; }
        public float Ior { get; init; } = 1.5f;
        public float ThicknessFactor { get; init; }
        public float AttenuationDistance { get; init; } = float.PositiveInfinity;
        public Vector3 AttenuationColor { get; init; } = Vector3.One;
        public MaterialTextureBinding Transmission { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding Thickness { get; init; } = MaterialTextureBinding.Missing;
        public GiTransmissionPolicy TransmissionPolicy { get; init; } = GiTransmissionPolicy.None;
        public OpticalBoundaryKind OpticalBoundary { get; init; } =
            OpticalBoundaryKind.ClosedVolume;
        public GiCausticCasterPolicy CausticCasterPolicy { get; init; } =
            GiCausticCasterPolicy.Default;
        /// <summary>
        /// Explicit C4 authoring intent. This value is ignored by all canonical
        /// DDGI transport paths; only the separate tagged-caustic system consumes
        /// it after topology/current-pose admission.
        /// </summary>
        public GiCausticParticipationMode CausticParticipation { get; init; } =
            GiCausticParticipationMode.None;
        /// <summary>
        /// Renderer-authored diffuse tint for a zero-thickness transmission lobe.
        /// This is independent of raster alpha and volume attenuation.
        /// </summary>
        public Vector3 ThinTransmissionTint { get; init; } = Vector3.One;

        /// <summary>Primary and secondary world-space UV flow velocities.</summary>
        public Vector2 WaterNormalVelocity0 { get; init; } = new(0.035f, 0.012f);
        public Vector2 WaterNormalVelocity1 { get; init; } = new(-0.018f, 0.027f);
        /// <summary>UV frequencies used when the normal texture is sampled twice.</summary>
        public float WaterNormalUvScale0 { get; init; } = 1f;
        public float WaterNormalUvScale1 { get; init; } = 1.73f;

        public float SpecularFactor { get; init; } = 1f;
        public Vector3 SpecularColorFactor { get; init; } = Vector3.One;
        public MaterialTextureBinding Specular { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding SpecularColor { get; init; } = MaterialTextureBinding.Missing;

        public float IridescenceFactor { get; init; }
        public float IridescenceIor { get; init; } = 1.3f;
        public float IridescenceThicknessMinimum { get; init; } = 100f;
        public float IridescenceThicknessMaximum { get; init; } = 400f;
        public MaterialTextureBinding Iridescence { get; init; } = MaterialTextureBinding.Missing;
        public MaterialTextureBinding IridescenceThickness { get; init; } = MaterialTextureBinding.Missing;

        public float Dispersion { get; init; }
        public Vector3 SubsurfaceColor { get; init; } = Vector3.One;
        public float SubsurfaceStrength { get; init; }
        public MaterialTextureBinding Subsurface { get; init; } = MaterialTextureBinding.Missing;
    }
