using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Njulf.Rendering;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class GiCausticScreenResolveContractTests
{
    [Test]
    public void ManagedScreenAbi_HasExactFrozenSizesAndOffsets()
    {
        GiCausticScreenGpuAbi.VerifyManagedLayout();

        Assert.Multiple(() =>
        {
            Assert.That(Marshal.SizeOf<GPUCausticScreenPushConstantsV1>(),
                Is.EqualTo(GiCausticScreenGpuAbi.PushConstantsBytes));
            Assert.That(Marshal.SizeOf<GPUCausticScreenFrameConstantsV1>(),
                Is.EqualTo(GiCausticScreenGpuAbi.FrameConstantsBytes));
            Assert.That(GiCausticScreenGpuBindings.SceneDepth, Is.EqualTo(0u));
            Assert.That(GiCausticScreenGpuBindings.FrameConstants,
                Is.EqualTo(GiCausticScreenGpuAbi.DescriptorCount - 1u));
            Assert.That(GiCausticScreenGpuDescriptorSets.ScreenResources,
                Is.EqualTo(2u));
        });
    }

    [Test]
    public void ScreenLayout_AccountsPayloadRadianceAndCompactTileListExactly()
    {
        GiCausticScreenResolveLayout layout =
            GiCausticScreenResolveLayoutCompiler.Compile(new(
                Width: 1_920,
                Height: 1_080));

        Assert.Multiple(() =>
        {
            Assert.That(layout.IsValid, Is.True, layout.FailureReason);
            Assert.That(layout.TileCountX, Is.EqualTo(240));
            Assert.That(layout.TileCountY, Is.EqualTo(135));
            Assert.That(layout.TileCapacity, Is.EqualTo(32_400));
            Assert.That(layout.ReceiverPayloadBytes,
                Is.EqualTo(1_920UL * 1_080UL * 16UL));
            Assert.That(layout.RadianceBytes,
                Is.EqualTo(1_920UL * 1_080UL * 8UL));
            Assert.That(layout.MomentsBytes,
                Is.EqualTo(1_920UL * 1_080UL * 8UL));
            Assert.That(layout.TileScratchBytes %
                GiCausticScreenGpuAbi.ScratchAlignmentBytes, Is.Zero);
            Assert.That(layout.TileScratchBytes,
                Is.GreaterThanOrEqualTo(
                    (ulong)(GiCausticScreenGpuAbi.TileListWordOffset +
                        layout.TileCapacity) * sizeof(uint)));
        });
    }

    [Test]
    public void GpuLayout_AliasesBuildAndTileScratchButBudgetsBothImages()
    {
        GiCausticCacheLayout cache = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 256,
            maximumPhotonsPerCell: 8,
            maximumOccupiedCells: 128,
            recordStride: GiCausticGpuAbi.PhotonRecordBytes,
            writeBankCount: 2,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0UL,
            budgetBytes: 16UL * 1024UL * 1024UL);
        GiCausticGpuResourceLayout layout =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                cache,
                IndependentMemoryBudgetBytes: 16UL * 1024UL * 1024UL,
                ScreenResolveProfile: new(320, 180)));
        GiCausticGpuMemoryRequirements memory =
            layout.CreateMemoryRequirements(
                admitted: true,
                allocated: true);

        Assert.Multiple(() =>
        {
            Assert.That(layout.IsValid, Is.True, layout.FailureReason);
            Assert.That(layout.ScratchBytes,
                Is.EqualTo(System.Math.Max(
                    cache.SortScratchBytes,
                    layout.ScreenResolve.TileScratchBytes)));
            Assert.That(layout.TotalBytes,
                Is.EqualTo(layout.BufferTotalBytes +
                    layout.ScreenResolve.PersistentImageBytes +
                    layout.RuntimeMetadataBytes));
            Assert.That(layout.RuntimeMetadataBytes,
                Is.EqualTo((ulong)RenderingConstants.FramesInFlight *
                    ((ulong)GiCausticScreenGpuAbi.FrameConstantsBytes +
                     (ulong)GiCausticGpuAbi.CacheHeaderBytes)));
            Assert.That(memory.History.RequiredBytes,
                Is.EqualTo(layout.CacheTableBytes +
                    layout.PublicationHeaderBytes +
                    layout.ScreenResolve.PersistentImageBytes +
                    layout.RuntimeMetadataBytes));
            Assert.That(memory.RequiredBytes, Is.EqualTo(layout.TotalBytes));
        });
    }

    [Test]
    public void InvalidOrMissingScreenProfile_FailsClosedBeforeAllocation()
    {
        GiCausticCacheLayout cache = GiCausticCacheLayoutCompiler.Compile(
            photonTaskCapacity: 16,
            maximumPhotonsPerCell: 4,
            maximumOccupiedCells: 4,
            recordStride: GiCausticGpuAbi.PhotonRecordBytes,
            writeBankCount: 2,
            cacheBankCount: 2,
            targetLoadFactor: 0.5f,
            historyBytes: 0UL,
            budgetBytes: 1_000_000UL);
        GiCausticGpuResourceLayout layout =
            GiCausticGpuResourceLayoutCompiler.Compile(new(
                cache,
                IndependentMemoryBudgetBytes: 1_000_000UL));

        Assert.Multiple(() =>
        {
            Assert.That(layout.IsValid, Is.False);
            Assert.That(layout.FailureReason,
                Is.EqualTo("caustic-screen-resolve-extent-missing"));
            Assert.That(layout.TotalBytes, Is.Zero);
        });
    }

    [Test]
    public void ScreenShaders_UsePrivateSetAndRecorderUsesIndirectCompactedTiles()
    {
        string shared = ReadRepoText("Njulf.Shaders", "gi_caustic_screen.glsl");
        string classify = ReadRepoText(
            "Njulf.Shaders", "gi_caustic_screen_classify.comp");
        string resolve = ReadRepoText(
            "Njulf.Shaders", "gi_caustic_screen_resolve.comp");
        string composite = ReadRepoText(
            "Njulf.Shaders", "gi_caustic_screen_composite.comp");
        string recorder = ReadRepoText(
            "Njulf.Rendering", "Pipeline", "GiCausticScreenGpuPass.cs");

        Assert.Multiple(() =>
        {
            for (uint binding = 0u;
                 binding < GiCausticScreenGpuAbi.DescriptorCount;
                 binding++)
            {
                Assert.That(shared,
                    Does.Contain($"set = 2, binding = {binding}"),
                    $"C4 screen binding {binding} must be frozen in private set 2.");
            }
            Assert.That(shared, Does.Not.Contain("set = 0, binding = 0) uniform sampler2D"));
            Assert.That(classify, Does.Contain("GI_CAUSTIC_SCREEN_TILE_LIST_WORD_OFFSET"));
            Assert.That(resolve, Does.Contain("GiCausticFootprintWeight"));
            Assert.That(composite, Does.Contain("scene.rgb + caustic.rgb"));
            Assert.That(composite, Does.Not.Contain("caustic.rgb * caustic.a"));
            Assert.That(recorder, Does.Contain("CmdDispatchIndirect"));
            Assert.That(recorder, Does.Contain("IndirectCommandReadBit"));
            Assert.That(recorder, Does.Contain("_frameConstantBuffers"));
        });
    }

    private static string ReadRepoText(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(TestContext.CurrentContext.TestDirectory);
        while (directory is not null &&
               !Directory.Exists(Path.Combine(directory.FullName,
                   "Njulf.Rendering")))
        {
            directory = directory.Parent;
        }
        if (directory is null)
            throw new InvalidOperationException("Repository root was not found.");
        string path = Path.Combine(
            new[] { directory.FullName }.Concat(relativeSegments).ToArray());
        return File.ReadAllText(path);
    }
}
