using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Diagnostics;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public enum RenderGraphImageAllocationCategory
    {
        TransientRenderTarget,
        PersistentHistory,
        ImportedSwapchain,
        ExternalResource
    }

    public sealed record RenderGraphImageAllocationRequest(
        RenderGraphResourceHandle Handle,
        RenderGraphImageDesc Descriptor,
        ImageUsageFlags Usage,
        RenderGraphImageAllocationCategory Category,
        bool ShouldAllocate,
        ulong EstimatedBytes);

    public sealed record RenderGraphImageAllocationPlan(
        IReadOnlyList<RenderGraphImageAllocationRequest> Images,
        ulong GraphOwnedEstimatedBytes,
        ulong ImportedEstimatedBytes,
        ulong ExternalEstimatedBytes,
        int GraphOwnedImageCount)
    {
        public static RenderGraphImageAllocationPlan Empty { get; } = new(
            Images: [],
            GraphOwnedEstimatedBytes: 0,
            ImportedEstimatedBytes: 0,
            ExternalEstimatedBytes: 0,
            GraphOwnedImageCount: 0);
    }

    public static class RenderGraphImageAllocationPlanner
    {
        public static RenderGraphImageAllocationPlan Build(RenderGraphDeclarationPlan declarationPlan)
        {
            if (declarationPlan == null)
                throw new ArgumentNullException(nameof(declarationPlan));

            var requests = new List<RenderGraphImageAllocationRequest>(declarationPlan.Images.Count);
            ulong graphOwnedBytes = 0;
            ulong importedBytes = 0;
            ulong externalBytes = 0;
            int graphOwnedCount = 0;

            for (int i = 0; i < declarationPlan.Images.Count; i++)
            {
                RenderGraphImageDesc desc = declarationPlan.Images[i];
                var handle = new RenderGraphResourceHandle(RenderGraphResourceKind.Image, i, 1);
                declarationPlan.Usage.ImageUsages.TryGetValue(handle, out ImageUsageFlags usage);
                RenderGraphImageAllocationCategory category = ResolveCategory(desc.Persistence);
                bool shouldAllocate = desc.Persistence is RenderGraphResourcePersistence.Transient or RenderGraphResourcePersistence.History;
                ulong estimatedBytes = EstimateBytes(desc);

                if (shouldAllocate)
                {
                    ValidateAllocatable(desc, usage);
                    graphOwnedBytes = checked(graphOwnedBytes + estimatedBytes);
                    graphOwnedCount++;
                }
                else if (desc.Persistence == RenderGraphResourcePersistence.Imported)
                {
                    importedBytes = checked(importedBytes + estimatedBytes);
                }
                else
                {
                    externalBytes = checked(externalBytes + estimatedBytes);
                }

                requests.Add(new RenderGraphImageAllocationRequest(handle, desc, usage, category, shouldAllocate, estimatedBytes));
            }

            return new RenderGraphImageAllocationPlan(
                requests,
                graphOwnedBytes,
                importedBytes,
                externalBytes,
                graphOwnedCount);
        }

        private static RenderGraphImageAllocationCategory ResolveCategory(RenderGraphResourcePersistence persistence)
        {
            return persistence switch
            {
                RenderGraphResourcePersistence.Transient => RenderGraphImageAllocationCategory.TransientRenderTarget,
                RenderGraphResourcePersistence.History => RenderGraphImageAllocationCategory.PersistentHistory,
                RenderGraphResourcePersistence.Imported => RenderGraphImageAllocationCategory.ImportedSwapchain,
                RenderGraphResourcePersistence.External => RenderGraphImageAllocationCategory.ExternalResource,
                _ => RenderGraphImageAllocationCategory.ExternalResource
            };
        }

        private static void ValidateAllocatable(RenderGraphImageDesc desc, ImageUsageFlags usage)
        {
            if (desc.Width == 0 || desc.Height == 0)
                throw new InvalidOperationException($"Graph-owned image '{desc.Name}' requires a concrete non-zero extent before allocation.");
            if (usage == 0)
                throw new InvalidOperationException($"Graph-owned image '{desc.Name}' has no declared Vulkan image usage.");
        }

        private static ulong EstimateBytes(RenderGraphImageDesc desc)
        {
            if (desc.Width == 0 || desc.Height == 0)
                return 0;

            return ImageByteEstimator.EstimateBytes(
                desc.Format,
                new Extent3D { Width = desc.Width, Height = desc.Height, Depth = 1 },
                desc.MipCount,
                desc.ArrayLayers,
                desc.Samples);
        }
    }
}
