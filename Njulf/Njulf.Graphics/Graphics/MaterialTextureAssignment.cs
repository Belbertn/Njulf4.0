using System;
using Njulf.Core.Math;

namespace Njulf.Graphics;

/// <summary>Supplies a typed texture; sampler and UV settings come from the material definition.
/// Null clears a slot during an update; material creation requires a non-null texture.</summary>
public readonly record struct MaterialTextureAssignment(MaterialTextureSlot Slot, ITexture? Texture);
