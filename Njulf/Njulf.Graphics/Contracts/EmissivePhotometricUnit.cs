using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

    /// <summary>
    /// Authoring convention for emissive strength. Values are converted to the
    /// renderer's exposure-independent, scene-linear radiance before they reach
    /// raster lighting, DDGI, or emissive-source selection.
    /// </summary>
    public enum EmissivePhotometricUnit
    {
        /// <summary>
        /// Backwards-compatible glTF convention: strength directly multiplies the
        /// emissive factor and texture in scene-linear radiance units.
        /// </summary>
        SceneLinearRadiance = 0,

        /// <summary>
        /// Strength is the luminance, in cd/m² (nits), produced by the authored
        /// emissive factor when the emissive texture is white.
        /// </summary>
        LuminanceNits = 1
    }
