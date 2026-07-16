namespace Njulf.Rendering.Data
{
    public readonly struct SpotShadowAtlasRect
    {
        public SpotShadowAtlasRect(uint x, uint y, uint width, uint height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public uint X { get; }
        public uint Y { get; }
        public uint Width { get; }
        public uint Height { get; }
    }
}
