namespace Njulf.Assets.Cooked;

public static class CookedRuntimePolicy
{
    public const string AllowSourceFallbackVariable = "NJULF_ALLOW_SOURCE_ASSET_RUNTIME_LOAD";
    public const string StrictVariable = "NJULF_COOKED_ASSET_STRICT";
    public const string RequireSignatureVariable = "NJULF_COOKED_ASSET_REQUIRE_SIGNATURE";

    public static bool AllowSourceFallback => IsEnvironmentEnabled(AllowSourceFallbackVariable, defaultValue: false);
    public static bool Strict => IsEnvironmentEnabled(StrictVariable, defaultValue: true);
    public static bool RequireSignature => IsEnvironmentEnabled(RequireSignatureVariable, DefaultRequireSignature);

    private const bool DefaultRequireSignature = false;

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
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (bool.TryParse(value, out bool enabled))
            return enabled;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "yes" or "on" => true,
            "0" or "no" or "off" => false,
            _ => defaultValue
        };
    }
}
