using System;
using System.Runtime.InteropServices;
using Njulf.Core.Geometry;
using Njulf.Core.Math;

namespace Njulf.Assets
{
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
            Name = "Unnamed";
        }
    }
}
