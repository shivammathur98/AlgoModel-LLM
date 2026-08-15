namespace AlgoTrader.Domain.Strategy;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Contract every strategy implements. Implementations must be deterministic and may only
/// use information contained in <see cref="StrategyContext"/> — no look-ahead (§16).
/// Strategies are broker-agnostic: they never reference broker or execution types.
/// </summary>
public interface IStrategy
{
    /// <summary>Unique strategy name, e.g. "MomentumBreakoutV1".</summary>
    string Name { get; }

    /// <summary>Strategy version, recorded with every backtest run (§21).</summary>
    string Version { get; }

    /// <summary>
    /// Evaluates the most recent closed candle and returns zero or more signals.
    /// Called exactly once per closed decision candle per symbol.
    /// </summary>
    IReadOnlyList<Signal> OnCandleClosed(StrategyContext context);
}

/// <summary>Everything a strategy is allowed to see when making one decision.</summary>
public sealed class StrategyContext
{
    public required string Symbol { get; init; }
    public required int InstrumentToken { get; init; }
    public required Timeframe Timeframe { get; init; }

    /// <summary>Closed candles ordered oldest → newest. The last item is the decision candle.</summary>
    public required IReadOnlyList<Candle> Candles { get; init; }

    /// <summary>Currently open position for this symbol, or null when flat.</summary>
    public OpenPosition? OpenPosition { get; init; }

    public required DateTimeOffset CurrentTimestampUtc { get; init; }

    /// <summary>Capital currently available for new entries.</summary>
    public decimal AvailableCapital { get; init; }
}
