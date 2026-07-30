using System.Reflection;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Exact in-process rollback state for a quality-switch qualification run.
/// Render settings expose stable child objects, so scalar values can be
/// restored without replacing renderer-observed settings instances.
/// </summary>
internal sealed class SampleRenderSettingsSnapshot
{
    private readonly RenderSettings _settings;
    private readonly RenderQualityPreset _qualityPreset;
    private readonly IReadOnlyList<CapturedProperty> _properties;
    private readonly IReadOnlyList<SimpleDdgiAuthoredVolume> _simpleDdgiAuthoredVolumes;
    private readonly string _fingerprint;

    private SampleRenderSettingsSnapshot(
        RenderSettings settings,
        RenderQualityPreset qualityPreset,
        IReadOnlyList<CapturedProperty> properties,
        IReadOnlyList<SimpleDdgiAuthoredVolume> simpleDdgiAuthoredVolumes,
        string fingerprint)
    {
        _settings = settings;
        _qualityPreset = qualityPreset;
        _properties = properties;
        _simpleDdgiAuthoredVolumes = simpleDdgiAuthoredVolumes;
        _fingerprint = fingerprint;
    }

    public static SampleRenderSettingsSnapshot Capture(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var properties = new List<CapturedProperty>(256);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CaptureContainer(settings, "RenderSettings", properties, visited);
        return new SampleRenderSettingsSnapshot(
            settings,
            settings.QualityPreset,
            properties,
            settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.ToArray(),
            SampleRenderSettingsFingerprint.Capture(settings));
    }

    public void Restore(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!ReferenceEquals(settings, _settings))
        {
            throw new ArgumentException(
                "A render-settings snapshot can only restore the instance from which it was captured.",
                nameof(settings));
        }

        settings.ApplyQualityPreset(_qualityPreset);

        // Restore every property once, then retry only properties that remain
        // different. This is important for fan-out setters such as the legacy
        // DDGI vertical offset: replaying an already-correct aggregate setter
        // would overwrite the individual per-cascade values on every pass.
        IReadOnlyList<CapturedProperty> pending = _properties;
        const int maximumRestorePasses = 8;
        for (int pass = 0;
             pass < maximumRestorePasses && pending.Count > 0;
             pass++)
        {
            foreach (CapturedProperty captured in pending)
            {
                try
                {
                    captured.Property.SetValue(captured.Target, captured.Value);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not restore render setting '{captured.Path}'.",
                        ex);
                }
            }

            var mismatched = new List<CapturedProperty>();
            foreach (CapturedProperty captured in _properties)
            {
                object? restoredValue;
                try
                {
                    restoredValue = captured.Property.GetValue(captured.Target);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Could not verify restored render setting '{captured.Path}'.",
                        ex);
                }

                if (!Equals(restoredValue, captured.Value))
                    mismatched.Add(captured);
            }
            pending = mismatched;
        }

        if (pending.Count > 0)
        {
            CapturedProperty first = pending[0];
            object? actual = first.Property.GetValue(first.Target);
            throw new InvalidOperationException(
                $"The render setting '{first.Path}' did not restore exactly after " +
                $"{maximumRestorePasses} passes. expected={first.Value}, actual={actual}.");
        }

        settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Clear();
        foreach (SimpleDdgiAuthoredVolume volume in _simpleDdgiAuthoredVolumes)
            settings.GlobalIllumination.SimpleDdgiAuthoredVolumes.Add(volume);

        string restoredFingerprint = SampleRenderSettingsFingerprint.Capture(settings);
        if (!string.Equals(restoredFingerprint, _fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The render-settings snapshot could not restore every publicly observable setting. " +
                $"expected={_fingerprint}, actual={restoredFingerprint}.");
        }
    }

    private static void CaptureContainer(
        object container,
        string path,
        ICollection<CapturedProperty> captured,
        ISet<object> visited)
    {
        if (!visited.Add(container))
            return;

        PropertyInfo[] properties = container.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetIndexParameters().Length == 0 &&
                property.GetMethod?.IsPublic == true)
            .OrderBy(static property => property.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (PropertyInfo property in properties)
        {
            object? value;
            try
            {
                value = property.GetValue(container);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not capture render setting '{path}.{property.Name}'.",
                    ex);
            }

            string propertyPath = $"{path}.{property.Name}";
            if (property.SetMethod?.IsPublic == true &&
                IsScalar(property.PropertyType))
            {
                captured.Add(new CapturedProperty(
                    container,
                    property,
                    value,
                    propertyPath));
                continue;
            }

            if (value != null && IsSettingsContainer(property.PropertyType))
            {
                CaptureContainer(value, propertyPath, captured, visited);
            }
        }
    }

    private static bool IsScalar(Type type) =>
        type.IsValueType ||
        type == typeof(string);

    private static bool IsSettingsContainer(Type type)
    {
        if (!type.IsClass || type == typeof(string))
            return false;
        string? typeNamespace = type.Namespace;
        if (typeNamespace == null ||
            !typeNamespace.StartsWith("Njulf.Rendering", StringComparison.Ordinal))
        {
            return false;
        }

        return type.Name.EndsWith("Settings", StringComparison.Ordinal) ||
            type.Name.EndsWith("Policy", StringComparison.Ordinal);
    }

    private sealed record CapturedProperty(
        object Target,
        PropertyInfo Property,
        object? Value,
        string Path);
}
