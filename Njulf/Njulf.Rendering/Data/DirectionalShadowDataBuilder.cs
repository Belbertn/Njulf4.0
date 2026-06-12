using System;
using Njulf.Core.Interfaces;
using Njulf.Rendering.Descriptors;
using NumericsVector3 = System.Numerics.Vector3;
using CoreMatrix4x4 = Njulf.Core.Math.Matrix4x4;
using CoreVector3 = Njulf.Core.Math.Vector3;
using CoreVector4 = Njulf.Core.Math.Vector4;

namespace Njulf.Rendering.Data
{
    public static class DirectionalShadowDataBuilder
    {
        private const float SplitLambda = 0.5f;

        public static GPUShadowData Build(
            ICamera camera,
            NumericsVector3 lightDirection,
            ShadowSettings settings,
            int selectedLightIndex)
        {
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            int cascadeCount = settings.DirectionalCascadeCount;
            float near = MathF.Max(camera.NearPlane, 0.001f);
            float far = MathF.Min(camera.FarPlane, MathF.Max(near + 0.01f, settings.MaxShadowDistance));
            CoreVector3 lightDir = ToCore(lightDirection);
            if (lightDir.Length() <= 0.0001f)
                lightDir = new CoreVector3(0f, -1f, 0f);
            lightDir = lightDir.Normalized();

            CoreMatrix4x4[] matrices = new CoreMatrix4x4[ShadowSettings.MaxDirectionalCascades];
            float[] splits = CalculateCascadeSplits(near, far, cascadeCount);
            float cascadeNear = near;
            for (int i = 0; i < cascadeCount; i++)
            {
                matrices[i] = BuildCascadeMatrix(
                    camera,
                    lightDir,
                    cascadeNear,
                    splits[i],
                    settings.DirectionalShadowMapSize,
                    settings.MaxShadowDistance);
                cascadeNear = splits[i];
            }

            for (int i = cascadeCount; i < matrices.Length; i++)
                matrices[i] = matrices[cascadeCount - 1];

            return new GPUShadowData
            {
                LightViewProjection0 = matrices[0],
                LightViewProjection1 = matrices[1],
                LightViewProjection2 = matrices[2],
                LightViewProjection3 = matrices[3],
                CascadeSplits = new CoreVector4(splits[0], splits[1], splits[2], splits[3]),
                Settings = new CoreVector4(
                    1f,
                    settings.NormalBias,
                    settings.DirectionalShadowMapSize,
                    settings.PcfRadius),
                Indices = new CoreVector4(
                    settings.DirectionalShadowsEnabled ? 1f : 0f,
                    cascadeCount,
                    BindlessIndex.DirectionalShadowTextureBase,
                    selectedLightIndex)
            };
        }

        public static float[] CalculateCascadeSplits(float nearPlane, float farPlane, int cascadeCount)
        {
            cascadeCount = cascadeCount < 1 ? 1 : cascadeCount > ShadowSettings.MaxDirectionalCascades ? ShadowSettings.MaxDirectionalCascades : cascadeCount;
            nearPlane = MathF.Max(nearPlane, 0.001f);
            farPlane = MathF.Max(farPlane, nearPlane + 0.001f);

            var splits = new float[ShadowSettings.MaxDirectionalCascades];
            float range = farPlane - nearPlane;
            float ratio = farPlane / nearPlane;

            for (int i = 0; i < cascadeCount; i++)
            {
                float p = (i + 1f) / cascadeCount;
                float log = nearPlane * MathF.Pow(ratio, p);
                float uniform = nearPlane + range * p;
                splits[i] = SplitLambda * log + (1f - SplitLambda) * uniform;
            }

            splits[cascadeCount - 1] = farPlane;
            for (int i = cascadeCount; i < splits.Length; i++)
                splits[i] = farPlane;

            return splits;
        }

