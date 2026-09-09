namespace Njulf.Assets;

/// <summary>Stops upload admission and requests cooperative cancellation; the host must continue pumping.</summary>
public interface IContentUploadLifetime
{
    void BeginShutdown();
}
