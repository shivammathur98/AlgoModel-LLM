namespace AlgoTrader.Domain.Risk;

using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Trading;

/// <summary>Why the risk engine rejected a signal or order (§14, §15).</summary>
public enum RiskRejectionReason
{
    None = 0,
    KillSwitchActive,
    TradingHalted,
    MaxDailyLossBreached,
    MaxTradesPerDayBreached,
    MaxSimultaneousPositionsBreached,
    MaxCapitalUtilizationBreached,
    MaxPerTradeRiskBreached,
    MaxExposurePerSymbolBreached,
    SymbolAlreadyOpen,
    DuplicateOrder,
    OutsideTradingHours,
    MarketDataStale,
    BrokerDisconnected,
    PositionMismatch,
    InsufficientFunds,
    InvalidRequest
}

/// <summary>Risk engine verdict for one signal or order.</summary>
public sealed record RiskDecision(bool IsApproved, RiskRejectionReason Reason = RiskRejectionReason.None, string? Detail = null)
{
    public static RiskDecision Approved() => new(true);

    public static RiskDecision Rejected(RiskRejectionReason reason, string? detail = null)
        => new(false, reason, detail);
}

/// <summary>
/// Snapshot of portfolio and system state at evaluation time. Built by the caller so the
/// risk engine itself stays stateless about how the snapshot is produced.
/// </summary>
public sealed record RiskEvaluationContext(
    DateTimeOffset TimestampUtc,
    decimal AvailableCapital,
    decimal RealizedPnlToday,
    int TradesToday,
    int OpenPositionCount,
    int OpenOrderCount,
    IReadOnlySet<string> SymbolsWithOpenPositions,
    bool IsMarketDataStale,
    bool IsBrokerConnected);

/// <summary>
/// The risk engine has authority to reject any signal or order (§14).
/// Only approved items may reach the execution engine.
/// </summary>
public interface IRiskEngine
{
    /// <summary>Evaluates a strategy signal against all active risk rules.</summary>
    Task<RiskDecision> EvaluateSignalAsync(Signal signal, RiskEvaluationContext context, CancellationToken cancellationToken = default);

    /// <summary>Evaluates a concrete order request (sizing, exposure, duplicates) against all active risk rules.</summary>
    Task<RiskDecision> EvaluateOrderAsync(OrderRequest order, RiskEvaluationContext context, CancellationToken cancellationToken = default);
}
