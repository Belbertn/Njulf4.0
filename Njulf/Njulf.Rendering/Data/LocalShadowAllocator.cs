using System;

namespace Njulf.Rendering.Data
{
    public static class LocalShadowAllocator
    {
        public static int CalculateSpotAtlasCapacity(uint atlasSize, uint tileSize)
        {
            ValidateSpotAtlas(atlasSize, tileSize);
            uint tilesPerSide = atlasSize / tileSize;
            return checked((int)(tilesPerSide * tilesPerSide));
        }

        public static SpotShadowAtlasRect GetSpotTileRect(uint atlasSize, uint tileSize, int tileIndex)
        {
            int capacity = CalculateSpotAtlasCapacity(atlasSize, tileSize);
            if (tileIndex < 0 || tileIndex >= capacity)
                throw new ArgumentOutOfRangeException(nameof(tileIndex));

            uint tilesPerSide = atlasSize / tileSize;
            uint x = (uint)tileIndex % tilesPerSide;
            uint y = (uint)tileIndex / tilesPerSide;
            return new SpotShadowAtlasRect(x * tileSize, y * tileSize, tileSize, tileSize);
        }

        public static void ValidateSpotAtlas(uint atlasSize, uint tileSize)
        {
            if (!IsPowerOfTwo(atlasSize) || !IsPowerOfTwo(tileSize))
                throw new ArgumentException("Spot shadow atlas and tile sizes must be powers of two.");
            if (atlasSize < 1024 || atlasSize > 8192)
                throw new ArgumentOutOfRangeException(nameof(atlasSize));
            if (tileSize < 128 || tileSize > 2048 || tileSize > atlasSize)
                throw new ArgumentOutOfRangeException(nameof(tileSize));
        }

        private static bool IsPowerOfTwo(uint value) => value != 0 && (value & (value - 1)) == 0;
    }
}
