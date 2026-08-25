using System;
using Njulf.Assets;
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

            var linearClamp = new TextureSamplerDescription(
                TextureWrapMode.ClampToEdge,
                TextureWrapMode.ClampToEdge,
                TextureFilterMode.Linear,
                TextureFilterMode.Linear,
                TextureMipFilterMode.Nearest,
                1.0f);
            var pointClamp = linearClamp with
            {
                MinFilter = TextureFilterMode.Nearest,
                MagFilter = TextureFilterMode.Nearest
            };

            try
            {
                _areaTexture = _textureManager.CreateTexture(
                    SmaaLookupData.AreaWidth,
                    SmaaLookupData.AreaHeight,
                    Format.R8G8Unorm,
                    bindlessIndex: BindlessIndex.SmaaAreaTexture,
                    bindlessHeap: bindlessHeap,
                    samplerDescription: linearClamp,
                    debugName: "SMAA Canonical Area Texture");
                _textureManager.UploadTextureData(
                    _areaTexture,
                    SmaaLookupData.DecodeArea(),
                    SmaaLookupData.AreaWidth,
                    SmaaLookupData.AreaHeight,
                    Format.R8G8Unorm);

                _searchTexture = _textureManager.CreateTexture(
                    SmaaLookupData.SearchWidth,
                    SmaaLookupData.SearchHeight,
                    Format.R8Unorm,
                    bindlessIndex: BindlessIndex.SmaaSearchTexture,
                    bindlessHeap: bindlessHeap,
                    samplerDescription: pointClamp,
                    debugName: "SMAA Canonical Search Texture");
                _textureManager.UploadTextureData(
                    _searchTexture,
                    SmaaLookupData.DecodeSearch(),
                    SmaaLookupData.SearchWidth,
                    SmaaLookupData.SearchHeight,
                    Format.R8Unorm);
            }
            catch
            {
                if (_areaTexture.IsValid)
                    _textureManager.DestroyTexture(_areaTexture);
                if (_searchTexture.IsValid)
                    _textureManager.DestroyTexture(_searchTexture);
                _areaTexture = TextureHandle.Invalid;
                _searchTexture = TextureHandle.Invalid;
                throw;
            }
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
