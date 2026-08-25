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
            Vector2 sequenceMean = CalculateSequenceMean(count);
            float x = Halton(index, 2) - sequenceMean.X;
            float y = Halton(index, 3) - sequenceMean.Y;
            return new Vector2((x * 2.0f) / width, (y * 2.0f) / height);
        }

        private static Vector2 CalculateSequenceMean(int sampleCount)
        {
            float x = 0.0f;
            float y = 0.0f;
            for (int index = 1; index <= sampleCount; index++)
            {
                x += Halton(index, 2);
                y += Halton(index, 3);
            }

            float inverseCount = 1.0f / sampleCount;
            return new Vector2(x * inverseCount, y * inverseCount);
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
