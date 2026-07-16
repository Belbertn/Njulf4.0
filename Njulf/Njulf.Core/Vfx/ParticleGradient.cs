using System;
using Njulf.Core.Math;

namespace Njulf.Core.Vfx
{
    public sealed class ParticleGradient
    {
        private readonly ParticleGradientKey[] _keys;

        private ParticleGradient(ReadOnlySpan<ParticleGradientKey> keys)
        {
            if (keys.Length == 0)
                throw new ArgumentException("Particle gradient must contain at least one key.", nameof(keys));

            _keys = keys.ToArray();
            Array.Sort(_keys, static (a, b) => a.Time.CompareTo(b.Time));
            for (int i = 0; i < _keys.Length; i++)
                _keys[i] = new ParticleGradientKey(Clamp01(_keys[i].Time), _keys[i].Color);
        }

        public static ParticleGradient White { get; } = Constant(Color.White);

        public IReadOnlyList<ParticleGradientKey> Keys => _keys;

        public static ParticleGradient Constant(Color color)
        {
            return new ParticleGradient(stackalloc[] { new ParticleGradientKey(0.0f, color) });
        }

        public static ParticleGradient Linear(Color start, Color end)
        {
            return new ParticleGradient(stackalloc[]
            {
                new ParticleGradientKey(0.0f, start),
                new ParticleGradientKey(1.0f, end)
            });
        }

        public static ParticleGradient FromKeys(params ParticleGradientKey[] keys)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            return new ParticleGradient(keys);
        }

        public Color Sample(float t)
        {
            t = Clamp01(t);
            if (_keys.Length == 1)
                return _keys[0].Color;

            ParticleGradientKey previous = _keys[0];
            for (int i = 1; i < _keys.Length; i++)
            {
                ParticleGradientKey next = _keys[i];
                if (t <= next.Time)
                {
                    float span = next.Time - previous.Time;
                    float localT = span <= 0.000001f ? 0.0f : (t - previous.Time) / span;
                    return Color.Lerp(previous.Color, next.Color, localT);
                }

                previous = next;
            }

            return _keys[^1].Color;
        }

        private static float Clamp01(float value)
        {
            if (value < 0.0f)
                return 0.0f;
            return value > 1.0f ? 1.0f : value;
        }
    }

    public readonly record struct ParticleGradientKey(float Time, Color Color);
}
