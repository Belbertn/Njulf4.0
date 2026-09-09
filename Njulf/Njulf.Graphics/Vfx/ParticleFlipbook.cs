using System;

namespace Njulf.Core.Vfx
{
    public sealed class ParticleFlipbook
    {
        private int _columns = 1;
        private int _rows = 1;
        private int _frameCount = 1;
        private float _framesPerSecond;

        public int Columns
        {
            get => _columns;
            init => _columns = value < 1 ? 1 : value;
        }

        public int Rows
        {
            get => _rows;
            init => _rows = value < 1 ? 1 : value;
        }

        public int FrameCount
        {
            get => System.Math.Min(_frameCount, Columns * Rows);
            init => _frameCount = value < 1 ? 1 : value;
        }

        public float FramesPerSecond
        {
            get => _framesPerSecond;
            init => _framesPerSecond = value < 0.0f ? 0.0f : value;
        }

        public bool Loop { get; init; } = true;
        public bool RandomStartFrame { get; init; } = true;
        public bool InterpolateFrames { get; init; } = true;

        public int GetFrame(float particleAgeSeconds, float lifetimeSeconds, int startFrame)
        {
            int count = FrameCount;
            if (count <= 1)
                return 0;

            int frame = startFrame;
            if (FramesPerSecond > 0.0f)
                frame += (int)MathF.Floor(particleAgeSeconds * FramesPerSecond);
            else if (lifetimeSeconds > 0.0f)
                frame += (int)MathF.Floor((particleAgeSeconds / lifetimeSeconds) * count);

            if (Loop)
                return PositiveModulo(frame, count);

            return System.Math.Clamp(frame, 0, count - 1);
        }

        private static int PositiveModulo(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }
    }
}
