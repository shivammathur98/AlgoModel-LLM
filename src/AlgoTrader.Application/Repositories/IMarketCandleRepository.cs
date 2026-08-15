namespace AlgoTrader.Application.Repositories;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;

/// <summary>Repository for market candle storage with deduplication (§8).</summary>
public interface IMarketCandleRepository
{
    /// <summary>Saves candles, skipping duplicates based on (InstrumentToken, Timeframe, TimestampUtc).</summary>
    Task<int> SaveCandlesAsync(IReadOnlyList<Candle> candles, CancellationToken cancellationToken = default);

    /// <summary>Retrieves candles for an instrument in the given time range, ordered by timestamp ascending.</summary>
    Task<IReadOnlyList<Candle>> GetCandlesAsync(
        int instrumentToken,
        Timeframe timeframe,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the most recent candle timestamp for an instrument/timeframe pair.</summary>
    Task<DateTimeOffset?> GetLatestCandleTimestampAsync(
        int instrumentToken,
        Timeframe timeframe,
        CancellationToken cancellationToken = default);
}
