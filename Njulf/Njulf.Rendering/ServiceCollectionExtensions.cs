using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Silk.NET.Windowing;

namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class RenderingOptions
    {
        public bool EnableValidation { get; set; } =
#if DEBUG
            true;
#else
            false;
#endif

        public uint MaxImportedTextureDimension { get; set; } = ReadMaxImportedTextureDimension();

        private static uint ReadMaxImportedTextureDimension()
        {
            string? value = Environment.GetEnvironmentVariable("NJULF_MAX_IMPORTED_TEXTURE_SIZE");
            return uint.TryParse(value, out uint parsed) ? parsed : 2048u;
        }
    }

    public static class RenderingServiceCollectionExtensions
    {
        public static IServiceCollection AddRendering(this IServiceCollection services, IWindow window)
        {
            return services.AddRendering(window, configure: null);
        }

        public static IServiceCollection AddRendering(
            this IServiceCollection services,
            IWindow window,
            Action<RenderingOptions>? configure)
        {
            if (services == null)
                throw new ArgumentNullException(nameof(services));
            if (window == null)
                throw new ArgumentNullException(nameof(window));

            var options = new RenderingOptions();
            configure?.Invoke(options);

            services.AddSingleton(options);
            services.AddSingleton<IWindow>(window);

            services.TryAddSingleton(provider =>
            {
                var renderingOptions = provider.GetRequiredService<RenderingOptions>();
                var registeredWindow = provider.GetRequiredService<IWindow>();
                return new VulkanContext(registeredWindow, renderingOptions.EnableValidation);
            });

            services.TryAddSingleton<SwapchainManager>();
            services.TryAddSingleton<SynchronizationManager>();
            services.TryAddSingleton<CommandBufferManager>();
            services.TryAddSingleton<BufferManager>();
            services.TryAddSingleton<StagingRing>();
            services.TryAddSingleton<FenceBasedDeleter>();
            services.TryAddSingleton<BindlessHeap>();
            services.TryAddSingleton(provider =>
            {
                var textureManager = new TextureManager(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetService<BindlessHeap>(),
                    provider.GetService<FenceBasedDeleter>());
                textureManager.MaxLoadedTextureDimension = provider.GetRequiredService<RenderingOptions>().MaxImportedTextureDimension;
                return textureManager;
            });
            services.TryAddSingleton<MeshManager>();
            services.TryAddSingleton<MaterialManager>();
            services.TryAddSingleton<IModelRenderUploadService, ModelRenderUploadService>();
            services.TryAddSingleton<LightManager>();
            services.TryAddSingleton<SceneDataBuilder>();
            services.TryAddSingleton<RenderGraph>();

            services.TryAddSingleton(provider => new VulkanRenderer(
                provider.GetRequiredService<IWindow>(),
                provider.GetRequiredService<VulkanContext>(),
                provider.GetRequiredService<SwapchainManager>(),
                provider.GetRequiredService<SynchronizationManager>(),
                provider.GetRequiredService<CommandBufferManager>(),
                provider.GetRequiredService<BufferManager>(),
                provider.GetRequiredService<TextureManager>(),
                provider.GetRequiredService<MeshManager>(),
                provider.GetRequiredService<MaterialManager>(),
                provider.GetRequiredService<LightManager>(),
                provider.GetRequiredService<BindlessHeap>(),
                provider.GetRequiredService<RenderGraph>(),
                provider.GetRequiredService<SceneDataBuilder>(),
                provider.GetRequiredService<StagingRing>(),
                provider.GetRequiredService<FenceBasedDeleter>(),
                provider.GetRequiredService<IModelRenderUploadService>(),
                ownsDependencies: false));

            services.TryAddSingleton<IRenderer>(provider => provider.GetRequiredService<VulkanRenderer>());

            return services;
        }
    }
}
