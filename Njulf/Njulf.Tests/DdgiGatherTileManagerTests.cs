using System;
using System.Collections.Generic;
using Njulf.Core.Math;
using Njulf.Core.Scene;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class DdgiGatherTileManagerTests
    {
        [Test]
        public void BuildTiles_SelectsProjectedLocalVolumeAndClipmapPair()
        {
            DdgiFrameLayout layout = CreateLayout(
                new[]
                {
                    CreateVolume(new Vector3(-0.9f, -0.9f, -0.5f), new Vector3(0.7f, 1.8f, 1.0f)),
                    CreateVolume(new Vector3(-1.0f, -1.0f, -0.5f), new Vector3(2.0f, 2.0f, 1.0f)),
                    CreateVolume(new Vector3(-1.0f, -1.0f, -0.5f), new Vector3(2.0f, 2.0f, 1.0f))
                },
                new[]
                {
                    DdgiProbeVolumeRuntimeMetadata.Authored,
                    new DdgiProbeVolumeRuntimeMetadata(
                        DdgiProbeVolumeKind.CameraClipmap,
                        CascadeIndex: 0,
                        LogicalGridMinX: 0,
                        LogicalGridMinY: 0,
                        LogicalGridMinZ: 0,
                        RingOffsetX: 0,
                        RingOffsetY: 0,
                        RingOffsetZ: 0,
                        EdgeBlendFraction: 0.35f,
                        Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag),
                    new DdgiProbeVolumeRuntimeMetadata(
                        DdgiProbeVolumeKind.CameraClipmap,
                        CascadeIndex: 1,
                        LogicalGridMinX: 0,
                        LogicalGridMinY: 0,
                        LogicalGridMinZ: 0,
                        RingOffsetX: 0,
                        RingOffsetY: 0,
                        RingOffsetZ: 0,
                        EdgeBlendFraction: 0.0f,
                        Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
                });
            var tiles = new GPUDdgiGatherTile[4];

            DdgiGatherTileManager.BuildResult result = DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 32,
                screenHeight: 32,
                tiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.Header.TileCountX, Is.EqualTo(2u));
                Assert.That(result.Header.TileCountY, Is.EqualTo(2u));
                Assert.That(result.Header.TileSize, Is.EqualTo(DdgiGatherTileManager.TileSize));
                Assert.That(result.Header.Flags & DdgiGatherTileManager.HeaderEnabledFlag, Is.Not.Zero);
                Assert.That(result.TileCount, Is.EqualTo(4));
                Assert.That(result.SelectedLocalTileCount, Is.EqualTo(2));
                Assert.That(result.SelectedClipmapTileCount, Is.EqualTo(4));
                Assert.That(result.FallbackTileCount, Is.EqualTo(0));
                Assert.That(tiles, Has.All.Matches<GPUDdgiGatherTile>(tile =>
                    (tile.Flags & DdgiGatherTileManager.TilePrimaryClipmapValidFlag) != 0u));
                Assert.That(tiles[0].LocalVolumeIndex, Is.EqualTo(0u));
                Assert.That(tiles[0].PrimaryClipmapVolumeIndex, Is.EqualTo(1u));
                Assert.That(tiles[0].SecondaryClipmapVolumeIndex, Is.EqualTo(2u));
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileLocalVolumeValidFlag, Is.Not.Zero);
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TilePrimaryClipmapValidFlag, Is.Not.Zero);
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileSecondaryClipmapValidFlag, Is.Not.Zero);
                Assert.That(tiles[0].BlendWeights.X, Is.EqualTo(1.0f));
                Assert.That(tiles[0].BlendWeights.Y, Is.EqualTo(0.65f).Within(0.0001f));
                Assert.That(tiles[0].BlendWeights.Z, Is.EqualTo(0.35f).Within(0.0001f));
                Assert.That(tiles[1].LocalVolumeIndex, Is.EqualTo(DdgiGatherTileManager.InvalidVolumeIndex));
                Assert.That(tiles[1].PrimaryClipmapVolumeIndex, Is.EqualTo(1u));
            });
        }

        [Test]
        public void BuildTiles_MarksFallbackOnlyForTilesWithoutLocalOrClipmapCandidates()
        {
            DdgiFrameLayout layout = CreateLayout(
                new[]
                {
                    CreateVolume(new Vector3(-0.9f, -0.9f, -0.5f), new Vector3(0.7f, 1.8f, 1.0f))
                },
                new[]
                {
                    DdgiProbeVolumeRuntimeMetadata.Authored
                });
            var tiles = new GPUDdgiGatherTile[4];

            DdgiGatherTileManager.BuildResult result = DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 32,
                screenHeight: 32,
                tiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.TileCount, Is.EqualTo(4));
                Assert.That(result.SelectedLocalTileCount, Is.EqualTo(2));
                Assert.That(result.SelectedClipmapTileCount, Is.EqualTo(0));
                Assert.That(result.FallbackTileCount, Is.EqualTo(2));
                Assert.That(result.Header.Flags & DdgiGatherTileManager.HeaderEnabledFlag, Is.Not.Zero);

                Assert.That(tiles[0].LocalVolumeIndex, Is.EqualTo(0u));
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileLocalVolumeValidFlag, Is.Not.Zero);
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileFallbackFlag, Is.Zero);
                Assert.That(tiles[0].BlendWeights.X, Is.EqualTo(1.0f));
                Assert.That(tiles[0].BlendWeights.W, Is.EqualTo(0.0f));

                Assert.That(tiles[1].LocalVolumeIndex, Is.EqualTo(DdgiGatherTileManager.InvalidVolumeIndex));
                Assert.That(tiles[1].Flags & DdgiGatherTileManager.TileFallbackFlag, Is.Not.Zero);
                Assert.That(tiles[1].BlendWeights.W, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void BuildTiles_SelectsClipmapCandidateForAllTilesWhenCameraRelativeVolumesAreActive()
        {
            DdgiFrameLayout layout = CreateLayout(
                new[]
                {
                    CreateVolume(new Vector3(-1.0f, -1.0f, -0.5f), new Vector3(2.0f, 2.0f, 1.0f))
                },
                new[]
                {
                    new DdgiProbeVolumeRuntimeMetadata(
                        DdgiProbeVolumeKind.CameraClipmap,
                        CascadeIndex: 0,
                        LogicalGridMinX: 0,
                        LogicalGridMinY: 0,
                        LogicalGridMinZ: 0,
                        RingOffsetX: 0,
                        RingOffsetY: 0,
                        RingOffsetZ: 0,
                        EdgeBlendFraction: 0.0f,
                        Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
                });
            var tiles = new GPUDdgiGatherTile[4];

            DdgiGatherTileManager.BuildResult result = DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 32,
                screenHeight: 32,
                tiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.TileCount, Is.EqualTo(4));
                Assert.That(result.SelectedLocalTileCount, Is.EqualTo(0));
                Assert.That(result.SelectedClipmapTileCount, Is.EqualTo(4));
                Assert.That(result.FallbackTileCount, Is.EqualTo(0));
                Assert.That(tiles, Has.All.Matches<GPUDdgiGatherTile>(tile =>
                    tile.LocalVolumeIndex == DdgiGatherTileManager.InvalidVolumeIndex &&
                    tile.PrimaryClipmapVolumeIndex == 0u &&
                    (tile.Flags & DdgiGatherTileManager.TilePrimaryClipmapValidFlag) != 0u &&
                    (tile.Flags & DdgiGatherTileManager.TileFallbackFlag) == 0u &&
                    tile.BlendWeights.Y == 1.0f &&
                    tile.BlendWeights.W == 0.0f));
            });
        }

        [Test]
        public void BuildTiles_EncodesSupportReadinessInBlendWeights()
        {
            DdgiFrameLayout layout = CreateLayout(
                new[]
                {
                    CreateVolume(new Vector3(-0.9f, -0.9f, -0.5f), new Vector3(0.7f, 1.8f, 1.0f)),
                    CreateVolume(new Vector3(-1.0f, -1.0f, -0.5f), new Vector3(2.0f, 2.0f, 1.0f)),
                    CreateVolume(new Vector3(-1.0f, -1.0f, -0.5f), new Vector3(2.0f, 2.0f, 1.0f))
                },
                new[]
                {
                    DdgiProbeVolumeRuntimeMetadata.Authored,
                    new DdgiProbeVolumeRuntimeMetadata(
                        DdgiProbeVolumeKind.CameraClipmap,
                        CascadeIndex: 0,
                        LogicalGridMinX: 0,
                        LogicalGridMinY: 0,
                        LogicalGridMinZ: 0,
                        RingOffsetX: 0,
                        RingOffsetY: 0,
                        RingOffsetZ: 0,
                        EdgeBlendFraction: 0.25f,
                        Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag),
                    new DdgiProbeVolumeRuntimeMetadata(
                        DdgiProbeVolumeKind.CameraClipmap,
                        CascadeIndex: 1,
                        LogicalGridMinX: 0,
                        LogicalGridMinY: 0,
                        LogicalGridMinZ: 0,
                        RingOffsetX: 0,
                        RingOffsetY: 0,
                        RingOffsetZ: 0,
                        EdgeBlendFraction: 0.0f,
                        Flags: GlobalIlluminationProbeVolumeData.VolumeCameraRelativeFlag)
                });
            var tiles = new GPUDdgiGatherTile[4];

            DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 32,
                screenHeight: 32,
                tiles,
                new DdgiGatherTileManager.DdgiGatherSupportReadiness(0.5f, 0.8f, 0.25f));

            Assert.Multiple(() =>
            {
                Assert.That(tiles[0].BlendWeights.X, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(tiles[0].BlendWeights.Y, Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(tiles[0].BlendWeights.Z, Is.EqualTo(0.0625f).Within(0.0001f));
            });
        }

        [Test]
        public void BuildTiles_MarksFallbackWhenActiveLayoutHasNoCandidates()
        {
            DdgiFrameLayout layout = CreateLayout(Array.Empty<GlobalIlluminationProbeVolume>(), Array.Empty<DdgiProbeVolumeRuntimeMetadata>());
            var tiles = new GPUDdgiGatherTile[1];

            DdgiGatherTileManager.BuildResult result = DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 1,
                screenHeight: 1,
                tiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.TileCount, Is.EqualTo(1));
                Assert.That(result.FallbackTileCount, Is.EqualTo(1));
                Assert.That(result.Header.Flags & DdgiGatherTileManager.HeaderEnabledFlag, Is.Not.Zero);
                Assert.That(tiles[0].LocalVolumeIndex, Is.EqualTo(DdgiGatherTileManager.InvalidVolumeIndex));
                Assert.That(tiles[0].PrimaryClipmapVolumeIndex, Is.EqualTo(DdgiGatherTileManager.InvalidVolumeIndex));
                Assert.That(tiles[0].SecondaryClipmapVolumeIndex, Is.EqualTo(DdgiGatherTileManager.InvalidVolumeIndex));
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileFallbackFlag, Is.Not.Zero);
                Assert.That(tiles[0].BlendWeights.W, Is.EqualTo(1.0f));
            });
        }

        [Test]
        public void BuildTiles_DoesNotMarkFallbackWhenDdgiInactive()
        {
            DdgiFrameLayout layout = CreateLayout(
                Array.Empty<GlobalIlluminationProbeVolume>(),
                Array.Empty<DdgiProbeVolumeRuntimeMetadata>(),
                isDdgiActive: false);
            var tiles = new GPUDdgiGatherTile[1];

            DdgiGatherTileManager.BuildResult result = DdgiGatherTileManager.BuildTiles(
                layout,
                Matrix4x4.Identity,
                screenWidth: 1,
                screenHeight: 1,
                tiles);

            Assert.Multiple(() =>
            {
                Assert.That(result.TileCount, Is.EqualTo(1));
                Assert.That(result.FallbackTileCount, Is.EqualTo(0));
                Assert.That(result.Header.Flags & DdgiGatherTileManager.HeaderEnabledFlag, Is.Zero);
                Assert.That(tiles[0].Flags & DdgiGatherTileManager.TileFallbackFlag, Is.Zero);
                Assert.That(tiles[0].BlendWeights.W, Is.EqualTo(0.0f));
            });
        }

        private static DdgiFrameLayout CreateLayout(
            IReadOnlyList<GlobalIlluminationProbeVolume> volumes,
            IReadOnlyList<DdgiProbeVolumeRuntimeMetadata> metadata,
            bool isDdgiActive = true)
        {
            return new DdgiFrameLayout(
                volumes,
                metadata,
                Array.Empty<BoundingBox>(),
                Array.Empty<DdgiDirtyRegion>(),
                Array.Empty<DdgiFrameLayoutDirtyProbeRequest>(),
                isDdgiActive: isDdgiActive,
                cameraRelativeEnabled: true,
                defaultVolumeIncluded: false,
                authoredVolumeCount: 1,
                cameraRelativeCascadeCount: Math.Max(0, metadata.Count - 1),
                authoredProbeCount: 8,
                cameraRelativeProbeCount: 16,
                totalPhysicalProbeCount: 24);
        }

        private static GlobalIlluminationProbeVolume CreateVolume(Vector3 origin, Vector3 size)
        {
            return new GlobalIlluminationProbeVolume
            {
                Origin = origin,
                Size = size,
                ProbeCountX = 2,
                ProbeCountY = 2,
                ProbeCountZ = 2
            };
        }
    }
}
