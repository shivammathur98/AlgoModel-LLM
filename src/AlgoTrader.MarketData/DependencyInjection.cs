namespace AlgoTrader.MarketData;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Services;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.MarketData.Kite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>DI registration for the MarketData layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds market data services: WebSocket provider, candle aggregator, and orchestrator.</summary>
    public static IServiceCollection AddAlgoTraderMarketData(this IServiceCollection services)
    {
        // Symbol resolver: resolves instrument token → (symbol, exchange) via a scoped repository.
        services.AddSingleton<Func<int, (string Symbol, string Exchange)>>(sp => token =>
        {
            using var scope = sp.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInstrumentRepository>();
            var instrument = repo.GetByTokenAsync(token).GetAwaiter().GetResult();
            return instrument != null
                ? (instrument.Symbol, instrument.Exchange)
                : ($"TOKEN_{token}", "NSE");
        });

        // Candle aggregator: stateful singleton that aggregates ticks into candles.
        services.AddSingleton<ICandleAggregator, CandleAggregator>();

        // Last-price cache: shared, thread-safe snapshot of the newest price per instrument (§7). The
        // live feed writes it; the paper trading loop (§8) and risk staleness checks (§14) read it.
        services.AddSingleton<ILastPriceCache, LastPriceCache>();

        services.AddHttpClient<KiteHistoricalDataProvider>((serviceProvider, client) =>
        {
            var broker = serviceProvider.GetRequiredService<IOptions<BrokerSettings>>().Value;
            client.BaseAddress = new Uri("https://api.kite.trade/");
            client.Timeout = TimeSpan.FromSeconds(broker.RequestTimeoutSeconds);
        });

        // Historical download provider is deliberately separate from the broker/order adapter.
        services.AddScoped<IHistoricalDataProvider>(sp => sp.GetRequiredService<KiteHistoricalDataProvider>());

        // WebSocket market data provider: singleton (one connection per app). Full live streaming
        // is operationalised in Phase 10; registration here keeps the market-data boundary stable.
        services.AddSingleton<ILiveMarketDataProvider, KiteWebSocketMarketDataProvider>();

        // The service owns a scoped persistence repository. A later hosted live-feed worker can
        // create an explicit scope per processing batch rather than retaining a DbContext.
        services.AddScoped<MarketDataService>();

        return services;
    }
}
