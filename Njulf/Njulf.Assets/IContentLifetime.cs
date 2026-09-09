namespace Njulf.Assets;

/// <summary>Host shutdown contract. Keep pumping uploads until active operations settle.</summary>
public interface IContentLifetime
{
    int ActiveOperationCount { get; }
    void BeginShutdown();
}
