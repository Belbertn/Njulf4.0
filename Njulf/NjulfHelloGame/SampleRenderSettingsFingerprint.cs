using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Njulf.Rendering.Data;

namespace NjulfHelloGame;

/// <summary>
/// Produces an in-process identity for every publicly observable render setting.
/// Quality-switch rollback uses this after a completed frame so restoring only
/// the preset cannot masquerade as restoring command-line and scene overrides.
/// </summary>
internal static class SampleRenderSettingsFingerprint
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Capture(RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(serialized)).ToLowerInvariant()}";
    }

    /// <summary>
    /// Captures the complete render-settings identity shared by the authored
    /// directional-shadow controlled isolation. The two roles intentionally
    /// differ only in the capture-only forced static-cascade refresh switch,
    /// so normalize exactly that switch and no other setting. This is called
    /// after the timing window, never while a Draw is in progress.
    /// </summary>
    public static string CaptureDirectionalIsolationFamily(
        RenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool forceRefresh =
            settings.Shadows.ForceStaticCascadeCacheRefresh;
        try
        {
            settings.Shadows.ForceStaticCascadeCacheRefresh = false;
            return Capture(settings);
        }
        finally
        {
            settings.Shadows.ForceStaticCascadeCacheRefresh = forceRefresh;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            MaxDepth = 64
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
