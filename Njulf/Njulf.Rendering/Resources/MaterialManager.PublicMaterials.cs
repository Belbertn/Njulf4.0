using System.Runtime.ExceptionServices;
using Njulf.Assets.Cooked;
using Njulf.Core.Scene;
using Njulf.Graphics;
using Njulf.Rendering.Data;

namespace Njulf.Rendering.Resources;

public sealed unsafe partial class MaterialManager
{
    internal void UpdatePublicMaterial(MaterialHandle handle, RenderObject? target,
        MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> assignments)
    {
        GraphicsDevice?.EnsureUsable();
        ArgumentNullException.ThrowIfNull(definition);
        MaterialDefinition before = GetMaterialDefinition(handle);
        definition = MaterialDefinitionValidator.ValidateAndNormalize(definition);
        uint assigned = 0;
        foreach (var assignment in assignments)
        {
            _ = VulkanGraphicsDevice.GetBinding(definition, assignment.Slot); // Validate the slot, even when clearing it.
            uint bit = 1u << (int)assignment.Slot;
            if ((assigned & bit) != 0)
                throw new ArgumentException("A texture slot was assigned twice.", nameof(assignments));
            assigned |= bit;
            if (assignment.Texture != null)
                RequireGraphicsDevice().ValidateTexture(assignment.Texture);
        }
        for (int i = 0; i < 18; i++)
        {
            var slot = (MaterialTextureSlot)i;
            if ((assigned & (1u << i)) == 0 &&
                VulkanGraphicsDevice.GetBinding(definition, slot).Texture != VulkanGraphicsDevice.GetBinding(before, slot).Texture)
                throw new ArgumentException("Use typed assignments to change or clear material textures.", nameof(definition));
        }

        var acquired = new List<TextureHandle>(18);
        Exception? failure = null;
        try
        {
            foreach (var assignment in assignments)
            {
                var binding = VulkanGraphicsDevice.GetBinding(definition, assignment.Slot);
                TextureHandle texture = TextureHandle.Invalid;
                if (assignment.Texture != null)
                {
                    var graphics = RequireGraphicsDevice();
                    texture = graphics.TextureResources.RetainGraphicsBinding(
                        graphics.ValidateTexture(assignment.Texture), binding.Sampler);
                    acquired.Add(texture);
                }
                definition = VulkanGraphicsDevice.SetBinding(definition, assignment.Slot, binding with { Texture = texture });
            }
            // A sampler change needs a descriptor alias even when the image itself was omitted.
            for (int i = 0; i < 18; i++)
            {
                var slot = (MaterialTextureSlot)i;
                var binding = VulkanGraphicsDevice.GetBinding(definition, slot);
                if ((assigned & (1u << i)) != 0 || !binding.IsBound ||
                    binding.Sampler == VulkanGraphicsDevice.GetBinding(before, slot).Sampler)
                    continue;
                TextureHandle texture = RequireGraphicsDevice().TextureResources.RetainGraphicsBinding(binding.Texture, binding.Sampler);
                acquired.Add(texture);
                definition = VulkanGraphicsDevice.SetBinding(definition, slot, binding with { Texture = texture });
            }
            if (target == null)
                UpdateMaterialDefinition(handle, definition);
            else
            {
                if (target.Material is not VulkanMaterial current || current.Handle != handle ||
                    !ReferenceEquals(current.OwnerIdentity, ResourceOwner))
                    throw new InvalidOperationException("Object material changed before the edit could be applied.");
                UpdateRenderObjectMaterialDefinition(target, definition);
            }
        }
        catch (Exception error) { failure = error; }

        // The manager acquires its own references before publication. These are only preparation references.
        List<Exception>? cleanupFailures = null;
        for (int i = acquired.Count - 1; i >= 0; i--)
        {
            try { RequireGraphicsDevice().ReleaseTexture(acquired[i]); }
            catch (Exception error) { (cleanupFailures ??= []).Add(error); }
        }
        if (cleanupFailures != null)
        {
            if (failure != null) cleanupFailures.Insert(0, failure);
            throw new AggregateException("Material edit texture cleanup failed.", cleanupFailures);
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Texture? RetainMaterialTexture(MaterialHandle handle, MaterialTextureSlot slot)
    {
        GraphicsDevice?.EnsureUsable();
        var binding = VulkanGraphicsDevice.GetBinding(GetMaterialDefinition(handle), slot);
        if (!binding.IsBound) return null;
        var graphics = RequireGraphicsDevice();
        graphics.TextureResources.RetainTexture(binding.Texture);
        return AdoptTexture(graphics, binding.Texture);
    }

    /// <summary>Loads an owned texture for a common PBR slot, using the existing file cache and mip policy.</summary>
    /// <remarks>Requires the device thread. Relative paths use the current directory. Dispose the result after
    /// assigning it; the material acquires its own reference. Color/emission use sRGB; other slots use linear data.</remarks>
    public Texture LoadMaterialTexture(string path, MaterialTextureSlot slot)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var graphics = RequireGraphicsDevice();
        graphics.EnsureUsable();
        var (srgb, semantic) = slot switch
        {
            MaterialTextureSlot.BaseColor or MaterialTextureSlot.Emissive => (true, TextureSemantic.Color),
            MaterialTextureSlot.Normal => (false, TextureSemantic.Normal),
            MaterialTextureSlot.MetallicRoughness => (false, TextureSemantic.Data),
            MaterialTextureSlot.Occlusion => (false, TextureSemantic.Scalar),
            _ => throw new ArgumentOutOfRangeException(nameof(slot), "File loading supports the five common PBR slots.")
        };
        TextureHandle handle = graphics.TextureResources.LoadTextureFromFile(
            Path.GetFullPath(path), srgb: srgb, semantic: semantic);
        return AdoptTexture(graphics, handle);
    }

    /// <summary>Returns the normalized file path, empty for an unbound slot, or null for an embedded/generated texture.</summary>
    public string? GetMaterialTexturePath(IMaterial material, MaterialTextureSlot slot)
    {
        ArgumentNullException.ThrowIfNull(material);
        if (material is not VulkanMaterial typed || !ReferenceEquals(typed.OwnerIdentity, ResourceOwner))
            throw new ArgumentException("Material belongs to another graphics device.", nameof(material));
        var binding = VulkanGraphicsDevice.GetBinding(material.Definition, slot);
        return binding.IsBound ? _textureManager?.GetGraphicsTextureSourcePath(binding.Texture) : string.Empty;
    }

    private VulkanGraphicsDevice RequireGraphicsDevice() => GraphicsDevice
        ?? throw new NotSupportedException("Texture operations require an attached graphics device.");

    private static Texture AdoptTexture(VulkanGraphicsDevice graphics, TextureHandle handle)
    {
        try
        {
            var info = graphics.TextureResources.GetTextureInfo(handle);
            return new VulkanTexture(graphics, handle, checked((int)info.Extent.Width), checked((int)info.Extent.Height),
                graphics.TextureResources.GetGraphicsTextureColorSpace(handle));
        }
        catch { graphics.ReleaseTexture(handle); throw; }
    }
}
