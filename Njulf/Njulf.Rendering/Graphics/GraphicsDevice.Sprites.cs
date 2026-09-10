using Njulf.Core.Math;
using Njulf.Rendering;
using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    private VulkanRenderer _spriteRenderer = null!;
    internal void AttachSprites(VulkanRenderer renderer) => _spriteRenderer = renderer;
    public override SpriteBatch CreateSpriteBatch() { EnsureUsable(); return new(new SpriteSink(this)); }
    internal override Texture RetainSpriteAtlas(Texture atlas)
    {
        TextureHandle handle = ValidateTexture(atlas);
        Textures.RetainTexture(handle);
        return new VulkanTexture(this, handle, atlas.Width, atlas.Height, atlas.ColorSpace);
    }
    private sealed class SpriteSink(VulkanGraphicsDevice device) : ISpriteBatchSink
    {
        private ulong _frame;
        public Vector2 Begin()
        {
            device.EnsureUsable(); device._lifetime.EnsureFrameInProgress("SpriteBatch");
            _frame = device._spriteRenderer.SpriteFrameSerial;
            return device._spriteRenderer.SpriteDisplaySize;
        }
        public void ValidateFrame()
        {
            device.EnsureUsable(); device._lifetime.EnsureFrameInProgress("SpriteBatch");
            if (_frame != device._spriteRenderer.SpriteFrameSerial) throw new InvalidOperationException("End the sprite batch in the frame where Begin was called.");
        }
        public SpriteTextureLease Retain(Texture texture, SpriteSampler sampler)
        {
            TextureHandle original = device.ValidateTexture(texture);
            var filter = sampler == SpriteSampler.PointClamp ? TextureFilterMode.Nearest : TextureFilterMode.Linear;
            var description = new TextureSamplerDescription(TextureWrapMode.ClampToEdge, TextureWrapMode.ClampToEdge,
                filter, filter, TextureMipFilterMode.Nearest, 1);
            TextureHandle retained = device.Textures.RetainGraphicsBinding(original, description);
            return new(device.Textures.GetBindlessTextureIndex(retained), () => device.ReleaseTexture(retained));
        }
        public void Submit(SpriteDrawData data) { ValidateFrame(); device._spriteRenderer.QueueSpriteDrawData(data); }
    }
}
