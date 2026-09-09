using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

    /// <summary>
    /// Opt-in authoring contract for the deliberately small first caustic scope.
    /// A material is never inferred to be a caustic caster from metallic or
    /// transmission values alone.
    /// </summary>
    public enum GiCausticParticipationMode : byte
    {
        None = 0,
        MirrorHero = 1,
        ClosedDielectricHero = 2,
        RoughSpecularReference = 3
    }
