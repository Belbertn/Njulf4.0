using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Supplies a typed texture; sampler and UV settings come from the material definition.</summary>
public readonly record struct MaterialTextureAssignment(MaterialTextureSlot Slot, ITexture Texture);
