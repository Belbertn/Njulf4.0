using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;

namespace Njulf.Graphics;

internal sealed partial class VulkanGraphicsDevice
{
    private TextureManager Textures => _materials.TextureManager
        ?? throw new NotSupportedException("This device has no texture manager.");

    /// <summary>Creates a one-mip RGBA8 texture. Input bytes are consumed synchronously.</summary>
    public override VulkanTexture CreateTexture2D(int width, int height, ReadOnlySpan<byte> rgba8, TextureColorSpace colorSpace)
    {
        EnsureUsable();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (colorSpace is not (TextureColorSpace.Linear or TextureColorSpace.Srgb))
            throw new ArgumentOutOfRangeException(nameof(colorSpace));
        if (rgba8.Length != checked(width * height * 4))
            throw new ArgumentException("RGBA8 data must contain exactly width * height * 4 bytes.", nameof(rgba8));
        TextureHandle handle = Textures.CreateGraphicsTexture(width, height, rgba8, colorSpace == TextureColorSpace.Srgb);
        try { return new VulkanTexture(this, handle, width, height, colorSpace); }
        catch { ReleaseTexture(handle); throw; }
    }

    internal void ReleaseTexture(TextureHandle handle) => DeferRelease(() => Textures.ReleaseTexture(handle));

    /// <summary>Creates a material retaining each assigned texture binding. Callers keep their texture references.</summary>
    public override VulkanMaterial CreateMaterial(MaterialDefinition definition, ReadOnlySpan<MaterialTextureAssignment> textures)
    {
        EnsureUsable();
        ArgumentNullException.ThrowIfNull(definition);
        for (int i = 0; i < 18; i++)
            if (GetBinding(definition, (MaterialTextureSlot)i).IsBound)
                throw new ArgumentException("Use typed texture assignments with the graphics API; raw handles belong to manager APIs.", nameof(definition));
        uint assigned = 0;
        foreach (var assignment in textures)
        {
            if ((uint)assignment.Slot >= 18) throw new ArgumentOutOfRangeException(nameof(textures));
            uint bit = 1u << (int)assignment.Slot;
            if ((assigned & bit) != 0) throw new ArgumentException("A texture slot was assigned twice.", nameof(textures));
            assigned |= bit;
            ArgumentNullException.ThrowIfNull(assignment.Texture);
            ObjectDisposedException.ThrowIf(assignment.Texture.IsDisposed, assignment.Texture);
            ValidateTexture(assignment.Texture);
        }
        var acquired = new TextureHandle[textures.Length];
        int count = 0;
        bool transferred = false;
        try
        {
            foreach (var assignment in textures)
            {
                MaterialTextureBinding binding = GetBinding(definition, assignment.Slot);
                TextureHandle handle = Textures.RetainGraphicsBinding(ValidateTexture(assignment.Texture!), binding.Sampler);
                acquired[count++] = handle;
                definition = SetBinding(definition, assignment.Slot, binding with { Texture = handle });
            }
            MaterialHandle material = _materials.RegisterMaterialDefinition(definition);
            transferred = true;
            try { return new VulkanMaterial(this, material); }
            catch { ReleaseMaterial(material); throw; }
        }
        catch (Exception creationFailure)
        {
            List<Exception>? failures = null;
            if (!transferred)
                for (int i = count - 1; i >= 0; i--)
                    try { ReleaseTexture(acquired[i]); }
                    catch (Exception releaseFailure) { (failures ??= new()).Add(releaseFailure); }
            if (failures != null)
            {
                failures.Insert(0, creationFailure);
                throw new AggregateException("Material creation and texture rollback failed.", failures);
            }
            throw;
        }
    }

    internal static MaterialTextureBinding GetBinding(MaterialDefinition d, MaterialTextureSlot slot) => slot switch
    {
        MaterialTextureSlot.BaseColor => d.BaseColor,
        MaterialTextureSlot.Normal => d.Normal,
        MaterialTextureSlot.MetallicRoughness => d.MetallicRoughness,
        MaterialTextureSlot.Occlusion => d.Occlusion,
        MaterialTextureSlot.Emissive => d.Emissive,
        MaterialTextureSlot.Clearcoat => d.Extensions.Clearcoat,
        MaterialTextureSlot.ClearcoatRoughnessTexture => d.Extensions.ClearcoatRoughnessTexture,
        MaterialTextureSlot.ClearcoatNormal => d.Extensions.ClearcoatNormal,
        MaterialTextureSlot.SheenColor => d.Extensions.SheenColor,
        MaterialTextureSlot.SheenRoughnessTexture => d.Extensions.SheenRoughnessTexture,
        MaterialTextureSlot.Anisotropy => d.Extensions.Anisotropy,
        MaterialTextureSlot.Transmission => d.Extensions.Transmission,
        MaterialTextureSlot.Thickness => d.Extensions.Thickness,
        MaterialTextureSlot.Specular => d.Extensions.Specular,
        MaterialTextureSlot.SpecularColor => d.Extensions.SpecularColor,
        MaterialTextureSlot.Iridescence => d.Extensions.Iridescence,
        MaterialTextureSlot.IridescenceThickness => d.Extensions.IridescenceThickness,
        MaterialTextureSlot.Subsurface => d.Extensions.Subsurface,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
    internal static MaterialDefinition SetBinding(MaterialDefinition d, MaterialTextureSlot slot, MaterialTextureBinding binding) => slot switch
    {
        MaterialTextureSlot.BaseColor => d with { BaseColor = binding },
        MaterialTextureSlot.Normal => d with { Normal = binding },
        MaterialTextureSlot.MetallicRoughness => d with { MetallicRoughness = binding },
        MaterialTextureSlot.Occlusion => d with { Occlusion = binding },
        MaterialTextureSlot.Emissive => d with { Emissive = binding },
        MaterialTextureSlot.Clearcoat => d with { Extensions = d.Extensions with { Clearcoat = binding } },
        MaterialTextureSlot.ClearcoatRoughnessTexture => d with { Extensions = d.Extensions with { ClearcoatRoughnessTexture = binding } },
        MaterialTextureSlot.ClearcoatNormal => d with { Extensions = d.Extensions with { ClearcoatNormal = binding } },
        MaterialTextureSlot.SheenColor => d with { Extensions = d.Extensions with { SheenColor = binding } },
        MaterialTextureSlot.SheenRoughnessTexture => d with { Extensions = d.Extensions with { SheenRoughnessTexture = binding } },
        MaterialTextureSlot.Anisotropy => d with { Extensions = d.Extensions with { Anisotropy = binding } },
        MaterialTextureSlot.Transmission => d with { Extensions = d.Extensions with { Transmission = binding } },
        MaterialTextureSlot.Thickness => d with { Extensions = d.Extensions with { Thickness = binding } },
        MaterialTextureSlot.Specular => d with { Extensions = d.Extensions with { Specular = binding } },
        MaterialTextureSlot.SpecularColor => d with { Extensions = d.Extensions with { SpecularColor = binding } },
        MaterialTextureSlot.Iridescence => d with { Extensions = d.Extensions with { Iridescence = binding } },
        MaterialTextureSlot.IridescenceThickness => d with { Extensions = d.Extensions with { IridescenceThickness = binding } },
        MaterialTextureSlot.Subsurface => d with { Extensions = d.Extensions with { Subsurface = binding } },
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}
