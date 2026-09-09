using System;

namespace Njulf.Core.Vfx
{
    public sealed class ParticleCurve
    {
        private readonly ParticleCurveKey[] _keys;

        private ParticleCurve(ReadOnlySpan<ParticleCurveKey> keys)
        {
            if (keys.Length == 0)
                throw new ArgumentException("Particle curve must contain at least one key.", nameof(keys));

            _keys = keys.ToArray();
            Array.Sort(_keys, static (a, b) => a.Time.CompareTo(b.Time));
            for (int i = 0; i < _keys.Length; i++)
                _keys[i] = new ParticleCurveKey(Clamp01(_keys[i].Time), _keys[i].Value);
        }

        public IReadOnlyList<ParticleCurveKey> Keys => _keys;

        public static ParticleCurve Constant(float value)
        {
            return new ParticleCurve(stackalloc[] { new ParticleCurveKey(0.0f, value) });
        }

        public static ParticleCurve Linear(float start, float end)
        {
            return new ParticleCurve(stackalloc[]
            {
                new ParticleCurveKey(0.0f, start),
                new ParticleCurveKey(1.0f, end)
            });
        }

        public static ParticleCurve FromKeys(params ParticleCurveKey[] keys)
        {
            if (keys == null)
                throw new ArgumentNullException(nameof(keys));

            return new ParticleCurve(keys);
        }

        public float Sample(float t)
        {
            t = Clamp01(t);
            if (_keys.Length == 1)
                return _keys[0].Value;

            ParticleCurveKey previous = _keys[0];
            for (int i = 1; i < _keys.Length; i++)
            {
                ParticleCurveKey next = _keys[i];
                if (t <= next.Time)
                {
                    float span = next.Time - previous.Time;
                    float localT = span <= 0.000001f ? 0.0f : (t - previous.Time) / span;
                    return previous.Value + (next.Value - previous.Value) * localT;
                }

                previous = next;
            }

            return _keys[^1].Value;
        }

        private static float Clamp01(float value)
        {
            if (value < 0.0f)
                return 0.0f;
            return value > 1.0f ? 1.0f : value;
        }
    }

    public readonly record struct ParticleCurveKey(float Time, float Value);
}
