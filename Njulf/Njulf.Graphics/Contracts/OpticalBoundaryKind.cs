using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    /// <summary>
    /// Declares how a physical transmission boundary is interpreted. Closed
    /// volumes must be watertight and alternate entry/exit intersections; water
    /// surfaces are open interfaces whose exterior is air and whose interior is
    /// the authored material medium.
    /// </summary>
    public enum OpticalBoundaryKind : byte
    {
        ClosedVolume = 0,
        WaterSurface = 1
    }
