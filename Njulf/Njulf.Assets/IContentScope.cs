namespace Njulf.Assets;

/// <summary>An independent content lifetime. Dispose on the device thread after detaching its scenes.</summary>
/// <remarks>Loaded assets are borrowed. Scopes share cached assets, but never scene instances.</remarks>
public interface IContentScope : IContentManager, IDisposable { }
