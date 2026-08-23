using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Resources;

public sealed class VolumetricFogResources : IDisposable
{
    private readonly VolumetricImage[] _images;
    private bool _disposed;

    public VolumetricFogResources(
        Core.VulkanContext context,
        VolumetricFogGridLayout layout,
        VolumetricFogQualityProfile profile)
    {
        if (layout.FroxelCount == 0)
            throw new ArgumentException("A live froxel layout is required.", nameof(layout));

        Layout = layout;
        Profile = profile;
        var froxelExtent = new Extent3D(layout.Width, layout.Height, layout.Depth);
        var clusterExtent = new Extent3D(
            layout.ClusterWidth,
            layout.ClusterHeight,
            layout.ClusterDepth);
        var lightingExtent = new Extent3D(
            layout.LightingWidth,
            layout.LightingHeight,
            layout.LightingDepth);
        var resolveExtent = new Extent3D(
            layout.ResolveWidth,
            layout.ResolveHeight,
            1u);
        var images = new List<VolumetricImage>();
        VolumetricImage Create(
            string name,
            Format format,
            Extent3D extent,
            uint mipLevels = 1,
            bool true3D = false)
        {
            var image = new VolumetricImage(
                context, name, format, extent, mipLevels, true3D);
            images.Add(image);
            return image;
        }

        try
        {
            MediumCoefficients = Create(
                "Froxel Medium Coefficients", Format.R16G16B16A16Sfloat, froxelExtent);
            MediumAuxiliary = Create(
                "Froxel Velocity and Anisotropy",
                Format.R16G16B16A16Sfloat,
                froxelExtent);
            DirectRadiance = Create(
                "Froxel Direct Lighting Cache",
                Format.R16G16B16A16Sfloat,
                lightingExtent);
            IndirectRadiance = Create(
                "Froxel Indirect Lighting Cache",
                Format.R16G16B16A16Sfloat,
                lightingExtent);
            History0 = Create(
                "Froxel History 0", Format.R16G16B16A16Sfloat, froxelExtent);
            History1 = Create(
                "Froxel History 1", Format.R16G16B16A16Sfloat, froxelExtent);
            HistoryConfidence0 = Create(
                "Froxel History Confidence 0", Format.R16Sfloat, froxelExtent);
            HistoryConfidence1 = Create(
                "Froxel History Confidence 1", Format.R16Sfloat, froxelExtent);
            ResolvedHalf = Create(
                "Froxel Half Resolution Resolve",
                Format.R16G16B16A16Sfloat,
                resolveExtent);
            LightingMedium = Create(
                "Froxel Reduced Lighting Medium",
                Format.R16G16B16A16Sfloat,
                lightingExtent);
            CoarseTransmittance = Create(
                "Froxel Coarse Transmittance",
                Format.R16G16B16A16Sfloat,
                clusterExtent);
            Noise = Create(
                "Froxel Tileable Density Noise",
                Format.R8G8B8A8Unorm,
                new Extent3D(64, 64, 64),
                true3D: true);

            if (profile.MultipleScatteringIterations > 0)
            {
                MultipleScattering0 = Create(
                    "Froxel Lighting Cache Multiple Scattering 0",
                    Format.R16G16B16A16Sfloat,
                    lightingExtent);
                MultipleScattering1 = Create(
                    "Froxel Lighting Cache Multiple Scattering 1",
                    Format.R16G16B16A16Sfloat,
                    lightingExtent);
            }

            _images = images.ToArray();
        }
        catch
        {
            foreach (VolumetricImage image in images)
                image.Dispose();
            throw;
        }
    }

    public VolumetricFogGridLayout Layout { get; }
    public VolumetricFogQualityProfile Profile { get; }
    public VolumetricImage MediumCoefficients { get; }
    public VolumetricImage MediumAuxiliary { get; }
    public VolumetricImage DirectRadiance { get; }
    public VolumetricImage IndirectRadiance { get; }
    public VolumetricImage History0 { get; }
    public VolumetricImage History1 { get; }
    public VolumetricImage HistoryConfidence0 { get; }
    public VolumetricImage HistoryConfidence1 { get; }
    public VolumetricImage ResolvedHalf { get; }
    public VolumetricImage LightingMedium { get; }
    public VolumetricImage CoarseTransmittance { get; }
    public VolumetricImage Noise { get; }
    public VolumetricImage? MultipleScattering0 { get; }
    public VolumetricImage? MultipleScattering1 { get; }
    public IReadOnlyList<VolumetricImage> Images => _images;
    public ulong AllocationByteSize
    {
        get
        {
            ulong total = 0;
            foreach (VolumetricImage image in _images)
                total = checked(total + image.AllocationByteSize);
            return total;
        }
    }

    public VolumetricImage CurrentHistory(int frameIndex) =>
        (frameIndex & 1) == 0 ? History0 : History1;

    public VolumetricImage PreviousHistory(int frameIndex) =>
        (frameIndex & 1) == 0 ? History1 : History0;

    public VolumetricImage CurrentHistoryConfidence(int frameIndex) =>
        (frameIndex & 1) == 0 ? HistoryConfidence0 : HistoryConfidence1;

    public VolumetricImage PreviousHistoryConfidence(int frameIndex) =>
        (frameIndex & 1) == 0 ? HistoryConfidence1 : HistoryConfidence0;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (VolumetricImage image in _images)
            image.Dispose();
        GC.SuppressFinalize(this);
    }
}
