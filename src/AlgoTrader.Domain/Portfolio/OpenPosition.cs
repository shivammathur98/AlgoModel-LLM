namespace AlgoTrader.Domain.Portfolio;

/// <summary>One open (unsquared) position tracked by the platform.</summary>
public sealed record OpenPosition(
    int InstrumentToken,
    string Symbol,
    string StrategyName,
    int Quantity,
    decimal AveragePrice,
    DateTimeOffset OpenedAtUtc,
    decimal? StopPrice,
    decimal? TargetPrice,
    string CorrelationId);
