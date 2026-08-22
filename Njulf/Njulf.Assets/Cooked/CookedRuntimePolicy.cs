namespace Njulf.Assets.Cooked;

public static class CookedRuntimePolicy
{
    public const string AllowSourceFallbackVariable = "NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD";
    public const string StrictVariable = "NJULF_COOKED_ASSET_STRICT";
    public const string RequireSignatureVariable = "NJULF_COOKED_ASSET_REQUIRE_SIGNATURE";

    public static bool AllowSourceFallback => IsEnvironmentEnabled(
        AllowSourceFallbackVariable,
        defaultValue: DefaultAllowSourceFallback);
    public static bool Strict => IsEnvironmentEnabled(StrictVariable, defaultValue: true);
    public static bool RequireSignature => IsEnvironmentEnabled(RequireSignatureVariable, DefaultRequireSignature);

    private const bool DefaultRequireSignature = false;

#if NJULF_DEVELOPMENT
    private const bool DefaultAllowSourceFallback = true;
#else
    private const bool DefaultAllowSourceFallback = false;
#endif

    public static CookedAssetReaderFlags ReaderFlags
    {
        get
        {
            CookedAssetReaderFlags flags = CookedAssetReaderFlags.StrictSourceHash | CookedAssetReaderFlags.PreferMemoryMapped;
            if (RequireSignature)
                flags |= CookedAssetReaderFlags.RequireSignature;
            return flags;
        }
    }

    public static bool IsEnvironmentEnabled(string name, bool defaultValue)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return ParseBooleanSetting(name, value, defaultValue);
    }

    internal static bool ParseBooleanSetting(
        string name,
        string? value,
        bool defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (value is null)
            return defaultValue;
        string normalized = value.Trim();
        if (bool.TryParse(normalized, out bool enabled))
            return enabled;
        return normalized.ToLowerInvariant() switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"Environment variable '{name}' has invalid Boolean value " +
                $"'{value}'. Use true/false, 1/0, yes/no, or on/off.")
        };
    }
}
