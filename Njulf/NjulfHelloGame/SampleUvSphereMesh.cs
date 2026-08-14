using System;
using System.Collections.Generic;
using Njulf.Rendering.Data;
using CoreVector2 = Njulf.Core.Math.Vector2;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace NjulfHelloGame;

/// <summary>Shared unit UV-sphere geometry for code-authored sample fixtures.</summary>
internal static class SampleUvSphereMesh
{
    public const int DefaultLatitudeSegments = 24;
    public const int DefaultLongitudeSegments = 48;

    public static GPUVertex[] CreateVertices(
        int latitudeSegments = DefaultLatitudeSegments,
        int longitudeSegments = DefaultLongitudeSegments)
    {
        ValidateSegments(latitudeSegments, longitudeSegments);
        var vertices = new List<GPUVertex>(
            2 + (latitudeSegments - 1) * longitudeSegments)
        {
            CreateVertex(CoreVector3.UnitY, 0f, 0f)
        };

        for (int latitude = 1; latitude < latitudeSegments; latitude++)
        {
            float v = (float)latitude / latitudeSegments;
            float theta = v * MathF.PI;
            float y = MathF.Cos(theta);
            float ringRadius = MathF.Sin(theta);

            for (int longitude = 0; longitude < longitudeSegments; longitude++)
            {
                float u = (float)longitude / longitudeSegments;
                float phi = u * MathF.Tau;
                var normal = new CoreVector3(
                    ringRadius * MathF.Cos(phi),
                    y,
                    ringRadius * MathF.Sin(phi));
                vertices.Add(CreateVertex(normal, u, v));
            }
        }

        vertices.Add(CreateVertex(CoreVector3.Down, 0f, 1f));
        return vertices.ToArray();
    }

    public static uint[] CreateIndices(
        int latitudeSegments = DefaultLatitudeSegments,
        int longitudeSegments = DefaultLongitudeSegments)
    {
        ValidateSegments(latitudeSegments, longitudeSegments);
        var indices = new List<uint>(latitudeSegments * longitudeSegments * 6);
        uint bottomIndex = (uint)(1 + (latitudeSegments - 1) * longitudeSegments);

        for (int longitude = 0; longitude < longitudeSegments; longitude++)
        {
            uint current = RingVertexIndex(0, longitude, longitudeSegments);
            uint next = RingVertexIndex(0, longitude + 1, longitudeSegments);
            indices.Add(0u);
            indices.Add(next);
            indices.Add(current);
        }

        for (int latitude = 0; latitude < latitudeSegments - 2; latitude++)
        {
            for (int longitude = 0; longitude < longitudeSegments; longitude++)
            {
                uint upperCurrent = RingVertexIndex(
                    latitude, longitude, longitudeSegments);
                uint upperNext = RingVertexIndex(
                    latitude, longitude + 1, longitudeSegments);
                uint lowerCurrent = RingVertexIndex(
                    latitude + 1, longitude, longitudeSegments);
                uint lowerNext = RingVertexIndex(
                    latitude + 1, longitude + 1, longitudeSegments);

                indices.Add(upperCurrent);
                indices.Add(upperNext);
                indices.Add(lowerCurrent);
                indices.Add(upperNext);
                indices.Add(lowerNext);
                indices.Add(lowerCurrent);
            }
        }

        int lastRing = latitudeSegments - 2;
        for (int longitude = 0; longitude < longitudeSegments; longitude++)
        {
            uint current = RingVertexIndex(
                lastRing, longitude, longitudeSegments);
            uint next = RingVertexIndex(
                lastRing, longitude + 1, longitudeSegments);
            indices.Add(bottomIndex);
            indices.Add(current);
            indices.Add(next);
        }

        return indices.ToArray();
    }

    private static uint RingVertexIndex(
        int ring,
        int longitude,
        int longitudeSegments)
    {
        int wrappedLongitude = longitude % longitudeSegments;
        if (wrappedLongitude < 0)
            wrappedLongitude += longitudeSegments;
        return (uint)(1 + ring * longitudeSegments + wrappedLongitude);
    }

    private static GPUVertex CreateVertex(CoreVector3 normal, float u, float v)
    {
        float phi = u * MathF.Tau;
        var tangent = new CoreVector3(-MathF.Sin(phi), 0f, MathF.Cos(phi));
        return new GPUVertex
        {
            Position = normal,
            Padding0 = 0f,
            Normal = normal,
            Padding1 = 0f,
            TexCoord = new CoreVector2(u, v),
            TexCoord2 = CoreVector2.Zero,
            Tangent = new CoreVector4(tangent, 1f),
            Color = GPUVertex.DefaultColor
        };
    }

    private static void ValidateSegments(int latitudeSegments, int longitudeSegments)
    {
        if (latitudeSegments < 3)
            throw new ArgumentOutOfRangeException(nameof(latitudeSegments));
        if (longitudeSegments < 3)
            throw new ArgumentOutOfRangeException(nameof(longitudeSegments));
    }
}
