namespace AlgoTrader.Infrastructure;

using AlgoTrader.Domain.Common;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers Infrastructure-layer services. Called from the composition root.
/// </summary>
public static class DependencyInjection
{
    /// <summary>Adds Infrastructure services (system clock, etc.) to the DI container.</summary>
    public static IServiceCollection AddAlgoTraderInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISystemClock, SystemClock>();
        return services;
    }
}
