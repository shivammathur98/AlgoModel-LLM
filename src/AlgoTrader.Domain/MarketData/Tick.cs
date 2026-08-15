namespace AlgoTrader.Domain.MarketData;

/// <summary>Immutable single-tick quote from a live market data feed.</summary>
public sealed record Tick(
    int InstrumentToken,
    DateTimeOffset TimestampUtc,
    decimal LastPrice,
    decimal BidPrice,
    decimal AskPrice,
    long Volume);
