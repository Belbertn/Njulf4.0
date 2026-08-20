using System.Diagnostics;
using Njulf.Core.Math;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class AmbientOcclusionBlurSharedTileTests
{
    private const int GroupSize = 8;
    private const int MaximumRadius = 4;
    private const int SharedStride = GroupSize + MaximumRadius * 2;
    private const int SharedSampleCount = GroupSize * SharedStride;

    [Test]
    public void ShaderContract_UsesOnePreBarrierSharedTileForOnlyTheEstablishedAxes()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string renderingDirectory = FindRepoDirectory("Njulf.Rendering");
        string shader = File.ReadAllText(Path.Combine(
            shaderDirectory,
            "ambient_occlusion_blur.comp"));
        string pass = File.ReadAllText(Path.Combine(
            renderingDirectory,
            "Pipeline",
            "AmbientOcclusionBlurPass.cs"));
        string main = shader[shader.IndexOf("void main()", StringComparison.Ordinal)..];
        string normalizedMain = main.Replace("\r\n", "\n", StringComparison.Ordinal);
        int preloadIndex = main.IndexOf(
            "PreloadAoTile(",
            StringComparison.Ordinal);
        int barrierIndex = main.IndexOf("barrier();", StringComparison.Ordinal);
        int boundsReturnIndex = main.IndexOf(
            "if (pixel.x >= extent.x || pixel.y >= extent.y)",
            StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(shader, Does.Contain(
                "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;"));
            Assert.That(shader, Does.Contain("const int AO_BLUR_GROUP_SIZE = 8;"));
            Assert.That(shader, Does.Contain("const int AO_BLUR_MAX_RADIUS = 4;"));
            Assert.That(shader, Does.Contain(
                "shared float SharedAo[AO_BLUR_SHARED_SAMPLE_COUNT];"));
            Assert.That(shader, Does.Contain(
                "shared float SharedViewDepth[AO_BLUR_SHARED_SAMPLE_COUNT];"));
            Assert.That(shader, Does.Contain(
                "SharedAo[sharedIndex] = FetchSourceAo(samplePixel);"));
            Assert.That(shader, Does.Contain(
                "SharedViewDepth[sharedIndex] = depth <= 0.000001"));
            Assert.That(shader, Does.Contain(
                "? -1.0 : ReconstructViewDepth(sampleUv, depth);"));
            Assert.That(shader, Does.Contain(
                "vec2 invAoSize = 1.0 / max(pc.Dimensions, vec2(1.0));"));
            Assert.That(shader, Does.Contain(
                "vec2 sampleUv = (vec2(samplePixel) + vec2(0.5)) * invAoSize;"));
            Assert.That(shader, Does.Contain(
                "int radius = int(clamp(pc.Radius, 0u, 4u));"));
            Assert.That(shader, Does.Contain(
                "for (int i = -radius; i <= radius; i++)"),
                "tap order must remain the legacy negative-to-positive sequence");
            Assert.That(shader, Does.Contain(
                "bool horizontal = pc.Direction.x == 1.0;"));
            Assert.That(shader, Does.Contain(
                "float ao = SharedAo[sharedIndex];"));
            Assert.That(shader, Does.Contain(
                "float viewDepth = SharedViewDepth[sharedIndex];"));
            Assert.That(shader, Does.Not.Contain(
                "ivec2(pc.Direction * float(i))"));
            Assert.That(main, Does.Not.Contain("FetchSourceAo("),
                "main must consume only preloaded AO values");
            Assert.That(main, Does.Not.Contain("FetchDepth("),
                "main must not issue per-center or per-tap depth fetches");
            Assert.That(main, Does.Not.Contain("ReconstructViewDepth("),
                "main must not reconstruct center or tap depths after preload");
            Assert.That(normalizedMain, Does.Contain(
                "if (viewDepth < 0.0)\n            continue;"));
            Assert.That(main, Does.Contain(
                "if (depthDifference > depthSigma * 4.0)"));
            Assert.That(CountOccurrences(shader, "FetchSourceAo("), Is.EqualTo(2),
                "only the function declaration and shared preload may fetch AO");
            Assert.That(CountOccurrences(shader, "FetchDepth("), Is.EqualTo(2),
                "only the function declaration and shared preload may fetch depth");
            Assert.That(CountOccurrences(shader, "ReconstructViewDepth("), Is.EqualTo(2),
                "only the function declaration and shared preload may reconstruct depth");
            Assert.That(preloadIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(barrierIndex, Is.GreaterThan(preloadIndex));
            Assert.That(boundsReturnIndex, Is.GreaterThan(barrierIndex),
                "fringe invocations must preload and reach the workgroup barrier before returning");
            Assert.That(CountOccurrences(pass, "            Dispatch(cmd,"), Is.EqualTo(2));
            Assert.That(pass, Does.Contain(
                "Dispatch(cmd, _horizontalSet, _renderTargets.AmbientOcclusionRaw.Extent, new Vector2(1.0f, 0.0f)"));
            Assert.That(pass, Does.Contain(
                "Dispatch(cmd, _verticalSet, _renderTargets.AmbientOcclusionBlurred.Extent, new Vector2(0.0f, 1.0f)"));
        });
    }

    [TestCase(1, 1)]
    [TestCase(7, 7)]
    [TestCase(8, 8)]
    [TestCase(9, 9)]
    [TestCase(15, 17)]
    [TestCase(959, 539)]
    [TestCase(960, 540)]
    public void SharedTileIndexing_InitializesBothArraysAndMatchesEveryLegacyClampedTap(
        int width,
        int height)
    {
        Span<int> sharedSourceIndices = stackalloc int[SharedSampleCount];
        Span<float> sharedAo = stackalloc float[SharedSampleCount];
        Span<float> sharedViewDepth = stackalloc float[SharedSampleCount];
        Span<bool> sharedAoInitialized = stackalloc bool[SharedSampleCount];
        Span<bool> sharedViewDepthInitialized = stackalloc bool[SharedSampleCount];
        long comparisonCount = 0;
        foreach (BlurAxis axis in Enum.GetValues<BlurAxis>())
        {
            int groupCountX = (width + GroupSize - 1) / GroupSize;
            int groupCountY = (height + GroupSize - 1) / GroupSize;
            for (int groupY = 0; groupY < groupCountY; groupY++)
            {
                for (int groupX = 0; groupX < groupCountX; groupX++)
                {
                    FillSharedTile(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        width,
                        height,
                        groupX,
                        groupY,
                        axis);
                    for (int localY = 0; localY < GroupSize; localY++)
                    {
                        int pixelY = groupY * GroupSize + localY;
                        if (pixelY >= height)
                            continue;

                        for (int localX = 0; localX < GroupSize; localX++)
                        {
                            int pixelX = groupX * GroupSize + localX;
                            if (pixelX >= width)
                                continue;

                            for (int radius = 0; radius <= MaximumRadius; radius++)
                            {
                                for (int offset = -radius; offset <= radius; offset++)
                                {
                                    int expectedX = axis == BlurAxis.Horizontal
                                        ? Math.Clamp(pixelX + offset, 0, width - 1)
                                        : pixelX;
                                    int expectedY = axis == BlurAxis.Vertical
                                        ? Math.Clamp(pixelY + offset, 0, height - 1)
                                        : pixelY;
                                    int expected = EncodePixel(expectedX, expectedY, width);
                                    int sharedIndex = GetSharedIndex(
                                        localX,
                                        localY,
                                        offset,
                                        axis);
                                    int actual = sharedSourceIndices[sharedIndex];
                                    if (actual != expected)
                                    {
                                        Assert.Fail(
                                            $"{width}x{height} {axis} group=({groupX},{groupY}) " +
                                            $"local=({localX},{localY}) radius={radius} offset={offset}: " +
                                            $"expected source {expected}, actual {actual}.");
                                    }
                                    float expectedAo = FetchAo(expectedX, expectedY);
                                    if (BitConverter.SingleToInt32Bits(sharedAo[sharedIndex]) !=
                                        BitConverter.SingleToInt32Bits(expectedAo))
                                    {
                                        Assert.Fail(
                                            $"{width}x{height} {axis} group=({groupX},{groupY}) " +
                                            $"local=({localX},{localY}) radius={radius} offset={offset}: " +
                                            "shared AO did not come from the legacy clamped tap.");
                                    }
                                    Pixel expectedPixel = new(expectedX, expectedY);
                                    float expectedViewDepth = GetPreloadedViewDepth(
                                        width,
                                        height,
                                        expectedPixel);
                                    if (BitConverter.SingleToInt32Bits(sharedViewDepth[sharedIndex]) !=
                                        BitConverter.SingleToInt32Bits(expectedViewDepth))
                                    {
                                        Assert.Fail(
                                            $"{width}x{height} {axis} group=({groupX},{groupY}) " +
                                            $"local=({localX},{localY}) radius={radius} offset={offset}: " +
                                            "shared view depth did not use the legacy UV/predicate path.");
                                    }
                                    comparisonCount++;
                                }
                            }
                        }
                    }
                }
            }
        }

        Assert.That(comparisonCount,
            Is.EqualTo((long)width * height * 50L));
    }

    [TestCase(1, 1)]
    [TestCase(7, 7)]
    [TestCase(8, 8)]
    [TestCase(9, 9)]
    [TestCase(15, 17)]
    [TestCase(959, 539)]
    [TestCase(960, 540)]
    public void SharedTileReference_MatchesLegacyAcrossEdgesAndInvalidDepth(
        int width,
        int height)
    {
        Span<int> sharedSourceIndices = stackalloc int[SharedSampleCount];
        Span<float> sharedAo = stackalloc float[SharedSampleCount];
        Span<float> sharedViewDepth = stackalloc float[SharedSampleCount];
        Span<bool> sharedAoInitialized = stackalloc bool[SharedSampleCount];
        Span<bool> sharedViewDepthInitialized = stackalloc bool[SharedSampleCount];
        IReadOnlyList<Pixel> samples = CreateReferenceSamples(width, height);
        foreach (BlurAxis axis in Enum.GetValues<BlurAxis>())
        {
            int loadedGroupX = -1;
            int loadedGroupY = -1;
            foreach (Pixel pixel in samples)
            {
                int groupX = pixel.X / GroupSize;
                int groupY = pixel.Y / GroupSize;
                if (groupX != loadedGroupX || groupY != loadedGroupY)
                {
                    FillSharedTile(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        width,
                        height,
                        groupX,
                        groupY,
                        axis);
                    loadedGroupX = groupX;
                    loadedGroupY = groupY;
                }

                for (int radius = 0; radius <= MaximumRadius; radius++)
                {
                    float legacy = EvaluateLegacy(
                        width,
                        height,
                        pixel,
                        axis,
                        radius);
                    float tiled = EvaluateTiled(
                        pixel,
                        axis,
                        radius,
                        sharedAo,
                        sharedViewDepth);
                    Assert.That(
                        BitConverter.SingleToInt32Bits(tiled),
                        Is.EqualTo(BitConverter.SingleToInt32Bits(legacy)),
                        $"{width}x{height} {axis} pixel={pixel} radius={radius}");
                }
            }
        }

        Pixel zeroDepthPixel = new(0, 0);
        Assert.That(
            EvaluateLegacy(width, height, zeroDepthPixel, BlurAxis.Horizontal, MaximumRadius),
            Is.EqualTo(FetchAo(zeroDepthPixel.X, zeroDepthPixel.Y)));
        int pixelCount = checked(width * height);
        if (pixelCount > 1)
        {
            Pixel thresholdDepthPixel = DecodePixel(1, width);
            Assert.That(
                EvaluateLegacy(width, height, thresholdDepthPixel, BlurAxis.Horizontal, MaximumRadius),
                Is.EqualTo(FetchAo(thresholdDepthPixel.X, thresholdDepthPixel.Y)));
        }
        if (pixelCount > 2)
        {
            Pixel positiveInfinityDepthPixel = DecodePixel(2, width);
            Assert.That(
                FetchDepth(width, height, positiveInfinityDepthPixel),
                Is.EqualTo(float.PositiveInfinity));
            Assert.That(
                EvaluateLegacy(width, height, positiveInfinityDepthPixel, BlurAxis.Horizontal, MaximumRadius),
                Is.EqualTo(FetchAo(positiveInfinityDepthPixel.X, positiveInfinityDepthPixel.Y)),
                "+Inf must take the reconstruction path and retain the center-AO fallback");
        }
        if (pixelCount > 3)
        {
            Pixel belowThresholdDepthPixel = DecodePixel(3, width);
            Assert.That(
                EvaluateLegacy(width, height, belowThresholdDepthPixel, BlurAxis.Horizontal, MaximumRadius),
                Is.EqualTo(FetchAo(belowThresholdDepthPixel.X, belowThresholdDepthPixel.Y)));
        }
        if (pixelCount > 4)
        {
            Pixel nanDepthPixel = new(width - 1, height - 1);
            Assert.That(float.IsNaN(FetchDepth(width, height, nanDepthPixel)), Is.True);
            Assert.That(
                EvaluateLegacy(width, height, nanDepthPixel, BlurAxis.Vertical, MaximumRadius),
                Is.EqualTo(FetchAo(nanDepthPixel.X, nanDepthPixel.Y)),
                "the <= predicate must leave NaN on the reconstruction path and preserve center-AO fallback");
        }
    }

    [Test]
    public void SharedTileShader_CompilesAndPassesSpirvValidation()
    {
        string shaderDirectory = FindRepoDirectory("Njulf.Shaders");
        string shaderPath = Path.Combine(
            shaderDirectory,
            "ambient_occlusion_blur.comp");
        string outputPath = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            $"ambient-occlusion-blur-{Guid.NewGuid():N}.spv");
        try
        {
            RunTool(
                "glslangValidator",
                "-V",
                "--target-env",
                "vulkan1.3",
                "-Os",
                $"-I{shaderDirectory}",
                "-o",
                outputPath,
                shaderPath);
            RunTool(
                "spirv-val",
                "--target-env",
                "vulkan1.3",
                outputPath);
            Assert.That(new FileInfo(outputPath).Length, Is.GreaterThan(20));
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static void FillSharedTile(
        Span<int> sharedSourceIndices,
        Span<float> sharedAo,
        Span<float> sharedViewDepth,
        Span<bool> sharedAoInitialized,
        Span<bool> sharedViewDepthInitialized,
        int width,
        int height,
        int groupX,
        int groupY,
        BlurAxis axis)
    {
        sharedSourceIndices.Fill(-1);
        sharedAo.Fill(float.NaN);
        sharedViewDepth.Fill(float.NaN);
        sharedAoInitialized.Clear();
        sharedViewDepthInitialized.Clear();
        int baseX = groupX * GroupSize;
        int baseY = groupY * GroupSize;
        Vector2 invAoSize = new(
            1.0f / Math.Max(width, 1),
            1.0f / Math.Max(height, 1));
        for (int localY = 0; localY < GroupSize; localY++)
        {
            for (int localX = 0; localX < GroupSize; localX++)
            {
                StoreSharedSample(
                    sharedSourceIndices,
                    sharedAo,
                    sharedViewDepth,
                    sharedAoInitialized,
                    sharedViewDepthInitialized,
                    GetSharedIndex(localX, localY, 0, axis),
                    baseX + localX,
                    baseY + localY,
                    width,
                    height,
                    invAoSize);
                if (axis == BlurAxis.Horizontal && localX < MaximumRadius)
                {
                    StoreSharedSample(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        localY * SharedStride + localX,
                        baseX + localX - MaximumRadius,
                        baseY + localY,
                        width,
                        height,
                        invAoSize);
                    StoreSharedSample(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        localY * SharedStride +
                            MaximumRadius + GroupSize + localX,
                        baseX + GroupSize + localX,
                        baseY + localY,
                        width,
                        height,
                        invAoSize);
                }
                else if (axis == BlurAxis.Vertical && localY < MaximumRadius)
                {
                    StoreSharedSample(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        localY * GroupSize + localX,
                        baseX + localX,
                        baseY + localY - MaximumRadius,
                        width,
                        height,
                        invAoSize);
                    StoreSharedSample(
                        sharedSourceIndices,
                        sharedAo,
                        sharedViewDepth,
                        sharedAoInitialized,
                        sharedViewDepthInitialized,
                        (MaximumRadius + GroupSize + localY) * GroupSize +
                            localX,
                        baseX + localX,
                        baseY + GroupSize + localY,
                        width,
                        height,
                        invAoSize);
                }
            }
        }

        for (int index = 0; index < SharedSampleCount; index++)
        {
            if (sharedSourceIndices[index] < 0 || !sharedAoInitialized[index])
            {
                Assert.Fail(
                    $"Shared AO index {index} was not fully initialized for " +
                    $"{axis} group ({groupX},{groupY}).");
            }
            if (!sharedViewDepthInitialized[index])
            {
                Assert.Fail(
                    $"Shared view-depth index {index} was not initialized for " +
                    $"{axis} group ({groupX},{groupY}).");
            }
        }
    }

    private static void StoreSharedSample(
        Span<int> sharedSourceIndices,
        Span<float> sharedAo,
        Span<float> sharedViewDepth,
        Span<bool> sharedAoInitialized,
        Span<bool> sharedViewDepthInitialized,
        int sharedIndex,
        int sampleX,
        int sampleY,
        int width,
        int height,
        Vector2 invAoSize)
    {
        Pixel samplePixel = new(
            Math.Clamp(sampleX, 0, width - 1),
            Math.Clamp(sampleY, 0, height - 1));
        sharedSourceIndices[sharedIndex] = EncodePixel(
            samplePixel.X,
            samplePixel.Y,
            width);
        sharedAo[sharedIndex] = FetchAo(samplePixel.X, samplePixel.Y);
        sharedAoInitialized[sharedIndex] = true;

        sharedViewDepth[sharedIndex] = GetPreloadedViewDepth(
            width,
            height,
            samplePixel,
            invAoSize);
        sharedViewDepthInitialized[sharedIndex] = true;
    }

    private static float GetPreloadedViewDepth(
        int width,
        int height,
        Pixel samplePixel)
    {
        Vector2 invAoSize = new(
            1.0f / Math.Max(width, 1),
            1.0f / Math.Max(height, 1));
        return GetPreloadedViewDepth(
            width,
            height,
            samplePixel,
            invAoSize);
    }

    private static float GetPreloadedViewDepth(
        int width,
        int height,
        Pixel samplePixel,
        Vector2 invAoSize)
    {
        Vector2 sampleUv = new(
            (samplePixel.X + 0.5f) * invAoSize.X,
            (samplePixel.Y + 0.5f) * invAoSize.Y);
        float depth = FetchDepth(width, height, samplePixel);
        return depth <= 0.000001f
            ? -1.0f
            : ReconstructViewDepth(sampleUv, depth);
    }

    private static int GetSharedIndex(
        int localX,
        int localY,
        int offset,
        BlurAxis axis) => axis == BlurAxis.Horizontal
        ? localY * SharedStride + localX + MaximumRadius + offset
        : (localY + MaximumRadius + offset) * GroupSize + localX;

    private static int EncodePixel(int x, int y, int width) =>
        checked(y * width + x);

    private static Pixel DecodePixel(int encoded, int width) =>
        new(encoded % width, encoded / width);

    private static float EvaluateLegacy(
        int width,
        int height,
        Pixel pixel,
        BlurAxis axis,
        int radius)
    {
        Vector2 invAoSize = new(
            1.0f / Math.Max(width, 1),
            1.0f / Math.Max(height, 1));
        Vector2 uv = new(
            (pixel.X + 0.5f) * invAoSize.X,
            (pixel.Y + 0.5f) * invAoSize.Y);
        float centerDepth = FetchDepth(width, height, pixel);
        if (centerDepth <= 0.000001f)
            return FetchAo(pixel.X, pixel.Y);

        float centerViewDepth = ReconstructViewDepth(uv, centerDepth);
        float depthSigma = MathF.Min(
            MathF.Max(0.65f, 0.05f),
            MathF.Max(0.08f, centerViewDepth * 0.03f));
        float total = 0.0f;
        float weightSum = 0.0f;
        for (int offset = -radius; offset <= radius; offset++)
        {
            Pixel sample = OffsetAndClamp(pixel, axis, offset, width, height);
            Vector2 sampleUv = new(
                (sample.X + 0.5f) * invAoSize.X,
                (sample.Y + 0.5f) * invAoSize.Y);
            float ao = FetchAo(sample.X, sample.Y);
            float depth = FetchDepth(width, height, sample);
            if (depth <= 0.000001f)
                continue;

            float viewDepth = ReconstructViewDepth(sampleUv, depth);
            float depthDifference = MathF.Abs(centerViewDepth - viewDepth);
            if (depthDifference > depthSigma * 4.0f)
                continue;

            float spatialWeight = Gaussian(offset, Math.Max(radius, 1));
            float depthWeight = Gaussian(depthDifference, depthSigma);
            float weight = spatialWeight * depthWeight;
            total += ao * weight;
            weightSum += weight;
        }

        return weightSum > 0.0f
            ? total / weightSum
            : FetchAo(pixel.X, pixel.Y);
    }

    private static float EvaluateTiled(
        Pixel pixel,
        BlurAxis axis,
        int radius,
        ReadOnlySpan<float> sharedAo,
        ReadOnlySpan<float> sharedViewDepth)
    {
        int localX = pixel.X % GroupSize;
        int localY = pixel.Y % GroupSize;
        int centerSharedIndex = GetSharedIndex(localX, localY, 0, axis);
        float centerViewDepth = sharedViewDepth[centerSharedIndex];
        if (centerViewDepth < 0.0f)
            return sharedAo[centerSharedIndex];

        float depthSigma = MathF.Min(
            MathF.Max(0.65f, 0.05f),
            MathF.Max(0.08f, centerViewDepth * 0.03f));
        float total = 0.0f;
        float weightSum = 0.0f;
        for (int offset = -radius; offset <= radius; offset++)
        {
            int sharedIndex = GetSharedIndex(
                localX,
                localY,
                offset,
                axis);
            float ao = sharedAo[sharedIndex];
            float viewDepth = sharedViewDepth[sharedIndex];
            if (viewDepth < 0.0f)
                continue;

            float depthDifference = MathF.Abs(centerViewDepth - viewDepth);
            if (depthDifference > depthSigma * 4.0f)
                continue;

            float spatialWeight = Gaussian(offset, Math.Max(radius, 1));
            float depthWeight = Gaussian(depthDifference, depthSigma);
            float weight = spatialWeight * depthWeight;
            total += ao * weight;
            weightSum += weight;
        }

        return weightSum > 0.0f
            ? total / weightSum
            : sharedAo[centerSharedIndex];
    }

    private static float FetchDepth(int width, int height, Pixel pixel)
    {
        int encoded = EncodePixel(pixel.X, pixel.Y, width);
        if (encoded == 0)
            return 0.0f;
        if (encoded == 1)
            return 0.000001f;
        if (encoded == 2)
            return float.PositiveInfinity;
        if (encoded == 3)
            return 0.0000005f;
        if (encoded == checked(width * height - 1))
            return float.NaN;
        return 0.12f + ((pixel.X * 17 + pixel.Y * 29) % 71) * 0.001f;
    }

    private static float ReconstructViewDepth(Vector2 uv, float depth)
    {
        float clipX = uv.X * 2.0f - 1.0f;
        float clipY = uv.Y * 2.0f - 1.0f;
        float viewZ = depth * 2.3f + clipX * 0.17f - clipY * 0.11f;
        float viewW = 1.0f + depth * 0.23f;
        return MathF.Abs(viewZ / MathF.Max(MathF.Abs(viewW), 0.00001f));
    }

    private static float FetchAo(int x, int y) =>
        ((x * 37 + y * 53 + 11) % 251) / 250.0f;

    private static float Gaussian(float value, float sigma)
    {
        float safeSigma = MathF.Max(sigma, 0.0001f);
        return MathF.Exp(
            -(value * value) /
            (2.0f * safeSigma * safeSigma));
    }

    private static Pixel OffsetAndClamp(
        Pixel pixel,
        BlurAxis axis,
        int offset,
        int width,
        int height) => axis == BlurAxis.Horizontal
        ? new Pixel(Math.Clamp(pixel.X + offset, 0, width - 1), pixel.Y)
        : new Pixel(pixel.X, Math.Clamp(pixel.Y + offset, 0, height - 1));

    private static IReadOnlyList<Pixel> CreateReferenceSamples(
        int width,
        int height)
    {
        int[] xCandidates =
            [0, 1, 2, 3, 6, 7, 8, width / 2, width - 2, width - 1];
        int[] yCandidates =
            [0, 1, 6, 7, 8, height / 2, height - 2, height - 1];
        var samples = new HashSet<Pixel>();
        foreach (int x in xCandidates)
        {
            foreach (int y in yCandidates)
            {
                samples.Add(new Pixel(
                    Math.Clamp(x, 0, width - 1),
                    Math.Clamp(y, 0, height - 1)));
            }
        }
        return samples
            .OrderBy(static pixel => pixel.Y)
            .ThenBy(static pixel => pixel.X)
            .ToArray();
    }

    private static void RunTool(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo) ??
            throw new AssertionException($"Could not start {fileName}.");
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{fileName} timed out. {standardOutput} {standardError}");
        }
        Assert.That(process.ExitCode, Is.Zero,
            $"{fileName} failed. {standardOutput} {standardError}");
    }

    private static int CountOccurrences(string value, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static string FindRepoDirectory(string name)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (directory != null)
        {
            string candidate = Path.Combine(directory, name);
            if (Directory.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new AssertionException($"Could not find repo directory '{name}'.");
    }

    private enum BlurAxis : byte
    {
        Horizontal,
        Vertical
    }

    private readonly record struct Pixel(int X, int Y);
}
