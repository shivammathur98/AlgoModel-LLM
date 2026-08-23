namespace AlgoTrader.Application.Observability;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;

/// <summary>
/// No-op <see cref="ITradingMetrics"/>. The safe default wherever metrics are irrelevant — unit tests, backtests,
/// and any code path that takes an optional metrics dependency. <see cref="Instance"/> is shared and stateless.
/// </summary>
public sealed class NullTradingMetrics : ITradingMetrics
{
    /// <summary>The shared no-op instance.</summary>
    public static readonly NullTradingMetrics Instance = new();

    private NullTradingMetrics() { }

    public void TickProcessed(TradingMode mode) { }
    public void CandleClosed(TradingMode mode) { }
    public void SignalGenerated(TradingMode mode, string strategyName, SignalDirection direction) { }
    public void RiskRejected(TradingMode mode, RiskRejectionReason reason) { }
    public void OrderSubmitted(TradingMode mode, OrderSide side, OrderType type) { }
    public void OrderFilled(TradingMode mode, OrderSide side) { }
    public void OrderRejected(TradingMode mode, OrderSide side) { }
    public void ReconciliationCompleted(bool isClean, int criticalCount) { }
    public void KillSwitchEngaged(string initiatedBy) { }
}
