using System;
using System.Collections.Generic;

namespace Njulf.Rendering.Pipeline
{
    public readonly record struct RenderGraphResolutionContext
    {
        public RenderGraphResolutionContext(
            uint swapchainWidth,
            uint swapchainHeight,
            uint sceneWidth,
            uint sceneHeight)
        {
            if (swapchainWidth == 0 || swapchainHeight == 0)
                throw new ArgumentOutOfRangeException(nameof(swapchainWidth), "Swapchain extent must be non-zero.");
            if (sceneWidth == 0 || sceneHeight == 0)
                throw new ArgumentOutOfRangeException(nameof(sceneWidth), "Scene extent must be non-zero.");

            SwapchainWidth = swapchainWidth;
            SwapchainHeight = swapchainHeight;
            SceneWidth = sceneWidth;
            SceneHeight = sceneHeight;
        }

        public uint SwapchainWidth { get; }
        public uint SwapchainHeight { get; }
        public uint SceneWidth { get; }
        public uint SceneHeight { get; }

        public static RenderGraphResolutionContext FromScale(uint swapchainWidth, uint swapchainHeight, float sceneScale)
        {
            if (sceneScale <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(sceneScale));

            return new RenderGraphResolutionContext(
                swapchainWidth,
                swapchainHeight,
                Math.Max(1u, (uint)MathF.Ceiling(swapchainWidth * sceneScale)),
                Math.Max(1u, (uint)MathF.Ceiling(swapchainHeight * sceneScale)));
        }
    }

    public sealed record RenderGraphResolvedImageDesc(
        RenderGraphImageDesc Source,
        uint Width,
        uint Height,
        bool HistoryInvalidated);

    public sealed record RenderGraphMaterializedPlan(
        RenderGraphDeclarationPlan DeclarationPlan,
        IReadOnlyList<RenderGraphResolvedImageDesc> ResolvedImages);

    public static class RenderGraphResolutionResolver
    {
        public static RenderGraphResolvedImageDesc Resolve(
            RenderGraphImageDesc desc,
            RenderGraphResolutionContext current,
            RenderGraphResolutionContext? previous = null)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));

            (uint width, uint height) = ResolveExtent(desc, current);
            bool historyInvalidated = desc.Persistence == RenderGraphResourcePersistence.History &&
                                      previous.HasValue &&
                                      ResolutionChanged(desc, current, previous.Value);

            return new RenderGraphResolvedImageDesc(desc, width, height, historyInvalidated);
        }

        public static IReadOnlyList<RenderGraphResolvedImageDesc> ResolveAll(
            IEnumerable<RenderGraphImageDesc> images,
            RenderGraphResolutionContext current,
            RenderGraphResolutionContext? previous = null)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));

            var resolved = new List<RenderGraphResolvedImageDesc>();
            foreach (RenderGraphImageDesc image in images)
                resolved.Add(Resolve(image, current, previous));
            return resolved;
        }

        private static bool ResolutionChanged(
            RenderGraphImageDesc desc,
            RenderGraphResolutionContext current,
            RenderGraphResolutionContext previous)
        {
            (uint currentWidth, uint currentHeight) = ResolveExtent(desc, current);
            (uint previousWidth, uint previousHeight) = ResolveExtent(desc, previous);
            return currentWidth != previousWidth || currentHeight != previousHeight;
        }

        private static (uint Width, uint Height) ResolveExtent(RenderGraphImageDesc desc, RenderGraphResolutionContext context)
        {
            return desc.ResolutionClass switch
            {
                RenderGraphResolutionClass.Swapchain => (context.SwapchainWidth, context.SwapchainHeight),
                RenderGraphResolutionClass.Scene => (context.SceneWidth, context.SceneHeight),
                RenderGraphResolutionClass.HalfScene => Scale(context.SceneWidth, context.SceneHeight, 0.5f),
                RenderGraphResolutionClass.QuarterScene => Scale(context.SceneWidth, context.SceneHeight, 0.25f),
                RenderGraphResolutionClass.Fixed => ResolveFixed(desc),
                RenderGraphResolutionClass.CustomScale => Scale(context.SceneWidth, context.SceneHeight, desc.CustomResolutionScale),
                RenderGraphResolutionClass.HistoryMatchedScene => (context.SceneWidth, context.SceneHeight),
                _ => (context.SceneWidth, context.SceneHeight)
            };
        }

        private static (uint Width, uint Height) ResolveFixed(RenderGraphImageDesc desc)
        {
            if (desc.Width == 0 || desc.Height == 0)
                throw new InvalidOperationException($"Fixed-size graph image '{desc.Name}' must declare Width and Height.");
            return (desc.Width, desc.Height);
        }

        private static (uint Width, uint Height) Scale(uint width, uint height, float scale)
        {
            if (scale <= 0.0f)
                throw new InvalidOperationException("Resolution scale must be positive.");
            return (
                Math.Max(1u, (uint)MathF.Ceiling(width * scale)),
                Math.Max(1u, (uint)MathF.Ceiling(height * scale)));
        }
    }

    internal static class RenderGraphResolutionMaterializer
    {
        public static RenderGraphMaterializedPlan Materialize(
            RenderGraphDeclarationPlan declarationPlan,
            RenderGraphResolutionContext current,
            RenderGraphResolutionContext? previous = null)
        {
            if (declarationPlan == null)
                throw new ArgumentNullException(nameof(declarationPlan));

            IReadOnlyList<RenderGraphResolvedImageDesc> resolvedImages =
                RenderGraphResolutionResolver.ResolveAll(declarationPlan.Images, current, previous);

            var concreteImages = new RenderGraphImageDesc[resolvedImages.Count];
            for (int i = 0; i < resolvedImages.Count; i++)
            {
                RenderGraphResolvedImageDesc resolved = resolvedImages[i];
                concreteImages[i] = resolved.Source with
                {
                    Width = resolved.Width,
                    Height = resolved.Height
                };
            }

            RenderGraphCompilationDiagnostics diagnostics = RenderGraphDeclarationCompiler.Compile(
                declarationPlan.Passes,
                concreteImages,
                declarationPlan.Buffers,
                declarationPlan.Usage);

            return new RenderGraphMaterializedPlan(
                declarationPlan with
                {
                    Images = concreteImages,
                    Diagnostics = diagnostics
                },
                resolvedImages);
        }
    }
}
