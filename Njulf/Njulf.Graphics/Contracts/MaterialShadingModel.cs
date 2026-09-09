using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

    /// <summary>
    /// Renderer-owned material classification. Values are part of the CPU/GPU
    /// transport contract and must remain stable when serialized or uploaded.
    /// </summary>
    public enum MaterialShadingModel : uint
    {
        Pbr = 0,
        Unlit = 1,
        Foliage = 2,
        Decal = 3,
        SubsurfaceApproximation = 4,
        /// <summary>
        /// A zero-thickness dielectric sheet. Unlike cloth using the GI-only
        /// ThinSurface policy, this model opts into visible Fresnel reflection and
        /// tinted raster transmission in the transparent forward path.
        /// </summary>
        ThinGlass = 5
    }
