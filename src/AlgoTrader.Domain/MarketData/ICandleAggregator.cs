namespace AlgoTrader.Domain.MarketData;

using AlgoTrader.Domain.Enums;

/// <summary>
/// Converts a live tick stream into closed candles (§7) for timeframes the
/// broker does not stream natively.
/// </summary>
public interface ICandleAggregator
{
    /// <summary>
    /// Feeds one tick. Returns the candle that closed as a consequence of this tick,
    /// or null when the current bar is still forming.
    /// </summary>
    Candle? OnTick(Tick tick, Timeframe timeframe);

    /// <summary>Drops any in-progress bar for the instrument.</summary>
    void Reset(int instrumentToken);
}
