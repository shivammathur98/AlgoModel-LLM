namespace AlgoTrader.Domain.MarketData;

/// <summary>Base contract for any market data source.</summary>
public interface IMarketDataProvider
{
    /// <summary>Stable provider identifier, e.g. "KiteHistorical" or "KiteWebSocket".</summary>
    string ProviderName { get; }

    /// <summary>Whether the provider currently has a usable connection/session.</summary>
    bool IsConnected { get; }
}
