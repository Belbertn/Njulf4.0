using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Njulf.Core.Interfaces;

namespace Njulf.Assets
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAssets(this IServiceCollection services, string? contentRoot = null)
        {
            return services.AddAssetsInternal(contentRoot);
        }

        public static IServiceCollection AddAssetsWithContentRoot(this IServiceCollection services, string contentRoot)
        {
            return services.AddAssetsInternal(contentRoot);
        }

        private static IServiceCollection AddAssetsInternal(this IServiceCollection services, string? contentRoot)
        {
            services.TryAddSingleton(provider => new ContentManager(
                contentRoot,
                provider.GetService<IModelRenderUploadService>()));
            services.TryAddSingleton<IContentManager>(provider => provider.GetRequiredService<ContentManager>());
            services.TryAddSingleton<ModelImporter>();
            services.TryAddSingleton<MeshletBuilder>();
            services.TryAddSingleton<ProcessedMeshAssetBuilder>();
            return services;
        }
    }
}
