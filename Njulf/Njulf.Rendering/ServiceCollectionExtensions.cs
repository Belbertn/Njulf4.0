using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Njulf.Assets;
using Njulf.Core.Interfaces;
using Njulf.Rendering;
using Njulf.Rendering.Core;
using Njulf.Rendering.Data;
using Njulf.Rendering.Descriptors;
using Njulf.Rendering.Diagnostics;
using Njulf.Rendering.Memory;
using Njulf.Rendering.Pipeline;
using Njulf.Rendering.Resources;
using Silk.NET.Windowing;

namespace Microsoft.Extensions.DependencyInjection
{
    public sealed class RenderingOptions
    {
        private RendererValidationSettings _validationSettings = RendererValidationSettings.FromEnvironment();

        public bool EnableValidation
        {
            get => _validationSettings.EnableValidation;
            set => _validationSettings = _validationSettings with
            {
                Mode = value ? RendererValidationMode.Standard : RendererValidationMode.Off
            };
        }

        public RendererValidationSettings ValidationSettings
        {
            get => _validationSettings;
            set => _validationSettings = value ?? throw new ArgumentNullException(nameof(value));
        }

        public static bool DefaultEnableValidation { get; } =
#if DEBUG
            true;
#else
            false;
#endif

        private static readonly TextureBudgetProfile DefaultTextureBudgetProfile = ReadTextureBudgetProfile();
        private uint _maxImportedTextureDimension = ReadMaxImportedTextureDimension(DefaultTextureBudgetProfile);

        public TextureBudgetProfile TextureBudgetProfile { get; private set; } = DefaultTextureBudgetProfile;
        public uint MaxImportedTextureDimension
        {
            get => _maxImportedTextureDimension;
            set
            {
                TextureBudgetProfile = TextureBudgetProfile.Custom;
                _maxImportedTextureDimension = value;
            }
        }
        public ulong StagingBufferSize { get; set; } = ReadStagingBufferSize();

        public void ApplyTextureBudgetProfile(TextureBudgetProfile profile)
        {
            TextureBudgetProfile = profile;
            _maxImportedTextureDimension = GetProfileMaxDimension(profile);
        }

        public void SetCustomMaxImportedTextureDimension(uint maxDimension)
        {
            TextureBudgetProfile = TextureBudgetProfile.Custom;
            _maxImportedTextureDimension = maxDimension;
        }

        private static TextureBudgetProfile ReadTextureBudgetProfile()
        {
            string? explicitMax = Environment.GetEnvironmentVariable("NJULF_MAX_IMPORTED_TEXTURE_SIZE");
            if (uint.TryParse(explicitMax, out _))
                return TextureBudgetProfile.Custom;

            string? value = Environment.GetEnvironmentVariable("NJULF_TEXTURE_BUDGET_PROFILE");
            return Enum.TryParse(value, ignoreCase: true, out TextureBudgetProfile parsed)
                ? parsed
                : TextureBudgetProfile.Development;
        }

        private static uint ReadMaxImportedTextureDimension(TextureBudgetProfile profile)
        {
            string? value = Environment.GetEnvironmentVariable("NJULF_MAX_IMPORTED_TEXTURE_SIZE");
            return uint.TryParse(value, out uint parsed) ? parsed : GetProfileMaxDimension(profile);
        }

        private static uint GetProfileMaxDimension(TextureBudgetProfile profile)
        {
            return profile switch
            {
                TextureBudgetProfile.HighQuality => 2048u,
                TextureBudgetProfile.Cinematic => 4096u,
                _ => 1024u
            };
        }

        private static ulong ReadStagingBufferSize()
        {
            string? value = Environment.GetEnvironmentVariable("NJULF_STAGING_BUFFER_SIZE_BYTES");
            return ulong.TryParse(value, out ulong parsed) ? parsed : StagingRing.DefaultStagingBufferSize;
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
                return new VulkanContext(
                    registeredWindow,
                    renderingOptions.ValidationSettings,
                    provider.GetService<RendererStartupLog>(),
                    DeviceRequirementOverride.FromEnvironment());
            });

            services.TryAddSingleton<SwapchainManager>();
            services.TryAddSingleton<SynchronizationManager>();
            services.TryAddSingleton<CommandBufferManager>();
            services.TryAddSingleton<GpuAllocationTracker>();
            services.TryAddSingleton<BufferManager>();
            services.TryAddSingleton(provider =>
            {
                var renderingOptions = provider.GetRequiredService<RenderingOptions>();
                return new StagingRing(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    renderingOptions.StagingBufferSize);
            });
            services.TryAddSingleton<FenceBasedDeleter>();
            services.TryAddSingleton<BindlessHeap>();
            services.TryAddSingleton(provider =>
            {
                var textureManager = new TextureManager(
                    provider.GetRequiredService<VulkanContext>(),
                    provider.GetRequiredService<BufferManager>(),
                    provider.GetService<BindlessHeap>(),
                    provider.GetService<FenceBasedDeleter>());
                RenderingOptions options = provider.GetRequiredService<RenderingOptions>();
                textureManager.MaxLoadedTextureDimension = options.MaxImportedTextureDimension;
                textureManager.ActiveTextureBudgetProfile = options.TextureBudgetProfile;
                return textureManager;
            });
            services.TryAddSingleton<MeshManager>();
            services.TryAddSingleton<MaterialManager>();
            services.TryAddSingleton<IModelRenderUploadService, ModelRenderUploadService>();
            services.TryAddSingleton<LightManager>();
            services.TryAddSingleton<SceneDataBuilder>();
            services.TryAddSingleton<RenderGraphImageAllocator>();
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
