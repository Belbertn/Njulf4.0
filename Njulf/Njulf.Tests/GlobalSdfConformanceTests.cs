using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Njulf.Core.Math;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using Silk.NET.Vulkan;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Tests;

[TestFixture]
public sealed class GlobalSdfConformanceTests
{
    private const float SdfDistanceEncodeVoxelRange = 32.0f;
    private const float VoxelSampleGateVoxels = 0.95f;
    private const float VoxelAxisSampleOffset = 0.45f;
    private const float VoxelCornerSampleOffset = 0.35f;
    private const float ValidationRoomWallThickness = 0.22f;
    private const float ValidationRoomWallGroundOverlap = 0.16f;
    private static readonly CoreVector3 UnitBoxMin = new(-0.5f, -0.5f, -0.5f);
    private static readonly CoreVector3 UnitBoxMax = new(0.5f, 0.5f, 0.5f);
    private static readonly CoreVector3 ConformanceWorldMin = new(-24.0f, -8.0f, -96.0f);

    [Test]
    public void GlobalSdfUpdateShaderMirror_IsPinnedToProductionBakeMath()
    {
        string update = ReadRepoText("Njulf.Shaders", "global_sdf_update.comp");
        string common = ReadRepoText("Njulf.Shaders", "common.glsl");
        string sampleMeshSdf = ExtractFunction(update, "float SampleMeshSdf(GPUMeshSdf");
        string composeDistance = ExtractFunction(update, "float ComposeGlobalSdfDistance(");
        string conservativeDistance = ExtractFunction(update, "float ComposeConservativeGlobalSdfVoxelDistance(");

        Assert.Multiple(() =>
        {
            Assert.That(common, Does.Contain("const uint MESH_SDF_FLAG_ANALYTIC_BOX = 1u << 1;"));
            Assert.That(common, Does.Contain("const float SDF_DISTANCE_ENCODE_VOXEL_RANGE = 32.0;"));
            Assert.That(sampleMeshSdf, Does.Contain("TransformWorldToMeshSdfLocal(meshSdf, worldPosition)"));
            Assert.That(sampleMeshSdf, Does.Contain("(meshSdf.Flags & MESH_SDF_FLAG_ANALYTIC_BOX) != 0u"));
            Assert.That(sampleMeshSdf, Does.Contain("return SignedDistanceToScaledLocalAabb(localPosition, meshLocalMin, meshLocalMax, localToWorldScale);"));
            Assert.That(sampleMeshSdf, Does.Contain("return length(outside * localToWorldScale);"));
            Assert.That(sampleMeshSdf, Does.Contain("float minAxisScale = min(localToWorldScale.x, min(localToWorldScale.y, localToWorldScale.z));"));
            Assert.That(sampleMeshSdf, Does.Contain("return DecodeSdfDistance(normalizedDistance, localVoxelSize) * minAxisScale;"));
            Assert.That(composeDistance, Does.Contain("DistanceToBoundingSphere(worldPosition, SharedMeshSdfBoundsCenterRadius[candidateIndex])"));
            Assert.That(composeDistance, Does.Contain("if (distanceMeters >= 0.0 && boundsDistance >= distanceMeters)"));
            Assert.That(conservativeDistance, Does.Contain("abs(centerDistance) > voxelSize * GLOBAL_SDF_VOXEL_SAMPLE_GATE_VOXELS"));
            Assert.That(conservativeDistance, Does.Contain("float axisOffset = voxelSize * GLOBAL_SDF_VOXEL_AXIS_SAMPLE_OFFSET;"));
            Assert.That(conservativeDistance, Does.Contain("float cornerOffset = voxelSize * GLOBAL_SDF_VOXEL_CORNER_SAMPLE_OFFSET;"));
            Assert.That(update, Does.Contain("float safeBound = max(0.0, min(distanceToBrickSurface.x, min(distanceToBrickSurface.y, distanceToBrickSurface.z))) + paddingMeters;"));
            Assert.That(update, Does.Contain("distanceMeters = min(distanceMeters, safeBound);"));
        });
    }

    [Test]
    public void SampleMeshSdfMirror_CoversAnalyticBoxOutsideBoundsAndTextureScalePaths()
    {
        GPUMeshSdf analytic = CreateBakedUnitBoxRecord(flags: MeshSdfBakePlanner.MeshSdfFlagAnalyticBox);
        CoreMatrix4x4 analyticWorld =
            CoreMatrix4x4.CreateScale(new CoreVector3(2.0f, 3.0f, 4.0f)) *
            CoreMatrix4x4.CreateRotationY(0.35f) *
            CoreMatrix4x4.CreateTranslation(new CoreVector3(10.0f, 2.0f, -6.0f));
        Assert.That(MeshSdfManager.TryCreateInstanceGpuRecord(analytic, analyticWorld, out GPUMeshSdf analyticInstance), Is.True);

        GPUMeshSdf nonAnalytic = CreateBakedUnitBoxRecord(flags: 0u);
        Assert.That(MeshSdfManager.TryCreateInstanceGpuRecord(nonAnalytic, analyticWorld, out GPUMeshSdf nonAnalyticInstance), Is.True);

        CoreVector3 localOutside = new(0.75f, 0.0f, 0.0f);
        CoreVector3 worldOutside = localOutside * analyticWorld;
        CoreVector3 localInside = new(0.0f, 0.0f, 0.0f);
        CoreVector3 worldInside = localInside * analyticWorld;

        float analyticDistance = SampleMeshSdf(analyticInstance, worldOutside, static _ => throw new InvalidOperationException());
        float outsideBoundsDistance = SampleMeshSdf(nonAnalyticInstance, (new CoreVector3(0.75f, 0.75f, 0.75f) * analyticWorld), static _ => 0.0f);
        float decodedTextureDistance = SampleMeshSdf(nonAnalyticInstance, worldInside, static _ => 0.5f);

        Assert.Multiple(() =>
        {
            Assert.That(analyticDistance, Is.EqualTo(0.5f).Within(1.0e-4f));
            Assert.That(outsideBoundsDistance, Is.GreaterThan(0.0f));
            Assert.That(decodedTextureDistance, Is.EqualTo(DecodeSdfDistance(0.5f, nonAnalytic.LocalBoundsMinAndVoxelSize.W) * 2.0f).Within(1.0e-4f));
        });
    }

