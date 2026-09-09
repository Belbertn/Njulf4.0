using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

    public enum MaterialBlendMode : uint
    {
        Opaque = 0,
        Mask = 1,
        AlphaBlend = 2,
        PremultipliedAlpha = 3,
        Additive = 4,
        Multiply = 5
    }
