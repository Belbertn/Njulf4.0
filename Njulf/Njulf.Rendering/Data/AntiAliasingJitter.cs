using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data
{
    public static class AntiAliasingJitter
    {
        public static Vector2 GetHaltonJitter(int sampleIndex, int sampleCount, uint width, uint height, bool enabled)
        {
            if (!enabled || width == 0 || height == 0)
                return Vector2.Zero;

            int count = sampleCount <= 3 ? 2 : sampleCount <= 6 ? 4 : sampleCount <= 12 ? 8 : 16;
            int index = Math.Abs(sampleIndex) % count + 1;
            float x = Halton(index, 2) - 0.5f;
            float y = Halton(index, 3) - 0.5f;
            return new Vector2(x / width, y / height);
        }

        private static float Halton(int index, int radix)
        {
            float result = 0.0f;
            float fraction = 1.0f / radix;

            while (index > 0)
            {
                result += fraction * (index % radix);
                index /= radix;
                fraction /= radix;
            }

            return result;
        }
    }
}
