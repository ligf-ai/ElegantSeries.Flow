using ElegantSeries.Flow.Core.Navigation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ElegantSeries.Flow.Core.Extensions;

/// <summary>
/// Extension methods for registering ElegantSeries.Flow navigation services.
/// </summary>
public static class FlowServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="INavigationService"/> as a singleton.
    /// Suitable for single-window applications or global navigation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddFlowNavigation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<INavigationService, NavigationService>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="INavigationService"/> as a scoped service.
    /// Suitable for multi-window applications where each window has its own scope
    /// with an isolated navigation stack.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddScopedFlowNavigation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<INavigationService, NavigationService>();
        return services;
    }
}
