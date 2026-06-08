using Microsoft.Extensions.DependencyInjection;
using Njulf.Core.Interfaces;

namespace Njulf.Assets
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddAssets(this IServiceCollection services, string contentRoot = null)
        {
            services.AddSingleton<IContentManager>(new ContentManager(contentRoot));
            services.AddSingleton<ContentManager>();
            services.AddSingleton<ModelImporter>();
            services.AddSingleton<MeshletBuilder>();
            return services;
        }

        public static IServiceCollection AddAssetsWithContentRoot(this IServiceCollection services, string contentRoot)
        {
            services.AddSingleton<IContentManager>(new ContentManager(contentRoot));
            services.AddSingleton<ContentManager>();
            services.AddSingleton<ModelImporter>();
            services.AddSingleton<MeshletBuilder>();
            return services;
        }
    }
}
