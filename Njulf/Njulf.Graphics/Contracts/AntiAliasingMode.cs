using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    public enum AntiAliasingMode : uint
    {
        None = 0,
        Fxaa = 1,
        SmaaLow = 2,
        SmaaMedium = 3,
        SmaaHigh = 4,
        Taa = 5
    }
