namespace AlgoTrader.Domain.MarketData;

using AlgoTrader.Domain.Enums;

/// <summary>
/// Immutable OHLCV candle. Timestamps are UTC and mark the start of the bar.
/// All prices are <see cref="decimal"/> — never floating point (§37).
/// </summary>
public sealed record Candle(
    int InstrumentToken,
    string Symbol,
    string Exchange,
    Timeframe Timeframe,
    DateTimeOffset TimestampUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume)
{
    /// <summary>Natural key used to prevent duplicate candle insertion (§8).</summary>
    public CandleKey Key => new(InstrumentToken, Timeframe, TimestampUtc);
}

/// <summary>Natural key of a candle: instrument + timeframe + bar start time.</summary>
public sealed record CandleKey(int InstrumentToken, Timeframe Timeframe, DateTimeOffset TimestampUtc);
