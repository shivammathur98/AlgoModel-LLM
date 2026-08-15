namespace AlgoTrader.Application.Repositories;

/// <summary>Persistable audit record for a reproducible completed backtest.</summary>
public sealed record BacktestRunRecord(
    long StrategyVersionId,
    string RunCorrelationId,
    string ParametersHash,
    string DataFingerprint,
    string Universe,
    DateTimeOffset DataStartUtc,
    DateTimeOffset DataEndUtc,
    decimal InitialCapital,
    decimal FinalCapital,
    string ExecutionModel,
    string CostModel,
    string SlippageModel,
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRate,
    decimal GrossPnl,
    decimal TotalCharges,
    decimal TotalSlippage,
    decimal NetPnl,
    decimal MaxDrawdown,
    decimal? ProfitFactor,
    decimal? SharpeRatio,
    decimal? SortinoRatio,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<BacktestTradeRecord> Trades,
    string? Notes = null)
{
    /// <summary>Database identity assigned after persistence.</summary>
    public long Id { get; init; }
}

/// <summary>Persistable closed trade belonging to a backtest run.</summary>
public sealed record BacktestTradeRecord(
    int InstrumentToken,
    string Symbol,
    DateTimeOffset EntryTimestampUtc,
    DateTimeOffset ExitTimestampUtc,
    decimal EntryPrice,
    decimal ExitPrice,
    int Quantity,
    decimal GrossPnl,
    decimal Charges,
    decimal Slippage,
    decimal NetPnl,
    int HoldingMinutes,
    string ExitReason,
    string CorrelationId);

/// <summary>Storage boundary for completed backtest runs and their immutable trade ledger.</summary>
public interface IBacktestRunRepository
{
    /// <summary>Atomically saves one completed run and all of its closed trades.</summary>
    Task<long> AddCompletedAsync(BacktestRunRecord run, CancellationToken cancellationToken = default);

    /// <summary>Loads a run and its closed trades, or null when it does not exist.</summary>
    Task<BacktestRunRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);
}
