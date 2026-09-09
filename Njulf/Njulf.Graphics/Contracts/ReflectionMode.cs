using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    public enum ReflectionMode : uint
    {
        Disabled = 0,
        GlobalEnvironmentOnly = 1,
        StaticProbes = 2,
        StaticProbesAndSsr = 3,
        StaticProbesAndPlanar = 4,
        /// <summary>
        /// Screen-space reflections first, bounded ray-query recovery second,
        /// directional DDGI as the stable off-screen base, then local probes
        /// and the global environment as compatibility fallbacks.
        /// </summary>
        HybridRayQuery = 5
    }
