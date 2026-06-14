using System;
using System.Collections.Generic;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets
{
    public class MeshletBuilder
    {
        private const int MaxVerticesPerMeshlet = 64;
        private const int MaxTrianglesPerMeshlet = 126;

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

            BuildMeshlets(
                vertices,
                indices,
                meshlets,
                meshletVertices,
                meshletTriangles);

            mesh.Meshlets = meshlets.ToArray();
            mesh.MeshletVertices = meshletVertices.ToArray();
            mesh.MeshletTriangles = meshletTriangles.ToArray();

            return mesh;
        }

        private static void BuildMeshlets(
            Vector3[] vertices,
            uint[] indices,
            List<Meshlet> meshlets,
            List<uint> meshletVertices,
            List<uint> meshletTriangles)
        {
            int totalTriangles = indices.Length / 3;
            var vertexToTriangles = new List<List<int>>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
                vertexToTriangles.Add(new List<int>());

            for (int triangleIndex = 0; triangleIndex < totalTriangles; triangleIndex++)
            {
                for (int corner = 0; corner < 3; corner++)
                {
                    uint vertexIndex = indices[triangleIndex * 3 + corner];
                    if (vertexIndex >= vertices.Length)
                        throw new ArgumentOutOfRangeException(nameof(indices), $"Index {vertexIndex} is outside the vertex buffer.");

                    vertexToTriangles[(int)vertexIndex].Add(triangleIndex);
                }
            }

            var usedTriangles = new bool[totalTriangles];
            var candidateMarks = new bool[totalTriangles];

            for (int seedTriangle = 0; seedTriangle < totalTriangles; seedTriangle++)
            {
                if (usedTriangles[seedTriangle])
                    continue;

                var meshletTriangleIds = new List<int> { seedTriangle };
                var meshletVertexSet = new HashSet<int>();
                var meshletLocalVertices = new Dictionary<int, int>();

                AddTriangleVertices(seedTriangle, indices, meshletVertexSet, meshletLocalVertices);

                bool expanded = true;
                while (expanded &&
                       meshletTriangleIds.Count < MaxTrianglesPerMeshlet &&
                       meshletLocalVertices.Count < MaxVerticesPerMeshlet)
                {
                    expanded = false;
                    var candidates = new List<int>();

                    foreach (int vertexIndex in meshletVertexSet)
                    {
                        foreach (int candidateTriangle in vertexToTriangles[vertexIndex])
                        {
                            if (usedTriangles[candidateTriangle] ||
                                meshletTriangleIds.Contains(candidateTriangle) ||
                                candidateMarks[candidateTriangle])
                            {
                                continue;
                            }

                            candidateMarks[candidateTriangle] = true;
                            candidates.Add(candidateTriangle);
                        }
                    }

                    candidates.Sort((a, b) => CompareTriangleFit(a, b, meshletVertexSet, indices));

                    foreach (int candidateTriangle in candidates)
                    {
                        candidateMarks[candidateTriangle] = false;

                        if (meshletTriangleIds.Count >= MaxTrianglesPerMeshlet)
                            break;

                        int newVertexCount = CountNewTriangleVertices(candidateTriangle, indices, meshletLocalVertices);

                        if (meshletLocalVertices.Count + newVertexCount > MaxVerticesPerMeshlet)
                            continue;

                        meshletTriangleIds.Add(candidateTriangle);
                        AddTriangleVertices(candidateTriangle, indices, meshletVertexSet, meshletLocalVertices);
                        expanded = true;
                    }

                    for (int i = 0; i < candidates.Count; i++)
                        candidateMarks[candidates[i]] = false;
                }

                uint meshletVertexOffset = (uint)meshletVertices.Count;
                uint meshletTriangleOffset = (uint)meshletTriangles.Count / 3;

                ComputeMeshletBounds(vertices, meshletVertexSet, out var center, out float radius);

                meshlets.Add(new Meshlet(
                    center,
                    radius,
                    meshletVertexOffset,
                    (uint)meshletVertexSet.Count,
                    meshletTriangleOffset,
                    (uint)meshletTriangleIds.Count,
                    meshletVertexOffset,
                    (uint)meshletVertexSet.Count,
                    meshletTriangleOffset,
                    (uint)meshletTriangleIds.Count));

                var localToGlobalVertices = new uint[meshletLocalVertices.Count];
                foreach (var pair in meshletLocalVertices)
                    localToGlobalVertices[pair.Value] = (uint)pair.Key;

                for (int i = 0; i < localToGlobalVertices.Length; i++)
                    meshletVertices.Add(localToGlobalVertices[i]);

                foreach (int triangleIndex in meshletTriangleIds)
                {
                    usedTriangles[triangleIndex] = true;
                    for (int corner = 0; corner < 3; corner++)
                        meshletTriangles.Add((uint)meshletLocalVertices[(int)indices[triangleIndex * 3 + corner]]);
                }
            }
        }

        private static void AddTriangleVertices(
            int triangleIndex,
            uint[] indices,
            HashSet<int> meshletVertexSet,
            Dictionary<int, int> meshletLocalVertices)
        {
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex = (int)indices[triangleIndex * 3 + corner];
                if (meshletLocalVertices.ContainsKey(vertexIndex))
                    continue;

                meshletVertexSet.Add(vertexIndex);
                meshletLocalVertices[vertexIndex] = meshletLocalVertices.Count;
            }
        }

        private static int CountNewTriangleVertices(
            int triangleIndex,
            uint[] indices,
            Dictionary<int, int> meshletLocalVertices)
        {
            int count = 0;
            int firstNewVertex = -1;
            int secondNewVertex = -1;
            for (int corner = 0; corner < 3; corner++)
            {
                int vertexIndex = (int)indices[triangleIndex * 3 + corner];
                if (meshletLocalVertices.ContainsKey(vertexIndex) ||
                    vertexIndex == firstNewVertex ||
                    vertexIndex == secondNewVertex)
                {
                    continue;
                }

                if (count == 0)
                    firstNewVertex = vertexIndex;
                else
                    secondNewVertex = vertexIndex;

                count++;
            }

            return count;
        }

        private static int CompareTriangleFit(int a, int b, HashSet<int> seedVertices, uint[] indices)
        {
            int aShared = 0, bShared = 0;
            for (int j = 0; j < 3; j++)
            {
                if (seedVertices.Contains((int)indices[a * 3 + j]))
                    aShared++;

                if (seedVertices.Contains((int)indices[b * 3 + j]))
                    bShared++;
            }
            return bShared.CompareTo(aShared);
        }

        private static void ComputeMeshletBounds(
            Vector3[] vertices,
            HashSet<int> meshletVertexSet,
            out Vector3 center,
            out float radius)
        {
            center = Vector3.Zero;
            foreach (int vertexIndex in meshletVertexSet)
                center += vertices[vertexIndex];

            center /= meshletVertexSet.Count;

            radius = 0f;
            foreach (int vertexIndex in meshletVertexSet)
                radius = System.Math.Max(radius, Vector3.Distance(center, vertices[vertexIndex]));
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
