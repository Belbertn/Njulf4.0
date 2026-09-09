using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Color interpretation shared by runtime resources and content tools. Numeric values are serialized.</summary>
public enum TextureColorSpace
{
    /// <summary>Linear channel values, including non-color data.</summary>
    Linear = 0,
    /// <summary>sRGB-encoded color channels; alpha remains linear.</summary>
    Srgb = 1,
    /// <summary>Linear high-dynamic-range color values.</summary>
    HdrLinear = 2
}
