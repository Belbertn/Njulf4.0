using System;
using System.Collections.Generic;
using System.Linq;

namespace Njulf.Rendering.Diagnostics;

public sealed record DeviceRequirementOverride(
    IReadOnlyList<string> MissingDeviceExtensions,
    IReadOnlyList<string> MissingFeatures,
    IReadOnlyList<string> MissingQueueFamilies)
{
    public static DeviceRequirementOverride Empty { get; } = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        Array.Empty<string>());

    public bool HasOverrides =>
        MissingDeviceExtensions.Count != 0 ||
        MissingFeatures.Count != 0 ||
        MissingQueueFamilies.Count != 0;

    public static DeviceRequirementOverride FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("NJULF_RENDERER_DEVICE_REQUIREMENT_OVERRIDE");
        if (string.IsNullOrWhiteSpace(value))
            return Empty;

        var extensions = new List<string>();
        var features = new List<string>();
        var queues = new List<string>();
        foreach (string token in value.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("extension:", StringComparison.OrdinalIgnoreCase))
                extensions.Add(token["extension:".Length..]);
            else if (token.StartsWith("feature:", StringComparison.OrdinalIgnoreCase))
                features.Add(token["feature:".Length..]);
            else if (token.StartsWith("queue:", StringComparison.OrdinalIgnoreCase))
                queues.Add(token["queue:".Length..]);
            else
                features.Add(token);
        }

        return new DeviceRequirementOverride(
            extensions.Distinct(StringComparer.Ordinal).ToArray(),
            features.Distinct(StringComparer.Ordinal).ToArray(),
            queues.Distinct(StringComparer.Ordinal).ToArray());
    }
}
