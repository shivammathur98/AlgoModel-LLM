namespace AlgoTrader.Domain.Trading;

using AlgoTrader.Domain.Enums;

/// <summary>Direction expressed by a strategy signal.</summary>
public enum SignalDirection
{
    /// <summary>Open a long position.</summary>
    LongEntry,

    /// <summary>Close an existing long position.</summary>
    LongExit
}

/// <summary>
/// A strategy's intent to enter or exit. A signal is NOT an order: it must pass the
/// risk engine before the execution engine may act on it (§14).
/// </summary>
public sealed record Signal(
    string StrategyName,
    string StrategyVersion,
    int InstrumentToken,
    string Symbol,
    SignalDirection Direction,
    DateTimeOffset TimestampUtc,
    decimal? EntryPrice = null,
    decimal? StopPrice = null,
    decimal? TargetPrice = null,
    string? Notes = null)
{
    /// <summary>Correlation identifier propagated to risk decisions, orders, fills and logs (§28).</summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
}
