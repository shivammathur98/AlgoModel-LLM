namespace AlgoTrader.Domain.MarketData;

using AlgoTrader.Domain.Enums;

/// <summary>
/// Source of historical candles (broker historical APIs, local database).
/// Implementations must return bars ordered by timestamp ascending.
/// </summary>
public interface IHistoricalDataProvider : IMarketDataProvider
{
    /// <summary>
    /// Fetches candles for an instrument in the half-open range [fromUtc, toUtc).
    /// </summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}
