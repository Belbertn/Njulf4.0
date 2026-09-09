using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;


    /// <summary>
    /// One independent glTF texture binding. A binding is equal only when its
    /// image handle, sampler, UV set, and complete transform are equal.
    /// </summary>
    public sealed record MaterialTextureBinding
    {
        public static MaterialTextureBinding Missing { get; } = new();

        public TextureHandle Texture { get; init; } = TextureHandle.Invalid;
        public TextureSamplerDescription Sampler { get; init; } = TextureSamplerDescription.Default;
        public int TexCoordSet { get; init; }
        public Vector2 Offset { get; init; } = Vector2.Zero;
        public Vector2 Scale { get; init; } = Vector2.One;
        public float RotationRadians { get; init; }

        public bool IsBound => Texture.IsValid;
    }
