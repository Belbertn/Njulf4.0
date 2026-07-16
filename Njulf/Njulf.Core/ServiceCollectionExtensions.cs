using Microsoft.Extensions.DependencyInjection;
using Njulf.Core.Camera;
using Njulf.Core.Interfaces;
using Njulf.Core.Math;
using Njulf.Core.Scene;

namespace Njulf.Core
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddNjulfCore(this IServiceCollection services)
        {
            services.AddSingleton<Scene.Scene>();
            services.AddSingleton<ICamera, FirstPersonCamera>();
            return services;
        }

        public static IServiceCollection AddCamera<T>(this IServiceCollection services) where T : class, ICamera
        {
            services.AddSingleton<ICamera, T>();
            return services;
        }

        public static IServiceCollection AddCamera(this IServiceCollection services, ICamera camera)
        {
            services.AddSingleton(camera);
            services.AddSingleton<ICamera>(camera);
            return services;
        }
    }
}
