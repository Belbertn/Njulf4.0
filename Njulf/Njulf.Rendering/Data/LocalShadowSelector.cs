using System;
using System.Collections.Generic;
using Njulf.Core.Interfaces;
using Njulf.Rendering.Resources;
using NumericsVector3 = System.Numerics.Vector3;

namespace Njulf.Rendering.Data
{
    public sealed class LocalShadowSelector
    {
        private readonly HashSet<int> _previousSpotSelection = new();
        private readonly HashSet<int> _previousPointSelection = new();

        public LocalShadowSelection Select(
            Light[] lights,
            ICamera camera,
            ShadowSettings settings,
            int spotAtlasCapacityOverride = -1,
            int pointShadowCapacityOverride = -1)
        {
            if (lights == null)
                throw new ArgumentNullException(nameof(lights));

            return Select(lights.AsSpan(), camera, settings, spotAtlasCapacityOverride, pointShadowCapacityOverride);
        }

        public LocalShadowSelection Select(
            ReadOnlySpan<Light> lights,
            ICamera camera,
            ShadowSettings settings,
            int spotAtlasCapacityOverride = -1,
            int pointShadowCapacityOverride = -1)
        {
            if (camera == null)
                throw new ArgumentNullException(nameof(camera));
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            var spotCandidates = new List<SelectedLocalShadow>();
            var pointCandidates = new List<SelectedLocalShadow>();

            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (!IsCandidate(light, settings))
                    continue;

                float score = Score(light, camera, i);
                if (light.Type == LightType.Spot && IsValidSpot(light))
                    spotCandidates.Add(new SelectedLocalShadow(i, light, score + (_previousSpotSelection.Contains(i) ? 0.05f : 0f)));
                else if (light.Type == LightType.Point)
                    pointCandidates.Add(new SelectedLocalShadow(i, light, score + (_previousPointSelection.Contains(i) ? 0.05f : 0f)));
            }

            int spotAtlasCapacity = spotAtlasCapacityOverride >= 0
                ? spotAtlasCapacityOverride
                : settings.SpotShadowAtlasCapacity;
            int pointShadowCapacity = pointShadowCapacityOverride >= 0
                ? pointShadowCapacityOverride
                : settings.MaxShadowedPointLights;
            int spotBudget = settings.SpotShadowsEnabled ? Math.Min(settings.MaxShadowedSpotLights, spotAtlasCapacity) : 0;
            int pointBudget = settings.PointShadowsEnabled ? Math.Min(settings.MaxShadowedPointLights, pointShadowCapacity) : 0;

            SelectedLocalShadow[] selectedSpots = SelectTop(spotCandidates, spotBudget);
            SelectedLocalShadow[] selectedPoints = SelectTop(pointCandidates, pointBudget);
            Remember(_previousSpotSelection, selectedSpots);
            Remember(_previousPointSelection, selectedPoints);

            return new LocalShadowSelection
            {
                SpotLights = selectedSpots,
                PointLights = selectedPoints,
                SpotCandidateCount = spotCandidates.Count,
                PointCandidateCount = pointCandidates.Count,
                SpotRejectedByBudgetCount = Math.Max(0, spotCandidates.Count - selectedSpots.Length),
                PointRejectedByBudgetCount = Math.Max(0, pointCandidates.Count - selectedPoints.Length),
                SpotAtlasCapacity = spotAtlasCapacity
            };
        }

        private static bool IsCandidate(Light light, ShadowSettings settings)
        {
            return light.CastsShadows &&
                   light.Intensity >= settings.MinimumLocalShadowIntensity &&
                   light.Range >= settings.MinimumLocalShadowRange &&
                   (light.Type == LightType.Spot || light.Type == LightType.Point);
        }

        private static bool IsValidSpot(Light light)
        {
            return light.SpotAngle > 0.01f && light.SpotAngle < MathF.PI * 0.99f;
        }

        private static float Score(Light light, ICamera camera, int lightIndex)
        {
            NumericsVector3 toLight = light.Position - new NumericsVector3(camera.Position.X, camera.Position.Y, camera.Position.Z);
            float distance = MathF.Max(toLight.Length(), 0.001f);
            float projectedSize = light.Range / distance;
            return light.ShadowPriority * 1000f +
                   projectedSize * 100f +
                   MathF.Max(light.Intensity, 0f) +
                   1f / distance -
                   lightIndex * 0.0001f;
        }

        private static SelectedLocalShadow[] SelectTop(List<SelectedLocalShadow> candidates, int budget)
        {
            if (budget <= 0 || candidates.Count == 0)
                return [];

            candidates.Sort(static (a, b) => b.Score.CompareTo(a.Score));
            int count = Math.Min(budget, candidates.Count);
            var selected = new SelectedLocalShadow[count];
            candidates.CopyTo(0, selected, 0, count);
            return selected;
        }

        private static void Remember(HashSet<int> previousSelection, SelectedLocalShadow[] selected)
        {
            previousSelection.Clear();
            for (int i = 0; i < selected.Length; i++)
                previousSelection.Add(selected[i].LightIndex);
        }
    }
}
