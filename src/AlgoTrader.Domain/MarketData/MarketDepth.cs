namespace AlgoTrader.Domain.MarketData;

/// <summary>One price level of an order book.</summary>
public sealed record MarketDepthLevel(decimal Price, int Quantity, int OrderCount);

/// <summary>Top-of-book and depth snapshot where the broker provides it.</summary>
public sealed record MarketDepth(
    int InstrumentToken,
    DateTimeOffset TimestampUtc,
    decimal LastPrice,
    long Volume,
    IReadOnlyList<MarketDepthLevel> Buy,
    IReadOnlyList<MarketDepthLevel> Sell);
