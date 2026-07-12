using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using Njulf.Rendering.Resources;
using NUnit.Framework;
using CoreVector3 = Njulf.Core.Math.Vector3;

namespace Njulf.Tests
{
    [TestFixture]
    public sealed class FarFieldClipmapOracleTests
    {
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
        public void SimpleDdgiAndFarFieldStructLayouts_AreStableForShaderInterop()
        {
            Assert.Multiple(() =>
            {
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiParams>(), Is.EqualTo(160));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiRayResult>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUFarFieldClipmapParams>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUFarFieldInstance>(), Is.EqualTo(80));
                Assert.That(Marshal.SizeOf<GPUFarFieldVoxelizePushConstants>(), Is.EqualTo(32));
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
                Assert.That(clipmap, Does.Contain("out bool stepExhausted"));
                Assert.That(clipmap, Does.Contain("visitedSteps = stepIndex + 1u;"));
                Assert.That(clipmap, Does.Contain("uint voxelBufferIndex;"));
                Assert.That(clipmap, Does.Contain("p.voxelBufferIndex = uint(max(diagnostics.x, 0.0));"));
                Assert.That(clipmap, Does.Contain("ReadStorageWord(p.voxelBufferIndex, FarFieldVoxelIndex(voxel, p))"));
                Assert.That(voxelize, Does.Contain("const uint FARFIELD_VOXELIZE_MODE_TRIANGLES = 1u;"));
                Assert.That(voxelize, Does.Contain("const uint FARFIELD_VOXELIZE_MODE_PUBLISH = 2u;"));
                Assert.That(voxelize, Does.Contain("uint CurrentFrameIndex;"));
                Assert.That(voxelize, Does.Contain("WriteStorageFloat(pc.ParamsBufferIndex, 16u, float(pc.VoxelBufferIndex));"));
                Assert.That(voxelize, Does.Contain("bool FarFieldTriangleBoxOverlap(vec3 boxCenter, vec3 halfSize, vec3 p0, vec3 p1, vec3 p2)"));
                Assert.That(voxelize, Does.Contain("if (!FarFieldTriangleBoxOverlap(voxelCenter, halfVoxel, p0, p1, p2))"));
                Assert.That(voxelize, Does.Contain("atomicOr(BindlessStorageBuffers[nonuniformEXT(pc.VoxelBufferIndex)].Words[voxelIndex], packed);"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_RAY_COUNTER = FAR_FIELD_COUNTER_BASE + 0u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_HIT_COUNTER = FAR_FIELD_COUNTER_BASE + 1u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_STEP_EXHAUSTED_COUNTER = FAR_FIELD_COUNTER_BASE + 2u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_BAKED_TRIANGLE_COUNTER = FAR_FIELD_COUNTER_BASE + 3u;"));
                Assert.That(common, Does.Contain("const uint FAR_FIELD_OCCUPIED_VOXEL_WRITE_COUNTER = FAR_FIELD_COUNTER_BASE + 4u;"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_RAY_COUNTER, 1u);"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_HIT_COUNTER, 1u);"));
                Assert.That(simpleTrace, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_STEP_EXHAUSTED_COUNTER, 1u);"));
                Assert.That(voxelize, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_BAKED_TRIANGLE_COUNTER, 1u);"));
                Assert.That(voxelize, Does.Contain("AddRendererDiagnostic(pc.CurrentFrameIndex, FAR_FIELD_OCCUPIED_VOXEL_WRITE_COUNTER, 1u);"));
                Assert.That(renderer, Does.Contain("public const int FarFieldCounterBase = DdgiTraceRingMismatchSampleBase + DdgiTraceRingMismatchSampleCount;"));
                Assert.That(renderer, Does.Contain("public const int FarFieldCounterCount = 5;"));
                Assert.That(bakePass, Does.Contain("CurrentFrameIndex = sceneData.CurrentFrameIndex"));
                Assert.That(bakePass, Does.Contain("private const uint VoxelizeModePublish = 2;"));
                Assert.That(bakePass, Does.Contain("uint bakeVoxelBufferIndex = checked((uint)_manager.BakeVoxelBufferIndex);"));
                Assert.That(bakePass, Does.Contain("Mode = VoxelizeModePublish"));
                Assert.That(bakePass, Does.Contain("_manager.MarkBakePublished();"));
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
                Assert.That(renderer, Does.Contain("_farFieldClipmapManager!.Upload(scene, camera.Position, _stagingRing, _currentCommandBuffer);"));
                Assert.That(renderer, Does.Contain("_simpleDdgiVolumeManager?.Upload(scene, camera.Position, _stagingRing, _currentCommandBuffer);"));
                Assert.That(simpleManager, Does.Contain("public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)"));
                Assert.That(simpleManager, Does.Contain("ResolveSceneClampedOrigin(sceneBounds.Min, sceneBounds.Max, latticeSize, spacing, cameraPosition"));
                Assert.That(simpleManager, Does.Contain("if (_recenteredThisFrame)"));
                Assert.That(simpleManager, Does.Contain("_atlasPreservedOnRecenterThisFrame = true;"));
                Assert.That(simpleManager, Does.Contain("public bool AtlasClearedThisFrame => _atlasClearedThisFrame;"));
                Assert.That(simpleManager, Does.Contain("if (_recenteredThisFrame || _atlasFresh)"));
                Assert.That(simpleManager, Does.Contain("updateBudget = _probeCount;"));
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
                Assert.That(farFieldManager, Does.Contain("public void Upload(Scene scene, Vector3 cameraPosition, StagingRing stagingRing, CommandBuffer commandBuffer)"));
                Assert.That(farFieldManager, Does.Contain("public int BakeVoxelBufferIndex => _bakeVoxelBufferIndex;"));
                Assert.That(farFieldManager, Does.Contain("public void MarkBakePublished()"));
                Assert.That(farFieldManager, Does.Contain("(_activeVoxelBufferIndex, _bakeVoxelBufferIndex) = (_bakeVoxelBufferIndex, _activeVoxelBufferIndex);"));
                Assert.That(farFieldManager, Does.Contain("ResolveSceneClampedOrigin(bounds.Min, bounds.Max, cubicExtent, voxelSize, cameraPosition"));
                Assert.That(farFieldManager, Does.Contain("if (recentered)"));
                Assert.That(farFieldManager, Does.Contain("_bakePending = true;"));
                Assert.That(farFieldManager, Does.Contain("internal static Vector3 ResolveSceneClampedOrigin("));
                Assert.That(farFieldManager, Does.Contain("private static bool ShouldRecenter(Vector3 cameraPosition, Vector3 currentOrigin, float extent, Vector3 sceneMin, Vector3 sceneMax)"));
                Assert.That(farFieldManager, Does.Contain("Vector3 quarter = e * 0.25f;"));
                Assert.That(farFieldManager, Does.Contain("ResolveDesiredSceneClampedAxisOrigin(sceneMin.X, sceneMax.X, extent, voxelSize, cameraPosition.X)"));
                Assert.That(farFieldManager, Does.Contain("return sceneMin - Math.Max(extent - sceneExtent, 0.0f) * 0.5f;"));
                Assert.That(farFieldManager, Does.Contain("float target = SnapScalar(cameraPosition - extent * 0.5f, voxelSize);"));
                Assert.That(farFieldManager, Does.Contain("CreateSignature(resolution, new BoundingBox(_clipmapOrigin, _clipmapOrigin + new Vector3(cubicExtent)), _gpuInstances)"));
            });
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
        }
    }
}
