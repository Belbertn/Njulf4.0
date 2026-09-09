using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    public readonly record struct TextureSamplerDescription(
        TextureWrapMode WrapU,
        TextureWrapMode WrapV,
        TextureFilterMode MinFilter,
        TextureFilterMode MagFilter,
        TextureMipFilterMode MipFilter,
        float MaxAnisotropy)
    {
        public static TextureSamplerDescription Default { get; } = new(
            TextureWrapMode.Repeat,
            TextureWrapMode.Repeat,
            TextureFilterMode.Linear,
            TextureFilterMode.Linear,
            TextureMipFilterMode.Linear,
            16f);
    }
