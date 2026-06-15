using System;
using System.Collections.Generic;
using System.Linq;
using Njulf.Rendering.Descriptors;
using Silk.NET.Vulkan;

namespace Njulf.Rendering.Pipeline
{
    public sealed record RenderGraphImageDescriptorBinding(
        RenderGraphResourceHandle Handle,
        string ResourceName,
        int BindlessIndex,
        ImageLayout ExpectedLayout,
        bool IsStaticIndex);

    public sealed record RenderGraphDescriptorPlan(IReadOnlyList<RenderGraphImageDescriptorBinding> ImageBindings)
    {
        public static RenderGraphDescriptorPlan Empty { get; } = new([]);
    }

    public static class RenderGraphDescriptorPlanner
    {
        private static readonly IReadOnlyDictionary<string, int> StaticTextureIndices = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Scene Depth"] = BindlessIndex.DepthTexture,
            ["Hi-Z Depth Pyramid"] = BindlessIndex.HiZDepthTexture,
            ["HDR Scene Color"] = BindlessIndex.HdrSceneColorTexture,
            ["Fogged HDR Scene Color"] = BindlessIndex.FoggedSceneColorTexture,
            ["Ambient Occlusion Raw"] = BindlessIndex.AmbientOcclusionRawTexture,
            ["Ambient Occlusion Blurred"] = BindlessIndex.AmbientOcclusionBlurredTexture,
            ["Scene Normal"] = BindlessIndex.SceneNormalTexture,
            ["LDR Scene Color"] = BindlessIndex.LdrSceneColorTexture,
            ["SMAA Edges"] = BindlessIndex.SmaaEdgesTexture,
            ["SMAA Blend Weights"] = BindlessIndex.SmaaBlendWeightsTexture,
            ["SMAA Area Texture"] = BindlessIndex.SmaaAreaTexture,
            ["SMAA Search Texture"] = BindlessIndex.SmaaSearchTexture,
            ["Motion Vectors"] = BindlessIndex.MotionVectorTexture,
            ["Weighted OIT Accumulation"] = BindlessIndex.WeightedOitAccumulationTexture,
            ["Weighted OIT Revealage"] = BindlessIndex.WeightedOitRevealageTexture,
            ["TAA History A"] = BindlessIndex.TaaHistoryTexture,
            ["TAA History B"] = BindlessIndex.TaaHistoryTexture,
            ["Directional Shadow Map Array"] = BindlessIndex.DirectionalShadowTextureBase,
            ["Spot Shadow Atlas"] = BindlessIndex.SpotShadowAtlasTexture,
            ["Point Shadow Cubemap Array"] = BindlessIndex.PointShadowCubemapArrayTexture,
            ["Environment Cubemap"] = BindlessIndex.EnvironmentCubemapTexture,
            ["Irradiance Cubemap"] = BindlessIndex.IrradianceCubemapTexture,
            ["Prefiltered Environment Cubemap"] = BindlessIndex.PrefilteredEnvironmentTexture,
            ["BRDF LUT"] = BindlessIndex.BrdfLutTexture,
            ["Reflection Probe Cubemap Array"] = BindlessIndex.ReflectionProbeCubemapArrayTexture
        };

        public static RenderGraphDescriptorPlan Build(RenderGraphDeclarationPlan declarationPlan)
        {
            if (declarationPlan == null)
                throw new ArgumentNullException(nameof(declarationPlan));

            var bindings = new List<RenderGraphImageDescriptorBinding>();
            int nextDynamicIndex = BindlessIndex.FirstDynamicTextureIndex;
            var usedStaticIndices = new Dictionary<int, string>();
            HashSet<RenderGraphResourceHandle> liveImages = declarationPlan.Diagnostics.ResourceLifetimes
                .Where(lifetime => lifetime.Kind == RenderGraphResourceKind.Image)
                .Select(lifetime => lifetime.Handle)
                .ToHashSet();

            for (int i = 0; i < declarationPlan.Images.Count; i++)
            {
                RenderGraphImageDesc image = declarationPlan.Images[i];
                var handle = new RenderGraphResourceHandle(RenderGraphResourceKind.Image, i, 1);
                if (!liveImages.Contains(handle))
                    continue;

                if (!IsSampled(declarationPlan, handle))
                    continue;

                bool isStatic = TryResolveStaticIndex(image.Name, out int bindlessIndex);
                if (!isStatic)
                    bindlessIndex = nextDynamicIndex++;

                if (isStatic &&
                    usedStaticIndices.TryGetValue(bindlessIndex, out string? existingName) &&
                    !CanShareStaticIndex(existingName, image.Name))
                {
                    throw new InvalidOperationException(
                        $"Graph image descriptor index {bindlessIndex} is shared by '{existingName}' and '{image.Name}'.");
                }

                if (isStatic)
                    usedStaticIndices[bindlessIndex] = image.Name;

                bindings.Add(new RenderGraphImageDescriptorBinding(
                    handle,
                    image.Name,
                    bindlessIndex,
                    ResolveExpectedLayout(declarationPlan, handle),
                    isStatic));
            }

            return new RenderGraphDescriptorPlan(bindings);
        }

        private static bool TryResolveStaticIndex(string name, out int index)
        {
            if (StaticTextureIndices.TryGetValue(name, out index))
                return true;

            if (string.Equals(name, "Bloom Extract", StringComparison.Ordinal))
            {
                index = BindlessIndex.BloomMipTextureBase;
                return true;
            }

            const string bloomPrefix = "Bloom Mip ";
            if (name.StartsWith(bloomPrefix, StringComparison.Ordinal) &&
                int.TryParse(name.AsSpan(bloomPrefix.Length), out int mip) &&
                mip >= 0 &&
                mip < BindlessIndex.MaxBloomMipTextures)
            {
                index = BindlessIndex.BloomMipTextureBase + mip;
                return true;
            }

            index = -1;
            return false;
        }

        private static bool IsSampled(RenderGraphDeclarationPlan declarationPlan, RenderGraphResourceHandle handle)
        {
            return declarationPlan.Usage.ImageUsages.TryGetValue(handle, out ImageUsageFlags usage) &&
                   (usage & ImageUsageFlags.SampledBit) != 0;
        }

        private static ImageLayout ResolveExpectedLayout(RenderGraphDeclarationPlan declarationPlan, RenderGraphResourceHandle handle)
        {
            foreach (RenderGraphPassDesc pass in declarationPlan.Passes)
            {
                foreach (RenderGraphResourceUse use in pass.Reads.Concat(pass.ReadWrites))
                {
                    if (use.Handle.Equals(handle) && use.Access == RenderGraphResourceAccess.SampledRead)
                        return IsDepthImage(declarationPlan.Images[handle.Index].Format)
                            ? ImageLayout.DepthStencilReadOnlyOptimal
                            : ImageLayout.ShaderReadOnlyOptimal;
                }
            }

            return ImageLayout.General;
        }

        private static bool IsDepthImage(Format format)
        {
            return format is Format.D16Unorm or
                Format.D24UnormS8Uint or
                Format.D32Sfloat or
                Format.D32SfloatS8Uint;
        }

        private static bool CanShareStaticIndex(string existingName, string requestedName)
        {
            return (existingName, requestedName) is ("TAA History A", "TAA History B") or ("TAA History B", "TAA History A");
        }
    }
}
