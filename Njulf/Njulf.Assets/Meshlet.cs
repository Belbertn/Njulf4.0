using System;
using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Assets
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct Meshlet
    {
        public Vector3 BoundingSphereCenter;
        public float BoundingSphereRadius;
        public uint VertexOffset;
        public uint VertexCount;
        public uint IndexOffset;
        public uint IndexCount;
        public uint LocalVertexOffset;
        public uint LocalVertexCount;
        public uint LocalTriangleOffset;
        public uint LocalTriangleCount;

        public Meshlet(
            Vector3 boundingSphereCenter,
            float boundingSphereRadius,
            uint vertexOffset,
            uint vertexCount,
            uint indexOffset,
            uint indexCount,
            uint localVertexOffset,
            uint localVertexCount,
            uint localTriangleOffset,
            uint localTriangleCount)
        {
            BoundingSphereCenter = boundingSphereCenter;
            BoundingSphereRadius = boundingSphereRadius;
            VertexOffset = vertexOffset;
            VertexCount = vertexCount;
            IndexOffset = indexOffset;
            IndexCount = indexCount;
            LocalVertexOffset = localVertexOffset;
            LocalVertexCount = localVertexCount;
            LocalTriangleOffset = localTriangleOffset;
            LocalTriangleCount = localTriangleCount;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct MeshletVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector3 Tangent;
        public Vector3 Bitangent;
        public Vector2 TexCoord;

        public MeshletVertex(Vector3 position, Vector3 normal, Vector3 tangent, Vector3 bitangent, Vector2 texCoord)
        {
            Position = position;
            Normal = normal;
            Tangent = tangent;
            Bitangent = bitangent;
            TexCoord = texCoord;
        }
    }

    public class MeshletMesh
    {
        public Meshlet[] Meshlets { get; set; }
        public Vector3[] Vertices { get; set; }
        public uint[] Indices { get; set; }
        public uint[] MeshletVertices { get; set; }
        public uint[] MeshletTriangles { get; set; }
        public BoundingBox BoundingBox { get; set; }
        public BoundingSphere BoundingSphere { get; set; }
        public string Name { get; set; }

        public MeshletMesh()
        {
            Meshlets = Array.Empty<Meshlet>();
            Vertices = Array.Empty<Vector3>();
            Indices = Array.Empty<uint>();
            MeshletVertices = Array.Empty<uint>();
            MeshletTriangles = Array.Empty<uint>();
        }
    }
}
