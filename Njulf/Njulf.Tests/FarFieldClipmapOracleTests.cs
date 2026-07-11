using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Njulf.Rendering.Data;
using NUnit.Framework;

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
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiParams>(), Is.EqualTo(128));
                Assert.That(Marshal.SizeOf<GPUSimpleDdgiRayResult>(), Is.EqualTo(32));
                Assert.That(Marshal.SizeOf<GPUFarFieldClipmapParams>(), Is.EqualTo(96));
                Assert.That(Marshal.SizeOf<GPUFarFieldInstance>(), Is.EqualTo(80));
            });
        }

        [Test]
        public void FarFieldShaderContracts_ArePresent()
        {
            string clipmap = ReadRepoText("Njulf.Shaders", "farfield_clipmap.glsl");
            string voxelize = ReadRepoText("Njulf.Shaders", "farfield_voxelize.comp");

            Assert.Multiple(() =>
            {
                Assert.That(clipmap, Does.Contain("bool TraceFarFieldClipmap("));
                Assert.That(clipmap, Does.Contain("ReadStorageWord(uint(FAR_FIELD_CLIPMAP_VOXEL_BUFFER_INDEX), FarFieldVoxelIndex(voxel, p))"));
                Assert.That(voxelize, Does.Contain("const uint FARFIELD_VOXELIZE_MODE_TRIANGLES = 1u;"));
                Assert.That(voxelize, Does.Contain("atomicOr(BindlessStorageBuffers[nonuniformEXT(pc.VoxelBufferIndex)].Words[voxelIndex], packed);"));
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