    [TestCase(0.125f)]
    [TestCase(0.25f)]
    [TestCase(0.5f)]
    [TestCase(1.0f)]
    public void GiSdfCascadeField_ConservativeBakeMatchesAnalyticBoxOracle(float voxelSize)
    {
        List<BoxInstance> boxes = CreateGiSdfCascadeFieldBoxes();
        Assert.That(boxes, Has.Count.EqualTo(40));

        List<CandidateInstance> candidates = CreateCandidateInstances(boxes);
        List<ConformanceFailure> failures = new();
        ErrorAccumulator accumulator = new();

        foreach (CoreVector3 worldPosition in EnumerateVoxelCentersAroundBoxes(boxes, voxelSize))
        {
            DistanceResult reference = EvaluateReferenceScene(boxes, worldPosition);
            DistanceResult actual = ComposeStoredGlobalSdfVoxelDistance(candidates, worldPosition, voxelSize);
            float error = actual.Distance - reference.Distance;
            bool nearSurface = MathF.Abs(reference.Distance) < voxelSize * 2.0f;

            accumulator.Observe(reference.Distance, actual.Distance, actual.InstanceName, worldPosition, nearSurface);

            if (nearSurface && MathF.Abs(error) > voxelSize * 1.05f)
            {
                failures.Add(ConformanceFailure.NearSurface(worldPosition, reference, actual, error));
                continue;
            }

            if (actual.Distance < 0.0f && actual.Distance < reference.Distance - voxelSize * 1.05f)
            {
                failures.Add(ConformanceFailure.PhantomInterior(worldPosition, reference, actual, error));
                continue;
            }

            if (nearSurface && actual.Distance > reference.Distance + voxelSize * 1.05f)
                failures.Add(ConformanceFailure.Tunneling(worldPosition, reference, actual, error));
        }

        if (failures.Count > 0)
            Assert.Fail(BuildFailureReport(voxelSize, failures, accumulator));

        Assert.Multiple(() =>
        {
            Assert.That(accumulator.SampleCount, Is.GreaterThan(10_000), "conformance sample coverage");
            Assert.That(accumulator.NearSurfaceSampleCount, Is.GreaterThan(500), "near-surface sample coverage");
            Assert.That(accumulator.MaxNearSurfaceAbsError, Is.LessThanOrEqualTo(voxelSize * 1.05f), accumulator.WorstNearSurfaceMessage);
            Assert.That(accumulator.MaxNegativeOvershoot, Is.LessThanOrEqualTo(voxelSize * 1.05f), accumulator.WorstNegativeOvershootMessage);
            Assert.That(accumulator.MaxNearSurfacePositiveOvershoot, Is.LessThanOrEqualTo(voxelSize * 1.05f), accumulator.WorstPositiveOvershootMessage);
        });
    }

    private static DistanceResult ComposeStoredGlobalSdfVoxelDistance(
        IReadOnlyList<CandidateInstance> allInstances,
        CoreVector3 worldPosition,
        float voxelSize)
    {
        List<CandidateInstance> brickCandidates = GatherBrickCandidates(allInstances, worldPosition, voxelSize);
        float distance = ComposeConservativeGlobalSdfVoxelDistance(brickCandidates, worldPosition, voxelSize, out string? contributingInstance);
        float safeBound = CalculateSafeBound(worldPosition, voxelSize);

        if (brickCandidates.Count == 0 || distance > 1.0e10f)
            distance = safeBound;
        else
            distance = MathF.Min(distance, safeBound);

        return new DistanceResult(distance, contributingInstance ?? "<safe-bound>");
    }

    private static List<CandidateInstance> GatherBrickCandidates(
        IReadOnlyList<CandidateInstance> allInstances,
        CoreVector3 worldPosition,
        float voxelSize)
    {
        float brickWorldSize = voxelSize * GlobalSdfManager.BrickSize;
        CoreVector3 brickWorldMin = GetBrickWorldMin(worldPosition, voxelSize);
        CoreVector3 brickWorldMax = brickWorldMin + new CoreVector3(brickWorldSize);
        CoreVector3 brickPadding = new(voxelSize * 4.0f);
        CoreVector3 meshPadding = new(voxelSize);

        List<CandidateInstance> result = new();
        for (int i = 0; i < allInstances.Count; i++)
        {
            CandidateInstance candidate = allInstances[i];
            BoundingBox meshCullBounds = new(candidate.WorldBounds.Min - meshPadding, candidate.WorldBounds.Max + meshPadding);
            if (!AabbIntersects(brickWorldMin - brickPadding, brickWorldMax + brickPadding, meshCullBounds.Min, meshCullBounds.Max))
                continue;

            CoreVector3 boundsCenter = (meshCullBounds.Min + meshCullBounds.Max) * 0.5f;
            float boundsRadius = (meshCullBounds.Max - meshCullBounds.Min).Length() * 0.5f;
            result.Add(candidate with { BoundsCenterRadius = new CoreVector4(boundsCenter.X, boundsCenter.Y, boundsCenter.Z, boundsRadius) });
        }

        return result;
    }

