using Njulf.Core.Interfaces;

namespace Njulf.Graphics;

/// <summary>Provides the graphics API backed by a renderer's existing device.</summary>
public interface IGraphicsDeviceProvider
{
    /// <summary>Gets the renderer-owned graphics device. Applications must not dispose it.</summary>
    GraphicsDevice GraphicsDevice { get; }
}

/// <summary>Access to graphics resources from the normal renderer contract.</summary>
public static class RendererGraphicsExtensions
{
    /// <summary>
    /// Gets the same graphics device on every call. Resource operations are available
    /// after renderer initialization, on its device thread, until shutdown begins.
    /// </summary>
    /// <exception cref="NotSupportedException">The renderer does not provide a graphics device.</exception>
    public static GraphicsDevice GetGraphicsDevice(this IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return renderer is IGraphicsDeviceProvider provider
            ? provider.GraphicsDevice
            : throw new NotSupportedException("This renderer does not provide the Njulf graphics API.");
    }
}
