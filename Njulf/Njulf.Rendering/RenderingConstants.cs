using System;

namespace Njulf.Rendering
{
    public static class RenderingConstants
    {
        public const int FramesInFlight = 2;

        static RenderingConstants()
        {
            if (FramesInFlight < 1 || (FramesInFlight & (FramesInFlight - 1)) != 0)
                throw new InvalidOperationException($"{nameof(FramesInFlight)} must be a positive power of 2. Current value: {FramesInFlight}");
        }

        public static int NextFrameIndex(int currentFrame) => (currentFrame + 1) % FramesInFlight;

        public static void ValidateFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"Frame index must be between 0 and {FramesInFlight - 1}. Current value: {frameIndex}");
        }
    }
}
