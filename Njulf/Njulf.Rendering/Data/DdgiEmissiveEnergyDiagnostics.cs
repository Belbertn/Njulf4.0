using System;
using Njulf.Core.Math;

namespace Njulf.Rendering.Data;

/// <summary>
/// Exposure-independent energy view of the currently selected heterogeneous
/// emissive-source table. Mesh values use world-space square metres and
/// Lambertian exitance; macro emitters already store integrated radiant power.
/// </summary>
public readonly record struct DdgiEmissiveEnergyDiagnostics(
    int SelectedMeshSourceCount,
    int SelectedMacroSourceCount,
    Vector3 AreaWeightedAverageRadiance,
    float PeakSelectedLuminanceNits,
    double SelectedCoveredAreaSquareMeters,
    double IntegratedPowerRed,
    double IntegratedPowerGreen,
    double IntegratedPowerBlue,
    double IntegratedPowerLuminance,
    float SelectedProbability)
{
    public static DdgiEmissiveEnergyDiagnostics Calculate(
        ReadOnlySpan<GPUDdgiEmissiveSource> sources,
        DdgiEmissiveTriangleTableStats meshTableStats)
    {
        double weightedRed = 0.0;
        double weightedGreen = 0.0;
        double weightedBlue = 0.0;
        double weightedArea = 0.0;
        double area = 0.0;
        double powerRed = 0.0;
        double powerGreen = 0.0;
        double powerBlue = 0.0;
        double macroProposalImportance = 0.0;
        float peakLuminance = 0f;
        int meshCount = 0;
        int macroCount = 0;

        foreach (GPUDdgiEmissiveSource source in sources)
        {
            DdgiEmissiveSourceFlags flags = DdgiEmissiveTriangleTable.DecodeFlags(source);
            double red = Math.Max(source.RadianceSelectionProbability.X, 0f);
            double green = Math.Max(source.RadianceSelectionProbability.Y, 0f);
            double blue = Math.Max(source.RadianceSelectionProbability.Z, 0f);
            if ((flags & DdgiEmissiveSourceFlags.MacroEmitter) != 0)
            {
                macroCount++;
                powerRed += red;
                powerGreen += green;
                powerBlue += blue;
                macroProposalImportance += Luminance(red, green, blue) / (2.0 * Math.PI);
                continue;
            }
            if ((flags & DdgiEmissiveSourceFlags.Triangle) == 0)
                continue;

            meshCount++;
            double geometricArea = Math.Max(source.Vertex0Area.W, 0f);
            double sideWeight = (flags & DdgiEmissiveSourceFlags.DoubleSided) != 0
                ? 2.0
                : 1.0;
            double radiatingArea = geometricArea * sideWeight;
            area += geometricArea;
            weightedArea += radiatingArea;
            weightedRed += red * radiatingArea;
            weightedGreen += green * radiatingArea;
            weightedBlue += blue * radiatingArea;
            powerRed += Math.PI * red * radiatingArea;
            powerGreen += Math.PI * green * radiatingArea;
            powerBlue += Math.PI * blue * radiatingArea;
            peakLuminance = Math.Max(
                peakLuminance,
                EmissivePhotometry.SceneLinearLuminanceToNits(
                    (float)Luminance(red, green, blue)));
        }

        Vector3 average = weightedArea > 0.0
            ? new Vector3(
                (float)(weightedRed / weightedArea),
                (float)(weightedGreen / weightedArea),
                (float)(weightedBlue / weightedArea))
            : Vector3.Zero;
        double selectedImportance = Math.Max(meshTableStats.SelectedImportance, 0.0) +
                                    macroProposalImportance;
        double totalImportance = Math.Max(meshTableStats.TotalImportance, 0.0) +
                                 macroProposalImportance;
        float selectedProbability = totalImportance > 0.0
            ? (float)Math.Clamp(selectedImportance / totalImportance, 0.0, 1.0)
            : 0f;

        return new DdgiEmissiveEnergyDiagnostics(
            meshCount,
            macroCount,
            average,
            peakLuminance,
            area,
            powerRed,
            powerGreen,
            powerBlue,
            Luminance(powerRed, powerGreen, powerBlue),
            selectedProbability);
    }

    private static double Luminance(double red, double green, double blue) =>
        0.2126 * red + 0.7152 * green + 0.0722 * blue;
}

