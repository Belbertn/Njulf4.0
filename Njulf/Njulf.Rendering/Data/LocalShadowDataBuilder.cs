using System;
using Njulf.Rendering.Resources;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Rendering.Data
{
    public static class LocalShadowDataBuilder
    {
        private const float PointShadowMinimumFaceOverlapTexels = 2f;

        public static GPUSpotShadow[] BuildSpotShadows(SelectedLocalShadow[] selectedLights, ShadowSettings settings)
        {
            if (selectedLights == null)
                throw new ArgumentNullException(nameof(selectedLights));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var shadows = new GPUSpotShadow[selectedLights.Length];
            FillSpotShadows(selectedLights, settings, shadows);
            return shadows;
        }

        public static void FillSpotShadows(
            ReadOnlySpan<SelectedLocalShadow> selectedLights,
            ShadowSettings settings,
            Span<GPUSpotShadow> destination)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (destination.Length < selectedLights.Length)
                throw new ArgumentException("Destination is smaller than the selected spot shadow count.", nameof(destination));

            for (int i = 0; i < selectedLights.Length; i++)
            {
                SelectedLocalShadow selected = selectedLights[i];
                Light light = selected.Light;
                SpotShadowAtlasRect rect = LocalShadowAllocator.GetSpotTileRect(settings.SpotShadowAtlasSize, settings.SpotShadowTileSize, i);
                float atlasSize = settings.SpotShadowAtlasSize;
                float padding = 1f / atlasSize;
                CoreMatrix4x4 viewProjection = BuildSpotViewProjection(light);
                destination[i] = new GPUSpotShadow
                {
                    LightViewProjection = viewProjection,
                    AtlasScaleOffset = new CoreVector4(
                        MathF.Max(rect.Width - 2f, 1f) / atlasSize,
                        MathF.Max(rect.Height - 2f, 1f) / atlasSize,
                        rect.X / atlasSize + padding,
                        rect.Y / atlasSize + padding),
                    BiasStrengthTexelSize = new CoreVector4(
                        settings.SpotNormalBias,
                        settings.SpotConstantDepthBias,
                        GetShadowStrength(light),
                        1f / MathF.Max(settings.SpotShadowAtlasSize, 1u)),
                    LightIndex = selected.LightIndex,
                    AtlasTile = i,
                    PcfRadius = settings.SpotPcfRadius,
                    Enabled = 1
                };
            }
        }

        public static GPUPointShadow[] BuildPointShadows(SelectedLocalShadow[] selectedLights, ShadowSettings settings)
        {
            if (selectedLights == null)
                throw new ArgumentNullException(nameof(selectedLights));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var shadows = new GPUPointShadow[selectedLights.Length];
            FillPointShadows(selectedLights, settings, shadows);
            return shadows;
        }

        public static void FillPointShadows(
            ReadOnlySpan<SelectedLocalShadow> selectedLights,
            ShadowSettings settings,
            Span<GPUPointShadow> destination)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (destination.Length < selectedLights.Length)
                throw new ArgumentException("Destination is smaller than the selected point shadow count.", nameof(destination));

            Span<CoreMatrix4x4> matrices = stackalloc CoreMatrix4x4[6];
            for (int i = 0; i < selectedLights.Length; i++)
            {
                SelectedLocalShadow selected = selectedLights[i];
                Light light = selected.Light;
                FillPointFaceViewProjections(light, settings, matrices);
                destination[i] = new GPUPointShadow
                {
                    FaceViewProjection0 = matrices[0],
                    FaceViewProjection1 = matrices[1],
                    FaceViewProjection2 = matrices[2],
                    FaceViewProjection3 = matrices[3],
                    FaceViewProjection4 = matrices[4],
                    FaceViewProjection5 = matrices[5],
                    PositionRange = new CoreVector4(light.Position.X, light.Position.Y, light.Position.Z, GetFarPlane(light)),
                    BiasStrengthTexelSize = new CoreVector4(
                        settings.PointNormalBias,
                        settings.PointConstantDepthBias,
                        GetShadowStrength(light),
                        1f / MathF.Max(settings.PointShadowMapSize, 1u)),
                    LightIndex = selected.LightIndex,
                    CubemapIndex = i,
                    PcfRadius = settings.PointPcfRadius,
                    Enabled = 1
                };
            }
        }

        public static GPULocalLightShadowIndex[] BuildShadowIndexMap(
            int lightCount,
            SelectedLocalShadow[] selectedSpots,
            SelectedLocalShadow[] selectedPoints)
        {
            if (lightCount < 0)
                throw new ArgumentOutOfRangeException(nameof(lightCount));

            var indices = new GPULocalLightShadowIndex[lightCount];
            FillShadowIndexMap(lightCount, selectedSpots, selectedPoints, indices);
            return indices;
        }

        public static void FillShadowIndexMap(
            int lightCount,
            ReadOnlySpan<SelectedLocalShadow> selectedSpots,
            ReadOnlySpan<SelectedLocalShadow> selectedPoints,
            Span<GPULocalLightShadowIndex> destination)
        {
            if (lightCount < 0)
                throw new ArgumentOutOfRangeException(nameof(lightCount));
            if (destination.Length < lightCount)
                throw new ArgumentException("Destination is smaller than the light count.", nameof(destination));

            for (int i = 0; i < lightCount; i++)
            {
                destination[i].SpotShadowIndex = -1;
                destination[i].PointShadowIndex = -1;
                destination[i].Padding0 = 0;
                destination[i].Padding1 = 0;
            }

            for (int i = 0; i < selectedSpots.Length; i++)
                destination[selectedSpots[i].LightIndex].SpotShadowIndex = i;
            for (int i = 0; i < selectedPoints.Length; i++)
                destination[selectedPoints[i].LightIndex].PointShadowIndex = i;
        }

        public static CoreMatrix4x4 BuildSpotViewProjection(Light light)
        {
            CoreVector3 position = ToCore(light.Position);
            CoreVector3 direction = ToCore(light.Direction);
            if (direction.Length() <= 0.0001f)
                direction = CoreVector3.Forward;
            direction = direction.Normalized();
            CoreVector3 up = MathF.Abs(CoreVector3.Dot(direction, CoreVector3.UnitY)) > 0.95f
                ? CoreVector3.UnitZ
                : CoreVector3.UnitY;

            float fov = Clamp(light.SpotAngle * 2f, 0.02f, MathF.PI * 0.99f);
            return CoreMatrix4x4.CreateLookAt(position, position + direction, up) *
                   CoreMatrix4x4.CreatePerspectiveFieldOfView(fov, 1f, GetNearPlane(light), GetFarPlane(light));
        }

        public static CoreMatrix4x4[] BuildPointFaceViewProjections(Light light)
        {
            CoreVector3 position = ToCore(light.Position);
            CoreMatrix4x4 projection = CreatePointFaceProjection(light, new ShadowSettings());
            return
            [
                CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitX, -CoreVector3.UnitY) * projection,
                CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitX, -CoreVector3.UnitY) * projection,
                CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitY, CoreVector3.UnitZ) * projection,
                CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitY, -CoreVector3.UnitZ) * projection,
                CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitZ, -CoreVector3.UnitY) * projection,
                CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitZ, -CoreVector3.UnitY) * projection
            ];
        }

        public static void FillPointFaceViewProjections(Light light, Span<CoreMatrix4x4> destination)
        {
            FillPointFaceViewProjections(light, new ShadowSettings(), destination);
        }

        private static void FillPointFaceViewProjections(
            Light light,
            ShadowSettings settings,
            Span<CoreMatrix4x4> destination)
        {
            if (destination.Length < 6)
                throw new ArgumentException("Destination must contain at least six matrices.", nameof(destination));

            CoreVector3 position = ToCore(light.Position);
            CoreMatrix4x4 projection = CreatePointFaceProjection(light, settings);
            destination[0] = CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitX, -CoreVector3.UnitY) * projection;
            destination[1] = CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitX, -CoreVector3.UnitY) * projection;
            destination[2] = CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitY, CoreVector3.UnitZ) * projection;
            destination[3] = CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitY, -CoreVector3.UnitZ) * projection;
            destination[4] = CoreMatrix4x4.CreateLookAt(position, position + CoreVector3.UnitZ, -CoreVector3.UnitY) * projection;
            destination[5] = CoreMatrix4x4.CreateLookAt(position, position - CoreVector3.UnitZ, -CoreVector3.UnitY) * projection;
        }

        private static CoreMatrix4x4 CreatePointFaceProjection(Light light, ShadowSettings settings)
        {
            float halfExtentScale = 1f + 2f * GetPointFaceOverlapTexels(settings) / MathF.Max(settings.PointShadowMapSize, 1u);
            float fieldOfView = MathF.Min(MathF.PI * 0.99f, 2f * MathF.Atan(halfExtentScale));
            return CoreMatrix4x4.CreatePerspectiveFieldOfView(fieldOfView, 1f, GetNearPlane(light), GetFarPlane(light));
        }

        private static float GetPointFaceOverlapTexels(ShadowSettings settings)
        {
            return MathF.Max(PointShadowMinimumFaceOverlapTexels, settings.PointPcfRadius + 1f);
        }

        private static float GetNearPlane(Light light)
        {
            return light.ShadowNearPlane > 0f ? light.ShadowNearPlane : 0.05f;
        }

        private static float GetFarPlane(Light light)
        {
            float farPlane = light.ShadowFarPlane > 0f ? light.ShadowFarPlane : light.Range;
            return MathF.Max(farPlane, GetNearPlane(light) + 0.01f);
        }

        private static float GetShadowStrength(Light light)
        {
            return light.ShadowStrength <= 0f ? 1f : Clamp(light.ShadowStrength, 0f, 1f);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            return value > max ? max : value;
        }

        private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);
    }
}
