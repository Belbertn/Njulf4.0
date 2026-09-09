using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    public enum GiTransmissionPolicy : byte
    {
        None = 0,
        RemoveFromOpaqueDiffuse = 1,
        ThinSurface = 2,
        Volume = 3,
        Unsupported = 255
    }
