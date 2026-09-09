using Microsoft.Extensions.DependencyInjection;
using Njulf.Core.Interfaces;
using Njulf.Input;
using Silk.NET.Input;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Registers the default game-thread input service.</summary>
    public static class InputServiceCollectionExtensions
    {
        /// <summary>Registers a container-owned singleton borrowing the registered native input context.</summary>
        public static IServiceCollection AddInput(this IServiceCollection services)
        {
            services.AddSingleton<IInputManager, InputManager>(provider =>
            {
                var inputContext = provider.GetRequiredService<IInputContext>();
                var inputManager = new InputManager(inputContext);
                inputManager.Initialize();
                return inputManager;
            });
            
            return services;
        }
        
        /// <summary>Registers a placeholder requiring replacement with a window-created native context before resolution.</summary>
        public static IServiceCollection AddInputContext(this IServiceCollection services)
        {
            services.AddSingleton<IInputContext>(_ =>
                throw new InvalidOperationException("Register a Silk.NET IInputContext created from the application window before adding input services."));
            return services;
        }
    }
}
