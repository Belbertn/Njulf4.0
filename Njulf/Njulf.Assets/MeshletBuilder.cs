using System;
using System.Collections.Generic;
using Njulf.Core.Math;

namespace Njulf.Assets
{
    public class MeshletBuilder
    {
        private const int MaxVerticesPerMeshlet = 64;
        private const int MaxTrianglesPerMeshlet = 126;
        private const int MaxMeshletsPerChunk = 2048;
        private const int MaxVerticesPerChunk = 1024;

        public MeshletMesh BuildMeshlets(
            Vector3[] vertices,
            uint[] indices,
            Vector3[]? normals = null,
            Vector3[]? tangents = null,
            Vector3[]? bitangents = null,
            Vector2[]? texCoords = null,
            string? name = null)
        {
            if (vertices == null || vertices.Length == 0)
                throw new ArgumentException("Vertices cannot be null or empty");
            if (indices == null || indices.Length == 0)
                throw new ArgumentException("Indices cannot be null or empty");
            if (indices.Length % 3 != 0)
                throw new ArgumentException("Indices must be a multiple of 3 (triangles only)");

            var mesh = new MeshletMesh
            {
                Name = name ?? "Unnamed",
                Vertices = vertices,
                Indices = indices
            };

            ComputeBoundingVolume(vertices, out var bbox, out var bsphere);
            mesh.BoundingBox = bbox;
            mesh.BoundingSphere = bsphere;

            var meshlets = new List<Meshlet>();
            var meshletVertices = new List<uint>();
            var meshletTriangles = new List<uint>();

            int totalVertices = vertices.Length;
            int totalTriangles = indices.Length / 3;

            if (totalVertices > MaxVerticesPerChunk)
            {
                // Split into chunks
                int chunkCount = (totalVertices + MaxVerticesPerChunk - 1) / MaxVerticesPerChunk;
                for (int c = 0; c < chunkCount; c++)
                {
                    int chunkStart = c * MaxVerticesPerChunk;
                    int chunkEnd = System.Math.Min(chunkStart + MaxVerticesPerChunk, totalVertices);
                    int chunkVertices = chunkEnd - chunkStart;

                    BuildChunkMeshlets(
                        vertices, indices, normals, tangents, bitangents, texCoords,
                        chunkStart, chunkEnd, chunkVertices,
                        meshlets, meshletVertices, meshletTriangles);
                }
            }
            else
            {
                BuildChunkMeshlets(
                    vertices, indices, normals, tangents, bitangents, texCoords,
                    0, totalVertices, totalVertices,
                    meshlets, meshletVertices, meshletTriangles);
            }

            mesh.Meshlets = meshlets.ToArray();
            mesh.MeshletVertices = meshletVertices.ToArray();
            mesh.MeshletTriangles = meshletTriangles.ToArray();

            return mesh;
        }

