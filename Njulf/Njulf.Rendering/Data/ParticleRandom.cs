namespace Njulf.Rendering.Data
{
    public struct ParticleRandom
    {
        private uint _state;

        public ParticleRandom(uint seed)
        {
            _state = seed == 0 ? 0x9E3779B9u : seed;
        }

        public uint NextUInt()
        {
            uint x = _state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            _state = x == 0 ? 0x9E3779B9u : x;
            return _state;
        }

        public float NextFloat()
        {
            return (NextUInt() >> 8) * (1.0f / 16777216.0f);
        }

        public float NextFloat(float min, float max)
        {
            return min + (max - min) * NextFloat();
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;

            uint range = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % range);
        }
    }
}
