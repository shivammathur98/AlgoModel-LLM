namespace AlgoTrader.Broker;

using AlgoTrader.Broker.Zerodha;
using AlgoTrader.Domain.Broker;
using Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for the Broker layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds the configured broker adapter (<see cref="ZerodhaKiteBroker"/>).</summary>
    public static IServiceCollection AddAlgoTraderBroker(this IServiceCollection services)
    {
        // Typed HttpClient: a ZerodhaKiteBroker is created per-scope with a configured HttpClient.
        services.AddHttpClient<ZerodhaKiteBroker>();

        // Broker order/account contract. Historical market data is registered by the MarketData
        // module so it remains independently replaceable and testable.
        services.AddScoped<ITradingBroker>(sp => sp.GetRequiredService<ZerodhaKiteBroker>());

        return services;
    }
}