        private void BuildChunkMeshlets(
            Vector3[] vertices,
            uint[] indices,
            Vector3[] normals,
            Vector3[] tangents,
            Vector3[] bitangents,
            Vector2[] texCoords,
            int chunkStart,
            int chunkEnd,
            int chunkVertices,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            int chunkVertexCount = chunkEnd - chunkStart;
            int maxMeshletsInChunk = (chunkVertexCount + MaxVerticesPerMeshlet - 1) / MaxVerticesPerMeshlet;

            // Build adjacency information
            var vertexToTriangles = new List<List<int>>(chunkVertexCount);
            for (int i = 0; i < chunkVertexCount; i++)
                vertexToTriangles.Add(new List<int>());

            // Collect triangles that use vertices in this chunk
            var chunkTriangles = new List<int>();
            for (int i = 0; i < indices.Length / 3; i++)
            {
                uint i0 = indices[i * 3];
                uint i1 = indices[i * 3 + 1];
                uint i2 = indices[i * 3 + 2];

                bool inChunk = false;
                for (int j = 0; j < 3; j++)
                {
                    uint idx = j == 0 ? i0 : j == 1 ? i1 : i2;
                    if (idx >= (uint)chunkStart && idx < (uint)chunkEnd)
                    {
                        inChunk = true;
                        break;
                    }
                }

                if (inChunk)
                {
                    chunkTriangles.Add(i);
                    for (int j = 0; j < 3; j++)
                    {
                        uint idx = j == 0 ? i0 : j == 1 ? i1 : i2;
                        int localIdx = (int)(idx - (uint)chunkStart);
                        if (localIdx >= 0 && localIdx < chunkVertexCount)
                            vertexToTriangles[localIdx].Add(chunkTriangles.Count - 1);
                    }
                }
            }

            // Greedy meshlet building
            var usedVertices = new bool[chunkVertexCount];
            var usedTriangles = new bool[chunkTriangles.Count];

            foreach (int triIdx in chunkTriangles)
            {
                if (usedTriangles[triIdx])
                    continue;

                var seedTriangles = new List<int> { triIdx };
                var seedVertices = new HashSet<int>();
                var localVertices = new Dictionary<int, int>();

                // Add vertices from seed triangle
                for (int j = 0; j < 3; j++)
                {
                    uint globalIdx = indices[triIdx * 3 + j];
                    int localIdx = (int)(globalIdx - (uint)chunkStart);
                    if (localIdx >= 0 && localIdx < chunkVertexCount)
                    {
                        seedVertices.Add(localIdx);
                        localVertices[localIdx] = localVertices.Count;
                    }
                }

                // Try to expand the meshlet
                bool expanded = true;
                while (expanded && seedTriangles.Count < MaxTrianglesPerMeshlet && localVertices.Count < MaxVerticesPerMeshlet)
                {
                    expanded = false;
                    var candidates = new List<int>();

                    // Find candidate triangles adjacent to current vertices
                    foreach (int v in seedVertices)
                    {
                        foreach (int t in vertexToTriangles[v])
                        {
                            if (!usedTriangles[t] && !seedTriangles.Contains(t) && !candidates.Contains(t))
                                candidates.Add(t);
                        }
                    }

                    // Sort candidates by shared vertex count
                    candidates.Sort((a, b) => CompareTriangleFit(a, b, seedVertices, indices));

                    foreach (int t in candidates)
                    {
                        if (seedTriangles.Count >= MaxTrianglesPerMeshlet)
                            break;

                        // Check if adding this triangle would exceed vertex limit
                        var newVertices = new List<int>();
                        for (int j = 0; j < 3; j++)
                        {
                            uint globalIdx = indices[t * 3 + j];
                            int localIdx = (int)(globalIdx - (uint)chunkStart);
                            if (localIdx >= 0 && localIdx < chunkVertexCount && !localVertices.ContainsKey(localIdx))
                                newVertices.Add(localIdx);
                        }

                        if (localVertices.Count + newVertices.Count > MaxVerticesPerMeshlet)
                            continue;

                        seedTriangles.Add(t);
                        foreach (int v in newVertices)
                        {
                            seedVertices.Add(v);
                            localVertices[v] = localVertices.Count;
                        }
                        expanded = true;
                    }
                }

                // Create meshlet
                uint meshletVertexOffset = (uint)meshletVertices.Count;
                uint meshletLocalVertexOffset = (uint)meshletVertices.Count - (uint)chunkStart;
                uint meshletTriangleOffset = (uint)meshletTriangles.Count / 3;

                // Compute bounding sphere
                var center = Vector3.Zero;
                float maxRadius = 0f;
                foreach (int v in seedVertices)
                {
                    Vector3 pos = vertices[chunkStart + v];
                    center += pos;
                    foreach (int v2 in seedVertices)
                    {
                        Vector3 pos2 = vertices[chunkStart + v2];
                        float dist = Vector3.Distance(pos, pos2);
                        if (dist > maxRadius)
                            maxRadius = dist;
                    }
                }
                center /= seedVertices.Count;
                maxRadius /= 2f;

                meshlets.Add(new Meshlet(
                    center,
                    maxRadius,
                    meshletVertexOffset,
                    (uint)seedVertices.Count,
                    (uint)meshletTriangles.Count,
                    (uint)seedTriangles.Count,
                    0, // Will be updated after all meshlets are created
                    (uint)seedVertices.Count,
                    0, // Will be updated after all meshlets are created
                    (uint)seedTriangles.Count));

                // Add vertices and triangles
                foreach (int v in seedVertices)
                {
                    meshletVertices.Add((uint)(chunkStart + v));
                }

                foreach (int t in seedTriangles)
                {
                    usedTriangles[t] = true;
                    for (int j = 0; j < 3; j++)
                    {
                        uint globalIdx = indices[t * 3 + j];
                        int localIdx = (int)(globalIdx - (uint)chunkStart);
                        if (localVertices.TryGetValue(localIdx, out int localVtxIdx))
                        {
                            meshletTriangles.Add((uint)localVtxIdx);
                        }
                    }
                }
            }

            // Update local offsets
            uint localVertexOffset = 0;
            uint localTriangleOffset = 0;
            for (int i = 0; i < meshlets.Count; i++)
            {
                var m = meshlets[i];
                meshlets[i] = new Meshlet(
                    m.BoundingSphereCenter,
                    m.BoundingSphereRadius,
                    m.VertexOffset,
                    m.VertexCount,
                    m.IndexOffset,
                    m.IndexCount,
                    localVertexOffset,
                    m.LocalVertexCount,
                    localTriangleOffset,
                    m.LocalTriangleCount);

                localVertexOffset += m.LocalVertexCount;
                localTriangleOffset += m.LocalTriangleCount;
            }
        }

        private int CompareTriangleFit(int a, int b, HashSet<int> seedVertices, uint[] indices)
        {
            int aShared = 0, bShared = 0;
            for (int j = 0; j < 3; j++)
            {
                uint globalIdxA = indices[a * 3 + j];
                int localIdxA = (int)globalIdxA;
                if (seedVertices.Contains(localIdxA))
                    aShared++;

                uint globalIdxB = indices[b * 3 + j];
                int localIdxB = (int)globalIdxB;
                if (seedVertices.Contains(localIdxB))
                    bShared++;
            }
            return bShared.CompareTo(aShared);
        }

        private static void ComputeBoundingVolume(Vector3[] vertices, out BoundingBox bbox, out BoundingSphere bsphere)
        {
            if (vertices == null || vertices.Length == 0)
            {
                bbox = new BoundingBox();
                bsphere = new BoundingSphere();
                return;
            }

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            foreach (var v in vertices)
            {
                min.X = System.Math.Min(min.X, v.X);
                min.Y = System.Math.Min(min.Y, v.Y);
                min.Z = System.Math.Min(min.Z, v.Z);
                max.X = System.Math.Max(max.X, v.X);
                max.Y = System.Math.Max(max.Y, v.Y);
                max.Z = System.Math.Max(max.Z, v.Z);
            }

            bbox = new BoundingBox(min, max);
            bsphere = BoundingSphere.FromBox(bbox);
        }

        public static MeshletMesh BuildSimpleMeshlets(Vector3[] vertices, uint[] indices, string? name = null)
        {
            var builder = new MeshletBuilder();
            return builder.BuildMeshlets(vertices, indices, null, null, null, null, name);
        }
    }
}
