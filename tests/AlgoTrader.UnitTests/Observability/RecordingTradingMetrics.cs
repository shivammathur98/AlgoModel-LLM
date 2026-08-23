namespace AlgoTrader.UnitTests.Observability;

using AlgoTrader.Application.Observability;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Recording <see cref="ITradingMetrics"/> test double. Captures every emission so a test can assert the
/// trade-lifecycle wiring fires the right metric with the right labels. Shared across the execution, kill-switch
/// and decision-cycle tests (the production <see cref="MeterTradingMetrics"/> emits to a real
/// <see cref="System.Diagnostics.Metrics.Meter"/>, which is thin I/O and left untested by convention).
/// </summary>
public sealed class RecordingTradingMetrics : ITradingMetrics
{
    public List<TradingMode> Ticks { get; } = new();
    public List<TradingMode> Candles { get; } = new();
    public List<(TradingMode Mode, string Strategy, SignalDirection Direction)> Signals { get; } = new();
    public List<(TradingMode Mode, RiskRejectionReason Reason)> RiskRejections { get; } = new();
    public List<(TradingMode Mode, OrderSide Side, OrderType Type)> Submitted { get; } = new();
    public List<(TradingMode Mode, OrderSide Side)> Filled { get; } = new();
    public List<(TradingMode Mode, OrderSide Side)> Rejected { get; } = new();
    public List<(bool IsClean, int CriticalCount)> Reconciliations { get; } = new();
    public List<string> KillSwitchEngagements { get; } = new();

    public void TickProcessed(TradingMode mode) => Ticks.Add(mode);
    public void CandleClosed(TradingMode mode) => Candles.Add(mode);
    public void SignalGenerated(TradingMode mode, string strategyName, SignalDirection direction) => Signals.Add((mode, strategyName, direction));
    public void RiskRejected(TradingMode mode, RiskRejectionReason reason) => RiskRejections.Add((mode, reason));
    public void OrderSubmitted(TradingMode mode, OrderSide side, OrderType type) => Submitted.Add((mode, side, type));
    public void OrderFilled(TradingMode mode, OrderSide side) => Filled.Add((mode, side));
    public void OrderRejected(TradingMode mode, OrderSide side) => Rejected.Add((mode, side));
    public void ReconciliationCompleted(bool isClean, int criticalCount) => Reconciliations.Add((isClean, criticalCount));
    public void KillSwitchEngaged(string initiatedBy) => KillSwitchEngagements.Add(initiatedBy);
}
