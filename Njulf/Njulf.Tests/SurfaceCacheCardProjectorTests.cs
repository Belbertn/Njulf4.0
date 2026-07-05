using System.Numerics;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;

namespace Njulf.Tests;

[TestFixture]
public sealed class SurfaceCacheCardProjectorTests
{
    [Test]
    public void CreateCard_MapsTileAllocationAndObjectAxisMetadata()
    {
        var meshInfo = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-2.0f, -1.0f, -0.5f),
            BoundingBoxMax = new Vector3(2.0f, 3.0f, 1.5f)
        };

        var card = SurfaceCacheCardProjector.CreateCard(
            42,
            4,
            meshInfo,
            new SurfaceCacheAtlasAllocation(64, 96, 32),
            frameIndex: 17);

        Assert.Multiple(() =>
        {
            Assert.That(card.ObjectIndex, Is.EqualTo(42));
            Assert.That(card.Axis, Is.EqualTo(4));
            Assert.That(card.LastCaptureFrame, Is.EqualTo(17));
            Assert.That(card.AtlasRect.X, Is.EqualTo(64));
            Assert.That(card.AtlasRect.Y, Is.EqualTo(96));
            Assert.That(card.AtlasRect.Z, Is.EqualTo(32));
            Assert.That(card.AtlasRect.W, Is.EqualTo(32));
            Assert.That(card.WorldAxisNAndDepthRange.X, Is.EqualTo(0.0f).Within(1e-6f));
            Assert.That(card.WorldAxisNAndDepthRange.Y, Is.EqualTo(0.0f).Within(1e-6f));
            Assert.That(card.WorldAxisNAndDepthRange.Z, Is.EqualTo(1.0f).Within(1e-6f));
            Assert.That(card.WorldAxisNAndDepthRange.W, Is.EqualTo(2.0f).Within(1e-6f));
        });
    }

    [Test]
    public void ProjectToWorld_MapsCardCornersToMeshBounds()
    {
        var meshInfo = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-2.0f, -1.0f, -0.5f),
            BoundingBoxMax = new Vector3(2.0f, 3.0f, 1.5f)
        };

        var card = SurfaceCacheCardProjector.CreateCard(
            0,
            4,
            meshInfo,
            new SurfaceCacheAtlasAllocation(0, 0, 32),
            frameIndex: 0);

        var minCorner = SurfaceCacheCardProjector.ProjectToWorld(card, 0.0f, 0.0f, 0.0f);
        var maxCorner = SurfaceCacheCardProjector.ProjectToWorld(card, 1.0f, 1.0f, 1.0f);

        Assert.Multiple(() =>
        {
            Assert.That(minCorner.X, Is.EqualTo(-2.0f).Within(1e-5f));
            Assert.That(minCorner.Y, Is.EqualTo(-1.0f).Within(1e-5f));
            Assert.That(minCorner.Z, Is.EqualTo(-0.5f).Within(1e-5f));
            Assert.That(maxCorner.X, Is.EqualTo(2.0f).Within(1e-5f));
            Assert.That(maxCorner.Y, Is.EqualTo(3.0f).Within(1e-5f));
            Assert.That(maxCorner.Z, Is.EqualTo(1.5f).Within(1e-5f));
        });
    }

    [Test]
    public void CreateCard_WithWorldMatrix_ProjectsCardIntoInstanceSpace()
    {
        var meshInfo = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-1.0f, -1.0f, -1.0f),
            BoundingBoxMax = new Vector3(1.0f, 1.0f, 1.0f)
        };

        var card = SurfaceCacheCardProjector.CreateCard(
            7,
            4,
            meshInfo,
            CoreMatrix4x4.CreateTranslation(new Njulf.Core.Math.Vector3(10.0f, 2.0f, -3.0f)),
            new SurfaceCacheAtlasAllocation(0, 0, 32),
            frameIndex: 11);

        var minCorner = SurfaceCacheCardProjector.ProjectToWorld(card, 0.0f, 0.0f, 0.0f);
        var maxCorner = SurfaceCacheCardProjector.ProjectToWorld(card, 1.0f, 1.0f, 1.0f);

        Assert.Multiple(() =>
        {
            Assert.That(card.ObjectIndex, Is.EqualTo(7));
            Assert.That(minCorner.X, Is.EqualTo(9.0f).Within(1e-5f));
            Assert.That(minCorner.Y, Is.EqualTo(1.0f).Within(1e-5f));
            Assert.That(minCorner.Z, Is.EqualTo(-4.0f).Within(1e-5f));
            Assert.That(maxCorner.X, Is.EqualTo(11.0f).Within(1e-5f));
            Assert.That(maxCorner.Y, Is.EqualTo(3.0f).Within(1e-5f));
            Assert.That(maxCorner.Z, Is.EqualTo(-2.0f).Within(1e-5f));
        });
    }

    [Test]
    public void CalculateGridBounds_CoversPaddedCardMaximum()
    {
        var meshInfo = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-2.0f, -1.0f, -0.5f),
            BoundingBoxMax = new Vector3(2.0f, 3.0f, 1.5f)
        };
        GPUSurfaceCard card = SurfaceCacheCardProjector.CreateCard(
            0,
            4,
            meshInfo,
            new SurfaceCacheAtlasAllocation(0, 0, 32),
            frameIndex: 0);

        SurfaceCacheManager.CalculateGridBounds([card], out CoreVector3 gridMin, out float cellSize);

        CoreVector3 paddedMax = SurfaceCacheCardProjector.ProjectToWorld(card, 1.0f, 1.0f, 1.0f) + new CoreVector3(1.0f);
        int x = (int)MathF.Floor((paddedMax.X - gridMin.X) / cellSize);
        int y = (int)MathF.Floor((paddedMax.Y - gridMin.Y) / cellSize);
        int z = (int)MathF.Floor((paddedMax.Z - gridMin.Z) / cellSize);

        Assert.Multiple(() =>
        {
            Assert.That(x, Is.InRange(0, 23));
            Assert.That(y, Is.InRange(0, 23));
            Assert.That(z, Is.InRange(0, 23));
        });
    }

    [Test]
    public void ResolveCardTileSize_UsesLargerTilesForLargeProjectedSurfaces()
    {
        var largeWall = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-48.0f, -24.0f, -1.0f),
            BoundingBoxMax = new Vector3(48.0f, 24.0f, 1.0f)
        };
        var smallProp = new MeshInfo
        {
            BoundingBoxMin = new Vector3(-0.25f, -0.25f, -0.25f),
            BoundingBoxMax = new Vector3(0.25f, 0.25f, 0.25f)
        };

        int largeTile = SurfaceCacheManager.ResolveCardTileSize(largeWall, CoreMatrix4x4.Identity, 4, SurfaceCacheManager.MaxTileSize);
        int smallTile = SurfaceCacheManager.ResolveCardTileSize(smallProp, CoreMatrix4x4.Identity, 4, SurfaceCacheManager.MaxTileSize);

        Assert.Multiple(() =>
        {
            Assert.That(largeTile, Is.EqualTo(SurfaceCacheManager.MaxTileSize));
            Assert.That(smallTile, Is.EqualTo(SurfaceCacheManager.MinTileSize));
        });
    }
}
