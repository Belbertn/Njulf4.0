using System;

namespace Njulf.Rendering
{
    public static class RenderingConstants
    {
        public const int FramesInFlight = 2;
        public const int ForwardClusterTileSize = 16;
        public const int ForwardClusterDepthSliceCount = 24;
        public const int ForwardClusterMaxLights = 64;
        public const float ForwardClusterNearPlane = 0.1f;
        public const float ForwardClusterFarPlane = 1000.0f;

        static RenderingConstants()
        {
            if (FramesInFlight < 1 || (FramesInFlight & (FramesInFlight - 1)) != 0)
                throw new InvalidOperationException($"{nameof(FramesInFlight)} must be a positive power of 2. Current value: {FramesInFlight}");
        }

        public static int NextFrameIndex(int currentFrame) => (currentFrame + 1) % FramesInFlight;

        public static uint CalculateForwardClusterCount(
            uint tileCountX,
            uint tileCountY) =>
            checked(
                tileCountX * tileCountY *
                ForwardClusterDepthSliceCount);

        public static uint CalculateForwardClusterDepthSlice(float viewDepth)
        {
            float depth = Math.Clamp(
                viewDepth,
                ForwardClusterNearPlane,
                ForwardClusterFarPlane);
            float normalized = MathF.Log(
                depth / ForwardClusterNearPlane) /
                MathF.Log(
                    ForwardClusterFarPlane /
                    ForwardClusterNearPlane);
            return (uint)Math.Clamp(
                (int)MathF.Floor(
                    normalized * ForwardClusterDepthSliceCount),
                0,
                ForwardClusterDepthSliceCount - 1);
        }

        public static uint CalculateForwardClusterIndex(
            uint tileX,
            uint tileY,
            uint depthSlice,
            uint tileCountX,
            uint tileCountY)
        {
            if (tileCountX == 0 || tileCountY == 0 ||
                tileX >= tileCountX || tileY >= tileCountY ||
                depthSlice >= ForwardClusterDepthSliceCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depthSlice),
                    "Forward cluster coordinates are outside the configured grid.");
            }

            return checked(
                (depthSlice * tileCountY + tileY) * tileCountX +
                tileX);
        }

        public static void ValidateFrameIndex(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= FramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"Frame index must be between 0 and {FramesInFlight - 1}. Current value: {frameIndex}");
        }
    }
}