public enum DdgiEmissiveEnergyChangeKind
{
    None,
    MeshScale,
    RadianceOrTexture
}

public readonly record struct DdgiEmissiveEnergyChangeWarning(
    DdgiEmissiveEnergyChangeKind Kind,
    double AreaRatio,
    double AverageLuminanceRatio,
    double IntegratedPowerRatio,
    string Message)
{
    public bool HasWarning => Kind != DdgiEmissiveEnergyChangeKind.None;
}

/// <summary>
/// Pure, conservative detector for large energy changes that commonly result
/// from accidental mesh scaling or texture/radiance edits. It deliberately
/// ignores source creation/removal and small editing noise.
/// </summary>
public static class DdgiEmissiveEnergyChangeEvaluator
{
    public static DdgiEmissiveEnergyChangeWarning Evaluate(
        DdgiEmissiveEnergyDiagnostics previous,
        DdgiEmissiveEnergyDiagnostics current)
    {
        if (previous.SelectedMeshSourceCount <= 0 ||
            current.SelectedMeshSourceCount <= 0 ||
            previous.SelectedMacroSourceCount != current.SelectedMacroSourceCount)
        {
            return default;
        }

        double previousAverage = EmissivePhotometry.Luminance(
            previous.AreaWeightedAverageRadiance);
        double currentAverage = EmissivePhotometry.Luminance(
            current.AreaWeightedAverageRadiance);
        if (previous.SelectedCoveredAreaSquareMeters <= 1e-12 ||
            previousAverage <= 1e-12 ||
            previous.IntegratedPowerLuminance <= 1e-12)
        {
            return default;
        }

        double areaRatio = current.SelectedCoveredAreaSquareMeters /
                           previous.SelectedCoveredAreaSquareMeters;
        double averageRatio = currentAverage / previousAverage;
        double powerRatio = current.IntegratedPowerLuminance /
                            previous.IntegratedPowerLuminance;
        if (!double.IsFinite(areaRatio) ||
            !double.IsFinite(averageRatio) ||
            !double.IsFinite(powerRatio))
        {
            return default;
        }

        bool areaChanged = OutsideRatio(areaRatio, 0.8, 1.25);
        bool radianceStable = !OutsideRatio(averageRatio, 0.9, 1.1);
        if (areaChanged && radianceStable && OutsideRatio(powerRatio, 0.8, 1.25))
        {
            return new DdgiEmissiveEnergyChangeWarning(
                DdgiEmissiveEnergyChangeKind.MeshScale,
                areaRatio,
                averageRatio,
                powerRatio,
                $"Emissive world-space area changed {areaRatio:0.###}x while average radiance stayed stable; integrated power changed {powerRatio:0.###}x. Verify mesh scale or accept the physical energy change.");
        }

        bool areaStable = !OutsideRatio(areaRatio, 0.9, 1.1);
        if (areaStable &&
            OutsideRatio(averageRatio, 2.0 / 3.0, 1.5) &&
            OutsideRatio(powerRatio, 2.0 / 3.0, 1.5))
        {
            return new DdgiEmissiveEnergyChangeWarning(
                DdgiEmissiveEnergyChangeKind.RadianceOrTexture,
                areaRatio,
                averageRatio,
                powerRatio,
                $"Emissive radiance changed {averageRatio:0.###}x at stable area; integrated power changed {powerRatio:0.###}x. Verify texture import, photometric units, or the artistic multiplier.");
        }

        return default;
    }

    private static bool OutsideRatio(double value, double minimum, double maximum) =>
        value < minimum || value > maximum;
}
