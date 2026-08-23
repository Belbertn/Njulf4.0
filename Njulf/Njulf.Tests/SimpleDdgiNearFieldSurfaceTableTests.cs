using Njulf.Rendering.Resources;
using Njulf.Rendering.Data;
using NUnit.Framework;

namespace Njulf.Tests;

[TestFixture]
public sealed class SimpleDdgiNearFieldSurfaceTableTests
{
    [Test]
    public void FrameBanks_DeduplicateStableIdentityAndPublishExactEntry()
    {
        var table = new SimpleDdgiNearFieldSurfaceTable(frameBankCount: 2);
        var entry = Entry(11u, 17u, 3, 5);
        table.BeginFrame(0u);

        bool first = table.TryGetOrAdd(entry, out ushort firstToken);
        bool duplicate = table.TryGetOrAdd(entry, out ushort duplicateToken);
        ReadOnlyMemory<GPUSimpleDdgiNearFieldSurfaceEntry> published =
            table.Seal();

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(duplicate, Is.True);
            Assert.That(firstToken, Is.Zero);
            Assert.That(duplicateToken, Is.EqualTo(firstToken));
            Assert.That(published.Length, Is.EqualTo(1));
            Assert.That(published.Span[0], Is.EqualTo(entry));
        });
    }

    [Test]
    public void UnsupportedSurface_IsPixelLocalAndDoesNotConsumeCapacity()
    {
        var table = new SimpleDdgiNearFieldSurfaceTable(frameBankCount: 2);
        table.BeginFrame(0u);
        var unsupported = Entry(1u, 2u, 1, 1) with
        {
            Flags = SimpleDdgiNearFieldSurfaceFlags.Opaque
        };

        bool accepted = table.TryGetOrAdd(unsupported, out ushort token);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False);
            Assert.That(token, Is.EqualTo(ushort.MaxValue));
            Assert.That(table.ActiveEntryCount, Is.Zero);
            Assert.That(table.Seal().Length, Is.Zero);
        });
    }

    [Test]
    public void RevisionWrap_AdvancesGlobalSceneGeneration()
    {
        var table = new SimpleDdgiNearFieldSurfaceTable(frameBankCount: 2);
        table.BeginFrame(0u);
        Assert.That(table.TryGetOrAdd(Entry(5u, 7u, ushort.MaxValue,
            ushort.MaxValue), out _), Is.True);
        table.Seal();
        uint before = table.SceneGeneration;

        table.BeginFrame(1u);
        Assert.That(table.TryGetOrAdd(Entry(5u, 7u, 1, 1), out _), Is.True);

        Assert.That(table.SceneGeneration, Is.Not.EqualTo(before));
    }

    [Test]
    public void CapacityOverflow_InvalidatesOnlyTheAdditionalSurface()
    {
        var table = new SimpleDdgiNearFieldSurfaceTable(frameBankCount: 2);
        table.BeginFrame(0u);
        for (uint index = 0u;
             index < SimpleDdgiNearFieldSurfaceTable.Capacity;
             index++)
        {
            Assert.That(table.TryGetOrAdd(
                Entry(index + 1u, index + 1u, 1, 1), out _), Is.True);
        }

        bool overflow = table.TryGetOrAdd(
            Entry(100_000u, 100_000u, 1, 1), out ushort token);

        Assert.Multiple(() =>
        {
            Assert.That(overflow, Is.False);
            Assert.That(token, Is.EqualTo(ushort.MaxValue));
            Assert.That(table.ActiveEntryCount,
                Is.EqualTo(SimpleDdgiNearFieldSurfaceTable.Capacity));
            Assert.That(table.OverflowPixelCount, Is.EqualTo(1u));
        });
    }

    [Test]
    public void ScenePublication_UsesStableMaterialIdentityAndPixelLocalEligibility()
    {
        uint first = SceneDataBuilder.CreateNearFieldStableMaterialIdentity(
            new MaterialHandle(7, 2u));
        uint repeated = SceneDataBuilder.CreateNearFieldStableMaterialIdentity(
            new MaterialHandle(7, 2u));
        uint nextGeneration =
            SceneDataBuilder.CreateNearFieldStableMaterialIdentity(
                new MaterialHandle(7, 3u));
        uint opaque = SceneDataBuilder.CreateNearFieldSurfaceFlags(
            MaterialRenderMode.Opaque,
            isGeometryDecal: false);
        uint masked = SceneDataBuilder.CreateNearFieldSurfaceFlags(
            MaterialRenderMode.Mask,
            isGeometryDecal: false);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Zero);
            Assert.That(repeated, Is.EqualTo(first));
            Assert.That(nextGeneration, Is.Not.EqualTo(first));
            Assert.That(opaque & (uint)
                SimpleDdgiNearFieldSurfaceFlags.Opaque, Is.Not.Zero);
            Assert.That(masked & (uint)
                SimpleDdgiNearFieldSurfaceFlags.AlphaMasked, Is.Not.Zero);
            Assert.That(opaque & (uint)
                SimpleDdgiNearFieldSurfaceFlags.MotionVectorsValid,
                Is.Not.Zero);
            Assert.That(SceneDataBuilder.CreateNearFieldSurfaceFlags(
                MaterialRenderMode.Blend, false), Is.Zero);
            Assert.That(SceneDataBuilder.CreateNearFieldSurfaceFlags(
                MaterialRenderMode.Opaque, true), Is.Zero);
        });
    }

    [Test]
    public void ScenePublication_PacksNonZeroWrapSafeSixteenBitRevisions()
    {
        uint first = SceneDataBuilder.PackNearFieldRevisions(0UL, 0u);
        uint boundary = SceneDataBuilder.PackNearFieldRevisions(
            65_534UL, 65_535u);
        uint wrapped = SceneDataBuilder.PackNearFieldRevisions(
            65_535UL, 65_536u);

        Assert.Multiple(() =>
        {
            Assert.That(first & 0xffffu, Is.EqualTo(1u));
            Assert.That(first >> 16, Is.EqualTo(1u));
            Assert.That(boundary & 0xffffu, Is.EqualTo(65_535u));
            Assert.That(boundary >> 16, Is.EqualTo(65_535u));
            Assert.That(wrapped & 0xffffu, Is.EqualTo(1u));
            Assert.That(wrapped >> 16, Is.EqualTo(1u));
        });
    }

    private static GPUSimpleDdgiNearFieldSurfaceEntry Entry(
        uint objectId,
        uint materialId,
        ushort objectRevision,
        ushort materialRevision) => new(
        objectId,
        materialId,
        objectRevision,
        materialRevision,
        SimpleDdgiNearFieldSurfaceFlags.Opaque |
        SimpleDdgiNearFieldSurfaceFlags.CoverageValid |
        SimpleDdgiNearFieldSurfaceFlags.MotionVectorsValid);
}
