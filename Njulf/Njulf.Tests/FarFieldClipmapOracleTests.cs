using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class FarFieldClipmapOracleTests
    {
        [Test]
        public void SharedPagedVoxelStorage_IsOneGenerationAwarePhysicalAllocation()
        {
            var active = new BufferHandle(97, 98);

            Assert.Multiple(() =>
            {
                Assert.That(
                    FarFieldClipmapManager.SharesVoxelAllocation(active, active),
                    Is.True,
                    "Paged active and bake roles must produce one render-graph binding.");
                Assert.That(
                    FarFieldClipmapManager.SharesVoxelAllocation(active, new BufferHandle(97, 99)),
                    Is.False,
                    "A recycled handle slot is a different Vulkan allocation lifetime.");
                Assert.That(
                    FarFieldClipmapManager.SharesVoxelAllocation(BufferHandle.Invalid, BufferHandle.Invalid),
                    Is.False,
                    "Invalid descriptor roles do not identify a physical allocation.");
            });
        }

        [Test]
        public void TraceFarFieldClipmap_HitsAnalyticOccupiedVoxelWithDda()
        {
            var grid = new OccupancyGrid(new Vector3(0.0f), voxelSize: 1.0f, new Vector3I(8, 8, 8));
            grid.Set(5, 3, 3, true);

            bool hit = TraceFarFieldClipmap(
                grid,
                origin: new Vector3(-1.0f, 3.5f, 3.5f),
                dir: Vector3.UnitX,
                tMin: 0.0f,
                tMax: 64.0f,
                maxTraceSteps: 32,
                out float hitT,
                out Vector3 normal);

            Assert.Multiple(() =>
            {
                Assert.That(hit, Is.True);
                Assert.That(hitT, Is.EqualTo(6.0f).Within(1.0e-5f));
                Assert.That(normal, Is.EqualTo(new Vector3(-1.0f, 0.0f, 0.0f)));
            });
        }

        [Test]
        public void TraceFarFieldClipmap_MissesWhenRayLeavesGridBeforeOccupiedVoxel()
        {
            var grid = new OccupancyGrid(new Vector3(0.0f), voxelSize: 1.0f, new Vector3I(8, 8, 8));
            grid.Set(5, 3, 3, true);

            bool hit = TraceFarFieldClipmap(
                grid,
                origin: new Vector3(-1.0f, 0.5f, 0.5f),
                dir: Vector3.UnitX,
                tMin: 0.0f,
                tMax: 64.0f,
                maxTraceSteps: 32,
                out _,
                out _);

            Assert.That(hit, Is.False);
        }

        [Test]
        public void JumpFloodDistanceField_MatchesAnalyticBoxSdfWithinVoxelTolerance()
        {
            var grid = new OccupancyGrid(new Vector3(0.0f), voxelSize: 1.0f, new Vector3I(32, 32, 32));
            Vector3I boxMin = new(10, 11, 12);
            Vector3I boxMaxExclusive = new(15, 17, 16);
            for (int z = boxMin.Z; z < boxMaxExclusive.Z; z++)
            for (int y = boxMin.Y; y < boxMaxExclusive.Y; y++)
            for (int x = boxMin.X; x < boxMaxExclusive.X; x++)
                grid.Set(x, y, z, true);

            ushort[] distances = BuildJumpFloodDistanceField(grid);
            Vector3I[] samples =
            [
                new(9, 13, 14),
                new(16, 13, 14),
                new(9, 10, 11),
                new(4, 13, 14),
                new(20, 20, 20),
                new(12, 14, 13)
            ];

            foreach (Vector3I sample in samples)
            {
                float sampledDistance = DecodeTraceDistance(grid, distances, sample);
                float analyticDistance = AnalyticBoxDistance(sample, boxMin, boxMaxExclusive);
                Assert.That(sampledDistance, Is.EqualTo(analyticDistance).Within(0.30f), sample.ToString());
            }
        }

        [Test]
        public void SimpleDdgiAndFarFieldStructLayouts_AreStableForShaderInterop()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiParams>(), Is.EqualTo(160));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiVolume>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiRayResult>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiProbeState>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiProbeUpdate>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiRelocationClassification>(), Is.EqualTo(48));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiPushConstants>(), Is.EqualTo(76));
                Assert.That(Marshal.SizeOf<GPUFarFieldClipmapParams>(), Is.EqualTo(144));
                Assert.That(Marshal.SizeOf<GPUFarFieldPageTableEntry>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUFarFieldInstance>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUFarFieldVoxelizePushConstants>(), Is.EqualTo(56));
            });
        }

        [Test]
        public void FarFieldTriangleBoxOverlap_RejectsAabbOnlyFalsePositives()
        {
            Vector3 half = new(0.5f);

            bool centered = FarFieldTriangleBoxOverlap(
                boxCenter: Vector3.Zero,
                halfSize: half,
                p0: new Vector3(-0.25f, -0.25f, 0.0f),
                p1: new Vector3(0.25f, -0.25f, 0.0f),
                p2: new Vector3(0.0f, 0.25f, 0.0f));

            bool outside = FarFieldTriangleBoxOverlap(
                boxCenter: Vector3.Zero,
                halfSize: half,
                p0: new Vector3(1.1f, 1.1f, 0.0f),
                p1: new Vector3(1.5f, 1.1f, 0.0f),
                p2: new Vector3(1.1f, 1.5f, 0.0f));

            bool aabbOnlyFalsePositive = FarFieldTriangleBoxOverlap(
                boxCenter: Vector3.Zero,
                halfSize: half,
                p0: new Vector3(0.4f, 0.8f, 0.0f),
                p1: new Vector3(0.8f, 0.4f, 0.0f),
                p2: new Vector3(0.8f, 0.8f, 0.0f));

            Assert.Multiple(() =>
            {
                Assert.That(centered, Is.True);
                Assert.That(outside, Is.False);
                Assert.That(aabbOnlyFalsePositive, Is.False);
            });
        }

        [Test]
        public void FarFieldShaderContracts_ArePresent()
        {
            string clipmap = ReadRepoText("Njulf.Shaders", "farfield_clipmap.glsl");
            string voxelize = ReadRepoText("Njulf.Shaders", "farfield_voxelize.comp");
            string common = ReadRepoText("Njulf.Shaders", "common.glsl");
            string simpleTrace = ReadRepoText("Njulf.Shaders", "ddgi_simple_trace.comp");
            string renderer = ReadRepoText("Njulf.Rendering", "Resources", "RendererDiagnosticsBuffer.cs");
            string bakePass = ReadRepoText("Njulf.Rendering", "Pipeline", "FarFieldClipmapBakePass.cs");

            Assert.Multiple(() =>
            {
                Assert.That(clipmap, Does.Contain("bool TraceFarFieldClipmap("));
                Assert.That(clipmap, Does.Contain("bool TraceFarFieldClipmapDetailed("));
                Assert.That(clipmap, Does.Contain("bool TraceFarFieldClipmapSphereMarch("));
                Assert.That(clipmap, Does.Contain("ReadFarFieldDistanceVoxels"));
                Assert.That(clipmap, Does.Contain("out bool stepExhausted"));
                Assert.That(clipmap, Does.Contain("visitedSteps = stepIndex + 1u;"));
                Assert.That(clipmap, Does.Contain("uint voxelBufferIndex;"));
                Assert.That(clipmap, Does.Contain("uint distanceBufferIndex;"));
                Assert.That(clipmap, Does.Contain("p.voxelBufferIndex = uint(max(diagnostics.x, 0.0));"));
                Assert.That(clipmap, Does.Contain("p.distanceBufferIndex = uint(max(jumpFlood.x, 0.0));"));
                Assert.That(clipmap, Does.Contain("ReadStorageWord(p.voxelBufferIndex, FarFieldVoxelIndex(voxel, p))"));
                Assert.That(clipmap, Does.Contain("bool TraceFarFieldPaged("));
                Assert.That(clipmap, Does.Contain("bool FindFarFieldPage("));
                Assert.That(clipmap, Does.Contain("FAR_FIELD_PAGE_VALID_FLAG"));
                Assert.That(clipmap, Does.Contain("FarFieldTraceMaximumDistance"));
                string jumpFlood = ReadRepoText("Njulf.Shaders", "farfield_jumpflood.comp");
                Assert.That(voxelize, Does.Contain("const uint FARFIELD_VOXELIZE_MODE_TRIANGLES = 1u;"));
                Assert.That(voxelize, Does.Contain("const uint FARFIELD_VOXELIZE_MODE_PUBLISH = 2u;"));
                Assert.That(voxelize, Does.Contain("uint CurrentFrameIndex;"));
                Assert.That(voxelize, Does.Contain("uint PageVoxelOffset;"));
                Assert.That(voxelize, Does.Contain("uint PageGeneration;"));
                Assert.That(voxelize, Does.Contain("WriteStorageFloat(pc.ParamsBufferIndex, 16u, float(pc.VoxelBufferIndex));"));
                Assert.That(voxelize, Does.Contain("bool FarFieldTriangleBoxOverlap(vec3 boxCenter, vec3 halfSize, vec3 p0, vec3 p1, vec3 p2)"));
                Assert.That(voxelize, Does.Contain("if (!FarFieldTriangleBoxOverlap(voxelCenter, halfVoxel, p0, p1, p2))"));
                Assert.That(voxelize, Does.Contain("Words[pc.PageVoxelOffset + voxelIndex], packed"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_RAY_COUNTER = FAR_FIELD_COUNTER_BASE + 0u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_HIT_COUNTER = FAR_FIELD_COUNTER_BASE + 1u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_STEP_EXHAUSTED_COUNTER = FAR_FIELD_COUNTER_BASE + 2u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_BAKED_TRIANGLE_COUNTER = FAR_FIELD_COUNTER_BASE + 3u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_OCCUPIED_VOXEL_WRITE_COUNTER = FAR_FIELD_COUNTER_BASE + 4u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_STEP_BUCKET_4_COUNTER = FAR_FIELD_COUNTER_BASE + 9u;"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_RAY_COUNTER, 1u);"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_HIT_COUNTER, 1u);"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_STEP_EXHAUSTED_COUNTER, 1u);"));
                Assert.That(simpleTrace, Does.Contain("farFieldOnlyRing = volume.spacing >= 15.999;"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, stepBucket, 1u);"));
                Assert.That(jumpFlood, Does.Contain("FARFIELD_JUMP_FLOOD_MODE_PROPAGATE"));
                Assert.That(jumpFlood, Does.Contain("WritePackedDistance(dispatchIndex, distance0, distance1);"));
                Assert.That(jumpFlood, Does.Contain("WriteStorageFloat(pc.ParamsBufferIndex, 23u, 1.0);"));
                Assert.That(voxelize, Does.Contain("uint DiagnosticFlags;"));
                Assert.That(voxelize, Does.Contain("void AddFarFieldDiagnostic(uint counterIndex, uint value)"));
                Assert.That(voxelize, Does.Contain("if ((pc.DiagnosticFlags & FARFIELD_DIAGNOSTIC_FLAG_DETAILED) != 0u)"));
                Assert.That(voxelize, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, counterIndex, value);"));
                Assert.That(voxelize, Does.Contain("AddFarFieldDiagnostic(FAR_FIELD_BAKED_TRIANGLE_COUNTER, 1u);"));
                Assert.That(voxelize, Does.Contain("AddFarFieldDiagnostic(FAR_FIELD_OCCUPIED_VOXEL_WRITE_COUNTER, 1u);"));
                Assert.That(renderer, Does.Contain("public const int FarFieldCounterBase = DdgiTraceRingMismatchSampleBase + DdgiTraceRingMismatchSampleCount;"));
                Assert.That(renderer, Does.Contain("public const int FarFieldCounterCount = 10;"));
                Assert.That(bakePass, Does.Contain("CurrentFrameIndex = sceneData.CurrentFrameIndex"));
                Assert.That(bakePass, Does.Contain("private const uint VoxelizeModePublish = 2;"));
                Assert.That(bakePass, Does.Contain("private const uint JumpFloodModePropagate = 1;"));
                Assert.That(bakePass, Does.Contain("uint bakeVoxelBufferIndex = checked((uint)_manager.BakeVoxelBufferIndex);"));
                Assert.That(bakePass, Does.Contain("BuildJumpFloodDistanceField("));
                Assert.That(bakePass, Does.Contain("_manager.MarkPageBakePublished(work.Request);"));
                Assert.That(bakePass, Does.Contain("PageVoxelOffset = pageVoxelOffset"));
                Assert.That(bakePass, Does.Contain("Mode = VoxelizeModePublish"));
                Assert.That(bakePass, Does.Contain("_manager.MarkBakePublished();"));
            });
        }

        [Test]
        public void FarFieldPageCache_EvictsDeterministicallyAndNeverLetsLowPriorityReplaceNearResidency()
        {
            var cache = new FarFieldPageCache();
            cache.Configure(2);
            cache.BeginFrame(1);
            FarFieldPageKey near = new(0, 0, 0, 0);
            FarFieldPageKey mid = new(1, 0, 0, 0);
            FarFieldPageKey far = new(2, 0, 0, 0);
            cache.Request(near, 11UL, priority: 100);
            cache.Request(mid, 12UL, priority: 50);

            Assert.That(cache.TryBeginBake(out FarFieldPageBakeRequest nearBake), Is.True);
            cache.MarkBakePublished(nearBake);
            Assert.That(cache.TryBeginBake(out FarFieldPageBakeRequest midBake), Is.True);
            cache.MarkBakePublished(midBake);
            cache.BeginFrame(4); // Conservative publication delay has elapsed.

            cache.Request(far, 13UL, priority: 10);
            Assert.That(cache.TryGetPhysicalPage(far, out _, out _), Is.False);
            Assert.That(cache.IsResident(near), Is.True);
            Assert.That(cache.IsResident(mid), Is.True);

            cache.Request(far, 13UL, priority: 200);
            Assert.Multiple(() =>
            {
                Assert.That(cache.IsResident(near), Is.True);
                Assert.That(cache.TryGetPhysicalPage(mid, out _, out _), Is.False);
                Assert.That(cache.TryGetPhysicalPage(far, out int physicalPage, out uint generation), Is.True);
                Assert.That(physicalPage, Is.EqualTo(1));
                Assert.That(generation, Is.GreaterThan(0u));
                Assert.That(cache.EvictionCount, Is.EqualTo(1));
            });
        }

        [Test]
        public void FarFieldPageCache_RevisionChangeInvalidatesPublishedPageBeforeReuse()
        {
            var cache = new FarFieldPageCache();
            cache.Configure(1);
            cache.BeginFrame(10);
            FarFieldPageKey key = new(1, -4, 2, 9);
            cache.Request(key, 100UL, priority: 10);
            Assert.That(cache.TryBeginBake(out FarFieldPageBakeRequest bake), Is.True);
            cache.MarkBakePublished(bake);
            cache.BeginFrame(13);
            Assert.That(cache.IsResident(key), Is.True);

            cache.Request(key, 101UL, priority: 10);
            cache.BuildGpuTable(new GPUFarFieldPageTableEntry[cache.RequiredGpuTableCapacity]);
            Assert.Multiple(() =>
            {
                Assert.That(cache.IsResident(key), Is.False);
                Assert.That(cache.TryBeginBake(out FarFieldPageBakeRequest rebuilt), Is.True);
                Assert.That(rebuilt.Generation, Is.Not.EqualTo(bake.Generation));
                Assert.That(rebuilt.SourceRevision, Is.EqualTo(101UL));
            });
        }

        [Test]
        public void FarFieldPageCache_PublishingPageStaysGpuVisibleUntilItCanBeEvicted()
        {
            var cache = new FarFieldPageCache();
            cache.Configure(1);
            cache.BeginFrame(1);
            FarFieldPageKey key = new(0, 2, -1, 4);
            cache.Request(key, 123UL, priority: 50);
            Assert.That(cache.TryBeginBake(out FarFieldPageBakeRequest bake), Is.True);
            cache.MarkBakePublished(bake);

            var table = new GPUFarFieldPageTableEntry[cache.RequiredGpuTableCapacity];
            cache.BuildGpuTable(table);

            Assert.Multiple(() =>
            {
                Assert.That(cache.IsResident(key), Is.False, "The conservative eviction delay remains active.");
                Assert.That(
                    table,
                    Has.Some.Matches<GPUFarFieldPageTableEntry>(entry =>
                        entry.WorldPageX == key.X &&
                        entry.WorldPageY == key.Y &&
                        entry.WorldPageZ == key.Z &&
                        (entry.CascadeAndFlags & FarFieldPageCache.ValidFlag) != 0u),
                    "A recorded publication must remain visible through the next CPU page-table upload.");
            });
        }

        [Test]
        public void FarFieldStaticSourceRevision_TracksMeshAndMaterialContentRevisions()
        {
            ulong baseline = FarFieldClipmapManager.CreateStaticInstanceSourceRevision(
                new MeshHandle(7, 3),
                materialContentRevision: 11);
            ulong changedMesh = FarFieldClipmapManager.CreateStaticInstanceSourceRevision(
                new MeshHandle(7, 4),
                materialContentRevision: 11);
            ulong changedMaterial = FarFieldClipmapManager.CreateStaticInstanceSourceRevision(
                new MeshHandle(7, 3),
                materialContentRevision: 12);

            Assert.Multiple(() =>
            {
                Assert.That(changedMesh, Is.Not.EqualTo(baseline));
                Assert.That(changedMaterial, Is.Not.EqualTo(baseline));
            });
        }

        [Test]
        public void FarFieldStaticSnapshotRefresh_ReusesStableSceneContent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    FarFieldClipmapManager.ShouldRefreshStaticInstanceSnapshot(
                        hasSnapshot: true,
                        sameScene: true,
                        previousSceneContentRevision: 42,
                        sceneContentRevision: 42),
                    Is.False);
                Assert.That(
                    FarFieldClipmapManager.ShouldRefreshStaticInstanceSnapshot(
                        hasSnapshot: false,
                        sameScene: true,
                        previousSceneContentRevision: 42,
                        sceneContentRevision: 42),
                    Is.True);
                Assert.That(
                    FarFieldClipmapManager.ShouldRefreshStaticInstanceSnapshot(
                        hasSnapshot: true,
                        sameScene: false,
                        previousSceneContentRevision: 42,
                        sceneContentRevision: 42),
                    Is.True);
                Assert.That(
                    FarFieldClipmapManager.ShouldRefreshStaticInstanceSnapshot(
                        hasSnapshot: true,
                        sameScene: true,
                        previousSceneContentRevision: 42,
                        sceneContentRevision: 43),
                    Is.True);
                Assert.That(
                    FarFieldClipmapManager.ShouldRefreshStaticInstanceSnapshot(
                        hasSnapshot: true,
                        sameScene: true,
                        previousSceneContentRevision: 42,
                        sceneContentRevision: 0),
                    Is.True,
                    "The legacy overload intentionally refreshes because it has no revision contract.");
            });
        }

        [Test]
        public void SimpleDdgiSceneClampedOrigin_AnchorsAxesCoveredByTheLattice()
        {
            bool hasOrigin = false;
            bool recentered;
            CoreVector3 sceneMin = new(-6.5f, -1.75f, -6.5f);
            CoreVector3 sceneMax = new(6.5f, 1.75f, 6.5f);
            CoreVector3 latticeSize = new(13.0f, 4.0f, 13.0f);

            CoreVector3 initial = SimpleDdgiVolumeManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                latticeSize,
                spacing: 0.5f,
                cameraPosition: new CoreVector3(0.0f, 1.0f, 0.0f),
                currentOrigin: default,
                ref hasOrigin,
                out recentered);
            CoreVector3 farCamera = SimpleDdgiVolumeManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                latticeSize,
                spacing: 0.5f,
                cameraPosition: new CoreVector3(0.0f, 35.0f, -40.0f),
                currentOrigin: initial,
                ref hasOrigin,
                out bool farRecentered);

            Assert.Multiple(() =>
            {
                Assert.That(initial, Is.EqualTo(new CoreVector3(-6.5f, -2.0f, -6.5f)));
                Assert.That(recentered, Is.False);
                Assert.That(farCamera, Is.EqualTo(initial));
                Assert.That(farRecentered, Is.False);
            });
        }

        [Test]
        public void SimpleDdgiSceneClampedOrigin_FollowsOnlyOversizedAxesAndClampsInsideScene()
        {
            bool hasOrigin = false;
            CoreVector3 sceneMin = new(0.0f, -2.0f, 0.0f);
            CoreVector3 sceneMax = new(100.0f, 2.0f, 12.0f);
            CoreVector3 latticeSize = new(20.0f, 4.0f, 16.0f);

            CoreVector3 initial = SimpleDdgiVolumeManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                latticeSize,
                spacing: 1.0f,
                cameraPosition: new CoreVector3(10.0f, 50.0f, 200.0f),
                currentOrigin: default,
                ref hasOrigin,
                out bool initialRecentered);
            CoreVector3 moved = SimpleDdgiVolumeManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                latticeSize,
                spacing: 1.0f,
                cameraPosition: new CoreVector3(90.0f, -50.0f, -200.0f),
                currentOrigin: initial,
                ref hasOrigin,
                out bool movedRecentered);

            Assert.Multiple(() =>
            {
                Assert.That(initial, Is.EqualTo(new CoreVector3(0.0f, -2.0f, -2.0f)));
                Assert.That(initialRecentered, Is.False);
                Assert.That(moved, Is.EqualTo(new CoreVector3(80.0f, -2.0f, -2.0f)));
                Assert.That(movedRecentered, Is.True);
            });
        }

        [Test]
        public void SimpleDdgiToroidalAddressing_PreservesOverlappingPhysicalSlotsAcrossRandomScrolls()
        {
            const int countX = 7;
            const int countY = 5;
            const int countZ = 9;
            var random = new Random(0x51D6);
            GPUSimpleDdgiVolume current = CreateToroidalVolume(countX, countY, countZ, 0, 0, 0);

            for (int step = 0; step < 2048; step++)
            {
                int deltaX = random.Next(-countX * 2, countX * 2 + 1);
                int deltaY = random.Next(-countY * 2, countY * 2 + 1);
                int deltaZ = random.Next(-countZ * 2, countZ * 2 + 1);
                int nextOffsetX = PositiveModulo((int)current.RaysAndReserved.Y + deltaX, countX);
                int nextOffsetY = PositiveModulo((int)current.RaysAndReserved.Z + deltaY, countY);
                int nextOffsetZ = PositiveModulo((int)current.RaysAndReserved.W + deltaZ, countZ);
                GPUSimpleDdgiVolume next = CreateToroidalVolume(countX, countY, countZ, nextOffsetX, nextOffsetY, nextOffsetZ);

                for (int z = 0; z < countZ; z++)
                for (int y = 0; y < countY; y++)
                for (int x = 0; x < countX; x++)
                {
                    int nextLogicalX = x - deltaX;
                    int nextLogicalY = y - deltaY;
                    int nextLogicalZ = z - deltaZ;
                    if (nextLogicalX < 0 || nextLogicalX >= countX ||
                        nextLogicalY < 0 || nextLogicalY >= countY ||
                        nextLogicalZ < 0 || nextLogicalZ >= countZ)
                    {
                        continue;
                    }

                    int previousPhysical = SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex(current, x, y, z);
                    int nextPhysical = SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex(next, nextLogicalX, nextLogicalY, nextLogicalZ);
                    Assert.That(nextPhysical, Is.EqualTo(previousPhysical),
                        $"step={step}, logical=({x},{y},{z}), delta=({deltaX},{deltaY},{deltaZ})");
                }

                for (int physical = 0; physical < countX * countY * countZ; physical++)
                {
                    (int logicalX, int logicalY, int logicalZ) = SimpleDdgiVolumeManager.CalculateLogicalProbeCoordinate(next, physical);
                    Assert.That(
                        SimpleDdgiVolumeManager.CalculatePhysicalProbeLocalIndex(next, logicalX, logicalY, logicalZ),
                        Is.EqualTo(physical),
                        $"step={step}, physical={physical}");
                }

                current = next;
            }
        }

        [Test]
        public void SimpleDdgiProbeGenerationInvalidation_ReusesFreshSlotsOnlyOncePerEvent()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    SimpleDdgiVolumeManager.ShouldAdvanceProbeGenerationForInvalidation(
                        wasFresh: false,
                        previousInvalidationMarker: 11u,
                        currentInvalidationMarker: 12u,
                        forceGenerationAdvance: false),
                    Is.True);
                Assert.That(
                    SimpleDdgiVolumeManager.ShouldAdvanceProbeGenerationForInvalidation(
                        wasFresh: true,
                        previousInvalidationMarker: 12u,
                        currentInvalidationMarker: 12u,
                        forceGenerationAdvance: true),
                    Is.False,
                    "Overlapping invalidation events in one frame must deduplicate.");
                Assert.That(
                    SimpleDdgiVolumeManager.ShouldAdvanceProbeGenerationForInvalidation(
                        wasFresh: true,
                        previousInvalidationMarker: 12u,
                        currentInvalidationMarker: 13u,
                        forceGenerationAdvance: true),
                    Is.True,
                    "A fresh physical slot re-exposed by a later scroll needs a new generation.");
                Assert.That(
                    SimpleDdgiVolumeManager.ShouldAdvanceProbeGenerationForInvalidation(
                        wasFresh: true,
                        previousInvalidationMarker: 12u,
                        currentInvalidationMarker: 13u,
                        forceGenerationAdvance: false),
                    Is.False,
                    "A persistent fresh slot is not a new logical assignment.");
            });
        }

        [Test]
        public void FarFieldSceneClampedOrigin_AnchorsCubicClipmapWhenItCoversScene()
        {
            bool hasOrigin = false;
            CoreVector3 sceneMin = new(-10.0f, -2.0f, -3.0f);
            CoreVector3 sceneMax = new(10.0f, 4.0f, 9.0f);

            CoreVector3 initial = FarFieldClipmapManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                extent: 20.0f,
                voxelSize: 0.5f,
                cameraPosition: new CoreVector3(0.0f, 0.0f, 0.0f),
                currentOrigin: default,
                ref hasOrigin,
                out bool recentered);
            CoreVector3 farCamera = FarFieldClipmapManager.ResolveSceneClampedOrigin(
                sceneMin,
                sceneMax,
                extent: 20.0f,
                voxelSize: 0.5f,
                cameraPosition: new CoreVector3(250.0f, 250.0f, 250.0f),
                currentOrigin: initial,
                ref hasOrigin,
                out bool farRecentered);

            Assert.Multiple(() =>
            {
                Assert.That(initial, Is.EqualTo(new CoreVector3(-10.0f, -9.0f, -7.0f)));
                Assert.That(recentered, Is.False);
                Assert.That(farCamera, Is.EqualTo(initial));
                Assert.That(farRecentered, Is.False);
            });
        }

        [Test]
        public void SimpleDdgiAndFarFieldManagers_ResolveSceneClampedOriginsAndForceRefresh()
        {
            string simpleManager = ReadRepoText("Njulf.Rendering", "Resources", "SimpleDdgiVolumeManager.cs");
            string farFieldManager = ReadRepoText("Njulf.Rendering", "Resources", "FarFieldClipmapManager.cs");
            string renderer = ReadRepoText("Njulf.Rendering", "VulkanRenderer.cs");

            Assert.Multiple(() =>
            {
                Assert.That(renderer, Does.Contain("_farFieldClipmapManager!.Upload("));
                Assert.That(renderer, Does.Contain("sceneData.SceneContentRevision);"));
                Assert.That(renderer, Does.Contain("SimpleDdgiDirtySignature simpleDdgiDirtySignature = CreateSimpleDdgiDirtySignature(scene, lightSnapshot, _ddgiEmissiveSourceRevision);"));
                Assert.That(renderer, Does.Contain("simpleDdgiDirtySignature.Signature"));
                Assert.That(renderer, Does.Contain("simpleDdgiDirtySignature.ReasonFlags"));
                Assert.That(simpleManager, Does.Contain("ulong lightingSignature,"));
                Assert.That(simpleManager, Does.Contain("uint dirtyReasonFlags"));
                Assert.That(simpleManager, Does.Contain("ResolveSceneClampedOrigin(sceneBounds.Min, sceneBounds.Max, latticeSize, spacing, cameraPosition"));
                Assert.That(simpleManager, Does.Contain("if (_recenteredThisFrame)"));
                Assert.That(simpleManager, Does.Contain("PreserveScrolledAtlasData(commandBuffer);"));
                Assert.That(simpleManager, Does.Contain("BuildScrollCopyRunsForTest"));
                Assert.That(simpleManager, Does.Contain("_scrollCopyCount++;"));
                Assert.That(simpleManager, Does.Contain("_atlasPreservedOnRecenterThisFrame = true;"));
                Assert.That(simpleManager, Does.Contain("public bool AtlasClearedThisFrame => _atlasClearedThisFrame;"));
                Assert.That(simpleManager, Does.Contain("MarkFreshForNewOrScrolledProbes();"));
                Assert.That(simpleManager, Does.Contain("_probesToUpdate = BuildUpdateQueue(updateBudget);"));
                Assert.That(simpleManager, Does.Contain("UploadProbeUpdateQueue(stagingRing, commandBuffer);"));
                Assert.That(simpleManager, Does.Contain("ClearAtlasBuffersIfRequired(commandBuffer);"));
                Assert.That(simpleManager, Does.Not.Contain("if (_recenteredThisFrame)\r\n                _atlasClearRequired = true;"));
                Assert.That(simpleManager, Does.Contain("internal static Vector3 ResolveSceneClampedOrigin("));
                Assert.That(simpleManager, Does.Contain("private static bool ShouldRecenter(Vector3 cameraPosition, Vector3 currentOrigin, Vector3 latticeSize, Vector3 sceneMin, Vector3 sceneMax)"));
                Assert.That(simpleManager, Does.Contain("Vector3 quarter = latticeSize * 0.25f;"));
                Assert.That(simpleManager, Does.Contain("ResolveDesiredSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, latticeSize.X, spacing, cameraPosition.X)"));
                Assert.That(simpleManager, Does.Contain("return sceneMin - Math.Max(latticeExtent - sceneExtent, 0.0f) * 0.5f;"));
                Assert.That(simpleManager, Does.Contain("float target = SnapScalar(cameraPosition - latticeExtent * 0.5f, spacing);"));
                Assert.That(simpleManager, Does.Contain("return sceneExtent > latticeExtent && (cameraPosition < innerMin || cameraPosition > innerMax);"));
                Assert.That(simpleManager, Does.Contain("MathF.Floor(value / s) * s"));
                Assert.That(simpleManager, Does.Contain("CalculatePhysicalProbeLocalIndex(GPUSimpleDdgiVolume volume, int logicalX, int logicalY, int logicalZ)"));
                Assert.That(simpleManager, Does.Contain("CalculateLogicalProbeCoordinate(GPUSimpleDdgiVolume volume, int physicalLocalIndex)"));
                Assert.That(simpleManager, Does.Contain("AdvanceProbeGeneration"));
                Assert.That(simpleManager, Does.Contain("_probeStateReadbackUpdateMarkers"));
                Assert.That(farFieldManager, Does.Contain("public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)"));
                Assert.That(farFieldManager, Does.Contain("public int BakeVoxelBufferIndex => _pagedMode ? BindlessIndex.FarFieldClipmapVoxelBuffer : _bakeVoxelBufferIndex;"));
                Assert.That(farFieldManager, Does.Contain("private void UploadPaged("));
                Assert.That(farFieldManager, Does.Contain("EnsurePagedCapacity(pageResolution, pagePoolCapacity);"));
                Assert.That(farFieldManager, Does.Contain("RequestCameraPages(cameraPosition, gi, settingsSignature, sceneContentChanged);"));
                Assert.That(farFieldManager, Does.Contain("FarFieldPageBakeWork"));
                Assert.That(farFieldManager, Does.Contain("public int DistanceBufferIndex => BindlessIndex.FarFieldClipmapDistanceBuffer;"));
                Assert.That(farFieldManager, Does.Contain("Far Field Clipmap Distance Field R16"));
                Assert.That(farFieldManager, Does.Contain("public void MarkBakePublished()"));
                Assert.That(farFieldManager, Does.Contain("(_activeVoxelBufferIndex, _bakeVoxelBufferIndex) = (_bakeVoxelBufferIndex, _activeVoxelBufferIndex);"));
                Assert.That(farFieldManager, Does.Contain("_distanceFieldValid = true;"));
                Assert.That(farFieldManager, Does.Contain("ResolveSceneClampedOrigin(bounds.Min, bounds.Max, cubicExtent, voxelSize, cameraPosition"));
                Assert.That(farFieldManager, Does.Contain("if (recentered)"));
                Assert.That(farFieldManager, Does.Contain("_bakePending = true;"));
                Assert.That(farFieldManager, Does.Contain("internal static Vector3 ResolveSceneClampedOrigin("));
                Assert.That(farFieldManager, Does.Contain("private static bool ShouldRecenter(Vector3 cameraPosition, Vector3 currentOrigin, float extent, Vector3 sceneMin, Vector3 sceneMax)"));
                Assert.That(farFieldManager, Does.Contain("Vector3 quarter = e * 0.25f;"));
                Assert.That(farFieldManager, Does.Contain("ResolveDesiredSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, extent, voxelSize, cameraPosition.X)"));
                Assert.That(farFieldManager, Does.Contain("return sceneMin - Math.Max(extent - sceneExtent, 0.0f) * 0.5f;"));
                Assert.That(farFieldManager, Does.Contain("float target = SnapScalar(cameraPosition - extent * 0.5f, voxelSize);"));
                Assert.That(farFieldManager, Does.Contain("CreateStaticInstanceSourceRevision"));
                Assert.That(farFieldManager, Does.Contain("mesh.Generation"));
                Assert.That(farFieldManager, Does.Contain("GetMaterialContentRevision"));
                Assert.That(farFieldManager, Does.Contain("hash = HashAdd(hash, instance.World);"));
            });
        }

        private static GPUSimpleDdgiVolume CreateToroidalVolume(int countX, int countY, int countZ, int offsetX, int offsetY, int offsetZ)
        {
            return new GPUSimpleDdgiVolume
            {
                OriginAndSpacing = new CoreVector4(0.0f, 0.0f, 0.0f, 1.0f),
                GridCountsAndFirstProbe = new CoreVector4(countX, countY, countZ, 0.0f),
                RaysAndReserved = new CoreVector4(0.0f, offsetX, offsetY, offsetZ)
            };
        }

        private static int PositiveModulo(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        private static bool TraceFarFieldClipmap(
            OccupancyGrid grid,
            Vector3 origin,
            Vector3 dir,
            float tMin,
            float tMax,
            uint maxTraceSteps,
            out float hitT,
            out Vector3 faceNormal)
        {
            hitT = tMax;
            faceNormal = Vector3.Zero;

            Vector3 invDir = new(
                InvDirComponent(dir.X),
                InvDirComponent(dir.Y),
                InvDirComponent(dir.Z));
            Vector3 boundsMin = grid.Origin;
            Vector3 boundsMax = grid.Origin + new Vector3(grid.Resolution.X, grid.Resolution.Y, grid.Resolution.Z) * grid.VoxelSize;
            Vector3 t0 = (boundsMin - origin) * invDir;
            Vector3 t1 = (boundsMax - origin) * invDir;
            Vector3 tNear3 = Vector3.Min(t0, t1);
            Vector3 tFar3 = Vector3.Max(t0, t1);
            float tNear = Math.Max(Math.Max(tNear3.X, tNear3.Y), Math.Max(tNear3.Z, tMin));
            float tFar = Math.Min(Math.Min(tFar3.X, tFar3.Y), Math.Min(tFar3.Z, tMax));
            if (tNear > tFar)
                return false;

            Vector3 pos = origin + dir * tNear;
            Vector3I voxel = Clamp(WorldToVoxel(grid, pos), Vector3I.Zero, grid.Resolution - Vector3I.One);
            Vector3I stepDir = new(Math.Sign(dir.X), Math.Sign(dir.Y), Math.Sign(dir.Z));
            Vector3 nextBoundary = grid.Origin + (new Vector3(voxel.X, voxel.Y, voxel.Z) + Step(Vector3.Zero, dir)) * grid.VoxelSize;
            Vector3 tMaxAxis = (nextBoundary - origin) * invDir;
            Vector3 tDelta = Vector3.Abs(new Vector3(grid.VoxelSize) * invDir);
            float t = tNear;

            for (uint stepIndex = 0; stepIndex < maxTraceSteps && t <= tFar; stepIndex++)
            {
                if (!grid.Inside(voxel))
                    break;

                if (grid.Get(voxel.X, voxel.Y, voxel.Z))
                {
                    hitT = t;
                    return true;
                }

                if (tMaxAxis.X < tMaxAxis.Y && tMaxAxis.X < tMaxAxis.Z)
                {
                    voxel.X += stepDir.X;
                    t = tMaxAxis.X;
                    tMaxAxis.X += tDelta.X;
                    faceNormal = new Vector3(-stepDir.X, 0.0f, 0.0f);
                }
                else if (tMaxAxis.Y < tMaxAxis.Z)
                {
                    voxel.Y += stepDir.Y;
                    t = tMaxAxis.Y;
                    tMaxAxis.Y += tDelta.Y;
                    faceNormal = new Vector3(0.0f, -stepDir.Y, 0.0f);
                }
                else
                {
                    voxel.Z += stepDir.Z;
                    t = tMaxAxis.Z;
                    tMaxAxis.Z += tDelta.Z;
                    faceNormal = new Vector3(0.0f, 0.0f, -stepDir.Z);
                }
            }

            return false;
        }

        private static float InvDirComponent(float value)
        {
            return Math.Abs(value) > 0.000001f ? 1.0f / value : 1.0e30f;
        }

        private static ushort[] BuildJumpFloodDistanceField(OccupancyGrid grid)
        {
            int count = grid.Resolution.X * grid.Resolution.Y * grid.Resolution.Z;
            Vector3I?[] source = new Vector3I?[count];
            Vector3I?[] dest = new Vector3I?[count];
            for (int z = 0; z < grid.Resolution.Z; z++)
            for (int y = 0; y < grid.Resolution.Y; y++)
            for (int x = 0; x < grid.Resolution.X; x++)
            {
                int index = Linear(grid, x, y, z);
                if (grid.Get(x, y, z))
                    source[index] = dest[index] = new Vector3I(x, y, z);
            }

            for (int stride = HighestPowerOfTwoLessThanOrEqualTo(Math.Max(Math.Max(grid.Resolution.X, grid.Resolution.Y), grid.Resolution.Z)) >> 1; stride >= 1; stride >>= 1)
            {
                for (int z = 0; z < grid.Resolution.Z; z++)
                for (int y = 0; y < grid.Resolution.Y; y++)
                for (int x = 0; x < grid.Resolution.X; x++)
                {
                    Vector3I voxel = new(x, y, z);
                    Vector3I? best = source[Linear(grid, x, y, z)];
                    float bestDistance2 = SeedDistance2(voxel, best);
                    for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        Vector3I neighbor = new(x + dx * stride, y + dy * stride, z + dz * stride);
                        if (!grid.Inside(neighbor))
                            continue;
                        Vector3I? candidate = source[Linear(grid, neighbor.X, neighbor.Y, neighbor.Z)];
                        float candidateDistance2 = SeedDistance2(voxel, candidate);
                        if (candidateDistance2 < bestDistance2)
                        {
                            bestDistance2 = candidateDistance2;
                            best = candidate;
                        }
                    }

                    dest[Linear(grid, x, y, z)] = best;
                }

                (source, dest) = (dest, source);
            }

            ushort[] distances = new ushort[count];
            for (int z = 0; z < grid.Resolution.Z; z++)
            for (int y = 0; y < grid.Resolution.Y; y++)
            for (int x = 0; x < grid.Resolution.X; x++)
            {
                Vector3I voxel = new(x, y, z);
                Vector3I? seed = source[Linear(grid, x, y, z)];
                distances[Linear(grid, x, y, z)] = seed.HasValue
                    ? (ushort)Math.Clamp(MathF.Round(SeedBoxDistance(voxel, seed.Value) * 256.0f), 0.0f, 65535.0f)
                    : ushort.MaxValue;
            }

            return distances;
        }

        private static float DecodeTraceDistance(OccupancyGrid grid, ushort[] distances, Vector3I voxel)
        {
            float distanceVoxels = distances[Linear(grid, voxel.X, voxel.Y, voxel.Z)] / 256.0f;
            return distanceVoxels * grid.VoxelSize;
        }

        private static float AnalyticBoxDistance(Vector3I voxel, Vector3I boxMin, Vector3I boxMaxExclusive)
        {
            Vector3 p = new(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f);
            Vector3 min = new(boxMin.X, boxMin.Y, boxMin.Z);
            Vector3 max = new(boxMaxExclusive.X, boxMaxExclusive.Y, boxMaxExclusive.Z);
            Vector3 d = Vector3.Max(Vector3.Max(min - p, p - max), Vector3.Zero);
            return d.Length();
        }

        private static float SeedDistance2(Vector3I voxel, Vector3I? seed)
        {
            if (!seed.HasValue)
                return float.PositiveInfinity;
            Vector3I s = seed.Value;
            int dx = voxel.X - s.X;
            int dy = voxel.Y - s.Y;
            int dz = voxel.Z - s.Z;
            return dx * dx + dy * dy + dz * dz;
        }

        private static float SeedBoxDistance(Vector3I voxel, Vector3I seed)
        {
            Vector3 d = Vector3.Abs(new Vector3(voxel.X - seed.X, voxel.Y - seed.Y, voxel.Z - seed.Z)) - new Vector3(0.5f);
            return Vector3.Max(d, Vector3.Zero).Length();
        }

        private static int Linear(OccupancyGrid grid, int x, int y, int z) =>
            x + y * grid.Resolution.X + z * grid.Resolution.X * grid.Resolution.Y;

        private static int HighestPowerOfTwoLessThanOrEqualTo(int value)
        {
            int result = 1;
            while ((result << 1) <= value)
                result <<= 1;
            return result;
        }

        private static Vector3I WorldToVoxel(OccupancyGrid grid, Vector3 worldPosition)
        {
            Vector3 scaled = (worldPosition - grid.Origin) / grid.VoxelSize;
            Vector3 v = new(MathF.Floor(scaled.X), MathF.Floor(scaled.Y), MathF.Floor(scaled.Z));
            return new Vector3I((int)v.X, (int)v.Y, (int)v.Z);
        }

        private static Vector3 Step(Vector3 edge, Vector3 x)
        {
            return new Vector3(
                x.X < edge.X ? 0.0f : 1.0f,
                x.Y < edge.Y ? 0.0f : 1.0f,
                x.Z < edge.Z ? 0.0f : 1.0f);
        }

        private static bool FarFieldTriangleBoxOverlap(Vector3 boxCenter, Vector3 halfSize, Vector3 p0, Vector3 p1, Vector3 p2)
        {
            Vector3 v0 = p0 - boxCenter;
            Vector3 v1 = p1 - boxCenter;
            Vector3 v2 = p2 - boxCenter;
            Vector3 e0 = v1 - v0;
            Vector3 e1 = v2 - v1;
            Vector3 e2 = v0 - v2;

            Vector3 triMin = Vector3.Min(v0, Vector3.Min(v1, v2));
            Vector3 triMax = Vector3.Max(v0, Vector3.Max(v1, v2));
            if (triMin.X > halfSize.X || triMax.X < -halfSize.X ||
                triMin.Y > halfSize.Y || triMax.Y < -halfSize.Y ||
                triMin.Z > halfSize.Z || triMax.Z < -halfSize.Z)
                return false;

            Vector3 normal = Vector3.Cross(e0, e1);
            if (!FarFieldPlaneBoxOverlap(normal, v0, halfSize))
                return false;

            Vector3[] axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
            Vector3[] edges = [e0, e1, e2];
            foreach (Vector3 edge in edges)
            foreach (Vector3 axis in axes)
            {
                if (!FarFieldAxisOverlap(Vector3.Cross(edge, axis), v0, v1, v2, halfSize))
                    return false;
            }

            return true;
        }

        private static bool FarFieldAxisOverlap(Vector3 axis, Vector3 v0, Vector3 v1, Vector3 v2, Vector3 halfSize)
        {
            if (Vector3.Dot(axis, axis) <= 0.0000001f)
                return true;

            float p0 = Vector3.Dot(v0, axis);
            float p1 = Vector3.Dot(v1, axis);
            float p2 = Vector3.Dot(v2, axis);
            float minP = Math.Min(p0, Math.Min(p1, p2));
            float maxP = Math.Max(p0, Math.Max(p1, p2));
            float radius = Vector3.Dot(Vector3.Abs(axis), halfSize);
            return minP <= radius && maxP >= -radius;
        }

        private static bool FarFieldPlaneBoxOverlap(Vector3 normal, Vector3 vertex, Vector3 halfSize)
        {
            float centerDistance = Vector3.Dot(normal, vertex);
            float radius = Vector3.Dot(Vector3.Abs(normal), halfSize);
            return Math.Abs(centerDistance) <= radius;
        }

        private static Vector3I Clamp(Vector3I value, Vector3I min, Vector3I max)
        {
            return new Vector3I(
                Math.Clamp(value.X, min.X, max.X),
                Math.Clamp(value.Y, min.Y, max.Y),
                Math.Clamp(value.Z, min.Z, max.Z));
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

        private sealed class OccupancyGrid
        {
            private readonly bool[] _occupied;

            public OccupancyGrid(Vector3 origin, float voxelSize, Vector3I resolution)
            {
                Origin = origin;
                VoxelSize = voxelSize;
                Resolution = resolution;
                _occupied = new bool[resolution.X * resolution.Y * resolution.Z];
            }

            public Vector3 Origin { get; }
            public float VoxelSize { get; }
            public Vector3I Resolution { get; }

            public bool Inside(Vector3I voxel)
            {
                return voxel.X >= 0 && voxel.Y >= 0 && voxel.Z >= 0 &&
                    voxel.X < Resolution.X && voxel.Y < Resolution.Y && voxel.Z < Resolution.Z;
            }

            public bool Get(int x, int y, int z)
            {
                return _occupied[x + y * Resolution.X + z * Resolution.X * Resolution.Y];
            }

            public void Set(int x, int y, int z, bool occupied)
            {
                _occupied[x + y * Resolution.X + z * Resolution.X * Resolution.Y] = occupied;
            }
        }

        private struct Vector3I
        {
            public static readonly Vector3I Zero = new(0, 0, 0);
            public static readonly Vector3I One = new(1, 1, 1);

            public Vector3I(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public int X;
            public int Y;
            public int Z;

            public static Vector3I operator -(Vector3I left, Vector3I right)
            {
                return new Vector3I(left.X - right.X, left.Y - right.Y, left.Z - right.Z);
            }

            public override string ToString() => $"({X}, {Y}, {Z})";
        }
    }
}
