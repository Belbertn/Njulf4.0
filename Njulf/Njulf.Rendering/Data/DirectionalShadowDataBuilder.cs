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
        public static GPUShadowData Build(
            ICamera camera,
            NumericsVector3 lightDirection,
            ShadowSettings settings,
            int selectedLightIndex,
            float shadowStrength)
        {
            var transientState = new DirectionalShadowStabilizationState();
            return Build(
                camera,
                lightDirection,
                settings,
                selectedLightIndex,
                shadowStrength,
                transientState,
                stableLightIdentity: 0UL,
                shadowResourceGeneration: 0u);
        }

        public static GPUShadowData Build(
            ICamera camera,
            NumericsVector3 lightDirection,
            ShadowSettings settings,
            int selectedLightIndex,
            float shadowStrength,
            DirectionalShadowStabilizationState stabilizationState,
            ulong stableLightIdentity,
            uint shadowResourceGeneration)
        {
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (stabilizationState == null)
                throw new ArgumentNullException(nameof(stabilizationState));

            int cascadeCount = settings.DirectionalCascadeCount;
            float near = MathF.Max(camera.NearPlane, 0.001f);
            float far = MathF.Min(camera.FarPlane, MathF.Max(near + 0.01f, settings.MaxShadowDistance));
            CoreVector3 lightDir = ToCore(lightDirection);
            ulong configurationSignature = CreateConfigurationSignature(
                camera,
                settings,
                near,
                far,
                shadowResourceGeneration);
            DirectionalShadowStabilizationState.BasisFrame basis =
                stabilizationState.BeginFrame(
                    stableLightIdentity,
                    lightDir,
                    configurationSignature);
            lightDir = basis.Direction;

            CoreMatrix4x4[] matrices = new CoreMatrix4x4[ShadowSettings.MaxDirectionalCascades];
            float[] splits = CalculateCascadeSplits(
                near,
                far,
                cascadeCount,
                settings.DirectionalCascadeSplitLambda);
            float[] transitionWidths = CalculateCascadeTransitionWidths(
                near,
                far,
                splits,
                cascadeCount,
                settings.DirectionalCascadeBlendFraction);
            for (int i = 0; i < cascadeCount; i++)
            {
                float cascadeNear = i == 0 ? near : splits[i - 1];
                float cascadeFar = splits[i];
                if (i > 0)
                    cascadeNear = MathF.Max(near, cascadeNear - transitionWidths[i - 1]);
                if (i + 1 < cascadeCount)
                    cascadeFar = MathF.Min(far, cascadeFar + transitionWidths[i]);

                matrices[i] = BuildCascadeMatrix(
                    camera,
                    lightDir,
                    cascadeNear,
                    cascadeFar,
                    settings,
                    stabilizationState,
                    basis,
                    i);
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
                    ResolveShadowStrength(shadowStrength),
                    settings.NormalBias,
                    settings.DirectionalShadowMapSize,
                    settings.PcfRadius),
                Indices = new CoreVector4(
                    settings.DirectionalShadowsEnabled ? 1f : 0f,
                    cascadeCount,
                    BindlessIndex.DirectionalShadowTextureBase,
                    selectedLightIndex),
                CascadeTransitionData = new CoreVector4(
                    settings.DirectionalCascadeBlendFraction,
                    near,
                    far,
                    0f)
            };
        }

        public static GPUDirectionalShadowParameters BuildParameters(
            ShadowSettings settings,
            ReadOnlySpan<DirectionalShadowCascadeFitDiagnostics> diagnostics,
            DirectionalShadowMode effectiveMode = DirectionalShadowMode.Cascaded,
            float sunAngularRadiusRadians = 0f,
            RaySceneReadinessSnapshot rayScene = default,
            bool csmTemporalActive = false,
            DirectionalShadowQualificationLevel qualificationLevel =
                DirectionalShadowQualificationLevel.Developer,
            uint screenResourceGeneration = 0u,
            bool historyValid = false)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            return new GPUDirectionalShadowParameters
            {
                CascadeWorldTexelSizes = new CoreVector4(
                    GetWorldTexelSize(diagnostics, 0),
                    GetWorldTexelSize(diagnostics, 1),
                    GetWorldTexelSize(diagnostics, 2),
                    GetWorldTexelSize(diagnostics, 3)),
                FilterAndBias = new CoreVector4(
                    (float)settings.DirectionalFilterMode,
                    (float)settings.DirectionalBiasMode,
                    0.75f,
                    settings.NormalBias),
                ModeAndRayDistance = new CoreVector4(
                    (float)settings.RequestedDirectionalShadowMode,
                    (float)effectiveMode,
                    settings.DirectionalContactShadowDistance,
                    Math.Clamp(sunAngularRadiusRadians, 0f, MathF.PI * 0.25f)),
                TemporalAndSampling = new CoreVector4(
                    settings.DirectionalSoftRecoveryRayCount,
                    settings.DirectionalSoftHistoryLength,
                    settings.DirectionalSoftSpatialPassCount,
                    settings.DirectionalTransparentSoftRayCount),
                RaySceneBoundsMinimum = new CoreVector4(
                    rayScene.CoverageMinimum,
                    rayScene.HasQualifiedBounds ? 1f : 0f),
                RaySceneBoundsMaximum = new CoreVector4(
                    rayScene.CoverageMaximum,
                    rayScene.HasQualifiedBounds ? 1f : 0f),
                RuntimeFlags = new CoreVector4(
                    csmTemporalActive ? 1f : 0f,
                    (float)qualificationLevel,
                    screenResourceGeneration,
                    historyValid ? 1f : 0f)
            };
        }

        private static float GetWorldTexelSize(
            ReadOnlySpan<DirectionalShadowCascadeFitDiagnostics> diagnostics,
            int cascade)
        {
            return (uint)cascade < (uint)diagnostics.Length &&
                   float.IsFinite(diagnostics[cascade].WorldTexelSize)
                ? MathF.Max(0f, diagnostics[cascade].WorldTexelSize)
                : 0f;
        }

        private static float ResolveShadowStrength(float shadowStrength)
        {
            // Match local-shadow and packed-light handling for legacy Lights.
            return Math.Clamp(shadowStrength <= 0f ? 1f : shadowStrength, 0f, 1f);
        }

        public static float[] CalculateCascadeSplits(float nearPlane, float farPlane, int cascadeCount)
        {
            return CalculateCascadeSplits(nearPlane, farPlane, cascadeCount, 0.5f);
        }

        public static float[] CalculateCascadeSplits(
            float nearPlane,
            float farPlane,
            int cascadeCount,
            float splitLambda)
        {
            cascadeCount = cascadeCount < 1 ? 1 : cascadeCount > ShadowSettings.MaxDirectionalCascades ? ShadowSettings.MaxDirectionalCascades : cascadeCount;
            nearPlane = MathF.Max(nearPlane, 0.001f);
            farPlane = MathF.Max(farPlane, nearPlane + 0.001f);

            var splits = new float[ShadowSettings.MaxDirectionalCascades];
            float range = farPlane - nearPlane;
            float ratio = farPlane / nearPlane;
            splitLambda = Math.Clamp(splitLambda, 0f, 1f);

            for (int i = 0; i < cascadeCount; i++)
            {
                float p = (i + 1f) / cascadeCount;
                float log = nearPlane * MathF.Pow(ratio, p);
                float uniform = nearPlane + range * p;
                splits[i] = splitLambda * log + (1f - splitLambda) * uniform;
            }

            splits[cascadeCount - 1] = farPlane;
            for (int i = cascadeCount; i < splits.Length; i++)
                splits[i] = farPlane;

            return splits;
        }

        private static float[] CalculateCascadeTransitionWidths(
            float nearPlane,
            float farPlane,
            float[] splits,
            int cascadeCount,
            float blendFraction)
        {
            var widths = new float[ShadowSettings.MaxDirectionalCascades];
            float fraction = Math.Clamp(blendFraction, 0.02f, 0.30f);
            for (int boundary = 0; boundary + 1 < cascadeCount; boundary++)
            {
                float previousBoundary = boundary == 0 ? nearPlane : splits[boundary - 1];
                float boundaryDistance = splits[boundary];
                float nextBoundary = boundary + 1 == cascadeCount - 1
                    ? farPlane
                    : splits[boundary + 1];
                float previousSpan = MathF.Max(0.001f, boundaryDistance - previousBoundary);
                float nextSpan = MathF.Max(0.001f, nextBoundary - boundaryDistance);
                widths[boundary] = MathF.Min(previousSpan, nextSpan) * fraction;
            }

            return widths;
        }

        internal static CoreMatrix4x4 BuildCascadeMatrix(
            ICamera camera,
            CoreVector3 lightDirection,
            float nearDistance,
            float farDistance,
            ShadowSettings settings,
            DirectionalShadowStabilizationState stabilizationState,
            DirectionalShadowStabilizationState.BasisFrame basis,
            int cascadeIndex)
        {
            CoreVector3[] corners = BuildFrustumCorners(camera, nearDistance, farDistance);
            CoreVector3 center = CoreVector3.Zero;
            for (int i = 0; i < corners.Length; i++)
                center += corners[i];
            center /= corners.Length;

            // A rotation-only light view makes the texel grid world anchored.
            // Centering the view on the camera slice would make its light-space
            // centre zero every frame and defeat snapping.
            CoreMatrix4x4 lightView = CoreMatrix4x4.CreateLookAt(
                -lightDirection * 100f,
                CoreVector3.Zero,
                basis.Up);

            CoreVector3 min = TransformPoint(corners[0], lightView);
            CoreVector3 max = min;
            double radiusSquared = DistanceSquared(corners[0], center);
            for (int i = 1; i < corners.Length; i++)
            {
                CoreVector3 lightSpaceCorner = TransformPoint(corners[i], lightView);
                min = CoreVector3.Min(min, lightSpaceCorner);
                max = CoreVector3.Max(max, lightSpaceCorner);
                radiusSquared = Math.Max(radiusSquared, DistanceSquared(corners[i], center));
            }

            double receiverRadius = Math.Max(Math.Sqrt(radiusSquared), 0.0005);
            double guardTexels = ResolveGuardTexels(settings);
            double mapSize = Math.Max(1u, settings.DirectionalShadowMapSize);
            double guardDenominator = Math.Max(0.5, 1.0 - 2.0 * guardTexels / mapSize);
            double halfExtent = receiverRadius / guardDenominator;
            float width = (float)Math.Max(halfExtent * 2.0, 0.001);
            float height = width;

            CoreVector3 lightSpaceCenter = TransformPoint(center, lightView);
            double rawCenterX = lightSpaceCenter.X;
            double rawCenterY = lightSpaceCenter.Y;
            double texelSizeDouble = width / mapSize;
            float centerX = (float)(Math.Round(
                rawCenterX / texelSizeDouble,
                MidpointRounding.AwayFromZero) * texelSizeDouble);
            float centerY = (float)(Math.Round(
                rawCenterY / texelSizeDouble,
                MidpointRounding.AwayFromZero) * texelSizeDouble);

            min.X = centerX - width * 0.5f;
            max.X = centerX + width * 0.5f;
            min.Y = centerY - height * 0.5f;
            max.Y = centerY + height * 0.5f;
            float rawDepthMinimum = min.Z - settings.DirectionalCasterExtrusionDistance;
            float rawDepthMaximum = max.Z + settings.DirectionalCasterExtrusionDistance;
            stabilizationState.StabilizeDepth(
                cascadeIndex,
                rawDepthMinimum,
                rawDepthMaximum,
                texelSizeDouble,
                out float stableDepthMinimum,
                out float stableDepthMaximum);
            min.Z = stableDepthMinimum;
            max.Z = stableDepthMaximum;

            CoreMatrix4x4 crop = CoreMatrix4x4.CreateTranslation(new CoreVector3(
                -(min.X + max.X) * 0.5f,
                -(min.Y + max.Y) * 0.5f,
                0f));
            CoreMatrix4x4 projection = CoreMatrix4x4.CreateOrthographic(width, height, -max.Z, -min.Z);
            stabilizationState.RecordDiagnostics(
                cascadeIndex,
                new DirectionalShadowCascadeFitDiagnostics(
                    cascadeIndex,
                    basis.Direction,
                    basis.Right,
                    basis.Up,
                    (float)rawCenterX,
                    (float)rawCenterY,
                    centerX,
                    centerY,
                    width,
                    (float)texelSizeDouble,
                    (float)guardTexels,
                    rawDepthMinimum,
                    rawDepthMaximum,
                    stableDepthMinimum,
                    stableDepthMaximum,
                    basis.ResetReason));
            return lightView * crop * projection;
        }

        private static double ResolveGuardTexels(ShadowSettings settings)
        {
            // The manual PCF footprint reaches radius+1 texels from its base
            // texel. Add half a texel for nearest-grid snapping. Radius zero is
            // still a bilinear four-comparison footprint.
            return Math.Max(1.5, settings.PcfRadius + 1.5);
        }

        private static double DistanceSquared(CoreVector3 left, CoreVector3 right)
        {
            double x = (double)left.X - right.X;
            double y = (double)left.Y - right.Y;
            double z = (double)left.Z - right.Z;
            return x * x + y * y + z * z;
        }

        internal static CoreVector3[] BuildFrustumCorners(ICamera camera, float nearDistance, float farDistance)
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

        private static ulong CreateConfigurationSignature(
            ICamera camera,
            ShadowSettings settings,
            float effectiveNear,
            float effectiveFar,
            uint shadowResourceGeneration)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong hash = offset;
            Add((uint)settings.DirectionalCascadeCount);
            Add(settings.DirectionalShadowMapSize);
            Add((uint)settings.PcfRadius);
            Add((uint)settings.DirectionalFilterMode);
            Add(BitConverter.SingleToUInt32Bits(settings.DirectionalCascadeSplitLambda));
            Add(BitConverter.SingleToUInt32Bits(settings.DirectionalCascadeBlendFraction));
            Add(BitConverter.SingleToUInt32Bits(settings.DirectionalCasterExtrusionDistance));
            Add(BitConverter.SingleToUInt32Bits(camera.FieldOfView));
            Add(BitConverter.SingleToUInt32Bits(camera.AspectRatio));
            Add(BitConverter.SingleToUInt32Bits(effectiveNear));
            Add(BitConverter.SingleToUInt32Bits(effectiveFar));
            Add(shadowResourceGeneration);
            return hash;

            void Add(uint value)
            {
                hash ^= value;
                hash *= prime;
            }
        }

        private static CoreVector3 TransformPoint(CoreVector3 point, CoreMatrix4x4 matrix) => point * matrix;

        private static CoreVector3 ToCore(NumericsVector3 value) => new(value.X, value.Y, value.Z);
    }
}
