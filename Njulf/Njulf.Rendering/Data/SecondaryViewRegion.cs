using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

internal readonly record struct SecondaryViewRegion(uint X, uint Y, uint Width, uint Height)
{
    internal SecondaryViewRegion Resolve(uint width, uint height) =>
        Width == 0 || Height == 0 ? new(0, 0, width, height) : this;

    internal Matrix4x4 Crop(Matrix4x4 viewProjection, uint width, uint height)
    {
        SecondaryViewRegion r = Resolve(width, height);
        Matrix4x4 crop = Matrix4x4.Identity;
        crop.M11 = (float)width / r.Width;
        crop.M22 = (float)height / r.Height;
        crop.M41 = ((float)width - 2f * r.X - r.Width) / r.Width;
        crop.M42 = ((float)height - 2f * r.Y - r.Height) / r.Height;
        return viewProjection * crop;
    }
}

internal static class SecondaryViewFootprint
{
    internal static SecondaryViewRegion Compute(AutomaticPlanarCluster cluster,
        Matrix4x4 mainViewProjection, Matrix4x4 captureViewProjection,
        uint width, uint height, int mipCount)
    {
        Vector2 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
        Frustum main = SceneDataBuilder.ExtractFrustum(mainViewProjection);
        ReadOnlySpan<Vector4> planes = [main.Left, main.Right, main.Bottom, main.Top, main.Near, main.Far];
        Span<Vector3> polygon = stackalloc Vector3[16];
        Span<Vector3> clipped = stackalloc Vector3[16];
        float roughness = 0f;
        foreach (AutomaticPlanarCandidate member in cluster.Members)
        {
            roughness = Math.Max(roughness, member.MaximumSamplingRoughness);
            Vector2 low = member.ProjectedBoundsMin, high = member.ProjectedBoundsMax;
            polygon[0] = Point(member, low.X, low.Y);
            polygon[1] = Point(member, high.X, low.Y);
            polygon[2] = Point(member, high.X, high.Y);
            polygon[3] = Point(member, low.X, high.Y);
            int count = 4;
            foreach (Vector4 plane in planes)
            {
                int nextCount = 0;
                for (int i = 0; i < count; i++)
                {
                    Vector3 a = polygon[i], b = polygon[(i + 1) % count];
                    float da = Vector4.Dot(new(a, 1), plane), db = Vector4.Dot(new(b, 1), plane);
                    if (da >= 0) clipped[nextCount++] = a;
                    if ((da >= 0) != (db >= 0)) clipped[nextCount++] = a + (b - a) * (da / (da - db));
                }
                clipped[..nextCount].CopyTo(polygon);
                count = nextCount;
            }
            for (int i = 0; i < count; i++)
            {
                Vector3 p = polygon[i];
                Matrix4x4 m = captureViewProjection;
                float w = p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;
                if (!float.IsFinite(w) || w <= 1e-5f) return default;
                Vector2 uv = new((p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41) / w * 0.5f + 0.5f,
                    (p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42) / w * 0.5f + 0.5f);
                minimum = new(Math.Min(minimum.X, uv.X), Math.Min(minimum.Y, uv.Y));
                maximum = new(Math.Max(maximum.X, uv.X), Math.Max(maximum.Y, uv.Y));
            }
        }
        if (!float.IsFinite(minimum.X) || !float.IsFinite(roughness)) return default;
        int highestMip = Math.Clamp((int)MathF.Ceiling(roughness * (mipCount - 1)), 0, mipCount - 1);
        float mipRoughness = mipCount <= 1 ? 0 : (float)highestMip / (mipCount - 1);
        // The production GGX kernel uses light.xy/max(light.z,.2), bounded by 5.
        float filterRadius = highestMip == 0 ? 0 : 5f * Math.Min(0.25f, 0.01f + mipRoughness * mipRoughness * 0.20f);
        float texels = 16f + (1 << highestMip); // Temporal motion guard and trilinear footprint.
        uint left = (uint)Math.Clamp(MathF.Floor((minimum.X - filterRadius) * width - texels), 0, width);
        uint top = (uint)Math.Clamp(MathF.Floor((minimum.Y - filterRadius) * height - texels), 0, height);
        uint right = (uint)Math.Clamp(MathF.Ceiling((maximum.X + filterRadius) * width + texels), left, width);
        uint bottom = (uint)Math.Clamp(MathF.Ceiling((maximum.Y + filterRadius) * height + texels), top, height);
        return right > left && bottom > top ? new(left, top, right - left, bottom - top) : default;
    }

    private static Vector3 Point(AutomaticPlanarCandidate c, float x, float y) =>
        c.WorldOrigin + c.WorldTangent * x + c.WorldBitangent * y;
}
