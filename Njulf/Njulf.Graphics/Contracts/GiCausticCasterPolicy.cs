using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    /// <summary>
    /// General caustic-caster authoring policy. Default automatically admits
    /// validated closed dielectrics and water while mirrors and rough specular
    /// materials remain explicit opt-ins.
    /// </summary>
    public enum GiCausticCasterPolicy : byte
    {
        Default = 0,
        Disabled = 1,
        Mirror = 2,
        RoughSpecular = 3,
        DielectricPriority = 4
    }
