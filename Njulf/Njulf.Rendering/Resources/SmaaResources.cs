using System;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources
{
    public sealed class SmaaResources : IDisposable
    {
        private readonly TextureManager _textureManager;
        private TextureHandle _areaTexture = TextureHandle.Invalid;
        private TextureHandle _searchTexture = TextureHandle.Invalid;
        private bool _disposed;

        public SmaaResources(TextureManager textureManager, BindlessHeap bindlessHeap)
        {
            _textureManager = textureManager ?? throw new ArgumentNullException(nameof(textureManager));
            if (bindlessHeap == null)
                throw new ArgumentNullException(nameof(bindlessHeap));

            _areaTexture = _textureManager.CreateTexture(
                1,
                1,
                Format.R8G8B8A8Unorm,
                bindlessIndex: BindlessIndex.SmaaAreaTexture,
                bindlessHeap: bindlessHeap,
                debugName: "SMAA Area Texture");
            _textureManager.UploadTextureData(_areaTexture, stackalloc byte[] { 255, 255, 255, 255 }, 1, 1, Format.R8G8B8A8Unorm);

            _searchTexture = _textureManager.CreateTexture(
                1,
                1,
                Format.R8G8B8A8Unorm,
                bindlessIndex: BindlessIndex.SmaaSearchTexture,
                bindlessHeap: bindlessHeap,
                debugName: "SMAA Search Texture");
            _textureManager.UploadTextureData(_searchTexture, stackalloc byte[] { 255, 255, 255, 255 }, 1, 1, Format.R8G8B8A8Unorm);
        }

        public bool IsReady => _areaTexture.IsValid && _searchTexture.IsValid;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _textureManager.DestroyTexture(_areaTexture);
            _textureManager.DestroyTexture(_searchTexture);
            _areaTexture = TextureHandle.Invalid;
            _searchTexture = TextureHandle.Invalid;
            GC.SuppressFinalize(this);
        }
    }
}