    private static float ComposeConservativeGlobalSdfVoxelDistance(
        IReadOnlyList<CandidateInstance> candidates,
        CoreVector3 worldPosition,
        float voxelSize,
        out string? contributingInstance)
    {
        DistanceResult center = ComposeGlobalSdfDistance(candidates, worldPosition);
        contributingInstance = center.InstanceName;
        if (candidates.Count == 0 || MathF.Abs(center.Distance) > voxelSize * VoxelSampleGateVoxels)
            return center.Distance;

        float bestPositiveDistance = center.Distance >= 0.0f ? center.Distance : 1.0e20f;
        float bestNegativeAbsDistance = center.Distance < 0.0f ? MathF.Abs(center.Distance) : 1.0e20f;
        float bestNegativeDistance = center.Distance < 0.0f ? center.Distance : 1.0e20f;
        string? bestPositiveInstance = center.Distance >= 0.0f ? center.InstanceName : null;
        string? bestNegativeInstance = center.Distance < 0.0f ? center.InstanceName : null;

        void Accumulate(CoreVector3 samplePosition)
        {
            DistanceResult sample = ComposeGlobalSdfDistance(candidates, samplePosition);
            if (sample.Distance < 0.0f)
            {
                float absDistance = MathF.Abs(sample.Distance);
                if (absDistance < bestNegativeAbsDistance)
                {
                    bestNegativeAbsDistance = absDistance;
                    bestNegativeDistance = sample.Distance;
                    bestNegativeInstance = sample.InstanceName;
                }
            }
            else if (sample.Distance < bestPositiveDistance)
            {
                bestPositiveDistance = sample.Distance;
                bestPositiveInstance = sample.InstanceName;
            }
        }

        float axisOffset = voxelSize * VoxelAxisSampleOffset;
        Accumulate(worldPosition + new CoreVector3(axisOffset, 0.0f, 0.0f));
        Accumulate(worldPosition - new CoreVector3(axisOffset, 0.0f, 0.0f));
        Accumulate(worldPosition + new CoreVector3(0.0f, axisOffset, 0.0f));
        Accumulate(worldPosition - new CoreVector3(0.0f, axisOffset, 0.0f));
        Accumulate(worldPosition + new CoreVector3(0.0f, 0.0f, axisOffset));
        Accumulate(worldPosition - new CoreVector3(0.0f, 0.0f, axisOffset));

        float cornerOffset = voxelSize * VoxelCornerSampleOffset;
        for (int z = -1; z <= 1; z += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int x = -1; x <= 1; x += 2)
                    Accumulate(worldPosition + new CoreVector3(x * cornerOffset, y * cornerOffset, z * cornerOffset));
            }
        }

        if (bestNegativeAbsDistance < 1.0e19f)
        {
            contributingInstance = bestNegativeInstance;
            return bestNegativeDistance;
        }

        if (bestPositiveDistance < 1.0e19f)
        {
            contributingInstance = bestPositiveInstance;
            return bestPositiveDistance;
        }

        contributingInstance = center.InstanceName;
        return center.Distance;
    }

    private static DistanceResult ComposeGlobalSdfDistance(IReadOnlyList<CandidateInstance> candidates, CoreVector3 worldPosition)
    {
        float distanceMeters = 1.0e20f;
        int nearestCandidateIndex = candidates.Count;
        float nearestCandidateBoundsDistance = 1.0e20f;
        string? contributingInstance = null;

        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            float boundsDistance = DistanceToBoundingSphere(worldPosition, candidates[candidateIndex].BoundsCenterRadius);
            if (boundsDistance < nearestCandidateBoundsDistance)
            {
                nearestCandidateBoundsDistance = boundsDistance;
                nearestCandidateIndex = candidateIndex;
            }
        }

        if (nearestCandidateIndex < candidates.Count)
        {
            CandidateInstance nearest = candidates[nearestCandidateIndex];
            distanceMeters = SampleMeshSdf(nearest.GpuRecord, worldPosition, static _ => 0.0f);
            contributingInstance = nearest.Name;
        }

        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            if (candidateIndex == nearestCandidateIndex)
                continue;

            CandidateInstance candidate = candidates[candidateIndex];
            float boundsDistance = DistanceToBoundingSphere(worldPosition, candidate.BoundsCenterRadius);
            if (distanceMeters >= 0.0f && boundsDistance >= distanceMeters)
                continue;

            float candidateDistance = SampleMeshSdf(candidate.GpuRecord, worldPosition, static _ => 0.0f);
            if (candidateDistance < distanceMeters)
            {
                distanceMeters = candidateDistance;
                contributingInstance = candidate.Name;
            }
        }

        return new DistanceResult(distanceMeters, contributingInstance ?? "<none>");
    }

    private static float SampleMeshSdf(
        GPUMeshSdf meshSdf,
        CoreVector3 worldPosition,
        Func<CoreVector3, float> sampleNormalizedDistance)
    {
        CoreVector3 localPosition = TransformWorldToMeshSdfLocal(meshSdf, worldPosition);
        CoreVector3 localMin = meshSdf.LocalBoundsMinAndVoxelSize.Xyz();
        float localVoxelSize = MathF.Max(meshSdf.LocalBoundsMinAndVoxelSize.W, 0.0001f);
        CoreVector3 localExtent = Max(meshSdf.LocalBoundsExtentAndInvVoxelSize.Xyz(), new CoreVector3(0.0001f));
        CoreVector3 localMax = localMin + localExtent;
        CoreVector3 localToWorldScale = Max(meshSdf.LocalToWorldAxisScale.Xyz(), new CoreVector3(0.0001f));
        CoreVector3 meshLocalMin = localMin + new CoreVector3(localVoxelSize);
        CoreVector3 meshLocalMax = localMax - new CoreVector3(localVoxelSize);

        if ((meshSdf.Flags & MeshSdfBakePlanner.MeshSdfFlagAnalyticBox) != 0u && GreaterThanOrEqual(meshLocalMax, meshLocalMin))
            return SignedDistanceToScaledLocalAabb(localPosition, meshLocalMin, meshLocalMax, localToWorldScale);

        CoreVector3 uvw = (localPosition - localMin) / localExtent;
        if (AnyLessThan(uvw, CoreVector3.Zero) || AnyGreaterThan(uvw, CoreVector3.One))
        {
            CoreVector3 outside = Max(Max(localMin - localPosition, localPosition - localMax), CoreVector3.Zero);
            return (outside * localToWorldScale).Length();
        }

        if (GreaterThanOrEqual(meshLocalMax, meshLocalMin))
        {
            CoreVector3 outsideMeshBounds = Max(Max(meshLocalMin - localPosition, localPosition - meshLocalMax), CoreVector3.Zero);
            if (outsideMeshBounds.LengthSquared() > 0.0f)
                return (outsideMeshBounds * localToWorldScale).Length();
        }

        float normalizedDistance = sampleNormalizedDistance(uvw);
        float minAxisScale = MathF.Min(localToWorldScale.X, MathF.Min(localToWorldScale.Y, localToWorldScale.Z));
        return DecodeSdfDistance(normalizedDistance, localVoxelSize) * minAxisScale;
    }

    private static DistanceResult EvaluateReferenceScene(IReadOnlyList<BoxInstance> boxes, CoreVector3 worldPosition)
    {
        float best = 1.0e20f;
        string bestName = "<none>";
        for (int i = 0; i < boxes.Count; i++)
        {
            BoxInstance box = boxes[i];
            CoreVector3 localPosition = worldPosition * box.WorldToLocal;
            float distance = SignedDistanceToScaledLocalAabb(localPosition, UnitBoxMin, UnitBoxMax, box.Scale);
            if (distance < best)
            {
                best = distance;
                bestName = box.Name;
            }
        }

        return new DistanceResult(best, bestName);
    }

    private static List<CandidateInstance> CreateCandidateInstances(IReadOnlyList<BoxInstance> boxes)
    {
        GPUMeshSdf bakedRecord = CreateBakedUnitBoxRecord(MeshSdfBakePlanner.MeshSdfFlagAnalyticBox);
        List<CandidateInstance> result = new(boxes.Count);
        for (int i = 0; i < boxes.Count; i++)
        {
            BoxInstance box = boxes[i];
            Assert.That(
                MeshSdfManager.TryCreateInstanceGpuRecord(bakedRecord, box.WorldMatrix, out GPUMeshSdf instanceRecord),
                Is.True,
                $"Failed to create mesh SDF instance record for {box.Name}");
            result.Add(new CandidateInstance(box.Name, instanceRecord, CreateWorldBounds(instanceRecord), default));
        }

        return result;
    }

    private static GPUMeshSdf CreateBakedUnitBoxRecord(uint flags)
    {
        MeshSdfBakeDescriptor descriptor = MeshSdfBakePlanner.CreateDescriptor(new MeshInfo
        {
            BoundingBoxMin = new NumericsVector3(-0.5f, -0.5f, -0.5f),
            BoundingBoxMax = new NumericsVector3(0.5f, 0.5f, 0.5f),
            VertexCount = 24,
            IndexCount = 36
        });

        return new GPUMeshSdf
        {
            LocalBoundsMinAndVoxelSize = new CoreVector4(descriptor.BoundsMin.X, descriptor.BoundsMin.Y, descriptor.BoundsMin.Z, descriptor.VoxelSize),
            LocalBoundsExtentAndInvVoxelSize = new CoreVector4(descriptor.BoundsExtent.X, descriptor.BoundsExtent.Y, descriptor.BoundsExtent.Z, descriptor.InvVoxelSize),
            WorldBoundsMinAndLocalScaleX = new CoreVector4(descriptor.BoundsMin.X, descriptor.BoundsMin.Y, descriptor.BoundsMin.Z, 1.0f),
            WorldBoundsMaxAndLocalScaleY = new CoreVector4(descriptor.BoundsMax.X, descriptor.BoundsMax.Y, descriptor.BoundsMax.Z, 1.0f),
            WorldToLocalRow0 = new CoreVector4(1.0f, 0.0f, 0.0f, 0.0f),
            WorldToLocalRow1 = new CoreVector4(0.0f, 1.0f, 0.0f, 0.0f),
            WorldToLocalRow2 = new CoreVector4(0.0f, 0.0f, 1.0f, 0.0f),
            LocalToWorldAxisScale = new CoreVector4(1.0f, 1.0f, 1.0f, 1.0f),
            TextureIndex = 0,
            ResolutionX = descriptor.Extent.Width,
            ResolutionY = descriptor.Extent.Height,
            ResolutionZ = descriptor.Extent.Depth,
            VertexCount = 24,
            IndexCount = 36,
            Flags = flags
        };
    }

    private static IEnumerable<CoreVector3> EnumerateVoxelCentersAroundBoxes(IReadOnlyList<BoxInstance> boxes, float voxelSize)
    {
        var seen = new HashSet<VoxelKey>();
        int strideVoxels = voxelSize <= 0.125f ? 2 : 1;
        for (int i = 0; i < boxes.Count; i++)
        {
            BoundingBox bounds = boxes[i].WorldAabb;
            CoreVector3 padding = new(voxelSize * 2.0f);
            int minX = VoxelIndexFloorX(bounds.Min.X - padding.X, voxelSize);
            int minY = VoxelIndexFloorY(bounds.Min.Y - padding.Y, voxelSize);
            int minZ = VoxelIndexFloorZ(bounds.Min.Z - padding.Z, voxelSize);
            int maxX = VoxelIndexFloorX(bounds.Max.X + padding.X, voxelSize);
            int maxY = VoxelIndexFloorY(bounds.Max.Y + padding.Y, voxelSize);
            int maxZ = VoxelIndexFloorZ(bounds.Max.Z + padding.Z, voxelSize);

            for (int z = minZ; z <= maxZ; z += strideVoxels)
            {
                for (int y = minY; y <= maxY; y += strideVoxels)
                {
                    for (int x = minX; x <= maxX; x += strideVoxels)
                    {
                        var key = new VoxelKey(x, y, z);
                        if (!seen.Add(key))
                            continue;

                        yield return ConformanceWorldMin + new CoreVector3(
                            (x + 0.5f) * voxelSize,
                            (y + 0.5f) * voxelSize,
                            (z + 0.5f) * voxelSize);
                    }
                }
            }
        }
    }

    private static int VoxelIndexFloorX(float worldX, float voxelSize) =>
        (int)MathF.Floor((worldX - ConformanceWorldMin.X) / voxelSize - 0.5f);

    private static int VoxelIndexFloorY(float worldY, float voxelSize) =>
        (int)MathF.Floor((worldY - ConformanceWorldMin.Y) / voxelSize - 0.5f);

    private static int VoxelIndexFloorZ(float worldZ, float voxelSize) =>
        (int)MathF.Floor((worldZ - ConformanceWorldMin.Z) / voxelSize - 0.5f);

    private static List<BoxInstance> CreateGiSdfCascadeFieldBoxes()
    {
        List<BoxInstance> boxes = new();

        AddSolidBox(boxes, "GI.SdfCascadeField.Foundation", new CoreVector3(0.0f, -0.08f, -44.0f), new CoreVector3(34.0f, 0.16f, 92.0f));

        AddRoom(boxes, "GI.SdfCascadeField.NearRoom", centerZ: -8.0f, width: 9.0f, height: 4.2f, depth: 10.0f, includeFrontWall: false, centerX: -4.5f, includeFloor: false);
        AddRoom(boxes, "GI.SdfCascadeField.MidRoom", centerZ: -34.0f, width: 10.0f, height: 5.0f, depth: 13.0f, includeFrontWall: false, centerX: 5.0f, includeFloor: false);
        AddRoom(boxes, "GI.SdfCascadeField.FarRoom", centerZ: -72.0f, width: 13.0f, height: 6.0f, depth: 16.0f, includeFrontWall: false, centerX: -2.0f, includeFloor: false);

        AddSolidBox(boxes, "GI.SdfCascadeField.LeftBoundary", new CoreVector3(-17.0f, 2.0f, -44.0f), new CoreVector3(0.32f, 4.0f, 86.0f));
        AddSolidBox(boxes, "GI.SdfCascadeField.RightBoundary", new CoreVector3(17.0f, 2.0f, -44.0f), new CoreVector3(0.32f, 4.0f, 86.0f));

        for (int i = 0; i < 12; i++)
        {
            float z = -13.0f - i * 5.4f;
            float x = (i % 2 == 0 ? -7.5f : 7.5f) + ((i * 37) % 5 - 2) * 0.35f;
            AddBox(
                boxes,
                $"GI.SdfCascadeField.Occluder.{i}",
                new CoreVector3(x, 0.9f + 0.08f * (i % 3), z),
                new CoreVector3(1.4f + 0.25f * (i % 4), 1.8f + 0.25f * (i % 2), 0.55f),
                i % 2 == 0 ? 0.32f : -0.38f);
        }

        for (int i = 0; i < 10; i++)
        {
            float z = -11.0f - i * 7.0f;
            float x = -12.0f + (i % 5) * 6.0f;
            AddSolidBox(boxes, $"GI.SdfCascadeField.Pillar.{i}", new CoreVector3(x, 1.45f, z), new CoreVector3(0.65f, 2.9f, 0.65f));
        }

        AddWall(boxes, "GI.SdfCascadeField.NearAmberPanel", new CoreVector3(-7.8f, 2.0f, -12.8f), CoreMatrix4x4.CreateRotationY(MathF.PI * 0.5f), new CoreVector3(1.6f, 1.25f, 1.0f));
        AddWall(boxes, "GI.SdfCascadeField.MidBluePanel", new CoreVector3(9.9f, 2.5f, -40.0f), CoreMatrix4x4.CreateRotationY(-MathF.PI * 0.5f), new CoreVector3(1.8f, 1.4f, 1.0f));
        AddWall(boxes, "GI.SdfCascadeField.FarAmberPanel", new CoreVector3(-8.7f, 3.0f, -78.0f), CoreMatrix4x4.CreateRotationY(MathF.PI * 0.5f), new CoreVector3(2.2f, 1.6f, 1.0f));

        return boxes;
    }

    private static void AddRoom(
        List<BoxInstance> boxes,
        string prefix,
        float centerZ,
        float width,
        float height,
        float depth,
        bool includeFrontWall,
        float centerX,
        bool includeFloor)
    {
        float leftX = centerX - width * 0.5f;
        float rightX = centerX + width * 0.5f;
        float backZ = centerZ - depth * 0.5f;
        float frontZ = centerZ + depth * 0.5f;
        float shellThickness = ValidationRoomWallThickness;
        float wallHeight = height + ValidationRoomWallGroundOverlap;
        float wallCenterY = (height - ValidationRoomWallGroundOverlap) * 0.5f;

        if (includeFloor)
            AddSolidBox(boxes, $"{prefix}.Floor", new CoreVector3(centerX, -shellThickness * 0.5f, centerZ), new CoreVector3(width + shellThickness * 2.0f, shellThickness, depth + shellThickness * 2.0f));

        AddSolidBox(boxes, $"{prefix}.Ceiling", new CoreVector3(centerX, height + shellThickness * 0.5f, centerZ), new CoreVector3(width + shellThickness * 2.0f, shellThickness, depth + shellThickness * 2.0f));
        AddSolidBox(boxes, $"{prefix}.BackWall", new CoreVector3(centerX, wallCenterY, backZ - shellThickness * 0.5f), new CoreVector3(width + shellThickness * 2.0f, wallHeight, shellThickness));
        AddSolidBox(boxes, $"{prefix}.LeftWall", new CoreVector3(leftX - shellThickness * 0.5f, wallCenterY, centerZ), new CoreVector3(shellThickness, wallHeight, depth));
        AddSolidBox(boxes, $"{prefix}.RightWall", new CoreVector3(rightX + shellThickness * 0.5f, wallCenterY, centerZ), new CoreVector3(shellThickness, wallHeight, depth));

        if (includeFrontWall)
            AddSolidBox(boxes, $"{prefix}.FrontWall", new CoreVector3(centerX, wallCenterY, frontZ + shellThickness * 0.5f), new CoreVector3(width + shellThickness * 2.0f, wallHeight, shellThickness));
    }

    private static void AddSolidBox(List<BoxInstance> boxes, string name, CoreVector3 position, CoreVector3 scale) =>
        AddTransformedBox(boxes, name, CoreMatrix4x4.CreateScale(scale) * CoreMatrix4x4.CreateTranslation(position), scale);

    private static void AddWall(List<BoxInstance> boxes, string name, CoreVector3 position, CoreMatrix4x4 rotation, CoreVector3 scale)
    {
        CoreVector3 solidScale = new(
            MathF.Max(scale.X, ValidationRoomWallThickness),
            MathF.Max(scale.Y, ValidationRoomWallThickness),
            ValidationRoomWallThickness);
        AddTransformedBox(boxes, name, CoreMatrix4x4.CreateScale(solidScale) * rotation * CoreMatrix4x4.CreateTranslation(position), solidScale);
    }

    private static void AddBox(List<BoxInstance> boxes, string name, CoreVector3 position, CoreVector3 scale, float rotationY) =>
        AddTransformedBox(boxes, name, CoreMatrix4x4.CreateScale(scale) * CoreMatrix4x4.CreateRotationY(rotationY) * CoreMatrix4x4.CreateTranslation(position), scale);

    private static void AddTransformedBox(List<BoxInstance> boxes, string name, CoreMatrix4x4 worldMatrix, CoreVector3 scale)
    {
        boxes.Add(new BoxInstance(
            name,
            worldMatrix,
            worldMatrix.Invert(),
            scale,
            TransformUnitBoxBounds(worldMatrix)));
    }

    private static float SignedDistanceToScaledLocalAabb(
        CoreVector3 point,
        CoreVector3 boundsMin,
        CoreVector3 boundsMax,
        CoreVector3 localToWorldScale)
    {
        CoreVector3 outside = Max(Max(boundsMin - point, point - boundsMax), CoreVector3.Zero);
        float outsideDistance = (outside * localToWorldScale).Length();
        if (outsideDistance > 0.0f)
            return outsideDistance;

        CoreVector3 insideDistance = Min(point - boundsMin, boundsMax - point) * localToWorldScale;
        return -MathF.Min(insideDistance.X, MathF.Min(insideDistance.Y, insideDistance.Z));
    }

    private static CoreVector3 TransformWorldToMeshSdfLocal(GPUMeshSdf meshSdf, CoreVector3 worldPosition) =>
        new(
            CoreVector3.Dot(worldPosition, new CoreVector3(meshSdf.WorldToLocalRow0.X, meshSdf.WorldToLocalRow1.X, meshSdf.WorldToLocalRow2.X)) + meshSdf.WorldToLocalRow0.W,
            CoreVector3.Dot(worldPosition, new CoreVector3(meshSdf.WorldToLocalRow0.Y, meshSdf.WorldToLocalRow1.Y, meshSdf.WorldToLocalRow2.Y)) + meshSdf.WorldToLocalRow1.W,
            CoreVector3.Dot(worldPosition, new CoreVector3(meshSdf.WorldToLocalRow0.Z, meshSdf.WorldToLocalRow1.Z, meshSdf.WorldToLocalRow2.Z)) + meshSdf.WorldToLocalRow2.W);

    private static BoundingBox TransformUnitBoxBounds(CoreMatrix4x4 worldMatrix)
    {
        CoreVector3[] corners =
        [
            new(-0.5f, -0.5f, -0.5f),
            new(0.5f, -0.5f, -0.5f),
            new(-0.5f, 0.5f, -0.5f),
            new(0.5f, 0.5f, -0.5f),
            new(-0.5f, -0.5f, 0.5f),
            new(0.5f, -0.5f, 0.5f),
            new(-0.5f, 0.5f, 0.5f),
            new(0.5f, 0.5f, 0.5f)
        ];

        CoreVector3 min = new(float.MaxValue);
        CoreVector3 max = new(float.MinValue);
        for (int i = 0; i < corners.Length; i++)
        {
            CoreVector3 transformed = corners[i] * worldMatrix;
            min = CoreVector3.Min(min, transformed);
            max = CoreVector3.Max(max, transformed);
        }

        return new BoundingBox(min, max);
    }

    private static BoundingBox CreateWorldBounds(GPUMeshSdf instanceRecord)
    {
        return new BoundingBox(
            new CoreVector3(
                instanceRecord.WorldBoundsMinAndLocalScaleX.X,
                instanceRecord.WorldBoundsMinAndLocalScaleX.Y,
                instanceRecord.WorldBoundsMinAndLocalScaleX.Z),
            new CoreVector3(
                instanceRecord.WorldBoundsMaxAndLocalScaleY.X,
                instanceRecord.WorldBoundsMaxAndLocalScaleY.Y,
                instanceRecord.WorldBoundsMaxAndLocalScaleY.Z));
    }

    private static float CalculateSafeBound(CoreVector3 worldPosition, float voxelSize)
    {
        CoreVector3 brickWorldMin = GetBrickWorldMin(worldPosition, voxelSize);
        CoreVector3 brickWorldMax = brickWorldMin + new CoreVector3(voxelSize * GlobalSdfManager.BrickSize);
        CoreVector3 distanceToBrickSurface = Min(worldPosition - brickWorldMin, brickWorldMax - worldPosition);
        return MathF.Max(0.0f, MathF.Min(distanceToBrickSurface.X, MathF.Min(distanceToBrickSurface.Y, distanceToBrickSurface.Z))) + voxelSize * 4.0f;
    }

    private static CoreVector3 GetBrickWorldMin(CoreVector3 worldPosition, float voxelSize)
    {
        float brickWorldSize = voxelSize * GlobalSdfManager.BrickSize;
        return ConformanceWorldMin + new CoreVector3(
            MathF.Floor((worldPosition.X - ConformanceWorldMin.X) / brickWorldSize) * brickWorldSize,
            MathF.Floor((worldPosition.Y - ConformanceWorldMin.Y) / brickWorldSize) * brickWorldSize,
            MathF.Floor((worldPosition.Z - ConformanceWorldMin.Z) / brickWorldSize) * brickWorldSize);
    }

    private static float DistanceToBoundingSphere(CoreVector3 point, CoreVector4 centerRadius) =>
        MathF.Max((point - centerRadius.Xyz()).Length() - centerRadius.W, 0.0f);

    private static float DecodeSdfDistance(float normalizedDistance, float voxelSize) =>
        normalizedDistance * MathF.Max(voxelSize * SdfDistanceEncodeVoxelRange, 0.0001f);

    private static bool AabbIntersects(CoreVector3 aMin, CoreVector3 aMax, CoreVector3 bMin, CoreVector3 bMax) =>
        aMax.X >= bMin.X && aMax.Y >= bMin.Y && aMax.Z >= bMin.Z &&
        bMax.X >= aMin.X && bMax.Y >= aMin.Y && bMax.Z >= aMin.Z;

    private static bool GreaterThanOrEqual(CoreVector3 a, CoreVector3 b) =>
        a.X >= b.X && a.Y >= b.Y && a.Z >= b.Z;

    private static bool AnyLessThan(CoreVector3 a, CoreVector3 b) =>
        a.X < b.X || a.Y < b.Y || a.Z < b.Z;

    private static bool AnyGreaterThan(CoreVector3 a, CoreVector3 b) =>
        a.X > b.X || a.Y > b.Y || a.Z > b.Z;

    private static CoreVector3 Min(CoreVector3 a, CoreVector3 b) => CoreVector3.Min(a, b);
    private static CoreVector3 Max(CoreVector3 a, CoreVector3 b) => CoreVector3.Max(a, b);

    private static string BuildFailureReport(float voxelSize, IReadOnlyList<ConformanceFailure> failures, ErrorAccumulator accumulator)
    {
        string[] worst = failures
            .OrderByDescending(static failure => MathF.Abs(failure.Error))
            .Take(12)
            .Select(static failure => failure.ToString())
            .ToArray();

        return
            $"Global SDF conformance failed for voxelSize={voxelSize}m with {failures.Count} offenders.\n" +
            $"Samples={accumulator.SampleCount}, nearSurface={accumulator.NearSurfaceSampleCount}, " +
            $"maxNearAbsError={accumulator.MaxNearSurfaceAbsError}, " +
            $"maxNegativeOvershoot={accumulator.MaxNegativeOvershoot}, " +
            $"maxPositiveOvershoot={accumulator.MaxNearSurfacePositiveOvershoot}\n" +
            string.Join('\n', worst);
    }

    private static string ReadRepoText(params string[] pathParts)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(new[] { directory }.Concat(pathParts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(pathParts));
    }

    private static string ExtractFunction(string source, string signature)
    {
        int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
            throw new InvalidOperationException($"Could not find '{signature}'.");

        int bodyStart = source.IndexOf('{', signatureIndex);
        if (bodyStart < 0)
            throw new InvalidOperationException($"Could not find body for '{signature}'.");

        int depth = 0;
        for (int i = bodyStart; i < source.Length; i++)
        {
            if (source[i] == '{')
                depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[signatureIndex..(i + 1)];
            }
        }

        throw new InvalidOperationException($"Could not find end of body for '{signature}'.");
    }

    private readonly record struct BoxInstance(
        string Name,
        CoreMatrix4x4 WorldMatrix,
        CoreMatrix4x4 WorldToLocal,
        CoreVector3 Scale,
        BoundingBox WorldAabb);

    private readonly record struct CandidateInstance(
        string Name,
        GPUMeshSdf GpuRecord,
        BoundingBox WorldBounds,
        CoreVector4 BoundsCenterRadius);

    private readonly record struct DistanceResult(float Distance, string InstanceName);

    private readonly record struct VoxelKey(int X, int Y, int Z);

    private readonly record struct ConformanceFailure(
        string Kind,
        CoreVector3 Position,
        DistanceResult Reference,
        DistanceResult Actual,
        float Error)
    {
        public static ConformanceFailure NearSurface(CoreVector3 position, DistanceResult reference, DistanceResult actual, float error) =>
            new("near-surface", position, reference, actual, error);

        public static ConformanceFailure PhantomInterior(CoreVector3 position, DistanceResult reference, DistanceResult actual, float error) =>
            new("phantom-interior", position, reference, actual, error);

        public static ConformanceFailure Tunneling(CoreVector3 position, DistanceResult reference, DistanceResult actual, float error) =>
            new("tunneling", position, reference, actual, error);

        public override string ToString() =>
            $"{Kind}: pos={Position}, ref={Reference.Distance} ({Reference.InstanceName}), actual={Actual.Distance} ({Actual.InstanceName}), error={Error}";
    }

    private sealed class ErrorAccumulator
    {
        public int SampleCount { get; private set; }
        public int NearSurfaceSampleCount { get; private set; }
        public float MaxNearSurfaceAbsError { get; private set; }
        public float MaxNegativeOvershoot { get; private set; }
        public float MaxNearSurfacePositiveOvershoot { get; private set; }
        public string WorstNearSurfaceMessage { get; private set; } = string.Empty;
        public string WorstNegativeOvershootMessage { get; private set; } = string.Empty;
        public string WorstPositiveOvershootMessage { get; private set; } = string.Empty;

        public void Observe(float referenceDistance, float actualDistance, string actualInstance, CoreVector3 position, bool nearSurface)
        {
            SampleCount++;
            float error = actualDistance - referenceDistance;
            if (nearSurface)
            {
                NearSurfaceSampleCount++;
                float absError = MathF.Abs(error);
                if (absError > MaxNearSurfaceAbsError)
                {
                    MaxNearSurfaceAbsError = absError;
                    WorstNearSurfaceMessage = $"Worst near-surface error at {position}: ref={referenceDistance}, actual={actualDistance}, contributor={actualInstance}";
                }

                if (error > MaxNearSurfacePositiveOvershoot)
                {
                    MaxNearSurfacePositiveOvershoot = error;
                    WorstPositiveOvershootMessage = $"Worst near-surface positive overshoot at {position}: ref={referenceDistance}, actual={actualDistance}, contributor={actualInstance}";
                }
            }

            float negativeOvershoot = actualDistance < 0.0f ? referenceDistance - actualDistance : 0.0f;
            if (negativeOvershoot > MaxNegativeOvershoot)
            {
                MaxNegativeOvershoot = negativeOvershoot;
                WorstNegativeOvershootMessage = $"Worst negative overshoot at {position}: ref={referenceDistance}, actual={actualDistance}, contributor={actualInstance}";
            }
        }
    }
}

file static class GlobalSdfConformanceVectorExtensions
{
    public static CoreVector3 Xyz(this CoreVector4 value) => new(value.X, value.Y, value.Z);
}
