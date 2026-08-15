namespace AlgoTrader.Backtesting;

using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;

/// <summary>All explicit dependencies and simulation assumptions for one deterministic backtest run.</summary>
public sealed record BacktestRunRequest(
    IStrategy Strategy,
    IReadOnlyList<Candle> Candles,
    decimal InitialCapital,
    IPositionSizer PositionSizer,
    BacktestPositionSizingSettings PositionSizing,
    IBacktestExecutionModel ExecutionModel,
    ITradingCostCalculator CostCalculator,
    TimeOnly? EndOfDayExitTimeIst = null,
    TimeSpan? MaximumHoldingTime = null,
    IntrabarExitPriority IntrabarExitPriority = IntrabarExitPriority.WorstCase,
    ProductType Product = ProductType.Intraday);

/// <summary>Configured portfolio limits supplied to a position sizer for every candidate entry.</summary>
public sealed record BacktestPositionSizingSettings(
    decimal MaxCapitalPerTrade,
    decimal MaxRiskPerTrade,
    decimal MaxExposurePerSymbol,
    PositionSizingMethod Method = PositionSizingMethod.RiskBased,
    decimal PercentOfCapital = 0.10m);

/// <summary>Reason a strategy signal did not become a submitted simulated order.</summary>
public enum BacktestSignalRejectionReason
{
    DuplicateEntry,
    NoOpenPosition,
    EndOfDayCutoff,
    InsufficientCapital,
    InvalidPositionSize,
    PendingOrderFromPreviousSession
}

/// <summary>Audit record for a discarded strategy signal. Accepted signals become pending orders.</summary>
public sealed record BacktestSignalRejection(
    string CorrelationId,
    string Symbol,
    DateTimeOffset TimestampUtc,
    BacktestSignalRejectionReason Reason);

/// <summary>Complete in-memory result of a deterministic run; persistence is intentionally a separate concern.</summary>
public sealed record BacktestRunResult(
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<BacktestSignalRejection> RejectedSignals,
    BacktestMetrics Metrics,
    decimal InitialCapital,
    decimal FinalCapital,
    int GeneratedSignals);
