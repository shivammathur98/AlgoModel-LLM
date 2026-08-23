namespace AlgoTrader.Execution;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Observability;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Registers the execution layer (§8, §11) with the DI container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="OrderExecutionEngine"/> as the <see cref="IExecutionEngine"/>. Scoped, because it
    /// depends on the scoped <see cref="ITradingBroker"/> and <see cref="IOrderRepository"/> — a per-operation
    /// scope keeps the broker session and the EF unit of work aligned. The engine reads a snapshot of the
    /// validated <see cref="TradingSettings"/> (trading mode never changes at runtime) and shares the
    /// process-wide <see cref="LiveTradingSafetyValidator"/> that enforces the three live gates.
    /// </summary>
    public static IServiceCollection AddAlgoTraderExecution(this IServiceCollection services)
    {
        services.AddScoped<IExecutionEngine>(sp => new OrderExecutionEngine(
            sp.GetRequiredService<IOptions<TradingSettings>>().Value,
            sp.GetRequiredService<ITradingBroker>(),
            sp.GetRequiredService<LiveTradingSafetyValidator>(),
            sp.GetRequiredService<IOrderRepository>(),
            sp.GetRequiredService<ISystemClock>(),
            sp.GetRequiredService<ILogger<OrderExecutionEngine>>(),
            sp.GetRequiredService<ITradingMetrics>()));
        return services;
    }
}
