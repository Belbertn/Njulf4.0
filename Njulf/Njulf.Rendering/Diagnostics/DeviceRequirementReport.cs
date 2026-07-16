using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Diagnostics;

public sealed record DeviceRequirementReport(
    string DeviceName,
    uint VendorId,
    uint DeviceId,
    string ApiVersion,
    string DriverVersion,
    IReadOnlyList<string> MissingInstanceExtensions,
    IReadOnlyList<string> MissingInstanceLayers,
    IReadOnlyList<string> MissingDeviceExtensions,
    IReadOnlyList<string> MissingFeatures,
    IReadOnlyList<string> MissingQueueFamilies,
    bool IsSupported)
{
    public static DeviceRequirementReport EmptyUnsupported(string reason)
    {
        return new DeviceRequirementReport(
            string.Empty,
            0,
            0,
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { reason },
            Array.Empty<string>(),
            IsSupported: false);
    }

    public string FormatSummary()
    {
        if (IsSupported)
            return $"Vulkan device '{DeviceName}' satisfies required renderer capabilities.";

        var groups = new List<string>();
        AddGroup(groups, "missing instance extensions", MissingInstanceExtensions);
        AddGroup(groups, "missing instance layers", MissingInstanceLayers);
        AddGroup(groups, "missing device extensions", MissingDeviceExtensions);
        AddGroup(groups, "missing features", MissingFeatures);
        AddGroup(groups, "missing queue families", MissingQueueFamilies);

        string prefix = string.IsNullOrWhiteSpace(DeviceName)
            ? "Vulkan device is unsupported"
            : $"Vulkan device '{DeviceName}' is unsupported";

        return groups.Count == 0 ? prefix : $"{prefix}: {string.Join("; ", groups)}.";
    }

    private static void AddGroup(List<string> groups, string label, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
            groups.Add($"{label}: {string.Join(", ", values.Distinct(StringComparer.Ordinal))}");
    }
}
