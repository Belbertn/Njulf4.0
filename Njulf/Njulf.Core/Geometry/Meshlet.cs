using System.Runtime.InteropServices;
using Njulf.Core.Math;

namespace Njulf.Core.Geometry
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
}
