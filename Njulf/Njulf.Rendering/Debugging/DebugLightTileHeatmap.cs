namespace Njulf.Rendering.Debug
{
    public enum DebugLightTileHeatClass
    {
        Empty = 0,
        Low = 1,
        NearCapacity = 2,
        Saturated = 3,
        Overflow = 4
    }

    /// <summary>CPU mirror of the light-tile shader's stable threshold contract.</summary>
    public static class DebugLightTileHeatmap
    {
        public static DebugLightTileHeatClass Classify(
            int lightCount,
            bool overflow,
            int maxLightsPerTile)
        {
            if (overflow)
                return DebugLightTileHeatClass.Overflow;
            if (lightCount <= 0)
                return DebugLightTileHeatClass.Empty;
            int capacity = Math.Max(1, maxLightsPerTile);
            if (lightCount >= capacity)
                return DebugLightTileHeatClass.Saturated;
            return (long)lightCount * 4L >= (long)capacity * 3L
                ? DebugLightTileHeatClass.NearCapacity
                : DebugLightTileHeatClass.Low;
        }
    }
}
