namespace AlgoTrader.MarketData;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Services;
using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.MarketData.Jugaad;
using AlgoTrader.MarketData.Kite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>DI registration for the MarketData layer.</summary>
public static class DependencyInjection
{
    /// <summary>Adds market data services: WebSocket provider, candle aggregator, and orchestrator.</summary>
    public static IServiceCollection AddAlgoTraderMarketData(this IServiceCollection services)
    {
        // Symbol resolver: resolves instrument token -> (symbol, exchange) via a scoped repository.
        services.AddSingleton<Func<int, (string Symbol, string Exchange)>>(sp => 
        {
            var cache = new System.Collections.Concurrent.ConcurrentDictionary<int, (string Symbol, string Exchange)>();
            Func<int, (string Symbol, string Exchange)> resolver = token => 
                cache.GetOrAdd(token, t =>
                {
                    using var scope = sp.CreateScope();
                    var repo = scope.ServiceProvider.GetRequiredService<IInstrumentRepository>();
                    var instrument = repo.GetByTokenAsync(t).GetAwaiter().GetResult();
                    if (instrument != null)
                        return (instrument.Symbol, instrument.Exchange);
                    return ($"TOKEN_{t}", "NSE");
                });
            return resolver;
        });

        // Candle aggregator: stateful singleton that aggregates ticks into candles.
        services.AddSingleton<ICandleAggregator, CandleAggregator>();

        // Last-price cache: shared, thread-safe snapshot of the newest price per instrument.
        // The live feed writes it; the paper trading loop and risk staleness checks read it.
        services.AddSingleton<ILastPriceCache, LastPriceCache>();

        services.AddHttpClient<KiteHistoricalDataProvider>((serviceProvider, client) =>
        {
            var broker = serviceProvider.GetRequiredService<IOptions<BrokerSettings>>().Value;
            client.BaseAddress = new Uri("https://api.kite.trade/");
            client.Timeout = TimeSpan.FromSeconds(broker.RequestTimeoutSeconds);
        });

        // Historical data provider: conditionally register Jugaad or Kite
        services.AddScoped<IHistoricalDataProvider>(sp =>
        {
            var marketDataSettings = sp.GetRequiredService<IOptions<MarketDataSettings>>().Value;
            if (string.Equals(marketDataSettings.HistoricalProvider, "Jugaad", StringComparison.OrdinalIgnoreCase))
            {
                return new JugaadHistoricalDataProvider(
                    sp.GetRequiredService<IOptions<MarketDataSettings>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JugaadHistoricalDataProvider>>());
            }

            return sp.GetRequiredService<KiteHistoricalDataProvider>();
        });

        // WebSocket market data provider: singleton (one connection per app).
        services.AddSingleton<ILiveMarketDataProvider, KiteWebSocketMarketDataProvider>();

        // The service owns a scoped persistence repository.
        services.AddScoped<MarketDataService>();

        return services;
    }
}