        private static CoreMatrix4x4 BuildCascadeMatrix(
            ICamera camera,
            CoreVector3 lightDirection,
            float nearDistance,
            float farDistance,
            uint shadowMapSize,
            float casterDepthPadding)
        {
            CoreVector3[] corners = BuildFrustumCorners(camera, nearDistance, farDistance);
            CoreVector3 center = CoreVector3.Zero;
            for (int i = 0; i < corners.Length; i++)
                center += corners[i];
            center /= corners.Length;

            CoreVector3 up = MathF.Abs(CoreVector3.Dot(lightDirection, CoreVector3.UnitY)) > 0.95f
                ? CoreVector3.UnitZ
                : CoreVector3.UnitY;
            CoreMatrix4x4 lightView = CoreMatrix4x4.CreateLookAt(center - lightDirection * 100f, center, up);

            CoreVector3 min = TransformPoint(corners[0], lightView);
            CoreVector3 max = min;
            for (int i = 1; i < corners.Length; i++)
            {
                CoreVector3 lightSpaceCorner = TransformPoint(corners[i], lightView);
                min = CoreVector3.Min(min, lightSpaceCorner);
                max = CoreVector3.Max(max, lightSpaceCorner);
            }

            float width = MathF.Max(max.X - min.X, 0.001f);
            float height = MathF.Max(max.Y - min.Y, 0.001f);
            float radius = MathF.Max(width, height) * 0.5f;
            width = radius * 2f;
            height = radius * 2f;

            float centerX = (min.X + max.X) * 0.5f;
            float centerY = (min.Y + max.Y) * 0.5f;
            float texelSize = width / MathF.Max(1u, shadowMapSize);
            centerX = MathF.Floor(centerX / texelSize) * texelSize;
            centerY = MathF.Floor(centerY / texelSize) * texelSize;

            min.X = centerX - width * 0.5f;
            max.X = centerX + width * 0.5f;
            min.Y = centerY - height * 0.5f;
            max.Y = centerY + height * 0.5f;
            float depthPadding = MathF.Max(1f, casterDepthPadding);
            min.Z -= depthPadding;
            max.Z += depthPadding;

            CoreMatrix4x4 crop = CoreMatrix4x4.CreateTranslation(new CoreVector3(
                -(min.X + max.X) * 0.5f,
                -(min.Y + max.Y) * 0.5f,
                0f));
            CoreMatrix4x4 projection = CoreMatrix4x4.CreateOrthographic(width, height, -max.Z, -min.Z);
            return lightView * crop * projection;
        }

        private static CoreVector3[] BuildFrustumCorners(ICamera camera, float nearDistance, float farDistance)
        {
            float tan = MathF.Tan(camera.FieldOfView * 0.5f);
            CoreVector3 forward = camera.Forward.Normalized();
            CoreVector3 right = camera.Right.Normalized();
            CoreVector3 up = camera.Up.Normalized();
            CoreVector3 position = camera.Position;

            CoreVector3 nearCenter = position + forward * nearDistance;
            CoreVector3 farCenter = position + forward * farDistance;
            float nearHeight = 2f * tan * nearDistance;
            float nearWidth = nearHeight * camera.AspectRatio;
            float farHeight = 2f * tan * farDistance;
            float farWidth = farHeight * camera.AspectRatio;

            return new[]
            {
                nearCenter - right * (nearWidth * 0.5f) - up * (nearHeight * 0.5f),
                nearCenter + right * (nearWidth * 0.5f) - up * (nearHeight * 0.5f),
                nearCenter - right * (nearWidth * 0.5f) + up * (nearHeight * 0.5f),
                nearCenter + right * (nearWidth * 0.5f) + up * (nearHeight * 0.5f),
                farCenter - right * (farWidth * 0.5f) - up * (farHeight * 0.5f),
                farCenter + right * (farWidth * 0.5f) - up * (farHeight * 0.5f),
                farCenter - right * (farWidth * 0.5f) + up * (farHeight * 0.5f),
                farCenter + right * (farWidth * 0.5f) + up * (farHeight * 0.5f)
            };
        }

        private static CoreVector3 TransformPoint(CoreVector3 point, CoreMatrix4x4 matrix) => point * matrix;

        private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);
    }
}
